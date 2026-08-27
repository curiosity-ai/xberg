//! Integration test: an untagged bulleted list whose markers and bodies align
//! into two columns must NOT be rendered as a Markdown table — it is a list.
//!
//! Reproduces the structured-document false-positive where the spatial
//! (no-rulings) table detector fused a heading + bulleted list + prose into a
//! grid. The PDF is hand-built and untagged (no `/StructTreeRoot`), so the
//! markdown converter's spatial table fallback is what must decline here.

use xberg_native_pdf::PdfDocument;

/// Untagged page: a bullet glyph column at x=72 and a body column at x=96, four
/// rows. `/ToUnicode` maps `*` → U+2022 BULLET so the cells are lone bullets.
fn bulleted_list_pdf() -> Vec<u8> {
    let tounicode = b"\
/CIDInit /ProcSet findresource begin
12 dict begin begincmap
1 begincodespacerange <00> <FF> endcodespacerange
1 beginbfchar
<2A> <2022>
endbfchar
endcmap CMapName currentdict /CMap defineresource pop end end";

    let content = b"BT /F1 12 Tf\n\
        1 0 0 1 72 700 Tm (*) Tj 1 0 0 1 96 700 Tm (Ship the API) Tj\n\
        1 0 0 1 72 684 Tm (*) Tj 1 0 0 1 96 684 Tm (Write the docs) Tj\n\
        1 0 0 1 72 668 Tm (*) Tj 1 0 0 1 96 668 Tm (Ship the release) Tj\n\
        1 0 0 1 72 652 Tm (*) Tj 1 0 0 1 96 652 Tm (Tag the build) Tj\n\
        ET\n";

    let mut buf: Vec<u8> = Vec::new();
    let mut off = vec![0usize; 7];
    let obj = |buf: &mut Vec<u8>, off: &mut Vec<usize>, id: usize, body: &str| {
        off[id] = buf.len();
        buf.extend_from_slice(format!("{id} 0 obj\n{body}\nendobj\n").as_bytes());
    };
    let stream = |buf: &mut Vec<u8>, off: &mut Vec<usize>, id: usize, dict: &str, data: &[u8]| {
        off[id] = buf.len();
        buf.extend_from_slice(format!("{id} 0 obj\n<< {dict} /Length {} >>\nstream\n", data.len()).as_bytes());
        buf.extend_from_slice(data);
        buf.extend_from_slice(b"\nendstream\nendobj\n");
    };

    buf.extend_from_slice(b"%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
    obj(&mut buf, &mut off, 1, "<< /Type /Catalog /Pages 2 0 R >>");
    obj(&mut buf, &mut off, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    obj(
        &mut buf,
        &mut off,
        3,
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] \
         /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    );
    stream(&mut buf, &mut off, 4, "", content);
    obj(
        &mut buf,
        &mut off,
        5,
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica \
         /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
    );
    stream(&mut buf, &mut off, 6, "", tounicode);

    let xref = buf.len();
    buf.extend_from_slice(b"xref\n0 7\n0000000000 65535 f \n");
    for offset in &off[1..=6] {
        buf.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    buf.extend_from_slice(b"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n");
    buf.extend_from_slice(format!("{xref}\n%%EOF\n").as_bytes());
    buf
}

#[test]
fn untagged_bulleted_list_is_not_rendered_as_table() {
    let doc = PdfDocument::from_bytes(bulleted_list_pdf()).unwrap();
    let text = doc.extract_text(0).unwrap();
    assert!(text.contains("Ship the API"), "list body not extracted: {text:?}");

    // The bullet column must not be detected as a table. ~keep
    let tables = doc.extract_tables(0).unwrap();
    assert!(
        tables.is_empty(),
        "bulleted list was wrongly detected as {} table(s)",
        tables.len()
    );
}
