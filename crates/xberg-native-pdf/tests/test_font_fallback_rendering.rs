//! Regression tests for missing-system-font fallback.
//!
//! Text rendered as garbled symbols when system fonts are missing.
//! The font parsing failure was silent, and the fallback font list
//! lacked common Linux alternatives.
//!
//! Two save-roundtrip tests (`test_text_preserved_after_save_roundtrip`,
//! `test_font_resources_preserved_after_save`) exercised
//! `DocumentEditor::save_with_options(SaveOptions::full_rewrite())` and were
//! removed along with the editor; the read-only extraction test below is
//! unaffected.

use xberg_native_pdf::document::PdfDocument;

/// Create a minimal PDF with a Type1 font and text content.
fn create_text_pdf() -> Vec<u8> {
    let content: &[u8] = b"BT /F1 12 Tf 72 720 Td (Sample text for testing) Tj ET";
    let cs_header = format!("<</Length {}>>", content.len());

    let mut pdf = Vec::new();
    pdf.extend_from_slice(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n");

    let off1 = pdf.len();
    pdf.extend_from_slice(b"1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n");

    let off2 = pdf.len();
    pdf.extend_from_slice(b"2 0 obj\n<</Type /Pages /Kids [3 0 R] /Count 1>>\nendobj\n");

    let off3 = pdf.len();
    pdf.extend_from_slice(
        b"3 0 obj\n<</Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources <</Font <</F1 5 0 R>>>>>>\nendobj\n",
    );

    let off4 = pdf.len();
    pdf.extend_from_slice(format!("4 0 obj\n{}\nstream\n", cs_header).as_bytes());
    pdf.extend_from_slice(content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    let off5 = pdf.len();
    pdf.extend_from_slice(
        b"5 0 obj\n<</Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding>>\nendobj\n",
    );

    let xref_offset = pdf.len();
    pdf.extend_from_slice(b"xref\n0 6\n");
    pdf.extend_from_slice(b"0000000000 65535 f \n");
    for &off in &[off1, off2, off3, off4, off5] {
        pdf.extend_from_slice(format!("{:010} 00000 n \n", off).as_bytes());
    }

    pdf.extend_from_slice(b"trailer\n<</Size 6 /Root 1 0 R>>\n");
    pdf.extend_from_slice(format!("startxref\n{}\n%%EOF", xref_offset).as_bytes());

    pdf
}

#[test]
fn test_text_extraction_with_standard_font() {
    let pdf_bytes = create_text_pdf();
    let doc = PdfDocument::from_bytes(pdf_bytes).unwrap();
    let text = doc.extract_text(0).unwrap_or_default();
    assert!(
        text.contains("Sample text"),
        "Should extract readable text, got: '{}'",
        text
    );
}
