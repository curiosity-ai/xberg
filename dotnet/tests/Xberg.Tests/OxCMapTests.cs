// Tests for the pdf_oxide `fonts/cmap.rs`, `fonts/predefined_cidfont.rs` and
// `fonts/cid_mappings/*` ports. The bfchar/bfrange cases mirror the Rust `mod tests`;
// the rest cover the fixtures the port has to survive in the wild.
using System.Text;
using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

public class OxCMapTests
{
    private static OxCMap Parse(string src) => OxCMap.ParseToUnicodeCMap(Encoding.UTF8.GetBytes(src));

    [Fact]
    public void BfCharSingleAndMultiple()
    {
        OxCMap cmap = Parse("beginbfchar\n<0041> <0041>\n<0042> <0042>\n<0043> <0043>\nendbfchar");
        Assert.Equal("A", cmap.Get(0x41));
        Assert.Equal("B", cmap.Get(0x42));
        Assert.Equal("C", cmap.Get(0x43));
        Assert.Null(cmap.Get(0x44));
    }

    [Fact]
    public void BfCharNonAscii()
    {
        Assert.Equal("\u00E9", Parse("beginbfchar\n<00E9> <00E9>\nendbfchar").Get(0xE9));
    }

    [Fact]
    public void BfCharLigatureAndEscapeSequence()
    {
        OxCMap cmap = Parse("beginbfchar\n<01> <00660066006C>\n<02> <space>\n<03> <tab>\nendbfchar");
        Assert.Equal("ffl", cmap.Get(1));
        Assert.Equal(" ", cmap.Get(2));
        Assert.Equal("\t", cmap.Get(3));
    }

    [Fact]
    public void BfRangeSequential()
    {
        OxCMap cmap = Parse("beginbfrange\n<0041> <0043> <0041>\nendbfrange");
        Assert.Equal("A", cmap.Get(0x41));
        Assert.Equal("B", cmap.Get(0x42));
        Assert.Equal("C", cmap.Get(0x43));
        Assert.Null(cmap.Get(0x44));
    }

    [Fact]
    public void BfRangeWithArrayDestination()
    {
        // <005F> <0061> [<00660066> <00660069> <00660066006C>] -> "ff", "fi", "ffl".
        OxCMap cmap = Parse("beginbfrange\n<005F> <0061> [<00660066> <00660069> <00660066006C>]\nendbfrange");
        Assert.Equal("ff", cmap.Get(0x5F));
        Assert.Equal("fi", cmap.Get(0x60));
        Assert.Equal("ffl", cmap.Get(0x61));
        Assert.Null(cmap.Get(0x62));
    }

    [Fact]
    public void BfRangeArrayLongerThanRangeIsTruncated()
    {
        // Over-sized arrays are lenient per upstream: extras past (hi - lo + 1) are dropped.
        OxCMap cmap = Parse("beginbfrange\n<0010> <0011> [<0041> <0042> <0043>]\nendbfrange");
        Assert.Equal("A", cmap.Get(0x10));
        Assert.Equal("B", cmap.Get(0x11));
        Assert.Null(cmap.Get(0x12));
    }

    [Fact]
    public void BfRangeDestinationIncrementsAcrossByteBoundary()
    {
        // Codes 0x00F0..0x0110 and destinations U+00F0..U+0110 both cross a byte boundary; the
        // increment is on the code point, not on any per-byte field.
        OxCMap cmap = Parse("beginbfrange\n<00F0> <0110> <00F0>\nendbfrange");
        Assert.Equal("\u00F0", cmap.Get(0x00F0));
        Assert.Equal("\u00FF", cmap.Get(0x00FF));
        Assert.Equal("\u0100", cmap.Get(0x0100));
        Assert.Equal("\u0110", cmap.Get(0x0110));
        Assert.Null(cmap.Get(0x0111));
    }

    [Fact]
    public void BfCharSurrogatePairDecodesToSupplementaryPlane()
    {
        // D835 DF0C is U+1D70C MATHEMATICAL ITALIC SMALL RHO.
        OxCMap cmap = Parse("beginbfchar\n<0003> <D835DF0C>\nendbfchar");
        Assert.Equal(char.ConvertFromUtf32(0x1D70C), cmap.Get(3));
    }

    [Fact]
    public void BfRangeSurrogateDestinationIncrementsOnCodePoint()
    {
        // Naively incrementing the raw 0xD835DF0C would walk the low surrogate past 0xDFFF;
        // the base must be decoded to U+1D70C first.
        OxCMap cmap = Parse("beginbfrange\n<0010> <0012> <D835DF0C>\nendbfrange");
        Assert.Equal(char.ConvertFromUtf32(0x1D70C), cmap.Get(0x10));
        Assert.Equal(char.ConvertFromUtf32(0x1D70D), cmap.Get(0x11));
        Assert.Equal(char.ConvertFromUtf32(0x1D70E), cmap.Get(0x12));
    }

    [Fact]
    public void BfCharDirectSupplementaryCodePoint()
    {
        Assert.Equal(char.ConvertFromUtf32(0x20BB7), Parse("beginbfchar\n<0004> <020BB7>\nendbfchar").Get(4));
    }

