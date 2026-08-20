// Ported from pdf_oxide 0.3.77 `src/fonts/truetype_parser.rs`
// (`TrueTypeFont::parse` / `build_unicode_map` / `build_width_table` / accessors,
//  `FontMetrics::from_font` and its PDF-unit conversions).
//
// The Rust original delegates every sfnt read to the `ttf-parser` crate. This port
// reads the tables itself — table directory plus head / hhea / maxp / hmtx / loca /
// post / OS_2 / name — so the .NET build carries no font-parsing dependency. Each
// accessor reproduces the value `ttf-parser` would have returned for the same font,
// including which table it prefers (e.g. OS/2 typo metrics over hhea when the font
// asks for them). `cmap` lookups are delegated to OxTrueTypeCMap.

using System.Globalization;
using System.Text;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>Big-endian cursor over a font blob. Every read is bounds-checked and
/// reports failure rather than throwing: /FontFile2 streams arrive straight from
/// untrusted PDFs, so a truncated or hostile table must degrade to "no metrics".</summary>
internal sealed class OxBeReader
{
    private readonly byte[] _data;
    private long _pos;

    public OxBeReader(byte[] data, long position = 0)
    {
        _data = data;
        _pos = position;
    }

    public int Length => _data.Length;

    /// <summary>Assignable past the end of the buffer, matching Rust's
    /// <c>Cursor::set_position</c>: an out-of-range offset is not an error by
    /// itself, it just makes the next read fail.</summary>
    public long Position
    {
        get => _pos;
        set => _pos = value;
    }

    private bool CanRead(int n) => _pos >= 0 && n <= _data.Length && _pos <= _data.Length - n;

    public bool TryU16(out ushort value)
    {
        if (!CanRead(2)) { value = 0; return false; }
        value = (ushort)((_data[_pos] << 8) | _data[_pos + 1]);
        _pos += 2;
        return true;
    }

    public bool TryI16(out short value)
    {
        bool ok = TryU16(out ushort raw);
        value = unchecked((short)raw);
        return ok;
    }

    public bool TryU32(out uint value)
    {
        if (!CanRead(4)) { value = 0; return false; }
        value = ((uint)_data[_pos] << 24) | ((uint)_data[_pos + 1] << 16)
              | ((uint)_data[_pos + 2] << 8) | _data[_pos + 3];
        _pos += 4;
        return true;
    }

    public bool TryI32(out int value)
    {
        bool ok = TryU32(out uint raw);
        value = unchecked((int)raw);
        return ok;
    }

    public bool TryRead(byte[] destination)
    {
        if (!CanRead(destination.Length)) return false;
        Array.Copy(_data, _pos, destination, 0, destination.Length);
        _pos += destination.Length;
        return true;
    }

    /// <summary>Absolute big-endian u16 read that leaves the cursor alone; used where
    /// the Rust code indexes into a pre-read array instead of streaming.</summary>
    public bool TryU16At(long offset, out ushort value)
    {
        if (offset < 0 || offset > _data.Length - 2) { value = 0; return false; }
        value = (ushort)((_data[offset] << 8) | _data[offset + 1]);
        return true;
    }
}

/// <summary>A parsed TrueType/OpenType font program (the payload of a PDF
/// /FontFile2 stream). Mirrors `TrueTypeFont` in truetype_parser.rs.</summary>
internal sealed class OxTrueTypeFont
{
    // sfnt versions accepted by ttf-parser for a single-face file. TrueType
    // collections ('ttcf') are not accepted: a /FontFile2 stream is always one face.
    private const uint SfntTrueType = 0x00010000;
    private const uint SfntOpenType = 0x4F54544F; // "OTTO"
    private const uint SfntAppleTrue = 0x74727565; // "true"

    private const uint TagHead = 0x68656164;
    private const uint TagHhea = 0x68686561;
    private const uint TagMaxp = 0x6D617870;
    private const uint TagHmtx = 0x686D7478;
    private const uint TagLoca = 0x6C6F6361;
    private const uint TagPost = 0x706F7374;
    private const uint TagOs2 = 0x4F532F32; // "OS/2"
    private const uint TagName = 0x6E616D65;

