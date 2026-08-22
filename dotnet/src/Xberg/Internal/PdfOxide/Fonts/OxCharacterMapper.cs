// Port of pdf_oxide 0.3.77 `fonts/character_mapper.rs` — CharacterMapper and
// PredefinedCMapConfig.
//
// Implements ISO 32000-1:2008 §9.10.2 Character-to-Unicode Mapping Priorities:
//   1. ToUnicode CMap (highest)
//   2. Adobe Glyph List
//   3. Predefined CMaps — CID-to-Unicode for CJK character collections
//   4. ActualText (handled externally, in BDC operator / structure-tree processing)
//   5. Font encoding (lowest)

using System.Text;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>
/// Seam for <c>fonts/cmap.rs</c>'s <c>CMap</c>, which is ported separately. Only the
/// code → Unicode lookup that <see cref="OxCharacterMapper"/> needs is declared here; the
/// concrete CMap is adapted onto this interface.
/// </summary>
internal interface IOxToUnicodeLookup
{
    /// <summary>The Unicode string mapped to <paramref name="code"/>, or null on a miss.</summary>
    string? Get(uint code);
}

/// <summary>
/// <c>PredefinedCMapConfig</c> — the character-collection ordering from CIDSystemInfo
/// ("GB1", "Japan1", "CNS1", "Korea1", "Arabic", "Persian", "Identity"), used to route a CID
/// into the right predefined CID → Unicode table (ISO 32000-1 §9.7.5.2).
/// </summary>
internal sealed class OxPredefinedCMapConfig
{
    internal OxPredefinedCMapConfig(string ordering)
    {
        Ordering = ordering;
    }

    internal string Ordering { get; }
}

/// <summary>
/// Character-to-Unicode mapper with the spec's five-level priority fallback chain.
/// </summary>
internal sealed class OxCharacterMapper
{
    /// <summary>
    /// Seam for <c>fonts/cid_mappings.rs</c> (the Adobe-GB1 / Japan1 / CNS1 / Korea1 / Arabic
    /// CID → Unicode tables), which is not part of this port slice. Takes the collection
    /// ordering and a CID and returns a Unicode codepoint. Left null, priority 3 resolves
    /// only the Identity ordering.
    /// </summary>
    internal static Func<string, ushort, uint?>? CidMappingLookup { get; set; }

    // Priority 1: ToUnicode CMap (explicit character code to Unicode mapping).
    private IOxToUnicodeLookup? _toUnicodeCMap;

    // Priority 3: predefined CMap config for CID-to-Unicode lookup.
    private OxPredefinedCMapConfig? _predefinedCMap;

    // Priority 5: font encoding (character code to character).
    private Dictionary<uint, Rune>? _fontEncoding;

    internal void SetToUnicodeCMap(IOxToUnicodeLookup? cmap) => _toUnicodeCMap = cmap;

    internal void SetPredefinedCMap(OxPredefinedCMapConfig? config) => _predefinedCMap = config;

    internal void SetFontEncoding(Dictionary<uint, Rune>? encoding) => _fontEncoding = encoding;

    /// <summary>
    /// <c>map_character</c> — map a character code to a Unicode string through the priority
    /// chain. Never returns null: an exhausted chain yields U+FFFD per §9.10.2.
    /// </summary>
    internal string? MapCharacter(uint code)
    {
        // Priority 1: ToUnicode CMap.
        //
        // When a ToUnicode CMap is attached it is the authoritative mapping, so a miss must
        // produce U+FFFD. Falling back to AGL or a CID-as-Unicode heuristic produces
        // "ciphertext" on subset Type0 fonts whose CIDs are insertion-ordered rather than
        // codepoint-aligned.
        if (_toUnicodeCMap is not null)
        {
            return _toUnicodeCMap.Get(code) ?? "�";
        }

        // Priority 2: Adobe Glyph List (standard glyph name for the code).
        string? glyphName = CodeToGlyphName(code);
        if (glyphName is not null)
        {
            string? unicodeStr = MapGlyphNameInternal(glyphName);
            if (unicodeStr is not null)
            {
                return unicodeStr;
            }
        }

        // Priority 3: predefined CMaps (CID-to-Unicode for CJK character collections).
        if (_predefinedCMap is not null)
        {
            string? unicodeStr = LookupPredefinedCMap(_predefinedCMap, code);
            if (unicodeStr is not null)
            {
                return unicodeStr;
            }
        }

        // Priority 4: ActualText — handled externally.

        // Priority 5: font encoding.
        if (_fontEncoding is not null && _fontEncoding.TryGetValue(code, out Rune ch))
        {
            return ch.ToString();
        }

        return "�";
    }

