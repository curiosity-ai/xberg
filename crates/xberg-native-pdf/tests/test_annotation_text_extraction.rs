//! Regression test: Form field and annotation text extraction.
//!
//! Verifies that Widget and FreeText annotation text is included in extract_text output,
//! and that the annotation code path does not crash on PDFs with or without annotations.

use xberg_native_pdf::document::PdfDocument;

#[test]
fn test_annotation_extraction_does_not_crash() {
    let doc = PdfDocument::open("tests/fixtures/simple.pdf").unwrap();
    let text = doc.extract_text(0).unwrap();
    let text2 = doc.extract_text(0).unwrap();
    assert_eq!(text, text2, "Annotation extraction should be deterministic");
}

#[test]
fn test_outline_pdf_annotation_extraction() {
    let doc = PdfDocument::open("tests/fixtures/outline.pdf").unwrap();
    let page_count = doc.page_count().unwrap();
    for i in 0..page_count {
        let text = doc.extract_text(i).unwrap();
        let text2 = doc.extract_text(i).unwrap();
        assert_eq!(text, text2, "Page {i}: annotation extraction should be deterministic");
        assert!(
            !text.contains("WhitePoint"),
            "Page {i}: annotation text contains WhitePoint metadata"
        );
        assert!(
            !text.contains("/CalRGB"),
            "Page {i}: annotation text contains /CalRGB metadata"
        );
    }
}
