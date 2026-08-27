//! Week 2 Days 8-9: CJK Script Support Test Suite
//!
//! Comprehensive tests for CJK (Chinese, Japanese, Korean) script detection
//! and word boundary analysis.
//!
//! Test Coverage:
//! - CJK punctuation detection
//! - Script type detection
//! - Script transition analysis
//! - Japanese-specific rules (Kana transitions, small Kana)
//! - Korean-specific rules (Hangul-Hanja mixing)
//! - Integration with word boundary detector

use xberg_native_pdf::text::{BoundaryContext, CharacterInfo, DocumentLanguage, WordBoundaryDetector, cjk_punctuation};

/// Helper to create a test character
fn make_char(code: u32, x_pos: f32, tj_offset: Option<i32>) -> CharacterInfo {
    CharacterInfo {
        code,
        glyph_id: Some(1),
        width: 1.0,
        x_position: x_pos,
        tj_offset,
        font_size: 12.0,
        is_ligature: false,
        original_ligature: None,
        protected_from_split: false,
    }
}

mod punctuation_tests {
    use super::*;

    #[test]
    fn test_ideographic_fullstop_creates_boundary() {
        // 。(U+3002) should create boundary ~keep
        assert!(cjk_punctuation::is_sentence_ending_punctuation(0x3002));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0x3002, None), 1.0);
    }

    #[test]
    fn test_fullwidth_question_mark_creates_boundary() {
        // ？(U+FF1F) should create boundary ~keep
        assert!(cjk_punctuation::is_sentence_ending_punctuation(0xFF1F));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0xFF1F, None), 1.0);
    }

    #[test]
    fn test_fullwidth_exclamation_creates_boundary() {
        // ！(U+FF01) should create boundary ~keep
        assert!(cjk_punctuation::is_sentence_ending_punctuation(0xFF01));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0xFF01, None), 1.0);
    }

    #[test]
    fn test_ideographic_comma_enumeration() {
        assert!(cjk_punctuation::is_enumeration_punctuation(0x3001));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0x3001, None), 0.9);
    }

    #[test]
    fn test_fullwidth_comma_enumeration() {
        assert!(cjk_punctuation::is_enumeration_punctuation(0xFF0C));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0xFF0C, None), 0.9);
    }

    #[test]
    fn test_fullwidth_semicolon() {
        assert!(cjk_punctuation::is_enumeration_punctuation(0xFF1B));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0xFF1B, None), 0.9);
    }

    #[test]
    fn test_fullwidth_parentheses() {
        assert!(cjk_punctuation::is_bracket_punctuation(0xFF08));
        assert!(cjk_punctuation::is_opening_bracket(0xFF08));
        assert!(cjk_punctuation::is_bracket_punctuation(0xFF09));
        assert!(cjk_punctuation::is_closing_bracket(0xFF09));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0xFF08, None), 0.8);
    }

    #[test]
    fn test_angle_brackets() {
        assert!(cjk_punctuation::is_bracket_punctuation(0x3008));
        assert!(cjk_punctuation::is_opening_bracket(0x3008));
        assert!(cjk_punctuation::is_bracket_punctuation(0x3009));
        assert!(cjk_punctuation::is_closing_bracket(0x3009));
    }

    #[test]
    fn test_ideographic_space() {
        assert!(cjk_punctuation::is_other_cjk_punctuation(0x3000));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0x3000, None), 0.7);
    }

    #[test]
    fn test_katakana_middle_dot() {
        assert!(cjk_punctuation::is_other_cjk_punctuation(0x30FB));
        assert!(cjk_punctuation::is_fullwidth_punctuation(0x30FB));
    }

    #[test]
    fn test_ascii_punctuation_not_cjk() {
        assert!(!cjk_punctuation::is_fullwidth_punctuation(0x002E));
        assert!(!cjk_punctuation::is_fullwidth_punctuation(0x002C));
        assert_eq!(cjk_punctuation::get_cjk_punctuation_boundary_score(0x002E, None), 0.0);
    }

    #[test]
    fn test_boundary_score_hierarchy() {
        let sentence = cjk_punctuation::get_cjk_punctuation_boundary_score(0x3002, None);
        let enumeration = cjk_punctuation::get_cjk_punctuation_boundary_score(0x3001, None);
        let bracket = cjk_punctuation::get_cjk_punctuation_boundary_score(0xFF08, None);
        let other = cjk_punctuation::get_cjk_punctuation_boundary_score(0x30FB, None);

        assert!(sentence > enumeration);
        assert!(enumeration > bracket);
        assert!(bracket > other);
    }
}

mod script_detection_tests {
    use super::*;
    use xberg_native_pdf::text::script_detector::{CJKScript, detect_cjk_script};