    /// <summary>
    /// <c>lookup_predefined_cmap</c> — route a CID through the character-collection ordering.
    /// "Identity" treats the CID as a direct Unicode codepoint (Identity-H/V).
    /// </summary>
    private static string? LookupPredefinedCMap(OxPredefinedCMapConfig config, uint code)
    {
        // CIDs are 16-bit values.
        ushort cid = (ushort)code;

        uint? unicodeCodepoint;
        switch (config.Ordering)
        {
            case "GB1":
            case "Japan1":
            case "CNS1":
            case "Korea1":
            // Adobe-Arabic-1 / Adobe-Persian-1 (Nazanin, Yagut, Mitra, Lotus) without
            // /ToUnicode need the §9.10.3 step-3 identity fallback over the Arabic block;
            // without it these decode as Latin-Extended-B garbage.
            case "Arabic":
            case "Persian":
                unicodeCodepoint = CidMappingLookup?.Invoke(config.Ordering, cid);
                break;
            case "Identity":
                // Identity mapping: CID == Unicode codepoint, valid for the BMP.
                unicodeCodepoint = code <= 0xFFFF ? code : null;
                break;
            default:
                unicodeCodepoint = null;
                break;
        }

        if (unicodeCodepoint is null || !Rune.IsValid((int)unicodeCodepoint.Value))
        {
            return null;
        }
        return new Rune(unicodeCodepoint.Value).ToString();
    }

    /// <summary><c>map_glyph_name</c> — the Adobe Glyph List lookup for a named glyph.</summary>
    internal string? MapGlyphName(string glyphName) => MapGlyphNameInternal(glyphName);

    private static string? MapGlyphNameInternal(string glyphName) =>
        OxGlyphNames.TryLookupAgl(glyphName, out char ch) ? ch.ToString() : null;

    /// <summary>
    /// <c>glyph_name_to_unicode</c> — the unified AGL-spec §6 chain, re-exported here so
    /// callers that reason in character_mapper terms find it under the Rust name.
    /// </summary>
    internal static string? GlyphNameToUnicode(string glyphName) =>
        OxGlyphNames.GlyphNameToUnicodeUnified(glyphName);

    /// <summary><c>code_to_glyph_name</c> — ASCII first, then the WinAnsi extended range.</summary>
    private string? CodeToGlyphName(uint code) =>
        code <= 0x7E ? CodeToGlyphNameAscii(code) : CodeToGlyphNameExtended(code);

    /// <summary><c>code_to_glyph_name_ascii</c> — 0x20-0x7E to standard glyph names.</summary>
    private static string? CodeToGlyphNameAscii(uint code)
    {
        if (code >= 0x41 && code <= 0x5A)
        {
            return ((char)code).ToString();
        }
        if (code >= 0x61 && code <= 0x7A)
        {
            return ((char)code).ToString();
        }
        return code switch
        {
            0x20 => "space",
            0x21 => "exclam",
            0x22 => "quotedbl",
            0x23 => "numbersign",
            0x24 => "dollar",
            0x25 => "percent",
            0x26 => "ampersand",
            0x27 => "quoteright",
            0x28 => "parenleft",
            0x29 => "parenright",
            0x2A => "asterisk",
            0x2B => "plus",
            0x2C => "comma",
            0x2D => "hyphen",
            0x2E => "period",
            0x2F => "slash",
            // Digits use the glyph names "zero" through "nine"
            0x30 => "zero",
            0x31 => "one",
            0x32 => "two",
            0x33 => "three",
            0x34 => "four",
            0x35 => "five",
            0x36 => "six",
            0x37 => "seven",
            0x38 => "eight",
            0x39 => "nine",
            0x3A => "colon",
            0x3B => "semicolon",
            0x3C => "less",
            0x3D => "equal",
            0x3E => "greater",
            0x3F => "question",
            0x40 => "at",
            0x5B => "bracketleft",
            0x5C => "backslash",
            0x5D => "bracketright",
            0x5E => "asciicircum",
            0x5F => "underscore",
            0x60 => "grave",
            0x7B => "braceleft",
            0x7C => "bar",
            0x7D => "braceright",
            0x7E => "asciitilde",
            _ => null,
        };
    }

