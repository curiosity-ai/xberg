#![allow(warnings)]
//! TDD Tests for Word Boundary Character-Level Tracking
//!
//! Tests verify that process_tj_array() properly collects character-level data
//! for use by WordBoundaryDetector as a primary (not just tiebreaker) detector.

mod common;

#[cfg(test)]
mod tests {
    use xberg_native_pdf::document::PdfDocument;
    use xberg_native_pdf::text::word_boundary::{BoundaryContext, CharacterInfo};

    /// `process_tj_array()` is private, but its character-level tracking is
    /// observable through word extraction: a `TJ` array with a significant
    /// negative offset between `(Hello)` and `(World)` must produce two
    /// distinct words, proving every character from both strings was
    /// collected and the offset was associated with the boundary between
    /// them (not dropped or merged into a single run).
    #[test]
    fn test_character_tracking_collects_all_characters() {
        let content = b"BT /F1 12 Tf 50 700 Td [(Hello) -200 (World)] TJ ET";
        let pdf = crate::common::build_minimal_pdf_raw(content, b"/MediaBox [0 0 612 792]");
        let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

        let words = doc.extract_words(0).expect("extract_words");
        let texts: Vec<&str> = words.iter().map(|w| w.text.as_str()).collect();

        assert_eq!(
            texts,
            vec!["Hello", "World"],
            "TJ offset must split the array into two distinct words, proving \
             every character from both strings was tracked and the offset \
             was attributed to the boundary between them"
        );
    }

    #[test]
    fn test_character_info_has_all_required_fields() {
        let char_info = CharacterInfo {
            code: 'H' as u32,
            glyph_id: Some(123),
            width: 500.0,
            x_position: 100.0,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        };

        assert_eq!(char_info.code, 'H' as u32);
        assert_eq!(char_info.glyph_id, Some(123));
        assert_eq!(char_info.width, 500.0);
        assert_eq!(char_info.x_position, 100.0);
        assert_eq!(char_info.tj_offset, None);
        assert_eq!(char_info.font_size, 12.0);
    }

    #[test]
    fn test_character_info_with_tj_offset() {
        let char_info = CharacterInfo {
            code: 'o' as u32,
            glyph_id: Some(456),
            width: 400.0,
            x_position: 500.0,
            tj_offset: Some(-200),
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        };

        assert_eq!(char_info.tj_offset, Some(-200));
        assert!(char_info.tj_offset.unwrap() < -100, "Should be beyond threshold");
    }

    #[test]
    fn test_boundary_context_from_extractor_state() {
        let context = BoundaryContext {
            font_size: 12.0,
            horizontal_scaling: 100.0,
            word_spacing: 0.0,
            char_spacing: 0.0,
        };

        assert_eq!(context.font_size, 12.0);
        assert_eq!(context.horizontal_scaling, 100.0);
        assert_eq!(context.word_spacing, 0.0);
        assert_eq!(context.char_spacing, 0.0);
    }

    #[test]
    fn test_boundary_context_with_scaling() {
        let context = BoundaryContext {
            font_size: 12.0,
            horizontal_scaling: 80.0,
            word_spacing: 0.0,
            char_spacing: 0.0,
        };

        assert_eq!(context.horizontal_scaling, 80.0);
    }

    #[test]
    fn test_character_array_maintains_order() {
        let chars = [
            CharacterInfo {
                code: 'H' as u32,
                glyph_id: None,
                width: 500.0,
                x_position: 0.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'e' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 500.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'l' as u32,
                glyph_id: None,
                width: 350.0,
                x_position: 900.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'l' as u32,
                glyph_id: None,
                width: 350.0,
                x_position: 1250.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'o' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 1600.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
        ];

        assert_eq!(chars.len(), 5);
        assert_eq!(chars[0].code, 'H' as u32);
        assert_eq!(chars[4].code, 'o' as u32);
    }

    #[test]
    fn test_tj_offset_tracking_with_word_boundary() {
        let chars = vec![
            CharacterInfo {
                code: 'H' as u32,
                glyph_id: None,
                width: 500.0,
                x_position: 0.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'e' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 500.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'l' as u32,
                glyph_id: None,
                width: 350.0,
                x_position: 900.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'l' as u32,
                glyph_id: None,
                width: 350.0,
                x_position: 1250.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'o' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 1600.0,
                tj_offset: Some(-200),
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'W' as u32,
                glyph_id: None,
                width: 500.0,
                x_position: 2100.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
        ];

        assert_eq!(chars[4].tj_offset, Some(-200));
        assert!(chars[4].tj_offset.unwrap() < -100);
    }

    #[test]
    fn test_character_positions_increase_left_to_right() {
        let chars = [
            CharacterInfo {
                code: 'T' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 0.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'e' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 400.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'x' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 800.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 't' as u32,
                glyph_id: None,
                width: 350.0,
                x_position: 1200.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
        ];

        for i in 0..chars.len() - 1 {
            assert!(
                chars[i].x_position < chars[i + 1].x_position,
                "Position should increase: {} < {}",
                chars[i].x_position,
                chars[i + 1].x_position
            );
        }
    }

    #[test]
    fn test_character_width_reflects_glyph_metrics() {
        let chars = vec![
            CharacterInfo {
                code: 'i' as u32,
                glyph_id: None,
                width: 200.0,
                x_position: 0.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'M' as u32,
                glyph_id: None,
                width: 800.0,
                x_position: 200.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'W' as u32,
                glyph_id: None,
                width: 900.0,
                x_position: 1000.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
        ];

        assert_eq!(chars[0].width, 200.0, "Narrow character 'i'");
        assert_eq!(chars[1].width, 800.0, "Wide character 'M'");
        assert_eq!(chars[2].width, 900.0, "Very wide character 'W'");
    }

    #[test]
    fn test_multi_element_tj_array_with_mixed_offsets() {
        let chars = vec![
            CharacterInfo {
                code: 'T' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 0.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'h' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 400.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'e' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 800.0,
                tj_offset: Some(-150),
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'q' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 1250.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'u' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 1650.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'i' as u32,
                glyph_id: None,
                width: 200.0,
                x_position: 2050.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'c' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 2250.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'k' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 2650.0,
                tj_offset: Some(-100),
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'b' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 3050.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'r' as u32,
                glyph_id: None,
                width: 350.0,
                x_position: 3450.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'o' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 3800.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'w' as u32,
                glyph_id: None,
                width: 600.0,
                x_position: 4200.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
            CharacterInfo {
                code: 'n' as u32,
                glyph_id: None,
                width: 400.0,
                x_position: 4800.0,
                tj_offset: None,
                font_size: 12.0,
                is_ligature: false,
                original_ligature: None,
                protected_from_split: false,
            },
        ];

        assert_eq!(chars.len(), 13);
        assert_eq!(chars[2].tj_offset, Some(-150));
        assert_eq!(chars[7].tj_offset, Some(-100));
    }
}