    #[test]
    fn test_detect_han_main_range() {
        assert_eq!(detect_cjk_script(0x4E00), Some(CJKScript::Han));
        assert_eq!(detect_cjk_script(0x6587), Some(CJKScript::Han));
        assert_eq!(detect_cjk_script(0x9FFF), Some(CJKScript::Han));
    }

    #[test]
    fn test_detect_han_extension_a() {
        assert_eq!(detect_cjk_script(0x3400), Some(CJKScript::HanExtensionA));
        assert_eq!(detect_cjk_script(0x4DBF), Some(CJKScript::HanExtensionA));
    }

    #[test]
    fn test_detect_han_extension_bf() {
        assert_eq!(detect_cjk_script(0x20000), Some(CJKScript::HanExtensionBF));
        assert_eq!(detect_cjk_script(0x2EBEF), Some(CJKScript::HanExtensionBF));
    }

    #[test]
    fn test_detect_hiragana() {
        assert_eq!(detect_cjk_script(0x3042), Some(CJKScript::Hiragana));
        assert_eq!(detect_cjk_script(0x3093), Some(CJKScript::Hiragana));
    }

    #[test]
    fn test_detect_katakana() {
        assert_eq!(detect_cjk_script(0x30A2), Some(CJKScript::Katakana));
        assert_eq!(detect_cjk_script(0x30F3), Some(CJKScript::Katakana));
    }

    #[test]
    fn test_detect_halfwidth_katakana() {
        assert_eq!(detect_cjk_script(0xFF66), Some(CJKScript::HalfwidthKatakana));
        assert_eq!(detect_cjk_script(0xFF9D), Some(CJKScript::HalfwidthKatakana));
    }

    #[test]
    fn test_detect_hangul() {
        assert_eq!(detect_cjk_script(0xAC00), Some(CJKScript::Hangul));
        assert_eq!(detect_cjk_script(0xD7AF), Some(CJKScript::Hangul));
    }

    #[test]
    fn test_detect_non_cjk() {
        assert_eq!(detect_cjk_script(0x0041), None);
        assert_eq!(detect_cjk_script(0x0020), None);
        assert_eq!(detect_cjk_script(0x00E9), None);
    }

    #[test]
    fn test_infer_japanese_with_hiragana() {
        use xberg_native_pdf::text::script_detector::infer_document_language;
        let scripts = vec![(CJKScript::Han, 100), (CJKScript::Hiragana, 50)];
        assert_eq!(infer_document_language(&scripts), Some(DocumentLanguage::Japanese));
    }

    #[test]
    fn test_infer_japanese_with_katakana() {
        use xberg_native_pdf::text::script_detector::infer_document_language;
        let scripts = vec![(CJKScript::Han, 100), (CJKScript::Katakana, 30)];
        assert_eq!(infer_document_language(&scripts), Some(DocumentLanguage::Japanese));
    }

    #[test]
    fn test_infer_korean() {
        use xberg_native_pdf::text::script_detector::infer_document_language;
        let scripts = vec![(CJKScript::Hangul, 100), (CJKScript::Han, 20)];
        assert_eq!(infer_document_language(&scripts), Some(DocumentLanguage::Korean));
    }

    #[test]
    fn test_infer_chinese() {
        use xberg_native_pdf::text::script_detector::infer_document_language;
        let scripts = vec![(CJKScript::Han, 100)];
        assert_eq!(infer_document_language(&scripts), Some(DocumentLanguage::Chinese));
    }

    #[test]
    fn test_japanese_han_hiragana_no_split() {
        use xberg_native_pdf::text::script_detector::should_split_on_script_transition;
        let result = should_split_on_script_transition(
            Some(CJKScript::Han),
            Some(CJKScript::Hiragana),
            Some(DocumentLanguage::Japanese),
        );
        assert_eq!(result, Some(false));
    }

    #[test]
    fn test_japanese_hiragana_katakana_no_split() {
        use xberg_native_pdf::text::script_detector::should_split_on_script_transition;
        let result = should_split_on_script_transition(
            Some(CJKScript::Hiragana),
            Some(CJKScript::Katakana),
            Some(DocumentLanguage::Japanese),
        );
        assert_eq!(result, Some(false));
    }

    #[test]
    fn test_korean_hangul_han_no_split() {
        use xberg_native_pdf::text::script_detector::should_split_on_script_transition;
        let result = should_split_on_script_transition(
            Some(CJKScript::Hangul),
            Some(CJKScript::Han),
            Some(DocumentLanguage::Korean),
        );
        assert_eq!(result, Some(false));
    }

    #[test]
    fn test_cjk_to_latin_split() {
        use xberg_native_pdf::text::script_detector::should_split_on_script_transition;
        let result = should_split_on_script_transition(Some(CJKScript::Han), None, Some(DocumentLanguage::Chinese));
        assert_eq!(result, Some(true));
    }
}

