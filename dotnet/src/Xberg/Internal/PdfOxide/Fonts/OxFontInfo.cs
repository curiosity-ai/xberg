// Ported from pdf_oxide `fonts/font_dict.rs`:
//   the FontInfo struct (lines 37-217), truetype_cmap / set_truetype_cmap /
//   has_truetype_cmap (357-405), best_mapping_provenance (407-453),
//   embedded_glyph_name (455-533), glyph_name_for_code (534-580),
//   gid_to_standard_glyph_name (3650-3935), get_byte_to_char_table (3937-3959),
//   char_to_unicode / char_to_unicode_uncached (3990-4782),
//   is_italic (4907-4921), is_symbolic (4923-4975), get_encoded_char (4948-5017)
//   and has_custom_encoding (5019-5028).
//
// The Rust memoizes with OnceLock/Mutex; the extractor is single-threaded per page,
// so these are plain lazily-initialised fields with no locking.
using System;
using System.Collections.Generic;
using Xberg.Internal.Pdf;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>Font information extracted from a PDF font dictionary.</summary>
internal sealed partial class OxFontInfo
{
    /// <summary>Base font name (e.g. "Times-Roman", "Helvetica-Bold").</summary>
    public string BaseFont = "Unknown";
    /// <summary>Font subtype (e.g. "Type1", "TrueType", "Type0").</summary>
    public string Subtype = "Unknown";
    public OxEncoding Encoding = OxEncoding.Standard("StandardEncoding");
    /// <summary>ToUnicode CMap, parsed on first character lookup.</summary>
    public IOxCMap? ToUnicode;
    /// <summary>/FontWeight from the FontDescriptor (400 = normal, 700 = bold).</summary>
    public int? FontWeight;
    /// <summary>
    /// FontDescriptor /Flags bit field (Table 123): bit 1 FixedPitch, 2 Serif, 3 Symbolic,
    /// 4 Script, 6 Nonsymbolic, 7 Italic, 19 ForceBold.
    /// </summary>
    public int? Flags;
    /// <summary>Vertical stem thickness (§9.6.2): &lt;80 light, 80-110 normal, &gt;110 bold.</summary>
    public float? StemV;
    /// <summary>
    /// Ascent as a fraction of em (/Ascent ÷ 1000). Defaults to 0.95 with no descriptor,
    /// matching Poppler's fallback.
    /// </summary>
    public float Ascent = 0.95f;
    /// <summary>Descent as a fraction of em; always ≤ 0. Defaults to -0.35.</summary>
    public float Descent = -0.35f;
    /// <summary>Embedded font program from /FontFile, /FontFile2 or /FontFile3.</summary>
    public byte[]? EmbeddedFontData;
    /// <summary>
    /// Whether the embedded program is TrueType (FontFile2). Only TrueType programs carry a
    /// `cmap`, so this gates the lazy extraction in <see cref="GetTrueTypeCMap"/>.
    /// </summary>
    public bool IsTrueTypeFont;
    /// <summary>CID → GID mapping (Type0 fonts only).</summary>
    public OxCIDToGIDMap? CidToGidMap;
    /// <summary>CIDFont character collection (Type0 fonts only).</summary>
    public OxCIDSystemInfo? CidSystemInfo;
    /// <summary>"CIDFontType0" (CFF) or "CIDFontType2" (TrueType).</summary>
    public string? CidFontType;
    /// <summary>
    /// FontMatrix[a] — scales glyph-space widths to text space. 0.001 for standard
    /// Type1/TrueType, 1.0 for a Type3 with an identity FontMatrix.
    /// </summary>
    public float FontMatrixA = 0.001f;
    /// <summary>Simple-font widths in 1000ths of em, indexed by (code - FirstChar).</summary>
    public float[]? Widths;
    public uint? FirstChar;
    public uint? LastChar;
    /// <summary>Width for codes the /Widths array does not cover.</summary>
    public float DefaultWidth = 550.0f;
    /// <summary>Type0 per-CID widths from /W. Sparse, so a map rather than an array.</summary>
    public Dictionary<ushort, float>? CidWidths;
    /// <summary>/DW, or the spec default 1000 when absent.</summary>
    public float CidDefaultWidth = 1000.0f;
    /// <summary>
    /// Whether /DW was actually present. Distinguishes a spec-default 1000 from an authored
    /// one, which <see cref="GetGlyphWidth"/> and <see cref="HasExplicitWidths"/> both need.
    /// </summary>
    public bool HasExplicitDw;
    /// <summary>Codes whose glyph name is compound (`f_f` → "ff").</summary>
    public Dictionary<byte, string> MultiCharMap = new();
    /// <summary>byte code → CFF glyph id for embedded CFF subsets.</summary>
    public Dictionary<byte, ushort>? CffGidMap;
    /// <summary>
    /// Raw /Differences glyph names by code. Unlike the Custom encoding map (which stores the
    /// *resolved* char) this keeps the name the writer assigned (§9.6.6.1, Table 114), which
    /// the punctuation-recovery interceptions in <see cref="CharToUnicode"/> treat as
    /// authoritative.
    /// </summary>
    public Dictionary<byte, string> DiffGlyphNames = new();
    /// <summary>0 = horizontal, 1 = vertical (tategaki), resolved from the /Encoding CMap.</summary>
    public byte Wmode;
    /// <summary>Per-CID vertical metrics from /W2; null on horizontal-only fonts.</summary>
    public Dictionary<ushort, OxVerticalMetrics>? CidVerticalMetrics;
    /// <summary>/DW2 defaults, or <see cref="OxVerticalMetrics.SpecDefault"/>.</summary>
    public OxVerticalMetrics CidDefaultVerticalMetrics = OxVerticalMetrics.SpecDefault;

