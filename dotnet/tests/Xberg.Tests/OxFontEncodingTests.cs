using System.Text;
using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

public class OxFontEncodingTests
{
    // ==========================================================================
    // Adobe Glyph List + glyph-name fallback chain
    // ==========================================================================

    [Fact]
    public void AglHasEveryEntryFromTheRustTable()
    {
        Assert.Equal(4281, OxGlyphNames.AglCount);
    }

    [Theory]
    [InlineData("A", 0x0041)]
    [InlineData("bullet", 0x2022)]
    [InlineData("fi", 0xFB01)]
    [InlineData("ffl", 0xFB04)]
    [InlineData("endash", 0x2013)]
    [InlineData("germandbls", 0x00DF)]
    [InlineData("zukatakana", 0x30BA)] // last entry in the generated table
    public void AglExactLookups(string name, int expected)
    {
        Assert.True(OxGlyphNames.TryLookupAgl(name, out char ch));
        Assert.Equal(expected, ch);
    }

    [Theory]
    [InlineData("squaredot", 0x22A1)]
    [InlineData("dblarrowup", 0x21C8)]
    [InlineData("lfloor", 0x230A)]
    [InlineData("maltese", 0x2720)]
    [InlineData("bullet1", 0x2219)]
    public void TexMathNamesResolveOutsideTheAgl(string name, int expected)
    {
        Assert.False(OxGlyphNames.TryLookupAgl(name, out _));
        Assert.Equal(expected, OxGlyphNames.GlyphNameToUnicode(name)!.Value.Value);
    }

    [Fact]
    public void SyntheticUniAndUFormsResolve()
    {
        Assert.Equal(0x2022, OxGlyphNames.GlyphNameToUnicode("uni2022")!.Value.Value);
        // The u-form reaches beyond the BMP, which is why the surface is a Rune.
        Assert.Equal(0x1F600, OxGlyphNames.GlyphNameToUnicode("u1F600")!.Value.Value);
        Assert.Equal("\U0001F600", OxGlyphNames.GlyphNameToUnicodeString("u1F600"));
    }

    [Fact]
    public void VariantSuffixesAreStripped()
    {
        Assert.Equal('A', OxGlyphNames.GlyphNameToUnicode("A.sc")!.Value.Value);
        Assert.Equal(0x2022, OxGlyphNames.GlyphNameToUnicode("bullet.alt")!.Value.Value);
        Assert.Equal(0xFB01, OxGlyphNames.GlyphNameToUnicode("fi.001")!.Value.Value);
        // Unknown base plus a suffix stays unknown.
        Assert.Null(OxGlyphNames.GlyphNameToUnicode("xyzzy.sc"));
    }

    [Fact]
    public void UnderscoreCompoundsTakeTheFirstComponentOnTheScalarSurface()
    {
        Assert.Equal('f', OxGlyphNames.GlyphNameToUnicode("f_i")!.Value.Value);
        Assert.Equal('T', OxGlyphNames.GlyphNameToUnicode("T_h")!.Value.Value);
        // The string surface short-circuits on the scalar result, matching the Rust order.
        Assert.Equal("f", OxGlyphNames.GlyphNameToUnicodeString("f_i"));
        Assert.Null(OxGlyphNames.GlyphNameToUnicodeString("xyzzy_plugh"));
    }

    [Fact]
    public void UnderscoreCompoundExpandsWhenNoComponentIsAnAglName()
    {
        // Neither "uni0041_uni0042" nor its first component is in the AGL, so the scalar
        // lookup fails and the per-component expansion runs.
        Assert.Equal("AB", OxGlyphNames.GlyphNameToUnicodeString("uni0041_uni0042"));
    }

    [Fact]
    public void UnknownGlyphNamesResolveToNull()
    {
        Assert.Null(OxGlyphNames.GlyphNameToUnicode("totallyunknown"));
        Assert.Null(OxGlyphNames.GlyphNameToUnicodeString("totallyunknown"));
    }

    [Fact]
    public void UnifiedChainRejectsControlCharacterSynthetics()
    {
        // uni0007 is a control char; the unified chain refuses it even though the lenient
        // font_dict arms would have produced BEL.
        Assert.Null(OxGlyphNames.GlyphNameToUnicodeUnified("uni0007"));
    }

