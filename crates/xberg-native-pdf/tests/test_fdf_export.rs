//! Integration tests for FDF and XFDF export functionality.
//!
//! Tests the export of form field data to FDF (Forms Data Format) and
//! XFDF (XML Forms Data Format) per ISO 32000-1:2008 Section 12.7.7.
//!
//! The `FdfWriter`/`XfdfWriter` unit tests below have no editor/writer
//! dependency at all -- `crate::fdf` is a read-adjacent export module used
//! by `extractors::forms::FormExtractor::export_fdf`/`export_xfdf`
//! (`src/extractors/forms.rs`), which is a live, non-editor entry point.
//! The end-to-end export tests previously drove that same code through
//! `DocumentEditor::export_form_data_fdf`/`export_form_data_xfdf`, filling
//! the source PDF's fields via the (now-removed) writer first; they are
//! rewritten here onto `FormExtractor::export_fdf`/`export_xfdf` against a
//! hand-built AcroForm PDF (merged field+widget objects, ISO 32000-1
//! §12.7.4.1), matching the pattern in `tests/test_form_extraction.rs`.

use tempfile::tempdir;
use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::extractors::forms::FormExtractor;
use xberg_native_pdf::fdf::{FdfField, FdfValue, FdfWriter, XfdfWriter};

#[test]
fn test_fdf_writer_basic() {
    let mut writer = FdfWriter::new();
    writer.add_field(FdfField::new("name", FdfValue::Text("John Doe".into())));
    writer.add_field(FdfField::new("email", FdfValue::Text("john@example.com".into())));

    let bytes = writer.to_bytes().unwrap();
    let content = String::from_utf8_lossy(&bytes);

    assert!(content.contains("%FDF-1.2"));
    assert!(content.contains("/Fields"));
    assert!(content.contains("/T (name)"));
    assert!(content.contains("/V (John Doe)"));
    assert!(content.contains("/T (email)"));
    assert!(content.contains("/V (john@example.com)"));
    assert!(content.contains("%%EOF"));
}

#[test]
fn test_fdf_writer_with_file_spec() {
    let writer = FdfWriter::new().with_file_spec("original.pdf");
    let bytes = writer.to_bytes().unwrap();
    let content = String::from_utf8_lossy(&bytes);

    assert!(content.contains("/F (original.pdf)"));
}

#[test]
fn test_fdf_writer_boolean_values() {
    let mut writer = FdfWriter::new();
    writer.add_field(FdfField::new("agree", FdfValue::Boolean(true)));
    writer.add_field(FdfField::new("decline", FdfValue::Boolean(false)));

    let bytes = writer.to_bytes().unwrap();
    let content = String::from_utf8_lossy(&bytes);

    assert!(content.contains("/V /Yes"));
    assert!(content.contains("/V /Off"));
}

#[test]
fn test_fdf_writer_name_values() {
    let mut writer = FdfWriter::new();
    writer.add_field(FdfField::new("choice", FdfValue::Name("Option1".into())));

    let bytes = writer.to_bytes().unwrap();
    let content = String::from_utf8_lossy(&bytes);

    assert!(content.contains("/V /Option1"));
}

#[test]
fn test_fdf_writer_array_values() {
    let mut writer = FdfWriter::new();
    writer.add_field(FdfField::new(
        "multi",
        FdfValue::Array(vec!["Choice A".into(), "Choice B".into()]),
    ));

    let bytes = writer.to_bytes().unwrap();
    let content = String::from_utf8_lossy(&bytes);

    assert!(content.contains("/V [ (Choice A) (Choice B) ]"));
}

#[test]
fn test_fdf_writer_special_chars() {
    let mut writer = FdfWriter::new();
    writer.add_field(FdfField::new("note", FdfValue::Text("Hello (World)".into())));

    let bytes = writer.to_bytes().unwrap();
    let content = String::from_utf8_lossy(&bytes);

    // Parentheses should be escaped ~keep
    assert!(content.contains("/V (Hello \\(World\\))"));
}

#[test]
fn test_fdf_write_to_file() {
    let temp_dir = tempdir().unwrap();
    let output_path = temp_dir.path().join("test.fdf");

    let mut writer = FdfWriter::new();
    writer.add_field(FdfField::new("test", FdfValue::Text("value".into())));
    writer.write_to_file(&output_path).unwrap();

    assert!(output_path.exists());

    let content = String::from_utf8_lossy(&std::fs::read(&output_path).unwrap()).to_string();
    assert!(content.contains("%FDF-1.2"));
    assert!(content.contains("/T (test)"));
}

