//! Integration tests for form field extraction and checkbox text leak prevention.

mod common;

use std::io::Write;
use tempfile::NamedTempFile;
use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::extractors::forms::{FieldType, FieldValue, FormExtractor};

/// Create a test PDF with various AcroForm field types (merged field+widget
/// objects, ISO 32000-1 §12.7.4.1) and return its bytes.
///
/// `name` and `ssn` are read directly via `/FT /Tx` + `/V` by the widget
/// text-extraction path (`document.rs`'s `Some("Tx")` arm) -- no appearance
/// stream needed. Same for `agree`/`newsletter` (`Some("Btn")` arm reads
/// `/V` to decide "[x]" vs nothing) and `country`/`interests` (`Some("Ch")`
/// arm reads `/V`/`/Opt`).
fn create_form_pdf_bytes() -> Vec<u8> {
    let objs: [String; 9] = [
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 10 0 R >>".to_string(),
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>".to_string(),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] \
         /Annots [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R 9 0 R] >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (John Doe) \
         /Rect [72 700 272 720] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (123-45-6789) \
         /Ff 1 /MaxLen 11 /Rect [72 670 222 690] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes \
         /Rect [72 640 87 655] /P 3 0 R >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (newsletter) \
         /Rect [72 610 87 625] /P 3 0 R >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Ch /T (country) /V (USA) \
         /Opt [(USA) (Canada) (UK)] /Rect [72 580 222 600] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>"
            .to_string(),
        "<< /Type /Annot /Subtype /Widget /FT /Ch /T (interests) /Ff 2097152 \
         /Opt [(Sports) (Music) (Art) (Technology)] /Rect [72 500 222 580] /P 3 0 R \
         /DA (/Helv 12 Tf 0 g) >>"
            .to_string(),
    ];
    let acroform = "<< /Fields [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R 9 0 R] /DA (/Helv 12 Tf 0 g) \
         /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>";

    let mut buf: Vec<u8> = b"%PDF-1.7\n".to_vec();
    let mut offsets = vec![0usize];
    for body in &objs {
        offsets.push(buf.len());
        buf.extend_from_slice(format!("{} 0 obj\n{}\nendobj\n", offsets.len() - 1, body).as_bytes());
    }
    offsets.push(buf.len());
    buf.extend_from_slice(format!("{} 0 obj\n{}\nendobj\n", offsets.len() - 1, acroform).as_bytes());

    common::finalize_pdf(buf, &offsets)
}

/// Helper to write bytes to a temp file and open as PdfDocument.
fn open_pdf_from_bytes(bytes: &[u8]) -> (NamedTempFile, PdfDocument) {
    let mut temp = NamedTempFile::new().expect("Failed to create temp file");
    temp.write_all(bytes).expect("Failed to write temp file");
    let doc = PdfDocument::open(temp.path().to_str().unwrap()).expect("Failed to open test PDF");
    (temp, doc)
}

#[test]
fn test_extract_form_fields_basic() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");

    assert!(fields.len() >= 6, "Expected at least 6 fields, got {}", fields.len());
}

#[test]
fn test_extract_text_field_value() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");

    let name_field = fields.iter().find(|f| f.full_name == "name");
    assert!(name_field.is_some(), "Should find 'name' field");
    let name_field = name_field.unwrap();

    assert_eq!(name_field.field_type, FieldType::Text);
    assert_eq!(name_field.value, FieldValue::Text("John Doe".to_string()));
}

#[test]
fn test_extract_text_field_readonly_flag() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");

    let ssn_field = fields.iter().find(|f| f.full_name == "ssn");
    assert!(ssn_field.is_some(), "Should find 'ssn' field");
    let ssn_field = ssn_field.unwrap();

    assert!(
        ssn_field.flags.is_some_and(|f| f & 1 != 0),
        "SSN field should be read-only"
    );
}

#[test]
fn test_extract_checkbox_field() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");

    let agree_field = fields.iter().find(|f| f.full_name == "agree");
    assert!(agree_field.is_some(), "Should find 'agree' checkbox");
    let agree_field = agree_field.unwrap();

    assert_eq!(agree_field.field_type, FieldType::Button);
}

#[test]
fn test_extract_choice_field() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");

    let country_field = fields.iter().find(|f| f.full_name == "country");
    assert!(country_field.is_some(), "Should find 'country' choice field");
    let country_field = country_field.unwrap();

    assert_eq!(country_field.field_type, FieldType::Choice);
}