    // ==========================================================================
    // Type 1 built-in /Encoding scan
    // ==========================================================================

    [Fact]
    public void Type1EncodingParsesDupPutEntries()
    {
        byte[] font = Encoding.ASCII.GetBytes(
            "/Encoding 256 array\n" +
            "0 1 255 {1 index exch /.notdef put} for\n" +
            "dup 65 /A put\n" +
            "dup 66 /B put\n" +
            "dup 97 /a put\n" +
            "readonly def\n");

        Dictionary<byte, Rune>? map = OxType1Encoding.ParseType1Encoding(font);
        Assert.NotNull(map);
        Assert.Equal(new Rune('A'), map![65]);
        Assert.Equal(new Rune('B'), map[66]);
        Assert.Equal(new Rune('a'), map[97]);
    }

    [Fact]
    public void Type1EncodingResolvesLigatureGlyphNames()
    {
        byte[] font = Encoding.ASCII.GetBytes(
            "/Encoding 256 array\n" +
            "dup 11 /ff put\n" +
            "dup 12 /fi put\n" +
            "dup 14 /ffi put\n" +
            "readonly def\n");

        Dictionary<byte, Rune>? map = OxType1Encoding.ParseType1Encoding(font);
        Assert.NotNull(map);
        Assert.Equal(new Rune(0xFB00), map![11]);
        Assert.Equal(new Rune(0xFB01), map[12]);
        Assert.Equal(new Rune(0xFB03), map[14]);
    }

    [Fact]
    public void Type1CmrStyleFontProgramYieldsEveryMapping()
    {
        byte[] font = Encoding.ASCII.GetBytes(
            "%!PS-AdobeFont-1.0: CMR9 003.002\n" +
            "/Encoding 256 array\n" +
            "0 1 255 {1 index exch /.notdef put} for\n" +
            "dup 11 /ff put\ndup 12 /fi put\ndup 13 /fl put\n" +
            "dup 14 /ffi put\ndup 15 /ffl put\n" +
            "dup 65 /A put\ndup 97 /a put\ndup 48 /zero put\n" +
            "dup 58 /colon put\ndup 123 /endash put\n" +
            "readonly def\ncurrentdict end\ncurrentfile eexec\n");

        Dictionary<byte, Rune>? map = OxType1Encoding.ParseType1Encoding(font);
        Assert.NotNull(map);
        Assert.Equal(10, map!.Count);
        Assert.Equal(new Rune(0xFB04), map[15]);
        Assert.Equal(new Rune('0'), map[48]);
        Assert.Equal(new Rune(0x2013), map[123]);
    }

    [Fact]
    public void Type1PredefinedStandardEncodingIsNotACustomTable()
    {
        Assert.Null(OxType1Encoding.ParseType1Encoding(Encoding.ASCII.GetBytes("/Encoding StandardEncoding def\n")));
        Assert.Null(OxType1Encoding.ParseType1Encoding(Encoding.ASCII.GetBytes("no encoding here")));
    }

    // ==========================================================================
    // CFF INDEX / DICT / charset / encoding parsing on hand-built font programs
    // ==========================================================================

    /// <summary>
    /// Assemble a minimal CFF font program. The charset is written last so that a charset
    /// parse whose nGlyphs guess overshoots simply runs out of data, as it does on real fonts.
    /// </summary>
    private static byte[] BuildCff(byte[] charset, byte[]? encoding, params string[] strings)
    {
        byte[] header = { 1, 0, 4, 1 };
        byte[] nameIndex = MakeIndex(new[] { Encoding.ASCII.GetBytes("TestFont") });
        byte[] stringIndex = MakeIndex(Array.ConvertAll(strings, Encoding.ASCII.GetBytes));
        byte[] charStrings = MakeIndex(new[] { Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>() });

        int opCount = encoding is null ? 2 : 3;
        int topDictLen = opCount * 4;
        int topDictIndexLen = 2 + 1 + 2 + topDictLen;

        int charStringsOffset = header.Length + nameIndex.Length + topDictIndexLen + stringIndex.Length;
        int encodingOffset = charStringsOffset + charStrings.Length;
        int charsetOffset = encodingOffset + (encoding?.Length ?? 0);

        var dict = new List<byte>();
        dict.AddRange(DictInt(charsetOffset, 15));
        if (encoding is not null)
        {
            dict.AddRange(DictInt(encodingOffset, 16));
        }
        dict.AddRange(DictInt(charStringsOffset, 17));
        byte[] topDictIndex = MakeIndex(new[] { dict.ToArray() });
        Assert.Equal(topDictIndexLen, topDictIndex.Length);

        var font = new List<byte>();
        font.AddRange(header);
        font.AddRange(nameIndex);
        font.AddRange(topDictIndex);
        font.AddRange(stringIndex);
        font.AddRange(charStrings);
        if (encoding is not null)
        {
            font.AddRange(encoding);
        }
        font.AddRange(charset);
        return font.ToArray();
    }

