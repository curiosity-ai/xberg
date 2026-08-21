// Tests for the word-level extraction port: `Document::extract_words_inner`
// (document.rs:16623), `TextSpan::to_chars` (layout/text_block.rs:216) and
// `cluster_chars_into_words` (layout/clustering.rs:268).
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Layout;
using Xunit;

namespace Xberg.Tests;

public class OxWordExtractionTests
{
    private static OxTextSpan Span(
        string text, float x, float y, float width, float height = 10.0f,
        float fontSize = 10.0f, IEnumerable<float>? charWidths = null,
        IEnumerable<float>? charOffsets = null, float rotation = 0.0f,
        bool splitBoundary = false)
    {
        var span = new OxTextSpan
        {
            Text = text,
            Bbox = new OxRect(x, y, width, height),
            FontSize = fontSize,
            RotationDegrees = rotation,
            SplitBoundaryBefore = splitBoundary,
        };
        if (charWidths is not null) span.CharWidths = charWidths.ToList();
        if (charOffsets is not null) span.CharXOffsets = charOffsets.ToList();
        return span;
    }

    [Fact]
    public void ToChars_PrefersCapturedOriginsOverPrefixSummedWidths()
    {
        // The nominal widths omit the TJ kerning the offsets already account for, so the
        // third glyph must land where the offset says, not where the widths would put it.
        var span = Span("abc", 100.0f, 50.0f, 30.0f,
            charWidths: new[] { 10.0f, 10.0f, 10.0f },
            charOffsets: new[] { 100.0f, 110.0f, 125.0f });

        var chars = OxWordExtraction.ToChars(span);

        Assert.Equal(3, chars.Count);
        Assert.Equal(125.0f, chars[2].Bbox.X);
        Assert.Equal(10.0f, chars[2].Bbox.Width);
    }

    [Fact]
    public void ToChars_RejectsOriginsThatFallOutsideTheSpanBox()
    {
        // An offset list that does not fit the span's own box is not describing this span;
        // the widths, prefix-summed from the box's left edge, take over.
        var span = Span("abc", 100.0f, 50.0f, 30.0f,
            charWidths: new[] { 10.0f, 10.0f, 10.0f },
            charOffsets: new[] { 100.0f, 110.0f, 400.0f });

        var chars = OxWordExtraction.ToChars(span);

        Assert.Equal(new[] { 100.0f, 110.0f, 120.0f }, chars.Select(c => c.Bbox.X));
    }

    [Fact]
    public void ToChars_DividesTheBoxUniformlyWhenNoPerGlyphMetricsFit()
    {
        var span = Span("abcd", 0.0f, 0.0f, 40.0f);

        var chars = OxWordExtraction.ToChars(span);

        Assert.Equal(new[] { 0.0f, 10.0f, 20.0f, 30.0f }, chars.Select(c => c.Bbox.X));
        Assert.All(chars, c => Assert.Equal(10.0f, c.Bbox.Width));
    }

    [Fact]
    public void ToChars_KeepsSupplementaryScalarsWhole()
    {
        // Two regional-indicator scalars are four UTF-16 units; Rust walks scalars, so the
        // span must decompose into two glyphs, not four.
        var span = Span("\U0001F1EE\U0001F1E9", 0.0f, 0.0f, 20.0f);

        var chars = OxWordExtraction.ToChars(span);

        Assert.Equal(2, chars.Count);
        Assert.Equal(0x1F1EE, chars[0].CodePoint);
        Assert.Equal(0x1F1E9, chars[1].CodePoint);
    }

    [Fact]
    public void ExtractWords_SplitsOnTheSpaceGlyphAndDropsIt()
    {
        // "ab cd" laid out on a 5pt grid: the median glyph width is 5, so the word-gap
        // threshold is 1.5pt and nothing inside either token reaches it.
        var span = Span("ab cd", 0.0f, 0.0f, 25.0f,
            charWidths: new[] { 5.0f, 5.0f, 5.0f, 5.0f, 5.0f },
            charOffsets: new[] { 0.0f, 5.0f, 10.0f, 15.0f, 20.0f });

        var words = OxWordExtraction.ExtractWords(new[] { span });

        Assert.Equal(new[] { "ab", "cd" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_SplitsAGapWiderThanTheAdaptiveThreshold()
    {
        // Same glyphs with no space, but "cd" is pushed a whole em clear of "ab": the
        // clustering breaks there even though no whitespace was encoded.
        var span = Span("abcd", 0.0f, 0.0f, 30.0f,
            charWidths: new[] { 5.0f, 5.0f, 5.0f, 5.0f },
            charOffsets: new[] { 0.0f, 5.0f, 20.0f, 25.0f });

        var words = OxWordExtraction.ExtractWords(new[] { span });

        Assert.Equal(new[] { "ab", "cd" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_ClustersGlyphsInGeometricOrderNotEmissionOrder()
    {
        // A producer that draws the second glyph to the right of the third still reads
        // left-to-right, because the clustering sorts by position before grouping.
        var span = Span("acb", 0.0f, 0.0f, 15.0f,
            charWidths: new[] { 5.0f, 5.0f, 5.0f },
            charOffsets: new[] { 0.0f, 10.0f, 5.0f });

        var words = OxWordExtraction.ExtractWords(new[] { span });

        Assert.Equal(new[] { "abc" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_MergesAbuttingWordsFromSeparateSpans()
    {
        var words = OxWordExtraction.ExtractWords(new[]
        {
            Span("Q", 0.0f, 0.0f, 5.0f),
            Span("(x)", 5.0f, 0.0f, 15.0f),
        });

        Assert.Equal(new[] { "Q(x)" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_DoesNotMergeAcrossASplitBoundary()
    {
        // The span that opens a table cell is a hard boundary however tightly it abuts.
        var words = OxWordExtraction.ExtractWords(new[]
        {
            Span("Q", 0.0f, 0.0f, 5.0f),
            Span("(x)", 5.0f, 0.0f, 15.0f, splitBoundary: true),
        });

        Assert.Equal(new[] { "Q", "(x)" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_DoesNotMergeAcrossAMathBacktrack()
    {
        // A fraction's denominator is drawn after the relation sign that follows the
        // numerator, so it starts far behind the previous word at a baseline offset.
        var words = OxWordExtraction.ExtractWords(new[]
        {
            Span("=", 60.0f, 20.0f, 5.0f),
            Span("dt", 20.0f, 10.0f, 10.0f),
        });

        Assert.Equal(new[] { "=", "dt" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_DoesNotMergeALineWrapBackToTheMargin()
    {
        // A wrapped line can land at nearly the same Y as the one above it, but its X
        // resets by many ems — further than any same-line construct.
        var words = OxWordExtraction.ExtractWords(new[]
        {
            Span("whom", 400.0f, 100.0f, 20.0f),
            Span("tered", 50.0f, 99.8f, 25.0f),
        });

        Assert.Equal(new[] { "whom", "tered" }, words.Select(w => w.Text));
    }

    [Fact]
    public void ExtractWords_NeverMergesIntoOrOutOfARotatedRun()
    {
        // A rotated run's box is flattened onto X, so it overlaps columns it never touches.
        var words = OxWordExtraction.ExtractWords(new[]
        {
            Span("head", 0.0f, 0.0f, 20.0f, rotation: 90.0f),
            Span("body", 20.0f, 0.0f, 20.0f),
        });

        Assert.Equal(new[] { "head", "body" }, words.Select(w => w.Text));
    }
}
