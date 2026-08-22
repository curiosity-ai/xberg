using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the glyph-decoding half of the ported pdf_oxide text extractor
/// (pdf_oxide-0.3.77 src/extractors/text.rs lines 1858-2485): the byte-mode decision,
/// the character-code walk, the /ToUnicode fallback tiers, the U+FFFD preservation flag
/// and the quadrant snapping of a run's rotation.
/// </summary>
public sealed class OxTextDecodingTests : IDisposable
{
    public void Dispose() => OxTextDecoding.SetPreserveUnmappedGlyphs(false);

    // ---- test doubles ------------------------------------------------------------

    /// <summary>A parsed /ToUnicode CMap over an explicit table, with no code-width claim.</summary>
    private class StubCMap : IOxCMap
    {
        private readonly Dictionary<uint, string> _map;
        internal StubCMap(Dictionary<uint, string> map) => _map = map;

        public bool IsParsed => true;
        public int Count => _map.Count;
        public string? Lookup(uint code) => _map.TryGetValue(code, out string? s) ? s : null;
        public byte Wmode => 0;
    }

    /// <summary>The same, for a CMap whose codespace range declares its code width.</summary>
    private sealed class StubCMapWithWidth : StubCMap, IOxCMapCodeWidth
    {
        internal StubCMapWithWidth(Dictionary<uint, string> map, byte codeWidth) : base(map)
            => CodeWidth = codeWidth;

        public byte CodeWidth { get; }
    }

    private static OxFontInfo SimpleFont(Dictionary<byte, char>? encoding = null, IOxCMap? toUnicode = null) =>
        new()
        {
            BaseFont = "NotAStandardFont",
            Subtype = "Type1",
            Encoding = OxEncoding.Custom(encoding ?? new Dictionary<byte, char>()),
            ToUnicode = toUnicode,
        };

    private static OxFontInfo Type0Font(string encodingName, IOxCMap? toUnicode = null) =>
        new()
        {
            BaseFont = "NotAStandardFont",
            Subtype = "Type0",
            Encoding = OxEncoding.Standard(encodingName),
            ToUnicode = toUnicode,
        };

    private static List<(ushort Code, int Consumed)> Walk(byte[] bytes, OxFontInfo? font)
    {
        var codes = new List<(ushort, int)>();
        foreach ((ushort code, int consumed) in new OxTextCharIter(bytes, font)) codes.Add((code, consumed));
        return codes;
    }

    // ---- get_byte_mode / TextCharIter (text.rs:2292, 2347) ------------------------

    [Fact]
    public void OneByteAndTwoByteModesSplitTheSameBytesDifferently()
    {
        byte[] bytes = { 0x00, 0x41, 0x00, 0x42 };

        var simple = SimpleFont();
        Assert.Equal(OxByteMode.OneByte, OxTextDecoding.GetByteMode(simple));
        Assert.Equal(
            new List<(ushort, int)> { (0x00, 1), (0x41, 1), (0x00, 1), (0x42, 1) },
            Walk(bytes, simple));

        var cid = Type0Font("Identity-H");
        Assert.Equal(OxByteMode.TwoByte, OxTextDecoding.GetByteMode(cid));
        Assert.Equal(new List<(ushort, int)> { (0x0041, 2), (0x0042, 2) }, Walk(bytes, cid));
    }

    [Fact]
    public void NoFontIsOneByte()
    {
        Assert.Equal(OxByteMode.OneByte, OxTextDecoding.GetByteMode(null));
        Assert.Equal(new List<(ushort, int)> { (0xC3, 1), (0xA9, 1) }, Walk(new byte[] { 0xC3, 0xA9 }, null));
    }

    [Fact]
    public void TwoByteModeReadsATrailingOddByteAlone()
    {
        // The last byte has no partner, so it is taken single-byte rather than reading past
        // the end of the string.
        Assert.Equal(
            new List<(ushort, int)> { (0x0041, 2), (0x42, 1) },
            Walk(new byte[] { 0x00, 0x41, 0x42 }, Type0Font("Identity-H")));
    }

