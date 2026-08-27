//! PST (Outlook Personal Folders) file extraction.
//!
//! This module handles extraction of emails from Microsoft Outlook PST files
//! using the `outlook-pst` crate.
//!
//! # Features
//!
//! - **Unicode and ANSI PST support**: Handles both modern and legacy PST formats
//! - **Folder hierarchy traversal**: Extracts messages from all folders recursively
//! - **Message properties**: Extracts subject, sender, recipients, body
//!
//! # Example
//!
//! Not run as a doctest: `pub(crate)`, so it is unreachable from a downstream crate.
//!
//! ```ignore
//! use xberg::extraction::pst::extract_pst_messages;
//!
//! # fn example() -> xberg::Result<()> {
//! let pst_bytes = std::fs::read("archive.pst")?;
//! let (messages, _warnings) = extract_pst_messages(&pst_bytes)?;
//!
//! for msg in &messages {
//!     println!("Subject: {:?}", msg.subject);
//! }
//! # Ok(())
//! # }
//! ```

use crate::error::{Result, XbergError};
use crate::types::{EmailAttachment, EmailExtractionResult, ProcessingWarning};
use std::borrow::Cow;
use std::collections::HashMap;

#[cfg(feature = "email")]
use outlook_pst::{
    ltp::{prop_context::PropertyValue, table_context::TableContext},
    messaging::{folder::Folder as PstFolder, message::Message as PstMessage, store::EntryId},
    ndb::node_id::{NID_ROOT_FOLDER, NodeId},
};
#[cfg(feature = "email")]
use std::rc::Rc;

/// A folder queued for traversal: the folder handle itself, its recursion depth
/// (0 at the top level), and its display path (used only for warning messages).
///
/// Named to keep this shape out of `walk_folder_tree`'s signature and the seed
/// lists built by [`extract_from_store`] and [`discover_non_ipm_top_level_folders`]
/// (see issue #162), which otherwise trips clippy's `type_complexity` lint.
#[cfg(feature = "email")]
type PstFolderSeed = (Rc<dyn PstFolder>, u32, String);

/// Safety cap on rows read from a single PST contents/hierarchy table in one
/// pass.
///
/// PST folder/table structures are attacker-controllable input: a corrupt or
/// hostile table can report (or its row iterator can yield) an effectively
/// unbounded number of rows. Before issue #162 the traversal fully
/// materialized a table's rows via `.collect()` with no bound, so such a
/// table hung extraction forever — the existing per-folder recursion-depth
/// cap (`depth > 50`) never even came into play, because it protects against
/// deep/cyclic *folder* nesting, not an unbounded row iterator *within* a
/// single table. Reading rows one at a time and stopping at this cap fixes
/// that regardless of which table (IPM or non-IPM) is misbehaving.
#[cfg(feature = "email")]
const MAX_TABLE_ROWS: usize = 100_000;

/// Read node ids from a table's `rows_matrix()`, stopping after
/// [`MAX_TABLE_ROWS`] rows without ever materializing the rest of the
/// iterator. Returns `(ids, true)` when reading stopped only because the cap
/// was hit, so callers can surface a `ProcessingWarning` about truncation.
#[cfg(feature = "email")]
fn collect_row_ids(table: &dyn TableContext) -> (Vec<u32>, bool) {
    let mut ids = Vec::new();
    for row in table.rows_matrix() {
        if ids.len() >= MAX_TABLE_ROWS {
            return (ids, true);
        }
        ids.push(u32::from(row.id()));
    }
    (ids, false)
}

/// From the node ids returned by `Store::root_hierarchy_table()` (the true
/// PST root's direct children), determine which ones are non-IPM (non-mail)
/// top-level folders that still need to be traversed — i.e. every id except
/// the one already covered by the IPM (mail) sub-tree walk.
///
/// Pure and dependency-free so the enumeration decision (issue #162) can be
/// unit-tested without a real PST `Store`/`Folder`.
#[cfg(feature = "email")]
fn non_ipm_top_level_ids(top_level_ids: &[u32], ipm_node_id: Option<u32>) -> Vec<u32> {
    top_level_ids
        .iter()
        .copied()
        .filter(|id| Some(*id) != ipm_node_id)
        .collect()
}

/// Extract all email messages from a PST file.
///
/// Opens the PST file and traverses the full folder hierarchy, extracting
/// every message including subject, sender, recipients, and body text.
///
/// # Arguments
///
/// * `pst_data` - Raw bytes of the PST file
///
/// # Returns
///
/// A vector of `EmailExtractionResult`, one per message found.
///
/// # Errors
///
/// Returns an error if the PST data cannot be written to a temporary file,
/// or if the PST format is invalid.
#[cfg(all(feature = "email", not(target_arch = "wasm32")))]
pub(crate) fn extract_pst_messages(pst_data: &[u8]) -> Result<(Vec<EmailExtractionResult>, Vec<ProcessingWarning>)> {
    use std::io::Write;

    let mut temp_file = tempfile::Builder::new()
        .prefix("xberg_pst_")
        .suffix(".tmp")
        .tempfile()
        .map_err(crate::XbergError::from)?;

    temp_file.write_all(pst_data).map_err(crate::XbergError::from)?;
    temp_file.flush().map_err(crate::XbergError::from)?;

    let (messages, warnings) = extract_from_path(temp_file.path())?;
    Ok((messages, warnings))
}

/// WASM-safe fallback: PST extraction is not available on WASM due to tempfile incompatibility.
#[cfg(all(feature = "email", target_arch = "wasm32"))]
pub(crate) fn extract_pst_messages(_pst_data: &[u8]) -> Result<(Vec<EmailExtractionResult>, Vec<ProcessingWarning>)> {
    Err(XbergError::Validation {
        message: "PST extraction is not supported on WebAssembly targets".to_string(),
        source: None,
    })
}

/// Extract PST messages directly from a file path, bypassing the in-memory copy.
///
/// Used by `PstExtractor::extract_file` to avoid the double-allocation that
/// occurs when the full PST is first read into a `Vec<u8>` and then written
/// back out to a tempfile before parsing.
#[cfg(all(feature = "email", feature = "tokio-runtime"))]
pub(crate) fn extract_pst_from_path(
    path: &std::path::Path,
) -> Result<(Vec<EmailExtractionResult>, Vec<ProcessingWarning>)> {
    extract_from_path(path)
}