    /// <summary>
    /// <c>code_to_glyph_name_extended</c> — 0x80-0xFF to glyph names via WinAnsiEncoding
    /// (Windows-1252), the common fallback in PDFs. Per ISO 32000-1:2008 §9.6.6.1.
    /// </summary>
    internal string? CodeToGlyphNameExtended(uint code) => code switch
    {
        // 0x80-0x8F: WinAnsiEncoding specials
        0x80 => "Euro",           // U+20AC
        0x81 => null,             // Undefined in WinAnsiEncoding
        0x82 => "quotesinglbase", // U+201A
        0x83 => "florin",         // U+0192
        0x84 => "quotedblbase",   // U+201E
        0x85 => "ellipsis",       // U+2026
        0x86 => "dagger",         // U+2020
        0x87 => "daggerdbl",      // U+2021
        0x88 => "circumflex",     // U+02C6
        0x89 => "perthousand",    // U+2030
        0x8A => "Scaron",         // U+0160
        0x8B => "guilsinglleft",  // U+2039
        0x8C => "OEligature",     // U+0152
        0x8D => null,             // Undefined
        0x8E => "Zcaron",         // U+017D
        0x8F => null,             // Undefined

        // 0x90-0x9F: more WinAnsiEncoding specials
        0x90 => null,             // Undefined
        0x91 => "quoteleft",      // U+2018
        0x92 => "quoteright",     // U+2019
        0x93 => "quotedblleft",   // U+201C
        0x94 => "quotedblright",  // U+201D
        0x95 => "bullet",         // U+2022
        0x96 => "endash",         // U+2013
        0x97 => "emdash",         // U+2014
        0x98 => "tilde",          // U+02DC
        0x99 => "trademark",      // U+2122
        0x9A => "scaron",         // U+0161
        0x9B => "guilsinglright", // U+203A
        0x9C => "oeligature",     // U+0153
        0x9D => null,             // Undefined
        0x9E => "zcaron",         // U+017E
        0x9F => "ydieresis",      // U+0178

        // 0xA0-0xBF: Latin-1 Supplement punctuation and symbols
        0xA0 => "space",          // Non-breaking space U+00A0
        0xA1 => "exclamdown",
        0xA2 => "cent",
        0xA3 => "sterling",
        0xA4 => "currency",
        0xA5 => "yen",
        0xA6 => "brokenbar",
        0xA7 => "section",
        0xA8 => "dieresis",
        0xA9 => "copyright",
        0xAA => "ordfeminine",
        0xAB => "guillemotleft",
        0xAC => "logicalnot",
        0xAD => "hyphen",         // Soft hyphen U+00AD
        0xAE => "registered",
        0xAF => "macron",
        0xB0 => "degree",
        0xB1 => "plusminus",
        0xB2 => "twosuperior",
        0xB3 => "threesuperior",
        0xB4 => "acute",
        0xB5 => "mu",
        0xB6 => "paragraph",
        0xB7 => "periodcentered",
        0xB8 => "cedilla",
        0xB9 => "onesuperior",
        0xBA => "ordmasculine",
        0xBB => "guillemotright",
        0xBC => "onequarter",
        0xBD => "onehalf",
        0xBE => "threequarters",
        0xBF => "questiondown",

        // 0xC0-0xFF: accented uppercase and lowercase letters
        0xC0 => "Agrave",
        0xC1 => "Aacute",
        0xC2 => "Acircumflex",
        0xC3 => "Atilde",
        0xC4 => "Adieresis",
        0xC5 => "Aring",
        0xC6 => "AEligature",
        0xC7 => "Ccedilla",
        0xC8 => "Egrave",
        0xC9 => "Eacute",
        0xCA => "Ecircumflex",
        0xCB => "Edieresis",
        0xCC => "Igrave",
        0xCD => "Iacute",
        0xCE => "Icircumflex",
        0xCF => "Idieresis",
        0xD0 => "Eth",
        0xD1 => "Ntilde",
        0xD2 => "Ograve",
        0xD3 => "Oacute",
        0xD4 => "Ocircumflex",
        0xD5 => "Otilde",
        0xD6 => "Odieresis",
        0xD7 => "multiply",
        0xD8 => "Oslash",
        0xD9 => "Ugrave",
        0xDA => "Uacute",
        0xDB => "Ucircumflex",
        0xDC => "Udieresis",
        0xDD => "Yacute",
        0xDE => "Thorn",
        0xDF => "germandbls",
        0xE0 => "agrave",
        0xE1 => "aacute",
        0xE2 => "acircumflex",
        0xE3 => "atilde",
        0xE4 => "adieresis",
        0xE5 => "aring",
        0xE6 => "aeligature",
        0xE7 => "ccedilla",
        0xE8 => "egrave",
        0xE9 => "eacute",
        0xEA => "ecircumflex",
        0xEB => "edieresis",
        0xEC => "igrave",
        0xED => "iacute",
        0xEE => "icircumflex",
        0xEF => "idieresis",
        0xF0 => "eth",
        0xF1 => "ntilde",
        0xF2 => "ograve",
        0xF3 => "oacute",
        0xF4 => "ocircumflex",
        0xF5 => "otilde",
        0xF6 => "odieresis",
        0xF7 => "divide",
        0xF8 => "oslash",
        0xF9 => "ugrave",
        0xFA => "uacute",
        0xFB => "ucircumflex",
        0xFC => "udieresis",
        0xFD => "yacute",
        0xFE => "thorn",
        0xFF => "ydieresis",

        _ => null,
    };
}
