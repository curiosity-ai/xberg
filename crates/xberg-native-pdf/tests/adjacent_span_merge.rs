//! Regression coverage for the adjacent-span merge in `extract_words`
//! (`PdfDocument::extract_words` post-processing, see `src/document.rs`):
//! two consecutive text-showing operations whose bboxes abut on the same
//! line (`gap <= 0.15 * font_size`) must be merged into a single word, not
//! left as two.
//!
//! This replaces a test that previously built its fixture through
//! `crate::ffi::pdf_document_builder_*`, the C FFI builder API. That module
//! (`src/ffi.rs`) has been removed from this fork, so the fixture here is
//! hand-assembled PDF bytes instead — a minimal synthetic document with an
//! explicit content stream and glyph widths, giving exact control over the
//! horizontal gap between the two `Tj` operations under test.

use xberg_native_pdf::document::PdfDocument;

/// `A` and `B` shown via two separate `Tj` operations on the same line,
/// separated by `gap` points measured from the end of `A`'s declared advance
/// to the start of `B`. `A` and `B` are each given a 600/1000 em width, i.e.
/// 7.2 pt at the 12 pt font size used here, so the caller can position `B`
/// precisely.
fn adjacent_glyphs_pdf(gap: f32) -> Vec<u8> {
    const FONT_SIZE: f32 = 12.0;
    const GLYPH_WIDTH_PT: f32 = 7.2;
    let x_a = 100.0f32;
    let x_b = x_a + GLYPH_WIDTH_PT + gap;

    let mut content = Vec::new();
    content.extend_from_slice(format!("BT /F1 {FONT_SIZE} Tf\n").as_bytes());
    content.extend_from_slice(format!("1 0 0 1 {x_a} 500 Tm (A) Tj\n").as_bytes());
    content.extend_from_slice(format!("1 0 0 1 {x_b} 500 Tm (B) Tj\n").as_bytes());
    content.extend_from_slice(b"ET");

    build_minimal_pdf(&content)
}

/// Hand-assemble a one-page PDF with a single Type1 Helvetica font whose
/// glyphs `A` (65) and `B` (66) are both declared at 600/1000 em, so the
/// caller can compute exact glyph positions without depending on any
/// built-in AFM metrics table.
fn build_minimal_pdf(content: &[u8]) -> Vec<u8> {
    let mut pdf = b"%PDF-1.4\n".to_vec();

    let off1 = pdf.len();
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    let off2 = pdf.len();
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    let off3 = pdf.len();
    pdf.extend_from_slice(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] \
          /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
    );

    let off4 = pdf.len();
    pdf.extend_from_slice(format!("4 0 obj\n<< /Length {} >>\nstream\n", content.len()).as_bytes());
    pdf.extend_from_slice(content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    let off5 = pdf.len();
    pdf.extend_from_slice(
        b"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica \
          /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 66 /Widths [600 600] >>\nendobj\n",
    );

    let xref_pos = pdf.len();
    let offsets = [0usize, off1, off2, off3, off4, off5];
    pdf.extend_from_slice(format!("xref\n0 {}\n", offsets.len()).as_bytes());
    pdf.extend_from_slice(format!("{:010} 65535 f\r\n", 0).as_bytes());
    for &off in &offsets[1..] {
        pdf.extend_from_slice(format!("{off:010} 00000 n\r\n").as_bytes());
    }
    pdf.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
            offsets.len(),
            xref_pos
        )
        .as_bytes(),
    );
    pdf
}

/// Zero-gap consecutive `Tj` operations on the same line must be merged into
/// one word by `extract_words`'s adjacent-span merge pass, not left as two
/// separate single-letter words.
#[test]
fn adjacent_span_merge_no_gap_yields_single_word() {
    let doc = PdfDocument::from_bytes(adjacent_glyphs_pdf(0.0)).expect("open fixture");
    let words = doc.extract_words(0).expect("extract_words");
    let texts: Vec<&str> = words.iter().map(|w| w.text.as_str()).collect();

    assert!(
        words.iter().any(|w| w.text == "AB"),
        "expected a single merged word \"AB\", got: {texts:?}"
    );
    assert!(
        !texts.contains(&"A") && !texts.contains(&"B"),
        "the two glyphs were not merged, still separate words: {texts:?}"
    );
}

/// Sanity check that the fixture and assertions genuinely exercise the merge
/// path: with a large horizontal gap between the same two `Tj` operations
/// (well above the `0.15 * font_size` merge threshold), the letters must
/// stay separate words. If this test failed to fail, the assertions above
/// would be vacuous.
#[test]
fn wide_gap_between_glyphs_stays_two_words() {
    // font_size 12 -> merge threshold is 1.8pt; 5pt is well beyond it. ~keep
    let doc = PdfDocument::from_bytes(adjacent_glyphs_pdf(5.0)).expect("open fixture");
    let words = doc.extract_words(0).expect("extract_words");
    let texts: Vec<&str> = words.iter().map(|w| w.text.as_str()).collect();

    assert!(
        !words.iter().any(|w| w.text == "AB"),
        "glyphs merged despite a gap well beyond the threshold: {texts:?}"
    );
    assert!(
        texts.contains(&"A") && texts.contains(&"B"),
        "expected two separate words \"A\" and \"B\", got: {texts:?}"
    );
}
