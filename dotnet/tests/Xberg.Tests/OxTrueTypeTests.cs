using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

/// <summary>Synthetic-font coverage for the pdf_oxide TrueType port
/// (fonts/truetype_parser.rs, fonts/truetype_cmap.rs). Every font here is built
/// byte by byte so the expected lookups are known exactly, and so the malformed
/// cases can be malformed in one specific way at a time.</summary>
public class OxTrueTypeTests
{
    // =====================================================================
    // Byte-level font builders
    // =====================================================================

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public int Length => _b.Count;
        public byte[] ToArray() => _b.ToArray();

        public Buf U8(int v) { _b.Add((byte)v); return this; }
        public Buf U16(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); return this; }
        public Buf I16(int v) => U16(v & 0xFFFF);
        public Buf U32(long v)
        {
            _b.Add((byte)(v >> 24)); _b.Add((byte)(v >> 16));
            _b.Add((byte)(v >> 8)); _b.Add((byte)v);
            return this;
        }
        public Buf Bytes(byte[] v) { _b.AddRange(v); return this; }
        public Buf Pad(int count) { for (int i = 0; i < count; i++) _b.Add(0); return this; }
        public void PatchU32(int at, long v)
        {
            _b[at] = (byte)(v >> 24); _b[at + 1] = (byte)(v >> 16);
            _b[at + 2] = (byte)(v >> 8); _b[at + 3] = (byte)v;
        }
    }

    private static uint Tag(string s) =>
        ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    /// <summary>Assemble an sfnt from named table blobs: header, directory (tags in
    /// sorted order, as real fonts do), then the payloads.</summary>
    private static byte[] BuildFont(params (string Tag, byte[] Data)[] tables)
    {
        var ordered = tables.OrderBy(t => Tag(t.Tag)).ToArray();
        var buf = new Buf();
        buf.U32(0x00010000).U16(ordered.Length).U16(16).U16(0).U16(0);

        int dirStart = buf.Length;
        foreach (var _ in ordered) buf.U32(0).U32(0).U32(0).U32(0);

        int cursor = buf.Length;
        var offsets = new int[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            offsets[i] = cursor;
            cursor += ordered[i].Data.Length;
            if ((cursor & 3) != 0) cursor += 4 - (cursor & 3); // long-aligned, per spec
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            int rec = dirStart + i * 16;
            buf.PatchU32(rec, Tag(ordered[i].Tag));
            buf.PatchU32(rec + 8, offsets[i]);
            buf.PatchU32(rec + 12, ordered[i].Data.Length);
        }

        foreach (var t in ordered)
        {
            buf.Bytes(t.Data);
            while ((buf.Length & 3) != 0) buf.U8(0);
        }
        return buf.ToArray();
    }

    private static byte[] Head(int unitsPerEm = 1000, int indexToLocFormat = 0, int macStyle = 0)
    {
        var b = new Buf();
        b.U32(0x00010000);            // version
        b.U32(0);                     // fontRevision
        b.U32(0);                     // checkSumAdjustment
        b.U32(0x5F0F3CF5);            // magicNumber
        b.U16(0);                     // flags
        b.U16(unitsPerEm);            // 18: unitsPerEm
        b.U32(0).U32(0);              // created
        b.U32(0).U32(0);              // modified
        b.I16(-50).I16(-200).I16(900).I16(800); // 36: xMin yMin xMax yMax
        b.U16(macStyle);              // 44: macStyle
        b.U16(8);                     // lowestRecPPEM
        b.I16(2);                     // fontDirectionHint
        b.I16(indexToLocFormat);      // 50: indexToLocFormat
        b.I16(0);                     // 52: glyphDataFormat
        return b.ToArray();
    }

    private static byte[] Hhea(int ascender, int descender, int numberOfHMetrics)
    {
        var b = new Buf();
        b.U32(0x00010000);                 // version
        b.I16(ascender).I16(descender);    // 4, 6
        b.I16(0);                          // lineGap
        b.U16(1000);                       // advanceWidthMax
        b.I16(0).I16(0).U16(0);            // min lsb / min rsb / xMaxExtent
        b.I16(1).I16(0).I16(0);            // caret slope/offset
        b.I16(0).I16(0).I16(0).I16(0);     // reserved
        b.I16(0);                          // metricDataFormat
        b.U16(numberOfHMetrics);           // 34
        return b.ToArray();
    }

    private static byte[] Maxp(int numGlyphs) => new Buf().U32(0x00010000).U16(numGlyphs).Pad(26).ToArray();

    /// <summary>hmtx with `metrics.Length` full (advance, lsb) pairs followed by
    /// `trailing` bare left-side bearings — the compression that makes every glyph
    /// past numberOfHMetrics reuse the final advance.</summary>
    private static byte[] Hmtx(int[] advances, int trailing)
    {
        var b = new Buf();
        foreach (int a in advances) b.U16(a).I16(0);
        for (int i = 0; i < trailing; i++) b.I16(0);
        return b.ToArray();
    }

    private static byte[] Post(float italicAngle, bool fixedPitch)
    {
        var b = new Buf();
        b.U32(0x00030000);                                  // version 3.0 (no glyph names)
        b.U32((long)(italicAngle * 65536f) & 0xFFFFFFFFL);  // 4: italicAngle, 16.16
        b.I16(-100).I16(50);                                // underline position/thickness
        b.U32(fixedPitch ? 1 : 0);                          // 12: isFixedPitch
        b.U32(0).U32(0).U32(0).U32(0);                      // mem usage
        return b.ToArray();
    }

    private static byte[] Os2(int version, int fsSelection, int xHeight, int capHeight)
    {
        var b = new Buf();
        b.U16(version);                  // 0
        b.Pad(60);                       // 2..62: metrics, panose, unicode ranges, vendor
        b.U16(fsSelection);              // 62
        b.U16(32).U16(0xFFFF);           // 64/66: usFirstCharIndex / usLastCharIndex
        b.I16(750).I16(-250).I16(0);     // 68/70/72: sTypoAscender / Descender / LineGap
        b.U16(800).U16(200);             // 74/76: usWinAscent / usWinDescent
        b.U32(1).U32(0);                 // 78/82: ulCodePageRange1/2
        b.I16(xHeight);                  // 86
        b.I16(capHeight);                // 88
        b.U16(0).U16(0).U16(0);          // 90/92/94: defaultChar / breakChar / maxContext
        return b.ToArray();
    }

    private static byte[] Loca(int[] offsets, bool shortFormat)
    {
        var b = new Buf();
        foreach (int o in offsets)
        {
            if (shortFormat) b.U16(o / 2); else b.U32(o);
        }
        return b.ToArray();
    }

    private static byte[] Name(params (int PlatformId, int EncodingId, int NameId, string Value)[] records)
    {
        var strings = new Buf();
        var offsets = new (int Offset, int Length)[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            var utf16 = System.Text.Encoding.BigEndianUnicode.GetBytes(records[i].Value);
            offsets[i] = (strings.Length, utf16.Length);
            strings.Bytes(utf16);
        }

        var b = new Buf();
        b.U16(0).U16(records.Length).U16(6 + records.Length * 12);
        for (int i = 0; i < records.Length; i++)
        {
            b.U16(records[i].PlatformId).U16(records[i].EncodingId).U16(0)
             .U16(records[i].NameId).U16(offsets[i].Length).U16(offsets[i].Offset);
        }
        b.Bytes(strings.ToArray());
        return b.ToArray();
    }

    // ---- cmap subtable builders -----------------------------------------

    /// <summary>cmap table wrapping one subtable under the given platform/encoding.</summary>
    private static byte[] Cmap(params (int PlatformId, int EncodingId, byte[] Subtable)[] subtables)
    {
        var b = new Buf();
        b.U16(0).U16(subtables.Length);
        int offset = 4 + subtables.Length * 8;
        foreach (var s in subtables)
        {
            b.U16(s.PlatformId).U16(s.EncodingId).U32(offset);
            offset += s.Subtable.Length;
        }
        foreach (var s in subtables) b.Bytes(s.Subtable);
        return b.ToArray();
    }

    private static byte[] Format0(byte[] glyphIds)
    {
        var b = new Buf();
        b.U16(0).U16(262).U16(0).Bytes(glyphIds);
        return b.ToArray();
    }

    /// <summary>A format 4 subtable. Segments with a null glyphIds run use the
    /// idDelta formula; segments carrying one use idRangeOffset indirection into the
    /// shared glyphIdArray.</summary>
    private static byte[] Format4((int Start, int End, int Delta, ushort[]? Glyphs)[] segments)
    {
        int segCount = segments.Length;
        var b = new Buf();
        b.U16(4).U16(0).U16(0); // format, length placeholder, language
        b.U16(segCount * 2).U16(0).U16(0).U16(0);

        foreach (var s in segments) b.U16(s.End);
        b.U16(0); // reserved pad
        foreach (var s in segments) b.U16(s.Start);
        foreach (var s in segments) b.I16(s.Delta);

        // idRangeOffset[i] is a byte distance measured from its own slot, so it has to
        // account for every later slot plus the glyph runs already queued ahead of it.
        int glyphsBefore = 0;
        for (int i = 0; i < segCount; i++)
        {
            if (segments[i].Glyphs is null) { b.U16(0); continue; }
            int slotsAfter = segCount - i;      // remaining idRangeOffset entries incl. this one
            b.U16((slotsAfter + glyphsBefore - 1) * 2 + 2);
            glyphsBefore += segments[i].Glyphs!.Length;
        }

        foreach (var s in segments)
            if (s.Glyphs is not null)
                foreach (ushort g in s.Glyphs) b.U16(g);

        var bytes = b.ToArray();
        bytes[2] = (byte)(bytes.Length >> 8);
        bytes[3] = (byte)bytes.Length;
        return bytes;
    }

    private static byte[] Format6(int firstCode, ushort[] gids)
    {
        var b = new Buf();
        b.U16(6).U16(10 + gids.Length * 2).U16(0).U16(firstCode).U16(gids.Length);
        foreach (ushort g in gids) b.U16(g);
        return b.ToArray();
    }

    private static byte[] Format12((uint Start, uint End, uint StartGid)[] groups)
    {
        var b = new Buf();
        b.U16(12).U16(0).U32(16 + groups.Length * 12).U32(0).U32(groups.Length);
        foreach (var g in groups) b.U32(g.Start).U32(g.End).U32(g.StartGid);
        return b.ToArray();
    }

    private static byte[] MinimalFont(byte[] cmapTable) => BuildFont(
        ("head", Head()),
        ("hhea", Hhea(800, -200, 1)),
        ("maxp", Maxp(10)),
        ("hmtx", Hmtx(new[] { 500 }, 9)),
        ("cmap", cmapTable));

    // =====================================================================
    // Table directory
    // =====================================================================

    [Fact]
    public void TableDirectoryLocatesEveryTable()
    {
        byte[] font = BuildFont(
            ("head", Head()),
            ("hhea", Hhea(800, -200, 3)),
            ("maxp", Maxp(5)),
            ("hmtx", Hmtx(new[] { 600, 700, 800 }, 2)),
            ("post", Post(0f, false)));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.True(f!.HasTable("head"));
        Assert.True(f.HasTable("hhea"));
        Assert.True(f.HasTable("maxp"));
        Assert.True(f.HasTable("hmtx"));
        Assert.True(f.HasTable("post"));
        Assert.False(f.HasTable("cmap"));

        Assert.True(f.TryGetTable("maxp", out int offset, out int length));
        Assert.Equal(32, length);
        // The record must actually point at the maxp payload we wrote.
        Assert.Equal(0x00, font[offset]);
        Assert.Equal(0x01, font[offset + 1]);
        Assert.Equal(5, font[offset + 5]);
    }

    [Fact]
    public void ParseRejectsNonSfntVersion()
    {
        byte[] font = BuildFont(("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)));
        font[0] = 0xDE; font[1] = 0xAD;
        Assert.Null(OxTrueTypeFont.Parse(font));
    }

    [Fact]
    public void ParseRejectsFontMissingRequiredTables()
    {
        // hhea and maxp present, head absent: ttf-parser fails the face, so do we.
        Assert.Null(OxTrueTypeFont.Parse(BuildFont(("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)))));
        Assert.Null(OxTrueTypeFont.Parse(BuildFont(("head", Head()), ("maxp", Maxp(2)))));
        Assert.Null(OxTrueTypeFont.Parse(BuildFont(("head", Head()), ("hhea", Hhea(800, -200, 1)))));
    }

    [Fact]
    public void ParseRejectsOutOfRangeUnitsPerEm()
    {
        foreach (int upem in new[] { 0, 15, 16385 })
        {
            byte[] font = BuildFont(("head", Head(upem)), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)));
            Assert.Null(OxTrueTypeFont.Parse(font));
        }
    }

    // =====================================================================
    // Metrics
    // =====================================================================

    [Fact]
    public void MetricsComeFromHeadHheaOs2AndPost()
    {
        byte[] font = BuildFont(
            ("head", Head(unitsPerEm: 2048)),
            ("hhea", Hhea(1900, -500, 1)),
            ("maxp", Maxp(4)),
            ("hmtx", Hmtx(new[] { 1024 }, 3)),
            ("post", Post(-12.5f, fixedPitch: true)),
            ("OS/2", Os2(version: 4, fsSelection: 0x0021, xHeight: 1000, capHeight: 1400)),
            ("name", Name((3, 1, 6, "Test-PostScript"), (3, 1, 1, "Test Family"))));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.Equal(2048, f!.UnitsPerEm);
        Assert.Equal(1900, f.Ascender);
        Assert.Equal(-500, f.Descender);
        Assert.Equal((short)1400, f.CapHeight);
        Assert.Equal((short)1000, f.XHeight);
        Assert.Equal(-12.5f, f.ItalicAngle);
        Assert.True(f.IsBold);
        Assert.True(f.IsItalic);
        Assert.True(f.IsMonospaced);
        Assert.Equal((short)-50, f.BBox.XMin);
        Assert.Equal((short)800, f.BBox.YMax);
        Assert.Equal("Test-PostScript", f.PostScriptName);
        Assert.Equal("Test Family", f.FamilyName);

        // FixedPitch | Nonsymbolic | Italic
        Assert.Equal(1u | (1u << 5) | (1u << 6), f.FontFlags());
        Assert.Equal((short)140, f.StemV);
    }

    [Fact]
    public void UseTypoMetricsOverridesHhea()
    {
        byte[] plain = BuildFont(
            ("head", Head()), ("hhea", Hhea(900, -300, 1)), ("maxp", Maxp(2)),
            ("OS/2", Os2(version: 4, fsSelection: 0x0000, xHeight: 500, capHeight: 700)));
        byte[] typo = BuildFont(
            ("head", Head()), ("hhea", Hhea(900, -300, 1)), ("maxp", Maxp(2)),
            ("OS/2", Os2(version: 4, fsSelection: 0x0080, xHeight: 500, capHeight: 700)));

        Assert.Equal((short)900, OxTrueTypeFont.Parse(plain)!.Ascender);
        Assert.Equal((short)750, OxTrueTypeFont.Parse(typo)!.Ascender);
        Assert.Equal((short)-250, OxTrueTypeFont.Parse(typo)!.Descender);
    }

    [Fact]
    public void Os2Version0HasNoCapOrXHeight()
    {
        byte[] font = BuildFont(
            ("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)),
            ("OS/2", Os2(version: 0, fsSelection: 0, xHeight: 500, capHeight: 700)));

        var f = OxTrueTypeFont.Parse(font);
        Assert.Null(f!.CapHeight);
        Assert.Null(f.XHeight);

        // FontMetrics substitutes the ascender and half of it.
        var m = OxFontMetrics.FromFont(f);
        Assert.Equal((short)800, m.CapHeight);
        Assert.Equal((short)400, m.XHeight);
        Assert.Equal("Unknown", m.Name);
        Assert.Equal("Unknown", m.Family);
    }

    [Fact]
    public void WithoutOs2StyleFallsBackToHeadMacStyle()
    {
        // Subset fonts in Office exports routinely drop OS/2. ttf-parser reports
        // not-bold/not-italic for those, so IsBold/IsItalic do too — head.macStyle is
        // exposed separately for callers willing to trust the looser signal.
        byte[] font = BuildFont(
            ("head", Head(macStyle: 0x0003)), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.False(f!.IsBold);
        Assert.False(f.IsItalic);
        Assert.Equal(0x0003, f.HeadMacStyle);
        Assert.Equal((short)80, f.StemV);
    }

    [Fact]
    public void FontMetricsScalesToPdfUnits()
    {
        byte[] font = BuildFont(
            ("head", Head(unitsPerEm: 2048)), ("hhea", Hhea(1638, -410, 1)), ("maxp", Maxp(2)));

        var m = OxFontMetrics.FromFont(OxTrueTypeFont.Parse(font)!);
        Assert.Equal(1638 * 1000 / 2048, m.PdfAscender);
        Assert.Equal(-410 * 1000 / 2048, m.PdfDescender);
        Assert.Equal(500 * 1000 / 2048, m.ToPdfUnits(500));
        var (xMin, _, xMax, _) = m.PdfBBox;
        Assert.True(xMax > xMin);
    }

    // =====================================================================
    // hmtx advance widths
    // =====================================================================

    [Fact]
    public void AdvanceWidthsIncludingTrailingRun()
    {
        // 6 glyphs but only 3 full metrics: glyphs 3..5 reuse the last advance.
        byte[] font = BuildFont(
            ("head", Head(unitsPerEm: 1000)),
            ("hhea", Hhea(800, -200, numberOfHMetrics: 3)),
            ("maxp", Maxp(6)),
            ("hmtx", Hmtx(new[] { 600, 250, 900 }, trailing: 3)));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.Equal((ushort)6, f!.NumGlyphs);
        Assert.Equal((ushort)3, f.NumberOfHMetrics);

        Assert.Equal((ushort)600, f.GlyphHorAdvance(0));
        Assert.Equal((ushort)250, f.GlyphHorAdvance(1));
        Assert.Equal((ushort)900, f.GlyphHorAdvance(2));
        Assert.Equal((ushort)900, f.GlyphHorAdvance(3));
        Assert.Equal((ushort)900, f.GlyphHorAdvance(5));
        Assert.Null(f.GlyphHorAdvance(6));

        // unitsPerEm 1000 means font units are already 1/1000 em.
        Assert.Equal((ushort)600, f.GlyphWidth(0));
        Assert.Equal((ushort)900, f.GlyphWidth(4));
        Assert.Equal((ushort)500, f.GlyphWidth(999)); // unknown glyph default
    }

    [Fact]
    public void AdvanceWidthsScaleByUnitsPerEm()
    {
        byte[] font = BuildFont(
            ("head", Head(unitsPerEm: 2048)),
            ("hhea", Hhea(1600, -400, numberOfHMetrics: 2)),
            ("maxp", Maxp(2)),
            ("hmtx", Hmtx(new[] { 2048, 1024 }, trailing: 0)));

        var f = OxTrueTypeFont.Parse(font);
        Assert.Equal((ushort)1000, f!.GlyphWidth(0));
        Assert.Equal((ushort)500, f.GlyphWidth(1));
    }

    [Fact]
    public void TruncatedHmtxYieldsNoAdvanceRatherThanThrowing()
    {
        // numberOfHMetrics claims 3 pairs but the table only holds one.
        byte[] font = BuildFont(
            ("head", Head()),
            ("hhea", Hhea(800, -200, numberOfHMetrics: 3)),
            ("maxp", Maxp(4)),
            ("hmtx", Hmtx(new[] { 600 }, trailing: 0)));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.Equal((ushort)600, f!.GlyphHorAdvance(0));
        Assert.Null(f.GlyphHorAdvance(1));
        Assert.Equal((ushort)0, f.GlyphWidth(1)); // missing advance counts as zero
    }

    // =====================================================================
    // loca
    // =====================================================================

    [Fact]
    public void LocaShortAndLongFormatsGiveTheSameRanges()
    {
        int[] offsets = { 0, 32, 32, 96 }; // glyph 1 is blank (start == end)

        byte[] shortFont = BuildFont(
            ("head", Head(indexToLocFormat: 0)), ("hhea", Hhea(800, -200, 1)),
            ("maxp", Maxp(3)), ("loca", Loca(offsets, shortFormat: true)));
        byte[] longFont = BuildFont(
            ("head", Head(indexToLocFormat: 1)), ("hhea", Hhea(800, -200, 1)),
            ("maxp", Maxp(3)), ("loca", Loca(offsets, shortFormat: false)));

        foreach (byte[] font in new[] { shortFont, longFont })
        {
            var f = OxTrueTypeFont.Parse(font);
            Assert.NotNull(f);
            Assert.True(f!.TryGetGlyphRange(0, out uint s0, out uint e0));
            Assert.Equal(0u, s0);
            Assert.Equal(32u, e0);
            Assert.True(f.TryGetGlyphRange(1, out uint s1, out uint e1));
            Assert.Equal(s1, e1); // blank glyph
            Assert.True(f.TryGetGlyphRange(2, out uint s2, out uint e2));
            Assert.Equal(32u, s2);
            Assert.Equal(96u, e2);
            Assert.False(f.TryGetGlyphRange(3, out _, out _));
        }
    }

    [Fact]
    public void MissingOrTruncatedLocaIsNotFatal()
    {
        var noLoca = OxTrueTypeFont.Parse(BuildFont(
            ("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(3))));
        Assert.NotNull(noLoca);
        Assert.False(noLoca!.TryGetGlyphRange(0, out _, out _));

        // 3 glyphs need 4 entries; supply 2.
        var shortLoca = OxTrueTypeFont.Parse(BuildFont(
            ("head", Head(indexToLocFormat: 0)), ("hhea", Hhea(800, -200, 1)),
            ("maxp", Maxp(3)), ("loca", Loca(new[] { 0, 32 }, shortFormat: true))));
        Assert.NotNull(shortLoca);
        Assert.True(shortLoca!.TryGetGlyphRange(0, out _, out _));
        Assert.False(shortLoca.TryGetGlyphRange(1, out _, out _));
    }

    // =====================================================================
    // cmap format 4
    // =====================================================================

    [Fact]
    public void CmapFormat4DeltaSegments()
    {
        // 'A'..'C' -> 3..5, 'a' -> 40, plus the mandatory 0xFFFF sentinel.
        byte[] font = MinimalFont(Cmap((3, 1, Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x43, 3 - 0x41, null),
            (0x61, 0x61, 40 - 0x61, null),
            (0xFFFF, 0xFFFF, 1, null),
        }))));

        var cmap = OxTrueTypeCMap.FromFontData(font);
        Assert.NotNull(cmap);
        Assert.Equal('A', cmap!.GetUnicode(3));
        Assert.Equal('B', cmap.GetUnicode(4));
        Assert.Equal('C', cmap.GetUnicode(5));
        Assert.Equal('a', cmap.GetUnicode(40));
        Assert.Null(cmap.GetUnicode(6));
        Assert.Equal(4, cmap.Count);
        Assert.False(cmap.IsEmpty);

        Assert.Equal((ushort)3, cmap.UnicodeToGid('A'));
        Assert.Equal("B", cmap.GetUnicodeString(4));
    }

    [Fact]
    public void CmapFormat4IdRangeOffsetIndirection()
    {
        // The middle segment resolves through glyphIdArray instead of idDelta, and one
        // of its entries is 0 — a hole the spec says must stay unmapped.
        byte[] font = MinimalFont(Cmap((3, 1, Format4(new (int, int, int, ushort[]?)[]
        {
            (0x20, 0x20, 1 - 0x20, null),
            (0x41, 0x44, 0, new ushort[] { 70, 0, 72, 73 }),
            (0xFFFF, 0xFFFF, 1, null),
        }))));

        var cmap = OxTrueTypeCMap.FromFontData(font);
        Assert.NotNull(cmap);
        Assert.Equal(' ', cmap!.GetUnicode(1));
        Assert.Equal('A', cmap.GetUnicode(70));
        Assert.Equal('C', cmap.GetUnicode(72));
        Assert.Equal('D', cmap.GetUnicode(73));
        Assert.Null(cmap.GetUnicode(71));  // the glyphIdArray hole
        Assert.Null(cmap.UnicodeToGid('B'));
    }

    [Fact]
    public void CmapFormat4IdRangeOffsetAppliesIdDelta()
    {
        // Non-zero idDelta on an indirect segment must be added to the array value.
        byte[] font = MinimalFont(Cmap((3, 1, Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x42, 5, new ushort[] { 100, 200 }),
            (0xFFFF, 0xFFFF, 1, null),
        }))));

        var cmap = OxTrueTypeCMap.FromFontData(font);
        Assert.NotNull(cmap);
        Assert.Equal('A', cmap!.GetUnicode(105));
        Assert.Equal('B', cmap.GetUnicode(205));
    }

    [Fact]
    public void CmapFormat4WildIdRangeOffsetMapsNothing()
    {
        // An idRangeOffset pointing far past the font would underflow/overflow the
        // index arithmetic; it has to come out as "unmapped", not as an exception.
        // 2 segments: header 14 + endCode 4 + pad 2 + startCode 4 + idDelta 4 = 28.
        const int IdRangeOffsetSlot0 = 28;

        // Far too large: the index lands past the end of the blob.
        var wild = Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x42, 0, new ushort[] { 10, 11 }),
            (0xFFFF, 0xFFFF, 1, null),
        });
        wild[IdRangeOffsetSlot0] = 0xFF;
        wild[IdRangeOffsetSlot0 + 1] = 0xF0;

        var cmap = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 1, wild))));
        Assert.NotNull(cmap);
        Assert.Null(cmap!.UnicodeToGid('A'));
        Assert.Null(cmap.UnicodeToGid('B'));

        // Too small: the spec formula underflows, which Rust's usize arithmetic wraps
        // into an unreachable index. It has to stay unmapped here too.
        var underflow = Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x42, 0, new ushort[] { 10, 11 }),
            (0xFFFF, 0xFFFF, 1, null),
        });
        underflow[IdRangeOffsetSlot0] = 0x00;
        underflow[IdRangeOffsetSlot0 + 1] = 0x02;

        var cmap2 = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 1, underflow))));
        Assert.NotNull(cmap2);
        Assert.Null(cmap2!.UnicodeToGid('A'));
    }

    // =====================================================================
    // cmap formats 0, 6, 12
    // =====================================================================

    [Fact]
    public void CmapFormat0MapsBytesThroughMacRoman()
    {
        var glyphIds = new byte[256];
        glyphIds[0x41] = 10;  // 'A'
        glyphIds[0x8A] = 20;  // Mac Roman a-umlaut -> U+00E4
        glyphIds[0xA5] = 30;  // Mac Roman bullet   -> U+2022
        glyphIds[0x42] = 0;   // explicit .notdef, must not produce a mapping

        var cmap = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((1, 0, Format0(glyphIds)))));
        Assert.NotNull(cmap);
        Assert.Equal('A', cmap!.GetUnicode(10));
        Assert.Equal('ä', cmap.GetUnicode(20));
        Assert.Equal('•', cmap.GetUnicode(30));
        Assert.Equal(3, cmap.Count);

        // (1,0) also feeds the symbolic byte -> GID hop.
        Assert.Equal((ushort)10, cmap.CodeToGid(0x41));
        Assert.Equal((ushort)20, cmap.CodeToGid(0x8A));
        Assert.Null(cmap.CodeToGid(0x42));
    }

    [Fact]
    public void CmapFormat6TrimmedRun()
    {
        var cmap = OxTrueTypeCMap.FromFontData(
            MinimalFont(Cmap((3, 1, Format6(0x30, new ushort[] { 17, 18, 19 })))));

        Assert.NotNull(cmap);
        Assert.Equal('0', cmap!.GetUnicode(17));
        Assert.Equal('1', cmap.GetUnicode(18));
        Assert.Equal('2', cmap.GetUnicode(19));
        Assert.Equal(3, cmap.Count);
    }

    [Fact]
    public void CmapFormat12ReachesBeyondTheBmp()
    {
        var cmap = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 10, Format12(new[]
        {
            (0x41u, 0x43u, 1u),          // 'A'..'C'  -> 1..3
            (0x1F600u, 0x1F602u, 900u),  // emoji     -> 900..902
            (0x10FFFDu, 0x10FFFDu, 950u) // last usable plane-16 scalar
        })))));

        Assert.NotNull(cmap);
        Assert.Equal('A', cmap!.GetUnicode(1));
        Assert.Equal('C', cmap.GetUnicode(3));
        Assert.Equal(0x1F600, cmap.GetUnicode(900));
        Assert.Equal(0x1F602, cmap.GetUnicode(902));
        Assert.Equal(0x10FFFD, cmap.GetUnicode(950));
        Assert.Equal("\U0001F600", cmap.GetUnicodeString(900));
        Assert.Equal((ushort)901, cmap.UnicodeToGid(0x1F601));
    }

    [Fact]
    public void CmapFormat12SkipsSurrogateCodepoints()
    {
        // A group spanning the surrogate block: those codes are not scalar values, so
        // char::from_u32 drops them and so must this port.
        var cmap = OxTrueTypeCMap.FromFontData(
            MinimalFont(Cmap((3, 10, Format12(new[] { (0xD7FEu, 0xE001u, 1u) })))));

        Assert.NotNull(cmap);
        Assert.Equal(0xD7FE, cmap!.GetUnicode(1));
        Assert.Equal(0xD7FF, cmap.GetUnicode(2));
        Assert.Null(cmap.GetUnicode(3));      // U+D800
        Assert.Null(cmap.GetUnicode(0x802));  // U+DFFF
        Assert.Equal(0xE000, cmap.GetUnicode(0x803));
    }

    // =====================================================================
    // Subtable selection
    // =====================================================================

    [Fact]
    public void SubtableSelectionPrefersWindowsFullThenBmp()
    {
        byte[] mac = Format0(new byte[256]);
        byte[] bmp = Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x41, 100 - 0x41, null), (0xFFFF, 0xFFFF, 1, null),
        });
        byte[] full = Format12(new[] { (0x41u, 0x41u, 200u) });

        // (3,10) outranks (3,1) regardless of directory order.
        var a = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((1, 0, mac), (3, 1, bmp), (3, 10, full))));
        Assert.Equal((ushort)200, a!.UnicodeToGid('A'));

        var b = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 10, full), (3, 1, bmp))));
        Assert.Equal((ushort)200, b!.UnicodeToGid('A'));

        // Without (3,10), (3,1) wins.
        var c = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((1, 0, mac), (3, 1, bmp))));
        Assert.Equal((ushort)100, c!.UnicodeToGid('A'));
    }

    [Fact]
    public void SymbolSubtableResolvesThroughF000()
    {
        // A (3,0) symbol subtable maps content bytes at 0xF020..; CodeToGid has to
        // undo that PUA offset so decode can go byte -> GID.
        byte[] symbol = Format4(new (int, int, int, ushort[]?)[]
        {
            (0xF041, 0xF042, 7 - 0xF041, null),
            (0xFFFF, 0xFFFF, 1, null),
        });
        byte[] bmp = Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x41, 100 - 0x41, null), (0xFFFF, 0xFFFF, 1, null),
        });

        var cmap = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 0, symbol), (3, 1, bmp))));
        Assert.NotNull(cmap);
        Assert.Equal((ushort)7, cmap!.CodeToGid(0x41));
        Assert.Equal((ushort)8, cmap.CodeToGid(0x42));
        Assert.Null(cmap.CodeToGid(0x43));
        // The Unicode side still came from (3,1).
        Assert.Equal((ushort)100, cmap.UnicodeToGid('A'));
    }

    [Fact]
    public void NoSymbolSubtableLeavesCodeToGidEmpty()
    {
        var cmap = OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 1, Format4(
            new (int, int, int, ushort[]?)[] { (0x41, 0x41, 0, null), (0xFFFF, 0xFFFF, 1, null) })))));
        Assert.NotNull(cmap);
        Assert.Null(cmap!.CodeToGid(0x41));
    }

    [Fact]
    public void UnsupportedCmapFormatIsRejected()
    {
        byte[] format2 = new Buf().U16(2).U16(6).U16(0).ToArray();
        Assert.Null(OxTrueTypeCMap.FromFontData(MinimalFont(Cmap((3, 1, format2)))));
    }

    [Fact]
    public void FontWithoutCmapStillParses()
    {
        var f = OxTrueTypeFont.Parse(BuildFont(
            ("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)), ("hmtx", Hmtx(new[] { 500 }, 1))));
        Assert.NotNull(f);
        Assert.Null(f!.CMap);
        Assert.Null(f.GlyphId('A'));
        Assert.Equal((ushort)500, f.CharWidth('A'));
        Assert.Empty(f.SupportedCodepoints());
    }

    // =====================================================================
    // Font-level Unicode map
    // =====================================================================

    [Fact]
    public void FontGlyphIdUnionsAllUnicodeSubtables()
    {
        // A real pattern: the (3,10) subtable carries only the astral additions while
        // (3,1) still holds the BMP. The cmap module's single-best pick sees only
        // (3,10); the font's glyph lookup has to see both, or Latin text stops mapping.
        byte[] bmp = Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x41, 11 - 0x41, null), (0xFFFF, 0xFFFF, 1, null),
        });
        byte[] full = Format12(new[] { (0x1F600u, 0x1F600u, 77u) });

        byte[] font = BuildFont(
            ("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(100)),
            ("hmtx", Hmtx(new[] { 400 }, 99)),
            ("cmap", Cmap((3, 1, bmp), (3, 10, full))));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.Equal((ushort)11, f!.GlyphId('A'));
        Assert.Equal((ushort)77, f.GlyphId(0x1F600));

        Assert.Null(f.CMap!.UnicodeToGid('A'));            // (3,10) outranks (3,1) here
        Assert.Equal((ushort)77, f.CMap.UnicodeToGid(0x1F600));
    }

    [Fact]
    public void UnicodeMapCoversBmpAndTheEmojiRangeOnly()
    {
        byte[] font = BuildFont(
            ("head", Head(unitsPerEm: 1000)),
            ("hhea", Hhea(800, -200, 2)),
            ("maxp", Maxp(1000)),
            ("hmtx", Hmtx(new[] { 400, 700 }, 998)),
            ("cmap", Cmap((3, 10, Format12(new[]
            {
                (0x41u, 0x41u, 1u),           // 'A'          — BMP, kept
                (0x1F600u, 0x1F600u, 2u),     // emoji        — kept
                (0x20000u, 0x20000u, 3u),     // CJK ext. B   — outside both ranges
            })))));

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.Equal((ushort)1, f!.GlyphId('A'));
        Assert.Equal((ushort)2, f.GlyphId(0x1F600));
        Assert.Null(f.GlyphId(0x20000));

        Assert.Equal(new[] { 0x41, 0x1F600 }, f.SupportedCodepoints());
        Assert.Equal((ushort)700, f.CharWidth('A'));      // glyph 1 -> second hmtx entry
        Assert.Equal((ushort)700, f.CharWidth(0x1F600));  // glyph 2 -> trailing run
        Assert.Equal((ushort)500, f.CharWidth(0x20000));  // unmapped -> default
    }

    // =====================================================================
    // Malformed input
    // =====================================================================

    [Fact]
    public void EmptyAndNullInputReturnNull()
    {
        Assert.Null(OxTrueTypeFont.Parse(null));
        Assert.Null(OxTrueTypeFont.Parse(Array.Empty<byte>()));
        Assert.Null(OxTrueTypeCMap.FromFontData(null));
        Assert.Null(OxTrueTypeCMap.FromFontData(Array.Empty<byte>()));
    }

    [Fact]
    public void RandomBytesReturnNull()
    {
        Assert.Null(OxTrueTypeFont.Parse(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0 }));
        Assert.Null(OxTrueTypeFont.Parse(System.Text.Encoding.ASCII.GetBytes("not a font file")));
        Assert.Null(OxTrueTypeCMap.FromFontData(System.Text.Encoding.ASCII.GetBytes("not a font file")));
    }

    [Fact]
    public void TruncationAtEveryLengthReturnsNullAndNeverThrows()
    {
        byte[] font = BuildFont(
            ("head", Head()),
            ("hhea", Hhea(800, -200, 2)),
            ("maxp", Maxp(4)),
            ("hmtx", Hmtx(new[] { 500, 600 }, 2)),
            ("post", Post(0f, false)),
            ("OS/2", Os2(2, 0, 500, 700)),
            ("name", Name((3, 1, 6, "Trunc"))),
            ("loca", Loca(new[] { 0, 16, 16, 32, 48 }, true)),
            ("cmap", Cmap((3, 1, Format4(new (int, int, int, ushort[]?)[]
            {
                (0x41, 0x44, 0, new ushort[] { 1, 2, 3, 4 }),
                (0xFFFF, 0xFFFF, 1, null),
            })))));

        Assert.NotNull(OxTrueTypeFont.Parse(font));

        for (int len = 0; len < font.Length; len++)
        {
            byte[] cut = font[..len];
            // Neither entry point may throw, and a partial font must never claim to be
            // whole — the only shapes allowed are null or a font whose tables all fit.
            var f = OxTrueTypeFont.Parse(cut);
            if (f is not null)
            {
                for (ushort g = 0; g < f.NumGlyphs; g++)
                {
                    f.GlyphHorAdvance(g);
                    f.TryGetGlyphRange(g, out _, out _);
                }
                _ = f.PostScriptName;
                _ = f.FamilyName;
            }
            OxTrueTypeCMap.FromFontData(cut);
        }
    }

    [Fact]
    public void TableRecordPointingOutsideTheBlobIsDropped()
    {
        byte[] font = BuildFont(
            ("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2)), ("post", Post(0f, true)));

        // Find the post record and push its offset past the end.
        int dirStart = 12;
        for (int i = 0; i < 4; i++)
        {
            int rec = dirStart + i * 16;
            uint tag = ((uint)font[rec] << 24) | ((uint)font[rec + 1] << 16) | ((uint)font[rec + 2] << 8) | font[rec + 3];
            if (tag != Tag("post")) continue;
            font[rec + 8] = 0xFF; font[rec + 9] = 0xFF; font[rec + 10] = 0xFF; font[rec + 11] = 0xFF;
        }

        var f = OxTrueTypeFont.Parse(font);
        Assert.NotNull(f);
        Assert.False(f!.HasTable("post"));
        Assert.False(f.IsMonospaced); // post is gone, so the fixed-pitch flag is lost
        Assert.Equal(0f, f.ItalicAngle);
    }

    [Fact]
    public void CmapPointingPastTheEndReturnsNull()
    {
        byte[] font = MinimalFont(Cmap((3, 1, Format4(new (int, int, int, ushort[]?)[]
        {
            (0x41, 0x41, 0, null), (0xFFFF, 0xFFFF, 1, null),
        }))));

        int dirStart = 12;
        for (int i = 0; i < 5; i++)
        {
            int rec = dirStart + i * 16;
            uint tag = ((uint)font[rec] << 24) | ((uint)font[rec + 1] << 16) | ((uint)font[rec + 2] << 8) | font[rec + 3];
            if (tag != Tag("cmap")) continue;
            font[rec + 8] = 0x7F; font[rec + 9] = 0xFF; font[rec + 10] = 0xFF; font[rec + 11] = 0xF0;
        }

        Assert.Null(OxTrueTypeCMap.FromFontData(font));
        Assert.NotNull(OxTrueTypeFont.Parse(font)); // the font itself is still usable
    }

    // =====================================================================
    // PDF emission helpers
    // =====================================================================

    [Fact]
    public void WidthsArrayGroupsConsecutiveGlyphs()
    {
        byte[] font = BuildFont(
            ("head", Head(unitsPerEm: 1000)),
            ("hhea", Hhea(800, -200, 5)),
            ("maxp", Maxp(5)),
            ("hmtx", Hmtx(new[] { 100, 200, 300, 400, 500 }, 0)));

        var f = OxTrueTypeFont.Parse(font)!;
        Assert.Equal("[]", System.Text.Encoding.ASCII.GetString(f.GenerateWidthsArray(Array.Empty<ushort>())));
        Assert.Equal(
            "[1 [200 300]4 [500]]",
            System.Text.Encoding.ASCII.GetString(f.GenerateWidthsArray(new ushort[] { 2, 1, 4 })));
    }

    [Fact]
    public void ToUnicodeCMapEncodesBmpAndSurrogatePairs()
    {
        var f = OxTrueTypeFont.Parse(BuildFont(
            ("head", Head()), ("hhea", Hhea(800, -200, 1)), ("maxp", Maxp(2))))!;

        string cmap = f.GenerateToUnicodeCMap(new Dictionary<int, ushort> { [0x41] = 1, [0x1F600] = 5000 });
        Assert.Contains("begincmap", cmap);
        Assert.Contains("2 beginbfchar", cmap);
        Assert.Contains("<0001> <0041>", cmap);
        Assert.Contains("<1388> <D83DDE00>", cmap);
        Assert.Contains("endbfchar", cmap);
        Assert.Contains("endcmap", cmap);

        Assert.DoesNotContain("beginbfchar", f.GenerateToUnicodeCMap(new Dictionary<int, ushort>()));
    }
}