    /// <summary>CFF DICT 3-byte integer operand (b0 = 28) followed by a 1-byte operator.</summary>
    private static byte[] DictInt(int value, byte op) =>
        new[] { (byte)28, (byte)(value >> 8), (byte)(value & 0xFF), op };

    private static byte[] MakeIndex(byte[][] entries)
    {
        if (entries.Length == 0)
        {
            return new byte[] { 0, 0 };
        }
        var bytes = new List<byte> { (byte)(entries.Length >> 8), (byte)(entries.Length & 0xFF), 1 };
        int running = 1;
        bytes.Add((byte)running);
        foreach (byte[] e in entries)
        {
            running += e.Length;
            bytes.Add((byte)running);
        }
        foreach (byte[] e in entries)
        {
            bytes.AddRange(e);
        }
        return bytes.ToArray();
    }

    // Charset format 0: SIDs for GID 1..n. 34 = "A", 35 = "B", 391 = first String INDEX entry.
    private static readonly byte[] CharsetFormat0 = { 0, 0x00, 0x22, 0x00, 0x23, 0x01, 0x87 };

    // Encoding format 0: three codes, assigned GID 1, 2, 3 in order.
    private static readonly byte[] EncodingFormat0 = { 0, 3, 0x41, 0x42, 0x43 };

    [Fact]
    public void CffCustomEncodingResolvesCodeToUnicodeThroughTheCharset()
    {
        byte[] font = BuildCff(CharsetFormat0, EncodingFormat0, "germandbls");

        Dictionary<byte, Rune>? map = OxCffEncoding.ParseCffEncoding(font);
        Assert.NotNull(map);
        Assert.Equal(3, map!.Count);
        Assert.Equal(new Rune('A'), map[0x41]);
        Assert.Equal(new Rune('B'), map[0x42]);
        // SID 391 is the first custom string, resolved through the String INDEX.
        Assert.Equal(new Rune(0x00DF), map[0x43]);
    }

    [Fact]
    public void CffCustomEncodingGivesByteToGidDirectly()
    {
        byte[] font = BuildCff(CharsetFormat0, EncodingFormat0, "germandbls");

        Dictionary<byte, ushort>? gids = OxCffEncoding.ParseCffGidMapping(font);
        Assert.NotNull(gids);
        Assert.Equal(3, gids!.Count);
        Assert.Equal<int>(1, gids[0x41]);
        Assert.Equal<int>(2, gids[0x42]);
        Assert.Equal<int>(3, gids[0x43]);
    }

    [Fact]
    public void CffCharsetFormat1RangesExpand()
    {
        // Format 1: one range starting at SID 34 ("A") with nLeft = 2 -> A, B, C.
        byte[] charset = { 1, 0x00, 0x22, 2 };
        byte[] font = BuildCff(charset, EncodingFormat0);

        Dictionary<byte, Rune>? map = OxCffEncoding.ParseCffEncoding(font);
        Assert.NotNull(map);
        Assert.Equal(new Rune('A'), map![0x41]);
        Assert.Equal(new Rune('B'), map[0x42]);
        Assert.Equal(new Rune('C'), map[0x43]);
    }

    [Fact]
    public void CffStandardEncodingWithCustomCharsetFallsBackToGidKeyedMap()
    {
        // No Encoding operator in the Top DICT -> encoding offset 0 (StandardEncoding).
        byte[] font = BuildCff(CharsetFormat0, null, "germandbls");

        Dictionary<byte, Rune>? map = OxCffEncoding.ParseCffEncoding(font);
        Assert.NotNull(map);
        // Subset fonts in the wild use character codes that equal GIDs here.
        Assert.Equal(new Rune('A'), map![1]);
        Assert.Equal(new Rune('B'), map[2]);
        Assert.Equal(new Rune(0x00DF), map[3]);

        Dictionary<byte, ushort>? gids = OxCffEncoding.ParseCffGidMapping(font);
        Assert.NotNull(gids);
        Assert.Equal<int>(1, gids![0x41]); // byte 'A' -> SID 34 -> GID 1
        Assert.Equal<int>(2, gids[0x42]);
        Assert.False(gids.ContainsKey(0x43)); // 'C' is not in this subset's charset
    }

