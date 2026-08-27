//! `extract_words` must not fuse a relation sign with a backtracking
//! denominator, nor let the resulting word-topology change fabricate tables.
//!
//! A prior fix taught `extract_text` (via `assemble_text_from_spans`) to
//! break the line between a relation sign and a backtracking fraction
//! denominator (`dx1/dt = …` no longer extracts as `dx1 =dt`). That fix was
//! deliberately scoped to the composed-text path only: `extract_words` has
//! its own, separate post-clustering merge step (in `extract_words_inner`)
//! that joins adjacent words abutting or overlapping on the same line — and
//! it had the identical bug. Its `gap` check had no lower bound, so a word
//! that backtracks far behind the previous word's ORIGIN (not just its end)
//! also satisfies "gap ≤ a small positive number", and because the merge is
//! incremental (the merged word's bbox keeps growing), a chain of such
//! backtracks can collapse an entire displayed equation — and, in the worst
//! observed real-document case, the start of the following sentence — into
//! one word.
//!
//! Fixing that alone is not safe on its own: `extract_tables` (both the
//! internal path `extract_text`/`to_markdown`/`to_html` use, and the public
//! `extract_tables` API) feeds `extract_words` output into the spatial table
//! detector as its word geometry. Changing word topology changes what the
//! detector sees, and its punctuation-based prose-rejection guard
//! (`looks_like_prose_paragraph`) let a fabricated or garbled table through
//! whenever the newly-separated prose had no sentence terminator inside it
//! (a caption, a mid-clause fragment) or held vertically-stacked
//! single-character lines (a misread rotated axis label). Both gaps are
//! closed here with punctuation-independent, shape-based signals; the public
//! `extract_tables` API additionally got the same real-grid/prose filter the
//! internal path already had, since it had none at all.
//!
//! A third, related shape found during corpus validation: the same
//! unbounded-`gap` merge also fires across an ordinary line wrap when the
//! producer emits two consecutive lines at nearly the same y (some PDF
//! generators have sub-1pt baseline drift between lines), since the
//! backtrack guard's `y_diff > 1.0` check doesn't catch it. Guarded
//! separately by rejecting any merge whose `delta_x` backs up more than 5
//! font-sizes regardless of `y_diff` — no genuine same-line construct
//! backtracks that far.
//!
//! Verified empirically against real documents (not committed — this repo's
//! fixture policy keeps third-party PDFs out of the tree; fetch instructions
//! are in each opt-in test below): a displayed-math-heavy preprint page, a
//! small PDF-library regression file, and a biomedical journal article. All
//! three fabricated or garbled a table when the word-layer fix landed alone;
//! none does with the detector hardening also in place.

mod common;
use common::build_minimal_pdf_raw;

use xberg_native_pdf::document::PdfDocument;

/// Same geometry as the composed-text fixture: numerator `dx1` above a
/// vinculum, relation `=` at mid-height, denominator `dt` drawn AFTER it,
/// starting behind the `=` origin at a lower baseline. On `main` this fuses
/// into one `extract_words` token containing `"=dt"`.
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
fn relation_sign_stays_separate_in_extract_words() {
    let doc = PdfDocument::from_bytes(display_fraction_pdf()).expect("parse");
    let words = doc.extract_words(0).expect("words");
    let fused: Vec<&str> = words
        .iter()
        .map(|w| w.text.as_str())
        .filter(|t| t.contains("=dt"))
        .collect();
    assert!(
        fused.is_empty(),
        "extract_words must not fuse the relation sign with the backtracking \
         denominator into one token, got fused tokens: {fused:?} (all words: {:?})",
        words.iter().map(|w| &w.text).collect::<Vec<_>>()
    );
}

