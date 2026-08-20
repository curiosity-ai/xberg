// Ported from pdf_oxide 0.3.77 `src/fonts/truetype_cmap.rs`
// (`TrueTypeCMap::from_font_data`, `parse_sfnt_header`, `find_cmap_table`,
//  `parse_cmap_subtable` and formats 0 / 4 / 6 / 12, `build_symbol_code_to_gid`,
//  `code_to_gid`, `get_unicode`, plus the MAC_ROMAN_HIGH table).
//
// This is the fallback that gives a Type0 / CIDFontType2 font its text back when the
// PDF ships no /ToUnicode: the embedded font's own cmap is inverted into GID → Unicode.
//
// Rust's `build_symbol_code_to_gid` borrows `ttf-parser` for the (3,0)/(1,0) walk;
// here the same subtable readers serve both directions.

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>A TrueType `cmap` table lifted out of an embedded font program.</summary>
internal sealed class OxTrueTypeCMap
{
    private const uint CmapTag = 0x636D6170;

    private const uint SfntTrueType = 0x00010000;
    private const uint SfntOpenType = 0x4F54544F; // "OTTO"
    private const uint SfntAppleTrue = 0x74727565; // "true"

    /// <summary>A corrupt or hostile subtable can declare segments or groups spanning
    /// the whole code space, which would leave the walk spinning for minutes over
    /// mappings no real font has. Real cmaps stay well under 100k entries, so the walk
    /// stops here and keeps whatever was decoded up to that point.</summary>
    private const int MaxCmapIterations = 1 << 21;

    /// <summary>Mac Roman (platform 1, encoding 0) high-half → Unicode.
    /// Bytes 0x00..0x7F are identical to ASCII and are handled inline; this covers
    /// 0x80..0xFF per Apple's Mac Roman → Unicode reference.</summary>
    private static readonly char[] MacRomanHigh =
    {
        'Ä', 'Å', 'Ç', 'É', 'Ñ', 'Ö', 'Ü', 'á',
        'à', 'â', 'ä', 'ã', 'å', 'ç', 'é', 'è',
        'ê', 'ë', 'í', 'ì', 'î', 'ï', 'ñ', 'ó',
        'ò', 'ô', 'ö', 'õ', 'ú', 'ù', 'û', 'ü',
        '†', '°', '¢', '£', '§', '•', '¶', 'ß',
        '®', '©', '™', '´', '¨', '≠', 'Æ', 'Ø',
        '∞', '±', '≤', '≥', '¥', 'µ', '∂', '∑',
        '∏', 'π', '∫', 'ª', 'º', 'Ω', 'æ', 'ø',
        '¿', '¡', '¬', '√', 'ƒ', '≈', '∆', '«',
        '»', '…', '\u00A0', 'À', 'Ã', 'Õ', 'Œ', 'œ',
        '–', '—', '“', '”', '‘', '’', '÷', '◊',
        'ÿ', 'Ÿ', '⁄', '€', '‹', '›', 'ﬁ', 'ﬂ',
        '‡', '·', '‚', '„', '‰', 'Â', 'Ê', 'Á',
        'Ë', 'È', 'Í', 'Î', 'Ï', 'Ì', 'Ó', 'Ô',
        '\uF8FF', 'Ò', 'Ú', 'Û', 'Ù', 'ı', 'ˆ', '˜',
        '¯', '˘', '˙', '˚', '¸', '˝', '˛', 'ˇ',
    };

    private readonly Dictionary<ushort, int> _gidToUnicode;
    private readonly Dictionary<int, ushort> _unicodeToGid;
    private readonly Dictionary<uint, ushort> _symbolCodeToGid;

    private OxTrueTypeCMap(
        Dictionary<ushort, int> gidToUnicode,
        Dictionary<int, ushort> unicodeToGid,
        Dictionary<uint, ushort> symbolCodeToGid)
    {
        _gidToUnicode = gidToUnicode;
        _unicodeToGid = unicodeToGid;
        _symbolCodeToGid = symbolCodeToGid;
    }

