using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the wiring that joins the independently ported font modules: the font
/// dictionary reaching the CMap parser, the glyph list and the Adobe CID tables.
/// </summary>
[Collection(OxFontSeamCollection.Name)]
public class OxFontSeamTests
{
    private const string SimpleCMap =
        "/CIDInit /ProcSet findresource begin\n" +
        "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
        "1 beginbfchar\n<41> <0061>\nendbfchar\n" +
        "endcmap\n";

    [Fact]
    public void AParsedCMapSatisfiesTheMappersNarrowerLookupSeam()
    {
        var cmap = OxCMap.ParseToUnicodeCMap(System.Text.Encoding.ASCII.GetBytes(SimpleCMap));

        IOxToUnicodeLookup seam = OxFontWiring.AsToUnicodeLookup(cmap);
        Assert.Equal("a", seam.Get(0x41));
        Assert.Null(seam.Get(0x42));
    }

    [Fact]
    public void TheFontDictionarysCMapSeamParsesOnlyWhenFirstConsulted()
    {
        OxFontWiring.Install();

        IOxCMap lazy = OxFontSeams.CMaps!.CreateLazy(System.Text.Encoding.ASCII.GetBytes(SimpleCMap));
        Assert.Equal("a", lazy.Lookup(0x41));
        Assert.True(lazy.IsParsed);
        Assert.Equal(1, lazy.Count);
    }

    [Fact]
    public void TheGlyphNameSeamResolvesThroughTheAdobeGlyphList()
    {
        OxFontWiring.Install();

        Assert.Equal('A', OxFontSeams.GlyphNames!.GlyphNameToUnicode("A"));
        Assert.Equal("∈", OxFontSeams.GlyphNames!.GlyphNameToUnicodeString("element"));
    }

    [Fact]
    public void TheCidSeamResolvesCjkThroughTheAdobeTables()
    {
        OxFontWiring.Install();

        // Adobe-Japan1 CID 843 is HIRAGANA LETTER A; only the table knows that.
        Assert.Equal((uint)'あ', OxFontSeams.PredefinedCidUnicode!.LookupAdobeJapan1(843));
    }

    [Fact]
    public void TheMapperResolvesCjkCidsOnceTheSeamIsInstalled()
    {
        OxFontWiring.Install();

        var mapper = new OxCharacterMapper();
        mapper.SetPredefinedCMap(new OxPredefinedCMapConfig("Japan1"));

        Assert.Equal("あ", mapper.MapCharacter(843));
    }

    [Fact]
    public void TheCMapSeamCarriesItsCodespaceWidth()
    {
        OxFontWiring.Install();

        // A two-byte codespace is authoritative for the byte-mode decision: a CJK font
        // whose CMap declares one must be read two bytes at a time whatever its /Encoding
        // name says. Without this reaching the decoder those pages decode single-byte.
        IOxCMap twoByte = OxFontSeams.CMaps!.CreateLazy(System.Text.Encoding.ASCII.GetBytes(
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 beginbfchar\n<3042> <3042>\nendbfchar\nendcmap\n"));
        Assert.Equal(2, Assert.IsAssignableFrom<Xberg.Internal.PdfOxide.Text.IOxCMapCodeWidth>(twoByte).CodeWidth);

        IOxCMap oneByte = OxFontSeams.CMaps!.CreateLazy(System.Text.Encoding.ASCII.GetBytes(
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "1 beginbfchar\n<41> <0061>\nendbfchar\nendcmap\n"));
        Assert.Equal(1, Assert.IsAssignableFrom<Xberg.Internal.PdfOxide.Text.IOxCMapCodeWidth>(oneByte).CodeWidth);
    }
}
