//! Regression coverage for native two-column PDF reading order.

#![cfg(feature = "pdf")]

mod helpers;
use helpers::extract_bytes_document_blocking;

use xberg::core::config::{ExtractionConfig, OutputFormat, PdfConfig};

fn make_two_column_pdf() -> Vec<u8> {
    let stream = "\
BT /F1 11 Tf 1 0 0 1 60 712 Tm (The committee reviewed the annual) Tj ET\n\
BT /F1 11 Tf 1 0 0 1 60 698 Tm (report and) Tj ET\n\
BT /F1 11 Tf 1 0 0 1 330 712 Tm (approved the budget for the) Tj ET\n\
BT /F1 11 Tf 1 0 0 1 330 698 Tm (coming fiscal year.) Tj ET\n";
    let mut pdf = Vec::new();

    macro_rules! push {
        ($value:expr) => {
            pdf.extend_from_slice($value.as_bytes())
        };
    }

    push!("%PDF-1.4\n");
    let catalog_offset = pdf.len();
    push!("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
    let pages_offset = pdf.len();
    push!("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
    let page_offset = pdf.len();
    push!(
        "3 0 obj\n\
         << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]\n\
         /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\n\
         endobj\n"
    );
    let content_offset = pdf.len();
    push!(format!("4 0 obj\n<< /Length {} >>\nstream\n", stream.len()));
    push!(stream);
    push!("endstream\nendobj\n");
    let font_offset = pdf.len();
    push!("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
    let xref_offset = pdf.len();
    push!(format!(
        "xref\n0 6\n\
         0000000000 65535 f \r\n\
         {catalog_offset:010} 00000 n \r\n\
         {pages_offset:010} 00000 n \r\n\
         {page_offset:010} 00000 n \r\n\
         {content_offset:010} 00000 n \r\n\
         {font_offset:010} 00000 n \r\n\
         trailer\n<< /Size 6 /Root 1 0 R >>\n\
         startxref\n{xref_offset}\n%%EOF\n"
    ));
    pdf
}

fn normalized_content(config: &ExtractionConfig) -> String {
    extract_bytes_document_blocking(&make_two_column_pdf(), "application/pdf", config)
        .expect("two-column PDF extraction must succeed")
        .content
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

#[test]
fn native_two_column_pdf_uses_column_block_reading_order() {
    let expected = "The committee reviewed the annual report and approved the budget for the coming fiscal year.";

    assert_eq!(normalized_content(&ExtractionConfig::default()), expected);
}

#[test]
fn explicit_reading_order_uses_column_block_reading_order() {
    let config = ExtractionConfig {
        pdf_options: Some(PdfConfig {
            reading_order: true,
            ..PdfConfig::default()
        }),
        ..ExtractionConfig::default()
    };
    let expected = "The committee reviewed the annual report and approved the budget for the coming fiscal year.";

    assert_eq!(normalized_content(&config), expected);
}

#[test]
fn markdown_two_column_pdf_uses_column_block_reading_order() {
    let config = ExtractionConfig {
        output_format: OutputFormat::Markdown,
        ..ExtractionConfig::default()
    };
    let expected = "The committee reviewed the annual report and approved the budget for the coming fiscal year.";

    assert_eq!(normalized_content(&config), expected);
}
