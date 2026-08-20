using System.Collections.Generic;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>Covers /PageLabels formatting (ISO 32000-1 §12.4.2).</summary>
public class PdfPageLabelTests
{
    private static PageLabelRange Range(int startPage, PageLabelStyle style, uint startValue = 1, string? prefix = null)
        => new() { StartPage = startPage, Style = style, StartValue = startValue, Prefix = prefix };

    [Fact]
    public void RomanFrontMatterCountsFromItsOwnStartValue()
    {
        var range = Range(0, PageLabelStyle.RomanLower);
        Assert.Equal("i", range.FormatLabel(0));
        Assert.Equal("iv", range.FormatLabel(3));
        Assert.Equal("ix", range.FormatLabel(8));
    }

    [Fact]
    public void ARangeStartingMidDocumentNumbersFromItsStartValue()
    {
        // Body pages resume arabic numbering at 1 after eight roman front-matter pages.
        var range = Range(8, PageLabelStyle.Decimal);
        Assert.Equal("1", range.FormatLabel(8));
        Assert.Equal("12", range.FormatLabel(19));
    }

    [Fact]
    public void AStartValueOffsetsTheWholeRange()
    {
        var range = Range(0, PageLabelStyle.Decimal, startValue: 101);
        Assert.Equal("101", range.FormatLabel(0));
        Assert.Equal("105", range.FormatLabel(4));
    }

    [Fact]
    public void AlphabeticLabelsRollOverPastZ()
    {
        var upper = Range(0, PageLabelStyle.AlphaUpper);
        Assert.Equal("A", upper.FormatLabel(0));
        Assert.Equal("Z", upper.FormatLabel(25));
        Assert.Equal("AA", upper.FormatLabel(26));
        Assert.Equal("AB", upper.FormatLabel(27));
        Assert.Equal("aa", Range(0, PageLabelStyle.AlphaLower).FormatLabel(26));
    }

    [Fact]
    public void APrefixWithoutAStyleLabelsEveryPageTheSame()
    {
        var range = Range(0, PageLabelStyle.None, prefix: "Appendix-");
        Assert.Equal("Appendix-", range.FormatLabel(0));
        Assert.Equal("Appendix-", range.FormatLabel(3));
    }

    [Fact]
    public void APrefixCombinesWithTheNumber()
    {
        var range = Range(0, PageLabelStyle.Decimal, prefix: "A-");
        Assert.Equal("A-1", range.FormatLabel(0));
        Assert.Equal("A-3", range.FormatLabel(2));
    }

    [Fact]
    public void RomanNumeralsUseSubtractiveForms()
    {
        var upper = Range(0, PageLabelStyle.RomanUpper);
        Assert.Equal("IV", upper.FormatLabel(3));
        Assert.Equal("XL", upper.FormatLabel(39));
        Assert.Equal("MCMXCIV", upper.FormatLabel(1993));
    }
}
