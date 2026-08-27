//! Regression tests for the object-nesting recursion-depth bound in
//! `parser::parse_array` / `parser::parse_dictionary`.
//!
//! Without a depth bound, `parse_object` -> `parse_array`/`parse_dictionary`
//! -> `parse_object` recurses once per container-nesting level with no
//! limit. A hostile object body like `[[[[[...` or `<</A<</A<</A...` with
//! tens of thousands of opening brackets recurses until the call stack
//! overflows. A stack overflow is a SIGSEGV/abort that kills the whole host
//! process — not a catchable panic, so no `#[should_panic]` and no
//! `catch_unwind` at any extractor boundary can contain it.
//!
//! IMPORTANT: these tests deliberately do NOT reproduce that ~20,000+ level
//! crash directly. Running an object body anywhere near that depth would
//! abort this test *process* against unfixed code rather than failing it
//! cleanly, which would take the whole `cargo test` run down with it. Instead
//! `OVER_BOUND_DEPTH` below is chosen to be comfortably past the parser's
//! internal bound (100 levels) while staying far short of stack-overflow
//! territory: on unfixed (unbounded) code this depth parses successfully
//! with no crash, it just produces a different (fully-parsed) result than
//! the fixed parser's "rejected" result — that difference is what the
//! assertions below check.
//!
//! Object 6 in the fixture PDF built here is never referenced by the
//! catalog/page tree; it exists purely so `PdfDocument::load_object` can be
//! pointed at it directly, mirroring how a real PDF's cross-reference table
//! can list an object whose body is hostile regardless of whether the
//! document actually uses it.

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::object::{Object, ObjectRef};

/// Nesting depth for the "exceeds bound" tests. Comfortably more than the
/// parser's internal nesting bound (100 levels, see
/// `xberg_native_pdf::parser`'s private `MAX_OBJECT_NESTING_DEPTH`) so the
/// guard is guaranteed to trip well before the last bracket, but far short
/// of the tens of thousands of levels that would risk overflowing the stack
/// on unfixed code (see module docs above).
const OVER_BOUND_DEPTH: usize = 150;

/// Nesting depth for the positive control: comfortably inside the parser's
/// bound.
const WITHIN_BOUND_DEPTH: usize = 20;

/// Integer sentinel placed at the innermost level of the nested-array
/// fixtures, so a successful parse can be checked for an exact value rather
/// than just "parsed to something".
const SENTINEL_ARRAY_LEAF: i64 = 424_242;

/// String sentinel placed at the innermost level of the nested-dictionary
/// fixtures.
const SENTINEL_DICT_LEAF: &str = "control-value";

/// Build a minimal PDF (catalog, page tree, one empty page — none of which
/// these tests exercise) plus an extra, otherwise-unreferenced indirect
/// object `6 0 obj` whose body is exactly `object_body`. The page tree is
/// only present because `PdfDocument::from_bytes` validates that `/Root` is
/// loadable at open time; object 6's hostile body is never touched until a
/// test explicitly calls `load_object(ObjectRef::new(6, 0))`.
fn build_pdf_with_extra_object(object_body: &[u8]) -> Vec<u8> {
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
    pdf.extend_from_slice(b"6 0 obj\n");
    pdf.extend_from_slice(object_body);
    pdf.extend_from_slice(b"\nendobj\n");

    finalize_xref(pdf, &offsets)
}

