//! Regression tests for commit 5d6e37e863 ("fix(pdf): report table failures that
//! take out a whole detector pass"), `crates/xberg/src/extractors/pdf/extraction.rs`.
//!
//! That commit added `table_stage_failure_warning` and wired it into the four
//! `unwrap_or_else`/`Err` fallbacks that substitute an empty table result when a
//! whole table-detection pass fails outright, rather than just one page:
//!
//! * the native pass's `unwrap_or_else` (extraction.rs, `extract_tables_native` call)
//! * the bordered pass's `unwrap_or_else` (extraction.rs, `extract_tables_bordered` call)
//! * the heuristic pass's `Err` arm (extraction.rs, `extract_tables_heuristic` call)
//! * the `guard_native_panic` fallback around the whole table-detection closure
//!   (extraction.rs, "whole-document" stage)
//!
//! Before that commit, all four substituted `Vec::new()` for the warnings, so a
//! pass that failed or panicked came back indistinguishable from a PDF that
//! genuinely has no tables — this is exactly the gap the finding against #603
//! describes as having zero test coverage.
//!
//! Verification that the gap was real: grepping the test suite (`crates/xberg/tests/`
//! and `crates/xberg/src/**`) for `pdf_tables` (the warning's `source`),
//! `table_stage_failure_warning`, and each of the four literal warning-message
//! fragments this commit introduced ("native table extraction failed for the
//! document", "bordered table extraction failed for the document", "heuristic
//! table extraction failed for the document", "whole-document table extraction
//! failed for the document") turned up no hits outside
//! `crates/xberg/src/extractors/pdf/extraction.rs` and
//! `crates/xberg/src/pdf/native/table.rs` themselves — nothing anywhere asserted on
//! these warnings.
//!
//! Only the "whole-document" (panic-guard) path is exercised here with a real
//! failure. The native and bordered passes only return `Err` when
//! `NativeDocument::doc.page_count()` fails (`pdf/native/table.rs`), but
//! `extract_text_and_metadata` — which runs earlier in the same pipeline and
//! whose own `?` would abort the whole extraction first — already calls
//! `page_count()` successfully on the same document handle
//! (`pdf/native/text.rs`), so that Err arm cannot be reached through the public
//! API without corrupting the document's internal state between calls, which is
//! not something a test can do without either a production-code seam or a
//! xberg_native_pdf-internal PDF corpus this repository does not have. The same applies
//! to the heuristic pass's hierarchy-extraction `Err` arm
//! (`pdf/native/hierarchy.rs`) for its `page_count()` fallback. These are noted
//! as untestable through the public API rather than left silently uncovered.
//!
//! The "whole-document" panic-guard path used to be exercised here too, driving
//! `test_documents/pdf/total_order_panic_1198_tables_path.pdf` through the public
//! API and asserting a real `xberg_native_pdf` panic and its resulting `pdf_tables`
//! warning. That fixture no longer panics: the `xberg-native` fork's commit
//! `9b0f9c99` ("Fix reading-order sort panics on scanned/malformed PDFs (#807)")
//! replaced the non-transitive pairwise tategaki column comparator with
//! `sort_vertical_tategaki`, a genuine total order, at all three call sites, and
//! `9b0f9c99` is an ancestor of the `xberg-native` 1.0.1 version this crate now
//! consumes (see `Cargo.lock`). No fixture in `test_documents/` still reaches a
//! real `xberg_native_pdf` panic through the table-detection stage (the sibling
//! `xberg_native_pdf_total_order_panic_1198.rs::tables_path_repro_extracts_without_panic`
//! already documents this same fixture as extracting cleanly). The message-format
//! coverage now lives as a direct unit test,
//! `table_stage_failure_warning_formats_the_message_it_promises` in
//! `crates/xberg/src/extractors/pdf/extraction.rs`'s `mod tests`, which
//! constructs the error directly rather than driving a real panic — it proves
//! the message format, not that a panic can still occur. Do not re-add a test
//! that asserts a panic on this fixture.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "pdf")]

mod helpers;
use helpers::{extract_uri_document_blocking, get_test_file_path};

use xberg::ExtractionConfig;

/// Depends on: proving the four `table_stage_failure_warning` call sites are
/// conditional on their respective pass actually failing — none of them fire
/// unconditionally. A warning source that always appears would be exactly as
/// broken as one that never does; only a genuine, unmodified successful
/// extraction distinguishes the two, so this deliberately does NOT construct a
/// warning or stub any pass — it runs a real, valid PDF through the public API.
#[test]
fn should_not_emit_pdf_tables_warning_on_a_successful_extraction() {
    let path = get_test_file_path("pdf/simple.pdf");
    if !path.exists() {
        eprintln!(
            "skipping: fixture not found at {} (test_documents submodule?)",
            path.display()
        );
        return;
    }
    let config = ExtractionConfig::default();

    let result =
        extract_uri_document_blocking(&path, None, &config).expect("a valid, non-panicking PDF must extract cleanly");

    let table_warnings: Vec<_> = result
        .processing_warnings
        .iter()
        .filter(|warning| warning.source == "pdf_tables")
        .collect();

    assert!(
        table_warnings.is_empty(),
        "no table-detection pass failed for this document, so no pdf_tables stage-failure warning \
         should be present, got: {table_warnings:?}"
    );
}