    // Lazily-resolved memos. Each answer is loop-invariant for the font's life, and text
    // extraction asks for them once per glyph.
    private bool _truetypeCmapInit;
    private IOxTrueTypeCMap? _truetypeCmap;
    private bool _embeddedGlyphNamesInit;
    private IReadOnlyList<string?>? _embeddedGlyphNames;
    private char[]? _byteToCharTable;
    private float[]? _byteToWidthTable;
    private OxFontWeight? _weightMemo;
    private bool? _italicMemo;
    private bool _std14MemoInit;
    private OxStd14Flags? _std14Memo;
    private readonly Dictionary<uint, string?> _unicodeMemo = new();

    /// <summary>
    /// The TrueType cmap, extracted on first access. Deferring it saves the 10-25ms
    /// per-font extraction whenever /ToUnicode resolves every character.
    /// </summary>
    public IOxTrueTypeCMap? GetTrueTypeCMap()
    {
        if (_truetypeCmapInit) return _truetypeCmap;
        _truetypeCmapInit = true;

        if (!IsTrueTypeFont) return _truetypeCmap = null;
        byte[]? fontData = EmbeddedFontData;
        if (fontData is null || fontData.Length == 0) return _truetypeCmap = null;

        var cmap = OxFontSeams.TrueType?.CMapFromFontData(fontData);
        _truetypeCmap = cmap is not null && !cmap.IsEmpty ? cmap : null;
        return _truetypeCmap;
    }

    /// <summary>Install a cmap directly (cmap sharing between fonts, and tests).</summary>
    public void SetTrueTypeCmap(IOxTrueTypeCMap? cmap)
    {
        _truetypeCmapInit = true;
        _truetypeCmap = cmap;
    }

    public bool HasTrueTypeCmap() => GetTrueTypeCMap() is not null;

    /// <summary>
    /// The most authoritative Unicode-mapping resource this font offers. A fact derived from
    /// the font's structure — which resources exist — not a decode of any character code, so
    /// it mirrors the §9.10.2 priority order across every font type.
    ///
    /// <see cref="OxMappingProvenance.Fallback"/> is the load-bearing value: the font carries
    /// no mapping resource at all, so any Unicode extracted for its glyphs is a fabricated
    /// echo rather than something read from the file.
    /// </summary>
    public OxMappingProvenance BestMappingProvenance()
    {
        if (ToUnicode is not null && ToUnicode.IsParsed && ToUnicode.Count > 0)
            return OxMappingProvenance.ToUnicode;

        if (Subtype == "Type0" && CidSystemInfo is not null
            && CidSystemInfo.Ordering != "Identity" && CidSystemInfo.Ordering.Length > 0)
            return OxMappingProvenance.PredefinedCMap;

        if (HasTrueTypeCmap()) return OxMappingProvenance.EmbeddedCmap;

        // A simple font always resolves through /Encoding → glyph name → AGL, and a symbolic
        // Symbol/ZapfDingbats through its built-in encoding.
        if (Subtype != "Type0") return OxMappingProvenance.EncodingName;

        return OxMappingProvenance.Fallback;
    }

    /// <summary>
    /// The embedded program's own `post`/charset glyph name for <paramref name="gid"/>.
    /// Used by §9.10.2 Priority 3c: PowerPoint/Acrobat Identity-H subsets routinely strip the
    /// Unicode cmap but keep post Format 2 names, and bullets and fi/fl ligatures recover
    /// only through this path.
    /// </summary>
    public string? EmbeddedGlyphName(ushort gid)
    {
        if (!_embeddedGlyphNamesInit)
        {
            _embeddedGlyphNamesInit = true;
            byte[]? fontData = EmbeddedFontData;
            _embeddedGlyphNames = fontData is null || fontData.Length == 0
                ? null
                : OxFontSeams.TrueType?.GlyphNames(fontData);
        }
        var names = _embeddedGlyphNames;
        if (names is null || gid >= names.Count) return null;
        return names[gid];
    }

    /// <summary>
    /// Authoritative glyph name for a simple-font character code (§9.6.6.1 / §9.10.2):
    /// the /Differences name, else the embedded program's name for the code's GID.
    /// </summary>
    private string? GlyphNameForCode(uint charCode)
    {
        if (DiffGlyphNames.TryGetValue((byte)charCode, out string? name)) return name;

        // For embedded CFF subsets the byte→GID map is authoritative; otherwise the code is
        // the GID, per the TrueType simple-font convention.
        ushort gid = CffGidMap is not null && CffGidMap.TryGetValue((byte)charCode, out ushort g)
            ? g
            : (ushort)charCode;
        return EmbeddedGlyphName(gid);
    }