    [Theory]
    [InlineData("Identity-H", nameof(OxByteMode.TwoByte))]
    [InlineData("Identity-V", nameof(OxByteMode.TwoByte))]
    [InlineData("OneByteIdentityH", nameof(OxByteMode.OneByte))]
    [InlineData("UniJIS-UCS2-H", nameof(OxByteMode.TwoByte))]
    [InlineData("UniGB-UTF16-H", nameof(OxByteMode.TwoByte))]
    [InlineData("H", nameof(OxByteMode.TwoByte))]
    [InlineData("V", nameof(OxByteMode.TwoByte))]
    [InlineData("90ms-RKSJ-H", nameof(OxByteMode.ShiftJIS))]
    [InlineData("GBpc-EUC-H", nameof(OxByteMode.TwoByte))]
    [InlineData("B5pc-H", nameof(OxByteMode.TwoByte))]
    [InlineData("KSCms-UHC-H", nameof(OxByteMode.TwoByte))]
    [InlineData("UniCNS-H", nameof(OxByteMode.TwoByte))]
    [InlineData("WeirdCustomCMap", nameof(OxByteMode.OneByte))]
    public void ByteModeFollowsTheEncodingName(string name, string expected)
    {
        // The mode is compared by name because xUnit's InlineData cannot carry an internal
        // enum through a public test method's signature.
        Assert.Equal(expected, OxTextDecoding.GetByteMode(Type0Font(name)).ToString());
    }

    [Fact]
    public void ShiftJisPairsOnlyLeadBytes()
    {
        // 0x82 is a lead byte, 0x41 is not.
        Assert.Equal(
            new List<(ushort, int)> { (0x82A0, 2), (0x41, 1) },
            Walk(new byte[] { 0x82, 0xA0, 0x41 }, Type0Font("90ms-RKSJ-H")));
    }

    [Fact]
    public void ToUnicodeCodespaceWidthOverridesAnUnrecognisedEncodingName()
    {
        // §9.7.5: begincodespacerange is authoritative. A CJK font whose /Encoding is a
        // custom CMap stream matches none of the name patterns, and reading it single-byte
        // turns the CJK into Latin garbage.
        var map = new Dictionary<uint, string> { [0x4E00] = "一" };
        Assert.Equal(
            OxByteMode.TwoByte,
            OxTextDecoding.GetByteMode(Type0Font("SomeEmbeddedCMap", new StubCMapWithWidth(map, 2))));

        // A CMap that reports a 1-byte codespace leaves the name rules in charge.
        Assert.Equal(
            OxByteMode.OneByte,
            OxTextDecoding.GetByteMode(Type0Font("SomeEmbeddedCMap", new StubCMapWithWidth(map, 1))));
    }

    // ---- decode_text_to_unicode (text.rs:2392) ------------------------------------

    [Fact]
    public void Type0IdentityCMapDecodesCidsAsCodePoints()
    {
        // Identity-H with no /CIDSystemInfo: producers routinely assign CID == code point,
        // so the 2-byte codes decode straight to characters.
        string text = OxTextDecoding.DecodeTextToUnicode(
            new byte[] { 0x00, 0x41, 0x00, 0x42, 0x4E, 0x00 }, Type0Font("Identity-H"));
        Assert.Equal("AB一", text);
    }

    [Fact]
    public void Type0IdentityCMapResolvesThroughToUnicodeFirst()
    {
        var font = Type0Font("Identity-H", new StubCMap(new Dictionary<uint, string>
        {
            [0x0003] = "H", [0x0004] = "i",
        }));
        Assert.Equal("Hi", OxTextDecoding.DecodeTextToUnicode(new byte[] { 0x00, 0x03, 0x00, 0x04 }, font));
    }

    [Fact]
    public void NoFontFallsBackToLatin1()
    {
        // §9.6.6: with no font at all, bytes 0x00-0xFF map straight onto U+0000-U+00FF.
        Assert.Equal("Aé", OxTextDecoding.DecodeTextToUnicode(new byte[] { 0x41, 0xE9 }, null));
    }

    [Fact]
    public void ControlCharactersFromAFailedResolutionAreDropped()
    {
        // Tab, LF and CR are legitimate whitespace; the rest of C0 is evidence of a broken
        // encoding, not text.
        var font = SimpleFont(new Dictionary<byte, char> { [0x41] = 'A' });
        Assert.Equal("A\t\n\r", OxTextDecoding.DecodeTextToUnicode(
            new byte[] { 0x41, 0x09, 0x0A, 0x0D, 0x01, 0x1F }, font));
    }