mod japanese_rules_tests {
    use super::*;
    use xberg_native_pdf::text::script_detector::{
        is_combining_mark, is_japanese_modifier, is_small_hiragana, is_small_katakana,
    };

    #[test]
    fn test_small_hiragana_detection() {
        assert!(is_small_hiragana(0x3041));
        assert!(is_small_hiragana(0x3043));
        assert!(is_small_hiragana(0x3045));
        assert!(is_small_hiragana(0x3047));
        assert!(is_small_hiragana(0x3049));
    }

    #[test]
    fn test_small_tsu_hiragana() {
        assert!(is_small_hiragana(0x3063));
        assert!(is_japanese_modifier(0x3063));
    }

    #[test]
    fn test_small_ya_yu_yo_hiragana() {
        assert!(is_small_hiragana(0x3083));
        assert!(is_small_hiragana(0x3085));
        assert!(is_small_hiragana(0x3087));
    }

    #[test]
    fn test_normal_hiragana_not_small() {
        assert!(!is_small_hiragana(0x3042));
        assert!(!is_small_hiragana(0x304B));
    }

    #[test]
    fn test_small_katakana_detection() {
        assert!(is_small_katakana(0x30A1));
        assert!(is_small_katakana(0x30A3));
        assert!(is_small_katakana(0x30A5));
    }

    #[test]
    fn test_small_tsu_katakana() {
        assert!(is_small_katakana(0x30C3));
        assert!(is_japanese_modifier(0x30C3));
    }

    #[test]
    fn test_combining_marks() {
        assert!(is_combining_mark(0x3099));
        assert!(is_combining_mark(0x309A));
        assert!(is_japanese_modifier(0x3099));
    }

    #[test]
    fn test_halfwidth_combining_marks() {
        assert!(is_combining_mark(0xFF9E));
        assert!(is_combining_mark(0xFF9F));
    }

