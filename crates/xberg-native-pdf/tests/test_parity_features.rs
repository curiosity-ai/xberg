// ~keep: test/bench binaries print by design; org logging policy exempts tests
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)]
mod common;

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::layout::RectFilterMode;

fn pdf_from_text(text: &str) -> PdfDocument {
    let content = common::text_run_op(text, 72.0, 700.0, "Helvetica", 12.0);
    let bytes =
        common::build_pdf_with_standard_fonts(content.as_bytes(), b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
    PdfDocument::from_bytes(bytes).unwrap()
}

#[test]
fn test_word_extraction() {
    let doc = pdf_from_text("Hello World");
    let words = doc.extract_words(0).unwrap();

    println!(
        "Extracted words: {:?}",
        words.iter().map(|w| &w.text).collect::<Vec<_>>()
    );

    assert!(words.len() >= 2, "Expected at least 2 words, found {}", words.len());
    let texts: Vec<String> = words.iter().map(|w| w.text.trim().to_string()).collect();
    assert!(
        texts.iter().any(|t| t == "Hello"),
        "Could not find 'Hello' in {:?}",
        texts
    );
    assert!(
        texts.iter().any(|t| t == "World"),
        "Could not find 'World' in {:?}",
        texts
    );
}

#[test]
fn test_line_extraction() {
    let mut content = String::new();
    content.push_str(&common::text_run_op("Line One", 72.0, 700.0, "Helvetica", 12.0));
    content.push_str(&common::text_run_op("Line Two", 72.0, 650.0, "Helvetica", 12.0));
    content.push_str(&common::text_run_op("Line Three", 72.0, 600.0, "Helvetica", 12.0));
    let doc = PdfDocument::from_bytes(common::build_pdf_with_standard_fonts(
        content.as_bytes(),
        b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
    ))
    .unwrap();
    let lines = doc.extract_text_lines(0).unwrap();

    println!(
        "Extracted lines: {:?}",
        lines.iter().map(|l| &l.text).collect::<Vec<_>>()
    );

    assert!(lines.len() >= 3, "Expected at least 3 lines, found {}", lines.len());
    let texts: Vec<String> = lines.iter().map(|l| l.text.clone()).collect();
    assert!(texts.iter().any(|t| t.contains("Line One")));
    assert!(texts.iter().any(|t| t.contains("Line Two")));
}

#[test]
fn test_rect_and_line_extraction_empty() {
    let doc = pdf_from_text("Test");
    let rects = doc.extract_rects(0).unwrap();
    let lines = doc.extract_lines(0).unwrap();

    assert!(rects.is_empty());
    assert!(lines.is_empty());
}

#[test]
fn test_table_extraction_basic() {
    // A 2-column x 2-row stroke-bordered grid (outer rect + one vertical +
    // one horizontal divider), standing in for the markdown-table PDF the
    // (now-removed) `Pdf::from_markdown` used to produce. `extract_tables`
    // uses `TableDetectionConfig::default()` (min_table_cells=4,
    // min_table_columns=2, `Both` line+text strategy), which this 4-cell
    // bordered grid satisfies via the line-based path. ~keep
    let mut content = String::new();
    content.push_str("1 w\n0 0 0 RG\n");
    content.push_str("100 650 300 50 re S\n");
    content.push_str("250 650 m 250 700 l S\n");
    content.push_str("100 675 m 400 675 l S\n");
    content.push_str(&common::text_run_op("Col1", 105.0, 683.0, "Helvetica", 10.0));
    content.push_str(&common::text_run_op("Col2", 255.0, 683.0, "Helvetica", 10.0));
    content.push_str(&common::text_run_op("Val1", 105.0, 658.0, "Helvetica", 10.0));
    content.push_str(&common::text_run_op("Val2", 255.0, 658.0, "Helvetica", 10.0));

    let doc = PdfDocument::from_bytes(common::build_pdf_with_standard_fonts(
        content.as_bytes(),
        b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
    ))
    .unwrap();

    let spans = doc.extract_spans(0).unwrap();
    println!("Spans found: {}", spans.len());
    for s in &spans {
        println!("  '{}' at {:?}", s.text, s.bbox);
    }

    let tables = doc.extract_tables(0).unwrap();

    assert!(!tables.is_empty(), "No tables detected in bordered-grid PDF");
}

#[test]
fn test_area_filtered_extraction() {
    let mut content = String::new();
    content.push_str(&common::text_run_op("Top Text", 72.0, 720.0, "Helvetica", 12.0));
    content.push_str(&common::text_run_op("Bottom Text", 72.0, 100.0, "Helvetica", 12.0));
    let doc = PdfDocument::from_bytes(common::build_pdf_with_standard_fonts(
        content.as_bytes(),
        b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
    ))
    .unwrap();

    let chars = doc.extract_chars(0).unwrap();
    println!("Chars found: {}", chars.len());
    for c in &chars {
        println!("  '{}' at {:?}", c.char, c.bbox);
    }

    // Extract only from top region
    // Margin top is usually 72.0 (1 inch)
    // Page height is 792.0
    // start_y is 792 - 72 = 720.0 ~keep
    let top_rect = xberg_native_pdf::geometry::Rect::new(0.0, 700.0, 612.0, 92.0);
    let top_text = doc
        .extract_text_in_rect(0, top_rect, RectFilterMode::Intersects)
        .unwrap();
    println!("Top text: '{}'", top_text);

    assert!(top_text.contains("Top Text"));
    assert!(!top_text.contains("Bottom Text"));

    let bottom_rect = xberg_native_pdf::geometry::Rect::new(0.0, 0.0, 612.0, 650.0);
    let bottom_text = doc
        .extract_text_in_rect(0, bottom_rect, RectFilterMode::Intersects)
        .unwrap();
    println!("Bottom text: '{}'", bottom_text);

    assert!(!bottom_text.contains("Top Text"));
    assert!(bottom_text.contains("Bottom Text"));
}

#[test]
fn test_within_harmonized_api() {
    let doc = pdf_from_text("Scoped Content");
    let rect = xberg_native_pdf::geometry::Rect::new(0.0, 0.0, 612.0, 792.0);

    let text = doc.extract_text_in_rect(0, rect, RectFilterMode::Intersects).unwrap();
    assert!(text.contains("Scoped Content"));

    let words = doc.extract_words_in_rect(0, rect, RectFilterMode::Intersects).unwrap();
    assert!(!words.is_empty());
}

#[test]
fn test_image_metadata_extraction() {
    let doc = pdf_from_text("No Images");
    let images = doc.extract_images(0).unwrap();
    assert!(images.is_empty());
}
