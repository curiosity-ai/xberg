//! Regression test for issue #1338.
//!
//! Under the default `Auto` strategy with no explicit `ocr` config (`ocr: None`,
//! which historically meant "OCR disabled"), a text-layer-less / scanned PDF was
//! detected as scanned but its signal was silently discarded — extraction returned
//! an empty native result with `ocr_used: false`. A whole-document text failure must
//! now route to OCR even without an explicit OCR config.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "ocr")]

mod helpers;
use helpers::{extract_uri_document_blocking, get_test_documents_dir, test_documents_available};
use xberg::core::config::ExtractionConfig;

#[test]
fn test_scanned_no_text_layer_pdf_ocrs_under_default_auto_config() {
    if !test_documents_available() {
        eprintln!("test_documents not available, skipping");
        return;
    }
    let path = get_test_documents_dir().join("pdf_scanned/issue_1338_scanned_no_text_layer.pdf");
    if !path.exists() {
        eprintln!("fixture {path:?} not found (submodule not updated?), skipping");
        return;
    }

    // Default config except caching is disabled for determinism: `ocr = None` and
    // `ocr_strategy = Auto`. Before #1338 this returned empty native content with
    // `ocr_used = false`; now the empty text layer is routed to OCR with a default config.
    let config = ExtractionConfig {
        use_cache: false,
        ..Default::default()
    };

    // Extraction failure (e.g. missing tessdata) must FAIL the test, not skip — a skip
    // here would let the test pass even when the fix is broken.
    let result = extract_uri_document_blocking(path, None, &config)
        .expect("extraction of the scanned repro PDF must succeed under default Auto config");

    assert!(
        !result.content.trim().is_empty(),
        "a scanned/text-layer-less PDF must recover OCR text under the default Auto config, \
         got empty content (regression of #1338)"
    );
    assert!(
        result.metadata.ocr_used,
        "extraction must report ocr_used=true for the scanned PDF under default Auto config"
    );
}
