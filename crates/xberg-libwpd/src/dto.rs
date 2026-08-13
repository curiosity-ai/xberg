//! Typed decode of the binary document model produced by `shim.cpp`.
//!
//! # Wire format (version 1)
//!
//! This spec MUST stay byte-for-byte identical to the comment block above the
//! serializer in `src/shim.cpp` — the two are independently hand-written
//! mirrors of the same format, not generated from a shared schema. All
//! integers are little-endian. Strings are raw UTF-8 bytes (not
//! NUL-terminated) with an explicit `u32` byte length, so embedded NULs never
//! truncate anything.
//!
//! ```text
//! document := version metadata_section event_section
//!
//! version         := u8                      // must equal WIRE_VERSION (1)
//!
//! metadata_section:= u32 metadata_count
//!                    metadata_count * metadata_entry
//! metadata_entry  := string key string value  // e.g. key = "dc:title"
//!
//! event_section   := u32 event_count
//!                    event_count * event
//! event           := u8 tag  payload          // payload shape depends on tag
//!
//! string          := u32 byte_len  byte_len * u8
//! ```
//!
//! ## Event tags and payloads
//!
//! | tag | event                | payload                                      |
//! |----:|-----------------------|-----------------------------------------------|
//! |   0 | `Text`                | `string text`                                 |
//! |   1 | `Tab`                 | —                                              |
//! |   2 | `Space`               | —                                              |
//! |   3 | `LineBreak`           | —                                              |
//! |   4 | `ParagraphEnd`        | —                                              |
//! |   5 | `ListItemStart`       | `u8 ordered` `u8 level` `u32 counter`         |
//! |   6 | `ListItemEnd`         | —                                              |
//! |   7 | `HeadingStart`        | `u8 level`                                    |
//! |   8 | `BoldStart`           | —                                              |
//! |   9 | `BoldEnd`             | —                                              |
//! |  10 | `ItalicStart`         | —                                              |
//! |  11 | `ItalicEnd`           | —                                              |
//! |  12 | `UnderlineStart`      | —                                              |
//! |  13 | `UnderlineEnd`        | —                                              |
//! |  14 | `StrikethroughStart`  | —                                              |
//! |  15 | `StrikethroughEnd`    | —                                              |
//! |  16 | `SuperscriptStart`    | —                                              |
//! |  17 | `SuperscriptEnd`      | —                                              |
//! |  18 | `SubscriptStart`      | —                                              |
//! |  19 | `SubscriptEnd`        | —                                              |
//! |  20 | `TableStart`          | —                                              |
//! |  21 | `RowStart`            | `u8 header`                                   |
//! |  22 | `CellStart`           | `i32 column` `u32 col_span` `u32 row_span`    |
//! |  23 | `CoveredCell`         | `i32 column`                                  |
//! |  24 | `CellEnd`             | —                                              |
//! |  25 | `RowEnd`              | —                                              |
//! |  26 | `TableEnd`            | —                                              |
//! |  27 | `HeaderStart`         | — (document running header, not a heading)    |
//! |  28 | `HeaderEnd`           | —                                              |
//! |  29 | `FooterStart`         | —                                              |
//! |  30 | `FooterEnd`           | —                                              |
//! |  31 | `NoteStart`           | `u8 endnote`                                   |
//! |  32 | `NoteEnd`             | —                                              |
//! |  33 | `AsideStart`          | `string kind`                                 |
//! |  34 | `AsideEnd`            | —                                              |
//! |  35 | `LinkStart`           | `string href`                                 |
//! |  36 | `LinkEnd`             | —                                              |
//! |  37 | `Field`               | `string text`                                 |
//!
//! `column` is `-1` when libwpd did not report `librevenge:column` for that
//! cell (see `shim.cpp`'s `getIntOr` default); every other integer is
//! non-negative.
//!
//! Booleans are serialized as a single `u8` (`0` or `1`); any other byte value
//! is a decode error.
//!
//! Metadata entries are not turned into events: they are collected up front
//! into [`WpdMetadata`]. The known keys are `dc:title`, `meta:initial-creator`,
//! `dc:subject` and `meta:keyword`, mapped onto `title`/`author`/`subject`/
//! `keywords` respectively. `meta:initial-creator` is libwpd's "Author" summary
//! field; `dc:creator` (WordPerfect's separate "Typist" field), `dc:type` and
//! `dc:language` (all also captured by the shim) have no dedicated field and are
//! only reachable via [`WpdMetadata::raw`], alongside every other pair, so no
//! metadata the shim captured is silently dropped.

