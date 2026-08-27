//! Regression guards for the liteparse head-to-head report (PDX-1, PDX-4, PDX-5).
//!
//! These pin behaviour that the report found broken and that was subsequently
//! fixed, using the report's own minimal fixture (`multi_column_table.pdf`:
//! a 4-column × 5-row financial-style table whose cells are each emitted with
//! a separate `Tj` operator and zero `TJ` arrays — the layout that historically
//! triggered word-concatenation and table-detection failures).

use xberg_native_pdf::PdfDocument;

const FIXTURE: &str = "tests/fixtures/multi_column_table.pdf";

/// PDX-1 — adjacent words from separate `Tj` operators must not be glued.
/// Historically `extract_text` returned `Year RevenueCost Net Income` and
/// `2021365,817 212,98194,680`; after the strong-geometric threshold fix the
/// word boundaries are recovered.
#[test]
fn pdx1_words_not_concatenated() {
    let doc = PdfDocument::open(FIXTURE).expect("open multi_column_table fixture");
    let text = doc.extract_text(0).expect("extract_text page 0");

    for needle in ["Year Revenue", "Revenue Cost", "2021 365,817"] {
        assert!(
            text.contains(needle),
            "PDX-1 regression: expected separated words {needle:?} in extracted text, got:\n{text}"
        );
    }
    assert!(
        !text.contains("RevenueCost"),
        "PDX-1 regression: words still concatenated (\"RevenueCost\") in:\n{text}"
    );
}

/// PDX-5 — the table detector must find the multi-column financial table, not
/// only single-column TOC/list structures. Historically the strict
/// "every row has row[0]'s column count" predicate rejected this fixture.
#[test]
fn pdx5_multicolumn_table_detected() {
    let doc = PdfDocument::open(FIXTURE).expect("open multi_column_table fixture");
    let pages = doc.page_count().expect("page count");
    let mut tables = Vec::new();
    for page in 0..pages {
        tables.extend(doc.extract_tables(page).expect("extract tables"));
    }

    let multi_column: Vec<_> = tables.iter().filter(|t| t.col_count >= 4).collect();
    assert!(
        !multi_column.is_empty(),
        "PDX-5 regression: no table with >=4 columns detected ({} table(s) total)",
        tables.len()
    );
    let cells: Vec<&str> = multi_column
        .iter()
        .flat_map(|t| t.rows.iter())
        .flat_map(|r| r.cells.iter())
        .map(|c| c.text.as_str())
        .collect();
    assert!(
        cells.iter().any(|c| c.contains("391,035")),
        "PDX-5 regression: data value '391,035' not in the detected table: {cells:?}"
    );
}