#[test]
fn test_extract_no_form_fields_on_plain_pdf() {
    let bytes = common::build_minimal_pdf_raw(
        b"BT /F1 12 Tf 1 0 0 1 72 700 Tm (No forms here) Tj ET",
        b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
    );
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");
    assert!(fields.is_empty(), "Plain PDF should have no form fields");
}

#[test]
fn test_form_field_has_bounds() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");

    let with_bounds = fields.iter().filter(|f| f.bounds.is_some()).count();
    assert!(with_bounds > 0, "At least some fields should have bounding boxes");
}

#[test]
fn test_checkbox_does_not_leak_off_into_text() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    let tokens: Vec<&str> = text.split_whitespace().collect();
    assert!(
        !tokens.contains(&"Off"),
        "Extracted text should not contain checkbox 'Off' state.\nGot: {}",
        text
    );
}

#[test]
fn test_checkbox_does_not_leak_yes_into_text() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    let tokens: Vec<&str> = text.split_whitespace().collect();
    assert!(
        !tokens.contains(&"Yes"),
        "Extracted text should not contain checkbox 'Yes' state.\nGot: {}",
        text
    );
}

#[test]
fn test_checkbox_does_not_leak_zapf_dingbats() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    assert!(
        !text.contains('\u{2714}'),
        "Extracted text should not contain ZapfDingbats checkmark.\nGot: {}",
        text
    );
}

#[test]
fn test_text_field_values_may_appear_in_text() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let _text = doc.extract_text(0).expect("Failed to extract text");
    let _fields = FormExtractor::extract_fields(&doc).expect("Failed to extract fields");
}

#[test]
fn test_has_xfa_on_non_xfa_pdf() {
    let bytes = create_form_pdf_bytes();
    let (_temp, mut doc) = open_pdf_from_bytes(&bytes);

    let has_xfa = xberg_native_pdf::xfa::XfaExtractor::has_xfa(&mut doc).expect("Failed to check XFA");

    assert!(!has_xfa, "AcroForm-only form should not have XFA");
}

#[test]
fn test_has_xfa_on_plain_pdf() {
    let bytes = common::build_minimal_pdf_raw(
        b"BT /F1 12 Tf 1 0 0 1 72 700 Tm (No forms) Tj ET",
        b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
    );
    let (_temp, mut doc) = open_pdf_from_bytes(&bytes);

    let has_xfa = xberg_native_pdf::xfa::XfaExtractor::has_xfa(&mut doc).expect("Failed to check XFA");

    assert!(!has_xfa, "Plain text PDF should not have XFA");
}

#[test]
fn test_extract_text_form_fields_inline() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    assert!(
        text.contains("John Doe"),
        "Text field value 'John Doe' should appear in extracted text.\nGot: {}",
        text
    );
    assert!(
        text.contains("123-45-6789"),
        "Text field value '123-45-6789' should appear in extracted text.\nGot: {}",
        text
    );
}

#[test]
fn test_widget_spans_checkbox_checked() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    assert!(
        text.contains("[x]"),
        "Checked checkbox should render as '[x]' in extracted text.\nGot: {}",
        text
    );
}

#[test]
fn test_widget_spans_checkbox_unchecked() {
    // An UNCHECKED checkbox carries no text and must NOT emit a "[ ]" marker
    // (CORPUS-1): that synthetic noise made xberg-native-pdf diverge from pdftotext /
    // PyMuPDF on AcroForm-heavy PDFs. Only the meaningful checked state "[x]"
    // is surfaced (see test_widget_spans_checkbox_checked). ~keep
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    assert!(
        !text.contains("[ ]"),
        "Unchecked checkbox must NOT emit a '[ ]' marker (noise).\nGot: {}",
        text
    );
}

#[test]
fn test_widget_spans_choice_field() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");

    assert!(
        text.contains("USA"),
        "Choice field value 'USA' should appear in extracted text.\nGot: {}",
        text
    );
}

#[test]
fn test_parse_font_size_from_da() {
    let bytes = create_form_pdf_bytes();
    let (_temp, doc) = open_pdf_from_bytes(&bytes);

    let text = doc.extract_text(0).expect("Failed to extract text");
    assert!(!text.is_empty(), "Extracted text should not be empty");
}
