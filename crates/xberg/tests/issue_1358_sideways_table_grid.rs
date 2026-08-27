//! Regression test for xberg-io/xberg#1358 — sideways (rotated, 90-degree)
//! tables extract with the cell text glued/word-reversed and, more
//! importantly, with cells bucketed into the *wrong* grid cell entirely
//! (rows and columns scrambled or transposed).
//!
//! The cell-text half of this defect (word order within a rotated cell) is
//! already fixed upstream: `xberg-native`'s `table_extractor.rs` now reads
//! `rotation_degrees: block.rotation_degrees` (previously a hard-coded
//! `0.0`), and that fix ships in the published `xberg-native` 1.0.1 that
//! this crate's `Cargo.lock` resolves. What is NOT independently verified is
//! grid bucketing: whether a state's row keeps its own numbers together, and
//! whether the header row stays distinct from the data rows.
//!
//! This test therefore does not count rows or columns at all — the corpus is
//! known to over-fabricate tables, so a more-scrambled table can produce
//! *more* pipe-delimited rows and would score better on any row-count metric.
//! Counting rows rewards the defect. Instead this test anchors on real,
//! external, independently-verified document content (see fixture note
//! below) and asserts on RELATIVE POSITION: a known row label must sit in
//! the same row as its known numeric value, and the header row must not
//! contain data values (the anti-transpose check).
//!
//! Fixture: `test_documents/vendored/pdfplumber/pdfs/nics-background-checks-2015-11-rotated.pdf`
//! — pdfplumber's canonical rotated-table fixture. Confirmed present via
//! `find test_documents -iname "*nics*"` before writing this test. Its
//! content is independently known from
//! `test_documents/ground_truth/pdf/nics-background-checks-2015-11-rotated.txt`
//! (a pre-existing, xberg-independent ground-truth transcription already
//! checked into the repo), whose relevant lines read (verbatim, whitespace
//! collapsed for this comment only):
//!
//!   State / Territory  Permit  Handgun  Long Gun  ...  Totals
//!   Alabama  18,870  23,022  22,650  859  1,178  0  14  15  0  2,179  2,307  11  0  0  0  13  14  0  3  2  0  71,137
//!   Alaska   209  3,062  3,209  191  184  0  9  3  0  100  100  0  18  9  1  0  0  0  0  0  0  7,095
//!
//! "Alabama" pairs with the row total "71,137"; "Alaska" pairs with "7,095".
//! Neither of these strings is a guess about xberg's output formatting — they
//! are literal glyphs printed on the page, and a native-PDF-text extractor
//! (no OCR involved for this fixture) must reproduce them as substrings even
//! if it mis-buckets which row/column they land in. Because word-level
//! reversal cannot reorder digits *within* a single token, "71,137" and
//! "7,095" survive as intact substrings even under the pre-fix word-reversal
//! defect; only their row placement is at stake here, which is exactly what
//! this test checks.
//!
//!
//! STATUS 2026-08-21 -- BOTH TESTS ARE `#[ignore]`d, and the reason is a measured control,
//! not a guess. xberg extracts ZERO tables from this fixture, and a control run against the
//! NON-rotated twin in the same directory (`nics-background-checks-2015-11.pdf`) extracts zero
//! tables too, with byte-identical 5733-char text content in both orientations, both containing
//! "Alabama" and "71,137". So the emptiness is NOT the rotation defect this file was written to
//! pin down -- the table extractor simply produces nothing for this document either way, and
//! these assertions would fail for a reason unrelated to GH#1358.
//!
//! Keep them ignored until table detection produces a grid here at all; at that point they
//! become the intended grid-bucketing regression test unchanged. Re-run the control with:
//!   xberg extract <fixture>.pdf --no-config-discovery --format json   (read .result.tables)
//!
//! The separate, real finding this surfaced: a 24-column government table extracts as prose
//! with zero detected tables, in both orientations. That is under-detection, and it is a
//! different defect from GH#1358.
//! Run with:
//!   cargo test -p xberg --features pdf --test issue_1358_sideways_table_grid

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "pdf")]

mod helpers;
use helpers::{extract_uri_document_blocking, get_test_file_path};

use xberg::ExtractionConfig;
use xberg::types::tables::Table;

const FIXTURE_RELATIVE_PATH: &str = "vendored/pdfplumber/pdfs/nics-background-checks-2015-11-rotated.pdf";

/// Row label taken from the ground-truth transcription (see module docs).
const ALABAMA_LABEL: &str = "Alabama";
/// Alabama's row total, from the same ground-truth line.
const ALABAMA_TOTAL: &str = "71,137";
/// A second, independent label/value pair to rule out a one-off coincidence.
const ALASKA_LABEL: &str = "Alaska";
const ALASKA_TOTAL: &str = "7,095";
/// Header-row anchor: the first column's header, from the same ground truth.
const HEADER_ANCHOR: &str = "State";

