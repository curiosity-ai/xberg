#![allow(clippy::useless_vec)]
#![allow(dead_code)]
//! Week 2 Day 10: Right-to-Left Script Support Test Suite
//!
//! Comprehensive tests for Arabic and Hebrew script handling:
//! - Script detection (Arabic, Hebrew, supplements, extensions)
//! - Diacritical mark handling (no boundaries before marks)
//! - Letter detection
//! - Number handling (Western and Eastern Arabic digits)
//! - Boundary rules (space, Tatweel, TJ offsets, transitions)
//! - LAM-ALEF ligature support
//! - Integration scenarios

use xberg_native_pdf::text::rtl_detector::*;
use xberg_native_pdf::text::{BoundaryContext, CharacterInfo};

/// Helper to create CharacterInfo for testing
fn make_char(code: u32, x_position: f32, tj_offset: Option<i32>) -> CharacterInfo {
    CharacterInfo {
        code,
        glyph_id: None,
        width: 400.0,
        x_position,
        tj_offset,
        font_size: 12.0,
        is_ligature: false,
        original_ligature: None,
        protected_from_split: false,
    }
}

fn make_context() -> BoundaryContext {
    BoundaryContext::new(12.0)
}

#[cfg(test)]
mod script_detection_tests {
    use super::*;

    #[test]
    fn test_detect_arabic_main_range() {
        assert_eq!(detect_rtl_script(0x0600), Some(RTLScript::Arabic));
        assert_eq!(detect_rtl_script(0x0627), Some(RTLScript::Arabic));
        assert_eq!(detect_rtl_script(0x0628), Some(RTLScript::Arabic));
        assert_eq!(detect_rtl_script(0x064B), Some(RTLScript::Arabic));
        assert_eq!(detect_rtl_script(0x06FF), Some(RTLScript::Arabic));

        assert!(is_rtl_text(0x0627));
        assert!(is_rtl_text(0x064B));
    }

    #[test]
    fn test_detect_arabic_supplement() {
        assert_eq!(detect_rtl_script(0x0750), Some(RTLScript::ArabicSupplement));
        assert_eq!(detect_rtl_script(0x0760), Some(RTLScript::ArabicSupplement));
        assert_eq!(detect_rtl_script(0x077F), Some(RTLScript::ArabicSupplement));

        assert!(is_rtl_text(0x0750));
    }

    #[test]
    fn test_detect_arabic_extended_a() {
        assert_eq!(detect_rtl_script(0x08A0), Some(RTLScript::ArabicExtendedA));
        assert_eq!(detect_rtl_script(0x08B0), Some(RTLScript::ArabicExtendedA));
        assert_eq!(detect_rtl_script(0x08FF), Some(RTLScript::ArabicExtendedA));

        assert!(is_rtl_text(0x08A0));
    }

    #[test]
    fn test_detect_hebrew() {
        assert_eq!(detect_rtl_script(0x0590), Some(RTLScript::Hebrew));
        assert_eq!(detect_rtl_script(0x05D0), Some(RTLScript::Hebrew));
        assert_eq!(detect_rtl_script(0x05D1), Some(RTLScript::Hebrew));
        assert_eq!(detect_rtl_script(0x05BC), Some(RTLScript::Hebrew));
        assert_eq!(detect_rtl_script(0x05FF), Some(RTLScript::Hebrew));

        assert!(is_rtl_text(0x05D0));
        assert!(is_rtl_text(0x05BC));
    }

    #[test]
    fn test_detect_presentation_forms_a() {
        assert_eq!(detect_rtl_script(0xFB50), Some(RTLScript::PresentationFormsA));
        assert_eq!(detect_rtl_script(0xFDCF), Some(RTLScript::PresentationFormsA));
        assert_eq!(detect_rtl_script(0xFDFF), Some(RTLScript::PresentationFormsA));

        assert!(is_rtl_text(0xFB50));
        assert!(is_rtl_text(0xFDCF));
    }

