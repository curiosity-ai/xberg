//! Integration tests for Phase 1: PDF Parsing Foundation
//!
//! Tests the complete PDF parsing pipeline with a real PDF file.

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::object::ObjectRef;

const SIMPLE_PDF_PATH: &str = "tests/fixtures/simple.pdf";

#[test]
fn test_open_simple_pdf() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");
    let _ = format!("{:?}", pdf);
}

#[test]
fn test_pdf_version() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");
    let (major, minor) = pdf.version();
    assert_eq!(major, 1, "Major version should be 1");
    assert_eq!(minor, 4, "Minor version should be 4");
}

#[test]
fn test_page_count() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");
    let count = pdf.page_count().expect("Failed to get page count");
    assert_eq!(count, 1, "simple.pdf should have 1 page");
}

#[test]
fn test_load_catalog() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");
    let catalog = pdf.catalog().expect("Failed to load catalog");

    let dict = catalog.as_dict().expect("Catalog should be a dictionary");

    let type_obj = dict.get("Type").expect("Catalog should have /Type");
    assert_eq!(type_obj.as_name(), Some("Catalog"), "/Type should be /Catalog");

    let pages_ref = dict.get("Pages").expect("Catalog should have /Pages");
    assert!(pages_ref.as_reference().is_some(), "/Pages should be a reference");
}

#[test]
fn test_load_object_by_reference() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");

    let obj1 = pdf.load_object(ObjectRef::new(1, 0)).expect("Failed to load object 1");
    assert!(obj1.as_dict().is_some(), "Object 1 should be a dictionary");

    let obj2 = pdf.load_object(ObjectRef::new(2, 0)).expect("Failed to load object 2");
    let dict2 = obj2.as_dict().expect("Object 2 should be a dictionary");

    assert_eq!(
        dict2.get("Type").and_then(|o| o.as_name()),
        Some("Pages"),
        "Object 2 /Type should be /Pages"
    );

    assert_eq!(
        dict2.get("Count").and_then(|o| o.as_integer()),
        Some(1),
        "Object 2 /Count should be 1"
    );

    let obj3 = pdf.load_object(ObjectRef::new(3, 0)).expect("Failed to load object 3");
    let dict3 = obj3.as_dict().expect("Object 3 should be a dictionary");

    assert_eq!(
        dict3.get("Type").and_then(|o| o.as_name()),
        Some("Page"),
        "Object 3 /Type should be /Page"
    );

    assert!(dict3.contains_key("MediaBox"), "Page should have /MediaBox");
}

#[test]
fn test_object_caching() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");

    let obj1_first = pdf.load_object(ObjectRef::new(1, 0)).expect("Failed to load object 1");
    let obj1_second = pdf
        .load_object(ObjectRef::new(1, 0))
        .expect("Failed to load object 1 (cached)");

    assert_eq!(obj1_first, obj1_second, "Cached object should equal original");
}

#[test]
fn test_load_nonexistent_object() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");

    // Per PDF spec §7.3.10, missing object references "shall be treated as null" ~keep
    let result = pdf.load_object(ObjectRef::new(999, 0));
    assert!(
        result.is_ok(),
        "Loading nonexistent object should return Ok(Null) per §7.3.10"
    );
    assert!(
        matches!(result.unwrap(), xberg_native_pdf::object::Object::Null),
        "Nonexistent object should resolve to Null"
    );
}

#[test]
fn test_catalog_to_pages_to_count_flow() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");

    let catalog = pdf.catalog().expect("Failed to load catalog");
    let catalog_dict = catalog.as_dict().expect("Catalog should be dict");

    let pages_ref = catalog_dict
        .get("Pages")
        .and_then(|o| o.as_reference())
        .expect("Catalog should have /Pages reference");

    let pages = pdf.load_object(pages_ref).expect("Failed to load Pages");
    let pages_dict = pages.as_dict().expect("Pages should be dict");

    let count = pages_dict
        .get("Count")
        .and_then(|o| o.as_integer())
        .expect("Pages should have /Count");

    assert_eq!(count, 1, "Page count should be 1");
}

#[test]
fn test_media_box_array() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");

    let page = pdf.load_object(ObjectRef::new(3, 0)).expect("Failed to load page");
    let page_dict = page.as_dict().expect("Page should be dict");

    let media_box = page_dict
        .get("MediaBox")
        .and_then(|o| o.as_array())
        .expect("Page should have /MediaBox array");

    assert_eq!(media_box.len(), 4, "MediaBox should have 4 values");
    assert_eq!(media_box[0].as_integer(), Some(0), "MediaBox[0] should be 0");
    assert_eq!(media_box[1].as_integer(), Some(0), "MediaBox[1] should be 0");
    assert_eq!(media_box[2].as_integer(), Some(612), "MediaBox[2] should be 612");
    assert_eq!(media_box[3].as_integer(), Some(792), "MediaBox[3] should be 792");
}

#[test]
fn test_full_document_structure() {
    let pdf = PdfDocument::open(SIMPLE_PDF_PATH).expect("Failed to open simple.pdf");

    assert_eq!(pdf.version(), (1, 4));

    let catalog = pdf.catalog().expect("Should have catalog");
    assert!(catalog.as_dict().is_some());

    let count = pdf.page_count().expect("Should have page count");
    assert_eq!(count, 1);

    let _ = pdf.load_object(ObjectRef::new(1, 0)).expect("Object 1 exists");
    let _ = pdf.load_object(ObjectRef::new(2, 0)).expect("Object 2 exists");
    let _ = pdf.load_object(ObjectRef::new(3, 0)).expect("Object 3 exists");
}
