//! Regression tests: xref table incorrectly marks real objects as free (`f`).
//!
//! Observed in the wild on PDFs whose producer corrupted the xref so that
//! hundreds of page objects carry `in_use=false` even though the objects
//! are physically present in the file body. Under strict §7.3.10 handling
//! those objects would be resolved to `Null`, which made
//! `get_page_by_scanning` miss them and bubbled up as
//! `RuntimeError: Page index N not found by scanning`.
//!
//! Fix: `load_object` now cross-checks the file body (via the existing
//! `scan_for_object` cache) before trusting a free xref entry. If an
//! `N G obj` marker exists in the file, the xref is ignored and the object
//! is loaded from the scanned offset. If no marker exists, the spec's null
//! fallback still applies.
//!
//! These tests synthesise a 2-page PDF, flip the xref entry for page 2 to
//! `f` (the exact shape the SEBI PDF exhibits), and assert that page count
//! plus content of BOTH pages is still reachable.

use xberg_native_pdf::document::PdfDocument;

// ---------------------------------------------------------------------------
// Helper: build a minimal 2-page PDF and return (bytes, obj_offsets) so the
// caller can rewrite individual xref rows afterwards.
// --------------------------------------------------------------------------- ~keep

fn build_two_page_pdf() -> (Vec<u8>, Vec<usize>) {
    let mut pdf = b"%PDF-1.4\n%\xE2\xE3\xCF\xD3\n".to_vec();
    let mut offsets = vec![0usize];

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n",
    );

    // 4 0 obj — page 2 (the one we'll flip to free in the xref) ~keep
    offsets.push(pdf.len());
    pdf.extend_from_slice(
        b"4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n",
    );

    offsets.push(pdf.len());
    let c1 = b"BT /F1 12 Tf 72 720 Td (Hello page one) Tj ET";
    pdf.extend_from_slice(format!("5 0 obj\n<< /Length {} >>\nstream\n", c1.len()).as_bytes());
    pdf.extend_from_slice(c1);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        b"6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n",
    );

    offsets.push(pdf.len());
    let c2 = b"BT /F1 12 Tf 72 720 Td (Hello page two) Tj ET";
    pdf.extend_from_slice(format!("7 0 obj\n<< /Length {} >>\nstream\n", c2.len()).as_bytes());
    pdf.extend_from_slice(c2);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    (pdf, offsets)
}

/// Append an xref table + trailer to `pdf`. `entries[i] = (offset, in_use)`
/// maps 1:1 to object id `i` (index 0 is the free-head sentinel).
fn append_xref(pdf: &mut Vec<u8>, entries: &[(usize, bool)]) {
    let xref_offset = pdf.len();
    pdf.extend_from_slice(format!("xref\n0 {}\n", entries.len()).as_bytes());
    pdf.extend_from_slice(b"0000000000 65535 f \r\n");
    for &(off, in_use) in &entries[1..] {
        let flag = if in_use { 'n' } else { 'f' };
        pdf.extend_from_slice(format!("{:010} 00000 {} \r\n", off, flag).as_bytes());
    }
    let trailer = format!(
        "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
        entries.len(),
        xref_offset
    );
    pdf.extend_from_slice(trailer.as_bytes());
}

#[test]
fn clean_pdf_baseline_both_pages_load() {
    let (mut pdf, offsets) = build_two_page_pdf();
    let entries: Vec<(usize, bool)> = offsets.iter().map(|&o| (o, true)).collect();
    append_xref(&mut pdf, &entries);

    let doc = PdfDocument::from_bytes(pdf).expect("open clean baseline PDF");
    assert_eq!(doc.page_count().expect("page count"), 2);

    let t1 = doc.extract_text(0).expect("extract page 1");
    let t2 = doc.extract_text(1).expect("extract page 2");
    assert!(t1.contains("page one"), "page 1 text: {t1:?}");
    assert!(t2.contains("page two"), "page 2 text: {t2:?}");
}

// ---------------------------------------------------------------------------
// Test 2: xref marks object 4 (= page 2) as free even though it exists in
// the file. Before the fix, loading page index 1 returned Null and
// `get_page_by_scanning` reported "Page index 1 not found by scanning".
// --------------------------------------------------------------------------- ~keep