    #[test]
    fn test_detect_presentation_forms_b() {
        assert_eq!(detect_rtl_script(0xFE70), Some(RTLScript::PresentationFormsB));
        assert_eq!(detect_rtl_script(0xFE80), Some(RTLScript::PresentationFormsB));
        assert_eq!(detect_rtl_script(0xFEFF), Some(RTLScript::PresentationFormsB));

        assert!(is_rtl_text(0xFE70));
    }

    #[test]
    fn test_non_rtl_scripts() {
        assert_eq!(detect_rtl_script(0x0041), None);
        assert_eq!(detect_rtl_script(0x007A), None);

        assert_eq!(detect_rtl_script(0x4E00), None);

        assert_eq!(detect_rtl_script(0x0400), None);

        assert!(!is_rtl_text(0x0041));
        assert!(!is_rtl_text(0x4E00));
    }

    #[test]
    fn test_script_boundary_values() {
        assert_eq!(detect_rtl_script(0x05FF), Some(RTLScript::Hebrew));
        assert_eq!(detect_rtl_script(0x0600), Some(RTLScript::Arabic));

        assert_eq!(detect_rtl_script(0x06FF), Some(RTLScript::Arabic));
        assert_eq!(detect_rtl_script(0x0700), None);

        assert_eq!(detect_rtl_script(0x074F), None);
        assert_eq!(detect_rtl_script(0x0750), Some(RTLScript::ArabicSupplement));
    }
}

#[cfg(test)]
mod diacritical_tests {
    use super::*;

    #[test]
    fn test_arabic_diacritics() {
        assert!(is_arabic_diacritic(0x064B));
        assert!(is_arabic_diacritic(0x064C));
        assert!(is_arabic_diacritic(0x064D));
        assert!(is_arabic_diacritic(0x064E));
        assert!(is_arabic_diacritic(0x064F));
        assert!(is_arabic_diacritic(0x0650));
        assert!(is_arabic_diacritic(0x0651));
        assert!(is_arabic_diacritic(0x0652));
        assert!(is_arabic_diacritic(0x0653));
        assert!(is_arabic_diacritic(0x0654));
        assert!(is_arabic_diacritic(0x0655));
        assert!(is_arabic_diacritic(0x0656));
        assert!(is_arabic_diacritic(0x0657));
        assert!(is_arabic_diacritic(0x0658));

        assert!(is_arabic_diacritic(0x06D6));
        assert!(is_arabic_diacritic(0x06DC));
        assert!(is_arabic_diacritic(0x06DF));
        assert!(is_arabic_diacritic(0x06E0));
        assert!(is_arabic_diacritic(0x06E4));
        assert!(is_arabic_diacritic(0x06E7));
        assert!(is_arabic_diacritic(0x06E8));
        assert!(is_arabic_diacritic(0x06EA));
        assert!(is_arabic_diacritic(0x06EB));
        assert!(is_arabic_diacritic(0x06EC));
        assert!(is_arabic_diacritic(0x06ED));

        assert!(!is_arabic_diacritic(0x0627));
        assert!(!is_arabic_diacritic(0x0628));
    }

    #[test]
    fn test_hebrew_diacritics() {
        assert!(is_hebrew_diacritic(0x05BC));
        assert!(is_hebrew_diacritic(0x05BD));
        assert!(is_hebrew_diacritic(0x05BF));
        assert!(is_hebrew_diacritic(0x05C1));
        assert!(is_hebrew_diacritic(0x05C2));
        assert!(is_hebrew_diacritic(0x05C4));
        assert!(is_hebrew_diacritic(0x05C5));
        assert!(is_hebrew_diacritic(0x05C7));

        assert!(is_hebrew_diacritic(0x05B0));
        assert!(is_hebrew_diacritic(0x05B1));
        assert!(is_hebrew_diacritic(0x05B9));

        assert!(!is_hebrew_diacritic(0x05D0));
        assert!(!is_hebrew_diacritic(0x05D1));
    }

