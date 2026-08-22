// Tests for the embedded-program glyph names pdf_oxide reads through
// `ttf_parser::Face::glyph_name` (`FontInfo::embedded_glyph_name`, font_dict.rs:476): the
// `post` table first, then the `CFF ` charset. Fonts are assembled byte by byte here so the
// charset formats and the post versions are pinned rather than inferred from a real face.
using System;
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

public sealed class OxEmbeddedGlyphNamesTests
{
    private const uint SfntTrueType = 0x00010000;
    private const uint SfntOpenType = 0x4F54544F; // "OTTO"

    // Charset format 0: GIDs 1..3 carry SID 34 ("A"), SID 35 ("B") and SID 391 (the first
    // custom string). GID 0 is .notdef and is never listed.
    private static readonly byte[] CharsetFormat0 =
        { 0, 0x00, 0x22, 0x00, 0x23, 0x01, 0x87 };

    [Fact]
    public void TheCharsetNamesEveryGlyphOfASidKeyedCff()
    {
        byte[] font = BuildCff(CharsetFormat0, hasRos: false, "germandbls");

        string?[]? names = OxCffEncoding.GlyphNamesByGid(font);

        Assert.NotNull(names);
        Assert.Equal(4, names!.Length);
        Assert.Equal(".notdef", names[0]);
        Assert.Equal("A", names[1]);
        Assert.Equal("B", names[2]);
        Assert.Equal("germandbls", names[3]);
    }

    [Fact]
    public void CharsetFormat1RangesNameConsecutiveGlyphs()
    {
        // One range: first SID 34 ("A") with nLeft 2, so GIDs 1..3 are A, B, C.
        byte[] font = BuildCff(new byte[] { 1, 0x00, 0x22, 2 }, hasRos: false);

        string?[]? names = OxCffEncoding.GlyphNamesByGid(font);

        Assert.NotNull(names);
        Assert.Equal(new string?[] { ".notdef", "A", "B", "C" }, names);
    }

    [Fact]
    public void ACidKeyedCffNamesNothing()
    {
        // A /ROS makes the charset a CID list, so it spells out no glyph names at all.
        byte[] font = BuildCff(CharsetFormat0, hasRos: true, "germandbls");

        Assert.Null(OxCffEncoding.GlyphNamesByGid(font));
    }

    [Fact]
    public void ACharsetTooShortForTheGlyphCountNamesNothing()
    {
        // Format 0 with two SIDs where the CharStrings INDEX declares four glyphs.
        byte[] font = BuildCff(new byte[] { 0, 0x00, 0x22, 0x00, 0x23 }, hasRos: false);

        Assert.Null(OxCffEncoding.GlyphNamesByGid(font));
    }

    [Fact]
    public void AnOpenTypeWrappedCffReportsItsCharsetNames()
    {
        byte[] otf = WrapInSfnt(SfntOpenType, numGlyphs: 4,
            ("CFF ", BuildCff(CharsetFormat0, hasRos: false, "germandbls")));

        IReadOnlyList<string?>? names = OxEmbeddedGlyphNames.ForFontData(otf);

        Assert.NotNull(names);
        Assert.Equal(4, names!.Count);
        // `.notdef` counts as no name, so the entry stays empty.
        Assert.Null(names[0]);
        Assert.Equal("A", names[1]);
        Assert.Equal("B", names[2]);
        Assert.Equal("germandbls", names[3]);
    }

    [Fact]
    public void GlyphsPastTheCharsetHaveNoName()
    {
        // maxp claims more glyphs than the CFF CharStrings INDEX holds; the surplus is unnamed.
        byte[] otf = WrapInSfnt(SfntOpenType, numGlyphs: 6,
            ("CFF ", BuildCff(CharsetFormat0, hasRos: false, "germandbls")));

        IReadOnlyList<string?>? names = OxEmbeddedGlyphNames.ForFontData(otf);

        Assert.NotNull(names);
        Assert.Equal(6, names!.Count);
        Assert.Equal("germandbls", names[3]);
        Assert.Null(names[4]);
        Assert.Null(names[5]);
    }

