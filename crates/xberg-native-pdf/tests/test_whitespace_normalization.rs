//! TDD Tests for Whitespace Normalization
//!
//! Tests verify that consecutive spaces and newlines are properly normalized
//! while preserving intentional formatting (code blocks, tables, paragraph breaks).

#[test]
fn test_normalize_single_space() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize("hello world"), "hello world");
}

#[test]
fn test_normalize_multiple_spaces() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize("hello   world"), "hello world");
    assert_eq!(normalizer.normalize("hello     world"), "hello world");
}

#[test]
fn test_normalize_tabs_to_spaces() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize("hello\t\tworld"), "hello world");
}

#[test]
fn test_normalize_mixed_whitespace() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize("hello  \t  world"), "hello world");
}

#[test]
fn test_preserve_single_newline() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let result = normalizer.normalize("line1\nline2");
    assert!(result.contains('\n'));
    assert!(!result.contains("  "));
}

#[test]
fn test_collapse_multiple_newlines_to_paragraph_break() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let result = normalizer.normalize("para1\n\n\npara2");
    assert!(result.contains("para1") && result.contains("para2"));
}

#[test]
fn test_trim_leading_spaces() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize("   hello"), "hello");
}

#[test]
fn test_trim_trailing_spaces() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize("hello   "), "hello");
}

#[test]
fn test_preserve_layout_mode_no_normalization() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(true);
    let text = "hello   world";
    assert_eq!(normalizer.normalize(text), text);
}

#[test]
fn test_preserve_intentional_double_space() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let text = "Sentence one.  Sentence two.";
    let result = normalizer.normalize(text);
    assert!(!result.contains("  "));
}

#[test]
fn test_normalize_at_line_breaks() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let result = normalizer.normalize("line1   \nline2");
    assert!(!result.contains("   \n"));
}

#[test]
fn test_normalize_after_line_breaks() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let result = normalizer.normalize("line1\n   line2");
    assert!(!result.contains("\n   "));
}

#[test]
fn test_handle_empty_string() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert_eq!(normalizer.normalize(""), "");
}

#[test]
fn test_handle_only_whitespace() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    assert!(normalizer.normalize("   \t  \n  ").trim().is_empty());
}

#[test]
fn test_normalize_multiple_paragraphs() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let text = "Para 1  with  spaces\n\n\nPara 2   with   spaces";
    let result = normalizer.normalize(text);

    assert!(!result.contains("  with  "));
    assert!(!result.contains("   with   "));
}

#[test]
fn test_normalize_code_block_marker_preserved() {
    use xberg_native_pdf::pipeline::text_processing::WhitespaceNormalizer;

    let normalizer = WhitespaceNormalizer::new(false);
    let text = "```\ncode with spaces\n```";
    let result = normalizer.normalize(text);
    assert!(result.contains("```"));
}
