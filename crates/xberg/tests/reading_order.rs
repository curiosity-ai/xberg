//! Reading-order reconstruction tests for multi-column PDFs.
//!
//! Tests verify that:
//! - Plain text output respects reading-order reordering when enabled
//! - Markdown output respects reading-order reordering when enabled
//!
//! Both tests are `#[ignore]`d because they instantiate the layout engine, which
//! downloads the RT-DETR layout ONNX weights from Hugging Face Hub on first use
//! (see `crates/xberg/src/layout/model_manager.rs`). Network + ~300MB of weights is
//! not acceptable in the default `cargo test -p xberg --features full` leg that
//! `scripts/ci/rust/run-unit-tests.sh` drives.
//!
//! NOTHING CURRENTLY INVOKES THESE TESTS. To run them, a CI job (or a developer)
//! must execute:
//! ```text
//! cargo test -p xberg --features full --test reading_order -- --ignored --nocapture
//! ```

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(all(feature = "pdf", feature = "layout-detection", not(target_arch = "wasm32")))]

mod helpers;
use helpers::extract_bytes_document_blocking;

use std::path::PathBuf;
use xberg::core::config::{ExtractionConfig, OutputFormat, PdfConfig};

/// The fixture is a multi-page, multi-column research paper (DocLayNet, arXiv 2206.01062).
/// Producing less than this is a hard extraction failure, not a marginal difference.
const MIN_EXPECTED_CONTENT_BYTES: usize = 1000;

/// Load the multi-column test document.
///
/// Panics naming the absolute path when absent: `test_documents/` is bucket-fetched
/// (`python3 test_documents/scripts/fetch_corpus.py`) and is not in a bare checkout.
/// These tests are opt-in via `--ignored`, so a caller who asked for them deserves a
/// hard, named failure rather than a silent skip.
fn load_test_pdf() -> Vec<u8> {
    let path =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../test_documents/vendored/docling/pdf/2206.01062.pdf");
    std::fs::read(&path).unwrap_or_else(|error| {
        panic!(
            "missing fixture {}: {error}. Run `python3 test_documents/scripts/fetch_corpus.py`",
            path.display()
        )
    })
}

/// Build a layout-enabled config for the given output format and reading-order setting.
fn layout_config(output_format: OutputFormat, reading_order: bool) -> ExtractionConfig {
    ExtractionConfig {
        output_format,
        pdf_options: Some(PdfConfig {
            reading_order,
            ..Default::default()
        }),
        layout: Some(Default::default()),
        use_layout_for_markdown: true,
        use_cache: false,
        ..Default::default()
    }
}

/// Plain-text output must change when reading-order reconstruction is enabled.
#[test]
#[ignore = "downloads the RT-DETR layout ONNX weights from Hugging Face Hub; run with --ignored"]
fn plain_text_output_differs_when_reading_order_is_enabled() {
    assert_reading_order_changes_output(OutputFormat::Plain, "plain text");
}

/// Markdown output must change when reading-order reconstruction is enabled.
///
/// Regression guard: reading-order was once wired only to the plain-text path, so
/// markdown came out identical with and without it.
#[test]
#[ignore = "downloads the RT-DETR layout ONNX weights from Hugging Face Hub; run with --ignored"]
fn markdown_output_differs_when_reading_order_is_enabled() {
    assert_reading_order_changes_output(OutputFormat::Markdown, "markdown");
}

/// Extract the fixture twice — reading-order off, then on — and assert the outputs differ.
fn assert_reading_order_changes_output(output_format: OutputFormat, label: &str) {
    let content = load_test_pdf();

    let without_reading_order = extract_bytes_document_blocking(
        &content,
        "application/pdf",
        &layout_config(output_format.clone(), false),
    )
    .unwrap_or_else(|error| panic!("{label} extraction with reading_order=false failed: {error}"));

    let with_reading_order =
        extract_bytes_document_blocking(&content, "application/pdf", &layout_config(output_format, true))
            .unwrap_or_else(|error| panic!("{label} extraction with reading_order=true failed: {error}"));

    assert!(
        without_reading_order.content.len() >= MIN_EXPECTED_CONTENT_BYTES,
        "{label} reading_order=false produced {} bytes, expected at least {MIN_EXPECTED_CONTENT_BYTES}",
        without_reading_order.content.len()
    );
    assert!(
        with_reading_order.content.len() >= MIN_EXPECTED_CONTENT_BYTES,
        "{label} reading_order=true produced {} bytes, expected at least {MIN_EXPECTED_CONTENT_BYTES}",
        with_reading_order.content.len()
    );

    assert_ne!(
        without_reading_order.content,
        with_reading_order.content,
        "{label} output is byte-identical with and without reading_order on a multi-column paper, \
         so reading-order reconstruction is not reaching the {label} path ({} bytes both times)",
        without_reading_order.content.len()
    );
}
