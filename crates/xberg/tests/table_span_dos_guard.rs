//! Regression tests for the `colspan`/`rowspan` denial-of-service guard added to
//! `extraction::grid_flatten::resolve_span_grid` and to the HTML attribute parser in
//! `extraction::html::structure`.
//!
//! An HTML table cell can carry a `colspan`/`rowspan` attribute up to `u32::MAX`
//! (e.g. `colspan="4294967295"`). Before the fix, `resolve_span_grid` trusted that
//! value directly as the width of a `Vec<u32>` occupancy buffer it grows via
//! `Vec::resize`, so a single hostile cell drove a ~17 GB allocation. These tests
//! route the payload through the two real extraction entry points that reach
//! `resolve_span_grid` with attacker-controlled spans: the HTML extractor (via
//! `extraction::html::converter`, which forwards spans parsed by the vendored
//! `html_to_markdown_rs` crate) and the email extractor (via
//! `extraction::html::structure::build_document_structure`, which owns its own
//! attribute parser). Both must extract quickly and produce a bounded table
//! instead of hanging or aborting the process trying to allocate.

mod helpers;

#[cfg(feature = "html")]
mod via_html_extractor {
    use super::helpers::extract_bytes_document_blocking;
    use xberg::core::config::ExtractionConfig;

    fn table_cells(html: &[u8]) -> Vec<Vec<String>> {
        let doc = extract_bytes_document_blocking(html, "text/html", &ExtractionConfig::default())
            .expect("extraction must succeed even with a hostile span attribute");
        doc.tables.first().map(|t| t.cells.clone()).unwrap_or_default()
    }

    /// `<td colspan="4294967295">` must produce a small, bounded grid.
    ///
    /// ~keep: this test is NOT a gate on our clamp, and it must not be described as
    /// one. Measured 2026-08-22 by removing both clamps and re-running: it still
    /// passes, because the vendored `html_to_markdown_rs` clamps `colspan` to 1000
    /// itself (`converter/block/table/cell.rs::clamp_table_span`) on the path this
    /// extractor takes, so the 1001 asserted below is upstream's bound, not ours.
    /// It is kept as an end-to-end pin: if upstream ever drops that clamp, our own
    /// clamp in `resolve_span_grid` is what keeps this assertion true.
    ///
    /// The gates on our clamp are `grid_flatten`'s unit tests and the
    /// `via_email_extractor` case below — that one genuinely hangs without the fix,
    /// because `structure.rs` owns its own attribute parser with no upstream bound.
    #[test]
    fn colspan_bomb_through_real_html_extractor_is_bounded() {
        let html = br#"<table>
<tr><td colspan="4294967295">bomb</td><td>next</td></tr>
</table>"#;
        let cells = table_cells(html);
        assert_eq!(cells.len(), 1, "single row: {cells:?}");
        assert_eq!(
            cells[0].len(),
            1001,
            "clamped to MAX_COL_SPAN (1000) columns for the bomb cell, plus 1 for the trailing cell: {cells:?}"
        );
        assert_eq!(cells[0][0], "bomb");
        assert_eq!(
            cells[0][1000], "next",
            "the trailing cell must land right after the clamped span, not after the raw request: {cells:?}"
        );
    }

    /// Same shape for `rowspan`: a value near `u32::MAX` must not overflow the
    /// `row_idx + row_span` addition inside `resolve_span_grid`, which panics under
    /// `cargo test`'s overflow checks against the unfixed code rather than failing
    /// an assertion. The table must still extract normally, with the rowspan's
    /// column still shown as reserved on the following row.
    #[test]
    fn rowspan_bomb_through_real_html_extractor_does_not_panic() {
        let html = br#"<table>
<tr><td rowspan="4294967295">bomb</td><td>right</td></tr>
<tr><td>below</td></tr>
</table>"#;
        let cells = table_cells(html);
        assert_eq!(cells.len(), 2, "two rows: {cells:?}");
        assert_eq!(cells[0], vec!["bomb".to_string(), "right".to_string()]);
        assert_eq!(
            cells[1],
            vec!["".to_string(), "below".to_string()],
            "column 0 is still reserved by the clamped rowspan on row 1: {cells:?}"
        );
    }