// The binary wire decoder below (WIRE_VERSION, the size-clamp consts, Reader, decode,
// decode_event) is reached only through the FFI extractor, which is gated
// `#[cfg(any(target_os = "linux", "macos", "windows"))]` (see lib.rs). On other targets
// — e.g. the iOS slices the Swift artifact bundle cross-compiles — only the re-exported
// DTO types are used, leaving the decoder unreferenced. Allow that rather than fail the
// bundle's `-D warnings` build; on the FFI targets the decoder is used, so nothing is masked.
#![allow(dead_code)]

use crate::WpdError;

/// The wire format version this decoder understands. Bump alongside the
/// serializer in `shim.cpp` any time the layout above changes; a mismatched
/// version is rejected rather than misparsed.
const WIRE_VERSION: u8 = 1;

/// Smallest possible encoded size of one metadata entry: two zero-length,
/// length-prefixed strings (`u32` key length + `u32` value length, both zero).
/// Used to clamp the pre-allocation for the metadata vector so an untrusted
/// count can never request an abort-sized allocation.
const MIN_METADATA_ENTRY_BYTES: usize = 8;

/// Smallest possible encoded size of one event: a single tag byte with no
/// payload (e.g. `Tab`). Used to clamp the pre-allocation for the event vector.
const MIN_EVENT_BYTES: usize = 1;

/// A single event recorded from the librevenge callback walk, decoded 1:1
/// from the binary stream `shim.cpp` produces. Events are strictly ordered
/// and properly nested (each `*Start` has a matching `*End`), mirroring the
/// order libwpd invoked the corresponding callbacks in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WpdEvent {
    /// Literal run of text.
    Text(String),
    /// A tab character.
    Tab,
    /// A non-breaking/explicit space.
    Space,
    /// An explicit line break within a paragraph.
    LineBreak,
    /// End of the current paragraph.
    ParagraphEnd,
    /// Start of a list item.
    ListItemStart {
        /// Whether the enclosing list is ordered (numbered) or unordered (bulleted).
        ordered: bool,
        /// 1-based nesting depth of the enclosing list.
        level: u8,
        /// 1-based position within an ordered list; `0` for unordered lists.
        counter: u32,
    },
    /// End of a list item.
    ListItemEnd,
    /// Start of a heading paragraph (`text:outline-level` 1-6).
    HeadingStart {
        /// Heading level, 1 through 6.
        level: u8,
    },
    /// Start of a bold span.
    BoldStart,
    /// End of a bold span.
    BoldEnd,
    /// Start of an italic span.
    ItalicStart,
    /// End of an italic span.
    ItalicEnd,
    /// Start of an underline span.
    UnderlineStart,
    /// End of an underline span.
    UnderlineEnd,
    /// Start of a strikethrough span.
    StrikethroughStart,
    /// End of a strikethrough span.
    StrikethroughEnd,
    /// Start of a superscript span.
    SuperscriptStart,
    /// End of a superscript span.
    SuperscriptEnd,
    /// Start of a subscript span.
    SubscriptStart,
    /// End of a subscript span.
    SubscriptEnd,
    /// Start of a table.
    TableStart,
    /// Start of a table row.
    RowStart {
        /// Whether librevenge flagged this row as a header row.
        header: bool,
    },
    /// Start of a real (non-covered) table cell.
    CellStart {
        /// Absolute grid column from `librevenge:column`, or `-1` if libwpd
        /// did not report one for this cell.
        column: i32,
        /// Number of columns this cell spans (at least 1).
        col_span: u32,
        /// Number of rows this cell spans (at least 1).
        row_span: u32,
    },
    /// A covered (merged-away) grid position from a vertical or horizontal span.
    CoveredCell {
        /// Absolute grid column from `librevenge:column`, or `-1` if unknown.
        column: i32,
    },
    /// End of a table cell.
    CellEnd,
    /// End of a table row.
    RowEnd,
    /// End of a table.
    TableEnd,
    /// Start of the document's running header (recurs on every page).
    HeaderStart,
    /// End of the document's running header.
    HeaderEnd,
    /// Start of the document's running footer.
    FooterStart,
    /// End of the document's running footer.
    FooterEnd,
    /// Start of a footnote or endnote body, anchored at this point in the flow.
    NoteStart {
        /// `true` for an endnote, `false` for a footnote.
        endnote: bool,
    },
    /// End of a footnote or endnote body.
    NoteEnd,
    /// Start of a comment or text-box aside.
    AsideStart {
        /// `"comment"` or `"box"`.
        kind: String,
    },
    /// End of a comment or text-box aside.
    AsideEnd,
    /// Start of a hyperlink span.
    LinkStart {
        /// The link target, if librevenge reported one.
        href: String,
    },
    /// End of a hyperlink span.
    LinkEnd,
    /// An inserted field (page number, page count, date, time, or another
    /// field type libwpd reported by name), already mapped to a stable
    /// placeholder string by the shim.
    Field(String),
}