    #[test]
    fn test_rtl_diacritic_combined() {
        assert!(is_rtl_diacritic(0x064B));
        assert!(is_rtl_diacritic(0x0651));
        assert!(is_rtl_diacritic(0x05BC));
        assert!(is_rtl_diacritic(0x05B0));

        assert!(!is_rtl_diacritic(0x0041));
        assert!(!is_rtl_diacritic(0x0627));
    }

    #[test]
    fn test_no_boundary_before_arabic_diacritic() {
        let context = make_context();
        let base = make_char(0x0628, 100.0, None);
        let mark = make_char(0x064E, 100.0, None);

        let result = should_split_at_rtl_boundary(&base, &mark, Some(&context));
        assert_eq!(result, Some(false), "Should not split before Arabic diacritic");
    }

    #[test]
    fn test_no_boundary_before_hebrew_diacritic() {
        let context = make_context();
        let base = make_char(0x05D1, 100.0, None);
        let mark = make_char(0x05BC, 100.0, None);

        let result = should_split_at_rtl_boundary(&base, &mark, Some(&context));
        assert_eq!(result, Some(false), "Should not split before Hebrew diacritic");
    }

    #[test]
    fn test_multiple_diacritics_same_base() {
        let context = make_context();

        let base = make_char(0x0628, 100.0, None);
        let mark1 = make_char(0x064E, 100.0, None);
        assert_eq!(should_split_at_rtl_boundary(&base, &mark1, Some(&context)), Some(false));

        let mark2 = make_char(0x0651, 100.0, None);
        assert_eq!(
            should_split_at_rtl_boundary(&mark1, &mark2, Some(&context)),
            Some(false)
        );
    }

    #[test]
    fn test_diacritic_sequences() {
        let context = make_context();

        let letter = make_char(0x05D0, 100.0, None);
        let vowel = make_char(0x05B0, 100.0, None);
        let dagesh = make_char(0x05BC, 100.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&letter, &vowel, Some(&context)),
            Some(false)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&vowel, &dagesh, Some(&context)),
            Some(false)
        );
    }

    #[test]
    fn test_diacritic_on_presentation_form() {
        let context = make_context();

        let form = make_char(0xFE82, 100.0, None);
        let mark = make_char(0x064E, 100.0, None);

        assert_eq!(should_split_at_rtl_boundary(&form, &mark, Some(&context)), Some(false));
    }
}

#[cfg(test)]
mod letter_detection_tests {
    use super::*;

    #[test]
    fn test_arabic_letters() {
        assert!(is_arabic_letter(0x0627));
        assert!(is_arabic_letter(0x0628));
        assert!(is_arabic_letter(0x062A));
        assert!(is_arabic_letter(0x062B));
        assert!(is_arabic_letter(0x062C));
        assert!(is_arabic_letter(0x062D));
        assert!(is_arabic_letter(0x062E));
        assert!(is_arabic_letter(0x062F));
        assert!(is_arabic_letter(0x0630));
        assert!(is_arabic_letter(0x0631));
        assert!(is_arabic_letter(0x0632));
        assert!(is_arabic_letter(0x0633));
        assert!(is_arabic_letter(0x0634));
        assert!(is_arabic_letter(0x0635));
        assert!(is_arabic_letter(0x0636));
        assert!(is_arabic_letter(0x0637));
        assert!(is_arabic_letter(0x0638));
        assert!(is_arabic_letter(0x0639));
        assert!(is_arabic_letter(0x063A));
        assert!(is_arabic_letter(0x0641));
        assert!(is_arabic_letter(0x0642));
        assert!(is_arabic_letter(0x0643));
        assert!(is_arabic_letter(0x0644));
        assert!(is_arabic_letter(0x0645));
        assert!(is_arabic_letter(0x0646));
        assert!(is_arabic_letter(0x0647));
        assert!(is_arabic_letter(0x0648));
        assert!(is_arabic_letter(0x064A));

        assert!(!is_arabic_letter(0x064B));
        assert!(!is_arabic_letter(0x0660));
    }

