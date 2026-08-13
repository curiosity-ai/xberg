//! PDF table-detection regression tests.
//!
//! Two properties are asserted against the real extraction pipeline:
//!
//! 1. **No false positives** — documents with no tabular content must produce
//!    `result.tables == []`. A spurious table poisons downstream markdown.
//! 2. **Well-formed positives** — on documents that do contain tables, every
//!    detected table must be structurally sound (non-empty grid, non-empty
//!    markdown, 1-indexed page number) and extraction must not error.
//!
//! Both run in the default `cargo test -p xberg --features full` leg. They need
//! `test_documents/`, which is bucket-fetched and absent from a bare checkout —
//! run `python3 test_documents/scripts/fetch_corpus.py`. Missing fixtures cause a
//! loud, named skip rather than a silent pass.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "pdf")]

mod helpers;
use helpers::extract_uri_document_blocking;

use helpers::*;
use std::path::PathBuf;
use xberg::core::config::ExtractionConfig;

/// Documents with no tabular content. Detecting any table in these is a false positive.
const NON_TABLE_PDFS: &[&str] = &["simple.pdf", "fake_memo.pdf", "searchable.pdf"];

/// Documents that do contain tabular content.
///
/// No per-document table *count* is asserted: counts shift legitimately with
/// pdf_oxide's native grid detector and with the bordered/heuristic tiers (see
/// `pdf_heuristic_tables.rs`, which documents `table_document.pdf` as borderline
/// for the prose filter). What is asserted is that the group as a whole yields at
/// least one table and that every table produced is well formed.
const TABLE_PDFS: &[&str] = &[
    "embedded_images_tables.pdf",
    "multi_page_tables.pdf",
    "table_document.pdf",
    "multi_page.pdf",
    // Listed as a non-table document until this test was first run. It is a fully
    // ruled 5x6 country/capital/currency grid -- 6 horizontal and 7 vertical rules,
    // text landing inside every column -- so detecting it is correct. The sibling
    // suite in pdf_table_ground_truth.rs already asserts no-tables for the other
    // three, and pointedly never did for this one.
    "google_doc_document.pdf",
];

/// Extraction config used by both tests.
///
/// `use_cache: false` keeps the tests independent of any on-disk cache left behind
/// by another test or an earlier run, so they are idempotent and order-independent.
fn table_detection_config() -> ExtractionConfig {
    ExtractionConfig {
        use_cache: false,
        force_ocr: false,
        ..Default::default()
    }
}

/// Resolve a fixture, returning `None` and logging the absolute path when absent.
fn resolve_fixture(filename: &str) -> Option<PathBuf> {
    let path = get_test_file_path(&format!("pdf/{filename}"));
    if !path.exists() {
        eprintln!(
            "missing fixture {} — run `python3 test_documents/scripts/fetch_corpus.py`",
            path.display()
        );
        return None;
    }
    Some(path)
}

/// Report that the corpus is absent. Returns `true` when the test must not proceed.
fn corpus_missing() -> bool {
    if test_documents_available() {
        return false;
    }
    eprintln!(
        "SKIPPING: test corpus not present at {} — run `python3 test_documents/scripts/fetch_corpus.py`",
        get_test_documents_dir().display()
    );
    true
}

/// Truncate a cell for inclusion in a failure message.
fn format_cell(cell: &str) -> String {
    const MAX_LEN: usize = 50;
    if cell.len() > MAX_LEN {
        format!("{}...", &cell[..cell.floor_char_boundary(MAX_LEN)])
    } else {
        cell.to_string()
    }
}

/// Render the first rows of a table for a failure message.
fn preview_table(table: &xberg::types::Table) -> String {
    const PREVIEW_ROWS: usize = 2;
    table
        .cells
        .iter()
        .take(PREVIEW_ROWS)
        .map(|row| {
            let cells: Vec<String> = row.iter().map(|cell| format_cell(cell.as_str())).collect();
            format!("| {} |", cells.join(" | "))
        })
        .collect::<Vec<_>>()
        .join(" / ")
}

/// Documents without tabular content must yield exactly zero tables.
#[test]
fn should_detect_no_tables_in_documents_without_tabular_content() {
    if corpus_missing() {
        return;
    }

    let config = table_detection_config();
    let mut evaluated = 0usize;
    let mut false_positives: Vec<String> = Vec::new();

    for filename in NON_TABLE_PDFS {
        let Some(path) = resolve_fixture(filename) else {
            continue;
        };
        evaluated += 1;

        let result = extract_uri_document_blocking(&path, None, &config)
            .unwrap_or_else(|error| panic!("extraction of {filename} must succeed, got: {error}"));

        if !result.tables.is_empty() {
            let previews: Vec<String> = result
                .tables
                .iter()
                .map(|table| {
                    format!(
                        "{}x{} [{}]",
                        table.cells.len(),
                        table.cells.first().map_or(0, Vec::len),
                        preview_table(table)
                    )
                })
                .collect();
            false_positives.push(format!(
                "{filename}: {} table(s) {}",
                result.tables.len(),
                previews.join("; ")
            ));
        }
    }

    if evaluated == 0 {
        eprintln!(
            "SKIPPING: none of the {} non-table fixtures are present",
            NON_TABLE_PDFS.len()
        );
        return;
    }

    assert_eq!(
        false_positives,
        Vec::<String>::new(),
        "{} of {evaluated} document(s) without tabular content produced false-positive tables",
        false_positives.len()
    );
}

/// Every table detected on a known-table document must be structurally well formed,
/// and the group must yield at least one table.
#[test]
fn should_produce_well_formed_tables_for_documents_with_tabular_content() {
    if corpus_missing() {
        return;
    }

    let config = table_detection_config();
    let mut evaluated = 0usize;
    let mut total_tables = 0usize;
    let mut defects: Vec<String> = Vec::new();

    for filename in TABLE_PDFS {
        let Some(path) = resolve_fixture(filename) else {
            continue;
        };
        evaluated += 1;

        let result = extract_uri_document_blocking(&path, None, &config)
            .unwrap_or_else(|error| panic!("extraction of {filename} must succeed, got: {error}"));
        total_tables += result.tables.len();

        for (index, table) in result.tables.iter().enumerate() {
            let label = format!("{filename} table {}", index + 1);

            if table.cells.is_empty() {
                defects.push(format!("{label}: zero rows"));
                continue;
            }
            if table.cells.iter().all(Vec::is_empty) {
                defects.push(format!("{label}: every row has zero cells"));
            }
            if table.markdown.trim().is_empty() {
                defects.push(format!("{label}: empty markdown for a {}-row grid", table.cells.len()));
            }
            if table.page_number == 0 {
                defects.push(format!("{label}: page_number must be 1-indexed, got 0"));
            }
            if table
                .cells
                .iter()
                .all(|row| row.iter().all(|cell| cell.trim().is_empty()))
            {
                defects.push(format!("{label}: every cell is blank"));
            }
        }
    }

    if evaluated == 0 {
        eprintln!("SKIPPING: none of the {} table fixtures are present", TABLE_PDFS.len());
        return;
    }

    assert_eq!(
        defects,
        Vec::<String>::new(),
        "{} malformed table(s) detected across {evaluated} document(s)",
        defects.len()
    );
    assert!(
        total_tables >= 1,
        "{evaluated} document(s) known to contain tabular content produced 0 tables in total — \
         the native, bordered and heuristic detection tiers all failed"
    );
}
