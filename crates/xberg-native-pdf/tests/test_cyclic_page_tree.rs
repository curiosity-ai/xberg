//! Test for cyclic page tree detection.
//! Verifies that circular Kids references produce an error instead of stack overflow.

use xberg_native_pdf::document::PdfDocument;

mod common;

/// Build a PDF where the Pages node's Kids array references itself.
fn build_pdf_cyclic_page_tree() -> Vec<u8> {
    b"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj

2 0 obj
<< /Type /Pages /Kids [2 0 R] /Count 1 >>
endobj

xref
0 3
0000000000 65535 f \r
0000000009 00000 n \r
0000000058 00000 n \r
trailer
<< /Size 3 /Root 1 0 R >>
startxref
120
%%EOF
"
    .to_vec()
}

#[test]
fn test_cyclic_page_tree_no_stack_overflow() {
    let data = build_pdf_cyclic_page_tree();
    let (_path_dir, path) = common::write_temp_pdf(&data, "cyclic_page_tree.pdf");
    let doc = PdfDocument::open(&path).expect("Should parse PDF structure");
    let result = doc.get_page_content_data(0);
    assert!(result.is_err(), "Expected error for cyclic page tree");
}