    /// <summary>Locate the `cmap` table, pick the best Unicode subtable and invert it.
    /// Returns null where Rust returns `Err(String)`; the caller then behaves as if the
    /// font had no cmap at all.
    ///
    /// Subtable priority: (3,10) Windows full repertoire, then (3,1) Windows BMP,
    /// then (0,3) Unicode 2.0. Ties go to the first record in directory order.</summary>
    public static OxTrueTypeCMap? FromFontData(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;

        var cursor = new OxBeReader(data);
        if (!ParseSfntHeader(cursor, out ushort numTables)) return null;
        if (!FindCmapTable(cursor, numTables, out uint cmapOffset)) return null;

        cursor.Position = cmapOffset;
        if (!cursor.TryU16(out ushort cmapVersion) || cmapVersion != 0) return null;
        if (!cursor.TryU16(out ushort numSubtables)) return null;

        uint subtableOffset = 0;
        int bestPriority = -1;
        bool found = false;

        for (int i = 0; i < numSubtables; i++)
        {
            if (!cursor.TryU16(out ushort platformId)) return null;
            if (!cursor.TryU16(out ushort encodingId)) return null;
            if (!cursor.TryU32(out uint offset)) return null;

            int priority = (platformId, encodingId) switch
            {
                (3, 10) => 30, // Windows, Unicode full repertoire
                (3, 1) => 20,  // Windows, Unicode BMP
                (0, 3) => 10,  // Unicode platform, Unicode 2.0
                _ => 0,
            };

            if (priority > bestPriority)
            {
                bestPriority = priority;
                subtableOffset = offset;
                found = true;
            }
        }

        if (!found) return null; // "No suitable cmap subtable found"

        cursor.Position = (long)cmapOffset + subtableOffset;
        if (!cursor.TryU16(out ushort format)) return null;

        var gidToUnicode = new Dictionary<ushort, int>();
        var unicodeToGid = new Dictionary<int, ushort>();
        bool isFormat0 = format == 0;

        void Sink(uint code, ushort gid)
        {
            int codepoint;
            if (isFormat0)
            {
                // ASCII pass-through for 0x00..0x7F (the Mac Roman lower half is
                // identical to ASCII). Above 0x7F route through the Mac Roman table so
                // byte 0x8A (a-umlaut in Mac Roman) maps to U+00E4, not U+008A.
                codepoint = code < 0x80 ? (int)code : MacRomanHigh[code - 0x80];
            }
            else if (!TryScalarValue(code, out codepoint))
            {
                return;
            }

            gidToUnicode[gid] = codepoint;
            unicodeToGid[codepoint] = gid;
        }

        if (!ParseSubtableBody(cursor, format, Sink)) return null;

        return new OxTrueTypeCMap(gidToUnicode, unicodeToGid, BuildSymbolCodeToGid(data));
    }

    // =====================================================================
    // Lookups
    // =====================================================================

    /// <summary>Unicode scalar for a glyph ID, or null when the glyph is unmapped.
    /// An int rather than a char because format 12 reaches past the BMP.</summary>
    public int? GetUnicode(ushort gid) => _gidToUnicode.TryGetValue(gid, out int cp) ? cp : null;

    /// <summary>The same lookup as text, surrogate-paired where needed.</summary>
    public string? GetUnicodeString(ushort gid)
    {
        int? cp = GetUnicode(gid);
        return cp.HasValue ? char.ConvertFromUtf32(cp.Value) : null;
    }

    /// <summary>Glyph ID for a Unicode scalar, from the same Unicode subtable.</summary>
    public ushort? UnicodeToGid(int codepoint) =>
        _unicodeToGid.TryGetValue(codepoint, out ushort gid) ? gid : null;

    /// <summary>Content byte → GID via the font's (3,0) symbol or (1,0) Macintosh
    /// subtable. Null when the font has no such subtable, in which case the caller
    /// falls back to treating the byte as a GID directly.</summary>
    public ushort? CodeToGid(ushort code) =>
        _symbolCodeToGid.TryGetValue(code, out ushort gid) ? gid : null;

    public int Count => _gidToUnicode.Count;

    public bool IsEmpty => _gidToUnicode.Count == 0;

    // =====================================================================
    // sfnt directory
    // =====================================================================

    private static bool ParseSfntHeader(OxBeReader cursor, out ushort numTables)
    {
        numTables = 0;
        if (!cursor.TryU32(out uint version)) return false;
        if (version != SfntTrueType && version != SfntOpenType && version != SfntAppleTrue) return false;
        if (!cursor.TryU16(out numTables)) return false;
        // searchRange / entrySelector / rangeShift: read to advance past them; the
        // directory is searched linearly rather than by the binary-search hints.
        return cursor.TryU16(out _) && cursor.TryU16(out _) && cursor.TryU16(out _);
    }

