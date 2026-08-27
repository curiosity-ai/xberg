//! Regression test bounding the previously-unbounded object-header line
//! reads in `PdfDocument::load_uncompressed_object_impl` (`document.rs`).
//!
//! Both `read_until(b'\n', ...)` calls in the header-reading phase -- the
//! initial read and the multi-line continuation read inside the
//! `has_standalone_obj_keyword` search loop -- had no size limit of their
//! own. The `MAX_BYTES` (100 MB) guard further down only checks the
//! *body* loop, between reads; it does nothing for either of these two
//! calls. On a file whose next LF is far away -- a CR-terminated PDF,
//! legal per ISO 32000-1, or simply a very long run of non-LF bytes at a
//! corrupted xref offset -- a single `read_until` call reads everything up
//! to that next LF (or EOF) in one allocation, no matter how far away it
//! is.
//!
//! The fix caps each of the two reads at 64 KB (mirrored below as
//! `MAX_HEADER_LINE_BYTES_MIRROR`, since the real constant is private to
//! `document.rs`). Capping only the *first* read would not close this on
//! its own: once the multi-line search loop takes over, its continuation
//! read is a second, otherwise-identical `read_until(b'\n', ...)` call, so
//! an attacker's long run would simply shift from the first call to the
//! second with no net reduction. This test's fixture makes sure the
//! "N G obj" keyword is never found at all (an all-ASCII run of unrelated
//! filler bytes), forcing the multi-line loop to run to its existing
//! 5-line limit -- exactly the scenario where bounding only the first read
//! would leave the second read to absorb the whole unbounded run.
//!
//! There is no public way to observe how many bytes a single internal
//! `read_until` call consumed, so this test observes the *cumulative*
//! effect instead: the eventual `Error::ParseError { reason, .. }` embeds
//! `full_header.trim()` verbatim, and `full_header`'s length is exactly the
//! sum of every header-phase read. Capping each read to 64 KB across at
//! most 5 lines bounds that sum to a small, fixed multiple of 64 KB no
//! matter how long the underlying run is; without the cap it grows with
//! the size of the attacker-controlled run.

mod common;

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::error::{Error, Result};
use xberg_native_pdf::object::{Object, ObjectRef};

/// Mirrors the private `MAX_HEADER_LINE_BYTES` constant in
/// `document.rs::load_uncompressed_object_impl`. Kept here (rather than
/// exported) because the two header-phase reads are an internal
/// implementation detail; this test observes their combined effect through
/// the public `load_object` API instead (see module docs).
const MAX_HEADER_LINE_BYTES_MIRROR: usize = 64 * 1024;

/// Hard cap on how many lines the multi-line header search examines before
/// giving up, matching `document.rs`'s local `max_header_lines`.
const MAX_HEADER_LINES_MIRROR: usize = 5;

/// A run of filler bytes long enough that a single, unbounded
/// `read_until(b'\n', ...)` call would have to read straight through it:
/// comfortably more than `MAX_HEADER_LINES_MIRROR * MAX_HEADER_LINE_BYTES_MIRROR`
/// (320 KB), so the fix's per-read cap must actually bind more than once
/// before the (existing, unrelated) 5-line search limit gives up.
const FILLER_LEN: usize = 2_000_000;

/// Build a minimal PDF (catalog, page tree, one empty page -- none of which
/// these tests exercise) plus an extra, otherwise-unreferenced indirect
/// object `6 0 obj` whose raw body is exactly `object_body`, terminated
/// with `endobj`. Mirrors the builder in `object_header_char_boundary.rs`;
/// duplicated locally per this suite's convention of keeping each defect's
/// fixture builder next to its test (see `tests/deep_object_nesting_bound.rs`).
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

/// FAILS IF: either header-phase read is unbounded. Against unfixed code,
/// the header search's very first `read_until` call has no LF to stop at
/// until it runs clean through all `FILLER_LEN` (2,000,000) filler bytes,
/// so the resulting `ParseError`'s `reason` string is on that order --
/// comfortably over `MAX_HEADER_LINES_MIRROR * MAX_HEADER_LINE_BYTES_MIRROR`
/// (320 KB). Against fixed code, each of up to 5 header-phase reads is
/// capped at 64 KB, so the search gives up (per the existing, unrelated
/// 5-line limit) having examined at most ~320 KB total, and `reason.len()`
/// stays under this test's generous 400 KB ceiling.
#[test]
fn should_bound_total_header_bytes_examined_when_no_lf_is_nearby() {
    let body = vec![b'X'; FILLER_LEN];
    let pdf = build_pdf_with_object_six_body(&body);

    let result = load_object_6(pdf);

    match result {
        Err(Error::ParseError { reason, .. }) => {
            let ceiling = MAX_HEADER_LINES_MIRROR * MAX_HEADER_LINE_BYTES_MIRROR + MAX_HEADER_LINE_BYTES_MIRROR;
            assert!(
                reason.len() <= ceiling,
                "header search must stop within {ceiling} bytes of total examined content, \
                 not scale with the attacker-controlled filler length; got a {}-byte reason",
                reason.len()
            );
        }
        other => panic!(
            "expected a bounded ParseError for a header with no \"N G obj\" text anywhere reachable, got {other:?}"
        ),
    }
}

/// POSITIVE CONTROL: an ordinary single-line "N G obj<<...>>" header whose
/// inline dictionary content is comfortably under the per-read cap must
/// still parse to the exact expected value -- proving the cap does not
/// disturb legitimate files, only pathological ones.
///
/// FAILS IF: the cap is set too small for realistic single-line object
/// headers, or otherwise regresses the "content after obj on the same
/// line" fast path.
#[test]
fn should_parse_single_line_header_within_bound_to_exact_value() {
    // Comfortably under the 64 KB cap, comfortably over a typical header.
    const PADDING_LEN: usize = 10_000;
    let mut body = b"6 0 obj<< /Type /Padded /Filler (".to_vec();
    body.extend_from_slice(&vec![b'A'; PADDING_LEN]);
    body.extend_from_slice(b") >>");
    let pdf = build_pdf_with_object_six_body(&body);

    let obj = load_object_6(pdf).expect("single-line header within the cap must still parse");
    let dict = obj.as_dict().expect("expected a dictionary");
    assert_eq!(dict.get("Type").and_then(|v| v.as_name()), Some("Padded"));
    assert_eq!(
        dict.get("Filler").and_then(|v| v.as_string()).map(|s| s.len()),
        Some(PADDING_LEN),
        "the padded literal string must round-trip at its full length"
    );
}
