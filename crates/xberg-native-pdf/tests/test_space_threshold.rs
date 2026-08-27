//! Regression test: Character fragmentation (spurious spaces).
//!
//! Verifies that the space threshold increase from 0.25 to 0.33 reduces
//! spurious space insertion between characters with small positioning gaps.

use xberg_native_pdf::document::PdfDocument;

#[test]
fn test_no_excessive_fragmentation_in_outline_pdf() {
    let doc = PdfDocument::open("tests/fixtures/outline.pdf").unwrap();
    let page_count = doc.page_count().unwrap();

    for i in 0..page_count {
        let text = doc.extract_text(i).unwrap();
        if text.is_empty() {
            continue;
        }

        let spaces = text.chars().filter(|c| *c == ' ').count();
        let non_spaces = text.chars().filter(|c| !c.is_whitespace()).count();

        if non_spaces > 10 {
            let ratio = spaces as f64 / non_spaces as f64;
            assert!(
                ratio < 0.5,
                "Page {}: Space ratio {:.2} too high (spaces={}, non_spaces={})",
                i,
                ratio,
                spaces,
                non_spaces
            );
        }
    }
}