    [Fact]
    public void PostFormatTwoNamesGlyphsFromBothTheStandardListAndItsOwnStrings()
    {
        // Index 16 is "hyphen" in the standard Macintosh order; 258 is the first own name.
        byte[] post = BuildPostFormat2(new ushort[] { 0, 16, 258 }, "widget");
        byte[] otf = WrapInSfnt(SfntTrueType, numGlyphs: 3, ("post", post));

        IReadOnlyList<string?>? names = OxEmbeddedGlyphNames.ForFontData(otf);

        Assert.NotNull(names);
        Assert.Null(names![0]);
        Assert.Equal("hyphen", names[1]);
        Assert.Equal("widget", names[2]);
    }

    [Fact]
    public void PostNamesWinOverTheCharset()
    {
        byte[] post = BuildPostFormat2(new ushort[] { 0, 258, 0, 0 }, "fromPost");
        byte[] otf = WrapInSfnt(SfntOpenType, numGlyphs: 4,
            ("CFF ", BuildCff(CharsetFormat0, hasRos: false, "germandbls")),
            ("post", post));

        IReadOnlyList<string?>? names = OxEmbeddedGlyphNames.ForFontData(otf);

        Assert.NotNull(names);
        Assert.Equal("fromPost", names![1]);
        // `post` spelling a glyph `.notdef` is an answer, not a reason to read the charset.
        Assert.Null(names[2]);
    }

    [Fact]
    public void APostTableWithoutNamesFallsThroughToTheCharset()
    {
        // Version 3.0 stores no names at all, which is the stripped-subset case.
        byte[] post = new byte[32];
        post[1] = 0x03;
        byte[] otf = WrapInSfnt(SfntOpenType, numGlyphs: 4,
            ("CFF ", BuildCff(CharsetFormat0, hasRos: false, "germandbls")),
            ("post", post));

        IReadOnlyList<string?>? names = OxEmbeddedGlyphNames.ForFontData(otf);

        Assert.NotNull(names);
        Assert.Equal("A", names![1]);
    }

    [Fact]
    public void AProgramCarryingNoNamesAtAllIsReportedAsUnnamed()
    {
        byte[] post = new byte[32];
        post[1] = 0x03;
        byte[] otf = WrapInSfnt(SfntTrueType, numGlyphs: 3, ("post", post));

        Assert.Null(OxEmbeddedGlyphNames.ForFontData(otf));
    }

    [Fact]
    public void DataThatIsNotAnSfntContainerCarriesNoNames()
    {
        // A bare CFF is what /FontFile3 ships; the loader wraps it before this point.
        Assert.Null(OxEmbeddedGlyphNames.ForFontData(BuildCff(CharsetFormat0, hasRos: false)));
    }

    // ---- builders ----------------------------------------------------------------

    /// <summary>
    /// A minimal CFF font program with four glyphs, the given charset, and optionally a /ROS
    /// to make it CID-keyed.
    /// </summary>
    private static byte[] BuildCff(byte[] charset, bool hasRos, params string[] strings)
    {
        byte[] header = { 1, 0, 4, 1 };
        byte[] nameIndex = MakeIndex(new[] { Encoding.ASCII.GetBytes("TestFont") });
        byte[] stringIndex = MakeIndex(Array.ConvertAll(strings, Encoding.ASCII.GetBytes));
        byte[] charStrings = MakeIndex(new byte[4][]
        {
            Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(),
        });

        // Two operators with a 3-byte operand each (charset, CharStrings) plus, when CID-keyed,
        // /ROS with its three operands and two-byte operator.
        int topDictLen = (2 * 4) + (hasRos ? (3 * 3) + 2 : 0);
        int topDictIndexLen = 2 + 1 + 2 + topDictLen;

        int charStringsOffset = header.Length + nameIndex.Length + topDictIndexLen + stringIndex.Length;
        int charsetOffset = charStringsOffset + charStrings.Length;

        var dict = new List<byte>();
        if (hasRos)
        {
            dict.AddRange(DictOperand(391));
            dict.AddRange(DictOperand(391));
            dict.AddRange(DictOperand(0));
            dict.Add(12);
            dict.Add(30);
        }
        dict.AddRange(DictOperand(charsetOffset));
        dict.Add(15);
        dict.AddRange(DictOperand(charStringsOffset));
        dict.Add(17);
        byte[] topDictIndex = MakeIndex(new[] { dict.ToArray() });
        Assert.Equal(topDictIndexLen, topDictIndex.Length);

        var font = new List<byte>();
        font.AddRange(header);
        font.AddRange(nameIndex);
        font.AddRange(topDictIndex);
        font.AddRange(stringIndex);
        font.AddRange(charStrings);
        font.AddRange(charset);
        return font.ToArray();
    }