#[test]
fn test_xfdf_writer_basic() {
    let mut writer = XfdfWriter::new();
    writer.add_field("name", "John Doe");
    writer.add_field("email", "john@example.com");

    let xml = writer.to_xml();

    assert!(xml.contains("<?xml version=\"1.0\""));
    assert!(xml.contains("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\""));
    assert!(xml.contains("<fields>"));
    assert!(xml.contains("<field name=\"name\">"));
    assert!(xml.contains("<value>John Doe</value>"));
    assert!(xml.contains("<field name=\"email\">"));
    assert!(xml.contains("<value>john@example.com</value>"));
    assert!(xml.contains("</xfdf>"));
}

#[test]
fn test_xfdf_writer_with_file_spec() {
    let writer = XfdfWriter::new().with_file_spec("original.pdf");
    let xml = writer.to_xml();

    assert!(xml.contains("<f href=\"original.pdf\"/>"));
}

#[test]
fn test_xfdf_writer_xml_escaping() {
    let mut writer = XfdfWriter::new();
    writer.add_field("company", "Smith & Jones <Consulting>");

    let xml = writer.to_xml();

    assert!(xml.contains("<value>Smith &amp; Jones &lt;Consulting&gt;</value>"));
}

#[test]
fn test_xfdf_writer_boolean_values() {
    let mut writer = XfdfWriter::new();
    writer.add_fdf_field(FdfField::new("agree", FdfValue::Boolean(true)));
    writer.add_fdf_field(FdfField::new("decline", FdfValue::Boolean(false)));

    let xml = writer.to_xml();

    assert!(xml.contains("<field name=\"agree\">"));
    assert!(xml.contains("<value>Yes</value>"));
    assert!(xml.contains("<field name=\"decline\">"));
    assert!(xml.contains("<value>Off</value>"));
}

#[test]
fn test_xfdf_writer_hierarchical() {
    let mut writer = XfdfWriter::new();
    let parent = FdfField::new("address", FdfValue::None)
        .with_kid(FdfField::new("street", FdfValue::Text("123 Main St".into())))
        .with_kid(FdfField::new("city", FdfValue::Text("Anytown".into())));
    writer.add_fdf_field(parent);

    let xml = writer.to_xml();

    assert!(xml.contains("<field name=\"address\">"));
    assert!(xml.contains("<field name=\"street\">"));
    assert!(xml.contains("<value>123 Main St</value>"));
    assert!(xml.contains("<field name=\"city\">"));
    assert!(xml.contains("<value>Anytown</value>"));
}

#[test]
fn test_xfdf_write_to_file() {
    let temp_dir = tempdir().unwrap();
    let output_path = temp_dir.path().join("test.xfdf");

    let mut writer = XfdfWriter::new();
    writer.add_field("test", "value");
    writer.write_to_file(&output_path).unwrap();

    assert!(output_path.exists());

    let content = std::fs::read_to_string(&output_path).unwrap();
    assert!(content.contains("<?xml version=\"1.0\""));
    assert!(content.contains("<field name=\"test\">"));
}

/// Build a one-page AcroForm PDF (merged field+widget objects, ISO
/// 32000-1 §12.7.4.1) with a filled text field and a checked checkbox.
/// Hand-built rather than via the (now-removed) writer/editor -- see
/// `tests/test_form_extraction.rs` for the same technique.
fn form_pdf_with_fields() -> Vec<u8> {
    let objs: [String; 5] = [
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>".to_string(),
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>".to_string(),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] >>".to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (username) /V (test_user) \
         /Rect [100 700 300 720] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes \
         /Rect [100 660 120 680] /P 3 0 R >>"
            .to_string(),
    ];
    let acroform = "<< /Fields [4 0 R 5 0 R] /DA (/Helv 12 Tf 0 g) \
         /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>";

    let mut buf: Vec<u8> = b"%PDF-1.7\n".to_vec();
    let mut offsets = vec![0usize];
    for body in &objs {
        offsets.push(buf.len());
        buf.extend_from_slice(format!("{} 0 obj\n{}\nendobj\n", offsets.len() - 1, body).as_bytes());
    }
    offsets.push(buf.len());
    buf.extend_from_slice(format!("{} 0 obj\n{}\nendobj\n", offsets.len() - 1, acroform).as_bytes());

    let xref_pos = buf.len();
    buf.extend_from_slice(format!("xref\n0 {}\n", offsets.len()).as_bytes());
    buf.extend_from_slice(b"0000000000 65535 f \n");
    for &off in &offsets[1..] {
        buf.extend_from_slice(format!("{off:010} 00000 n \n").as_bytes());
    }
    buf.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
            offsets.len(),
            xref_pos
        )
        .as_bytes(),
    );
    buf
}