    private static bool FindCmapTable(OxBeReader cursor, ushort numTables, out uint offset)
    {
        offset = 0;
        for (int i = 0; i < numTables; i++)
        {
            if (!cursor.TryU32(out uint tag)) return false;
            if (!cursor.TryU32(out _)) return false;               // checksum
            if (!cursor.TryU32(out uint tableOffset)) return false;
            if (!cursor.TryU32(out _)) return false;               // length

            if (tag == CmapTag)
            {
                offset = tableOffset;
                return true;
            }
        }
        return false; // "cmap table not found in font"
    }

    // =====================================================================
    // Subtable formats
    // =====================================================================

    private static bool ParseSubtableBody(OxBeReader cursor, ushort format, Action<uint, ushort> sink) =>
        format switch
        {
            0 => ParseFormat0(cursor, sink),
            4 => ParseFormat4(cursor, sink),
            6 => ParseFormat6(cursor, sink),
            12 => ParseFormat12(cursor, sink),
            _ => false, // "Unsupported cmap format"
        };

    /// <summary>Format 0: legacy 1-byte indexed, Mac Roman era (subtable length 262).
    ///
    /// Microsoft Office subset fonts (Calibri, Times New Roman subsets in Word/Excel
    /// exports) still ship a format-0 cmap for the (1,0) Macintosh encoding alongside
    /// their (3,1) Unicode cmap. When the Unicode cmap is missing or malformed this is
    /// the one that gets picked, and the byte code acts as the Mac Roman char code.</summary>
    private static bool ParseFormat0(OxBeReader cursor, Action<uint, ushort> sink)
    {
        if (!cursor.TryU16(out _)) return false; // length
        if (!cursor.TryU16(out _)) return false; // language

        var glyphIds = new byte[256];
        if (!cursor.TryRead(glyphIds)) return false;

        for (int b = 0; b < glyphIds.Length; b++)
        {
            byte gid = glyphIds[b];
            if (gid == 0) continue;
            sink((uint)b, gid);
        }
        return true;
    }

    /// <summary>Format 4: segmented BMP coverage (U+0000..U+FFFF).</summary>
    private static bool ParseFormat4(OxBeReader cursor, Action<uint, ushort> sink)
    {
        if (!cursor.TryU16(out _)) return false; // length
        if (!cursor.TryU16(out _)) return false; // language

        if (!cursor.TryU16(out ushort segCountX2)) return false;
        int segCount = segCountX2 / 2;

        // Binary-search hints; this walk enumerates every segment instead.
        if (!cursor.TryU16(out _)) return false; // searchRange
        if (!cursor.TryU16(out _)) return false; // entrySelector
        if (!cursor.TryU16(out _)) return false; // rangeShift

        var endCodes = new ushort[segCount];
        for (int i = 0; i < segCount; i++)
            if (!cursor.TryU16(out endCodes[i])) return false;

        if (!cursor.TryU16(out _)) return false; // reserved pad

        var startCodes = new ushort[segCount];
        for (int i = 0; i < segCount; i++)
            if (!cursor.TryU16(out startCodes[i])) return false;

        var idDeltas = new short[segCount];
        for (int i = 0; i < segCount; i++)
            if (!cursor.TryI16(out idDeltas[i])) return false;

        var idRangeOffsets = new ushort[segCount];
        for (int i = 0; i < segCount; i++)
            if (!cursor.TryU16(out idRangeOffsets[i])) return false;

        // glyphIdArray is whatever u16s remain. Rust reads to the end of the whole font
        // blob, not to the end of the subtable, so fonts whose declared subtable length
        // is short still resolve their indirections; index into the blob directly rather
        // than copying it.
        long glyphArrayStart = cursor.Position;
        long glyphArrayLen = (cursor.Length - glyphArrayStart) / 2;
        if (glyphArrayLen < 0) glyphArrayLen = 0;

        int budget = MaxCmapIterations;

        for (int seg = 0; seg < segCount; seg++)
        {
            uint start = startCodes[seg];
            uint end = endCodes[seg];
            int idDelta = idDeltas[seg];

            for (uint charCode = start; charCode <= end; charCode++)
            {
                if (charCode == 0xFFFF) break; // end segment marker
                if (--budget < 0) return true;

                ushort gid;
                if (idRangeOffsets[seg] == 0)
                {
                    gid = unchecked((ushort)(charCode + idDelta));
                }
                else
                {
                    // Per the TrueType spec:
                    //   offset = idRangeOffset[i]/2 + (charCode - startCode[i]) + i - segCount
                    // Rust evaluates this in usize, so a malformed idRangeOffset wraps to a
                    // huge value that then fails the bounds test; a negative result here has
                    // to be rejected the same way rather than used as an index.
                    long offset = idRangeOffsets[seg] / 2
                                  + (long)(charCode - start)
                                  + seg
                                  - segCount;
                    gid = 0;
                    if (offset >= 0 && offset < glyphArrayLen
                        && cursor.TryU16At(glyphArrayStart + offset * 2, out ushort raw) && raw != 0)
                    {
                        gid = unchecked((ushort)(raw + idDelta));
                    }
                }

                if (gid != 0) sink(charCode, gid);
            }
        }

        return true;
    }

