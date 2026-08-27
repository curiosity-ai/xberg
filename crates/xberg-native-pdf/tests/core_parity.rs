//! Core functional test-parity suite (Rust) — the reference implementation of
//! the shared cross-language test-parity spec. Every binding mirrors
//! these behaviors with its own idiomatic API. (Search is a binding-level
//! convenience and has no single Rust-core method, so it is covered in the
//! bindings, not here.)
//!
//! PDF-creation and write-side encryption ("create_pdf", "encrypt_roundtrip",
//! plus the `from_bytes_page_count` test built on the same fixture) were
//! removed along with the PDF writer/editor stack -- there is no longer a
//! Rust-core creation API for this suite to exercise.

use xberg_native_pdf::PdfDocument;

fn fixture_bytes() -> Vec<u8> {
    std::fs::read("tests/fixtures/simple.pdf").expect("simple.pdf fixture")
}

fn open() -> PdfDocument {
    PdfDocument::from_bytes(fixture_bytes()).expect("open simple.pdf")
}

#[test]
fn open_and_page_count() {
    assert_eq!(open().page_count().unwrap(), 1);
}

#[test]
fn extract_text() {
    let _: String = open().extract_text(0).unwrap();
}

#[test]
fn structured() {
    let _ = open().extract_structured(0).unwrap();
}

#[test]
fn open_error() {
    assert!(
        PdfDocument::from_bytes(b"this is not a pdf".to_vec()).is_err(),
        "opening non-PDF bytes must error"
    );
}