    /// <summary>
    /// Map a Glyph ID to a standard PostScript glyph name, for Type0 fonts with no ToUnicode
    /// CMap. Covers the ASCII printable range and the Windows-1252 / Latin-1 supplement.
    /// </summary>
    public static string? GidToStandardGlyphName(ushort gid)
    {
        return gid switch
        {
            // Control characters and whitespace (32-33)
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
            // Digits (48-57)
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
            // Punctuation (58-64)
            0x3A => "colon",
            0x3B => "semicolon",
            0x3C => "less",
            0x3D => "equal",
            0x3E => "greater",
            0x3F => "question",
            0x40 => "at",
            // Uppercase letters (65-90)
            0x41 => "A",
            0x42 => "B",
            0x43 => "C",
            0x44 => "D",
            0x45 => "E",
            0x46 => "F",
            0x47 => "G",
            0x48 => "H",
            0x49 => "I",
            0x4A => "J",
            0x4B => "K",
            0x4C => "L",
            0x4D => "M",
            0x4E => "N",
            0x4F => "O",
            0x50 => "P",
            0x51 => "Q",
            0x52 => "R",
            0x53 => "S",
            0x54 => "T",
            0x55 => "U",
            0x56 => "V",
            0x57 => "W",
            0x58 => "X",
            0x59 => "Y",
            0x5A => "Z",
            // Brackets (91-96)
            0x5B => "bracketleft",
            0x5C => "backslash",
            0x5D => "bracketright",
            0x5E => "asciicircum",
            0x5F => "underscore",
            0x60 => "quoteleft",
            // Lowercase letters (97-122)
            0x61 => "a",
            0x62 => "b",
            0x63 => "c",
            0x64 => "d",
            0x65 => "e",
            0x66 => "f",
            0x67 => "g",
            0x68 => "h",
            0x69 => "i",
            0x6A => "j",
            0x6B => "k",
            0x6C => "l",
            0x6D => "m",
            0x6E => "n",
            0x6F => "o",
            0x70 => "p",
            0x71 => "q",
            0x72 => "r",
            0x73 => "s",
            0x74 => "t",
            0x75 => "u",
            0x76 => "v",
            0x77 => "w",
            0x78 => "x",
            0x79 => "y",
            0x7A => "z",
            // Braces (123-126)
            0x7B => "braceleft",
            0x7C => "bar",
            0x7D => "braceright",
            0x7E => "asciitilde",

            // ==================================================================================
            // Extended Latin / Windows-1252 range (0x80-0xFF)
            // ==================================================================================
            // These mappings cover the extended ASCII characters commonly found in Western
            // European PDFs. When a Type0 font with Identity CMap encounters these GIDs,
            // we map them to their standard PostScript glyph names for AGL lookup.
            //
            // Per PDF Spec ISO 32000-1:2008 Section 9.10.2, when ToUnicode CMap is unavailable,
            // readers may use glyph name lookup as a fallback mechanism.

            // 0x80-0x8F: Windows-1252 extended control characters and symbols
            0x80 => "euro",  // U+20AC (Euro sign)
            // 0x81: undefined in Windows-1252
            0x82 => "quotesinglbase",  // U+201A (Single low quotation mark)
            0x83 => "florin",  // U+0192 (Latin small letter f with hook)
            0x84 => "quotedblbase",  // U+201E (Double low quotation mark)
            0x85 => "ellipsis",  // U+2026 (Horizontal ellipsis)
            0x86 => "dagger",  // U+2020 (Dagger)
            0x87 => "daggerdbl",  // U+2021 (Double dagger)
            0x88 => "circumflex",  // U+02C6 (Modifier letter circumflex accent)
            0x89 => "perthousand",  // U+2030 (Per mille sign)
            0x8A => "Scaron",  // U+0160 (Latin capital letter S with caron)
            0x8B => "guilsinglleft",  // U+2039 (Single left-pointing angle quotation mark)
            0x8C => "OE",  // U+0152 (Latin capital ligature OE)
            // 0x8D: undefined in Windows-1252
            0x8E => "Zcaron",  // U+017D (Latin capital letter Z with caron)
            // 0x8F: undefined in Windows-1252

            // 0x90-0x9F: Windows-1252 smart quotes, dashes, and accents
            // 0x90: undefined in Windows-1252
            0x91 => "quoteleft",  // U+2018 (Left single quotation mark)
            0x92 => "quoteright",  // U+2019 (Right single quotation mark)
            0x93 => "quotedblleft",  // U+201C (Left double quotation mark)
            0x94 => "quotedblright",  // U+201D (Right double quotation mark)
            0x95 => "bullet",  // U+2022 (Bullet)
            0x96 => "endash",  // U+2013 (En dash)
            0x97 => "emdash",  // U+2014 (Em dash)
            0x98 => "tilde",  // U+02DC (Small tilde)
            0x99 => "trademark",  // U+2122 (Trade mark sign)
            0x9A => "scaron",  // U+0161 (Latin small letter s with caron)
            0x9B => "guilsinglright",  // U+203A (Single right-pointing angle quotation mark)
            0x9C => "oe",  // U+0153 (Latin small ligature oe)
            // 0x9D: undefined in Windows-1252
            0x9E => "zcaron",  // U+017E (Latin small letter z with caron)
            0x9F => "Ydieresis",  // U+0178 (Latin capital letter Y with diaeresis)

            // 0xA0-0xFF: Latin-1 Supplement (ISO 8859-1)
            // Most of these are direct character mappings (À-ÿ)
            // Implement programmatically for common characters and fallback to glyph name generation
            0xA0 => "space",  // U+00A0 (No-break space)
            0xA1 => "exclamdown",  // U+00A1 (Inverted exclamation mark)
            0xA2 => "cent",  // U+00A2 (Cent sign)
            0xA3 => "sterling",  // U+00A3 (Pound sign)
            0xA4 => "currency",  // U+00A4 (Currency sign)
            0xA5 => "yen",  // U+00A5 (Yen sign)
            0xA6 => "brokenbar",  // U+00A6 (Broken bar)
            0xA7 => "section",  // U+00A7 (Section sign)
            0xA8 => "dieresis",  // U+00A8 (Diaeresis)
            0xA9 => "copyright",  // U+00A9 (Copyright sign)
            0xAA => "ordfeminine",  // U+00AA (Feminine ordinal indicator)
            0xAB => "guillemotleft",  // U+00AB (Left-pointing double angle quotation mark)
            0xAC => "logicalnot",  // U+00AC (Not sign)
            0xAD => "uni00AD",  // U+00AD (Soft hyphen)
            0xAE => "registered",  // U+00AE (Registered sign)
            0xAF => "macron",  // U+00AF (Macron)
            0xB0 => "degree",  // U+00B0 (Degree sign)
            0xB1 => "plusminus",  // U+00B1 (Plus-minus sign)
            0xB2 => "twosuperior",  // U+00B2 (Superscript two)
            0xB3 => "threesuperior",  // U+00B3 (Superscript three)
            0xB4 => "acute",  // U+00B4 (Acute accent)
            0xB5 => "mu",  // U+00B5 (Micro sign)
            0xB6 => "paragraph",  // U+00B6 (Pilcrow)
            0xB7 => "middot",  // U+00B7 (Middle dot)
            0xB8 => "cedilla",  // U+00B8 (Cedilla)
            0xB9 => "onesuperior",  // U+00B9 (Superscript one)
            0xBA => "ordmasculine",  // U+00BA (Masculine ordinal indicator)
            0xBB => "guillemotright",  // U+00BB (Right-pointing double angle quotation mark)
            0xBC => "onequarter",  // U+00BC (Vulgar fraction one quarter)
            0xBD => "onehalf",  // U+00BD (Vulgar fraction one half)
            0xBE => "threequarters",  // U+00BE (Vulgar fraction three quarters)
            0xBF => "questiondown",  // U+00BF (Inverted question mark)

            // 0xC0-0xFE: Latin-1 Supplement letters (À-þ)
            // These map directly to their Unicode equivalents via standard PostScript names
            // Format: glyph name is the Unicode character itself (e.g., "Agrave" for U+00C0)
            0xC0 => "Agrave",  // U+00C0 (Latin capital letter A with grave)
            0xC1 => "Aacute",  // U+00C1 (Latin capital letter A with acute)
            0xC2 => "Acircumflex",  // U+00C2 (Latin capital letter A with circumflex)
            0xC3 => "Atilde",  // U+00C3 (Latin capital letter A with tilde)
            0xC4 => "Adieresis",  // U+00C4 (Latin capital letter A with diaeresis)
            0xC5 => "Aring",  // U+00C5 (Latin capital letter A with ring above)
            0xC6 => "AE",  // U+00C6 (Latin capital letter AE)
            0xC7 => "Ccedilla",  // U+00C7 (Latin capital letter C with cedilla)
            0xC8 => "Egrave",  // U+00C8 (Latin capital letter E with grave)
            0xC9 => "Eacute",  // U+00C9 (Latin capital letter E with acute)
            0xCA => "Ecircumflex",  // U+00CA (Latin capital letter E with circumflex)
            0xCB => "Edieresis",  // U+00CB (Latin capital letter E with diaeresis)
            0xCC => "Igrave",  // U+00CC (Latin capital letter I with grave)
            0xCD => "Iacute",  // U+00CD (Latin capital letter I with acute)
            0xCE => "Icircumflex",  // U+00CE (Latin capital letter I with circumflex)
            0xCF => "Idieresis",  // U+00CF (Latin capital letter I with diaeresis)
            0xD0 => "Eth",  // U+00D0 (Latin capital letter Eth)
            0xD1 => "Ntilde",  // U+00D1 (Latin capital letter N with tilde)
            0xD2 => "Ograve",  // U+00D2 (Latin capital letter O with grave)
            0xD3 => "Oacute",  // U+00D3 (Latin capital letter O with acute)
            0xD4 => "Ocircumflex",  // U+00D4 (Latin capital letter O with circumflex)
            0xD5 => "Otilde",  // U+00D5 (Latin capital letter O with tilde)
            0xD6 => "Odieresis",  // U+00D6 (Latin capital letter O with diaeresis)
            0xD7 => "multiply",  // U+00D7 (Multiplication sign)
            0xD8 => "Oslash",  // U+00D8 (Latin capital letter O with stroke)
            0xD9 => "Ugrave",  // U+00D9 (Latin capital letter U with grave)
            0xDA => "Uacute",  // U+00DA (Latin capital letter U with acute)
            0xDB => "Ucircumflex",  // U+00DB (Latin capital letter U with circumflex)
            0xDC => "Udieresis",  // U+00DC (Latin capital letter U with diaeresis)
            0xDD => "Yacute",  // U+00DD (Latin capital letter Y with acute)
            0xDE => "Thorn",  // U+00DE (Latin capital letter Thorn)
            0xDF => "germandbls",  // U+00DF (Latin small letter sharp s)
            0xE0 => "agrave",  // U+00E0 (Latin small letter a with grave)
            0xE1 => "aacute",  // U+00E1 (Latin small letter a with acute)
            0xE2 => "acircumflex",  // U+00E2 (Latin small letter a with circumflex)
            0xE3 => "atilde",  // U+00E3 (Latin small letter a with tilde)
            0xE4 => "adieresis",  // U+00E4 (Latin small letter a with diaeresis)
            0xE5 => "aring",  // U+00E5 (Latin small letter a with ring above)
            0xE6 => "ae",  // U+00E6 (Latin small letter ae)
            0xE7 => "ccedilla",  // U+00E7 (Latin small letter c with cedilla)
            0xE8 => "egrave",  // U+00E8 (Latin small letter e with grave)
            0xE9 => "eacute",  // U+00E9 (Latin small letter e with acute)
            0xEA => "ecircumflex",  // U+00EA (Latin small letter e with circumflex)
            0xEB => "edieresis",  // U+00EB (Latin small letter e with diaeresis)
            0xEC => "igrave",  // U+00EC (Latin small letter i with grave)
            0xED => "iacute",  // U+00ED (Latin small letter i with acute)
            0xEE => "icircumflex",  // U+00EE (Latin small letter i with circumflex)
            0xEF => "idieresis",  // U+00EF (Latin small letter i with diaeresis)
            0xF0 => "eth",  // U+00F0 (Latin small letter eth)
            0xF1 => "ntilde",  // U+00F1 (Latin small letter n with tilde)
            0xF2 => "ograve",  // U+00F2 (Latin small letter o with grave)
            0xF3 => "oacute",  // U+00F3 (Latin small letter o with acute)
            0xF4 => "ocircumflex",  // U+00F4 (Latin small letter o with circumflex)
            0xF5 => "otilde",  // U+00F5 (Latin small letter o with tilde)
            0xF6 => "odieresis",  // U+00F6 (Latin small letter o with diaeresis)
            0xF7 => "divide",  // U+00F7 (Division sign)
            0xF8 => "oslash",  // U+00F8 (Latin small letter o with stroke)
            0xF9 => "ugrave",  // U+00F9 (Latin small letter u with grave)
            0xFA => "uacute",  // U+00FA (Latin small letter u with acute)
            0xFB => "ucircumflex",  // U+00FB (Latin small letter u with circumflex)
            0xFC => "udieresis",  // U+00FC (Latin small letter u with diaeresis)
            0xFD => "yacute",  // U+00FD (Latin small letter y with acute)
            0xFE => "thorn",  // U+00FE (Latin small letter thorn)
            0xFF => "ydieresis",  // U+00FF (Latin small letter y with diaeresis)

            // All other GIDs not in the supported ranges
            _ => null,
        };
    }

