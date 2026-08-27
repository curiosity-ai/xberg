//! Regression test for a char-boundary panic in `document::has_standalone_obj_keyword`,
//! which detects the "N G obj" header keyword while reading an indirect
//! object's header.
//!
//! The function used to take `&str` and slice it with `&s[i - 3..i]` to
//! check whether a matched "obj" was actually "endobj". Its input comes
//! from `String::from_utf8_lossy` over the raw bytes at an xref-table-
//! supplied offset -- attacker-controlled, since a crafted xref offset can
//! point anywhere in the file, including into binary content that happens
//! to contain the ASCII bytes `obj`. `from_utf8_lossy` is lossless for
//! already-valid UTF-8, so a 4-byte character (the U+1F600 emoji,
//! `F0 9F 98 80`) immediately followed by the literal bytes `o b j` decodes
//! unchanged to a `&str` in which `"obj"` starts at byte index 4. `i >= 3`
//! is then true, so the old code sliced `&s[1..4]` to compare against
//! `"end"` -- but bytes 1..4 are the three UTF-8 continuation bytes of the
//! emoji, not a char boundary, so the slice panicked with:
//!
//!   byte index 1 is not a char boundary; it is inside '😀' (bytes 0..4) of `😀obj
//!  `
//!
//! Fixed the same way `29fdd59d69` fixed the equivalent defect shape in
//! `fonts::cmap` (a `from_utf8_lossy` output sliced at a fixed byte
//! offset): `has_standalone_obj_keyword` now takes `&[u8]` and compares
//! bytes directly, so there is no char-boundary concept left to violate --
//! any byte-slice index that passes the existing bounds checks is always
//! valid.
//!
//! The fixture below places the malicious bytes at object 6's own body
//! (never referenced by the catalog/page tree), mirroring
//! `deep_object_nesting_bound.rs`'s use of an unreferenced extra object so
//! `PdfDocument::load_object` can be pointed at it directly, bypassing the
//! page tree. Object 1 (the actual `/Root`) cannot carry the malicious
//! bytes here: `PdfDocument::from_bytes` validates the root is loadable at
//! open time via a *different*, non-panicking check
//! (`validate_object_at_offset`), and a failure there triggers whole-file
//! xref reconstruction -- which would silently "heal" the corrupted offset
//! by re-scanning the file for real `N G obj` headers, defeating the
//! reproduction before `load_object` is ever reached.

mod common;

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::error::{Error, Result};
use xberg_native_pdf::object::{Object, ObjectRef};

/// U+1F600 GRINNING FACE, encoded as UTF-8: 4 bytes, `F0 9F 98 80`.
const EMOJI_UTF8: [u8; 4] = [0xF0, 0x9F, 0x98, 0x80];

/// Build a minimal PDF (catalog, page tree, one empty page -- none of which
/// these tests exercise) plus an extra, otherwise-unreferenced indirect
/// object `6 0 obj` whose raw body is exactly `object_body`, terminated
/// with `endobj`. Object 6's xref offset points directly at `object_body`'s
/// first byte; when `object_body` is not itself a well-formed `"6 0 obj"`
/// header, this reproduces an xref offset pointing straight into raw
/// content, per the bug this test targets.
fn build_pdf_with_object_six_body(object_body: &[u8]) -> Vec<u8> {
    let mut pdf = b"%PDF-1.4\n".to_vec();
    let mut offsets = vec![0usize];

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] \
          /Contents 4 0 R /Resources << >> >>\nendobj\n",
    );

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(object_body);
    pdf.extend_from_slice(b"\nendobj\n");

    common::finalize_pdf(pdf, &offsets)
}

/// Open the fixture PDF and load object 6 directly, bypassing the page tree.
fn load_object_6(pdf: Vec<u8>) -> Result<Object> {
    let doc = PdfDocument::from_bytes(pdf).expect("minimal fixture PDF must open");
    doc.load_object(ObjectRef::new(6, 0))
}

/// The exact byte layout from the bug report: a 4-byte UTF-8 character
/// immediately followed by the ASCII bytes `obj`, with `"obj"` starting at
/// byte index 4 -- so the pre-fix `&s[1..4]` slice lands inside the
/// character's continuation bytes.
///
/// FAILS IF: `has_standalone_obj_keyword` still slices a `&str` at a fixed
/// byte offset instead of operating on bytes. Against unfixed code this
/// panics with `byte index 1 is not a char boundary; it is inside '😀'
/// (bytes 0..4) of ...` -- deliberately not asserted via `#[should_panic]`:
/// the fixed function must not panic at all, so any panic here, with this
/// or any other message, is exactly the regression this test guards
/// against and must fail the test. Against fixed code, no valid `"N G
/// obj"` header exists in this content (there is no numeric object/
/// generation pair before "obj"), so the object degrades to a clean `Err`,
/// not a crash.
#[test]
fn should_not_panic_on_multibyte_char_immediately_before_obj_keyword() {
    let mut body = EMOJI_UTF8.to_vec();
    body.extend_from_slice(b"obj");
    let pdf = build_pdf_with_object_six_body(&body);

    let result = load_object_6(pdf);

    match result {
        Err(Error::ParseError { .. }) => {}
        other => panic!("expected a clean ParseError for a header with no valid \"N G obj\" text, got {other:?}"),
    }
}

/// POSITIVE CONTROL: a well-formed header whose object body separately
/// contains a multi-byte character (in a PDF literal string, unrelated to
/// the "N G obj" keyword scan) must still parse to the exact expected
/// value.
///
/// FAILS IF: the byte-based rewrite of `has_standalone_obj_keyword` or its
/// caller regresses ordinary multi-byte content handling elsewhere in the
/// object.
#[test]
fn should_parse_normally_when_multibyte_char_is_unrelated_to_obj_keyword() {
    let mut body = b"6 0 obj\n<< /Type /Emoji /Label (".to_vec();
    body.extend_from_slice(&EMOJI_UTF8);
    body.extend_from_slice(b") >>");
    let pdf = build_pdf_with_object_six_body(&body);

    let obj = load_object_6(pdf).expect("well-formed header with an unrelated emoji byte string must parse");
    let dict = obj.as_dict().expect("expected a dictionary");
    assert_eq!(
        dict.get("Type").and_then(|v| v.as_name()),
        Some("Emoji"),
        "dictionary content must parse correctly around the unrelated multi-byte character"
    );
    assert_eq!(
        dict.get("Label").and_then(|v| v.as_string()),
        Some(EMOJI_UTF8.as_slice()),
        "the literal string's raw bytes must round-trip exactly"
    );
}