    [Fact]
    public void TwoByteCodespaceSetsCodeWidth()
    {
        OxCMap wide = Parse("begincodespacerange\n<0000> <FFFF>\nendcodespacerange\nbeginbfchar\n<0041> <0041>\nendbfchar");
        Assert.Equal((byte)2, wide.CodeWidth);

        OxCMap narrow = Parse("begincodespacerange\n<00> <FF>\nendcodespacerange\nbeginbfchar\n<41> <0041>\nendbfchar");
        Assert.Equal((byte)1, narrow.CodeWidth);

        // Mixed codespaces take the widest declared entry.
        OxCMap mixed = Parse("begincodespacerange\n<00> <80>\n<8140> <9FFC>\nendcodespacerange");
        Assert.Equal((byte)2, mixed.CodeWidth);
    }

    [Fact]
    public void WModeDirectiveIsRead()
    {
        Assert.Equal((byte)1, Parse("/WMode 1 def\nbeginbfchar\n<41> <0041>\nendbfchar").WMode);
        Assert.Equal((byte)0, Parse("/WMode 0 def\nbeginbfchar\n<41> <0041>\nendbfchar").WMode);
        // Commented-out directives must not flip the writing mode.
        Assert.Equal((byte)0, Parse("% /WMode 1 def\nbeginbfchar\n<41> <0041>\nendbfchar").WMode);
        // Non-spec values fall back to horizontal.
        Assert.Equal((byte)0, Parse("/WMode 2 def\nbeginbfchar\n<41> <0041>\nendbfchar").WMode);
    }

    [Fact]
    public void LargeBfRangeCompressesAndStillResolves()
    {
        OxCMap cmap = Parse("beginbfrange\n<0100> <0300> <0500>\nendbfrange");
        Assert.True(cmap.RangeCount > 0);
        Assert.Equal(0, cmap.CharCount);
        Assert.Equal("\u0500", cmap.Get(0x100));
        Assert.Equal("\u0700", cmap.Get(0x300));
        Assert.Null(cmap.Get(0x0FF));
        Assert.Null(cmap.Get(0x301));
    }

    [Fact]
    public void LaterBfCharOverridesEarlierBfRange()
    {
        // §9.10.3 last-wins in document order; the override must survive range compression.
        OxCMap cmap = Parse("beginbfrange\n<0100> <0300> <0500>\nendbfrange\nbeginbfchar\n<0200> <0041>\nendbfchar");
        Assert.Equal("A", cmap.Get(0x200));
        Assert.Equal("\u05FF", cmap.Get(0x1FF));
        Assert.Equal("\u0601", cmap.Get(0x201));
    }

    [Fact]
    public void NotdefRangeOnlyFillsUnmappedCodes()
    {
        OxCMap cmap = Parse(
            "beginbfchar\n<0005> <0041>\nendbfchar\n" +
            "beginnotdefrange\n<0000> <0010> <FFFD>\nendnotdefrange");
        Assert.Equal("A", cmap.Get(5));
        Assert.Equal("\uFFFD", cmap.Get(6));
        Assert.Null(cmap.Get(0x11));
    }

    [Fact]
    public void EmptyStreamYieldsEmptyCMap()
    {
        OxCMap cmap = OxCMap.ParseToUnicodeCMap([]);
        Assert.True(cmap.IsEmpty);
        Assert.Equal((byte)1, cmap.CodeWidth);
        Assert.Equal((byte)0, cmap.WMode);
    }

