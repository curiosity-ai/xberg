//! Regression tests for the arithmetic guards in the cross-reference stream
//! parser (`xref.rs`) and its reconstruction fallback (`xref_reconstruction.rs`).
//!
//! Three hostile inputs used to reach unguarded arithmetic on attacker-influenced
//! values:
//!
//! 1. A negative `/W` element cast to `usize` before being range-checked, which
//!    a wrapping `entry_size` sum let slip past the only guard that existed
//!    (`entry_size == 0`), ending in a slice-index panic. **Release-reachable.**
//! 2. An xref-stream offset past EOF, reached (only) via the `/XRefStm` hybrid
//!    reference — the ordinary `startxref`/`/Prev` paths already reject an
//!    out-of-range offset before calling into the vulnerable function, so this
//!    one needed a different trigger than the naive "just point startxref past
//!    EOF" repro (see the test's doc comment for why). **Debug/fuzz-build only.**
//! 3. A `search_nearby_for_object` window bound computed with a bare `+` next to
//!    a `saturating_sub`, so a maliciously wide xref entry offset (reachable via
//!    `/W [1 8 2]`, which allows a full 8-byte/u64 offset field) could overflow
//!    the addition. **Debug/fuzz-build only** — the release-mode wraparound on
//!    both sides of the subtraction cancels out, see the test's doc comment.
//!
//! A fourth test is the positive control: an ordinary, well-formed `/W [1 2 1]`
//! cross-reference stream must still parse and resolve every object correctly.

mod common;

use common::text_run_op;
use std::io::Cursor;
use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::xref::{XRefEntryType, parse_xref};
use xberg_native_pdf::xref_reconstruction::search_nearby_for_object;

/// (1) A negative `/W` element must be rejected before it is ever cast to
/// `usize`, not after.
///
/// Before the fix, `parse_xref_stream` cast each `/W` element to `usize`
/// unchecked. For `/W [3 -1 1]`, the middle field (`-1i64 as usize`) became
/// `usize::MAX`. `entry_size` (`w1 + w2 + w3`) wrapped back down to `3` in a
/// release build, so the only existing guard (`entry_size == 0`) passed, and
/// `entry_data[w1..w1 + w2]` (i.e. `entry_data[3..2]`, since `3 + usize::MAX`
/// also wraps) panicked with "slice index starts at 3 but ends at 2" — a
/// **release-reachable** panic, because slice-range validation (`start <=
/// end`) is a runtime check independent of `overflow-checks`. A debug build
/// panics one step earlier, on the `w1 + w2` addition overflow itself.
///
/// The fix validates every `/W` element is in `0..=8` (the PDF spec caps
/// xref-stream field widths there) before any cast, so this must now return
/// a structured `Err` instead of panicking in either build profile.
#[test]
fn negative_w_field_does_not_panic_and_yields_no_entries() {
    let mut pdf = b"%PDF-1.5\n".to_vec();
    let xref_offset = pdf.len() as u64;
    pdf.extend_from_slice(b"1 0 obj\n");
    pdf.extend_from_slice(b"<< /Type /XRef /Size 2 /W [3 -1 1] /Index [0 1] /Length 4 >>\nstream\n");
    pdf.extend_from_slice(&[0u8, 0, 0, 0]);
    pdf.extend_from_slice(b"\nendstream\nendobj\n%%EOF\n");

    let mut cursor = Cursor::new(pdf);
    let result = parse_xref(&mut cursor, xref_offset);

    // `parse_xref` does not surface the /W error to its caller: when the stream
    // parse fails it falls back to `parse_traditional_xref` at the same offset
    // (xref.rs:423-438), which on this input succeeds with an empty table. That
    // fallback is deliberate ("preserve partial results"), so the observable
    // contract here is that the hostile /W yields no usable entries instead of
    // panicking -- verified against the unfixed code, which panics inside
    // `parse_xref_stream` at the slicing arithmetic rather than reaching this
    // assertion at all.
    let xref = result.expect("the /W guard must turn the panic into a recoverable parse failure");
    assert!(
        xref.is_empty(),
        "a hostile /W must not yield usable xref entries, got {} of them",
        xref.len()
    );
}