fn plain_pdf_without_forms() -> Vec<u8> {
    std::fs::read("tests/fixtures/simple.pdf").expect("simple.pdf fixture")
}

#[test]
fn test_export_fdf_via_form_extractor() {
    let temp_dir = tempdir().unwrap();
    let fdf_path = temp_dir.path().join("export.fdf");

    let doc = PdfDocument::from_bytes(form_pdf_with_fields()).unwrap();
    FormExtractor::export_fdf(&doc, &fdf_path).unwrap();

    let content = String::from_utf8_lossy(&std::fs::read(&fdf_path).unwrap()).to_string();
    assert!(content.contains("%FDF-1.2"));
    assert!(content.contains("/Fields"));
}

#[test]
fn test_export_xfdf_via_form_extractor() {
    let temp_dir = tempdir().unwrap();
    let xfdf_path = temp_dir.path().join("export.xfdf");

    let doc = PdfDocument::from_bytes(form_pdf_with_fields()).unwrap();
    FormExtractor::export_xfdf(&doc, &xfdf_path).unwrap();

    let content = std::fs::read_to_string(&xfdf_path).unwrap();
    assert!(content.contains("<?xml version=\"1.0\""));
    assert!(content.contains("<xfdf"));
    assert!(content.contains("<fields>"));
}

#[test]
fn test_export_from_pdf_without_forms() {
    let temp_dir = tempdir().unwrap();
    let fdf_path = temp_dir.path().join("empty.fdf");
    let xfdf_path = temp_dir.path().join("empty.xfdf");

    let doc = PdfDocument::from_bytes(plain_pdf_without_forms()).unwrap();

    FormExtractor::export_fdf(&doc, &fdf_path).unwrap();
    FormExtractor::export_xfdf(&doc, &xfdf_path).unwrap();

    assert!(fdf_path.exists());
    assert!(xfdf_path.exists());

    let fdf_content = String::from_utf8_lossy(&std::fs::read(&fdf_path).unwrap()).to_string();
    assert!(fdf_content.contains("%FDF-1.2"));
    assert!(fdf_content.contains("/Fields ["));

    let xfdf_content = std::fs::read_to_string(&xfdf_path).unwrap();
    assert!(xfdf_content.contains("<fields>"));
    assert!(xfdf_content.contains("</fields>"));
}

#[test]
fn test_fdf_round_trip_consistency() {
    let temp_dir = tempdir().unwrap();
    let fdf_path1 = temp_dir.path().join("export1.fdf");
    let fdf_path2 = temp_dir.path().join("export2.fdf");

    let bytes = form_pdf_with_fields();
    let doc1 = PdfDocument::from_bytes(bytes.clone()).unwrap();
    let doc2 = PdfDocument::from_bytes(bytes).unwrap();

    FormExtractor::export_fdf(&doc1, &fdf_path1).unwrap();
    FormExtractor::export_fdf(&doc2, &fdf_path2).unwrap();

    let content1 = String::from_utf8_lossy(&std::fs::read(&fdf_path1).unwrap()).to_string();
    let content2 = String::from_utf8_lossy(&std::fs::read(&fdf_path2).unwrap()).to_string();
    assert_eq!(content1, content2);
}

#[test]
fn test_xfdf_round_trip_consistency() {
    let temp_dir = tempdir().unwrap();
    let xfdf_path1 = temp_dir.path().join("export1.xfdf");
    let xfdf_path2 = temp_dir.path().join("export2.xfdf");

    let bytes = form_pdf_with_fields();
    let doc1 = PdfDocument::from_bytes(bytes.clone()).unwrap();
    let doc2 = PdfDocument::from_bytes(bytes).unwrap();

    FormExtractor::export_xfdf(&doc1, &xfdf_path1).unwrap();
    FormExtractor::export_xfdf(&doc2, &xfdf_path2).unwrap();

    let content1 = std::fs::read_to_string(&xfdf_path1).unwrap();
    let content2 = std::fs::read_to_string(&xfdf_path2).unwrap();
    assert_eq!(content1, content2);
}