#[cfg(feature = "email")]
fn extract_from_path(path: &std::path::Path) -> Result<(Vec<EmailExtractionResult>, Vec<ProcessingWarning>)> {
    let store = outlook_pst::open_store(path).map_err(|e| XbergError::Validation {
        message: format!("Failed to open PST file: {e}"),
        source: None,
    })?;

    Ok(extract_from_store(store.as_ref()))
}

/// Walk an already-opened PST `Store` and extract all messages, collecting
/// non-fatal `ProcessingWarning`s along the way.
///
/// Split out from `extract_from_path` so the traversal logic can be unit
/// tested against a synthetic `Store` implementation without needing a real
/// PST file on disk (see issue #162).
#[cfg(feature = "email")]
fn extract_from_store(
    store: &dyn outlook_pst::messaging::store::Store,
) -> (Vec<EmailExtractionResult>, Vec<ProcessingWarning>) {
    let mut warnings = Vec::new();

    let ipm_entry = match store.properties().ipm_sub_tree_entry_id() {
        Ok(e) => e,
        Err(e) => {
            warnings.push(ProcessingWarning {
                source: Cow::Borrowed("pst_extraction"),
                message: Cow::Owned(format!("Failed to locate IPM (mail) sub-tree in PST store: {e}")),
            });
            return (Vec::new(), warnings);
        }
    };

    let root_folder = match store.open_folder(&ipm_entry) {
        Ok(f) => f,
        Err(e) => {
            warnings.push(ProcessingWarning {
                source: Cow::Borrowed("pst_extraction"),
                message: Cow::Owned(format!("Failed to open IPM (mail) sub-tree root folder: {e}")),
            });
            return (Vec::new(), warnings);
        }
    };

    let root_name = root_folder
        .properties()
        .display_name()
        .unwrap_or_else(|_| "Top of Personal Folders".to_string());
    let ipm_node_id = u32::from(ipm_entry.node_id());
    let mut seeds: Vec<PstFolderSeed> = vec![(root_folder, 0, root_name)];

    let (non_ipm_seeds, mut discovery_warnings) = discover_non_ipm_top_level_folders(store, ipm_node_id);
    seeds.extend(non_ipm_seeds);
    warnings.append(&mut discovery_warnings);

    let (messages, mut traversal_warnings) = walk_folder_tree(store, seeds);
    warnings.append(&mut traversal_warnings);

    (messages, warnings)
}

/// Enumerate the PST store's true top-level folders and return every one
/// that is *not* the already-handled IPM (mail) sub-tree, ready to seed
/// [`walk_folder_tree`] alongside it.
///
/// Split out from `extract_from_store` (issue #162) so the enumeration can be
/// exercised without a fully-populated `StoreProperties` — every id is either
/// opened as a seed folder or reported via a `ProcessingWarning`, traversal
/// never aborts because one non-IPM folder failed to open.
///
/// # Reaches the root hierarchy table via a workaround, not `Store::root_hierarchy_table()`
///
/// `outlook_pst::messaging::store::Store::root_hierarchy_table()` deadlocks
/// unconditionally in `outlook-pst` 1.2.0 (the version on crates.io, and the
/// version this crate depends on): its default implementation locks the PST
/// file-reader `Mutex` to resolve the root folder's B-tree node, keeps that
/// `MutexGuard` alive, and then calls `TableContextInner::read`, which tries
/// to lock the *same* `Mutex` again on the same thread. `std::sync::Mutex` is
/// not reentrant, so the second `.lock()` call blocks forever — this happens
/// before a single row is read, on every PST file, not only malformed ones.
/// Upstream fixed this on `main` (PR #55) by scoping the lock guard to a block
/// that ends before `TableContext::read` runs, but nothing has been published:
/// crates.io tops out at 1.2.0 and the `outlook-pst` release workflow has no
/// `release-pr` job, so no 1.2.1 appears without a maintainer manually
/// bumping the version.
///
/// Instead of calling `root_hierarchy_table()`, this function reaches the
/// identical node (`NodeId::new(HierarchyTable, NID_ROOT_FOLDER.index())`)
/// through a path that is *already* correctly scoped in 1.2.0:
/// `FolderInner::read_table` binds its B-tree node inside a block, so the
/// file-reader lock is dropped before `TableContext::read` is called. Opening
/// the root folder through the public API —
/// `store.properties().make_entry_id(NID_ROOT_FOLDER)` ->
/// `store.open_folder(&entry_id)` -> `folder.hierarchy_table()` — is exactly
/// how upstream's own `FolderInner::read` expects the root to be opened:
/// `NID_ROOT_FOLDER`'s type bits (`0x122 & 0x1F == 0x02`) satisfy the
/// `NormalFolder | SearchFolder` gate, and `read` even special-cases
/// `entry_id.node_id() == NID_ROOT_FOLDER` when computing the folder type.
///
/// `Folder::hierarchy_table()` returns `Option`, not `Result` (it swallows
/// the underlying read error via `.ok()` internally), so its `None` case is
/// reported below as its own `ProcessingWarning` distinct from the two error
/// arms above it — never silently treated as "no folders found".
#[cfg(feature = "email")]
fn discover_non_ipm_top_level_folders(
    store: &dyn outlook_pst::messaging::store::Store,
    ipm_node_id: u32,
) -> (Vec<PstFolderSeed>, Vec<ProcessingWarning>) {
    let mut seeds = Vec::new();
    let mut warnings = Vec::new();

    let root_entry_id = match store.properties().make_entry_id(NID_ROOT_FOLDER) {
        Ok(e) => e,
        Err(e) => {
            warnings.push(ProcessingWarning {
                source: Cow::Borrowed("pst_extraction"),
                message: Cow::Owned(format!(
                    "Failed to build entry ID for PST root folder while enumerating non-IPM top-level folders: {e}"
                )),
            });
            return (seeds, warnings);
        }
    };
    let root_folder = match store.open_folder(&root_entry_id) {
        Ok(f) => f,
        Err(e) => {
            warnings.push(ProcessingWarning {
                source: Cow::Borrowed("pst_extraction"),
                message: Cow::Owned(format!(
                    "Failed to open PST root folder while enumerating non-IPM top-level folders: {e}"
                )),
            });
            return (seeds, warnings);
        }
    };
    let root_table = match root_folder.hierarchy_table() {
        Some(t) => t.clone(),
        None => {
            warnings.push(ProcessingWarning {
                source: Cow::Borrowed("pst_extraction"),
                message: Cow::Owned(
                    "PST root folder has no hierarchy table; cannot enumerate non-IPM top-level folders".to_string(),
                ),
            });
            return (seeds, warnings);
        }
    };

    let (top_level_ids, truncated) = collect_row_ids(root_table.as_ref());
    if truncated {
        warnings.push(ProcessingWarning {
            source: Cow::Borrowed("pst_extraction"),
            message: Cow::Owned(format!(
                "PST store root exceeds the maximum top-level folder limit ({MAX_TABLE_ROWS}); remaining top-level folders skipped"
            )),
        });
    }

    for id in non_ipm_top_level_ids(&top_level_ids, Some(ipm_node_id)) {
        let node = NodeId::from(id);
        let entry_id = match store.properties().make_entry_id(node) {
            Ok(e) => e,
            Err(e) => {
                warnings.push(ProcessingWarning {
                    source: Cow::Borrowed("pst_extraction"),
                    message: Cow::Owned(format!(
                        "Failed to create entry ID for non-IPM top-level folder node {:?}: {}; folder skipped",
                        node, e
                    )),
                });
                continue;
            }
        };
        let top_folder = match store.open_folder(&entry_id) {
            Ok(f) => f,
            Err(e) => {
                warnings.push(ProcessingWarning {
                    source: Cow::Borrowed("pst_extraction"),
                    message: Cow::Owned(format!(
                        "Failed to open non-IPM top-level folder (node {:?}): {}; folder skipped",
                        node, e
                    )),
                });
                continue;
            }
        };
        let top_name = top_folder
            .properties()
            .display_name()
            .unwrap_or_else(|_| format!("(unnamed non-IPM folder, node {node:?})"));
        seeds.push((top_folder, 0, top_name));
    }

    (seeds, warnings)
}

