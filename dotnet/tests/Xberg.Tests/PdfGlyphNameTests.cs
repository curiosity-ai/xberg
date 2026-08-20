using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>Covers glyph-name resolution (ISO 32000-1 §9.10.2) against the Adobe Glyph List.</summary>
public class PdfGlyphNameTests
{
    [Theory]
    [InlineData("A", "A")]
    [InlineData("zero", "0")]
    [InlineData("fi", "ﬁ")]
    [InlineData("adieresis", "ä")]
    public void LatinNamesResolve(string name, string expected) =>
        Assert.Equal(expected, PdfEncodings.GlyphNameToUnicode(name));

    [Theory]
    [InlineData("alpha", "α")]
    [InlineData("delta", "δ")]
    [InlineData("Sigma", "Σ")]
    [InlineData("lambda", "λ")]
    public void GreekNamesResolve(string name, string expected) =>
        Assert.Equal(expected, PdfEncodings.GlyphNameToUnicode(name));

    [Theory]
    [InlineData("element", "∈")]
    [InlineData("arrowright", "→")]
    [InlineData("lessequal", "≤")]
    [InlineData("greaterequal", "≥")]
    [InlineData("approxequal", "≈")]
    [InlineData("radical", "√")]
    [InlineData("partialdiff", "∂")]
    public void MathNamesResolve(string name, string expected) =>
        Assert.Equal(expected, PdfEncodings.GlyphNameToUnicode(name));

    [Fact]
    public void TexAmsNamesOutsideTheStandardListResolve() =>
        Assert.Equal("□", PdfEncodings.GlyphNameToUnicode("square"));

    [Fact]
    public void AVariantSuffixIsStripped() =>
        Assert.Equal("A", PdfEncodings.GlyphNameToUnicode("A.sc"));

    [Fact]
    public void ACompoundNameConcatenatesItsComponents() =>
        Assert.Equal("ff", PdfEncodings.GlyphNameToUnicode("f_f"));

    [Theory]
    [InlineData("uni0041", "A")]
    [InlineData("u00E9", "é")]
    public void CodepointNamesResolve(string name, string expected) =>
        Assert.Equal(expected, PdfEncodings.GlyphNameToUnicode(name));

    [Fact]
    public void AnUnknownNameResolvesToNothing() =>
        Assert.Equal("", PdfEncodings.GlyphNameToUnicode("g42"));
}