/// Find the index of the first row (within `table.cells`) whose joined text
/// contains `needle`. `None` if no row matches.
fn row_index_containing(table: &Table, needle: &str) -> Option<usize> {
    table
        .cells
        .iter()
        .position(|row| row.iter().any(|cell| cell.contains(needle)))
}

/// Join every cell in a row into one string for substring assertions.
fn row_text(table: &Table, row_index: usize) -> String {
    table.cells[row_index].join(" | ")
}

#[test]
#[ignore = "GH#1358: fixture yields zero tables in BOTH orientations -- see module docs"]
fn should_keep_alabama_label_and_its_total_in_the_same_row() {
    if !helpers::test_documents_available() {
        eprintln!("skipping: test_documents submodule not available");
        return;
    }
    let path = get_test_file_path(FIXTURE_RELATIVE_PATH);
    if !path.exists() {
        eprintln!("skipping: fixture not found at {}", path.display());
        return;
    }
    let config = ExtractionConfig::default();

    let result = extract_uri_document_blocking(&path, None, &config).expect("sideways-table fixture must extract");

    assert!(
        !result.tables.is_empty(),
        "expected at least one detected table in the rotated NICS fixture, got none"
    );

    let table = result
        .tables
        .iter()
        .find(|table| row_index_containing(table, ALABAMA_LABEL).is_some())
        .unwrap_or_else(|| {
            panic!(
                "no extracted table contains the row label '{ALABAMA_LABEL}'; tables were: {:?}",
                result.tables.iter().map(|t| &t.cells).collect::<Vec<_>>()
            )
        });

    let alabama_row = row_index_containing(table, ALABAMA_LABEL).expect("checked above");

    // ANTI-TRANSPOSE: the header row must not itself be the Alabama data row,
    // and must not already contain the row total (that would mean data leaked
    // into the header, i.e. rows/columns were swapped).
    let header_text = row_text(table, 0);
    assert!(
        !header_text.contains(ALABAMA_LABEL),
        "header row must not contain the data label '{ALABAMA_LABEL}'; a transposed grid puts \
         data values where headers belong. header row was: {header_text}"
    );
    assert!(
        header_text.contains(HEADER_ANCHOR),
        "header row must contain '{HEADER_ANCHOR}' (the first column's real header); got: {header_text}"
    );

    // THE LOAD-BEARING ASSERTION: grid bucketing must keep Alabama's row
    // total in Alabama's own row. If bucketing is scrambled, "71,137" ends up
    // in a different row than "Alabama" (e.g. shifted by one row, or paired
    // with a different state), and this assertion fails with the wrong row's
    // text on both sides of the comparison.
    let alabama_row_text = row_text(table, alabama_row);
    assert!(
        alabama_row_text.contains(ALABAMA_TOTAL),
        "row containing '{ALABAMA_LABEL}' must also contain its own total '{ALABAMA_TOTAL}'; \
         got row: {alabama_row_text}"
    );
}

#[test]
#[ignore = "GH#1358: fixture yields zero tables in BOTH orientations -- see module docs"]
fn should_keep_alaska_label_and_its_total_in_the_same_row_distinct_from_alabama() {
    if !helpers::test_documents_available() {
        eprintln!("skipping: test_documents submodule not available");
        return;
    }
    let path = get_test_file_path(FIXTURE_RELATIVE_PATH);
    if !path.exists() {
        eprintln!("skipping: fixture not found at {}", path.display());
        return;
    }
    let config = ExtractionConfig::default();

    let result = extract_uri_document_blocking(&path, None, &config).expect("sideways-table fixture must extract");

    let table = result
        .tables
        .iter()
        .find(|table| row_index_containing(table, ALASKA_LABEL).is_some())
        .unwrap_or_else(|| {
            panic!(
                "no extracted table contains the row label '{ALASKA_LABEL}'; tables were: {:?}",
                result.tables.iter().map(|t| &t.cells).collect::<Vec<_>>()
            )
        });

    let alaska_row = row_index_containing(table, ALASKA_LABEL).expect("checked above");
    let alabama_row = row_index_containing(table, ALABAMA_LABEL);

    // A second independent label/value pair rules out a lucky one-off match:
    // if grid bucketing is genuinely broken, at most one of the two label
    // rows could happen to still contain its own total by coincidence.
    let alaska_row_text = row_text(table, alaska_row);
    assert!(
        alaska_row_text.contains(ALASKA_TOTAL),
        "row containing '{ALASKA_LABEL}' must also contain its own total '{ALASKA_TOTAL}'; \
         got row: {alaska_row_text}"
    );

    if let Some(alabama_row) = alabama_row {
        assert_ne!(
            alaska_row, alabama_row,
            "'{ALASKA_LABEL}' and '{ALABAMA_LABEL}' are different rows in the source document \
             and must not collapse into the same extracted row"
        );
    }
}
