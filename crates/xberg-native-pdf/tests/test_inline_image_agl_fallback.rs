//! Unified ToUnicode / AGL fallback chain reaches every glyph-name lookup
//! site, including any future inline-image font-resolution callsite
//! (PDF spec §8.9.7).
//!
//! A robust ToUnicode + embedded-TrueType `cmap` + Adobe Glyph List +
//! synthetic `uniXXXX` / `uXXXXX` fallback chain lives in
//! `src/fonts/character_mapper.rs::glyph_name_to_unicode`. Its original
//! consumer was the full-document Type0 / Identity-H path in
//! `font_dict.rs::Font::char_code_to_unicode` (Priority 3c).
//!
//! Simple fonts, Type1 / CFF embedded encodings, and `/Differences` arrays
//! all routed through `font_dict::glyph_name_to_unicode` — which used to lack
//! that chain's variant-suffix stripping (`.alt`, `.sc`, `.001`). It now
//! delegates to the unified chain as a final fallback so every consumer
//! shares the same behaviour.
//!
//! # Inline-image text path (PDF spec §8.9.7)
//!
//! Per ISO 32000-1:2008 §8.9.7, the `BI ... ID ... EI` block carries image
//! data only — no text-drawing operators are legal inside. The parser at
//! `src/content/parser.rs::parse_inline_image` produces an
//! `Operator::InlineImage { dict, data }` value that the text extractor
//! discards (see `src/extractors/text.rs:4804`). No dedicated text-resolution
//! codepath exists today for inline-image content; if one is added later, it
//! will route glyph-name lookups through `font_dict::glyph_name_to_unicode`
//! and inherit the unified chain automatically.
//!
//! No integration test exercises an inline-image font reference end-to-end:
//! as noted above, no text-resolution codepath consumes inline-image glyph
//! names today, so there is nothing for such a test to call. The realistic
//! risk — a variant-suffix-stripping miss on subset fonts — is covered
//! instead by the unit tests in `src/fonts/font_dict.rs::tests`. Add an
//! integration test here once an inline-image text-resolution codepath
//! exists to exercise.

use xberg_native_pdf::fonts::CharacterMapper;

/// `CharacterMapper::map_glyph_name` is the public AGL lookup. It hits the
/// Adobe Glyph List exact match — the canonical case for inline-image fonts
/// that expose only glyph-name lookups.
#[test]
fn inline_image_text_with_no_tounicode_resolves_via_agl() {
    let mapper = CharacterMapper::new();
    assert_eq!(mapper.map_glyph_name("bullet"), Some("\u{2022}".to_string()));
    assert_eq!(mapper.map_glyph_name("fi"), Some("\u{FB01}".to_string()));
    assert_eq!(mapper.map_glyph_name("fl"), Some("\u{FB02}".to_string()));
    assert_eq!(mapper.map_glyph_name("A"), Some("A".to_string()));
    assert_eq!(mapper.map_glyph_name("space"), Some(" ".to_string()));
}

/// `CharacterMapper::map_character` returns U+FFFD when the chain has no
/// signal — matches the §9.10.2 posture for ToUnicode-miss cases.
/// Inline-image fonts that genuinely have no decodable glyph emit U+FFFD by
/// design; the fix is for the *recoverable* glyph-name cases.
#[test]
fn inline_image_text_without_glyph_name_falls_back_to_replacement() {
    let mapper = CharacterMapper::new();
    // No glyph-name path for a code beyond the WinAnsi range and with no
    // encoding / ToUnicode set → U+FFFD per §9.10.2. ~keep
    let result = mapper.map_character(0xE000);
    assert_eq!(result, Some("\u{FFFD}".to_string()));
}

/// A ToUnicode CMap, when present, wins regardless of how the font is
/// referenced (full-document or inline image). The chain checks ToUnicode
/// first; this is the no-regression guarantee for fonts that already have
/// proper Unicode mappings.
#[test]
fn inline_image_text_with_tounicode_unchanged_post_fix() {
    use xberg_native_pdf::fonts::parse_tounicode_cmap;
    let mut mapper = CharacterMapper::new();

    let cmap_data = b"/CIDInit /ProcSet findresource begin\n\
        12 dict begin\n\
        begincmap\n\
        /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n\
        /CMapName /Adobe-Identity-UCS def\n\
        1 beginbfchar\n\
        <0042> <0042>\n\
        endbfchar\n\
        endcmap\n\
        CMapName currentdict /CMap defineresource pop\n\
        end\n\
        end";
    mapper.set_tounicode_cmap(Some(parse_tounicode_cmap(cmap_data).unwrap()));

    assert_eq!(mapper.map_character(0x0042), Some("B".to_string()));
}