    [Fact]
    public void LazyCMapParsesOnceAndReportsWidth()
    {
        byte[] data = Encoding.UTF8.GetBytes(
            "/WMode 1 def\nbegincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "beginbfchar\n<0041> <0041>\nendbfchar");
        OxLazyCMap lazy = new(data);
        Assert.Equal((byte)2, lazy.CodeWidth());
        Assert.Equal((byte)1, lazy.WMode());
        Assert.Same(lazy.Get(), lazy.Get());
        Assert.Equal("A", lazy.Get()!.Get(0x41));

        // A second wrapper over identical bytes shares the globally cached parse.
        OxLazyCMap same = new((byte[])data.Clone());
        Assert.Same(lazy.Get(), same.Get());
    }

    [Fact]
    public void PredefinedNamesResolve()
    {
        void Check(string name, OxCharacterCollection expected) =>
            Assert.Equal(expected, OxPredefinedCidFont.IsPredefined(name));

        Check("Ryumin-Light", OxCharacterCollection.AdobeJapan1);
        Check("Ryumin-Light-Identity-V", OxCharacterCollection.AdobeJapan1);
        Check("HeiseiKakuGo-W5-UniJIS-UCS2-H", OxCharacterCollection.AdobeJapan1);
        Check("ABCDEF+Ryumin-Light-Identity-V", OxCharacterCollection.AdobeJapan1);
        Check("STSong-Light-GBK-EUC-H", OxCharacterCollection.AdobeGB1);
        Check("XEAACC+STSong-Light", OxCharacterCollection.AdobeGB1);
        Check("SimSun", OxCharacterCollection.AdobeGB1);
        Check("MHei-Medium-B5pc-H", OxCharacterCollection.AdobeCNS1);
        Check("MingLiU-ETen-B5-V", OxCharacterCollection.AdobeCNS1);
        Check("HYSMyeongJo-Medium-KSC-EUC-H", OxCharacterCollection.AdobeKorea1);
        Check("Batang", OxCharacterCollection.AdobeKorea1);
    }

    [Theory]
    [InlineData("ArialMT")]
    [InlineData("Helvetica")]
    [InlineData("Times-Roman")]
    [InlineData("AGaramondPro-Regular")]
    public void UnrelatedFontsAreNotPredefined(string name)
    {
        Assert.Null(OxPredefinedCidFont.IsPredefined(name));
    }

    [Fact]
    public void CMapSuffixStripDoesNotSwallowBaseName()
    {
        Assert.Equal("Ryumin-Light", OxPredefinedCidFont.StripCMapSuffix("Ryumin-Light"));
        Assert.Equal("STSong-Light", OxPredefinedCidFont.StripCMapSuffix("STSong-Light"));
        // Longest match wins, so -GBK-EUC-H beats the legacy single-letter -H.
        Assert.Equal("STSong-Light", OxPredefinedCidFont.StripCMapSuffix("STSong-Light-GBK-EUC-H"));
    }

    [Fact]
    public void CidToUnicodeSpotChecksPerRegistry()
    {
        // CID 34 is 'A' and CID 91 is 'z' in every collection's ASCII prologue.
        Assert.Equal(0x0041u, OxCidMappings.LookupAdobeJapan1(34));
        Assert.Equal(0x007Au, OxCidMappings.LookupAdobeJapan1(91));
        Assert.Equal(0x0020u, OxCidMappings.LookupAdobeJapan1(1));
        Assert.Equal(0x3042u, OxCidMappings.LookupAdobeJapan1(843));   // あ
        Assert.Equal(0x9FA3u, OxCidMappings.LookupAdobeJapan1(23057));

        Assert.Equal(0x0041u, OxCidMappings.LookupAdobeGb1(34));
        Assert.Equal(0x007Au, OxCidMappings.LookupAdobeGb1(91));
        Assert.Equal(0x0020u, OxCidMappings.LookupAdobeGb1(1));

        Assert.Equal(0x0041u, OxCidMappings.LookupAdobeCns1(34));
        Assert.Equal(0x0020u, OxCidMappings.LookupAdobeCns1(1));

        Assert.Equal(0x0041u, OxCidMappings.LookupAdobeKorea1(34));
        Assert.Equal(0xAC00u, OxCidMappings.LookupAdobeKorea1(1086));  // 가
    }

    [Fact]
    public void CidToUnicodeRoutesThroughCharacterCollection()
    {
        Assert.Equal(0x0041u, OxCharacterCollection.AdobeJapan1.CidToUnicode(34));
        Assert.Equal(0x3042u, OxCharacterCollection.AdobeJapan1.CidToUnicode(843));
        Assert.Equal(0x0041u, OxCharacterCollection.AdobeGB1.CidToUnicode(34));
        Assert.Equal(0x0041u, OxCharacterCollection.AdobeCNS1.CidToUnicode(34));
        Assert.Equal(0xAC00u, OxCharacterCollection.AdobeKorea1.CidToUnicode(1086));
    }

    [Fact]
    public void CidToUnicodeIdentityFallbackForRawUnicodeCids()
    {
        // Producers that put raw Unicode in the CID land in the per-collection identity ranges.
        // These CIDs are past the end of each table, so only the fallback can answer them.
        Assert.Equal(0x9FFFu, OxCidMappings.LookupAdobeJapan1(0x9FFF));  // CJK Unified Ideographs
        Assert.Equal(0xF900u, OxCidMappings.LookupAdobeGb1(0xF900));     // CJK Compatibility Ideographs
        Assert.Equal(0xF900u, OxCidMappings.LookupAdobeCns1(0xF900));    // CJK Compatibility Ideographs
        Assert.Equal(0xAC00u, OxCidMappings.LookupAdobeKorea1(0xAC00));  // Hangul Syllables

        // Outside both the table and the fallback ranges nothing is produced.
        Assert.Null(OxCidMappings.LookupAdobeJapan1(0xE000));
        Assert.Null(OxCidMappings.LookupAdobeKorea1(0xE000));
    }

    [Fact]
    public void AdobeArabicIsIdentityOverArabicBlocksOnly()
    {
        Assert.Equal(0x0627u, OxCidMappings.LookupAdobeArabic(0x0627));  // alef
        Assert.Equal(0x067Eu, OxCidMappings.LookupAdobeArabic(0x067E));  // Persian pe
        Assert.Equal(0xFB50u, OxCidMappings.LookupAdobeArabic(0xFB50));
        Assert.Equal(0xFE70u, OxCidMappings.LookupAdobeArabic(0xFE70));
        Assert.Null(OxCidMappings.LookupAdobeArabic(0x41));
        Assert.Null(OxCidMappings.LookupAdobeArabic(0x01A4));
        Assert.Null(OxCidMappings.LookupAdobeArabic(0x05D0));
    }
}