    [Fact]
    public void PdfEncodingDrivesByteToGidAgainstTheCharset()
    {
        // The CFF Encoding only lists 0x41-0x43, but §9.6.6 says the PDF /Encoding is
        // authoritative: 0xDF (WinAnsi germandbls) must resolve even though the font's own
        // Encoding never mentions it.
        byte[] font = BuildCff(CharsetFormat0, EncodingFormat0, "germandbls");

        Dictionary<byte, ushort>? gids = OxCffEncoding.ParseCffGidMappingWithPdfEncoding(
            font,
            OxPdfEncoding.Standard("WinAnsiEncoding"),
            new Dictionary<byte, string>());

        Assert.NotNull(gids);
        Assert.Equal<int>(1, gids![0x41]);
        Assert.Equal<int>(2, gids[0x42]);
        Assert.Equal<int>(3, gids[0xDF]);
        Assert.False(gids.ContainsKey(0x43));
    }

    [Fact]
    public void DifferencesOverrideTheBasePdfEncoding()
    {
        byte[] font = BuildCff(CharsetFormat0, EncodingFormat0, "germandbls");

        Dictionary<byte, ushort>? gids = OxCffEncoding.ParseCffGidMappingWithPdfEncoding(
            font,
            OxPdfEncoding.Standard("WinAnsiEncoding"),
            new Dictionary<byte, string> { [0x43] = "germandbls" });

        Assert.NotNull(gids);
        Assert.Equal<int>(3, gids![0x43]);
    }

    [Fact]
    public void IdentityPdfEncodingFallsThroughToTheCffEncodingPath()
    {
        byte[] font = BuildCff(CharsetFormat0, EncodingFormat0, "germandbls");

        Dictionary<byte, ushort>? gids = OxCffEncoding.ParseCffGidMappingWithPdfEncoding(
            font, OxPdfEncoding.Identity, new Dictionary<byte, string>());

        Assert.NotNull(gids);
        Assert.Equal<int>(3, gids![0x43]); // the CFF Encoding's own byte -> GID
    }

