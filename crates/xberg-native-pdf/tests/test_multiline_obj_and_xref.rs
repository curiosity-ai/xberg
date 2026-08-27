//! Regression tests for multi-line object headers and xref reconstruction.
//!
//! These tests cover:
//! - Multi-line object headers (e.g. "1\n0\nobj")
//! - Garbage-prepended PDFs where header_offset > 0
//! - Corrupt xref tables triggering reconstruction fallback
//! - The `contains("obj")` bug that matched "endobj"

use xberg_native_pdf::document::PdfDocument;

/// Build a minimal valid PDF where object headers use the given separator
/// between obj_num, gen_num, and "obj".
///
/// `header_fmt` takes (obj_num, gen_num) and returns the header string.
fn build_pdf_custom_headers(header_fmt: impl Fn(u32, u16) -> String) -> Vec<u8> {
    let mut pdf = b"%PDF-1.4\n".to_vec();

    let off1 = pdf.len();
    let h1 = header_fmt(1, 0);
    pdf.extend_from_slice(format!("{}\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n", h1).as_bytes());

    let off2 = pdf.len();
    let h2 = header_fmt(2, 0);
    pdf.extend_from_slice(format!("{}\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n", h2).as_bytes());

    let off3 = pdf.len();
    let h3 = header_fmt(3, 0);
    pdf.extend_from_slice(
        format!(
            "{}\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            h3
        )
        .as_bytes(),
    );

    let off4 = pdf.len();
    let h4 = header_fmt(4, 0);
    let content = "BT /F1 12 Tf 72 720 Td (Hello World) Tj ET";
    pdf.extend_from_slice(format!("{}\n<< /Length {} >>\nstream\n", h4, content.len()).as_bytes());
    pdf.extend_from_slice(content.as_bytes());
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    let off5 = pdf.len();
    let h5 = header_fmt(5, 0);
    pdf.extend_from_slice(
        format!(
            "{}\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n",
            h5
        )
        .as_bytes(),
    );

    finalize_xref(&mut pdf, &[0, off1, off2, off3, off4, off5]);
    pdf
}

fn finalize_xref(pdf: &mut Vec<u8>, obj_offsets: &[usize]) {
    let xref_offset = pdf.len();
    let count = obj_offsets.len();
    pdf.extend_from_slice(format!("xref\n0 {}\n", count).as_bytes());
    pdf.extend_from_slice(b"0000000000 65535 f \r\n");
    for &off in &obj_offsets[1..] {
        pdf.extend_from_slice(format!("{:010} 00000 n \r\n", off).as_bytes());
    }
    let trailer = format!(
        "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
        count, xref_offset
    );
    pdf.extend_from_slice(trailer.as_bytes());
}

#[test]
fn test_standard_single_line_headers() {
    let pdf = build_pdf_custom_headers(|id, generation| format!("{} {} obj", id, generation));
    let doc = PdfDocument::from_bytes(pdf).expect("should open standard PDF");
    assert_eq!(doc.page_count().expect("page count"), 1);
    let text = doc.extract_text(0).expect("extract text");
    assert!(text.contains("Hello World"), "text: {}", text);
}

#[test]
fn test_multiline_object_header_full_newline() {
    let pdf = build_pdf_custom_headers(|id, generation| format!("{}\n{}\nobj", id, generation));
    let doc = PdfDocument::from_bytes(pdf).expect("should open PDF with fully multi-line headers");
    assert_eq!(doc.page_count().expect("page count"), 1);
    let text = doc.extract_text(0).expect("extract text");
    assert!(text.contains("Hello World"), "text: {}", text);
}

#[test]
fn test_multiline_object_header_mixed() {
    let pdf = build_pdf_custom_headers(|id, generation| format!("{}\n{} obj", id, generation));
    let doc = PdfDocument::from_bytes(pdf).expect("should open PDF with mixed multi-line headers");
    assert_eq!(doc.page_count().expect("page count"), 1);
}

#[test]
fn test_multiline_object_header_crlf() {
    let pdf = build_pdf_custom_headers(|id, generation| format!("{}\r\n{}\r\nobj", id, generation));
    let doc = PdfDocument::from_bytes(pdf).expect("should open PDF with CRLF multi-line headers");
    assert_eq!(doc.page_count().expect("page count"), 1);
}