    private const ushort NameIdFamily = 1;
    private const ushort NameIdPostScript = 6;

    private readonly byte[] _data;
    private readonly Dictionary<uint, (int Offset, int Length)> _tables;

    private readonly ushort _unitsPerEm;
    private readonly short _headXMin, _headYMin, _headXMax, _headYMax;
    private readonly short _indexToLocFormat;
    private readonly short _hheaAscender, _hheaDescender;
    private readonly ushort _numberOfHMetrics;
    private readonly ushort _numGlyphs;

    private readonly short? _os2TypoAscender, _os2TypoDescender;
    private readonly short? _os2CapHeight, _os2XHeight;
    private readonly bool _os2UseTypoMetrics;
    private readonly bool _os2Bold, _os2Italic;

    private readonly float _italicAngle;
    private readonly bool _isMonospaced;

    private readonly Dictionary<int, ushort> _unicodeToGlyph = new();
    private readonly ushort[] _glyphWidths;

    private OxTrueTypeFont(
        byte[] data,
        Dictionary<uint, (int, int)> tables,
        HeadTable head,
        HheaTable hhea,
        ushort numGlyphs,
        Os2Table? os2,
        PostTable? post)
    {
        _data = data;
        _tables = tables;
        _unitsPerEm = head.UnitsPerEm;
        _headXMin = head.XMin; _headYMin = head.YMin; _headXMax = head.XMax; _headYMax = head.YMax;
        _indexToLocFormat = head.IndexToLocFormat;
        _hheaAscender = hhea.Ascender;
        _hheaDescender = hhea.Descender;
        _numberOfHMetrics = hhea.NumberOfHMetrics;
        _numGlyphs = numGlyphs;

        if (os2 is not null)
        {
            _os2TypoAscender = os2.TypoAscender;
            _os2TypoDescender = os2.TypoDescender;
            _os2CapHeight = os2.CapHeight;
            _os2XHeight = os2.XHeight;
            _os2UseTypoMetrics = os2.UseTypoMetrics;
            _os2Bold = os2.IsBold;
            _os2Italic = os2.IsItalic;
        }
        else
        {
            // head.macStyle is the pre-OS/2 style signal. ttf-parser reports false
            // without OS/2; the macStyle bits stay available via HeadMacStyle for
            // callers that want the looser answer.
            _os2Bold = false;
            _os2Italic = false;
        }

        _italicAngle = post?.ItalicAngle ?? 0f;
        _isMonospaced = post?.IsFixedPitch ?? false;

        _glyphWidths = new ushort[_numGlyphs];
        HeadMacStyle = head.MacStyle;

        BuildUnicodeMap();
        BuildWidthTable();
    }

    /// <summary>Raw head.macStyle bits (bit 0 bold, bit 1 italic).</summary>
    public ushort HeadMacStyle { get; }

    /// <summary>The font's Unicode cmap, or null when the font has none or it is
    /// unusable. Also serves callers that need GID→Unicode for text extraction.</summary>
    public OxTrueTypeCMap? CMap { get; private set; }

    // =====================================================================
    // Parsing — TrueTypeFont::parse
    // =====================================================================

    /// <summary>Parse a TrueType/OpenType font program. Returns null instead of
    /// raising: `TrueTypeError::{EmptyFont, ParseError}` in Rust, and every caller
    /// in the extraction path treats a bad embedded font as "no font".</summary>
    public static OxTrueTypeFont? Parse(byte[]? data)
    {
        if (data is null || data.Length == 0) return null; // TrueTypeError::EmptyFont

        var tables = ReadTableDirectory(data);
        if (tables is null) return null;

        // ttf-parser's Face::parse fails without these three, so neither does this port
        // pretend a font without them is usable.
        var head = ReadHead(data, tables);
        if (head is null) return null;
        var hhea = ReadHhea(data, tables);
        if (hhea is null) return null;
        if (!ReadNumGlyphs(data, tables, out ushort numGlyphs)) return null;

        var os2 = ReadOs2(data, tables);
        var post = ReadPost(data, tables);

        return new OxTrueTypeFont(data, tables, head, hhea, numGlyphs, os2, post);
    }