    [Fact]
    public void CffInsideAnOpenTypeWrapperIsUnwrapped()
    {
        byte[] cff = BuildCff(CharsetFormat0, EncodingFormat0, "germandbls");

        // Minimal OTTO sfnt with a single "CFF " table entry.
        var sfnt = new List<byte> { 0x4F, 0x54, 0x54, 0x4F, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        int tableOffset = 12 + 16;
        sfnt.AddRange(new byte[] { 0x43, 0x46, 0x46, 0x20 }); // "CFF "
        sfnt.AddRange(new byte[] { 0, 0, 0, 0 });             // checksum
        sfnt.AddRange(BeU32(tableOffset));
        sfnt.AddRange(BeU32(cff.Length));
        sfnt.AddRange(cff);

        Dictionary<byte, Rune>? map = OxCffEncoding.ParseCffEncoding(sfnt.ToArray());
        Assert.NotNull(map);
        Assert.Equal(new Rune('A'), map![0x41]);
    }

    private static byte[] BeU32(int v) =>
        new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    [Fact]
    public void CffSidTableCoversThePredefinedStrings()
    {
        Assert.Equal(".notdef", OxCffEncoding.SidToName(0));
        Assert.Equal("space", OxCffEncoding.SidToName(1));
        Assert.Equal("A", OxCffEncoding.SidToName(34));
        Assert.Equal("z", OxCffEncoding.SidToName(91));
        Assert.Equal("ffl", OxCffEncoding.SidToName(268));
        Assert.Equal("Semibold", OxCffEncoding.SidToName(390));
        Assert.Null(OxCffEncoding.SidToName(391));
        Assert.Equal<ushort?>(34, OxCffEncoding.GlyphNameToSid("A"));
        Assert.Null(OxCffEncoding.GlyphNameToSid("germandbls_not_a_sid"));
    }

    [Fact]
    public void MacRomanAndStandardByteNamesDivergeAboveAscii()
    {
        // Same byte, three different glyph names — this is why the base table matters.
        Assert.Equal("registered", OxCffEncoding.MacRomanByteToName(0xA8));
        Assert.Equal("currency", OxCffEncoding.StandardEncodingByteToName(0xA8));
        Assert.Equal("dieresis", OxEncodingTables.GidToStandardGlyphName(0xA8));
        // The Apple-logo PUA glyph has no portable name.
        Assert.Null(OxCffEncoding.MacRomanByteToName(0xF0));
        // StandardEncoding leaves 0xB0 unassigned.
        Assert.Null(OxCffEncoding.StandardEncodingByteToName(0xB0));
    }

    // ==========================================================================
    // Named encoding tables
    // ==========================================================================

    [Theory]
    [InlineData("WinAnsiEncoding", 0x92, 0x2019)]
    [InlineData("WinAnsiEncoding", 0x80, 0x20AC)]
    [InlineData("WinAnsiEncoding", 0xE9, 0x00E9)]
    [InlineData("StandardEncoding", 0x27, 0x2019)] // quoteright, not apostrophe
    [InlineData("StandardEncoding", 0xA4, 0x2044)] // fraction, not currency
    [InlineData("StandardEncoding", 0xAE, 0xFB01)] // fi ligature, not registered
    [InlineData("MacRomanEncoding", 0xD5, 0x2019)]
    [InlineData("MacRomanEncoding", 0xCA, 0x00A0)]
    [InlineData("MacRomanEncoding", 0xBD, 0x2126)]
    [InlineData("PDFDocEncoding", 0x80, 0x2022)]
    [InlineData("PDFDocEncoding", 0x8A, 0x2212)] // minus, distinct from hyphen
    public void StandardEncodingLookupsMatchAnnexD(string encoding, int code, int expected)
    {
        string? s = OxEncodingTables.StandardEncodingLookup(encoding, (byte)code);
        Assert.NotNull(s);
        Assert.Equal(expected, char.ConvertToUtf32(s!, 0));
    }

    [Fact]
    public void StandardEncodingLookupHoles()
    {
        Assert.Null(OxEncodingTables.StandardEncodingLookup("WinAnsiEncoding", 0x81));
        Assert.Null(OxEncodingTables.StandardEncodingLookup("StandardEncoding", 0xB0));
        Assert.Null(OxEncodingTables.StandardEncodingLookup("PDFDocEncoding", 0x9F));
        // Unknown encoding names fall back to identity over printable ASCII only.
        Assert.Equal("A", OxEncodingTables.StandardEncodingLookup("NoSuchEncoding", 0x41));
        Assert.Null(OxEncodingTables.StandardEncodingLookup("NoSuchEncoding", 0xE9));
    }

    [Fact]
    public void SymbolEncodingRecoversGreekAndMathOperators()
    {
        Assert.Equal<char?>((char)0x03C1, OxEncodingTables.SymbolEncodingLookup(0x72)); // rho
        Assert.Equal<char?>((char)0x0391, OxEncodingTables.SymbolEncodingLookup(0x41)); // Alpha
        Assert.Equal<char?>((char)0x222B, OxEncodingTables.SymbolEncodingLookup(0xF2)); // integral
        Assert.Equal<char?>((char)0x2211, OxEncodingTables.SymbolEncodingLookup(0xE1)); // summation
        Assert.Equal<char?>('5', OxEncodingTables.SymbolEncodingLookup(0x35));    // digits are identity
        Assert.Null(OxEncodingTables.SymbolEncodingLookup(0x00));
    }

    [Fact]
    public void ZapfDingbatsCoversOrnamentsAndTheCircledDigitRuns()
    {
        Assert.Equal<char?>((char)0x2713, OxEncodingTables.ZapfDingbatsEncodingLookup(0x33)); // check mark
        Assert.Equal<char?>((char)0x2460, OxEncodingTables.ZapfDingbatsEncodingLookup(0xAC)); // circled 1
        Assert.Equal<char?>((char)0x2469, OxEncodingTables.ZapfDingbatsEncodingLookup(0xB5)); // circled 10
        Assert.Equal<char?>((char)0x2192, OxEncodingTables.ZapfDingbatsEncodingLookup(0xD5)); // rightwards arrow
        Assert.Equal<char?>((char)0x27BE, OxEncodingTables.ZapfDingbatsEncodingLookup(0xFE));
        Assert.Null(OxEncodingTables.ZapfDingbatsEncodingLookup(0xF0)); // gap between the arrow runs
    }

    [Fact]
    public void BuiltinEncodingCipherDetection()
    {
        // A real encoding agrees with its named base on most shared codes.
        var real = new Dictionary<byte, Rune>
        {
            [0x41] = new Rune('A'),
            [0x42] = new Rune('B'),
            [0x43] = new Rune('C'),
            [0xCA] = new Rune(' '), // one non-standard slot
        };
        Assert.False(OxEncodingTables.BuiltinEncodingLooksLikeCipher(real, "WinAnsiEncoding"));

        // A subset cipher agrees with almost nothing.
        var cipher = new Dictionary<byte, Rune>
        {
            [0x41] = new Rune('t'),
            [0x42] = new Rune('h'),
            [0x43] = new Rune('e'),
            [0x44] = new Rune('q'),
        };
        Assert.True(OxEncodingTables.BuiltinEncodingLooksLikeCipher(cipher, "WinAnsiEncoding"));

        // Empty overlap is not evidence of a cipher.
        Assert.False(OxEncodingTables.BuiltinEncodingLooksLikeCipher(
            new Dictionary<byte, Rune>(), "WinAnsiEncoding"));
    }

    [Fact]
    public void MathAlphanumericSymbolsCollapseToTheirBase()
    {
        Assert.Equal((uint)'x', OxEncodingTables.MathAlphanumericBase(0x1D465));
        Assert.Equal((uint)'A', OxEncodingTables.MathAlphanumericBase(0x1D434));
        Assert.Equal((uint)'h', OxEncodingTables.MathAlphanumericBase(0x1D455)); // reserved hole
        Assert.Equal(0x03B2u, OxEncodingTables.MathAlphanumericBase(0x1D6FD));   // beta
        Assert.Equal(0x0391u, OxEncodingTables.MathAlphanumericBase(0x1D6E2));   // Alpha
        Assert.Equal((uint)'0', OxEncodingTables.MathAlphanumericBase(0x1D7CE));
        Assert.Equal((uint)'9', OxEncodingTables.MathAlphanumericBase(0x1D7FF));
        Assert.Null(OxEncodingTables.MathAlphanumericBase('A'));
        Assert.Null(OxEncodingTables.MathAlphanumericBase(0x1D800));
    }

    [Fact]
    public void WinAnsiRoundTrip()
    {
        Assert.Equal<byte?>((byte)0x41, OxEncodingTables.UnicodeToWinAnsi(0x41));
        Assert.Equal<byte?>((byte)0x80, OxEncodingTables.UnicodeToWinAnsi(0x20AC));
        Assert.Equal<byte?>((byte)0x92, OxEncodingTables.UnicodeToWinAnsi(0x2019));
        Assert.Null(OxEncodingTables.UnicodeToWinAnsi(0x10000));
        Assert.True(OxEncodingTables.IsWinAnsiChar(new Rune('A')));
        Assert.False(OxEncodingTables.IsWinAnsiChar(new Rune(0x4E2D)));
    }

    // ==========================================================================
    // UnicodeEncoder
    // ==========================================================================

    [Fact]
    public void IdentityHEncodingCachesGlyphLookups()
    {
        var encoder = new OxUnicodeEncoder();
        Func<uint, ushort?> lookup = cp => cp switch { 0x41 => (ushort)1, 0x42 => (ushort)2, _ => null };

        Assert.Equal("<00010002>", encoder.EncodeIdentityH("AB", lookup));
        Assert.Equal(2, encoder.CacheSize);
        // Missing glyphs fall back to .notdef and are not cached.
        Assert.Equal("<0000>", encoder.EncodeIdentityH("Z", lookup));
        Assert.Equal(2, encoder.CacheSize);
        encoder.ClearCache();
        Assert.Equal(0, encoder.CacheSize);
    }

    [Fact]
    public void LiteralAndHexStringEncoding()
    {
        Assert.Equal("(Hello)", OxUnicodeEncoder.EncodeLiteral("Hello"));
        Assert.Equal("(\\(test\\))", OxUnicodeEncoder.EncodeLiteral("(test)"));
        Assert.Equal("(back\\\\slash)", OxUnicodeEncoder.EncodeLiteral("back\\slash"));
        Assert.Equal("(\\351)", OxUnicodeEncoder.EncodeLiteral("é"));
        Assert.Equal("(?)", OxUnicodeEncoder.EncodeLiteral("中"));
        Assert.Equal("<414243>", OxEncodingTables.EncodeBytesAsHex("ABC"u8));
        Assert.Equal("(\\(\\))", OxEncodingTables.EncodeBytesAsLiteral(new byte[] { 0x28, 0x29 }));
        Assert.Equal("(\\177)", OxEncodingTables.EncodeBytesAsLiteral(new byte[] { 0x7F }));
    }

    [Fact]
    public void Utf16BeEncodingUsesSurrogatePairs()
    {
        Assert.Equal("<FEFF0041>", OxUnicodeEncoder.EncodeUtf16Be("A"));
        Assert.Equal("<FEFF20AC>", OxUnicodeEncoder.EncodeUtf16Be("€"));
        Assert.Equal("<FEFFD83DDE00>", OxUnicodeEncoder.EncodeUtf16Be("\U0001F600"));
        Assert.Equal("(Hello)", OxUnicodeEncoder.EncodeText("Hello"));
        Assert.StartsWith("<FEFF", OxUnicodeEncoder.EncodeText("Hello€World"));
    }

    // ==========================================================================
    // CharacterMapper priority chain
    // ==========================================================================

    private sealed class StubCMap : IOxToUnicodeLookup
    {
        private readonly Dictionary<uint, string> _map;

        internal StubCMap(Dictionary<uint, string> map) => _map = map;

        public string? Get(uint code) => _map.TryGetValue(code, out string? v) ? v : null;
    }

    [Fact]
    public void ToUnicodeCMapWinsAndItsMissesAreReplacementChars()
    {
        var mapper = new OxCharacterMapper();
        mapper.SetToUnicodeCMap(new StubCMap(new Dictionary<uint, string> { [0x01] = "Q" }));

        Assert.Equal("Q", mapper.MapCharacter(0x01));
        // A present CMap is authoritative: a miss must not fall through to AGL.
        Assert.Equal("�", mapper.MapCharacter(0x41));
    }

    [Fact]
    public void AdobeGlyphListIsThePriorityTwoFallback()
    {
        var mapper = new OxCharacterMapper();
        Assert.Equal("A", mapper.MapCharacter(0x41));
        Assert.Equal(" ", mapper.MapCharacter(0x20));
        Assert.Equal("’", mapper.MapCharacter(0x27)); // quoteright
        Assert.Equal("—", mapper.MapCharacter(0x97)); // WinAnsi emdash
        Assert.Equal("é", mapper.MapCharacter(0xE9));
    }

    [Fact]
    public void IdentityOrderingTreatsTheCidAsACodepoint()
    {
        var mapper = new OxCharacterMapper();
        mapper.SetPredefinedCMap(new OxPredefinedCMapConfig("Identity"));

        Assert.Equal("中", mapper.MapCharacter(0x4E2D));
        // Above the BMP the identity rule does not apply, and nothing else matches.
        Assert.Equal("�", mapper.MapCharacter(0x10000));
    }

    [Fact]
    public void FontEncodingIsTheLastResortBeforeReplacementChar()
    {
        var mapper = new OxCharacterMapper();
        mapper.SetFontEncoding(new Dictionary<uint, Rune> { [0x01] = new Rune(0x263A) });

        Assert.Equal("☺", mapper.MapCharacter(0x01));
        Assert.Equal("�", mapper.MapCharacter(0x02));
    }

    [Fact]
    public void ExtendedCodeToGlyphNameFollowsWinAnsi()
    {
        var mapper = new OxCharacterMapper();
        Assert.Equal("Euro", mapper.CodeToGlyphNameExtended(0x80));
        Assert.Equal("emdash", mapper.CodeToGlyphNameExtended(0x97));
        Assert.Null(mapper.CodeToGlyphNameExtended(0x81));
        Assert.Equal("germandbls", mapper.CodeToGlyphNameExtended(0xDF));
    }
}