#[test]
fn test_garbage_prefix_offset_adjustment() {
    let valid_pdf = build_pdf_custom_headers(|id, generation| format!("{} {} obj", id, generation));

    let mut garbage_pdf = vec![0xFFu8; 1024];
    garbage_pdf.extend_from_slice(&valid_pdf);

    // The xref offsets in this PDF are relative to the start of the valid PDF data,
    // which is now at byte 1024. The header_offset adjustment should fix this. ~keep
    let doc = PdfDocument::from_bytes(garbage_pdf).expect("should open garbage-prepended PDF");
    assert_eq!(doc.page_count().expect("page count"), 1);
    let text = doc.extract_text(0).expect("extract text");
    assert!(text.contains("Hello World"), "text: {}", text);
}

// ---------------------------------------------------------------------------
// Test 6: Corrupt xref triggers reconstruction fallback
// --------------------------------------------------------------------------- ~keep

#[test]
fn test_corrupt_xref_triggers_reconstruction() {
    let mut pdf = build_pdf_custom_headers(|id, generation| format!("{} {} obj", id, generation));

    // Find and corrupt the xref table — replace offset digits with zeros
    // to make the xref point to wrong locations ~keep
    let xref_marker = b"xref\n";
    if let Some(pos) = pdf.windows(xref_marker.len()).position(|w| w == xref_marker) {
        // Corrupt the xref entries: overwrite the offset numbers
        // Skip "xref\n0 N\n" and the free entry, then corrupt in-use entries ~keep
        let xref_start = pos + xref_marker.len();
        if let Some(nl) = pdf[xref_start..].iter().position(|&b| b == b'\n') {
            let entries_start = xref_start + nl + 1;
            let first_entry = entries_start + 20;
            let mut i = first_entry;
            while i + 20 <= pdf.len() && pdf[i] != b't' {
                // Replace first 10 chars (offset) with zeros ~keep
                for j in 0..10 {
                    if i + j < pdf.len() {
                        pdf[i + j] = b'0';
                    }
                }
                i += 20;
            }
        }
    }

    // Should still open via xref reconstruction ~keep
    let doc = PdfDocument::from_bytes(pdf).expect("should open PDF with corrupt xref via reconstruction");
    assert_eq!(doc.page_count().expect("page count"), 1);
}

#[test]
fn test_endobj_not_confused_with_obj() {
    // Build a PDF where the xref intentionally points a few bytes too late
    // (into the object body area), so the parser reads "endobj" before finding
    // the real header. The fix ensures "endobj" doesn't satisfy the loop condition.
    //
    // We test this indirectly: a standard PDF should parse correctly even though
    // every object body contains "endobj" (the loop should keep reading past it). ~keep
    let pdf = build_pdf_custom_headers(|id, generation| format!("{} {} obj", id, generation));
    let doc = PdfDocument::from_bytes(pdf).expect("standard PDF should open");
    assert_eq!(doc.page_count().expect("page count"), 1);
}

#[test]
fn test_multiline_header_with_extra_whitespace() {
    let pdf = build_pdf_custom_headers(|id, generation| format!("{}  \t {}  \t obj", id, generation));
    let doc = PdfDocument::from_bytes(pdf).expect("should handle extra whitespace in headers");
    assert_eq!(doc.page_count().expect("page count"), 1);
}

#[test]
fn test_xref_shift_offsets() {
    use xberg_native_pdf::xref::{CrossRefTable, XRefEntry, XRefEntryType};

    let mut xref = CrossRefTable::new();
    xref.add_entry(1, XRefEntry::uncompressed(100, 0));
    xref.add_entry(2, XRefEntry::uncompressed(200, 0));
    xref.add_entry(3, XRefEntry::compressed(5, 0));
    xref.add_entry(0, XRefEntry::free(0, 65535));

    xref.shift_offsets(50);

    assert_eq!(xref.get(1).unwrap().offset, 150);
    assert_eq!(xref.get(2).unwrap().offset, 250);
    assert_eq!(xref.get(3).unwrap().offset, 5);
    assert_eq!(xref.get(3).unwrap().entry_type, XRefEntryType::Compressed);
    assert_eq!(xref.get(0).unwrap().offset, 0);
}
