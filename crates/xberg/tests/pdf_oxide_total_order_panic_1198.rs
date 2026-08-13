//! Regression tests for issue #1198 — pdf_oxide reading-order sort panics with
//! "user-provided comparison function does not correctly implement a total order".
//!
//! Two real-world PDFs from a 346k-file production run trip a total-order
//! violation inside `pdf_oxide`'s stable sort (still present through 0.3.72), on
//! two different comparators, both reached with OCR disabled (the PDF parse /
//! reading-order path, not OCR):
//!
//! * `total_order_panic_1198_text_path.pdf` — panics in
//!   `pdf_oxide::extractors::text::TextExtractor::extract_text_spans`, reached
//!   via `xberg::pdf::oxide::text::extract_page_text_column_aware`.
//! * `total_order_panic_1198_tables_path.pdf` — panics in
//!   `pdf_oxide::pipeline::reading_order::tategaki::TategakiStrategy::apply`,
//!   reached via the table-detection phase of `extract_all_from_oxide_document`.
//!
//! Because these calls run synchronously on a Tokio worker, the uncaught panic
//! unwound through the async boundary and surfaced to bindings as an opaque
//! `RustPanic`, aborting the whole extraction. The fix wraps the pdf_oxide calls
//! in `oxide::guard_oxide_panic` (`catch_unwind`): a text-path panic becomes a
//! recoverable `Err`, and a table-path panic falls back to no tables while
//! preserving the page text.
//!
//! These tests load the two repro PDFs from the `test_documents` submodule and
//! assert extraction completes deterministically instead of panicking. They are
//! skipped when the submodule is not checked out (e.g. CI without submodules).

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "pdf")]

mod helpers;
use helpers::{extract_bytes_document_blocking, get_test_file_path};

use xberg::ExtractionConfig;

/// Read a repro PDF from `test_documents/pdf/`, returning `None` when the
/// submodule is not present so the test skips rather than fails.
fn read_repro(name: &str) -> Option<Vec<u8>> {
    let path = get_test_file_path(&format!("pdf/{name}"));
    if !path.exists() {
        eprintln!(
            "skipping: repro PDF not found at {} (test_documents submodule?)",
            path.display()
        );
        return None;
    }
    Some(std::fs::read(&path).expect("read repro PDF"))
}

/// The text-path repro must not abort with a raw panic. pdf_oxide (through 0.3.72)
/// still trips the total-order sort on this file, but `guard_oxide_panic` contains
/// it: the panic surfaces as a recoverable `Err` (skip-and-log the file) instead of
/// unwinding through the async boundary. Reaching past the extraction call at all
/// proves containment — an uncaught panic would abort the test thread inside
/// `block_on`. The test is written to also stay green if a future pdf_oxide release
/// fixes the sort and extraction simply succeeds.
#[test]
fn text_path_repro_is_contained_not_a_raw_panic() {
    let Some(bytes) = read_repro("total_order_panic_1198_text_path.pdf") else {
        return;
    };
    let config = ExtractionConfig::default();

    match extract_bytes_document_blocking(&bytes, "application/pdf", &config) {
        Ok(_) => {}
        Err(error) => {
            let message = error.to_string();
            assert!(
                message.contains("panicked in pdf_oxide"),
                "text-path panic must be contained by guard_oxide_panic, got: {message}"
            );
        }
    }
}

/// The tables-path repro must not panic. The table-detection phase is now guarded
/// so a tategaki total-order panic falls back to no tables while preserving the
/// page text — extraction must succeed with non-empty content.
#[test]
fn tables_path_repro_extracts_without_panic() {
    let Some(bytes) = read_repro("total_order_panic_1198_tables_path.pdf") else {
        return;
    };
    let config = ExtractionConfig::default();

    let result = extract_bytes_document_blocking(&bytes, "application/pdf", &config)
        .expect("tables-path repro must extract (table panic falls back to no tables, text preserved)");

    assert!(
        !result.content.trim().is_empty(),
        "tables-path repro must preserve page text after the table phase is contained"
    );
}