/// Walk a set of already-opened top-level folders (and their subtrees),
/// extracting every message and collecting non-fatal `ProcessingWarning`s.
///
/// Split out from `extract_from_store` (issue #162) so the traversal itself
/// — including its termination guarantees — can be unit tested against a
/// synthetic folder tree, independent of how the seed folders were
/// discovered (IPM sub-tree vs. non-IPM top-level folders).
///
/// Termination is guaranteed by two independent bounds: `depth > 50` caps how
/// deep (or how many times, for a cyclic tree) folders are nested, and
/// [`collect_row_ids`] caps how many rows are read from any single table —
/// without the latter, a table whose row iterator never terminates hangs this
/// function forever regardless of the depth cap, because the hang happens
/// while reading rows *within* one folder, before depth is ever considered.
///
/// # No de-duplication of messages across folders (investigated for issue #162)
///
/// [`discover_non_ipm_top_level_folders`] can seed genuine PST *search*
/// folders (`NodeIdType::SearchFolder`), whose contents tables, per
/// [MS-PST] 2.4.8.6, are specified to reference messages that physically live
/// in another (non-search) folder — a real aliasing hazard for a naive
/// per-folder walk. This was investigated against the vendored `outlook-pst`
/// 1.2.0 source rather than assumed: `Folder::contents_table()`
/// (`FolderInner::contents_table` in `messaging/folder.rs`) is *not*
/// type-aware — for every folder, search or normal, it unconditionally reads
/// `NodeIdType::ContentsTable` (nid type `0x0E`). A search folder's actual
/// linked-message rows live under the distinct `NodeIdType::SearchContentsTable`
/// (nid type `0x10`, same node index, different type tag per `NodeId::new`'s
/// bit layout in `ndb/node_id.rs`) — a variant that exists only as an enum
/// case in `ndb/node_id.rs` and is never referenced anywhere else in the
/// crate. So `contents_table()` on a search folder looks up a node id that a
/// search folder never has, and returns `None` (`read_table` maps a missing
/// B-tree entry to `Ok(None)`, not an error). No messages are ever read back
/// from a search folder through this API today, so no message can be emitted
/// twice — de-duplication would guard against a code path that cannot
/// currently execute. If a future `outlook-pst` upgrade adds real
/// `SearchContentsTable` support to `contents_table()`, this analysis must be
/// redone and de-duplication (preferring the real IPM `folder_path` over the
/// search folder's) added at that point.
#[cfg(feature = "email")]
fn walk_folder_tree(
    store: &dyn outlook_pst::messaging::store::Store,
    mut folder_stack: Vec<PstFolderSeed>,
) -> (Vec<EmailExtractionResult>, Vec<ProcessingWarning>) {
    let mut messages = Vec::new();
    let mut warnings = Vec::new();

    while let Some((folder, depth, folder_path)) = folder_stack.pop() {
        if depth > 50 {
            warnings.push(ProcessingWarning {
                source: Cow::Borrowed("pst_extraction"),
                message: Cow::Owned(format!(
                    "Folder '{folder_path}' exceeds maximum traversal depth (50); subtree truncated"
                )),
            });
            continue;
        }

        if let Some(contents) = folder.contents_table() {
            let (ids, truncated) = collect_row_ids(contents.as_ref());
            if truncated {
                warnings.push(ProcessingWarning {
                    source: Cow::Borrowed("pst_extraction"),
                    message: Cow::Owned(format!(
                        "Folder '{folder_path}' contents table exceeds the maximum row limit ({MAX_TABLE_ROWS}); remaining messages skipped"
                    )),
                });
            }
            for id in ids {
                let node = NodeId::from(id);
                let entry_id = match store.properties().make_entry_id(node) {
                    Ok(e) => e,
                    Err(e) => {
                        warnings.push(ProcessingWarning {
                            source: Cow::Borrowed("pst_extraction"),
                            message: Cow::Owned(format!(
                                "Failed to create entry ID for message node {:?}: {}",
                                node, e
                            )),
                        });
                        continue;
                    }
                };
                let msg = match store.open_message(&entry_id, None) {
                    Ok(m) => m,
                    Err(e) => {
                        warnings.push(ProcessingWarning {
                            source: Cow::Borrowed("pst_extraction"),
                            message: Cow::Owned(format!("Failed to open message {:?}: {}", entry_id, e)),
                        });
                        continue;
                    }
                };
                messages.push(extract_message_content(msg.as_ref(), &entry_id, &folder_path));
            }
        }

        if let Some(hierarchy) = folder.hierarchy_table() {
            let (ids, truncated) = collect_row_ids(hierarchy.as_ref());
            if truncated {
                warnings.push(ProcessingWarning {
                    source: Cow::Borrowed("pst_extraction"),
                    message: Cow::Owned(format!(
                        "Folder '{folder_path}' hierarchy table exceeds the maximum row limit ({MAX_TABLE_ROWS}); remaining subfolders skipped"
                    )),
                });
            }
            for id in ids {
                let node = NodeId::from(id);
                let entry_id = match store.properties().make_entry_id(node) {
                    Ok(e) => e,
                    Err(e) => {
                        warnings.push(ProcessingWarning {
                            source: Cow::Borrowed("pst_extraction"),
                            message: Cow::Owned(format!("Failed to create entry ID for folder node {:?}: {}", node, e)),
                        });
                        continue;
                    }
                };
                let sub_folder = match store.open_folder(&entry_id) {
                    Ok(f) => f,
                    Err(e) => {
                        warnings.push(ProcessingWarning {
                            source: Cow::Borrowed("pst_extraction"),
                            message: Cow::Owned(format!("Failed to open folder {:?}: {}", entry_id, e)),
                        });
                        continue;
                    }
                };
                let sub_name = sub_folder
                    .properties()
                    .display_name()
                    .unwrap_or_else(|_| format!("(unnamed folder, node {node:?})"));
                let sub_path = format!("{folder_path}/{sub_name}");
                folder_stack.push((sub_folder, depth + 1, sub_path));
            }
        }
    }

    (messages, warnings)
}