/// Genuine same-line, tightly-kerned neighbours (no baseline backtrack) must
/// still merge — this is the ordinary-adjacent-glyph-run feature the merge
/// step exists for (tagged CJK documents split typographically-adjacent
/// glyphs across marked-content runs). The backtrack guard must not
/// over-trigger on this shape: same baseline (`y_diff` ≈ 0), small negative
/// gap from a genuine kerning overlap, not a multi-em backtrack.
#[test]
fn tight_kerning_neighbours_still_merge() {
    let mut content = Vec::new();
    content.extend_from_slice(b"BT\n");
    content.extend_from_slice(b"/F1 12 Tf 1 0 0 1 100.00 700.00 Tm (Q) Tj\n");
    // 0.18pt overlap: ordinary tight kerning, not a math backtrack. ~keep
    content.extend_from_slice(b"/F1 12 Tf 1 0 0 1 109.82 700.00 Tm (mark) Tj\n");
    content.extend_from_slice(b"ET");
    let pdf = build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
    let doc = PdfDocument::from_bytes(pdf).expect("parse");
    let words = doc.extract_words(0).expect("words");
    let joined: String = words.iter().map(|w| w.text.as_str()).collect::<Vec<_>>().join("|");
    assert!(
        joined.contains("Qmark"),
        "ordinary tightly-kerned same-line neighbours must still merge, got words: {joined:?}"
    );
}

/// A unit price backtracking into a quantity column (`$0.14` then `50,170`
/// drawn starting behind the price's origin) is the same backtrack geometry
/// as the math case, just with digits instead of a relation sign. `main`
/// fuses this into one word/cell (`$0.1450,170`); after the fix the two
/// values are separate. This is an intentional behaviour change — a whole-
/// document diff against `main` will show it, and that is correct, not a
/// regression to revert.
#[test]
fn backtracking_price_and_quantity_split() {
    let mut content = Vec::new();
    content.extend_from_slice(b"BT\n");
    content.extend_from_slice(b"/F1 10 Tf 1 0 0 1 200.00 500.00 Tm ($0.14) Tj\n");
    content.extend_from_slice(b"/F1 10 Tf 1 0 0 1 160.00 494.00 Tm (50,170) Tj\n");
    content.extend_from_slice(b"ET");
    let pdf = build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
    let doc = PdfDocument::from_bytes(pdf).expect("parse");
    let words = doc.extract_words(0).expect("words");
    let fused = words.iter().any(|w| w.text.contains("0.1450"));
    assert!(
        !fused,
        "backtracking price/quantity pair must split into separate words, got: {:?}",
        words.iter().map(|w| &w.text).collect::<Vec<_>>()
    );
}

/// A line wrap whose two lines happen to sit at nearly the same y (some
/// producers emit sub-1pt baseline drift between consecutive lines — the
/// `y_diff > 1.0` half of the math-backtrack guard doesn't catch this) must
/// still not fuse the wrapped line's tail onto the next line's head. The
/// line's end (far right) and the next line's start (far left, ~35 em back)
/// is an order of magnitude beyond any genuine same-line construct (ordinary
/// kerning is near 0; a fraction backtrack is ~1-2 em) and can only be two
/// different lines. Reproduces a real `main` regression: "of whom" (end of
/// one line) fusing onto "tered with books" (start of the next) into
/// "whomteredwithbooks".
#[test]
fn line_wrap_with_near_zero_y_delta_does_not_fuse() {
    let mut content = Vec::new();
    content.extend_from_slice(b"BT\n");
    content.extend_from_slice(b"/F1 10 Tf 1 0 0 1 361.08 600.76 Tm (of whom) Tj\n");
    content.extend_from_slice(b"/F1 10 Tf 1 0 0 1 36.48 600.08 Tm (tered with books) Tj\n");
    content.extend_from_slice(b"ET");
    let pdf = build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
    let doc = PdfDocument::from_bytes(pdf).expect("parse");
    let words = doc.extract_words(0).expect("words");
    let fused = words.iter().any(|w| w.text.contains("whomtered"));
    assert!(
        !fused,
        "a wrapped line must not fuse onto the next line's start even when \
         y_diff is under 1pt, got: {:?}",
        words.iter().map(|w| &w.text).collect::<Vec<_>>()
    );
}