    #[test]
    fn test_hebrew_letters() {
        assert!(is_hebrew_letter(0x05D0));
        assert!(is_hebrew_letter(0x05D1));
        assert!(is_hebrew_letter(0x05D2));
        assert!(is_hebrew_letter(0x05D3));
        assert!(is_hebrew_letter(0x05D4));
        assert!(is_hebrew_letter(0x05D5));
        assert!(is_hebrew_letter(0x05D6));
        assert!(is_hebrew_letter(0x05D7));
        assert!(is_hebrew_letter(0x05D8));
        assert!(is_hebrew_letter(0x05D9));
        assert!(is_hebrew_letter(0x05DA));
        assert!(is_hebrew_letter(0x05DB));
        assert!(is_hebrew_letter(0x05DC));
        assert!(is_hebrew_letter(0x05DD));
        assert!(is_hebrew_letter(0x05DE));
        assert!(is_hebrew_letter(0x05DF));
        assert!(is_hebrew_letter(0x05E0));
        assert!(is_hebrew_letter(0x05E1));
        assert!(is_hebrew_letter(0x05E2));
        assert!(is_hebrew_letter(0x05E3));
        assert!(is_hebrew_letter(0x05E4));
        assert!(is_hebrew_letter(0x05E5));
        assert!(is_hebrew_letter(0x05E6));
        assert!(is_hebrew_letter(0x05E7));
        assert!(is_hebrew_letter(0x05E8));
        assert!(is_hebrew_letter(0x05E9));
        assert!(is_hebrew_letter(0x05EA));

        assert!(!is_hebrew_letter(0x05BC));
        assert!(!is_hebrew_letter(0x05BE));
    }

    #[test]
    fn test_letter_detection_ranges() {
        for code in 0x0621..=0x063A {
            if code != 0x0640 {
                assert!(is_arabic_letter(code), "Arabic letter 0x{:04X}", code);
            }
        }

        for code in 0x0641..=0x064A {
            assert!(is_arabic_letter(code), "Arabic letter 0x{:04X}", code);
        }

        for code in 0x05D0..=0x05EA {
            assert!(is_hebrew_letter(code), "Hebrew letter 0x{:04X}", code);
        }
    }

    #[test]
    fn test_letter_vs_diacritic_distinction() {
        assert!(is_arabic_letter(0x0628));
        assert!(!is_arabic_diacritic(0x0628));

        assert!(is_arabic_diacritic(0x064E));
        assert!(!is_arabic_letter(0x064E));

        assert!(is_hebrew_letter(0x05D0));
        assert!(!is_hebrew_diacritic(0x05D0));

        assert!(is_hebrew_diacritic(0x05BC));
        assert!(!is_hebrew_letter(0x05BC));
    }

    #[test]
    fn test_arabic_extended_letters() {
        assert!(is_arabic_letter(0x0750));
        assert!(is_arabic_letter(0x0767));

        assert!(is_arabic_letter(0x08A0));
        assert!(is_arabic_letter(0x08B4));
    }

    #[test]
    fn test_hebrew_punctuation() {
        assert!(is_hebrew_punctuation(0x05F3));
        assert!(is_hebrew_punctuation(0x05F4));

        assert!(!is_hebrew_punctuation(0x05D0));
        assert!(!is_hebrew_punctuation(0x05BC));
    }
}

#[cfg(test)]
mod number_handling_tests {
    use super::*;

    #[test]
    fn test_eastern_arabic_digits() {
        assert!(is_eastern_arabic_digit(0x06F0));
        assert!(is_eastern_arabic_digit(0x06F1));
        assert!(is_eastern_arabic_digit(0x06F2));
        assert!(is_eastern_arabic_digit(0x06F3));
        assert!(is_eastern_arabic_digit(0x06F4));
        assert!(is_eastern_arabic_digit(0x06F5));
        assert!(is_eastern_arabic_digit(0x06F6));
        assert!(is_eastern_arabic_digit(0x06F7));
        assert!(is_eastern_arabic_digit(0x06F8));
        assert!(is_eastern_arabic_digit(0x06F9));

        assert!(!is_eastern_arabic_digit(0x0030));
        assert!(!is_eastern_arabic_digit(0x0627));
    }

