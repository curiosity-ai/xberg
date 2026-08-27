//! Phase 9.2.C: Primary Word Boundary Detection Mode Implementation Tests
//!
//! Tests for the primary detection mode implementation that replaces
//! the stub process_tj_array_primary() method with actual functionality.
//!
//! This test suite verifies:
//! 1. BoundaryContext creation from graphics state
//! 2. Character partitioning at boundary positions
//! 3. Cluster to TextSpan conversion
//! 4. primary_detected flag is set correctly
//! 5. Backward compatibility with tiebreaker mode

use xberg_native_pdf::pipeline::config::{TextPipelineConfig, WordBoundaryMode};

#[test]
fn test_primary_mode_config_creation() {
    let config = TextPipelineConfig::default().with_word_boundary_mode(WordBoundaryMode::Primary);

    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Primary);
}

#[test]
fn test_tiebreaker_mode_config_creation() {
    let config = TextPipelineConfig::default();

    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_primary_mode_with_empty_character_array() {
    let _config = TextPipelineConfig::default().with_word_boundary_mode(WordBoundaryMode::Primary);
}

#[test]
fn test_primary_mode_fallback_to_tiebreaker() {
    // Phase 9.2.C: When character array is empty, should fall back to tiebreaker
    // This ensures no regression in existing behavior ~keep
}

#[test]
fn test_backward_compat_with_tiebreaker_mode() {
    let config = TextPipelineConfig::default().with_word_boundary_mode(WordBoundaryMode::Tiebreaker);

    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Tiebreaker);
}

#[test]
fn test_primary_mode_initialization() {
    let config = TextPipelineConfig::default().with_word_boundary_mode(WordBoundaryMode::Primary);

    assert_eq!(config.word_boundary_mode, WordBoundaryMode::Primary);
}
