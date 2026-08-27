//! Integration tests for the character-level `extract_chars` API.
//!
//! Uses minimal synthetic PDFs built in code, per `fixture-hygiene`.

mod common;

use xberg_native_pdf::document::PdfDocument;

/// `extract_chars` on a page with a single `Tj` string must return one
/// `TextChar` per character, in order, each carrying a finite, non-negative
/// bounding box.
#[test]
fn test_extract_chars_character_properties() {
    let content = b"BT /F1 12 Tf 50 700 Td (Hi) Tj ET";
    let pdf = common::build_minimal_pdf_raw(content, b"/MediaBox [0 0 612 792]");
    let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

    let chars = doc.extract_chars(0).expect("extract_chars");

    assert_eq!(chars.len(), 2, "expected one TextChar per input character");
    assert_eq!(chars[0].char, 'H');
    assert_eq!(chars[1].char, 'i');

    for c in &chars {
        assert!(c.bbox.x.is_finite(), "bbox x should be finite");
        assert!(c.bbox.y.is_finite(), "bbox y should be finite");
        assert!(c.bbox.width >= 0.0, "bbox width should be non-negative");
        assert!(c.bbox.height >= 0.0, "bbox height should be non-negative");
    }

    // 'H' is wider than 'i' in Helvetica, so its box must be wider. ~keep
    assert!(
        chars[0].bbox.width > chars[1].bbox.width,
        "'H' should be wider than 'i': got H={}, i={}",
        chars[0].bbox.width,
        chars[1].bbox.width
    );
}

/// `TextChar::bbox` is a `Rect` with `x`, `y`, `width`, `height` fields, and
/// characters advance left to right: each subsequent character's box starts
/// at or after the previous one's.
#[test]
fn test_extract_chars_bbox_format() {
    let content = b"BT /F1 12 Tf 50 700 Td (AB) Tj ET";
    let pdf = common::build_minimal_pdf_raw(content, b"/MediaBox [0 0 612 792]");
    let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

    let chars = doc.extract_chars(0).expect("extract_chars");
    assert_eq!(chars.len(), 2);

    let first = &chars[0].bbox;
    let second = &chars[1].bbox;
    assert!(
        second.x >= first.x,
        "second character must not start before the first: first.x={}, second.x={}",
        first.x,
        second.x
    );
}
