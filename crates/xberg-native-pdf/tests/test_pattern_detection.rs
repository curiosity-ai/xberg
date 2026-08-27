#![allow(clippy::needless_range_loop)]
//! Test suite for Week 2 Day 7: Email/URL Pattern Preservation (2C)
//!
//! This test suite verifies that email addresses and URLs are detected and
//! protected from word boundary splitting during text extraction.
//!
//! The pattern detector marks email and URL characters with the
//! `protected_from_split` flag, which prevents WordBoundaryDetector
//! from creating boundaries within these patterns.

use xberg_native_pdf::extractors::pattern_detector::{PatternDetector, PatternPreservationConfig};
use xberg_native_pdf::text::{BoundaryContext, CharacterInfo, WordBoundaryDetector};

/// Helper: Create a CharacterInfo for testing
fn create_char_info(code: u32) -> CharacterInfo {
    CharacterInfo {
        code,
        glyph_id: Some(1),
        width: 0.5,
        x_position: 0.0,
        tj_offset: None,
        font_size: 12.0,
        is_ligature: false,
        original_ligature: None,
        protected_from_split: false,
    }
}

/// Helper: Convert string to CharacterInfo array
fn string_to_chars(s: &str) -> Vec<CharacterInfo> {
    s.chars().map(|ch| create_char_info(ch as u32)).collect()
}

#[test]
fn test_email_pattern_detection() {
    let chars = string_to_chars("user@example.com");
    assert!(
        PatternDetector::has_email_pattern(&chars),
        "Should detect email pattern"
    );
}

#[test]
fn test_email_pattern_with_subdomain() {
    let chars = string_to_chars("user@mail.example.com");
    assert!(
        PatternDetector::has_email_pattern(&chars),
        "Should detect email with subdomain"
    );
}

#[test]
fn test_email_pattern_with_plus() {
    let chars = string_to_chars("user+tag@example.com");
    assert!(
        PatternDetector::has_email_pattern(&chars),
        "Should detect email with plus sign"
    );
}

#[test]
fn test_email_pattern_no_domain() {
    let chars = string_to_chars("user@example");
    assert!(
        !PatternDetector::has_email_pattern(&chars),
        "Should not detect email without dot in domain"
    );
}

#[test]
fn test_email_protection_from_split() {
    let mut chars = string_to_chars("user@example.com");
    let config = PatternPreservationConfig::default();

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    for (i, ch) in chars.iter().enumerate() {
        assert!(ch.protected_from_split, "Email character {} should be protected", i);
    }
}

#[test]
fn test_url_pattern_http() {
    let chars = string_to_chars("http://example.com");
    assert!(PatternDetector::has_url_pattern(&chars), "Should detect http:// URL");
}

#[test]
fn test_url_pattern_https() {
    let chars = string_to_chars("https://example.com");
    assert!(PatternDetector::has_url_pattern(&chars), "Should detect https:// URL");
}

#[test]
fn test_url_pattern_ftp() {
    let chars = string_to_chars("ftp://ftp.example.com");
    assert!(PatternDetector::has_url_pattern(&chars), "Should detect ftp:// URL");
}

#[test]
fn test_url_pattern_mailto() {
    let chars = string_to_chars("mailto:user@example.com");
    assert!(PatternDetector::has_url_pattern(&chars), "Should detect mailto: URL");
}

#[test]
fn test_url_protection_from_split() {
    let mut chars = string_to_chars("http://example.com");
    let config = PatternPreservationConfig::default();

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    for (i, ch) in chars.iter().enumerate() {
        assert!(ch.protected_from_split, "URL character {} should be protected", i);
    }
}

#[test]
fn test_boundary_skip_in_email() {
    let mut chars = string_to_chars("user@example.com");
    let config = PatternPreservationConfig::default();

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    // Try to detect boundaries - should find none because all chars are protected ~keep
    let context = BoundaryContext::new(12.0);
    let detector = WordBoundaryDetector::new();
    let boundaries = detector.detect_word_boundaries(&chars, &context);

    assert!(
        boundaries.is_empty(),
        "No boundaries should be created within protected email"
    );
}

#[test]
fn test_boundary_skip_in_url() {
    let mut chars = string_to_chars("http://example.com");
    let config = PatternPreservationConfig::default();

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    // Try to detect boundaries - should find none because all chars are protected ~keep
    let context = BoundaryContext::new(12.0);
    let detector = WordBoundaryDetector::new();
    let boundaries = detector.detect_word_boundaries(&chars, &context);

    assert!(
        boundaries.is_empty(),
        "No boundaries should be created within protected URL"
    );
}

