//! Regression test: global font cache must not share subset fonts across documents.
//!
//! Subset fonts (e.g. AAAAAA+Arial) have document-specific ToUnicode CMaps.
//! Without the cross-document cache-exclusion gate, two PDFs with the same
//! subset-prefixed BaseFont name would share a cached FontInfo, causing the
//! second document to use the first document's ToUnicode mapping — producing
//! wrong characters.

use xberg_native_pdf::document::PdfDocument;

/// Build a minimal PDF with a Type0 (CID) subset font and a ToUnicode CMap
/// that maps CID 1 to a specific Unicode character.
fn build_pdf_with_subset_font(unicode_char: char) -> Vec<u8> {
    let hex = format!("{:04X}", unicode_char as u32);
    let cmap = format!(
        "/CIDInit /ProcSet findresource begin\n\
         12 dict begin\n\
         begincmap\n\
         /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n\
         /CMapName /Adobe-Identity-UCS def\n\
         /CMapType 2 def\n\
         1 begincodespacerange\n\
         <0000> <FFFF>\n\
         endcodespacerange\n\
         1 beginbfchar\n\
         <0001> <{hex}>\n\
         endbfchar\n\
         endcmap\n\
         CMapName currentdict /CMap defineresource pop\n\
         end\n\
         end"
    );
    let cmap_bytes = cmap.as_bytes();

    let content = b"BT /F1 12 Tf <0001> Tj ET";

    let mut pdf = Vec::new();
    pdf.extend_from_slice(b"%PDF-1.4\n");

    let o1 = pdf.len();
    pdf.extend_from_slice(b"1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");

    let o2 = pdf.len();
    pdf.extend_from_slice(b"2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");

    let o3 = pdf.len();
    pdf.extend_from_slice(
        b"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] \
          /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj\n",
    );

    let o4 = pdf.len();
    let c4 = format!("4 0 obj << /Length {} >> stream\n", content.len());
    pdf.extend_from_slice(c4.as_bytes());
    pdf.extend_from_slice(content);
    pdf.extend_from_slice(b"\nendstream endobj\n");

    let o5 = pdf.len();
    pdf.extend_from_slice(
        b"5 0 obj << /Type /Font /Subtype /Type0 \
          /BaseFont /AAAAAA+TestFont \
          /Encoding /Identity-H \
          /ToUnicode 7 0 R \
          /DescendantFonts [6 0 R] >> endobj\n",
    );

    let o6 = pdf.len();
    pdf.extend_from_slice(
        b"6 0 obj << /Type /Font /Subtype /CIDFontType2 \
          /BaseFont /AAAAAA+TestFont \
          /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> \
          /W [1 [600]] /DW 1000 >> endobj\n",
    );

    let o7 = pdf.len();
    let c7 = format!("7 0 obj << /Length {} >> stream\n", cmap_bytes.len());
    pdf.extend_from_slice(c7.as_bytes());
    pdf.extend_from_slice(cmap_bytes);
    pdf.extend_from_slice(b"\nendstream endobj\n");

    let xref_offset = pdf.len();
    pdf.extend_from_slice(b"xref\n0 8\n");
    pdf.extend_from_slice(b"0000000000 65535 f \n");
    for offset in [o1, o2, o3, o4, o5, o6, o7] {
        let entry = format!("{:010} 00000 n \n", offset);
        pdf.extend_from_slice(entry.as_bytes());
    }

    pdf.extend_from_slice(b"trailer << /Size 8 /Root 1 0 R >>\nstartxref\n");
    let xref_str = format!("{}\n%%EOF\n", xref_offset);
    pdf.extend_from_slice(xref_str.as_bytes());

    pdf
}

#[test]
fn test_subset_font_cache_isolation() {
    xberg_native_pdf::fonts::global_cache::clear_global_font_cache();
    xberg_native_pdf::fonts::cmap::clear_cmap_cache();

    let pdf1_bytes = build_pdf_with_subset_font('A');
    let doc1 = PdfDocument::from_bytes(pdf1_bytes).expect("load pdf1");
    let text1 = doc1.extract_text(0).expect("extract pdf1");
    assert!(text1.contains('A'), "PDF 1 should contain 'A', got: {:?}", text1);

    let pdf2_bytes = build_pdf_with_subset_font('Z');
    let doc2 = PdfDocument::from_bytes(pdf2_bytes).expect("load pdf2");
    let text2 = doc2.extract_text(0).expect("extract pdf2");

    // Before the fix, text2 would contain 'A' (from cached PDF 1 font).
    // After the fix, text2 should contain 'Z'. ~keep
    assert!(
        text2.contains('Z'),
        "PDF 2 should contain 'Z' (not 'A' from cached font), got: {:?}",
        text2
    );
    assert!(
        !text2.contains('A'),
        "PDF 2 must NOT contain 'A' from cross-document cache pollution, got: {:?}",
        text2
    );
}
