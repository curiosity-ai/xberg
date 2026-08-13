//! Regression test for GH#1411: PDF page-number / repeated-text detection deleting real
//! table data.
//!
//! The confirmed casualty named in the issue is the table cell `22` in the "Psychiatric
//! Inpatient 22" row of `pdfa_001.pdf` (ground truth: `test_documents/ground_truth/pdf/pdfa_001.txt`,
//! e.g. lines "Inpatient 22 Inpatient Days 2753" / "Inpatient 22 Inpatient 2753"). The cell now
//! survives, and this test exists to keep it that way.
//!
//! Note for anyone tracing this later: the loss was originally blamed on
//! `mark_cross_page_repeating_short_text` (`pdf/structure/classify.rs`). That attribution is
//! WRONG. `finalize_paragraph` (`pdf/structure/pipeline.rs`) returns `block_bbox: Some(..)` by
//! default — the `None` arm is a narrow early return for segments carrying a tag-tree role — and
//! that detector skips any paragraph with `block_bbox.is_some()`. A loose table cell therefore
//! never reaches it.
//!
//! This is deliberately an extraction-level test (not a unit test against synthetic
//! paragraphs) because the original defect went unnoticed precisely because no test drove the
//! real pipeline end to end against this fixture for this specific value.
//!
//! `test_documents/` is bucket-fetched and absent from a bare checkout. Unlike the other PDF
//! regression suites, this test does NOT silently skip when the fixture is missing — a vacuous
//! pass here would hide the exact regression it exists to catch. Fetch the corpus first:
//!   python3 test_documents/scripts/fetch_corpus.py
//!
//! Run with:
//!   cargo test -p xberg --features pdf --test issue_1411_page_number_deletion -- --nocapture

#![cfg(feature = "pdf")]

mod helpers;
use helpers::{extract_uri_document_blocking, get_test_documents_dir};
use xberg::core::config::{ExtractionConfig, OutputFormat};

/// Tokenize text into normalized lowercase words, matching the convention used by
/// `pdf_markdown_regression.rs`'s word-level scoring so token boundaries agree between suites.
fn tokenize(text: &str) -> Vec<String> {
    text.split_whitespace()
        .map(|w| w.trim_matches(|c: char| c.is_ascii_punctuation()).to_lowercase())
        .filter(|w| !w.is_empty())
        .collect()
}

#[test]
fn test_pdfa_001_retains_standalone_table_cell_22() {
    let pdf_path = get_test_documents_dir().join("pdf/pdfa_001.pdf");
    assert!(
        pdf_path.exists(),
        "fixture missing at {}: test_documents/ is bucket-fetched and not present in a bare \
         checkout. Run `python3 test_documents/scripts/fetch_corpus.py` before running this \
         test - a skip here would silently hide the GH#1411 regression it exists to catch.",
        pdf_path.display()
    );

    let config = ExtractionConfig {
        output_format: OutputFormat::Markdown,
        use_cache: false,
        ..Default::default()
    };
    let result =
        extract_uri_document_blocking(&pdf_path, None, &config).expect("pdfa_001.pdf must extract successfully");

    let tokens = tokenize(&result.content);

    // Assert the CELL IN ITS ROW, not a bare "22" anywhere in the document. The ground truth
    // contains five standalone `22` tokens, so a document-wide count of >= 1 would still pass with
    // the Psychiatric-Inpatient cell deleted and some unrelated `22` surviving — a test that passes
    // when the bug is present. The pair `Inpatient 22` is what the issue actually names, and it
    // appears at ground-truth lines 75 and 95. ~keep
    let inpatient_22_pairs = tokens
        .windows(2)
        // `tokenize` lowercases, so match lowercase here.
        .filter(|pair| pair[0] == "inpatient" && pair[1] == "22")
        .count();

    assert!(
        inpatient_22_pairs >= 1,
        "GH#1411 regression: the table cell \"22\" from the \"Inpatient 22 Inpatient Days 2753\" \
         row (test_documents/ground_truth/pdf/pdfa_001.txt lines 75 and 95) is missing from \
         extraction output, so the cell was dropped even if some other \"22\" survived. Suspect \
         mark_cross_page_repeating_text / mark_cross_page_repeating_short_text in \
         crates/xberg/src/pdf/structure/classify.rs, or page_number.rs. Standalone \"22\" tokens \
         present: {}. Extracted content:\n{}",
        tokens.iter().filter(|w| w.as_str() == "22").count(),
        result.content
    );
}