    #[test]
    fn test_arabic_number_detection() {
        assert!(is_arabic_number(0x06F0));
        assert!(is_arabic_number(0x06F5));

        assert!(is_arabic_number(0x0030));
        assert!(is_arabic_number(0x0035));
        assert!(is_arabic_number(0x0039));

        assert!(!is_arabic_number(0x0627));
        assert!(!is_arabic_number(0x0041));
    }

    #[test]
    fn test_no_boundary_within_number_sequence() {
        let context = make_context();

        let digit1 = make_char(0x0031, 100.0, None);
        let digit2 = make_char(0x0032, 105.0, None);
        let digit3 = make_char(0x0033, 110.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&digit1, &digit2, Some(&context)),
            Some(false)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&digit2, &digit3, Some(&context)),
            Some(false)
        );

        let e_digit1 = make_char(0x06F1, 100.0, None);
        let e_digit2 = make_char(0x06F2, 105.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&e_digit1, &e_digit2, Some(&context)),
            Some(false)
        );
    }

    #[test]
    fn test_mixed_digit_sequences() {
        let context = make_context();

        let western = make_char(0x0031, 100.0, None);
        let eastern = make_char(0x06F2, 105.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&western, &eastern, Some(&context)),
            Some(false)
        );
    }
}

#[cfg(test)]
mod boundary_rules_tests {
    use super::*;