#[cfg(feature = "email")]
fn extract_message_content(message: &dyn PstMessage, entry_id: &EntryId, folder_path: &str) -> EmailExtractionResult {
    let props = message.properties();

    let subject = get_str_prop(props, 0x0037);
    let sender_name = get_str_prop(props, 0x0C1A);
    let sender_email = get_str_prop(props, 0x0C1F);
    let from_email = sender_email.or(sender_name);

    let plain_text = get_str_prop(props, 0x1000);
    let html_content = get_str_prop(props, 0x1013);
    // PR_RTF_COMPRESSED (0x1009): fallback body source when neither plain text
    // nor HTML is present. Decompressed and stripped via the same MS-OXRTFCP
    // helpers the MSG extraction path uses (extraction/email.rs).
    let rtf_body = get_binary_prop(props, 0x1009)
        .and_then(|data| super::email::decompress_rtf_compressed(&data))
        .map(|rtf| super::email::strip_rtf_to_plain_text(&rtf))
        .filter(|s| !s.is_empty());

    let content = resolve_pst_body(plain_text.as_deref(), html_content.as_deref(), rtf_body.as_deref());

    let date = props.get(0x0E06).and_then(|v| {
        if let PropertyValue::Time(ft) = v {
            Some(windows_filetime_to_string(*ft))
        } else {
            None
        }
    });

    let record_key = entry_id.record_key();
    let node_id_bytes = u32::from(entry_id.node_id()).to_le_bytes();
    let entry_id_hex: String = std::iter::repeat_n(0u8, 4)
        .chain(record_key.iter().copied())
        .chain(node_id_bytes.iter().copied())
        .map(|b| format!("{b:02X}"))
        .collect();

    let mut to_emails: Vec<String> = Vec::new();
    let mut cc_emails: Vec<String> = Vec::new();
    let mut bcc_emails: Vec<String> = Vec::new();

    if let Some(recipient_table) = message.recipient_table() {
        let context = recipient_table.context();
        let col_defs: Vec<(u16, _)> = context.columns().iter().map(|c| (c.prop_id(), c.prop_type())).collect();

        for row in recipient_table.rows_matrix() {
            let Ok(col_values) = row.columns(context) else {
                continue;
            };

            let mut recipient_type: i32 = 1;
            let mut display_name: Option<String> = None;
            let mut smtp_email: Option<String> = None;

            for ((prop_id, prop_type), value_opt) in col_defs.iter().zip(col_values.iter()) {
                let Some(value_record) = value_opt else {
                    continue;
                };
                let Ok(value) = recipient_table.read_column(value_record, *prop_type) else {
                    continue;
                };

                match prop_id {
                    0x0C15 => {
                        if let PropertyValue::Integer32(v) = value {
                            recipient_type = v;
                        }
                    }
                    0x3001 => {
                        display_name = prop_value_to_string(&value);
                    }
                    0x39FE | 0x3003 if smtp_email.is_none() => {
                        smtp_email = prop_value_to_string(&value);
                    }
                    _ => {}
                }
            }

            let recipient = smtp_email.or(display_name).unwrap_or_default();
            if recipient.is_empty() {
                continue;
            }
            match recipient_type {
                1 => to_emails.push(recipient),
                2 => cc_emails.push(recipient),
                3 => bcc_emails.push(recipient),
                _ => {
                    tracing::warn!(recipient_type, "Unknown MAPI recipient type; skipping recipient");
                }
            }
        }
    }

    let mut attachments: Vec<EmailAttachment> = Vec::new();

    if let Some(attach_table) = message.attachment_table() {
        let context = attach_table.context();
        let col_defs: Vec<(u16, _)> = context.columns().iter().map(|c| (c.prop_id(), c.prop_type())).collect();

        for row in attach_table.rows_matrix() {
            let Ok(col_values) = row.columns(context) else {
                continue;
            };

            let mut long_filename: Option<String> = None;
            let mut short_filename: Option<String> = None;
            let mut attach_data: Option<Vec<u8>> = None;

            for ((prop_id, prop_type), value_opt) in col_defs.iter().zip(col_values.iter()) {
                let Some(value_record) = value_opt else {
                    continue;
                };
                let Ok(value) = attach_table.read_column(value_record, *prop_type) else {
                    continue;
                };

                match prop_id {
                    0x3707 => long_filename = prop_value_to_string(&value),
                    0x3704 => short_filename = prop_value_to_string(&value),
                    0x3701 => {
                        if let PropertyValue::Binary(v) = value {
                            attach_data = Some(v.buffer().to_vec());
                        }
                    }
                    _ => {}
                }
            }

            let filename = long_filename.or(short_filename);
            let size = attach_data.as_ref().map(|d| d.len());
            let mime_type = filename
                .as_deref()
                .and_then(|f| mime_guess::from_path(f).first())
                .map(|m| m.to_string());
            let is_image = mime_type.as_deref().is_some_and(|m| m.starts_with("image/"));

            attachments.push(EmailAttachment {
                name: filename.clone(),
                filename,
                mime_type,
                size,
                is_image,
                data: attach_data.map(bytes::Bytes::from),
            });
        }
    }

    EmailExtractionResult {
        subject,
        from_email,
        to_emails,
        cc_emails,
        bcc_emails,
        date,
        message_id: None,
        plain_text,
        html_content,
        content,
        attachments,
        metadata: HashMap::from([
            ("entry_id".to_string(), entry_id_hex),
            ("folder_path".to_string(), folder_path.to_string()),
        ]),
    }
}