    /// <summary>Format 6: a trimmed contiguous run of codes.</summary>
    private static bool ParseFormat6(OxBeReader cursor, Action<uint, ushort> sink)
    {
        if (!cursor.TryU16(out _)) return false; // length
        if (!cursor.TryU16(out _)) return false; // language

        if (!cursor.TryU16(out ushort firstCode)) return false;
        if (!cursor.TryU16(out ushort count)) return false;

        for (int i = 0; i < count; i++)
        {
            if (!cursor.TryU16(out ushort gid)) return false;
            sink((uint)firstCode + (uint)i, gid);
        }
        return true;
    }

    /// <summary>Format 12: segmented coverage over the full Unicode range.</summary>
    private static bool ParseFormat12(OxBeReader cursor, Action<uint, ushort> sink)
    {
        if (!cursor.TryU16(out _)) return false;  // reserved
        if (!cursor.TryU32(out _)) return false;  // length
        if (!cursor.TryU32(out _)) return false;  // language
        if (!cursor.TryU32(out uint numGroups)) return false;

        int budget = MaxCmapIterations;

        for (uint g = 0; g < numGroups; g++)
        {
            if (!cursor.TryU32(out uint startCharCode)) return false;
            if (!cursor.TryU32(out uint endCharCode)) return false;
            if (!cursor.TryU32(out uint startGid)) return false;

            if (endCharCode < startCharCode) continue;
            // Nothing above U+10FFFF is a Unicode scalar, so a group claiming to run to
            // 0xFFFFFFFF contributes nothing past this clamp — it only burns the budget.
            if (endCharCode > 0x10FFFF) endCharCode = 0x10FFFF;

            for (uint offset = 0; startCharCode + offset <= endCharCode; offset++)
            {
                if (--budget < 0) return true;
                // The u16 truncation is deliberate: GIDs are 16-bit, and a group whose
                // startGlyphId + offset overflows is malformed either way.
                sink(startCharCode + offset, unchecked((ushort)(startGid + offset)));
            }
        }

        return true;
    }

    /// <summary>`char::from_u32` semantics: surrogate halves and anything above
    /// U+10FFFF are not scalar values and are dropped.</summary>
    private static bool TryScalarValue(uint code, out int codepoint)
    {
        if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
        {
            codepoint = 0;
            return false;
        }
        codepoint = (int)code;
        return true;
    }

    // =====================================================================
    // Face-wide Unicode → GID
    // =====================================================================