    #[test]
    fn test_no_boundary_before_small_hiragana() {
        let chars = vec![make_char(0x304B, 0.0, None), make_char(0x3083, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary before small ya ~keep
        assert!(
            boundaries.is_empty(),
            "Should not create boundary before small Hiragana"
        );
    }

    #[test]
    fn test_no_boundary_before_small_katakana() {
        let chars = vec![make_char(0x30AB, 0.0, None), make_char(0x30E3, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary before small ya ~keep
        assert!(
            boundaries.is_empty(),
            "Should not create boundary before small Katakana"
        );
    }

    #[test]
    fn test_hiragana_katakana_transition_no_split() {
        let chars = vec![make_char(0x3042, 0.0, None), make_char(0x30A2, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary between Hiragana and Katakana in Japanese ~keep
        assert!(boundaries.is_empty(), "Should not split Hiragana→Katakana in Japanese");
    }

    #[test]
    fn test_han_hiragana_transition_no_split() {
        let chars = vec![make_char(0x6587, 0.0, None), make_char(0x3042, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary between Han and Hiragana in Japanese ~keep
        assert!(boundaries.is_empty(), "Should not split Han→Hiragana in Japanese");
    }
}

mod korean_rules_tests {
    use super::*;

    #[test]
    fn test_hangul_han_transition_no_split() {
        let chars = vec![make_char(0xAC00, 0.0, None), make_char(0x6587, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary between Hangul and Han in Korean ~keep
        assert!(boundaries.is_empty(), "Should not split Hangul→Han in Korean");
    }

    #[test]
    fn test_han_hangul_transition_no_split() {
        let chars = vec![make_char(0x6587, 0.0, None), make_char(0xAC00, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary between Han and Hangul in Korean ~keep
        assert!(boundaries.is_empty(), "Should not split Han→Hangul in Korean");
    }

    #[test]
    fn test_korean_with_fullstop_punctuation() {
        let chars = vec![
            make_char(0xAC00, 0.0, None),
            make_char(0x3002, 12.0, None),
            make_char(0xAC01, 24.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should create boundary after fullstop ~keep
        assert!(boundaries.contains(&2), "Should create boundary after Korean fullstop");
    }

    #[test]
    fn test_korean_multi_hangul_syllables() {
        let chars = vec![
            make_char(0xAC00, 0.0, None),
            make_char(0xB098, 1.0, None),
            make_char(0xB2E4, 2.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        assert!(
            boundaries.is_empty(),
            "Should not split between Hangul syllables without spacing signals"
        );
    }

    #[test]
    fn test_korean_with_tj_offset() {
        let chars = vec![
            make_char(0xAC00, 0.0, None),
            make_char(0xB098, 12.0, Some(-150)),
            make_char(0xB2E4, 24.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // TJ offset should create boundary ~keep
        assert!(
            boundaries.contains(&2),
            "Should create boundary at TJ offset in Korean text"
        );
    }

    #[test]
    fn test_korean_hangul_to_latin() {
        let chars = vec![make_char(0xAC00, 0.0, None), make_char(0x0041, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should create boundary when switching to Latin ~keep
        assert!(boundaries.contains(&1), "Should create boundary from Hangul to Latin");
    }

    #[test]
    fn test_korean_latin_to_hangul() {
        let chars = vec![make_char(0x0041, 0.0, None), make_char(0xAC00, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should create boundary when switching from Latin ~keep
        assert!(boundaries.contains(&1), "Should create boundary from Latin to Hangul");
    }

    #[test]
    fn test_korean_mixed_hangul_hanja_word() {
        let chars = vec![
            make_char(0x5927, 0.0, None),
            make_char(0xD55C, 12.0, None),
            make_char(0x6C11, 24.0, None),
            make_char(0xAD6D, 36.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Korean);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundaries within mixed Hangul-Hanja word ~keep
        assert!(
            boundaries.is_empty(),
            "Should not split Hangul-Hanja transitions in Korean"
        );
    }
}

mod integration_tests {
    use super::*;

    #[test]
    fn test_chinese_with_punctuation() {
        let chars = vec![
            make_char(0x4F60, 0.0, None),
            make_char(0x597D, 12.0, None),
            make_char(0x3002, 24.0, None),
            make_char(0x4E16, 36.0, None),
            make_char(0x754C, 48.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Chinese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should create boundary after fullstop ~keep
        assert!(boundaries.contains(&3), "Should create boundary after Chinese fullstop");
    }

    #[test]
    fn test_japanese_mixed_scripts() {
        let chars = vec![
            make_char(0x6771, 0.0, None),
            make_char(0x4EAC, 1.0, None),
            make_char(0x3067, 2.0, None),
            make_char(0x3059, 3.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundaries in Japanese mixed script ~keep
        assert!(boundaries.is_empty(), "Should not split Japanese mixed Kanji-Hiragana");
    }

    #[test]
    fn test_japanese_with_small_tsu() {
        let chars = vec![
            make_char(0x304C, 0.0, None),
            make_char(0x3063, 1.0, None),
            make_char(0x3053, 2.0, None),
            make_char(0x3046, 3.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundary before or after small tsu ~keep
        assert!(boundaries.is_empty(), "Should not split Japanese word with small tsu");
    }

    #[test]
    fn test_japanese_katakana_word() {
        let chars = vec![
            make_char(0x30B3, 0.0, None),
            make_char(0x30F3, 1.0, None),
            make_char(0x30D4, 2.0, None),
            make_char(0x30E5, 3.0, None),
            make_char(0x30FC, 4.0, None),
            make_char(0x30BF, 5.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should not create boundaries within Katakana word ~keep
        assert!(boundaries.is_empty(), "Should not split Katakana word with small yu");
    }

    #[test]
    fn test_mixed_cjk_latin() {
        let chars = vec![
            make_char(0x0050, 0.0, None),
            make_char(0x0044, 12.0, None),
            make_char(0x0046, 24.0, None),
            make_char(0x6587, 36.0, None),
            make_char(0x4EF6, 48.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Chinese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Should create boundary between Latin and CJK ~keep
        assert!(boundaries.contains(&3), "Should create boundary from Latin to CJK");
    }

    #[test]
    fn test_cjk_with_space_character() {
        let chars = vec![
            make_char(0x4F60, 0.0, None),
            make_char(0x0020, 12.0, None),
            make_char(0x597D, 24.0, None),
        ];

        let context = BoundaryContext::new(12.0);
        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Chinese);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        // Space should create boundary ~keep
        assert!(boundaries.contains(&2), "Space should create boundary in CJK text");
    }

    #[test]
    fn test_language_inference_integration() {
        use xberg_native_pdf::text::script_detector::{detect_cjk_script, infer_document_language};

        let text_codes = vec![
            0x6771, 0x4EAC, 0x3067, 0x3059, 0x3002, 0x65E5, 0x672C, 0x8A9E, 0x3067, 0x3059,
        ];

        let scripts: Vec<_> = text_codes
            .iter()
            .filter_map(|&code| detect_cjk_script(code).map(|s| (s, 1)))
            .collect();

        let inferred_lang = infer_document_language(&scripts);
        assert_eq!(inferred_lang, Some(DocumentLanguage::Japanese));

        let detector = WordBoundaryDetector::new().with_document_language(DocumentLanguage::Japanese);

        let chars = vec![make_char(0x6771, 0.0, None), make_char(0x3067, 12.0, None)];

        let context = BoundaryContext::new(12.0);
        let boundaries = detector.detect_word_boundaries(&chars, &context);

        assert!(
            boundaries.is_empty(),
            "Inferred Japanese language should allow Kanji-Hiragana transition"
        );
    }
}