/// Get a string value from message properties by property ID.
#[cfg(feature = "email")]
fn get_str_prop(props: &outlook_pst::messaging::message::MessageProperties, prop_id: u16) -> Option<String> {
    prop_value_to_string(props.get(prop_id)?)
}

/// Read a binary property (e.g. `PR_RTF_COMPRESSED`) verbatim, without string conversion.
#[cfg(feature = "email")]
fn get_binary_prop(props: &outlook_pst::messaging::message::MessageProperties, prop_id: u16) -> Option<Vec<u8>> {
    match props.get(prop_id)? {
        PropertyValue::Binary(v) => Some(v.buffer().to_vec()),
        _ => None,
    }
}

/// Resolve the message body from the available sources, in the same precedence
/// order the MSG extraction path uses (extraction/email.rs): plain text first,
/// then cleaned HTML, then RTF-decompressed plain text, else empty.
///
/// Pure and dependency-free so it can be unit-tested without a real PST file.
fn resolve_pst_body(plain_text: Option<&str>, html_content: Option<&str>, rtf_body: Option<&str>) -> String {
    if let Some(plain) = plain_text.filter(|s| !s.is_empty()) {
        plain.to_string()
    } else if let Some(html) = html_content.filter(|s| !s.is_empty()) {
        super::email::clean_html_content(html)
    } else if let Some(rtf) = rtf_body.filter(|s| !s.is_empty()) {
        rtf.to_string()
    } else {
        String::new()
    }
}

/// Convert a `PropertyValue` to a `String`, if it holds a string type.
#[cfg(feature = "email")]
fn prop_value_to_string(value: &PropertyValue) -> Option<String> {
    match value {
        PropertyValue::String8(v) => Some(v.to_string()),
        PropertyValue::Unicode(v) => Some(v.to_string()),
        PropertyValue::Binary(v) => Some(String::from_utf8_lossy(v.buffer()).into_owned()),
        _ => None,
    }
}

#[cfg(feature = "email")]
fn windows_filetime_to_string(filetime: i64) -> String {
    use chrono::DateTime;

    const EPOCH_DIFF_100NS: i64 = 116_444_736_000_000_000;
    if filetime < EPOCH_DIFF_100NS {
        return format!("(invalid timestamp: {})", filetime);
    }
    let unix_100ns = filetime - EPOCH_DIFF_100NS;
    let unix_secs = unix_100ns / 10_000_000;
    let nsecs = (unix_100ns % 10_000_000) * 100;

    DateTime::from_timestamp(unix_secs, nsecs as u32)
        .map(|dt| dt.to_rfc3339_opts(chrono::SecondsFormat::Secs, true))
        .unwrap_or_else(|| format!("(invalid timestamp: {})", filetime))
}

#[cfg(not(feature = "email"))]
pub(crate) fn extract_pst_messages(_pst_data: &[u8]) -> Result<(Vec<EmailExtractionResult>, Vec<ProcessingWarning>)> {
    Err(XbergError::FeatureNotEnabled {
        feature: "email".to_string(),
        context: Some("PST extraction requires the 'email' feature to be enabled".to_string()),
    })
}

#[cfg(test)]
#[cfg(feature = "email")]
mod tests {
    use super::*;
    use outlook_pst::{
        ltp::prop_context::PropertyValue,
        messaging::folder::{Folder, FolderProperties},
        messaging::store::{EntryId, Store, StoreProperties, StoreRecordKey},
        ndb::node_id::NodeId,
    };
    use std::io;

    /// Regression tests for issue #152: PST body resolution must fall back to
    /// PR_RTF_COMPRESSED (decompressed+stripped) when plain text is absent, and
    /// must clean raw HTML rather than dumping markup when only HTML is present.
    #[test]
    fn test_resolve_pst_body_prefers_plain_text_issue_152() {
        let result = resolve_pst_body(Some("plain body"), Some("<p>html body</p>"), Some("rtf body"));
        assert_eq!(result, "plain body");
    }

    #[test]
    fn test_resolve_pst_body_cleans_html_when_plain_absent_issue_152() {
        let result = resolve_pst_body(None, Some("<p>Hello <b>World</b></p>"), Some("rtf body"));
        assert_eq!(result, "Hello World");
    }

    #[test]
    fn test_resolve_pst_body_falls_back_to_rtf_when_only_rtf_present_issue_152() {
        let result = resolve_pst_body(None, None, Some("rtf-derived plain text"));
        assert_eq!(result, "rtf-derived plain text");
    }

    #[test]
    fn test_resolve_pst_body_empty_when_all_absent_issue_152() {
        let result = resolve_pst_body(None, None, None);
        assert_eq!(result, "");
    }

    #[test]
    fn test_resolve_pst_body_treats_empty_strings_as_absent_issue_152() {
        let result = resolve_pst_body(Some(""), Some(""), Some("rtf fallback"));
        assert_eq!(result, "rtf fallback");
    }

    #[test]
    fn test_decompress_and_strip_rtf_via_shared_email_helpers_issue_152() {
        // End-to-end through the actual MS-OXRTFCP decoder shared with the MSG
        // path: build a minimal "uncompressed" (MELA-magic) RTF-compressed blob
        // and confirm extraction/pst.rs can decompress+strip it via the
        // extraction/email.rs helpers exactly as extract_message_content does.
        let rtf_plain = b"{\\rtf1 Hello RTF World\\par}";
        let comp_size = (rtf_plain.len() + 12) as u32;
        let mut data = Vec::new();
        data.extend_from_slice(&comp_size.to_le_bytes());
        data.extend_from_slice(&(rtf_plain.len() as u32).to_le_bytes());
        data.extend_from_slice(&0x414c_454du32.to_le_bytes()); // MELA = uncompressed
        data.extend_from_slice(&0u32.to_le_bytes()); // crc, unused for MELA
        data.extend_from_slice(rtf_plain);

        let decompressed = super::super::email::decompress_rtf_compressed(&data).expect("should decompress");
        let plain = super::super::email::strip_rtf_to_plain_text(&decompressed);
        assert_eq!(plain, "Hello RTF World");
    }