    private static Dictionary<uint, (int, int)>? ReadTableDirectory(byte[] data)
    {
        var r = new OxBeReader(data);
        if (!r.TryU32(out uint version)) return null;
        if (version != SfntTrueType && version != SfntOpenType && version != SfntAppleTrue) return null;

        if (!r.TryU16(out ushort numTables)) return null;
        if (!r.TryU16(out _) || !r.TryU16(out _) || !r.TryU16(out _)) return null; // searchRange/entrySelector/rangeShift

        var tables = new Dictionary<uint, (int, int)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            if (!r.TryU32(out uint tag)) return null;
            if (!r.TryU32(out _)) return null; // checksum
            if (!r.TryU32(out uint offset)) return null;
            if (!r.TryU32(out uint length)) return null;

            // A record pointing outside the blob is dropped rather than fatal — the same
            // slice-or-skip ttf-parser does, so a font with one bad optional table still
            // yields metrics from the tables that are intact.
            if (offset > (uint)data.Length) continue;
            if (length > (uint)data.Length - offset) continue;
            tables[tag] = ((int)offset, (int)length);
        }
        return tables;
    }

    private sealed class HeadTable
    {
        public ushort UnitsPerEm;
        public short XMin, YMin, XMax, YMax;
        public ushort MacStyle;
        public short IndexToLocFormat;
    }

    private static HeadTable? ReadHead(byte[] data, Dictionary<uint, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue(TagHead, out var t) || t.Length < 54) return null;
        var r = new OxBeReader(data, t.Offset + 18);
        if (!r.TryU16(out ushort unitsPerEm)) return null;
        // 16..16384 is the spec's legal range; ttf-parser rejects the face outright
        // outside it, and every metric here divides by unitsPerEm.
        if (unitsPerEm < 16 || unitsPerEm > 16384) return null;

        r.Position = t.Offset + 36;
        if (!r.TryI16(out short xMin) || !r.TryI16(out short yMin)
            || !r.TryI16(out short xMax) || !r.TryI16(out short yMax)) return null;
        if (!r.TryU16(out ushort macStyle)) return null;

        r.Position = t.Offset + 50;
        if (!r.TryI16(out short indexToLocFormat)) return null;

        return new HeadTable
        {
            UnitsPerEm = unitsPerEm,
            XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
            MacStyle = macStyle,
            IndexToLocFormat = indexToLocFormat,
        };
    }

    private sealed class HheaTable
    {
        public short Ascender, Descender;
        public ushort NumberOfHMetrics;
    }

    private static HheaTable? ReadHhea(byte[] data, Dictionary<uint, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue(TagHhea, out var t) || t.Length < 36) return null;
        var r = new OxBeReader(data, t.Offset + 4);
        if (!r.TryI16(out short ascender) || !r.TryI16(out short descender)) return null;
        r.Position = t.Offset + 34;
        if (!r.TryU16(out ushort numberOfHMetrics)) return null;
        return new HheaTable { Ascender = ascender, Descender = descender, NumberOfHMetrics = numberOfHMetrics };
    }

    private static bool ReadNumGlyphs(byte[] data, Dictionary<uint, (int Offset, int Length)> tables, out ushort numGlyphs)
    {
        numGlyphs = 0;
        if (!tables.TryGetValue(TagMaxp, out var t) || t.Length < 6) return false;
        var r = new OxBeReader(data, t.Offset);
        if (!r.TryU32(out uint version)) return false;
        if (version != 0x00005000 && version != 0x00010000) return false;
        if (!r.TryU16(out numGlyphs)) return false;
        // ttf-parser models numGlyphs as NonZeroU16; a zero-glyph face is rejected.
        return numGlyphs != 0;
    }

    private sealed class Os2Table
    {
        public short TypoAscender, TypoDescender;
        public short? CapHeight, XHeight;
        public bool UseTypoMetrics;
        public bool IsBold, IsItalic;
    }

    private static Os2Table? ReadOs2(byte[] data, Dictionary<uint, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue(TagOs2, out var t) || t.Length < 78) return null;
        var r = new OxBeReader(data, t.Offset);
        if (!r.TryU16(out ushort version)) return null;

        r.Position = t.Offset + 62;
        if (!r.TryU16(out ushort fsSelection)) return null;
        r.Position = t.Offset + 68; // past usFirstCharIndex / usLastCharIndex
        if (!r.TryI16(out short typoAscender) || !r.TryI16(out short typoDescender)) return null;

        short? xHeight = null, capHeight = null;
        // sxHeight/sCapHeight only exist from OS/2 version 2 onward; older tables end
        // before offset 86 and reading them would pick up whatever table follows.
        if (version >= 2 && t.Length >= 90)
        {
            r.Position = t.Offset + 86;
            if (r.TryI16(out short xh)) xHeight = xh;
            if (r.TryI16(out short ch)) capHeight = ch;
        }

        return new Os2Table
        {
            TypoAscender = typoAscender,
            TypoDescender = typoDescender,
            CapHeight = capHeight,
            XHeight = xHeight,
            UseTypoMetrics = (fsSelection & 0x0080) != 0,
            IsBold = (fsSelection & 0x0020) != 0,
            IsItalic = (fsSelection & 0x0001) != 0,
        };
    }

    private sealed class PostTable
    {
        public float ItalicAngle;
        public bool IsFixedPitch;
    }

    private static PostTable? ReadPost(byte[] data, Dictionary<uint, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue(TagPost, out var t) || t.Length < 32) return null;
        var r = new OxBeReader(data, t.Offset + 4);
        if (!r.TryI32(out int italicAngleFixed)) return null;
        r.Position = t.Offset + 12;
        if (!r.TryU32(out uint isFixedPitch)) return null;
        return new PostTable
        {
            // post.italicAngle is a 16.16 fixed-point value; pdf_oxide keeps it as f32.
            ItalicAngle = italicAngleFixed / 65536f,
            IsFixedPitch = isFixedPitch != 0,
        };
    }

    // =====================================================================
    // Caches — build_unicode_map / build_width_table
    // =====================================================================

    private void BuildUnicodeMap()
    {
        CMap = OxTrueTypeCMap.FromFontData(_data);

        foreach (var (codepoint, gid) in OxTrueTypeCMap.BuildFaceUnicodeToGid(_data))
        {
            // Basic Multilingual Plane (covers virtually all common scripts incl. CJK)
            // plus the emoji supplementary range. Scanning all 17 planes on every font
            // parse would be wasteful and this runs in the extraction hot path; the
            // emoji range is the one supplementary block that occurs routinely in user
            // text (e.g. filled form fields).
            if (codepoint <= 0xFFFF || (codepoint >= 0x1F000 && codepoint <= 0x1FAFF))
                _unicodeToGlyph[codepoint] = gid;
        }
    }

    private void BuildWidthTable()
    {
        for (int gid = 0; gid < _numGlyphs; gid++)
        {
            ushort advance = GlyphHorAdvance((ushort)gid) ?? 0;
            // Store as width in units of 1/1000 of em. The truncating u16 cast is the
            // Rust behaviour and is what lands in the PDF /W array.
            _glyphWidths[gid] = unchecked((ushort)((uint)advance * 1000u / _unitsPerEm));
        }
    }

    // =====================================================================
    // Table access
    // =====================================================================

    /// <summary>Look up a table by its four-character tag (e.g. "hmtx").
    /// Returns false when absent or when its record pointed outside the blob.</summary>
    public bool TryGetTable(string tag, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (tag.Length != 4) return false;
        uint key = ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
        if (!_tables.TryGetValue(key, out var t)) return false;
        offset = t.Offset;
        length = t.Length;
        return true;
    }

    public bool HasTable(string tag) => TryGetTable(tag, out _, out _);

    /// <summary>Advance width in font units straight from `hmtx`.
    /// Glyphs at or past numberOfHMetrics share the last entry's advance — the
    /// trailing-run compression that lets monospaced tails ship one metric.</summary>
    public ushort? GlyphHorAdvance(ushort glyphId)
    {
        if (glyphId >= _numGlyphs) return null;
        if (_numberOfHMetrics == 0) return null;
        if (!_tables.TryGetValue(TagHmtx, out var t)) return null;

        int index = glyphId < _numberOfHMetrics ? glyphId : _numberOfHMetrics - 1;
        var r = new OxBeReader(_data, (long)t.Offset + (long)index * 4);
        if ((long)index * 4 + 2 > t.Length) return null;
        return r.TryU16(out ushort advance) ? advance : null;
    }

    /// <summary>Byte range of this glyph's outline inside `glyf`, read from `loca`.
    /// An empty range (start == end) is a blank glyph such as space, which is legal;
    /// a reversed or out-of-table range is treated as absent.</summary>
    public bool TryGetGlyphRange(ushort glyphId, out uint start, out uint end)
    {
        start = 0;
        end = 0;
        if (glyphId >= _numGlyphs) return false;
        if (!_tables.TryGetValue(TagLoca, out var t)) return false;

        bool shortFormat = _indexToLocFormat == 0;
        int entrySize = shortFormat ? 2 : 4;
        long need = ((long)glyphId + 2) * entrySize;
        if (need > t.Length) return false;

        var r = new OxBeReader(_data, (long)t.Offset + (long)glyphId * entrySize);
        if (shortFormat)
        {
            // The short form stores offsets divided by two, so the whole table fits u16.
            if (!r.TryU16(out ushort s) || !r.TryU16(out ushort e)) return false;
            start = (uint)s * 2u;
            end = (uint)e * 2u;
        }
        else
        {
            if (!r.TryU32(out start) || !r.TryU32(out end)) return false;
        }
        return end >= start;
    }

    // =====================================================================
    // Accessors
    // =====================================================================

    public byte[] RawData => _data;

    public ushort UnitsPerEm => _unitsPerEm;

    /// <summary>hhea ascender, unless the font sets the OS/2 USE_TYPO_METRICS bit
    /// to say its typographic metrics are authoritative.</summary>
    public short Ascender =>
        _os2UseTypoMetrics && _os2TypoAscender.HasValue ? _os2TypoAscender.Value : _hheaAscender;

    public short Descender =>
        _os2UseTypoMetrics && _os2TypoDescender.HasValue ? _os2TypoDescender.Value : _hheaDescender;

    public short? CapHeight => _os2CapHeight;

    public short? XHeight => _os2XHeight;

    public float ItalicAngle => _italicAngle;

    public bool IsBold => _os2Bold;

    public bool IsItalic => _os2Italic;

    public bool IsMonospaced => _isMonospaced;

    public (short XMin, short YMin, short XMax, short YMax) BBox => (_headXMin, _headYMin, _headXMax, _headYMax);

    public ushort NumGlyphs => _numGlyphs;

    public ushort NumberOfHMetrics => _numberOfHMetrics;

    public string? PostScriptName => FindName(NameIdPostScript);

    public string? FamilyName => FindName(NameIdFamily);

    /// <summary>Glyph ID for a Unicode codepoint, or null when unmapped.</summary>
    public ushort? GlyphId(int codepoint) =>
        _unicodeToGlyph.TryGetValue(codepoint, out ushort gid) ? gid : null;

    /// <summary>Glyph width in 1/1000 em. 500 is the Rust default for an unknown
    /// glyph — a mid-range guess that keeps layout from collapsing.</summary>
    public ushort GlyphWidth(ushort glyphId) =>
        glyphId < _glyphWidths.Length ? _glyphWidths[glyphId] : (ushort)500;

    public ushort CharWidth(int codepoint)
    {
        ushort? gid = GlyphId(codepoint);
        return gid.HasValue ? GlyphWidth(gid.Value) : (ushort)500;
    }

    public List<int> SupportedCodepoints()
    {
        var codepoints = new List<int>(_unicodeToGlyph.Keys);
        codepoints.Sort();
        return codepoints;
    }

    /// <summary>StemV is not stored anywhere in TrueType, so pdf_oxide estimates it
    /// from the weight flag alone. Kept as-is so generated FontDescriptors match.</summary>
    public short StemV => IsBold ? (short)140 : (short)80;

    /// <summary>PDF FontDescriptor flags, per PDF spec Table 123.</summary>
    public uint FontFlags()
    {
        uint flags = 0;

        // Bit 1: FixedPitch (monospace)
        if (IsMonospaced) flags |= 1u << 0;

        // Bit 6: Nonsymbolic — set unconditionally because most TrueType fonts are,
        // and pdf_oxide does not attempt to detect symbolic ones here.
        flags |= 1u << 5;

        // Bit 7: Italic
        if (IsItalic) flags |= 1u << 6;

        return flags;
    }

    private string? FindName(ushort nameId)
    {
        if (!_tables.TryGetValue(TagName, out var t)) return null;
        var r = new OxBeReader(_data, t.Offset);
        if (!r.TryU16(out _)) return null; // format
        if (!r.TryU16(out ushort count)) return null;
        if (!r.TryU16(out ushort stringOffset)) return null;

        for (int i = 0; i < count; i++)
        {
            if (!r.TryU16(out ushort platformId)) return null;
            if (!r.TryU16(out ushort encodingId)) return null;
            if (!r.TryU16(out _)) return null; // languageID
            if (!r.TryU16(out ushort recordNameId)) return null;
            if (!r.TryU16(out ushort length)) return null;
            if (!r.TryU16(out ushort offset)) return null;
            if (recordNameId != nameId) continue;

            // ttf-parser decodes only UTF-16BE name records; a Macintosh-only name is
            // reported as absent, which is why FontMetrics falls back to "Unknown".
            bool isUnicode = platformId == 0
                || (platformId == 3 && (encodingId == 0 || encodingId == 1 || encodingId == 10));
            if (!isUnicode || (length & 1) != 0) continue;

            long start = (long)t.Offset + stringOffset + offset;
            if (start < 0 || length > _data.Length || start > _data.Length - length) continue;
            return Encoding.BigEndianUnicode.GetString(_data, (int)start, length);
        }
        return null;
    }

    // =====================================================================
    // PDF emission helpers
    // =====================================================================

    /// <summary>CIDFont /W array: `[start [w1 w2 ...] start2 [...]]`, consecutive
    /// glyph IDs grouped into one run. For Identity-H, CID == GID.</summary>
    public byte[] GenerateWidthsArray(IEnumerable<ushort> usedGlyphs)
    {
        var glyphs = new List<ushort>(new SortedSet<ushort>(usedGlyphs));
        var result = new StringBuilder();
        result.Append('[');

        int i = 0;
        while (i < glyphs.Count)
        {
            ushort start = glyphs[i];
            ushort end = start;
            var widths = new List<ushort> { GlyphWidth(start) };

            while (i + 1 < glyphs.Count && glyphs[i + 1] == end + 1)
            {
                i++;
                end = glyphs[i];
                widths.Add(GlyphWidth(end));
            }

            result.Append(start.ToString(CultureInfo.InvariantCulture)).Append(" [");
            for (int j = 0; j < widths.Count; j++)
            {
                if (j > 0) result.Append(' ');
                result.Append(widths[j].ToString(CultureInfo.InvariantCulture));
            }
            result.Append(']');

            i++;
        }

        result.Append(']');
        return Encoding.ASCII.GetBytes(result.ToString());
    }

    /// <summary>ToUnicode CMap mapping GIDs (used as CIDs under Identity-H) back to
    /// Unicode, so readers can extract text from the generated PDF.
    /// <paramref name="usedChars"/> maps codepoint → GID.</summary>
    public string GenerateToUnicodeCMap(IReadOnlyDictionary<int, ushort> usedChars)
    {
        var cmap = new StringBuilder();

        cmap.Append("/CIDInit /ProcSet findresource begin\n");
        cmap.Append("12 dict begin\n");
        cmap.Append("begincmap\n");
        cmap.Append("/CIDSystemInfo <<\n");
        cmap.Append("  /Registry (Adobe)\n");
        cmap.Append("  /Ordering (UCS)\n");
        cmap.Append("  /Supplement 0\n");
        cmap.Append(">> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n");
        cmap.Append("/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n");
        cmap.Append("<0000> <FFFF>\n");
        cmap.Append("endcodespacerange\n");

        // Rust sorts by GID out of a HashMap, so ties between two codepoints sharing a
        // GID are unordered there; the codepoint tiebreak makes this port deterministic.
        var mappings = new List<(ushort Gid, int Unicode)>(usedChars.Count);
        foreach (var kv in usedChars) mappings.Add((kv.Value, kv.Key));
        mappings.Sort((a, b) => a.Gid != b.Gid ? a.Gid.CompareTo(b.Gid) : a.Unicode.CompareTo(b.Unicode));

        // Max 100 bfchar entries per section per the PDF spec.
        for (int offset = 0; offset < mappings.Count; offset += 100)
        {
            int chunk = Math.Min(100, mappings.Count - offset);
            cmap.Append(chunk.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int k = offset; k < offset + chunk; k++)
            {
                var (gid, unicode) = mappings[k];
                if (unicode <= 0xFFFF)
                {
                    cmap.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture))
                        .Append("> <").Append(unicode.ToString("X4", CultureInfo.InvariantCulture)).Append(">\n");
                }
                else
                {
                    int high = ((unicode - 0x10000) >> 10) + 0xD800;
                    int low = ((unicode - 0x10000) & 0x3FF) + 0xDC00;
                    cmap.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture))
                        .Append("> <").Append(high.ToString("X4", CultureInfo.InvariantCulture))
                        .Append(low.ToString("X4", CultureInfo.InvariantCulture)).Append(">\n");
                }
            }
            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\n");
        cmap.Append("CMapName currentdict /CMap defineresource pop\n");
        cmap.Append("end\n");
        cmap.Append("end\n");

        return cmap.ToString();
    }
}

