//! TDD Tests for Extended ASCII Character Mapping in CharacterMapper
//!
//! Tests verify that character codes in the extended ASCII range (0x80-0xFF)
//! are properly mapped using WinAnsiEncoding (Windows-1252) fallback.

#[test]
fn test_extended_ascii_en_dash() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x96);
    assert_eq!(result, Some("endash".to_string()));
}

#[test]
fn test_extended_ascii_em_dash() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x97);
    assert_eq!(result, Some("emdash".to_string()));
}

#[test]
fn test_extended_ascii_left_quote() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x93);
    assert_eq!(result, Some("quotedblleft".to_string()));
}

#[test]
fn test_extended_ascii_right_quote() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x94);
    assert_eq!(result, Some("quotedblright".to_string()));
}

#[test]
fn test_extended_ascii_ellipsis() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x85);
    assert_eq!(result, Some("ellipsis".to_string()));
}

#[test]
fn test_extended_ascii_copyright() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    // 0xA9 in WinAnsiEncoding = copyright sign (©, U+00A9)
    let result = mapper.code_to_glyph_name_extended(0xA9);
    assert_eq!(result, Some("copyright".to_string()));
}

#[test]
fn test_extended_ascii_registered() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0xAE);
    assert_eq!(result, Some("registered".to_string()));
}

#[test]
fn test_extended_ascii_trademark() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x99);
    assert_eq!(result, Some("trademark".to_string()));
}

#[test]
fn test_extended_ascii_degree() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0xB0);
    assert_eq!(result, Some("degree".to_string()));
}

#[test]
fn test_extended_ascii_german_ae() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0xE4);
    assert_eq!(result, Some("adieresis".to_string()));
}

#[test]
fn test_extended_ascii_french_c_cedilla() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0xE7);
    assert_eq!(result, Some("ccedilla".to_string()));
}

#[test]
fn test_extended_ascii_euro() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x80);
    assert_eq!(result, Some("Euro".to_string()));
}

#[test]
fn test_map_character_with_extended_ascii() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mut mapper = CharacterMapper::new();

    mapper.set_font_encoding(None);

    let result = mapper.map_character(0x96);
    assert!(result.is_some());
    let mapped = result.unwrap();
    assert!(!mapped.is_empty());
}

#[test]
fn test_extended_ascii_fallback_with_custom_encoding() {
    use std::collections::HashMap;
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mut mapper = CharacterMapper::new();

    let mut encoding = HashMap::new();
    encoding.insert(0x41, 'A');
    mapper.set_font_encoding(Some(encoding));

    let result = mapper.map_character(0x41);
    assert_eq!(result, Some("A".to_string()));

    let result = mapper.map_character(0x96);
    assert!(result.is_some());
}

#[test]
fn test_extended_ascii_common_special_chars() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let test_cases = vec![
        (0x80, "Euro"),
        (0x85, "ellipsis"),
        (0x93, "quotedblleft"),
        (0x94, "quotedblright"),
        (0x96, "endash"),
        (0x97, "emdash"),
    ];

    for (code, expected_glyph) in test_cases {
        let result = mapper.code_to_glyph_name_extended(code);
        assert_eq!(
            result,
            Some(expected_glyph.to_string()),
            "Failed for code 0x{:02X}",
            code
        );
    }
}

#[test]
fn test_extended_ascii_invalid_codes() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0x01);
    assert_eq!(result, None);

    let result = mapper.code_to_glyph_name_extended(0x81);
    assert_eq!(result, None);
}

#[test]
fn test_extended_ascii_currency_symbols() {
    use xberg_native_pdf::fonts::character_mapper::CharacterMapper;

    let mapper = CharacterMapper::new();

    let result = mapper.code_to_glyph_name_extended(0xA4);
    assert_eq!(result, Some("currency".to_string()));

    let result = mapper.code_to_glyph_name_extended(0xA5);
    assert_eq!(result, Some("yen".to_string()));

    let result = mapper.code_to_glyph_name_extended(0xA3);
    assert_eq!(result, Some("sterling".to_string()));
}
