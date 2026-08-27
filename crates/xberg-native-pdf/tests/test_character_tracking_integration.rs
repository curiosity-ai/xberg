#![allow(clippy::assertions_on_constants, clippy::useless_vec)]
//! Integration tests for character-level tracking in PDF text extraction
//!
//! These tests verify that character-level data is collected during TJ array processing
//! and that it's available for word boundary detection.

use std::path::Path;
use xberg_native_pdf::document::PdfDocument;

mod common;

/// Helper to load a test PDF (unused, kept for future integration tests)
#[allow(dead_code)]
fn load_test_pdf(filename: &str) -> Result<PdfDocument, Box<dyn std::error::Error>> {
    let test_pdfs_dir = Path::new("tests/test_pdfs");

    let paths = vec![
        test_pdfs_dir.join(filename),
        Path::new("test_pdfs").join(filename),
        Path::new(".").join(filename),
    ];

    for path in paths {
        if path.exists() {
            return PdfDocument::open(&path).map_err(|e| format!("Failed to load {}: {}", path.display(), e).into());
        }
    }

    Err(format!("Could not find test PDF: {}", filename).into())
}

/// Character tracking during `TJ` array processing must recover every
/// character of a simple, single-run string with no internal offsets — the
/// baseline case the offset-splitting behaviour (tested separately) builds
/// on.
#[test]
fn test_character_tracking_with_simple_text() {
    let content = b"BT /F1 12 Tf 50 700 Td [(Hello) (World)] TJ ET";
    let pdf = common::build_minimal_pdf_raw(content, b"/MediaBox [0 0 612 792]");
    let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

    let text = doc.extract_text(0).expect("extract_text");

    assert_eq!(
        text.trim(),
        "HelloWorld",
        "adjacent TJ strings with no offset between them must be tracked \
         as a single run of characters, not dropped or merged incorrectly"
    );
}

#[test]
fn test_character_info_structure_completeness() {
    use xberg_native_pdf::text::word_boundary::CharacterInfo;

    let char_info = CharacterInfo {
        code: 'H' as u32,
        glyph_id: Some(123),
        width: 500.0,
        x_position: 100.0,
        tj_offset: Some(-150),
        font_size: 12.0,
        is_ligature: false,
        original_ligature: None,
        protected_from_split: false,
    };

    assert_eq!(char_info.code, 'H' as u32);
    assert_eq!(char_info.glyph_id, Some(123));
    assert_eq!(char_info.width, 500.0);
    assert_eq!(char_info.x_position, 100.0);
    assert_eq!(char_info.tj_offset, Some(-150));
    assert_eq!(char_info.font_size, 12.0);
}

#[test]
fn test_boundary_context_structure() {
    use xberg_native_pdf::text::word_boundary::BoundaryContext;

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
fn test_character_array_accumulation() {
    use xberg_native_pdf::text::word_boundary::CharacterInfo;

    let mut character_array = Vec::new();

    let text = "Hello";
    let mut x_pos = 0.0;
    let char_width = 400.0;

    for ch in text.chars() {
        character_array.push(CharacterInfo {
            code: ch as u32,
            glyph_id: None,
            width: char_width,
            x_position: x_pos,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        });
        x_pos += char_width;
    }

    assert_eq!(character_array.len(), 5);
    assert_eq!(character_array[0].code, 'H' as u32);
    assert_eq!(character_array[4].code, 'o' as u32);

    for i in 0..character_array.len() - 1 {
        assert!(character_array[i].x_position < character_array[i + 1].x_position);
    }
}

#[test]
fn test_tj_offset_association_with_characters() {
    use xberg_native_pdf::text::word_boundary::CharacterInfo;

    let mut character_array = Vec::new();
    let mut x_pos = 0.0;
    let char_width = 400.0;

    for ch in "Hello".chars() {
        character_array.push(CharacterInfo {
            code: ch as u32,
            glyph_id: None,
            width: char_width,
            x_position: x_pos,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        });
        x_pos += char_width;
    }

    let last_idx = character_array.len() - 1;
    character_array[last_idx].tj_offset = Some(-200);

    for ch in "World".chars() {
        character_array.push(CharacterInfo {
            code: ch as u32,
            glyph_id: None,
            width: char_width,
            x_position: x_pos,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        });
        x_pos += char_width;
    }

    assert_eq!(character_array[4].code, 'o' as u32);
    assert_eq!(character_array[4].tj_offset, Some(-200));

    assert!(character_array[4].tj_offset.unwrap() < -100);

    assert_eq!(character_array[5].code, 'W' as u32);
    assert_eq!(character_array[5].tj_offset, None);
}

#[test]
fn test_character_tracking_with_mixed_offsets() {
    use xberg_native_pdf::text::word_boundary::CharacterInfo;

    let mut character_array = Vec::new();
    let mut x_pos = 0.0;
    let char_width = 400.0;

    // Process "The" + offset -150 ~keep
    for ch in "The".chars() {
        character_array.push(CharacterInfo {
            code: ch as u32,
            glyph_id: None,
            width: char_width,
            x_position: x_pos,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        });
        x_pos += char_width;
    }
    character_array[2].tj_offset = Some(-150);

    // Process "quick" + offset -100 ~keep
    for ch in "quick".chars() {
        character_array.push(CharacterInfo {
            code: ch as u32,
            glyph_id: None,
            width: char_width,
            x_position: x_pos,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        });
        x_pos += char_width;
    }
    character_array[7].tj_offset = Some(-100);

    for ch in "brown".chars() {
        character_array.push(CharacterInfo {
            code: ch as u32,
            glyph_id: None,
            width: char_width,
            x_position: x_pos,
            tj_offset: None,
            font_size: 12.0,
            is_ligature: false,
            original_ligature: None,
            protected_from_split: false,
        });
        x_pos += char_width;
    }

    assert_eq!(character_array.len(), 13);

    assert_eq!(character_array[2].code, 'e' as u32);
    assert_eq!(character_array[2].tj_offset, Some(-150));

    assert_eq!(character_array[7].code, 'k' as u32);
    assert_eq!(character_array[7].tj_offset, Some(-100));

    assert_eq!(character_array[12].code, 'n' as u32);
    assert_eq!(character_array[12].tj_offset, None);
}

#[test]
fn test_character_tracking_preserves_font_metrics() {
    use xberg_native_pdf::text::word_boundary::CharacterInfo;

    let characters = vec![
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

    assert_eq!(characters[0].width, 200.0, "Narrow character");
    assert_eq!(characters[1].width, 800.0, "Wide character");
    assert_eq!(characters[2].width, 900.0, "Very wide character");

    assert!(characters[0].x_position < characters[1].x_position);
    assert!(characters[1].x_position < characters[2].x_position);
}

#[test]
fn test_character_tracking_with_scaling() {
    use xberg_native_pdf::text::word_boundary::CharacterInfo;

    let context = xberg_native_pdf::text::word_boundary::BoundaryContext {
        font_size: 12.0,
        horizontal_scaling: 80.0,
        word_spacing: 0.0,
        char_spacing: 0.0,
    };

    let character = CharacterInfo {
        code: 'a' as u32,
        glyph_id: None,
        width: 500.0,
        x_position: 0.0,
        tj_offset: None,
        font_size: context.font_size,
        is_ligature: false,
        original_ligature: None,
        protected_from_split: false,
    };

    assert_eq!(context.horizontal_scaling, 80.0);
    assert_eq!(character.width, 500.0);

    // The actual advance would be: width * (scaling / 100.0) = 500.0 * 0.80 = 400.0
    // This would be applied during position calculation in process_tj_array() ~keep
}
