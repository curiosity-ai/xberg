//! Integration tests for XObject path extraction.
//! Tests recursive Form XObject processing in `extract_paths`, including
//! cycle detection for self-referencing Form XObjects.
//!
//! Uses minimal synthetic PDFs built in code, per `fixture-hygiene`.

mod common;

use xberg_native_pdf::document::PdfDocument;

/// A Form XObject that invokes itself via `Do` must not hang: cycle
/// detection has to terminate the recursion and `extract_paths` must still
/// return (with the paths gathered before the cycle was cut).
#[test]
fn test_xobject_path_extraction_no_hang() {
    let form_content = b"0 0 m 50 50 l S /X1 Do";
    let page_content = b"/X1 Do";

    let mut pdf = b"%PDF-1.4\n".to_vec();
    let mut offsets = vec![0usize];

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] \
          /Contents 4 0 R /Resources << /XObject << /X1 5 0 R >> >> >>\nendobj\n",
    );

    offsets.push(pdf.len());
    pdf.extend_from_slice(format!("4 0 obj\n<< /Length {} >>\nstream\n", page_content.len()).as_bytes());
    pdf.extend_from_slice(page_content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        format!(
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] \
             /Resources << /XObject << /X1 5 0 R >> >> /Length {} >>\nstream\n",
            form_content.len()
        )
        .as_bytes(),
    );
    pdf.extend_from_slice(form_content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    let pdf = common::finalize_pdf(pdf, &offsets);
    let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

    // If cycle detection is broken, this call hangs and the test times out
    // rather than failing cleanly — that in itself is the regression signal. ~keep
    let paths = doc.extract_paths(0).expect("extract_paths must not hang or error");

    assert_eq!(
        paths.len(),
        1,
        "the self-referencing Form XObject should contribute exactly one \
         path (drawn once, before the cycle is cut)"
    );
}

/// `extract_paths` must recurse into non-cyclic Form XObjects and recover
/// paths drawn inside them, attributed back to the invoking page.
#[test]
fn test_extract_paths_from_form_xobject() {
    let form_content = b"10 10 m 90 90 l S";
    let page_content = b"/X1 Do";

    let mut pdf = b"%PDF-1.4\n".to_vec();
    let mut offsets = vec![0usize];

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] \
          /Contents 4 0 R /Resources << /XObject << /X1 5 0 R >> >> >>\nendobj\n",
    );

    offsets.push(pdf.len());
    pdf.extend_from_slice(format!("4 0 obj\n<< /Length {} >>\nstream\n", page_content.len()).as_bytes());
    pdf.extend_from_slice(page_content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    offsets.push(pdf.len());
    pdf.extend_from_slice(
        format!(
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Length {} >>\nstream\n",
            form_content.len()
        )
        .as_bytes(),
    );
    pdf.extend_from_slice(form_content);
    pdf.extend_from_slice(b"\nendstream\nendobj\n");

    let pdf = common::finalize_pdf(pdf, &offsets);
    let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

    let paths = doc.extract_paths(0).expect("extract_paths");
    assert_eq!(paths.len(), 1, "expected the single path drawn inside the Form XObject");
    assert!(paths[0].has_stroke(), "line drawn with `S` must be stroked");
}