    /// <summary>
    /// Pre-computed byte→char lookup for simple fonts. '\0' means "run the full
    /// <see cref="CharToUnicode"/> cascade": multi-char results, U+FFFD and control
    /// characters other than tab/LF/CR are all left as '\0'.
    /// </summary>
    public char[] GetByteToCharTable()
    {
        if (_byteToCharTable is not null) return _byteToCharTable;

        var tbl = new char[256];
        for (int i = 0; i <= 255; i++)
        {
            string? s = CharToUnicode((uint)i);
            if (s is null || s.Length == 0) continue;
            char c = s[0];
            if (s.Length == 1 && c != '�' && (c >= '\x20' || c == '\t' || c == '\n' || c == '\r'))
                tbl[i] = c;
        }
        _byteToCharTable = tbl;
        return tbl;
    }

    /// <summary>
    /// Character code → Unicode per §9.10.2. Ligatures (U+FB00–FB06) are preserved here;
    /// splitting them is a context decision the text pipeline makes, keeping this a pure
    /// encoding layer.
    /// </summary>
    public string? CharToUnicode(uint charCode)
    {
        if (_unicodeMemo.TryGetValue(charCode, out string? cached)) return cached;

        string? decoded = CharToUnicodeUncached(charCode);
        string? result = decoded is null ? null : OxFontTables.NormalizeCjkRadicalForms(decoded);
        _unicodeMemo[charCode] = result;
        return result;
    }