    [Fact]
    public void Utf8CMapFontSegmentsCodesByLeadByte()
    {
        // A Uni-Utf8-H font's codes are 1-4 bytes wide, so they exceed the 16 bits the
        // char iterator yields and are segmented here instead.
        var font = Type0Font("Uni-Utf8-H", new StubCMap(new Dictionary<uint, string>
        {
            [0x41] = "A",
            [0xE4B880] = "一",
            [0xF09F9880] = "\U0001F600",
        }));
        Assert.True(OxTextDecoding.FontHasUtf8CMap(font));

        byte[] bytes = { 0x41, 0xE4, 0xB8, 0x80, 0xF0, 0x9F, 0x98, 0x80 };
        Assert.Equal("A一\U0001F600", OxTextDecoding.DecodeTextToUnicode(bytes, font));
    }

    [Fact]
    public void Utf8CMapDetectionIsLimitedToType0StandardEncodings()
    {
        Assert.False(OxTextDecoding.FontHasUtf8CMap(Type0Font("Identity-H")));
        Assert.False(OxTextDecoding.FontHasUtf8CMap(SimpleFont()));
        Assert.True(OxTextDecoding.FontHasUtf8CMap(Type0Font("UniJIS-UTF8-H")));
    }

    // ---- the fallback tiers (text.rs:2122) ----------------------------------------

    [Fact]
    public void SimpleFontFallsThroughEncodingThenTheFallbackTable()
    {
        // 0x41 is in /Differences; 0x42 is in no mapping resource the font offers, so the
        // code itself is the last thing left to read it as.
        var font = SimpleFont(new Dictionary<byte, char> { [0x41] = 'X' });
        Assert.Equal("XB", OxTextDecoding.DecodeTextToUnicode(new byte[] { 0x41, 0x42 }, font));
    }

    [Theory]
    [InlineData(0x2014u, "—")] // tier 1: punctuation
    [InlineData(0x2026u, "…")]
    [InlineData(0x2211u, "∑")] // tier 2: mathematical operators
    [InlineData(0x21D4u, "⇔")]
    [InlineData(0x03B1u, "α")] // tier 3: Greek
    [InlineData(0x03A9u, "Ω")]
    [InlineData(0x20ACu, "€")] // tier 4: currency
    [InlineData(0x20B9u, "₹")]
    [InlineData(0x0041u, "A")] // tier 5: any other valid scalar
    [InlineData(0xE000u, "\uE000")] // private use area is still a valid scalar
    [InlineData(0x1F600u, "\U0001F600")] // supplementary planes included
    public void FallbackTiersMapACodeToItsCodePoint(uint code, string expected)
    {
        Assert.Equal(expected, OxTextDecoding.FallbackCharToUnicode(code));
    }

    [Theory]
    [InlineData(0xD800u)] // lone high surrogate
    [InlineData(0xDFFFu)] // lone low surrogate
    [InlineData(0x110000u)] // beyond U+10FFFF
    public void CodesThatAreNotScalarValuesFallToAQuestionMark(uint code)
    {
        Assert.Equal("?", OxTextDecoding.FallbackCharToUnicode(code));
    }

    // ---- the U+FFFD preservation flag (text.rs:28-66) ------------------------------

    [Fact]
    public void UnmappedGlyphsAreDroppedByDefaultAndKeptWhenTheFlagIsSet()
    {
        // A /ToUnicode that names a code but resolves it to the replacement character is a
        // genuinely unmapped glyph, which the historical default drops silently.
        var font = SimpleFont(
            new Dictionary<byte, char> { [0x41] = 'A' },
            new StubCMap(new Dictionary<uint, string> { [0x41] = "A", [0x42] = "�" }));

        byte[] bytes = { 0x41, 0x42, 0x41 };

        Assert.False(OxTextDecoding.PreserveUnmappedGlyphs);
        Assert.Equal("AA", OxTextDecoding.DecodeTextToUnicode(bytes, font));

        Assert.False(OxTextDecoding.SetPreserveUnmappedGlyphs(true));
        Assert.True(OxTextDecoding.PreserveUnmappedGlyphs);
        Assert.Equal("A�A", OxTextDecoding.DecodeTextToUnicode(bytes, font));

        Assert.True(OxTextDecoding.SetPreserveUnmappedGlyphs(false));
        Assert.Equal("AA", OxTextDecoding.DecodeTextToUnicode(bytes, font));
    }

