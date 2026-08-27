#![allow(clippy::field_reassign_with_default)]
//! Phase 9.2.A: WordBoundaryMode Configuration Tests
//!
//! Tests for WordBoundaryMode enum and its integration with TextPipelineConfig.
//! This phase adds configuration infrastructure with no functional changes yet.

use xberg_native_pdf::extractors::{TextExtractionConfig, TextExtractor};
use xberg_native_pdf::pipeline::config::{TextPipelineConfig, WordBoundaryMode};

#[test]
fn test_word_boundary_mode_enum_exists() {
    let _mode = WordBoundaryMode::Tiebreaker;
    let _mode = WordBoundaryMode::Primary;
}

#[test]
fn test_word_boundary_mode_default_is_tiebreaker() {
    assert_eq!(WordBoundaryMode::default(), WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_word_boundary_mode_clone_and_debug() {
    let mode = WordBoundaryMode::Primary;
    let mode_clone = mode;
    assert_eq!(mode, mode_clone);
    let _ = format!("{:?}", mode);
}

#[test]
fn test_word_boundary_mode_copy() {
    let mode = WordBoundaryMode::Primary;
    let mode_copy = mode;
    assert_eq!(mode, mode_copy);
    let _mode_again = mode;
}

#[test]
fn test_text_pipeline_config_default_mode_is_tiebreaker() {
    let config = TextPipelineConfig::default();
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_text_pipeline_config_with_word_boundary_mode() {
    let config = TextPipelineConfig::default().with_word_boundary_mode(WordBoundaryMode::Primary);
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Primary);
}

#[test]
fn test_text_pipeline_config_pdfplumber_compatible_uses_tiebreaker() {
    let config = TextPipelineConfig::pdfplumber_compatible();
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_text_extraction_config_has_word_boundary_mode() {
    let config = TextExtractionConfig::default();
    let _ = config.word_boundary_mode;
}

#[test]
fn test_text_extraction_config_default_mode_is_tiebreaker() {
    let config = TextExtractionConfig::default();
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_text_extractor_accepts_mode_from_config() {
    let mut config = TextExtractionConfig::default();
    config.word_boundary_mode = WordBoundaryMode::Primary;

    let _extractor = TextExtractor::with_config(config);
}

#[test]
fn test_text_extractor_defaults_to_tiebreaker_mode() {
    let config = TextExtractionConfig::default();
    let _extractor = TextExtractor::with_config(config);
}

#[test]
fn test_word_boundary_mode_partial_eq() {
    assert_eq!(WordBoundaryMode::Tiebreaker, WordBoundaryMode::Tiebreaker);
    assert_eq!(WordBoundaryMode::Primary, WordBoundaryMode::Primary);
    assert_ne!(WordBoundaryMode::Tiebreaker, WordBoundaryMode::Primary);
}

#[test]
fn test_builder_pattern_chaining() {
    let config = TextPipelineConfig::default().with_word_boundary_mode(WordBoundaryMode::Primary);

    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Primary);
}

#[test]
fn test_word_boundary_mode_eq_trait() {
    let mode1 = WordBoundaryMode::Tiebreaker;
    let mode2 = WordBoundaryMode::Tiebreaker;
    let mode3 = WordBoundaryMode::Primary;

    assert_eq!(mode1, mode1);

    assert_eq!(mode1, mode2);
    assert_eq!(mode2, mode1);

    assert_ne!(mode1, mode3);
    assert_ne!(mode2, mode3);
}