/// (2) An xref-stream offset past EOF must not underflow `file_len - offset`.
///
/// The naive repro (`startxref` pointing far past EOF, or a `/Prev` chain
/// ending there) does **not** reach `parse_xref_stream`: `parse_xref_iterative`
/// peeks the first byte at the (corrected) offset to decide whether to try a
/// traditional table or a stream, and reading past EOF always yields zero
/// bytes, which matches neither branch — the whole parse fails cleanly with
/// `Error::InvalidXref` *before* `parse_xref_stream` is ever called. That gate
/// does not exist on the `/XRefStm` hybrid-reference path (PDF §7.5.8.4),
/// which calls `parse_xref_stream` directly with no prefix check. This test
/// uses that path, since it is the one input shape that actually reaches the
/// vulnerable subtraction.
///
/// This assertion gates the debug/fuzz build only: `overflow-checks` are on
/// by default in debug, so `file_len - offset` panics there whenever
/// `offset > file_len`. In release, the same subtraction wraps to a huge
/// `usize` that the caller's `.min(256 * 1024)` clamps back down to a normal
/// read size, so nothing crashes there even without the fix — it just reads
/// unrelated bytes and fails to parse a valid xref stream, which is the
/// graceful `Err` this test observes (silently, since a bad `/XRefStm` is
/// logged and skipped rather than propagated) with the fix applied.
#[test]
fn xrefstm_offset_past_eof_does_not_panic() {
    let bogus_offset: u64 = 999_999_999_999;

    let mut pdf = b"%PDF-1.4\n".to_vec();
    let obj1_offset = pdf.len();
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog >>\nendobj\n");

    let xref_offset = pdf.len() as u64;
    pdf.extend_from_slice(b"xref\n0 2\n");
    pdf.extend_from_slice(b"0000000000 65535 f\r\n");
    pdf.extend_from_slice(format!("{obj1_offset:010} 00000 n\r\n").as_bytes());
    pdf.extend_from_slice(
        format!("trailer\n<< /Size 2 /Root 1 0 R /XRefStm {bogus_offset} >>\nstartxref\n{xref_offset}\n%%EOF\n")
            .as_bytes(),
    );

    let mut cursor = Cursor::new(pdf);
    let result = parse_xref(&mut cursor, xref_offset);

    let xref = result.expect("a bogus /XRefStm must not abort the whole xref parse, only be skipped");
    assert!(
        xref.contains(1),
        "the traditional table's own entry must survive an unparseable /XRefStm supplement"
    );
}

/// (3) `search_nearby_for_object`'s search-window bound must not overflow.
///
/// `approx_offset` comes from an xref entry offset field, which under a
/// maliciously wide `/W` (e.g. `[1 8 2]`, a full 8-byte/u64 offset) can be
/// any `u64` value up to `u64::MAX`. Before the fix, `start` was computed
/// with `saturating_sub` but `end` with a bare `+`, so `approx_offset +
/// search_range` could overflow near `u64::MAX`.
///
/// This assertion gates the debug/fuzz build only: in debug, the bare `+`
/// panics on overflow. In release, `wrapping_add`-style overflow on `end`
/// and the later `end - start` subtraction are both modular, and because
/// `start` never itself saturates for our extreme `approx_offset`, the two
/// wraps cancel exactly (`end - start` still equals `2 * search_range`), so
/// release never panicked either before or after this fix — the wraparound
/// happened to always double back to the deliberately intended value. This
/// test now asserts no panic and a clean "not found" `Err`, which is the
/// same outcome release always produced.
#[test]
fn search_nearby_for_object_near_u64_max_does_not_panic() {
    let mut cursor = Cursor::new(vec![0u8; 64]);
    let approx_offset = u64::MAX - 10;

    let result = search_nearby_for_object(&mut cursor, 5, approx_offset);

    assert!(
        result.is_err(),
        "no object marker exists in this buffer, so the search must report not-found, not panic"
    );
}

