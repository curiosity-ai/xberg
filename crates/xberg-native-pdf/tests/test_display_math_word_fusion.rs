//! Displayed fractions must not fuse the relation sign with the denominator.
//!
//! `dx1/dt = …` extracts as `dx1 =dt` on affected pages: the `=` sits at the
//! fraction's mid-height and the denominator `dt` is drawn AFTER it, starting
//! ~24pt behind the `=` origin at a ~4pt baseline offset. The extract_text
//! line emitter sees a same-line pair whose next span backtracks with a real
//! baseline drop, and — absent a dedicated branch — concatenates them into
//! `=dt`. The fix adds that branch (a backtracking span with `y_diff > 1`,
//! `delta_x ≤ 0.5`, `gap < -1em`, gated OFF for right-to-left runs, whose
//! leftward flow is not backtracking) so the line breaks instead.
//!
//! This covers the composed-text path (extract_text / to_markdown / to_html).
//! The lower-level `extract_words` de-fusion is intentionally NOT part of this
//! change: `extract_page_tables` feeds `extract_words` output into the spatial
//! table detector, so altering word geometry there perturbs table detection —
//! that work (word-level de-fusion plus the detector hardening it requires) is
//! tracked separately so this fix stays table-neutral.

mod common;
use common::build_minimal_pdf_raw;

use xberg_native_pdf::document::PdfDocument;

/// One displayed fraction in a real page's geometry: numerator `dx` + subscript
/// `1` above the bar, relation `=` to the right at mid-height, denominator `dt`
/// below the bar and to the LEFT of `=`. `dt`'s baseline sits ~8pt below the
/// `=` baseline (still one visual line) and there is no right-hand side, so the
/// reading order presents `=` immediately before `dt` — the exact ordering that
/// reaches the emitter's backtrack branch. On `main` this fixture emits `=dt`.
fn display_fraction_pdf() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0.4 w 131.8 719.5 m 145.0 719.5 l S\n");
    content.extend_from_slice(b"BT\n");
    content.extend_from_slice(b"/F1 8 Tf 1 0 0 1 131.81 721.00 Tm (dx) Tj\n");
    content.extend_from_slice(b"/F1 6 Tf 1 0 0 1 141.28 721.00 Tm (1) Tj\n");
    content.extend_from_slice(b"/F1 12 Tf 1 0 0 1 149.94 718.04 Tm (=) Tj\n");
    content.extend_from_slice(b"/F1 8 Tf 1 0 0 1 134.74 710.00 Tm (dt) Tj\n");
    content.extend_from_slice(b"ET");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn relation_sign_stays_separate_in_extract_text() {
    let doc = PdfDocument::from_bytes(display_fraction_pdf()).expect("parse");
    let text = doc.extract_text(0).expect("text");
    assert!(
        !text.contains("=dt"),
        "extract_text must not fuse the relation sign with the backtracking denominator, got: {text:?}"
    );
    assert!(text.contains('='), "the relation sign must survive, got: {text:?}");
    assert!(text.contains("dt"), "the denominator must survive, got: {text:?}");
}