/// Document metadata captured from libwpd's `setDocumentMetaData` callback.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct WpdMetadata {
    /// `dc:title`.
    pub title: Option<String>,
    /// `dc:creator`.
    pub author: Option<String>,
    /// `dc:subject`.
    pub subject: Option<String>,
    /// `meta:keyword`.
    pub keywords: Option<String>,
    /// Every metadata key/value pair the shim captured, in the order libwpd
    /// reported them, including keys with no dedicated field above (for
    /// example `dc:type`, `dc:language`).
    pub raw: Vec<(String, String)>,
}

/// The structured WordPerfect document model: an ordered event stream plus
/// document-level metadata.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct WpdDocument {
    /// The recorded event stream, in document order.
    pub events: Vec<WpdEvent>,
    /// Document metadata, if libwpd reported any.
    pub metadata: WpdMetadata,
}

/// Cursor over the wire bytes; every read is bounds-checked and reports
/// `WpdError::Internal` on truncation rather than panicking or reading out of
/// bounds. Malformed shim output must never crash the Rust side. ~keep
struct Reader<'a> {
    bytes: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, pos: 0 }
    }

    /// Bytes not yet consumed. Used to bound pre-allocations against untrusted
    /// length prefixes so a lying count can never request an abort-sized `Vec`.
    fn remaining(&self) -> usize {
        self.bytes.len().saturating_sub(self.pos)
    }

    fn u8(&mut self) -> Result<u8, WpdError> {
        let b = *self.bytes.get(self.pos).ok_or(WpdError::Internal)?;
        self.pos += 1;
        Ok(b)
    }

    fn bool(&mut self) -> Result<bool, WpdError> {
        match self.u8()? {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err(WpdError::Internal),
        }
    }

    fn u32(&mut self) -> Result<u32, WpdError> {
        let end = self.pos.checked_add(4).ok_or(WpdError::Internal)?;
        let slice = self.bytes.get(self.pos..end).ok_or(WpdError::Internal)?;
        self.pos = end;
        Ok(u32::from_le_bytes(slice.try_into().expect("slice is exactly 4 bytes")))
    }

    fn i32(&mut self) -> Result<i32, WpdError> {
        self.u32().map(|v| v as i32)
    }

    fn string(&mut self) -> Result<String, WpdError> {
        let len = self.u32()? as usize;
        let end = self.pos.checked_add(len).ok_or(WpdError::Internal)?;
        let slice = self.bytes.get(self.pos..end).ok_or(WpdError::Internal)?;
        self.pos = end;
        String::from_utf8(slice.to_vec()).map_err(|_| WpdError::InvalidUtf8)
    }
}