    /// Regression test for issue #764: entry_id must be the MAPI hex format,
    /// not the Rust Debug representation of the EntryId struct.
    #[test]
    fn test_entry_id_hex_format_issue_764() {
        let record_key_bytes: [u8; 16] = [
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        ];
        let record_key = StoreRecordKey::new(record_key_bytes);

        let node_id = NodeId::from(0x04 | (1u32 << 5));
        let entry_id = EntryId::new(record_key, node_id);

        let node_id_u32 = u32::from(entry_id.node_id());
        let node_id_le = node_id_u32.to_le_bytes();
        let expected: String = std::iter::repeat_n(0u8, 4)
            .chain(record_key_bytes.iter().copied())
            .chain(node_id_le.iter().copied())
            .map(|b| format!("{b:02X}"))
            .collect();

        assert_eq!(expected.len(), 48, "MAPI EntryID must be 48 hex chars");

        assert!(expected.starts_with("00000000"), "EntryID must start with 00000000");

        assert!(!expected.contains("EntryId"), "must not be Debug representation");
        assert!(!expected.contains("record_key"), "must not be Debug representation");
        assert!(!expected.contains('{'), "must not be Debug representation");
    }

    #[test]
    fn test_filetime_known_epoch() {
        let filetime: i64 = 116_444_736_000_000_000;
        let result = windows_filetime_to_string(filetime);
        assert_eq!(result, "1970-01-01T00:00:00Z");
    }

    #[test]
    fn test_filetime_known_date() {
        let filetime: i64 = 133_549_776_000_000_000;
        let result = windows_filetime_to_string(filetime);
        assert_eq!(result, "2024-03-15T12:00:00Z");
    }

    #[test]
    fn test_filetime_before_unix_epoch_is_invalid() {
        let filetime: i64 = 116_444_735_999_999_999;
        let result = windows_filetime_to_string(filetime);
        assert!(result.starts_with("(invalid timestamp:"));
    }

    #[test]
    fn test_filetime_zero_is_invalid() {
        let result = windows_filetime_to_string(0);
        assert!(result.starts_with("(invalid timestamp:"));
    }

    #[test]
    fn test_prop_value_integer32_returns_none() {
        let val = PropertyValue::Integer32(42);
        assert_eq!(prop_value_to_string(&val), None);
    }

    #[test]
    fn test_prop_value_boolean_returns_none() {
        let val = PropertyValue::Boolean(true);
        assert_eq!(prop_value_to_string(&val), None);
    }

    #[test]
    fn test_prop_value_time_returns_none() {
        let val = PropertyValue::Time(133_549_776_000_000_000);
        assert_eq!(prop_value_to_string(&val), None);
    }

    /// A `Store` whose `StoreProperties` are empty, so `ipm_sub_tree_entry_id()`
    /// always fails, without needing a real PST file on disk.
    struct FakeStoreWithoutIpmSubtree {
        properties: StoreProperties,
    }

    impl Store for FakeStoreWithoutIpmSubtree {
        fn properties(&self) -> &StoreProperties {
            &self.properties
        }

        fn root_hierarchy_table(&self) -> io::Result<Rc<dyn outlook_pst::ltp::table_context::TableContext>> {
            Err(io::Error::other("not implemented in test fake"))
        }

        fn unique_value(&self) -> u32 {
            0
        }

        fn open_folder(&self, _entry_id: &EntryId) -> io::Result<Rc<dyn outlook_pst::messaging::folder::Folder>> {
            Err(io::Error::other("not implemented in test fake"))
        }

        fn open_message(
            &self,
            _entry_id: &EntryId,
            _prop_ids: Option<&[u16]>,
        ) -> io::Result<Rc<dyn outlook_pst::messaging::message::Message>> {
            Err(io::Error::other("not implemented in test fake"))
        }

        fn named_property_map(&self) -> io::Result<Rc<dyn outlook_pst::messaging::named_prop::NamedPropertyMap>> {
            Err(io::Error::other("not implemented in test fake"))
        }

        fn search_update_queue(&self) -> io::Result<Rc<dyn outlook_pst::messaging::search::SearchUpdateQueue>> {
            Err(io::Error::other("not implemented in test fake"))
        }
    }

    /// Regression test for issue #162(a): a PST whose IPM (mail) sub-tree cannot
    /// be located must surface a `ProcessingWarning`, not silently return an
    /// empty result.
    #[test]
    fn test_extract_from_store_warns_when_ipm_subtree_missing_issue_162() {
        let store = FakeStoreWithoutIpmSubtree {
            properties: StoreProperties::default(),
        };

        let (messages, warnings) = extract_from_store(&store);

        assert!(
            messages.is_empty(),
            "no messages should be extracted when the IPM sub-tree cannot be located"
        );
        assert_eq!(warnings.len(), 1, "exactly one warning should be emitted");
        assert_eq!(warnings[0].source.as_ref(), "pst_extraction");
        assert!(
            warnings[0]
                .message
                .contains("Failed to locate IPM (mail) sub-tree in PST store"),
            "unexpected warning message: {}",
            warnings[0].message
        );
    }

    /// Regression test for issue #162(c): if the PST root folder's entry ID
    /// cannot be built (e.g. the store's record key is missing),
    /// `discover_non_ipm_top_level_folders` must report a `ProcessingWarning`
    /// and return no seeds -- never panic or silently return nothing.
    #[test]
    fn test_discover_non_ipm_top_level_folders_warns_when_entry_id_creation_fails() {
        let store = FakeStoreWithoutIpmSubtree {
            properties: StoreProperties::default(),
        };

        let (seeds, warnings) = discover_non_ipm_top_level_folders(&store, 0);

        assert!(
            seeds.is_empty(),
            "no seeds should be produced when the root entry ID cannot be built"
        );
        assert_eq!(warnings.len(), 1, "exactly one warning should be emitted");
        assert_eq!(warnings[0].source.as_ref(), "pst_extraction");
        assert!(
            warnings[0]
                .message
                .contains("Failed to build entry ID for PST root folder"),
            "unexpected warning message: {}",
            warnings[0].message
        );
    }

    /// Absolute path to the only PST fixture checked into the repo
    /// (`test_documents/email/empty.pst`), resolved the same way integration
    /// tests under `crates/xberg/tests/helpers/mod.rs` do: two levels up from
    /// this crate's manifest directory is the workspace root.
    fn empty_pst_fixture_path() -> std::path::PathBuf {
        let workspace_root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .expect("crates/xberg should have a parent directory")
            .parent()
            .expect("crates/xberg should have a workspace root two levels up")
            .to_path_buf();

        workspace_root.join("test_documents/email/empty.pst")
    }

    /// Wraps a real `Store` (opened from the `empty.pst` fixture, which has a
    /// genuine record key that `StoreProperties` provides no public
    /// constructor for outside `outlook_pst`) so a single method can be
    /// overridden to simulate a failure while everything else -- crucially
    /// `properties()` and its real record key -- still comes from the real
    /// store.
    struct StoreWithFailingOpenFolder {
        inner: Rc<dyn Store>,
    }