    [Fact]
    public void TheFlagAlsoGatesTheSimpleFontFastPathInAppend()
    {
        var font = SimpleFont(
            new Dictionary<byte, char> { [0x41] = 'A' },
            new StubCMap(new Dictionary<uint, string> { [0x41] = "A", [0x42] = "�" }));

        Assert.Equal("AA", AppendedText(font, new byte[] { 0x41, 0x42, 0x41 }));

        OxTextDecoding.SetPreserveUnmappedGlyphs(true);
        Assert.Equal("A�A", AppendedText(font, new byte[] { 0x41, 0x42, 0x41 }));
    }

    // ---- TjBuffer::append (text.rs:1984) ------------------------------------------

    private static string AppendedText(OxFontInfo? font, byte[] bytes)
    {
        var buffer = OxTextDecoding.NewTjBuffer(new OxGraphicsState(), mcid: null, cachedFont: font);
        Assert.True(buffer.IsEmpty);
        buffer.Append(bytes);
        return buffer.Unicode.ToString();
    }

    [Fact]
    public void AppendUsesTheSimpleFontLookupTable()
    {
        var font = SimpleFont(new Dictionary<byte, char> { [0x41] = 'A', [0x42] = 'B' });
        Assert.Equal("AB", AppendedText(font, new byte[] { 0x41, 0x42 }));
    }

    [Fact]
    public void AppendRoutesType0FontsThroughTheFullDecode()
    {
        Assert.Equal("AB", AppendedText(Type0Font("Identity-H"), new byte[] { 0x00, 0x41, 0x00, 0x42 }));
    }

    [Fact]
    public void AppendReadsUtf8SmuggledIntoASimpleFontString()
    {
        // Some producers write UTF-8 into string literals for a font that declares only a
        // Latin encoding and no /ToUnicode; reading those bytes as single codes yields
        // mojibake, so a fully valid UTF-8 slice with a non-Latin-1 character wins.
        var font = SimpleFont(new Dictionary<byte, char>());
        Assert.Equal("Привет", AppendedText(font, Encoding.UTF8.GetBytes("Привет")));
    }

    [Fact]
    public void Utf8DetectionIgnoresPlainLatin1AndInvalidSequences()
    {
        var font = SimpleFont(new Dictionary<byte, char> { [0x41] = 'A', [0xE9] = 'é' });

        // Valid UTF-8 but every character is Latin-1, so the encoding stays in charge.
        Assert.Equal("A", AppendedText(font, new byte[] { 0x41 }));

        // 0xE9 alone is not valid UTF-8, so the byte goes through /Differences.
        Assert.Equal("Aé", AppendedText(font, new byte[] { 0x41, 0xE9 }));
    }

    [Fact]
    public void AppendTruncatesAtTheStringImplementationLimit()
    {
        var font = SimpleFont(new Dictionary<byte, char> { [0x41] = 'A' });
        byte[] bytes = Enumerable.Repeat((byte)0x41, 40_000).ToArray();
        Assert.Equal(32_767, AppendedText(font, bytes).Length);
    }

    [Fact]
    public void NewTjBufferCapturesTheTextStateItStartedUnder()
    {
        var state = new OxGraphicsState
        {
            Ctm = OxMatrix.Scaling(2.0f, 2.0f),
            TextMatrix = OxMatrix.Translation(10.0f, 20.0f),
            FontSize = 12.0f,
            TextRise = 3.0f,
            CharSpace = 0.5f,
            WordSpace = 1.5f,
            HorizontalScaling = 90.0f,
            FontName = "F1",
            RenderMode = 3,
            TextWMode = 1,
        };
        var font = new OxFontInfo { BaseFont = "Courier-BoldOblique", Subtype = "Type1" };

        var buffer = OxTextDecoding.NewTjBuffer(state, mcid: 7, cachedFont: font);

        Assert.True(buffer.IsEmpty);
        Assert.Equal(7, buffer.Mcid);
        Assert.Equal("F1", buffer.FontName);
        Assert.Equal(24.0f, buffer.EffectiveFontSize, 4);
        Assert.Equal(2.0f, buffer.UserHScale, 4);
        Assert.Equal(20.0f, buffer.UserPosX, 4);
        Assert.Equal(40.0f, buffer.UserPosY, 4);
        // Text rise is stored as a ratio of font size so it stays scale-independent.
        Assert.Equal(0.25f, buffer.TextRise, 4);
        Assert.Equal(0.0f, buffer.RotationDegrees);
        Assert.Equal(OxFontWeight.Bold, buffer.FontWeight);
        Assert.True(buffer.IsItalic);
        Assert.True(buffer.IsMonospace);
        Assert.Equal((byte)3, buffer.RenderMode);
        Assert.Equal((byte)1, buffer.Wmode);
    }

