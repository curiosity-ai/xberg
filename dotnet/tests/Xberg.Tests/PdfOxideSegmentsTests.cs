using System.Collections.Generic;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the structure pipeline's own span producer (`pdf/oxide/hierarchy.rs ::
/// extract_segments_from_page_inner`). The invariant that matters is one segment per span:
/// the heading pass, the bold pass and the inline annotations all read style per segment, so
/// collapsing a line into one segment erases the distinctions they exist to make.
/// </summary>
public class PdfOxideSegmentsTests
{
    private static OxTextSpan Span(
        string text, float x, float y, float width, float fontSize = 10f,
        OxFontWeight weight = OxFontWeight.Normal)
        => new()
        {
            Text = text,
            Bbox = new OxRect(x, y, width, fontSize),
            FontSize = fontSize,
            FontWeight = weight,
        };

    [Fact]
    public void EachSpanBecomesItsOwnSegmentWithItsOwnWeight()
    {
        var spans = new List<OxTextSpan>
        {
            Span("Heading", 72f, 700f, 40f, weight: OxFontWeight.Bold),
            Span("body text", 116f, 700f, 50f),
        };

        var segments = PdfOxideSegments.FromPage(spans, 612f, 792f);

        Assert.Equal(2, segments.Count);
        Assert.True(segments[0].IsBold);
        Assert.False(segments[1].IsBold);
    }

    [Fact]
    public void ArtifactAndBlankSpansAreDropped()
    {
        var artifact = Span("running head", 72f, 770f, 60f);
        artifact.ArtifactType = OxArtifactType.Pagination;
        var spans = new List<OxTextSpan> { artifact, Span("   ", 72f, 700f, 5f), Span("kept", 72f, 690f, 20f) };

        var segments = PdfOxideSegments.FromPage(spans, 612f, 792f);

        Assert.Single(segments);
        Assert.Equal("kept", segments[0].Text);
    }

    [Fact]
    public void ARedrawnRunCollapsesAndKeepsTheBoldSignalItCarried()
    {
        // Faking bold by drawing the same run twice at a sub-point offset is precisely a
        // boldness cue, so the survivor absorbs it.
        var spans = new List<OxTextSpan>
        {
            Span("Total", 72f, 700f, 30f),
            Span("Total", 72.3f, 700.1f, 30f, weight: OxFontWeight.Bold),
        };

        var segments = PdfOxideSegments.FromPage(spans, 612f, 792f);

        Assert.Single(segments);
        Assert.True(segments[0].IsBold);
    }

    [Fact]
    public void SpansComeBackInRowBandOrderRatherThanEmissionOrder()
    {
        // The structure path asks for TopToBottom, not the column-aware order the text path
        // uses, so a span emitted late but drawn high on the page comes first.
        var spans = new List<OxTextSpan>
        {
            Span("second", 300f, 700f, 30f),
            Span("first", 72f, 700f, 30f),
            Span("above", 72f, 740f, 30f),
        };

        var segments = PdfOxideSegments.FromPage(spans, 612f, 792f);

        Assert.Equal(new[] { "above", "first", "second" }, segments.ConvertAll(s => s.Text));
    }

    [Fact]
    public void ASubscriptIsFoldedBackIntoTheWordItModifies()
    {
        // "H" plus a shifted "2" at 60% of the size is one word. Left apart the script becomes
        // its own segment, which splits the word around it and defeats every downstream test
        // that reads whole words.
        var baseSpan = Span("H", 72f, 700f, 7f);
        var script = Span("2", 79f, 698.5f, 3f, fontSize: 6f);
        var segments = PdfOxideSegments.FromPage(new List<OxTextSpan> { baseSpan, script }, 612f, 792f);

        Assert.Single(segments);
        Assert.Equal("H2", segments[0].Text);
    }

    [Fact]
    public void AnEmptyPageYieldsNoSegments() =>
        Assert.Empty(PdfOxideSegments.FromPage(new List<OxTextSpan>(), 612f, 792f));
}
