//! Integration tests for hyphenation module in text extraction pipeline

use xberg_native_pdf::pipeline::config::TextPipelineConfig;
use xberg_native_pdf::text::hyphenation::HyphenationHandler;

#[test]
fn test_hyphenation_reconstruction_simple() {
    let handler = HyphenationHandler::new();

    // Simple case: word split across line
    // "Government" is in the common words list, so it will be properly joined ~keep
    let input = "The Govern-\nment of the United States";
    let output = handler.process_text(input);

    assert!(
        output.contains("Government"),
        "Word should be reconstructed as 'Government'"
    );
}

#[test]
fn test_hyphenation_preserves_regular_hyphens() {
    let handler = HyphenationHandler::new();

    let input = "well-known phrase in the text";
    let output = handler.process_text(input);

    assert!(output.contains("well-known"), "Regular hyphens should be preserved");
}

#[test]
fn test_hyphenation_with_config_enabled() {
    let config_enabled = TextPipelineConfig::default().with_hyphenation_reconstruction(true);
    assert!(config_enabled.enable_hyphenation_reconstruction);
}

#[test]
fn test_hyphenation_with_config_disabled() {
    let config_disabled = TextPipelineConfig::default().with_hyphenation_reconstruction(false);
    assert!(!config_disabled.enable_hyphenation_reconstruction);
}

#[test]
fn test_hyphenation_default_enabled() {
    let config = TextPipelineConfig::default();
    assert!(config.enable_hyphenation_reconstruction);
}

#[test]
fn test_hyphenation_multiple_continuations() {
    let handler = HyphenationHandler::new();

    let input = "Govern-\nment issued a reorgan-\nization";
    let output = handler.process_text(input);

    assert!(
        output.contains("Government"),
        "First continuation should be reconstructed"
    );
}

#[test]
fn test_hyphenation_preserves_compound_words() {
    let handler = HyphenationHandler::new();

    let input = "content-type is a technical term";
    let output = handler.process_text(input);

    assert!(
        output.contains("content-type"),
        "Compound words should preserve hyphens"
    );
}

#[test]
fn test_hyphenation_edge_case_empty_string() {
    let handler = HyphenationHandler::new();

    let input = "";
    let output = handler.process_text(input);

    assert_eq!(output, "", "Empty string should remain empty");
}

#[test]
fn test_hyphenation_edge_case_no_hyphen() {
    let handler = HyphenationHandler::new();

    let input = "This is normal text\nwithout any hyphens\nat line ends.";
    let output = handler.process_text(input);

    assert_eq!(output, input, "Text without hyphens should remain unchanged");
}

#[test]
fn test_hyphenation_single_letter_word() {
    let handler = HyphenationHandler::new();

    let input = "word-\na";
    let output = handler.process_text(input);

    // Single letter after hyphen should not be joined (minimum continuation length = 2) ~keep
    assert!(!output.contains("worda"), "Single letter shouldn't be joined");
}

#[test]
fn test_hyphenation_preserves_formatting_characters() {
    let handler = HyphenationHandler::new();

    let input = "**bold text** contain-\ning word";
    let output = handler.process_text(input);

    assert!(output.contains("**bold text**"), "Formatting should be preserved");
}

#[test]
fn test_hyphenation_with_newlines_only() {
    let handler = HyphenationHandler::new();

    let input = "text\n\nwith\n\ngaps";
    let output = handler.process_text(input);

    assert_eq!(output, input, "Paragraph structure should be preserved");
}

#[test]
fn test_hyphenation_compound_prefix_self() {
    let handler = HyphenationHandler::new();

    let input = "self-regulation is important";
    let output = handler.process_text(input);

    assert!(
        output.contains("self-regulation"),
        "Compound prefix 'self' should be preserved"
    );
}

#[test]
fn test_hyphenation_compound_prefix_non() {
    let handler = HyphenationHandler::new();

    let input = "non-linear systems";
    let output = handler.process_text(input);

    assert!(
        output.contains("non-linear"),
        "Compound prefix 'non' should be preserved"
    );
}

#[test]
fn test_hyphenation_trailing_hyphen_only() {
    let handler = HyphenationHandler::new();

    let input = "word-";
    let output = handler.process_text(input);

    assert!(!output.is_empty(), "Should handle trailing hyphen gracefully");
}

#[test]
fn test_hyphenation_builder_pattern() {
    let handler = HyphenationHandler::new()
        .with_min_continuation_length(3)
        .with_preserve_compounds(false);

    let input = "text-\nab";
    let output = handler.process_text(input);

    // With min length 3, "ab" (2 chars) shouldn't be joined ~keep
    assert!(
        !output.contains("textab"),
        "Minimum length threshold should be respected"
    );
}

#[test]
fn test_hyphenation_multiline_paragraph() {
    let handler = HyphenationHandler::new();

    let input = "The quick brown\nfox jumps over\nthe lazy dog which is\nan example sentence.";
    let output = handler.process_text(input);

    assert_eq!(output, input, "Text without continuation hyphens unchanged");
}

#[test]
fn test_hyphenation_mixed_content() {
    let handler = HyphenationHandler::new();

    let input = "This is normal.\nThis requires careful implemen-\ntation of the solution.";
    let output = handler.process_text(input);

    assert!(
        output.contains("implementation"),
        "Word should be reconstructed as 'implementation'"
    );
}