/// <summary>Metrics harvested for a PDF FontDescriptor. Mirrors `FontMetrics`
/// in truetype_parser.rs; f32 throughout, as in the Rust original.</summary>
internal sealed class OxFontMetrics
{
    public string Name { get; init; } = "Unknown";
    public string Family { get; init; } = "Unknown";
    public ushort UnitsPerEm { get; init; }
    public short Ascender { get; init; }
    public short Descender { get; init; }
    public short CapHeight { get; init; }
    public short XHeight { get; init; }
    public float ItalicAngle { get; init; }
    public (short XMin, short YMin, short XMax, short YMax) BBox { get; init; }
    public short StemV { get; init; }
    public uint Flags { get; init; }
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }

    public static OxFontMetrics FromFont(OxTrueTypeFont font) => new()
    {
        Name = font.PostScriptName ?? "Unknown",
        Family = font.FamilyName ?? "Unknown",
        UnitsPerEm = font.UnitsPerEm,
        Ascender = font.Ascender,
        Descender = font.Descender,
        // Fonts without OS/2 v2 carry no cap/x-height; the ascender (and half of it)
        // are the Rust stand-ins, close enough for a FontDescriptor.
        CapHeight = font.CapHeight ?? font.Ascender,
        XHeight = font.XHeight ?? (short)(font.Ascender * 0.5f),
        ItalicAngle = font.ItalicAngle,
        BBox = font.BBox,
        StemV = font.StemV,
        Flags = font.FontFlags(),
        IsBold = font.IsBold,
        IsItalic = font.IsItalic,
    };

    /// <summary>Font units → PDF glyph space (1/1000 em).</summary>
    public int ToPdfUnits(short value) => value * 1000 / UnitsPerEm;

    public int PdfAscender => ToPdfUnits(Ascender);

    public int PdfDescender => ToPdfUnits(Descender);

    public int PdfCapHeight => ToPdfUnits(CapHeight);

    public (int XMin, int YMin, int XMax, int YMax) PdfBBox =>
        (ToPdfUnits(BBox.XMin), ToPdfUnits(BBox.YMin), ToPdfUnits(BBox.XMax), ToPdfUnits(BBox.YMax));
}