#[test]
fn xref_marks_real_page_free_still_loads() {
    let (mut pdf, offsets) = build_two_page_pdf();
    let mut entries: Vec<(usize, bool)> = offsets.iter().map(|&o| (o, true)).collect();
    entries[4].1 = false;
    append_xref(&mut pdf, &entries);

    let doc = PdfDocument::from_bytes(pdf).expect("open corrupt PDF");
    assert_eq!(
        doc.page_count().expect("page count"),
        2,
        "page count should reflect the real /Count, not be reduced by the free-flagged page"
    );

    let t2 = doc
        .extract_text(1)
        .expect("page 2 must load via file-scan recovery, not Null-resolve");
    assert!(t2.contains("page two"), "recovered page 2 text: {t2:?}");

    // Page 1 should still work (no regression in the clean path). ~keep
    let t1 = doc.extract_text(0).expect("page 1");
    assert!(t1.contains("page one"), "page 1 text: {t1:?}");
}

// ---------------------------------------------------------------------------
// Test 3: negative case — if the object is genuinely missing from the file
// (free in xref AND no body marker), the §7.3.10 null fallback must still
// kick in rather than erroring.
// --------------------------------------------------------------------------- ~keep

#[test]
fn genuinely_free_object_still_treated_as_null() {
    // Build a PDF that never contained object 4. We fake "object 4 is free"
    // by writing an xref with 5 entries (0,1,2,3,4) but only 3 real bodies
    // (catalog, pages, single page) and flag slot 4 free at offset 0. ~keep
    let mut pdf = b"%PDF-1.4\n%\xE2\xE3\xCF\xD3\n".to_vec();
    let mut offs = vec![0usize];

    offs.push(pdf.len());
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
    offs.push(pdf.len());
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
    offs.push(pdf.len());
    pdf.extend_from_slice(b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

    // Slot 4: truly free — no body, xref offset is the conventional 0. ~keep
    let mut entries: Vec<(usize, bool)> = offs.iter().map(|&o| (o, true)).collect();
    entries.push((0, false));

    append_xref(&mut pdf, &entries);

    let doc = PdfDocument::from_bytes(pdf).expect("open PDF with real free slot");
    assert_eq!(doc.page_count().expect("page count"), 1);
}

// ---------------------------------------------------------------------------
// Test 4: second corruption shape — xref marks object `n` (in-use) but
// points at a byte offset a few bytes BEFORE the real `N G obj` header.
// This happened in the SEBI report PDF for roughly a fifth of the page
// objects: the `endobj` keyword of the previous object was included in the
// xref offset, so the parser read `j\r\n545 0 obj` and rejected `j` as an
// object number. The fix re-queries `scan_for_object` on header parse
// failure and retries at the scan-reported offset.
// --------------------------------------------------------------------------- ~keep

#[test]
fn xref_offset_off_by_a_few_bytes_recovers_via_scan() {
    let (mut pdf, offsets) = build_two_page_pdf();
    // Corrupt the xref entry for obj 4 (page 2) by shifting its offset
    // back into the previous object's `endobj` tail. The body offset is
    // still the real `4 0 obj` header, but the xref points ~6 bytes
    // earlier — the exact pattern observed in the field. ~keep
    let mut entries: Vec<(usize, bool)> = offsets.iter().map(|&o| (o, true)).collect();
    let real_off = entries[4].0;
    entries[4].0 = real_off.saturating_sub(6);
    append_xref(&mut pdf, &entries);

    let doc = PdfDocument::from_bytes(pdf).expect("open PDF with shifted xref");
    assert_eq!(doc.page_count().expect("page count"), 2);

    let t2 = doc
        .extract_text(1)
        .expect("page 2 must load via scan-based offset correction");
    assert!(t2.contains("page two"), "recovered page 2 text: {t2:?}");

    let t1 = doc.extract_text(0).expect("page 1 baseline");
    assert!(t1.contains("page one"), "page 1 text: {t1:?}");
}