/// Build a one-page PDF terminated with a well-formed `/W [1 2 1]`
/// cross-reference *stream* (PDF 1.5+) rather than a traditional table.
///
/// Deliberately not built on `common::finalize_pdf`, which only ever emits
/// traditional tables and is shared by every other test file in this suite —
/// duplicating the small amount of xref-stream-specific bytes here keeps
/// that shared helper untouched. Returns the finished bytes and the object
/// offsets (`offsets[0]` is the unused free-list head, `offsets[6]` is the
/// xref stream object's own offset, i.e. the `startxref` target).
fn build_pdf_with_valid_xref_stream(content: &[u8], page_extra: &[u8]) -> (Vec<u8>, Vec<usize>) {
    let mut pdf = b"%PDF-1.5\n".to_vec();
    let mut offsets = vec![0usize];

    offsets.push(pdf.len()); // 1: Catalog
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    offsets.push(pdf.len()); // 2: Pages
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    offsets.push(pdf.len()); // 3: Page
    pdf.extend_from_slice(b"3 0 obj\n<< ");
    pdf.extend_from_slice(page_extra);
    pdf.extend_from_slice(b" /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

    offsets.push(pdf.len()); // 4: Contents
    pdf.extend_from_slice(format!("4 0 obj\n<< /Length {} >>\nstream\n", content.len()).as_bytes());
    pdf.extend_from_slice(content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    offsets.push(pdf.len()); // 5: Font
    pdf.extend_from_slice(
        b"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica \
          /Encoding /WinAnsiEncoding >>\nendobj\n",
    );

    offsets.push(pdf.len()); // 6: the xref stream itself

    // /W [1 2 1]: 1-byte type, 2-byte offset/obj-num, 1-byte generation/index.
    // Every offset in this tiny fixture is well under 65536, so the 2-byte
    // middle field never truncates.
    let mut entries = Vec::with_capacity(offsets.len() * 4);
    entries.extend_from_slice(&[0u8, 0x00, 0x00, 0xFF]); // object 0: free head
    for &off in &offsets[1..] {
        let off = u16::try_from(off).expect("fixture offsets fit in 16 bits");
        entries.extend_from_slice(&[1u8, (off >> 8) as u8, (off & 0xFF) as u8, 0]);
    }

    let dict = format!(
        "<< /Type /XRef /Size {size} /W [1 2 1] /Index [0 {size}] /Root 1 0 R /Length {len} >>",
        size = offsets.len(),
        len = entries.len()
    );
    pdf.extend_from_slice(format!("6 0 obj\n{dict}\nstream\n").as_bytes());
    pdf.extend_from_slice(&entries);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");
    pdf.extend_from_slice(format!("startxref\n{}\n%%EOF\n", offsets[6]).as_bytes());

    (pdf, offsets)
}

/// Positive control: a well-formed `/W [1 2 1]` xref stream — no negative
/// widths, no out-of-range offsets — must be unaffected by the bounds checks
/// added above, both at the raw cross-reference-table level and end to end
/// through document parsing and text extraction.
#[test]
fn valid_w_1_2_1_xref_stream_parses_and_resolves_objects() {
    let content = text_run_op("hello xref stream", 72.0, 700.0, "F1", 12.0);
    let (pdf, offsets) =
        build_pdf_with_valid_xref_stream(content.as_bytes(), b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");

    let mut cursor = Cursor::new(pdf.clone());
    let xref =
        parse_xref(&mut cursor, offsets[6] as u64).expect("a well-formed /W [1 2 1] xref stream must still parse");

    let catalog_entry = xref.get(1).expect("object 1 (Catalog) must be present");
    assert_eq!(catalog_entry.entry_type, XRefEntryType::Uncompressed);
    assert_eq!(catalog_entry.offset, offsets[1] as u64);

    let font_entry = xref.get(5).expect("object 5 (Font) must be present");
    assert_eq!(font_entry.entry_type, XRefEntryType::Uncompressed);
    assert_eq!(font_entry.offset, offsets[5] as u64);

    let doc = PdfDocument::from_bytes(pdf).expect("document must open via its own valid xref stream");
    let text = doc.extract_text(0).expect("extract page 0");
    assert_eq!(text.trim_end(), "hello xref stream", "got {text:?}");
}
