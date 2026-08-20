using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the glue joining the independently ported font modules: the character mapper
/// reaching a parsed /ToUnicode CMap, and its CID lookups reaching the Adobe tables.
/// </summary>
public class OxFontSeamTests
{
    [Fact]
    public void AParsedCMapSatisfiesTheMappersLookupSeam()
    {
        var cmap = OxCMap.ParseToUnicodeCMap(System.Text.Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "1 beginbfchar\n<41> <0061>\nendbfchar\n" +
            "endcmap\n"));

        IOxCMap seam = cmap;
        Assert.Equal("a", seam.Get(0x41));
        Assert.Null(seam.Get(0x42));
    }

    [Fact]
    public void TheMapperResolvesCjkCidsThroughTheAdobeTablesOnceTheSeamIsInstalled()
    {
        OxFontSeams.Install();

        var mapper = new OxCharacterMapper();
        mapper.SetPredefinedCMap(new OxPredefinedCMapConfig("Japan1"));

        // Adobe-Japan1 CID 843 is HIRAGANA LETTER A; the table is the only thing that knows it.
        Assert.Equal("あ", mapper.MapCharacter(843));
    }

    [Fact]
    public void TheIdentityOrderingIsResolvedWithoutTheTables()
    {
        // Identity needs no table: the CID is the code point, so it answers even
        // when nothing has been installed.
        var mapper = new OxCharacterMapper();
        mapper.SetPredefinedCMap(new OxPredefinedCMapConfig("Identity"));

        Assert.Equal("A", mapper.MapCharacter(0x41));
    }
}