    private string? CharToUnicodeUncached(uint charCode)
    {
        // ---- PRIORITY 1: ToUnicode CMap (§9.10.2, Method 1) --------------------------
        // A present CMap is authoritative. For a composite font a miss means the code
        // genuinely has no Unicode equivalent, so falling through to the predefined-CMap
        // path would invent plausible-looking but wrong CJK; only Identity-encoded fonts
        // keep a valid fallback (CID == code point) and are allowed through.
        if (ToUnicode is not null)
        {
            if (ToUnicode.IsParsed)
            {
                string? rawUnicode = ToUnicode.Lookup(charCode);

                // For Identity-encoded fonts U+FFFD and the BMP noncharacters U+FFFE/U+FFFF
                // are "no glyph" placeholders some producers stuff into ToUnicode, not real
                // mappings — the CID→GID→cmap fallback below recovers the real character.
                // Noncharacters are permanently reserved, so this can only improve output.
                string? effectiveHit = rawUnicode;
                if (rawUnicode is not null && Encoding.IsIdentity && rawUnicode.Length > 0)
                {
                    bool isPlaceholder = true;
                    foreach (char c in rawUnicode)
                    {
                        if (c != '�' && c != '￾' && c != '￿') { isPlaceholder = false; break; }
                    }
                    if (isPlaceholder) effectiveHit = null;
                }

                if (effectiveHit is not null)
                {
                    // Bare C0 controls (except tab/LF/CR, legitimate whitespace) are never
                    // valid visible text and indicate a broken ToUnicode entry. The CMap
                    // explicitly mapped this code, so do not fall through even for simple fonts.
                    bool isC0Control = effectiveHit.Length > 0;
                    foreach (char c in effectiveHit)
                    {
                        if (!(c <= 0x08 || (c >= 0x0B && c <= 0x0C) || (c >= 0x0E && c <= 0x1F)))
                        { isC0Control = false; break; }
                    }

                    if (effectiveHit == "�") return "�";
                    if (isC0Control) return "�";

                    // Interception A: when a present ToUnicode resolves a code to a
                    // non-sensible symbol (e.g. U+00AC) but the font's authoritative glyph
                    // name for that code is punctuation, prefer the §9.10.2(a)+(b) AGL
                    // result. Gated so a correctly-mapped period never enters here.
                    if (OxFontTables.IsNonSensibleSymbol(effectiveHit))
                    {
                        string? glyphName = GlyphNameForCode(charCode);
                        if (glyphName is not null)
                        {
                            string? punct = OxFontTables.PunctuationUnicodeForGlyphName(glyphName);
                            if (punct is not null) return punct;
                        }
                    }
                    return effectiveHit;
                }

                if (Subtype == "Type0" && !Encoding.IsIdentity) return "�";
            }
        }

        // ---- PRIORITY 2: predefined CMaps (§9.7.5.2) ---------------------------------
        // Identity-H/V maps a 2-byte CID straight to a code point (CID == Unicode). It is
        // resolved here, before the other fallbacks, for Type0 fonts.
        if (Subtype == "Type0" && Encoding.IsStandard)
        {
            string encodingName = Encoding.Name!;
            if (encodingName == "Identity-H" || encodingName == "Identity-V"
                || encodingName.Contains("UCS2", StringComparison.Ordinal)
                || encodingName.Contains("UTF16", StringComparison.Ordinal))
            {
                // §9.10.2 requires a Type0 font to carry either a ToUnicode CMap or a
                // predefined CMap (which needs CIDSystemInfo). Without CIDSystemInfo,
                // Identity-H/V is not by itself evidence that the CID is a code point.
                if (CidSystemInfo is not null)
                {
                    bool isIdentityOrdering = CidSystemInfo.Ordering == "Identity";
                    if (isIdentityOrdering)
                    {
                        // Adobe-Identity CIDs are glyph indices, not code points.
                        var ttCmap = GetTrueTypeCMap();
                        if (ttCmap is not null)
                        {
                            ushort gid = CidToGidMap is not null
                                ? CidToGidMap.GetGid((ushort)charCode)
                                : (ushort)charCode;
                            char? unicodeChar = ttCmap.GetUnicode(gid);
                            if (unicodeChar is not null) return unicodeChar.Value.ToString();
                        }
                    }

                    bool isUcs2OrUtf16 = encodingName.Contains("UCS2", StringComparison.Ordinal)
                        || encodingName.Contains("UTF16", StringComparison.Ordinal);
                    bool isNonIdentityOrdering = CidSystemInfo.Ordering != "Identity";

                    if (!isUcs2OrUtf16 && isNonIdentityOrdering)
                    {
                        // Identity-H/V over a CJK collection: the codes are CIDs, not Unicode.
                        uint? codePoint = OxFontTables.LookupPredefinedCMap(encodingName, CidSystemInfo, (ushort)charCode);
                        if (codePoint is not null)
                        {
                            string? s = OxFontTables.CharFromU32(codePoint.Value);
                            if (s is not null) return s;
                        }
                        // CID lookup failed — fall through to Priority 2b and beyond.
                    }
                    else
                    {
                        string? s = OxFontTables.CharFromU32(charCode);
                        if (s is not null && (!OxFontTables.IsControlCodePoint(charCode) || charCode == ' '))
                            return s;
                    }
                }
                else
                {
                    // Many producers assign CID == code point even without CIDSystemInfo;
                    // MuPDF uses the same last-resort fallback.
                    string? s = OxFontTables.CharFromU32(charCode);
                    if (s is not null && (!OxFontTables.IsControlCodePoint(charCode) || charCode == ' '))
                        return s;
                }
            }
        }

        // ---- PRIORITY 2a: Shift-JIS (RKSJ) direct decoding ---------------------------
        if (Subtype == "Type0" && Encoding.IsStandard
            && Encoding.Name!.Contains("RKSJ", StringComparison.Ordinal))
        {
            char? sjis = OxFontTables.ShiftJisToUnicode((ushort)charCode);
            if (sjis is not null) return sjis.Value.ToString();
        }

        // ---- PRIORITY 2b: Unicode-based predefined CMaps ------------------------------
        // Byte-encoding CMaps (GBpc-EUC-H, B5pc-H, EUC-H, KSC-EUC-H, …) carry raw legacy
        // multi-byte codes; decoding them directly yields the same Unicode the charcode→CID
        // →Unicode route would, and the spec's fallback clause permits it. That decode is
        // a no-op for Identity/UCS2 CMaps, so trying it first is safe.
        if (Subtype == "Type0")
        {
            string encName = Encoding.EncodingKind switch
            {
                OxEncoding.Kind.Standard => Encoding.Name!,
                OxEncoding.Kind.Identity => "Identity-H",
                _ => "",
            };

            string? cjk = OxFontTables.DecodeCjkRawCharCode(charCode, encName, CidSystemInfo);
            if (cjk is not null) return cjk;

            uint? codePoint = OxFontTables.LookupPredefinedCMap(encName, CidSystemInfo, (ushort)charCode);
            if (codePoint is not null)
            {
                string? s = OxFontTables.CharFromU32(codePoint.Value);
                if (s is not null) return s;
            }
        }

        // ---- PRIORITY 2 (simple fonts): built-in symbolic encodings -------------------
        // §9.6.6.1: for symbolic fonts the /Encoding entry is ignored and codes map through
        // the font's own built-in encoding — Symbol (Annex D.4), ZapfDingbats (D.5).
        if (IsSymbolic())
        {
            string fontNameLower = BaseFont.ToLowerInvariant();
            if (fontNameLower.Contains("symbol", StringComparison.Ordinal))
            {
                char? c = OxFontTables.SymbolEncodingLookup((byte)charCode);
                if (c is not null) return c.Value.ToString();
            }
            else if (fontNameLower.Contains("zapf", StringComparison.Ordinal)
                || fontNameLower.Contains("dingbat", StringComparison.Ordinal))
            {
                char? c = OxFontTables.ZapfDingbatsEncodingLookup((byte)charCode);
                if (c is not null) return c.Value.ToString();
            }
            // Other symbolic fonts fall through to /Encoding: the spec says to ignore it,
            // but some PDFs only work when it is honoured.
        }

        // ---- PRIORITY 3: the font's /Encoding entry (§9.10.2, Method 3) ---------------
        switch (Encoding.EncodingKind)
        {
            case OxEncoding.Kind.Standard:
            {
                string name = Encoding.Name!;
                if (name == "Identity-H" || name == "Identity-V")
                {
                    if (Subtype == "Type0")
                    {
                        // Priority 2 did not map this CID; fall back to CID-as-Unicode.
                        string? s = OxFontTables.CharFromU32(charCode);
                        if (s is not null && (!OxFontTables.IsControlCodePoint(charCode) || charCode == ' '))
                            return s;
                        return "�";
                    }
                    string? simple = OxFontTables.CharFromU32(charCode);
                    if (simple is not null) return simple;
                }

                // TrueType subset fonts with no /Encoding often use GIDs as codes. §9.6.5.4:
                // with no /Encoding and a (3,1) cmap, codes map through the cmap.
                if ((Subtype == "TrueType" || Subtype == "Type1") && name == "StandardEncoding")
                {
                    var ttCmap = GetTrueTypeCMap();
                    if (ttCmap is not null)
                    {
                        ushort gid = ttCmap.CodeToGid((ushort)charCode) ?? (ushort)charCode;
                        char? unicodeChar = ttCmap.GetUnicode(gid);
                        if (unicodeChar is not null) return unicodeChar.Value.ToString();
                    }
                }

                string? unicode = OxFontTables.StandardEncodingLookup(name, (byte)charCode);
                if (unicode is not null) return unicode;
                break;
            }

            case OxEncoding.Kind.Custom:
            {
                var map = Encoding.Map!;
                if (map.TryGetValue((byte)charCode, out char customChar))
                {
                    // Interception B: the /Differences glyph name is authoritative
                    // (§9.6.6.1), so a `/period`-named code wins as `.` no matter what the
                    // base or program encoding resolved it to.
                    if (OxFontTables.IsNonSensibleSymbol(customChar.ToString())
                        && DiffGlyphNames.TryGetValue((byte)charCode, out string? glyphName))
                    {
                        string? punct = OxFontTables.PunctuationUnicodeForGlyphName(glyphName);
                        if (punct is not null) return punct;
                    }

                    if (OxFontTables.IsLigatureChar(customChar))
                    {
                        string? expanded = OxFontTables.ExpandLigatureChar(customChar);
                        if (expanded is not null) return expanded;
                    }

                    return customChar.ToString();
                }
                if (MultiCharMap.TryGetValue((byte)charCode, out string? multi)) return multi;
                break;
            }

            case OxEncoding.Kind.Identity:
            {
                // Identity assumes code == Unicode, which holds for simple fonts only:
                // a Type0 font's codes are CIDs (§9.7.6.3).
                if (Subtype == "Type0")
                {
                    var ttCmap = GetTrueTypeCMap();
                    if (ttCmap is not null)
                    {
                        // GIDs are u16, so a CID above 0xFFFF cannot go through CIDToGIDMap.
                        if (charCode > 0xFFFF) return null;

                        ushort gid = CidToGidMap is not null
                            ? CidToGidMap.GetGid((ushort)charCode)
                            : (ushort)charCode;

                        char? unicodeChar = ttCmap.GetUnicode(gid);
                        if (unicodeChar is not null) return unicodeChar.Value.ToString();

                        // Priority 3c: the embedded program's own glyph-name table, which is
                        // authoritative where the hardcoded ASCII GID→name map below is a guess.
                        string? glyphName = EmbeddedGlyphName(gid);
                        if (glyphName is not null)
                        {
                            string? mapped = OxFontSeams.GlyphNames?.MapGlyphNameToUnicodeString(glyphName);
                            if (mapped is not null) return mapped;
                        }
                    }

                    // A present-but-empty /ToUnicode maps nothing, so it counts as absent —
                    // otherwise an Identity-ordered font with an empty CMap drops all its text.
                    bool hasUsableToUnicode = ToUnicode is not null && ToUnicode.IsParsed && ToUnicode.Count > 0;
                    bool isIdentityOrdered = CidSystemInfo is not null && CidSystemInfo.Ordering == "Identity";

                    // The GID→AGL fallback is a numeric guess: it reads the GID as a code point
                    // through the standard glyph-name table. That is meaningless for
                    // Identity-ordered subsets, whose GIDs are arbitrary — the guess would land
                    // on unrelated punctuation and shadow the CID-as-Unicode mapping below — and
                    // with a usable /ToUnicode present an unmapped code is genuinely unmapped,
                    // so U+FFFD is the honest answer.
                    if (!hasUsableToUnicode && !isIdentityOrdered && CidToGidMap is not null && charCode <= 0xFFFF)
                    {
                        ushort gid = CidToGidMap.GetGid((ushort)charCode);
                        string? glyphName = GidToStandardGlyphName(gid);
                        if (glyphName is not null)
                        {
                            char? unicodeChar = OxFontSeams.GlyphNames?.AdobeGlyphListLookup(glyphName);
                            if (unicodeChar is not null) return unicodeChar.Value.ToString();
                        }
                    }

                    // CID-as-Unicode: many producers assign CID == code point. Also used for
                    // uncovered whitespace on Identity-ordered fonts (CID 0x20 is reliably a
                    // space and producers routinely omit it; dropping it wrecks word boundaries).
                    bool identityWhitespace = isIdentityOrdered && charCode == 0x20;
                    if (!hasUsableToUnicode || identityWhitespace)
                    {
                        string? s = OxFontTables.CharFromU32(charCode);
                        if (s is not null && (!OxFontTables.IsControlCodePoint(charCode) || charCode == ' '))
                            return s;
                    }
                    return "�";
                }

                string? simpleIdentity = OxFontTables.CharFromU32(charCode);
                if (simpleIdentity is not null) return simpleIdentity;
                break;
            }
        }

        // ---- PRIORITY 4: TrueType cmap fallback for simple fonts ----------------------
        if (Subtype != "Type0")
        {
            var ttCmap = GetTrueTypeCMap();
            if (ttCmap is not null)
            {
                // Symbolic TrueType fonts index glyphs by content byte through a (3,0)/(1,0)
                // symbol cmap, so the byte is not the GID; resolve byte→GID first.
                ushort gid = ttCmap.CodeToGid((ushort)charCode) ?? (ushort)charCode;
                char? unicodeChar = ttCmap.GetUnicode(gid);
                if (unicodeChar is not null) return unicodeChar.Value.ToString();
            }
        }

        // ---- PRIORITY 6: Unicode ligature fallback ------------------------------------
        // With no font data identifying the glyph, a raw code in the ligature block is
        // safest read as its components — LaTeX and scientific producers emit these directly.
        string? ligature = charCode switch
        {
            0xFB00 => "ff",
            0xFB01 => "fi",
            0xFB02 => "fl",
            0xFB03 => "ffi",
            0xFB04 => "ffl",
            0xFB05 or 0xFB06 => "st",
            _ => null,
        };
        return ligature;
    }