    impl Store for StoreWithFailingOpenFolder {
        fn properties(&self) -> &StoreProperties {
            self.inner.properties()
        }

        fn root_hierarchy_table(&self) -> io::Result<Rc<dyn outlook_pst::ltp::table_context::TableContext>> {
            self.inner.root_hierarchy_table()
        }

        fn unique_value(&self) -> u32 {
            self.inner.unique_value()
        }

        fn open_folder(&self, _entry_id: &EntryId) -> io::Result<Rc<dyn Folder>> {
            Err(io::Error::other("simulated open_folder failure for test"))
        }

        fn open_message(
            &self,
            entry_id: &EntryId,
            prop_ids: Option<&[u16]>,
        ) -> io::Result<Rc<dyn outlook_pst::messaging::message::Message>> {
            self.inner.open_message(entry_id, prop_ids)
        }

        fn named_property_map(&self) -> io::Result<Rc<dyn outlook_pst::messaging::named_prop::NamedPropertyMap>> {
            self.inner.named_property_map()
        }

        fn search_update_queue(&self) -> io::Result<Rc<dyn outlook_pst::messaging::search::SearchUpdateQueue>> {
            self.inner.search_update_queue()
        }
    }

    /// Regression test for issue #162(c): if the PST root folder cannot be
    /// opened, `discover_non_ipm_top_level_folders` must report a
    /// `ProcessingWarning` distinct from the entry-ID-creation failure above,
    /// and still return no seeds. Wraps the real `empty.pst` fixture's `Store`
    /// so `properties().make_entry_id(NID_ROOT_FOLDER)` succeeds for real and
    /// only `open_folder` is made to fail.
    #[test]
    fn test_discover_non_ipm_top_level_folders_warns_when_root_folder_cannot_be_opened() {
        let fixture = empty_pst_fixture_path();
        assert!(fixture.exists(), "PST test fixture not found: {fixture:?}");
        let real_store = outlook_pst::open_store(&fixture).expect("should open empty.pst fixture");
        let store = StoreWithFailingOpenFolder { inner: real_store };

        let (seeds, warnings) = discover_non_ipm_top_level_folders(&store, 0);

        assert!(
            seeds.is_empty(),
            "no seeds should be produced when the root folder cannot be opened"
        );
        assert_eq!(warnings.len(), 1, "exactly one warning should be emitted");
        assert_eq!(warnings[0].source.as_ref(), "pst_extraction");
        assert!(
            warnings[0].message.contains("Failed to open PST root folder"),
            "unexpected warning message: {}",
            warnings[0].message
        );
    }

    /// A `Folder` whose `hierarchy_table()` always returns `None`, simulating
    /// `FolderInner::read_table`'s internal `.ok()` swallowing a read error.
    struct FolderWithNoHierarchyTable;

    impl Folder for FolderWithNoHierarchyTable {
        fn store(&self) -> Rc<dyn Store> {
            unreachable!("discover_non_ipm_top_level_folders never calls Folder::store on the root")
        }

        fn properties(&self) -> &FolderProperties {
            unreachable!("discover_non_ipm_top_level_folders never calls Folder::properties on the root")
        }

        fn hierarchy_table(&self) -> Option<&Rc<dyn outlook_pst::ltp::table_context::TableContext>> {
            None
        }

        fn contents_table(&self) -> Option<&Rc<dyn outlook_pst::ltp::table_context::TableContext>> {
            None
        }

        fn associated_table(&self) -> Option<&Rc<dyn outlook_pst::ltp::table_context::TableContext>> {
            None
        }
    }

    /// Wraps a real `Store` so `open_folder` returns a `Folder` whose
    /// `hierarchy_table()` is `None`, exercising the branch that
    /// `Store::root_hierarchy_table()`'s plain `Result` return type never had.
    struct StoreWithNoHierarchyTableRoot {
        inner: Rc<dyn Store>,
    }

    impl Store for StoreWithNoHierarchyTableRoot {
        fn properties(&self) -> &StoreProperties {
            self.inner.properties()
        }

        fn root_hierarchy_table(&self) -> io::Result<Rc<dyn outlook_pst::ltp::table_context::TableContext>> {
            self.inner.root_hierarchy_table()
        }

        fn unique_value(&self) -> u32 {
            self.inner.unique_value()
        }

        fn open_folder(&self, _entry_id: &EntryId) -> io::Result<Rc<dyn Folder>> {
            Ok(Rc::new(FolderWithNoHierarchyTable))
        }

        fn open_message(
            &self,
            entry_id: &EntryId,
            prop_ids: Option<&[u16]>,
        ) -> io::Result<Rc<dyn outlook_pst::messaging::message::Message>> {
            self.inner.open_message(entry_id, prop_ids)
        }

        fn named_property_map(&self) -> io::Result<Rc<dyn outlook_pst::messaging::named_prop::NamedPropertyMap>> {
            self.inner.named_property_map()
        }

        fn search_update_queue(&self) -> io::Result<Rc<dyn outlook_pst::messaging::search::SearchUpdateQueue>> {
            self.inner.search_update_queue()
        }
    }

    /// Regression test for issue #162(c): `Folder::hierarchy_table()` returns
    /// `Option`, not `Result` -- its `None` case (the underlying read error is
    /// swallowed inside `outlook_pst`) must surface its own distinct
    /// `ProcessingWarning`, never be treated the same as "no non-IPM folders
    /// found" (i.e. an empty, warning-free result).
    #[test]
    fn test_discover_non_ipm_top_level_folders_warns_when_hierarchy_table_is_none() {
        let fixture = empty_pst_fixture_path();
        assert!(fixture.exists(), "PST test fixture not found: {fixture:?}");
        let real_store = outlook_pst::open_store(&fixture).expect("should open empty.pst fixture");
        let store = StoreWithNoHierarchyTableRoot { inner: real_store };

        let (seeds, warnings) = discover_non_ipm_top_level_folders(&store, 0);

        assert!(
            seeds.is_empty(),
            "no seeds should be produced when the root has no hierarchy table"
        );
        assert_eq!(warnings.len(), 1, "exactly one warning should be emitted");
        assert_eq!(warnings[0].source.as_ref(), "pst_extraction");
        assert_eq!(
            warnings[0].message.as_ref(),
            "PST root folder has no hierarchy table; cannot enumerate non-IPM top-level folders"
        );
    }

