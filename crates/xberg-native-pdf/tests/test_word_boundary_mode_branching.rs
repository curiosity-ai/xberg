#![allow(warnings)]
use xberg_native_pdf::extractors::{TextExtractionConfig, TextExtractor};
use xberg_native_pdf::pipeline::config::WordBoundaryMode;

#[test]
fn test_mode_branching_accepts_tiebreaker_mode() {
    let config = TextExtractionConfig {
        word_boundary_mode: WordBoundaryMode::Tiebreaker,
        ..Default::default()
    };

    let _extractor = TextExtractor::with_config(config);
}

#[test]
fn test_mode_branching_accepts_primary_mode() {
    let config = TextExtractionConfig {
        word_boundary_mode: WordBoundaryMode::Primary,
        ..Default::default()
    };

    let _extractor = TextExtractor::with_config(config);
}

#[test]
fn test_tiebreaker_mode_path_exists() {
    let config = TextExtractionConfig::default();
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_primary_mode_path_exists() {
    let config = TextExtractionConfig {
        word_boundary_mode: WordBoundaryMode::Primary,
        ..Default::default()
    };

    let _extractor = TextExtractor::with_config(config);
}

#[test]
fn test_default_mode_is_tiebreaker() {
    let config = TextExtractionConfig::default();
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_mode_switching_possible() {
    let mut config = TextExtractionConfig::default();
    config.word_boundary_mode = WordBoundaryMode::Primary;
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Primary);

    config.word_boundary_mode = WordBoundaryMode::Tiebreaker;
    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_mode_field_publicly_accessible() {
    let config = TextExtractionConfig::default();
    let _mode = config.word_boundary_mode;
}