/// Append the cross-reference table, trailer and `startxref` for a body
/// whose object offsets are `offsets` (index 0 is the free head and is
/// ignored). Mirrors `tests/common/mod.rs::finalize_pdf`; duplicated locally
/// (rather than `mod common;`) because this fixture needs a 6th object that
/// the shared builder does not support.
fn finalize_xref(mut pdf: Vec<u8>, offsets: &[usize]) -> Vec<u8> {
    let xref_pos = pdf.len();
    pdf.extend_from_slice(format!("xref\n0 {}\n", offsets.len()).as_bytes());
    pdf.extend_from_slice(b"0000000000 65535 f\r\n");
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

/// `[[[...[leaf]...]]]` with `depth` opening/closing bracket pairs.
fn nested_array(depth: usize, leaf: &str) -> String {
    format!("{}{leaf}{}", "[".repeat(depth), "]".repeat(depth))
}

/// `<</A<</A...<</A leaf>>...>>>>` with `depth` levels, each keyed `/A`.
fn nested_dict(depth: usize, leaf: &str) -> String {
    format!("{}{leaf}{}", "<</A ".repeat(depth), ">>".repeat(depth))
}

/// Open the fixture PDF and load object 6 directly, bypassing the page tree.
fn load_object_6(pdf: Vec<u8>) -> Object {
    let doc = PdfDocument::from_bytes(pdf).expect("minimal fixture PDF must open");
    doc.load_object(ObjectRef::new(6, 0))
        .expect("load_object must not return Err: parser errors degrade to Object::Null, they don't propagate")
}

/// Drill through `depth` levels of single-element `Object::Array`, returning
/// the innermost element.
fn unwrap_nested_array(mut obj: &Object, depth: usize) -> &Object {
    for level in 0..depth {
        let arr = obj
            .as_array()
            .unwrap_or_else(|| panic!("expected an array at nesting level {level}, got {obj:?}"));
        assert_eq!(arr.len(), 1, "expected exactly one element at nesting level {level}");
        obj = &arr[0];
    }
    obj
}

/// Drill through `depth` levels of `Object::Dictionary` keyed `/A`,
/// returning the innermost `/A` value.
fn unwrap_nested_dict(mut obj: &Object, depth: usize) -> &Object {
    for level in 0..depth {
        let dict = obj
            .as_dict()
            .unwrap_or_else(|| panic!("expected a dictionary at nesting level {level}, got {obj:?}"));
        obj = dict
            .get("A")
            .unwrap_or_else(|| panic!("expected key /A at nesting level {level}"));
    }
    obj
}

/// A deeply nested array well beyond the parser's nesting bound must be
/// rejected as a whole, not partially parsed: the depth guard inside
/// `parse_array` returns `Err`, which propagates through every enclosing
/// `parse_array` call back up to `parse_object`, so `document.rs`'s object
/// loader falls back to `Object::Null` — its existing, pre-dating-this-fix
/// behavior for any object body it cannot parse — instead of recursing
/// further.
///
/// FAILS IF: the depth guard is removed, or its threshold raised past
/// `OVER_BOUND_DEPTH`. In either case this fixture's 150 levels parse
/// successfully (they are far too shallow to crash the process by
/// themselves — see module docs), so `load_object` would return the fully
/// parsed nested array instead of `Object::Null`.
#[test]
fn should_return_null_when_array_nesting_exceeds_bound() {
    let body = nested_array(OVER_BOUND_DEPTH, &SENTINEL_ARRAY_LEAF.to_string());
    let pdf = build_pdf_with_extra_object(body.as_bytes());
    let obj = load_object_6(pdf);
    assert_eq!(
        obj,
        Object::Null,
        "object exceeding the nesting bound must degrade to Null, not parse"
    );
}

/// Dictionary counterpart of `should_return_null_when_array_nesting_exceeds_bound`,
/// exercising `parse_dictionary`'s depth guard instead of `parse_array`'s.
///
/// FAILS IF: `parse_dictionary`'s depth guard is removed or its threshold
/// raised past `OVER_BOUND_DEPTH` — the dictionary would then parse
/// successfully instead of degrading to `Object::Null`.
#[test]
fn should_return_null_when_dictionary_nesting_exceeds_bound() {
    let body = nested_dict(OVER_BOUND_DEPTH, &format!("({SENTINEL_DICT_LEAF})"));
    let pdf = build_pdf_with_extra_object(body.as_bytes());
    let obj = load_object_6(pdf);
    assert_eq!(
        obj,
        Object::Null,
        "object exceeding the nesting bound must degrade to Null, not parse"
    );
}

/// POSITIVE CONTROL for the array case: nesting well within the bound must
/// still parse to exactly the expected value. This is the test that would
/// catch a bound set too tight (an off-by-one, or the constant accidentally
/// shrunk) — without it, an overly strict guard could silently break
/// legitimate PDFs while the two "exceeds bound" tests above keep passing.
///
/// FAILS IF: the guard rejects legitimate, shallow nesting (`load_object`
/// would panic in `load_object_6`'s `.expect`, or `unwrap_nested_array`
/// would panic on a `Null`/wrong-shape object instead of finding 20 levels
/// of single-element arrays), or if unrelated array-parsing logic regresses
/// the leaf value.
#[test]
fn should_parse_nested_array_within_bound_to_exact_value() {
    let body = nested_array(WITHIN_BOUND_DEPTH, &SENTINEL_ARRAY_LEAF.to_string());
    let pdf = build_pdf_with_extra_object(body.as_bytes());
    let obj = load_object_6(pdf);
    let leaf = unwrap_nested_array(&obj, WITHIN_BOUND_DEPTH);
    assert_eq!(leaf.as_integer(), Some(SENTINEL_ARRAY_LEAF));
}

/// POSITIVE CONTROL for the dictionary case, mirroring
/// `should_parse_nested_array_within_bound_to_exact_value`.
///
/// FAILS IF: the guard rejects legitimate, shallow dictionary nesting, or
/// unrelated dictionary-parsing logic regresses the leaf value.
#[test]
fn should_parse_nested_dictionary_within_bound_to_exact_value() {
    let body = nested_dict(WITHIN_BOUND_DEPTH, &format!("({SENTINEL_DICT_LEAF})"));
    let pdf = build_pdf_with_extra_object(body.as_bytes());
    let obj = load_object_6(pdf);
    let leaf = unwrap_nested_dict(&obj, WITHIN_BOUND_DEPTH);
    assert_eq!(leaf.as_string(), Some(SENTINEL_DICT_LEAF.as_bytes()));
}