/// Decode a document serialized by `xberg_wpd_extract_document` in `shim.cpp`.
/// See the module-level wire-format spec above; the two must stay in sync.
pub fn decode(bytes: &[u8]) -> Result<WpdDocument, WpdError> {
    let mut r = Reader::new(bytes);

    let version = r.u8()?;
    if version != WIRE_VERSION {
        return Err(WpdError::Internal);
    }

    let metadata_count = r.u32()?;
    // Clamp the pre-allocation to what the remaining bytes could possibly hold:
    // a lying count (e.g. u32::MAX in a 5-byte blob) must fail fast on the first
    // out-of-range read, never request an abort-sized `Vec` up front. ~keep
    let raw_cap = (metadata_count as usize).min(r.remaining() / MIN_METADATA_ENTRY_BYTES);
    let mut raw = Vec::with_capacity(raw_cap);
    let mut metadata = WpdMetadata::default();
    for _ in 0..metadata_count {
        let key = r.string()?;
        let value = r.string()?;
        match key.as_str() {
            "dc:title" => metadata.title = Some(value.clone()),
            // `dc:creator` is WordPerfect's separate "Typist" field, kept only in
            // `raw` rather than mistaken for the author.
            "meta:initial-creator" => metadata.author = Some(value.clone()),
            "dc:subject" => metadata.subject = Some(value.clone()),
            "meta:keyword" => metadata.keywords = Some(value.clone()),
            _ => {}
        }
        raw.push((key, value));
    }
    metadata.raw = raw;

    let event_count = r.u32()?;
    let events_cap = (event_count as usize).min(r.remaining() / MIN_EVENT_BYTES);
    let mut events = Vec::with_capacity(events_cap);
    for _ in 0..event_count {
        events.push(decode_event(&mut r)?);
    }

    Ok(WpdDocument { events, metadata })
}