    /// <summary>Heuristic italic check on the font name.</summary>
    public bool IsItalic()
    {
        if (_italicMemo is not null) return _italicMemo.Value;
        string nameLower = BaseFont.ToLowerInvariant();
        _italicMemo = nameLower.Contains("italic", StringComparison.Ordinal)
            || nameLower.Contains("oblique", StringComparison.Ordinal);
        return _italicMemo.Value;
    }

    /// <summary>
    /// Symbolic per FontDescriptor /Flags bit 3 (Table 123) — glyphs outside the Adobe
    /// standard Latin set, whose codes map through the font's built-in encoding.
    /// </summary>
    public bool IsSymbolic()
    {
        if (Flags is not null)
        {
            const int SymbolicBit = 1 << 2; // Bit 3
            return (Flags.Value & SymbolicBit) != 0;
        }
        string nameLower = BaseFont.ToLowerInvariant();
        return nameLower.Contains("symbol", StringComparison.Ordinal)
            || nameLower.Contains("zapf", StringComparison.Ordinal)
            || nameLower.Contains("dingbat", StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalize a raw code through the font's encoding, so word-boundary detection runs on
    /// real characters rather than byte codes.
    /// </summary>
    public char? GetEncodedChar(byte code)
    {
        switch (Encoding.EncodingKind)
        {
            case OxEncoding.Kind.Custom:
                return Encoding.Map!.TryGetValue(code, out char c) ? c : null;
            default:
                // Standard and Identity both pass ASCII through; anything above is left to
                // the ToUnicode CMap.
                return code < 128 ? (char)code : null;
        }
    }

    public bool HasCustomEncoding() => Encoding.IsCustom;
}