#[test]
fn test_false_positive_version_number() {
    let chars = string_to_chars("version 2.0");
    assert!(
        !PatternDetector::has_email_pattern(&chars),
        "Version number should not be detected as email"
    );
}

#[test]
fn test_multiple_patterns_in_text() {
    let mut chars = string_to_chars("Contact user@example.com or visit http://example.com");
    let config = PatternPreservationConfig::default();

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    let text = "Contact user@example.com or visit http://example.com";
    let email_start = text.find("user@").unwrap();
    let email_end = email_start + "user@example.com".len();
    let url_start = text.find("http://").unwrap();
    let url_end = url_start + "http://example.com".len();

    for i in email_start..email_end {
        assert!(
            chars[i].protected_from_split,
            "Email character {} should be protected",
            i
        );
    }

    for i in url_start..url_end {
        assert!(chars[i].protected_from_split, "URL character {} should be protected", i);
    }

    for i in 0..email_start {
        assert!(
            !chars[i].protected_from_split,
            "Non-pattern character {} should not be protected",
            i
        );
    }
}

#[test]
fn test_pattern_detection_config_flag() {
    let mut chars = string_to_chars("user@example.com");
    let config = PatternPreservationConfig {
        preserve_patterns: false,
        detect_emails: true,
        detect_urls: true,
    };

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    for ch in &chars {
        assert!(
            !ch.protected_from_split,
            "Characters should not be protected when pattern detection is disabled"
        );
    }
}

#[test]
fn test_email_detection_disabled() {
    let mut chars = string_to_chars("user@example.com");
    let config = PatternPreservationConfig {
        preserve_patterns: true,
        detect_emails: false,
        detect_urls: true,
    };

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    for ch in &chars {
        assert!(
            !ch.protected_from_split,
            "Characters should not be protected when email detection is disabled"
        );
    }
}

#[test]
fn test_url_detection_disabled() {
    let mut chars = string_to_chars("http://example.com");
    let config = PatternPreservationConfig {
        preserve_patterns: true,
        detect_emails: true,
        detect_urls: false,
    };

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    for ch in &chars {
        assert!(
            !ch.protected_from_split,
            "Characters should not be protected when URL detection is disabled"
        );
    }
}

#[test]
fn test_mixed_content_partial_protection() {
    let mut chars = string_to_chars("Email: user@example.com for info");
    let config = PatternPreservationConfig::default();

    PatternDetector::mark_pattern_contexts(&mut chars, &config).unwrap();

    let text = "Email: user@example.com for info";
    let email_start = text.find("user@").unwrap();
    let email_end = email_start + "user@example.com".len();

    for i in email_start..email_end {
        assert!(
            chars[i].protected_from_split,
            "Email character {} should be protected",
            i
        );
    }

    for i in 0..email_start {
        assert!(
            !chars[i].protected_from_split,
            "Non-email character {} should not be protected",
            i
        );
    }

    for i in email_end..chars.len() {
        assert!(
            !chars[i].protected_from_split,
            "Non-email character {} should not be protected",
            i
        );
    }
}

#[test]
fn test_url_with_path() {
    let chars = string_to_chars("https://example.com/path?q=1");
    assert!(
        PatternDetector::has_url_pattern(&chars),
        "Should detect URL with path and query"
    );
}

#[test]
fn test_url_with_port() {
    let chars = string_to_chars("http://example.com:8080");
    assert!(PatternDetector::has_url_pattern(&chars), "Should detect URL with port");
}

#[test]
fn test_email_case_insensitive() {
    let chars_upper = string_to_chars("USER@EXAMPLE.COM");
    assert!(
        PatternDetector::has_email_pattern(&chars_upper),
        "Should detect uppercase email"
    );

    let chars_mixed = string_to_chars("User@Example.Com");
    assert!(
        PatternDetector::has_email_pattern(&chars_mixed),
        "Should detect mixed-case email"
    );
}

#[test]
fn test_url_case_insensitive() {
    let chars_upper = string_to_chars("HTTP://EXAMPLE.COM");
    assert!(
        PatternDetector::has_url_pattern(&chars_upper),
        "Should detect uppercase HTTP URL"
    );

    let chars_mixed = string_to_chars("HtTp://Example.Com");
    assert!(
        PatternDetector::has_url_pattern(&chars_mixed),
        "Should detect mixed-case HTTP URL"
    );
}