fn decode_event(r: &mut Reader<'_>) -> Result<WpdEvent, WpdError> {
    let tag = r.u8()?;
    Ok(match tag {
        0 => WpdEvent::Text(r.string()?),
        1 => WpdEvent::Tab,
        2 => WpdEvent::Space,
        3 => WpdEvent::LineBreak,
        4 => WpdEvent::ParagraphEnd,
        5 => {
            let ordered = r.bool()?;
            let level = r.u8()?;
            let counter = r.u32()?;
            WpdEvent::ListItemStart {
                ordered,
                level,
                counter,
            }
        }
        6 => WpdEvent::ListItemEnd,
        7 => WpdEvent::HeadingStart { level: r.u8()? },
        8 => WpdEvent::BoldStart,
        9 => WpdEvent::BoldEnd,
        10 => WpdEvent::ItalicStart,
        11 => WpdEvent::ItalicEnd,
        12 => WpdEvent::UnderlineStart,
        13 => WpdEvent::UnderlineEnd,
        14 => WpdEvent::StrikethroughStart,
        15 => WpdEvent::StrikethroughEnd,
        16 => WpdEvent::SuperscriptStart,
        17 => WpdEvent::SuperscriptEnd,
        18 => WpdEvent::SubscriptStart,
        19 => WpdEvent::SubscriptEnd,
        20 => WpdEvent::TableStart,
        21 => WpdEvent::RowStart { header: r.bool()? },
        22 => {
            let column = r.i32()?;
            let col_span = r.u32()?;
            let row_span = r.u32()?;
            WpdEvent::CellStart {
                column,
                col_span,
                row_span,
            }
        }
        23 => WpdEvent::CoveredCell { column: r.i32()? },
        24 => WpdEvent::CellEnd,
        25 => WpdEvent::RowEnd,
        26 => WpdEvent::TableEnd,
        27 => WpdEvent::HeaderStart,
        28 => WpdEvent::HeaderEnd,
        29 => WpdEvent::FooterStart,
        30 => WpdEvent::FooterEnd,
        31 => WpdEvent::NoteStart { endnote: r.bool()? },
        32 => WpdEvent::NoteEnd,
        33 => WpdEvent::AsideStart { kind: r.string()? },
        34 => WpdEvent::AsideEnd,
        35 => WpdEvent::LinkStart { href: r.string()? },
        36 => WpdEvent::LinkEnd,
        37 => WpdEvent::Field(r.string()?),
        _ => return Err(WpdError::Internal),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn string_bytes(s: &str) -> Vec<u8> {
        let mut out = (s.len() as u32).to_le_bytes().to_vec();
        out.extend_from_slice(s.as_bytes());
        out
    }

    #[test]
    fn decode_rejects_unknown_version() {
        let bytes = vec![99u8, 0, 0, 0, 0, 0, 0, 0, 0];
        assert!(matches!(decode(&bytes), Err(WpdError::Internal)));
    }

    #[test]
    fn decode_rejects_truncated_input() {
        assert!(matches!(decode(&[1]), Err(WpdError::Internal)));
        assert!(matches!(decode(&[1, 0, 0]), Err(WpdError::Internal)));
    }

    #[test]
    fn decode_rejects_unknown_event_tag() {
        let mut bytes = vec![WIRE_VERSION];
        bytes.extend_from_slice(&0u32.to_le_bytes()); // metadata_count
        bytes.extend_from_slice(&1u32.to_le_bytes()); // event_count
        bytes.push(255); // unknown tag
        assert!(matches!(decode(&bytes), Err(WpdError::Internal)));
    }

    #[test]
    fn decode_rejects_abort_sized_count_without_allocating() {
        // first out-of-range read, never pre-allocate a ~200GB Vec (which would
        // abort the process, not return Err). Same for event_count.
        assert!(matches!(
            decode(&[WIRE_VERSION, 0xFF, 0xFF, 0xFF, 0xFF]),
            Err(WpdError::Internal)
        ));
        let mut bytes = vec![WIRE_VERSION];
        bytes.extend_from_slice(&0u32.to_le_bytes()); // metadata_count = 0
        bytes.extend_from_slice(&u32::MAX.to_le_bytes());
        assert!(matches!(decode(&bytes), Err(WpdError::Internal)));
    }

    #[test]
    fn decode_maps_initial_creator_to_author_not_typist() {
        // `dc:creator` is the separate Typist field and must stay raw-only.
        let mut bytes = vec![WIRE_VERSION];
        bytes.extend_from_slice(&2u32.to_le_bytes());
        bytes.extend(string_bytes("dc:creator"));
        bytes.extend(string_bytes("The Typist"));
        bytes.extend(string_bytes("meta:initial-creator"));
        bytes.extend(string_bytes("The Author"));
        bytes.extend_from_slice(&0u32.to_le_bytes()); // no events

        let doc = decode(&bytes).expect("valid document");
        assert_eq!(doc.metadata.author.as_deref(), Some("The Author"));
        assert!(
            doc.metadata
                .raw
                .iter()
                .any(|(k, v)| k == "dc:creator" && v == "The Typist")
        );
    }

    #[test]
    fn decode_parses_metadata_and_events() {
        let mut bytes = vec![WIRE_VERSION];
        bytes.extend_from_slice(&2u32.to_le_bytes());
        bytes.extend(string_bytes("dc:title"));
        bytes.extend(string_bytes("Sample"));
        bytes.extend(string_bytes("dc:type"));
        bytes.extend(string_bytes("report"));

        bytes.extend_from_slice(&3u32.to_le_bytes());
        bytes.push(1); // Tab
        bytes.push(7); // HeadingStart
        bytes.push(2); // level
        bytes.push(35); // LinkStart
        bytes.extend(string_bytes("https://example.com"));

        let doc = decode(&bytes).expect("valid document");
        assert_eq!(doc.metadata.title.as_deref(), Some("Sample"));
        assert_eq!(
            doc.metadata.raw,
            vec![
                ("dc:title".to_string(), "Sample".to_string()),
                ("dc:type".to_string(), "report".to_string()),
            ]
        );
        assert_eq!(
            doc.events,
            vec![
                WpdEvent::Tab,
                WpdEvent::HeadingStart { level: 2 },
                WpdEvent::LinkStart {
                    href: "https://example.com".to_string()
                },
            ]
        );
    }
}
