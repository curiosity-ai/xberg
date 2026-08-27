//! Tests for PDF header parsing with binary prefixes and resilience handling.
//!
//! Verifies that PDFs with binary data before the PDF header can be parsed.
//! Tests lenient mode which searches first 8192 bytes for %PDF- marker.

#[test]
fn test_pdf_header_parsing_basic() {
    use std::path::Path;
    use xberg_native_pdf::document::PdfDocument;

    let fixture_path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("simple.pdf");

    assert!(
        fixture_path.exists(),
        "Test fixture missing: {}",
        fixture_path.display()
    );

    let pdf_path = fixture_path.to_str().unwrap();

    let doc = match PdfDocument::open(pdf_path) {
        Ok(d) => d,
        Err(e) => panic!("Failed to open PDF: {}", e),
    };

    let (major, _minor) = doc.version();
    assert!(major >= 1, "Invalid PDF major version");

    let page_count = doc.page_count().expect("Failed to get page count");
    assert!(page_count > 0, "PDF should have at least one page");

    let _ = doc.extract_spans(0);
}

#[test]
fn test_pdf_header_parsing_multiple_pages() {
    use std::path::Path;
    use xberg_native_pdf::document::PdfDocument;

    let fixture_path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("simple.pdf");

    assert!(
        fixture_path.exists(),
        "Test fixture missing: {}",
        fixture_path.display()
    );

    let pdf_path = fixture_path.to_str().unwrap();

    let doc = PdfDocument::open(pdf_path).expect("Failed to open PDF");

    let (major, _minor) = doc.version();
    assert_eq!(major, 1);

    let page_count = doc.page_count().expect("Failed to get page count");
    assert!(page_count > 0);

    for i in 0..page_count {
        let _ = doc.extract_spans(i);
    }
}

#[test]
fn test_header_beyond_1024_bytes() {
    use std::io::Cursor;
    use xberg_native_pdf::document::parse_header;

    let mut data = vec![b'X'; 2000];
    data.extend_from_slice(b"%PDF-1.4\n");

    let mut cursor = Cursor::new(data);
    let (major, minor, offset) = parse_header(&mut cursor, true).unwrap();
    assert_eq!(major, 1);
    assert_eq!(minor, 4);
    assert_eq!(offset, 2000);
}

#[test]
fn test_header_beyond_8192_bytes_falls_back() {
    use std::io::Cursor;
    use xberg_native_pdf::document::parse_header;

    let mut data = vec![b'X'; 9000];
    data.extend_from_slice(b"%PDF-1.4\n");

    let mut cursor = Cursor::new(data.clone());
    let (major, minor, offset) = parse_header(&mut cursor, true).unwrap();
    assert_eq!((major, minor), (1, 4));
    assert_eq!(offset, 0);

    let mut cursor = Cursor::new(data);
    assert!(parse_header(&mut cursor, false).is_err());
}

#[test]
fn test_header_with_newline_in_version() {
    use std::io::Cursor;
    use xberg_native_pdf::document::parse_header;

    let data = b"%PDF-1.\n";
    let mut cursor = Cursor::new(&data[..]);
    assert!(parse_header(&mut cursor, false).is_err());

    let mut lenient_data = vec![b'X'; 1];
    lenient_data.extend_from_slice(b"%PDF-1.\n");
    let mut cursor = Cursor::new(lenient_data);
    let (major, minor, _offset) = parse_header(&mut cursor, true).unwrap();
    assert_eq!((major, minor), (1, 4));
}

#[test]
fn test_header_with_letter_version() {
    use std::io::Cursor;
    use xberg_native_pdf::document::parse_header;

    let mut data = vec![b'X'; 1];
    data.extend_from_slice(b"%PDF-a.4");
    data.push(b'\n');
    let mut cursor = Cursor::new(data);
    let (major, minor, _offset) = parse_header(&mut cursor, true).unwrap();
    assert_eq!((major, minor), (1, 4));
}

#[test]
fn test_authenticate_empty_password() {
    use std::path::Path;
    use xberg_native_pdf::document::PdfDocument;

    let fixture_path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("simple.pdf");

    assert!(
        fixture_path.exists(),
        "Test fixture missing: {}",
        fixture_path.display()
    );

    let doc = PdfDocument::open(&fixture_path).unwrap();
    let result = doc.authenticate(b"").unwrap();
    assert!(result, "Non-encrypted PDF should always authenticate");
}