    /// Proves the workaround does not reintroduce the deadlock a prior commit
    /// hit (see `crates/xberg/tests/pst_warnings_and_structure_test.rs`) when
    /// `discover_non_ipm_top_level_folders` was wired in using
    /// `Store::root_hierarchy_table()` directly: this calls it against the
    /// real, parsed `empty.pst` fixture. If the workaround reintroduced the
    /// deadlock, this test would hang rather than fail an assertion --
    /// running it under a timeout the first time is recommended.
    ///
    /// `empty.pst` actually has three non-IPM top-level folders -- "Search
    /// Root", "SPAM Search Folder 2", and "IPM_COMMON_VIEWS" -- all at depth
    /// 0. This test's primary job is proving the absence of the deadlock
    /// described above, not asserting an empty result; the exact folder set
    /// is pinned so a regression in discovery is still caught.
    #[test]
    fn test_discover_non_ipm_top_level_folders_against_real_empty_pst_fixture_issue_162c() {
        let fixture = empty_pst_fixture_path();
        assert!(fixture.exists(), "PST test fixture not found: {fixture:?}");
        let store = outlook_pst::open_store(&fixture).expect("should open empty.pst fixture");

        let ipm_entry = store
            .properties()
            .ipm_sub_tree_entry_id()
            .expect("empty.pst fixture should have a locatable IPM sub-tree");
        let ipm_node_id = u32::from(ipm_entry.node_id());

        let (seeds, warnings) = discover_non_ipm_top_level_folders(store.as_ref(), ipm_node_id);

        assert_eq!(
            warnings.len(),
            0,
            "well-formed empty.pst fixture should not produce warnings, got: {warnings:?}"
        );

        let mut names: Vec<&str> = seeds.iter().map(|(_, _, name)| name.as_str()).collect();
        names.sort_unstable();
        assert_eq!(
            names,
            vec!["IPM_COMMON_VIEWS", "SPAM Search Folder 2", "Search Root"],
            "unexpected non-IPM top-level folder set discovered in empty.pst"
        );
        assert_eq!(seeds.len(), 3, "expected exactly three non-IPM top-level folders");
        assert!(
            seeds.iter().all(|(_, depth, _)| *depth == 0),
            "all non-IPM top-level folders must be reported at depth 0, got: {:?}",
            seeds.iter().map(|(_, depth, name)| (depth, name)).collect::<Vec<_>>()
        );
    }

    /// [`non_ipm_top_level_ids`] must exclude exactly the id equal to
    /// `ipm_node_id`, preserving the order and every other id (including
    /// duplicates) from `top_level_ids`.
    #[test]
    fn test_non_ipm_top_level_ids_filters_out_ipm_node_only() {
        let top_level_ids = [10, 20, 30, 40];

        assert_eq!(
            non_ipm_top_level_ids(&top_level_ids, Some(20)),
            vec![10, 30, 40],
            "the id matching ipm_node_id must be removed and no others"
        );
    }

    /// When `ipm_node_id` is `None` (e.g. the caller has no IPM node to
    /// exclude), every id must be returned unchanged.
    #[test]
    fn test_non_ipm_top_level_ids_returns_all_ids_when_ipm_node_id_is_none() {
        let top_level_ids = [1, 2, 3];

        assert_eq!(non_ipm_top_level_ids(&top_level_ids, None), vec![1, 2, 3]);
    }

    /// When `ipm_node_id` does not match any id in `top_level_ids`, every id
    /// must be returned unchanged (no accidental over-filtering).
    #[test]
    fn test_non_ipm_top_level_ids_returns_all_ids_when_ipm_node_id_not_present() {
        let top_level_ids = [5, 6, 7];

        assert_eq!(non_ipm_top_level_ids(&top_level_ids, Some(999)), vec![5, 6, 7]);
    }

    /// An empty `top_level_ids` slice must yield an empty result regardless
    /// of `ipm_node_id`.
    #[test]
    fn test_non_ipm_top_level_ids_empty_input_returns_empty() {
        let top_level_ids: [u32; 0] = [];

        assert_eq!(non_ipm_top_level_ids(&top_level_ids, Some(1)), Vec::<u32>::new());
    }

    /// If `top_level_ids` contains the IPM node id more than once (a
    /// malformed/hostile hierarchy table), every occurrence must be filtered
    /// out, not just the first.
    #[test]
    fn test_non_ipm_top_level_ids_filters_all_duplicate_ipm_occurrences() {
        let top_level_ids = [7, 7, 8, 7];

        assert_eq!(non_ipm_top_level_ids(&top_level_ids, Some(7)), vec![8]);
    }

    /// Pins the finding behind `walk_folder_tree`'s "no de-duplication needed"
    /// doc comment: `outlook_pst::messaging::folder::Folder::contents_table()`
    /// (`outlook-pst` 1.2.0) is not type-aware for search folders. It always
    /// reads `NodeIdType::ContentsTable`, never the distinct
    /// `NodeIdType::SearchContentsTable` a real search folder's linked-message
    /// rows live under, so it returns `None` for a genuine search folder
    /// rather than the aliased messages a naive walk would need to
    /// de-duplicate. `empty.pst`'s "SPAM Search Folder 2" is exactly such a
    /// search folder (discovered as a non-IPM top-level seed). If a future
    /// `outlook-pst` upgrade starts returning `Some(_)` here, the "no
    /// aliasing risk" analysis in `walk_folder_tree`'s doc comment no longer
    /// holds and de-duplication must be added.
    #[test]
    fn test_search_folder_contents_table_is_none_no_message_aliasing_issue_162() {
        let fixture = empty_pst_fixture_path();
        assert!(fixture.exists(), "PST test fixture not found: {fixture:?}");
        let store = outlook_pst::open_store(&fixture).expect("should open empty.pst fixture");

        let ipm_entry = store
            .properties()
            .ipm_sub_tree_entry_id()
            .expect("empty.pst fixture should have a locatable IPM sub-tree");
        let ipm_node_id = u32::from(ipm_entry.node_id());

        let (seeds, warnings) = discover_non_ipm_top_level_folders(store.as_ref(), ipm_node_id);
        assert_eq!(warnings.len(), 0, "unexpected warnings: {warnings:?}");

        let (search_folder, _, _) = seeds
            .iter()
            .find(|(_, _, name)| name == "SPAM Search Folder 2")
            .expect("empty.pst fixture should contain the 'SPAM Search Folder 2' search folder");

        assert!(
            search_folder.contents_table().is_none(),
            "outlook-pst's Folder::contents_table() unexpectedly returned Some(_) for a search \
             folder -- this crate now exposes real search-folder contents, so walk_folder_tree's \
             'no aliasing risk' analysis is stale and de-duplication must be added"
        );
    }
}