    [Fact]
    public void MonospaceComesFromTheFixedPitchFlagBeforeTheNameHeuristic()
    {
        var flagged = new OxFontInfo { BaseFont = "Helvetica", Subtype = "Type1", Flags = 1 };
        Assert.True(OxTextDecoding.NewTjBuffer(new OxGraphicsState(), null, flagged).IsMonospace);

        var proportional = new OxFontInfo { BaseFont = "Helvetica", Subtype = "Type1", Flags = 32 };
        Assert.False(OxTextDecoding.NewTjBuffer(new OxGraphicsState(), null, proportional).IsMonospace);

        var byName = new OxFontInfo { BaseFont = "DejaVuSansMono", Subtype = "Type1" };
        Assert.True(OxTextDecoding.NewTjBuffer(new OxGraphicsState(), null, byName).IsMonospace);
    }

    // ---- snap_run_rotation (text.rs:1928) -----------------------------------------

    private static OxMatrix Rotation(float degrees)
    {
        float r = degrees * MathF.PI / 180.0f;
        return new OxMatrix(MathF.Cos(r), MathF.Sin(r), -MathF.Sin(r), MathF.Cos(r), 0, 0);
    }

    /// <summary>A rotation composed with a horizontal flip, so the determinant is negative.</summary>
    private static OxMatrix MirroredRotation(float degrees)
    {
        float r = degrees * MathF.PI / 180.0f;
        return new OxMatrix(MathF.Cos(r), MathF.Sin(r), MathF.Sin(r), -MathF.Cos(r), 0, 0);
    }

    [Theory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(3.0f, 0.0f)]
    [InlineData(-4.9f, 0.0f)]
    [InlineData(90.0f, 90.0f)]
    [InlineData(87.0f, 90.0f)]
    [InlineData(93.0f, 90.0f)]
    [InlineData(-90.0f, -90.0f)]
    [InlineData(-92.0f, -90.0f)]
    [InlineData(179.0f, 180.0f)]
    [InlineData(177.0f, 180.0f)]
    public void RotationSnapsToTheNearestQuadrantWithinTolerance(float actual, float expected)
    {
        Assert.Equal(expected, OxTextDecoding.SnapRunRotation(Rotation(actual)), 3);
    }

    [Theory]
    [InlineData(7.0f)]
    [InlineData(45.0f)]
    [InlineData(83.0f)]
    [InlineData(97.0f)]
    [InlineData(-45.0f)]
    public void RotationFurtherThanToleranceKeepsItsRawAngle(float actual)
    {
        Assert.Equal(actual, OxTextDecoding.SnapRunRotation(Rotation(actual)), 3);
    }

    [Fact]
    public void HalfTurnsWithNoSkewTakeTheHorizontalFastPath()
    {
        // A clean 180 turn has b = c = 0, so it is indistinguishable from upright text by
        // the fast path — upstream reports 0, and reading order treats the run as horizontal.
        Assert.Equal(0.0f, OxTextDecoding.SnapRunRotation(new OxMatrix(-1, 0, 0, -1, 0, 0)));
        Assert.Equal(0.0f, OxTextDecoding.SnapRunRotation(OxMatrix.Identity));
        Assert.Equal(0.0f, OxTextDecoding.SnapRunRotation(OxMatrix.Scaling(3.0f, 3.0f)));
    }

    [Fact]
    public void MirroredTextKeepsItsRawAngleRatherThanSnapping()
    {
        // Snapping a mirror to a quadrant would claim it is a clean rotation; the
        // reading-order path already isolates any non-zero rotation into its own block.
        Assert.Equal(88.0f, OxTextDecoding.SnapRunRotation(MirroredRotation(88.0f)), 3);
        Assert.Equal(90.0f, OxTextDecoding.SnapRunRotation(Rotation(88.0f)), 3);

        // Within tolerance of horizontal a mirror still reports exactly 0.
        Assert.Equal(0.0f, OxTextDecoding.SnapRunRotation(MirroredRotation(3.0f)), 3);
    }
}