    /// <summary>Union of every Unicode subtable, earlier records in the directory
    /// winning. This is `ttf-parser`'s `Face::glyph_index` rather than the single
    /// best-subtable pick above, and truetype_parser.rs's `build_unicode_map` goes
    /// through exactly that: fonts do ship a (3,10) subtable narrower than their
    /// (3,1) one, and picking only the "best" would lose the codepoints it omits.
    /// Returns an empty map when the font has no usable cmap.</summary>
    internal static Dictionary<int, ushort> BuildFaceUnicodeToGid(byte[]? data)
    {
        var map = new Dictionary<int, ushort>();
        if (data is null || data.Length == 0) return map;

        var cursor = new OxBeReader(data);
        if (!ParseSfntHeader(cursor, out ushort numTables)) return map;
        if (!FindCmapTable(cursor, numTables, out uint cmapOffset)) return map;

        cursor.Position = cmapOffset;
        if (!cursor.TryU16(out ushort cmapVersion) || cmapVersion != 0) return map;
        if (!cursor.TryU16(out ushort numSubtables)) return map;

        var unicodeSubtables = new List<uint>();
        for (int i = 0; i < numSubtables; i++)
        {
            if (!cursor.TryU16(out ushort platformId)) return map;
            if (!cursor.TryU16(out ushort encodingId)) return map;
            if (!cursor.TryU32(out uint offset)) return map;

            // ttf-parser counts the Unicode platform wholesale, plus Windows symbol
            // (which addresses the F0xx PUA), Unicode BMP and full repertoire.
            bool isUnicode = platformId == 0
                || (platformId == 3 && (encodingId == 0 || encodingId == 1 || encodingId == 10));
            if (isUnicode) unicodeSubtables.Add(offset);
        }

        foreach (uint offset in unicodeSubtables)
        {
            cursor.Position = (long)cmapOffset + offset;
            if (!cursor.TryU16(out ushort format)) continue;
            bool isFormat0 = format == 0;

            void Sink(uint code, ushort gid)
            {
                int codepoint;
                if (isFormat0) codepoint = code < 0x80 ? (int)code : MacRomanHigh[code - 0x80];
                else if (!TryScalarValue(code, out codepoint)) return;

                // First subtable to answer for a codepoint keeps it.
                if (!map.ContainsKey(codepoint)) map[codepoint] = gid;
            }

            ParseSubtableBody(cursor, format, Sink);
        }

        return map;
    }

    // =====================================================================
    // Symbolic fonts
    // =====================================================================

    /// <summary>Build content-byte → GID from the font's (3,0) symbol or (1,0)
    /// Macintosh subtable, resolving the 0xF000 symbol-PUA offset up front. Symbolic
    /// fonts index glyphs by content byte rather than by GID, so decode needs this hop
    /// before the GID → Unicode one. Empty when the font has no such subtable or it
    /// fails to parse — decode then treats the byte as a GID.</summary>
    private static Dictionary<uint, ushort> BuildSymbolCodeToGid(byte[] data)
    {
        var map = new Dictionary<uint, ushort>();

        var cursor = new OxBeReader(data);
        if (!ParseSfntHeader(cursor, out ushort numTables)) return map;
        if (!FindCmapTable(cursor, numTables, out uint cmapOffset)) return map;

        cursor.Position = cmapOffset;
        if (!cursor.TryU16(out ushort cmapVersion) || cmapVersion != 0) return map;
        if (!cursor.TryU16(out ushort numSubtables)) return map;

        uint chosenOffset = 0;
        int best = 0; // starts at 0, so only a (3,0) or (1,0) record is ever chosen
        bool found = false;

        for (int i = 0; i < numSubtables; i++)
        {
            if (!cursor.TryU16(out ushort platformId)) return map;
            if (!cursor.TryU16(out ushort encodingId)) return map;
            if (!cursor.TryU32(out uint offset)) return map;

            int priority = (platformId, encodingId) switch
            {
                (3, 0) => 2, // Windows symbol
                (1, 0) => 1, // Macintosh Roman
                _ => 0,
            };

            if (priority > best)
            {
                best = priority;
                chosenOffset = offset;
                found = true;
            }
        }

        if (!found) return map;

        cursor.Position = (long)cmapOffset + chosenOffset;
        if (!cursor.TryU16(out ushort format)) return map;

        var raw = new Dictionary<uint, ushort>();
        void Sink(uint code, ushort gid)
        {
            // Only single-byte content codes and their 0xF0xx symbol-PUA aliases can
            // ever be asked for; keeping the rest would materialise a whole BMP subtable
            // for nothing.
            if (code < 0x100 || (code >= 0xF000 && code <= 0xF0FF)) raw[code] = gid;
        }

        if (!ParseSubtableBody(cursor, format, Sink)) return map;

        for (uint code = 0; code < 256; code++)
        {
            if (raw.TryGetValue(code, out ushort gid) || raw.TryGetValue(0xF000u | code, out gid))
                map[code] = gid;
        }

        return map;
    }
}