    /// <summary>CFF DICT 3-byte integer operand (b0 = 28); the operator follows it.</summary>
    private static byte[] DictOperand(int value) =>
        new byte[] { 28, (byte)(value >> 8), (byte)value };

    private static byte[] MakeIndex(byte[][] entries)
    {
        var result = new List<byte> { (byte)(entries.Length >> 8), (byte)entries.Length };
        if (entries.Length == 0)
        {
            return result.ToArray();
        }
        result.Add(1); // offSize
        int offset = 1;
        result.Add((byte)offset);
        foreach (byte[] e in entries)
        {
            offset += e.Length;
            result.Add((byte)offset);
        }
        foreach (byte[] e in entries)
        {
            result.AddRange(e);
        }
        return result.ToArray();
    }

    private static byte[] BuildPostFormat2(ushort[] glyphIndexes, params string[] ownNames)
    {
        var post = new List<byte>(new byte[32]);
        post[1] = 0x02; // version 2.0
        post.Add((byte)(glyphIndexes.Length >> 8));
        post.Add((byte)glyphIndexes.Length);
        foreach (ushort i in glyphIndexes)
        {
            post.Add((byte)(i >> 8));
            post.Add((byte)i);
        }
        foreach (string name in ownNames)
        {
            post.Add((byte)name.Length);
            post.AddRange(Encoding.ASCII.GetBytes(name));
        }
        return post.ToArray();
    }

    /// <summary>
    /// An sfnt container holding the given tables plus the head / hhea / maxp trio a face needs
    /// before it parses at all.
    /// </summary>
    private static byte[] WrapInSfnt(uint version, int numGlyphs, params (string Tag, byte[] Data)[] tables)
    {
        var head = new byte[54];
        head[1] = 0x01;                               // majorVersion 1
        head[12] = 0x5F; head[13] = 0x0F; head[14] = 0x3C; head[15] = 0xF5; // magic
        head[18] = 0x03; head[19] = 0xE8;             // unitsPerEm 1000
        var hhea = new byte[36];
        hhea[1] = 0x01;
        var maxp = new byte[6];
        maxp[2] = 0x50;                               // version 0.5
        maxp[4] = (byte)(numGlyphs >> 8);
        maxp[5] = (byte)numGlyphs;

        var all = new List<(string Tag, byte[] Data)>(tables)
        {
            ("head", head), ("hhea", hhea), ("maxp", maxp),
        };
        all.Sort(static (a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        var outBytes = new List<byte>();
        void PutU16(int v) { outBytes.Add((byte)(v >> 8)); outBytes.Add((byte)v); }
        void PutU32(long v)
        {
            outBytes.Add((byte)(v >> 24)); outBytes.Add((byte)(v >> 16));
            outBytes.Add((byte)(v >> 8)); outBytes.Add((byte)v);
        }

        PutU32(version);
        PutU16(all.Count);
        PutU16(0); PutU16(0); PutU16(0);

        int offset = 12 + (all.Count * 16);
        var offsets = new List<int>();
        foreach ((_, byte[] data) in all)
        {
            offsets.Add(offset);
            offset += (data.Length + 3) & ~3;
        }
        for (int i = 0; i < all.Count; i++)
        {
            foreach (char c in all[i].Tag) outBytes.Add((byte)c);
            PutU32(0);
            PutU32(offsets[i]);
            PutU32(all[i].Data.Length);
        }
        for (int i = 0; i < all.Count; i++)
        {
            while (outBytes.Count < offsets[i]) outBytes.Add(0);
            outBytes.AddRange(all[i].Data);
        }
        return outBytes.ToArray();
    }
}