    #[test]
    fn test_space_creates_boundary() {
        let context = make_context();

        let letter = make_char(0x0628, 100.0, None);
        let space = make_char(0x0020, 105.0, None);
        let next_letter = make_char(0x062A, 110.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&letter, &space, Some(&context)),
            Some(true)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&space, &next_letter, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_tatweel_preserves_word() {
        let context = make_context();

        let letter1 = make_char(0x0628, 100.0, None);
        let tatweel = make_char(0x0640, 105.0, None);
        let letter2 = make_char(0x062A, 110.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&letter1, &tatweel, Some(&context)),
            Some(false)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&tatweel, &letter2, Some(&context)),
            Some(false)
        );
    }

    #[test]
    fn test_tj_offset_large_negative_creates_boundary() {
        let context = make_context();

        let letter1 = make_char(0x0628, 100.0, Some(-60));
        let letter2 = make_char(0x062A, 95.0, None);

        // Large negative TJ offset (< -50) creates boundary in RTL ~keep
        assert_eq!(
            should_split_at_rtl_boundary(&letter1, &letter2, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_tj_offset_small_negative_no_boundary() {
        let context = make_context();

        let letter1 = make_char(0x0628, 100.0, Some(-30));
        let letter2 = make_char(0x062A, 97.0, None);

        // Small negative TJ offset does not create boundary ~keep
        assert_eq!(
            should_split_at_rtl_boundary(&letter1, &letter2, Some(&context)),
            Some(false)
        );
    }

    #[test]
    fn test_rtl_to_ltr_transition() {
        let context = make_context();

        let arabic = make_char(0x0628, 100.0, None);
        let latin = make_char(0x0041, 105.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&arabic, &latin, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_ltr_to_rtl_transition() {
        let context = make_context();

        let latin = make_char(0x0041, 100.0, None);
        let arabic = make_char(0x0628, 105.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&latin, &arabic, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_arabic_punctuation_boundaries() {
        let context = make_context();

        let letter = make_char(0x0628, 100.0, None);
        let comma = make_char(0x060C, 105.0, None);
        let semicolon = make_char(0x061B, 110.0, None);
        let question = make_char(0x061F, 115.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&letter, &comma, Some(&context)),
            Some(true)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&letter, &semicolon, Some(&context)),
            Some(true)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&letter, &question, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_hebrew_punctuation_boundaries() {
        let context = make_context();

        let letter = make_char(0x05D0, 100.0, None);
        let geresh = make_char(0x05F3, 105.0, None);
        let gershayim = make_char(0x05F4, 110.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&letter, &geresh, Some(&context)),
            Some(true)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&letter, &gershayim, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_normal_letter_sequence_no_boundary() {
        let context = make_context();

        let beh = make_char(0x0628, 100.0, None);
        let teh = make_char(0x062A, 105.0, None);
        let seen = make_char(0x0633, 110.0, None);

        assert_eq!(should_split_at_rtl_boundary(&beh, &teh, Some(&context)), Some(false));
        assert_eq!(should_split_at_rtl_boundary(&teh, &seen, Some(&context)), Some(false));

        let alef = make_char(0x05D0, 100.0, None);
        let bet = make_char(0x05D1, 105.0, None);
        let gimel = make_char(0x05D2, 110.0, None);

        assert_eq!(should_split_at_rtl_boundary(&alef, &bet, Some(&context)), Some(false));
        assert_eq!(should_split_at_rtl_boundary(&bet, &gimel, Some(&context)), Some(false));
    }

    #[test]
    fn test_non_rtl_characters_return_none() {
        let context = make_context();

        let latin1 = make_char(0x0041, 100.0, None);
        let latin2 = make_char(0x0042, 105.0, None);

        assert_eq!(should_split_at_rtl_boundary(&latin1, &latin2, Some(&context)), None);
    }
}

#[cfg(test)]
mod ligature_tests {
    use super::*;

    #[test]
    fn test_lam_alef_ligature_detection() {
        assert!(is_lam_alef_ligature(0xFEFB));
        assert!(is_lam_alef_ligature(0xFEFC));
        assert!(is_lam_alef_ligature(0xFEF5));
        assert!(is_lam_alef_ligature(0xFEF6));
        assert!(is_lam_alef_ligature(0xFEF7));
        assert!(is_lam_alef_ligature(0xFEF8));
        assert!(is_lam_alef_ligature(0xFEF9));
        assert!(is_lam_alef_ligature(0xFEFA));

        assert!(!is_lam_alef_ligature(0x0644));
        assert!(!is_lam_alef_ligature(0x0627));
        assert!(!is_lam_alef_ligature(0xFB50));
    }

    #[test]
    fn test_lam_alef_decomposition() {
        assert_eq!(decompose_lam_alef(0xFEFB), Some((0x0644, 0x0627)));
        assert_eq!(decompose_lam_alef(0xFEFC), Some((0x0644, 0x0627)));
        assert_eq!(decompose_lam_alef(0xFEF5), Some((0x0644, 0x0622)));
        assert_eq!(decompose_lam_alef(0xFEF6), Some((0x0644, 0x0622)));
        assert_eq!(decompose_lam_alef(0xFEF7), Some((0x0644, 0x0623)));
        assert_eq!(decompose_lam_alef(0xFEF8), Some((0x0644, 0x0623)));
        assert_eq!(decompose_lam_alef(0xFEF9), Some((0x0644, 0x0625)));
        assert_eq!(decompose_lam_alef(0xFEFA), Some((0x0644, 0x0625)));

        assert_eq!(decompose_lam_alef(0x0644), None);
        assert_eq!(decompose_lam_alef(0x0627), None);
        assert_eq!(decompose_lam_alef(0x0041), None);
    }

    #[test]
    fn test_presentation_form_normalization() {
        assert_eq!(normalize_arabic_contextual_form(0xFB50), 0x0671);
        assert_eq!(normalize_arabic_contextual_form(0xFE82), 0x0627);

        assert_eq!(normalize_arabic_contextual_form(0xFE8F), 0x0628);
        assert_eq!(normalize_arabic_contextual_form(0xFE90), 0x0628);
        assert_eq!(normalize_arabic_contextual_form(0xFE91), 0x0628);
        assert_eq!(normalize_arabic_contextual_form(0xFE92), 0x0628);

        assert_eq!(normalize_arabic_contextual_form(0x0628), 0x0628);
        assert_eq!(normalize_arabic_contextual_form(0x0041), 0x0041);
    }

    #[test]
    fn test_ligature_boundary_behavior() {
        let context = make_context();

        let ligature = make_char(0xFEFC, 100.0, None);
        let letter = make_char(0x062A, 105.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&ligature, &letter, Some(&context)),
            Some(false)
        );
    }
}

#[cfg(test)]
mod integration_tests {
    use super::*;

    #[test]
    fn test_full_arabic_word_with_diacritics() {
        let context = make_context();

        let chars = vec![
            make_char(0x0643, 100.0, None),
            make_char(0x0650, 100.0, None),
            make_char(0x062A, 105.0, None),
            make_char(0x064E, 105.0, None),
            make_char(0x0627, 110.0, None),
            make_char(0x0628, 115.0, None),
            make_char(0x064C, 115.0, None),
        ];

        for i in 0..chars.len() - 1 {
            let result = should_split_at_rtl_boundary(&chars[i], &chars[i + 1], Some(&context));
            assert_eq!(result, Some(false), "Should not split at position {}", i);
        }
    }

    #[test]
    fn test_arabic_sentence_with_spaces() {
        let context = make_context();

        let word1_last = make_char(0x064C, 130.0, None);
        let space = make_char(0x0020, 135.0, None);
        let word2_first = make_char(0x0628, 140.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&word1_last, &space, Some(&context)),
            Some(true)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&space, &word2_first, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_hebrew_word_with_vowels() {
        let context = make_context();

        let chars = vec![
            make_char(0x05E9, 100.0, None),
            make_char(0x05C1, 100.0, None),
            make_char(0x05B8, 100.0, None),
            make_char(0x05DC, 105.0, None),
            make_char(0x05B9, 105.0, None),
            make_char(0x05DD, 110.0, None),
        ];

        for i in 0..chars.len() - 1 {
            let result = should_split_at_rtl_boundary(&chars[i], &chars[i + 1], Some(&context));
            assert_eq!(result, Some(false), "Should not split at position {}", i);
        }
    }

    #[test]
    fn test_mixed_rtl_and_ltr() {
        let context = make_context();

        let arabic_letter = make_char(0x0628, 100.0, None);
        let space = make_char(0x0020, 105.0, None);
        let latin_letter = make_char(0x0041, 110.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&arabic_letter, &space, Some(&context)),
            Some(true)
        );

        assert_eq!(
            should_split_at_rtl_boundary(&space, &latin_letter, Some(&context)),
            Some(true)
        );
    }

    #[test]
    fn test_arabic_with_numbers() {
        let context = make_context();

        let word_end = make_char(0x0629, 100.0, None);
        let space = make_char(0x0020, 105.0, None);
        let digit1 = make_char(0x06F2, 110.0, None);
        let digit2 = make_char(0x06F0, 115.0, None);
        let digit3 = make_char(0x06F2, 120.0, None);
        let digit4 = make_char(0x06F5, 125.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&word_end, &space, Some(&context)),
            Some(true)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&space, &digit1, Some(&context)),
            Some(true)
        );

        assert_eq!(
            should_split_at_rtl_boundary(&digit1, &digit2, Some(&context)),
            Some(false)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&digit2, &digit3, Some(&context)),
            Some(false)
        );
        assert_eq!(
            should_split_at_rtl_boundary(&digit3, &digit4, Some(&context)),
            Some(false)
        );
    }

    #[test]
    fn test_presentation_forms_in_context() {
        let context = make_context();

        let alef_final = make_char(0xFE82, 100.0, None);
        let beh_initial = make_char(0xFE91, 105.0, None);

        assert_eq!(
            should_split_at_rtl_boundary(&alef_final, &beh_initial, Some(&context)),
            Some(false)
        );
    }
}