    /// Positive control: an ordinary `colspan="2"` header must flatten to exactly
    /// the same grid it always has. A clamp set too low (or applied to the wrong
    /// value) would silently mangle this while every negative test above still
    /// passes, so this is the more important half of the coverage.
    #[test]
    fn ordinary_colspan_two_is_unaffected() {
        let html = br#"<table>
<tr><th colspan="2">Fuse</th><th>Circuit</th></tr>
<tr><td>101</td><td>40A</td><td>Blower</td></tr>
</table>"#;
        let cells = table_cells(html);
        assert_eq!(
            cells,
            vec![
                vec!["Fuse".to_string(), "".to_string(), "Circuit".to_string()],
                vec!["101".to_string(), "40A".to_string(), "Blower".to_string()],
            ]
        );
    }

    /// Positive control at three columns, so the fix is proven not to be a
    /// coincidence of the `colspan="2"` case above.
    #[test]
    fn ordinary_colspan_three_is_unaffected() {
        let html = br#"<table>
<tr><th colspan="3">Header</th></tr>
<tr><td>a</td><td>b</td><td>c</td></tr>
</table>"#;
        let cells = table_cells(html);
        assert_eq!(
            cells,
            vec![
                vec!["Header".to_string(), "".to_string(), "".to_string()],
                vec!["a".to_string(), "b".to_string(), "c".to_string()],
            ]
        );
    }
}

/// Same payload, routed through the email extractor's own HTML walker
/// (`extraction::html::structure::build_document_structure`), which parses
/// `colspan`/`rowspan` itself rather than forwarding values from
/// `html_to_markdown_rs`. This is the trigger path named in the report: an HTML
/// email part with a hostile span attribute.
#[cfg(feature = "email")]
mod via_email_extractor {
    use super::helpers::extract_bytes_document_blocking;
    use xberg::core::config::ExtractionConfig;

    fn html_email_table_cells(html_body: &str) -> Vec<Vec<String>> {
        let eml = format!(
            "From: sender@example.com\r\n\
To: recipient@example.com\r\n\
Subject: Table DoS probe\r\n\
Content-Type: text/html; charset=utf-8\r\n\
\r\n\
{html_body}"
        );
        let doc = extract_bytes_document_blocking(eml.as_bytes(), "message/rfc822", &ExtractionConfig::default())
            .expect("extraction must succeed even with a hostile span attribute in an HTML email part");
        doc.tables.first().map(|t| t.cells.clone()).unwrap_or_default()
    }

    /// A `colspan="4294967295"` inside an HTML email part must not attempt the ~17
    /// GB resize either. This exercises `extraction::html::structure`'s own attribute
    /// parser (clamped at parse time) independently of the HTML-extractor path above,
    /// which only exercises `extraction::grid_flatten`'s clamp.
    #[test]
    fn colspan_bomb_in_html_email_part_is_bounded() {
        let cells =
            html_email_table_cells(r#"<table><tr><td colspan="4294967295">bomb</td><td>next</td></tr></table>"#);
        assert_eq!(cells.len(), 1, "single row: {cells:?}");
        assert_eq!(
            cells[0].len(),
            1001,
            "clamped to MAX_COL_SPAN (1000) columns for the bomb cell, plus 1 for the trailing cell: {cells:?}"
        );
        assert_eq!(cells[0][0], "bomb");
        assert_eq!(cells[0][1000], "next");
    }

    /// Positive control on the email path: an ordinary two-column merge must keep
    /// producing exactly the same grid it always has.
    #[test]
    fn ordinary_colspan_two_in_html_email_part_is_unaffected() {
        let cells = html_email_table_cells(r#"<table><tr><th colspan="2">Fuse</th><th>Circuit</th></tr></table>"#);
        assert_eq!(
            cells,
            vec![vec!["Fuse".to_string(), "".to_string(), "Circuit".to_string()]]
        );
    }
}
