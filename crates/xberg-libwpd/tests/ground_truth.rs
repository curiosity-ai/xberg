//! Full-decode tests against the WordPerfect corpus in the `test_documents`
//! submodule, covering WP 4.2 through Corel WP6 and the Macintosh variants.
//!
//! These assert on the *structure* of the decoded [`xberg_libwpd::WpdDocument`]
//! — event kinds, table cell spans, note/aside bracketing, metadata — rather
//! than on any rendered string. Comparing rendered text/Markdown against
//! ground-truth fixtures is a concern of a layer above this crate (this
//! crate only decodes libwpd's structured model; it renders nothing).
//!
//! Every test skips when the submodule is not checked out (or its LFS objects
//! are not pulled), matching the other corpus-backed tests in this workspace.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests

use std::path::PathBuf;
use xberg_libwpd::{WpdDocument, WpdEvent};

/// Every document stem this crate can decode from the corpus, with its
/// source extension; WP 4.2/5.x samples use `.wp`.
const STEMS: &[&str] = &["wp42", "wp50", "wp51", "wp6", "wp_mac1", "wp_mac3"];

fn source_name(stem: &str) -> String {
    match stem {
        "wp42" | "wp50" | "wp51" => format!("{stem}.wp"),
        _ => format!("{stem}.wpd"),
    }
}

fn test_documents() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../test_documents")
}

/// Reads a corpus file, or returns `None` when the submodule is absent or the
/// file is still an unfetched LFS pointer.
fn corpus(rel: &str) -> Option<Vec<u8>> {
    let bytes = std::fs::read(test_documents().join(rel)).ok()?;
    if bytes.starts_with(b"version https://git-lfs") {
        return None;
    }
    Some(bytes)
}

fn text_events(doc: &WpdDocument) -> Vec<&str> {
    doc.events
        .iter()
        .filter_map(|e| match e {
            WpdEvent::Text(s) => Some(s.as_str()),
            _ => None,
        })
        .collect()
}

fn word_count(doc: &WpdDocument) -> usize {
    text_events(doc).iter().flat_map(|s| s.split_whitespace()).count()
}

#[test]
fn small_documents_decode_without_error() {
    let mut checked = 0;
    for stem in STEMS {
        let Some(bytes) = corpus(&format!("wordperfect/{}", source_name(stem))) else {
            continue;
        };
        assert!(xberg_libwpd::is_supported(&bytes), "{stem} should be recognized");
        let doc = xberg_libwpd::extract_document(&bytes).unwrap_or_else(|e| panic!("{stem}: {e}"));
        assert!(!doc.events.is_empty(), "{stem}: expected at least one event");
        assert!(word_count(&doc) > 0, "{stem}: expected extracted text");
        checked += 1;
    }
    eprintln!("compared {checked}/{} documents", STEMS.len());
}

/// `corel_wp6.wpd` is a 1.1 MB real-world WP6 report: nested emphasis,
/// superscripts, a footnote, tables and embedded images. This asserts the
/// decoded event stream actually carries that structure — not merely that
/// decoding succeeded — so the shim's serializer and Rust's decoder are
/// proven against a real, non-synthetic libwpd walk.
#[test]
fn corel_wp6_decodes_to_expected_structure() {
    let Some(bytes) = corpus("wordperfect/corel_wp6.wpd") else {
        return;
    };

    let doc = xberg_libwpd::extract_document(&bytes).expect("extract_document");

    let table_starts = doc.events.iter().filter(|e| matches!(e, WpdEvent::TableStart)).count();
    let table_ends = doc.events.iter().filter(|e| matches!(e, WpdEvent::TableEnd)).count();
    let cell_starts = doc
        .events
        .iter()
        .filter(|e| matches!(e, WpdEvent::CellStart { .. }))
        .count();
    let note_starts = doc
        .events
        .iter()
        .filter(|e| matches!(e, WpdEvent::NoteStart { .. }))
        .count();
    let superscript_starts = doc
        .events
        .iter()
        .filter(|e| matches!(e, WpdEvent::SuperscriptStart))
        .count();

    assert!(table_starts >= 1, "expected at least one table");
    assert_eq!(
        table_starts, table_ends,
        "every TableStart must have a matching TableEnd"
    );
    assert!(cell_starts >= 1, "expected at least one table cell");
    assert!(note_starts >= 1, "expected at least one footnote or endnote");
    assert!(superscript_starts >= 1, "expected at least one superscript span");

    // Every CellStart must carry a plausible column/span triple: spans are
    // at least 1 (libwpd's own invariant, mirrored by the shim's
    // non-negative.
    for event in &doc.events {
        if let WpdEvent::CellStart {
            column,
            col_span,
            row_span,
        } = event
        {
            assert!(*column >= -1, "column must be -1 or non-negative, got {column}");
            assert!(*col_span >= 1, "col_span must be at least 1, got {col_span}");
            assert!(*row_span >= 1, "row_span must be at least 1, got {row_span}");
        }
    }

    // The known note anchor text from the ground-truth fixture must appear
    // somewhere in the decoded text runs.
    let all_text = text_events(&doc).join(" ");
    assert!(
        all_text.contains("wordt op vrijwel"),
        "expected the known note body text to appear in the decoded events"
    );

    eprintln!(
        "corel_wp6: {} events, {table_starts} tables, {cell_starts} cells, \
         {note_starts} notes, {superscript_starts} superscript spans",
        doc.events.len()
    );
}

/// The CVE samples are malformed by construction and the `.wpg` files are
/// WordPerfect *Graphics*, which libwpd does not handle. None may panic, hang
/// or return success.
#[test]
fn malformed_and_unrelated_documents_are_rejected() {
    for name in [
        "cve_2007_1735_1.wpd",
        "cve_2015_1760_1.wpd",
        "cve_2015_1760_2.wpd",
        "graphic_v1.wpg",
        "graphic_v2.wpg",
    ] {
        let Some(bytes) = corpus(&format!("wordperfect/{name}")) else {
            continue;
        };
        assert!(
            !xberg_libwpd::is_supported(&bytes),
            "{name} should not be reported as supported"
        );
        assert!(
            xberg_libwpd::extract_document(&bytes).is_err(),
            "{name} should not decode"
        );
    }
}

/// Truncating a real document at many offsets exercises the parser's error
/// paths against input that starts out genuinely well-formed.
#[test]
fn truncated_documents_never_crash() {
    let Some(bytes) = corpus("wordperfect/wp51.wp") else {
        return;
    };
    for cut in (1..bytes.len()).step_by(613) {
        let head = &bytes[..cut];
        let _ = xberg_libwpd::is_supported(head);
        let _ = xberg_libwpd::extract_document(head);
    }
}
