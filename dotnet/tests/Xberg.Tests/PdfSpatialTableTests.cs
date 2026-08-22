using System.Collections.Generic;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>Covers the ruling-line table tier (`PdfSpatialTables`).</summary>
public class PdfSpatialTableTests
{
    private static PdfPath Line(double x1, double y1, double x2, double y2, double width = 0.75)
    {
        var ops = new List<PathOp> { PathOp.MoveTo(x1, y1), PathOp.LineTo(x2, y2) };
        return new PdfPath { Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Stroked = true, StrokeWidth = width };
    }

    private static PdfPath Rect(double x, double y, double w, double h, double width = 0.75)
    {
        var ops = new List<PathOp> { PathOp.Rect(x, y, w, h) };
        return new PdfPath { Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Stroked = true, StrokeWidth = width };
    }

    private static TableSpan Word(string text, double x, double y, double w = 20, double h = 10) =>
        new() { Text = text, Bbox = new PathRect(x, y, w, h), FontSize = 10 };

    private static Xberg.Internal.PdfOxide.OxTextSpan OxSpan(
        string text, float x, float y, float width, float rotation) =>
        new()
        {
            Text = text,
            Bbox = new Xberg.Internal.PdfOxide.OxRect(x, y, width, 10.0f),
            FontSize = 10.0f,
            RotationDegrees = rotation,
            CharWidths = new List<float>(new float[text.Length]),
        };

    /// <summary>
    /// A page whose runs are mostly sideways is ordered in the rotated frame upstream, and
    /// the map back out of that frame is not the exact inverse of the map into it: the word
    /// origins the detector measures carry a single-precision round-trip of the page width.
    /// </summary>
    [Fact]
    public void SidewaysPageWordOriginsCarryTheRotatedFrameRoundTrip()
    {
        var spans = new List<Xberg.Internal.PdfOxide.OxTextSpan>
        {
            OxSpan("alpha", 81.502f, 72.0f, 30.0f, 90.0f),
            OxSpan("beta", 81.502f, 120.0f, 24.0f, 90.0f),
        };

        var turned = PdfSpatialTables.WordsFromOxSpans(spans, 0.0, 0.0, 612.0, 792.0);
        var upright = PdfSpatialTables.WordsFromOxSpans(spans);

        // 612 - (612 - 81.502) lands one ULP of the page width above 81.502.
        Assert.Equal(81.50201416015625, turned[0].Bbox.X);
        Assert.Equal(81.50199890136719, upright[0].Bbox.X);
        // Only the origin turns; the run's own extents describe it in its upright frame.
        Assert.Equal(upright[0].Bbox.Width, turned[0].Bbox.Width);
        Assert.Equal(upright[0].Bbox.Y, turned[0].Bbox.Y);
    }

    /// <summary>
    /// The page's own <c>/Rotate</c> takes the rotated-frame branch out of play: upstream
    /// has already mapped such a page into its displayed frame before ordering.
    /// </summary>
    [Fact]
    public void ARotatedPageSkipsTheReadingFrameRoundTrip()
    {
        var spans = new List<Xberg.Internal.PdfOxide.OxTextSpan>
        {
            OxSpan("alpha", 81.502f, 72.0f, 30.0f, 90.0f),
            OxSpan("beta", 81.502f, 120.0f, 24.0f, 90.0f),
        };

        var words = PdfSpatialTables.WordsFromOxSpans(spans, 0.0, 0.0, 612.0, 792.0, pageRotation: 90);

        Assert.Equal(81.50199890136719, words[0].Bbox.X);
    }

    /// <summary>An upright page has no rotated reading frame to round-trip through.</summary>
    [Fact]
    public void AnUprightPageIsLeftAlone()
    {
        var spans = new List<Xberg.Internal.PdfOxide.OxTextSpan>
        {
            OxSpan("alpha", 81.502f, 72.0f, 30.0f, 0.0f),
            OxSpan("beta", 81.502f, 120.0f, 24.0f, 0.0f),
        };

        var words = PdfSpatialTables.WordsFromOxSpans(spans, 0.0, 0.0, 612.0, 792.0);

        Assert.Equal(81.50199890136719, words[0].Bbox.X);
    }

    [Fact]
    public void ANegativeExtentRectangleIsNormalizedLikeRustsRectNew()
    {
        // `20 204 480 -160 re` — Skia writes the box with the height running downward.
        var r = new PathRect(20, 204, 480, -160);
        Assert.Equal(44, r.Y);
        Assert.Equal(160, r.Height);
        Assert.Equal(44, r.Top);
        Assert.Equal(204, r.Bottom);
    }

    [Fact]
    public void AThinLongStrokeIsATablePrimitiveAndAZeroLengthDotIsNot()
    {
        Assert.True(Line(20, 76, 500, 76).IsTablePrimitive());
        Assert.True(Line(180, 44, 180, 204).IsTablePrimitive());
        // A zero-length round-capped segment renders as a blob, not a ruling.
        var dot = Line(180, 44, 180, 44, width: 6);
        dot.LineCap = 1;
        Assert.False(dot.IsTablePrimitive());
    }

    [Fact]
    public void AStrokeWidthEncodedRuleUsesItsRenderedExtent()
    {
        // A 1 pt segment stroked 40 pt wide is a bar 40 pt tall, not a speck.
        var bar = Line(100, 500, 101, 500, width: 40);
        var rendered = bar.RenderedBbox();
        Assert.Equal(40, rendered.Height, 3);
    }

    [Fact]
    public void AZeroLengthButtCappedSegmentKeepsItsDegenerateExtent()
    {
        // §8.4.3.3: a butt cap paints nothing at zero length. The bound upstream compares
        // the length against is the f32 machine epsilon, not the smallest denormal — with
        // the latter the guard never fires and the segment gains a stroke-wide box.
        var speck = Line(180, 44, 180, 44, width: 6);
        var rendered = speck.RenderedBbox();
        Assert.Equal(0, rendered.Width, 6);
        Assert.Equal(0, rendered.Height, 6);
    }

    [Fact]
    public void AnAxisAlignedRuleKeepsItsEdgesInSinglePrecision()
    {
        // The rendered edges are what the table bounding box is built from, and upstream
        // computes them in f32 throughout. A horizontal rule extends by exactly half the
        // stroke on the cross axis and not at all along its own, whatever its length.
        var rule = Line(73, 569.8, 543.2, 569.8, width: 1);
        var rendered = rule.RenderedBbox();
        Assert.Equal(73.0f, (float)rendered.X);
        Assert.Equal(543.2f - 73.0f, (float)rendered.Width);
        Assert.Equal(569.8f - 0.5f, (float)rendered.Y);
        Assert.Equal(1.0f, (float)rendered.Height);
        // `right()` is the single-precision sum of two single-precision edges.
        Assert.Equal((float)rendered.X + (float)rendered.Width, (float)rendered.Right);
    }

    [Fact]
    public void ARuledGridBecomesATableWithItsCellsInPlace()
    {
        var paths = new List<PdfPath>
        {
            Rect(20, 44, 480, 160),
            Line(20, 76, 500, 76), Line(20, 108, 500, 108),
            Line(20, 140, 500, 140), Line(20, 172, 500, 172),
            Line(180, 44, 180, 204), Line(286, 44, 286, 204), Line(393, 44, 393, 204),
        };
        var spans = new List<TableSpan>
        {
            Word("Region", 30, 180), Word("Q1", 190, 180), Word("Q2", 296, 180), Word("Q3", 400, 180),
            Word("North", 30, 148), Word("1,204", 190, 148), Word("1,388", 296, 148), Word("1,502", 400, 148),
            Word("South", 30, 116), Word("942", 190, 116), Word("1,011", 296, 116), Word("1,140", 400, 116),
            Word("East", 30, 84), Word("1,655", 190, 84), Word("1,702", 296, 84), Word("1,690", 400, 84),
            Word("West", 30, 52), Word("803", 190, 52), Word("877", 296, 52), Word("934", 400, 52),
        };

        var tables = PdfSpatialTables.DetectPageTables(spans, paths, 1, TableDetectionConfig.Strict());

        var table = Assert.Single(tables);
        Assert.Equal(new List<string> { "Region", "Q1", "Q2", "Q3" }, table.Cells[0]);
        Assert.Equal(new List<string> { "West", "803", "877", "934" }, table.Cells[4]);
        Assert.StartsWith("| Region | Q1 | Q2 | Q3 |\n| --- | --- | --- | --- |\n", table.Markdown);
        Assert.Equal(1u, table.PageNumber);
    }

    [Fact]
    public void APageWithNoRulingLinesYieldsNoRuledTable()
    {
        var spans = new List<TableSpan> { Word("Region", 30, 180), Word("Q1", 190, 180) };
        Assert.Empty(PdfSpatialTables.DetectPageTables(spans, new List<PdfPath>(), 1, TableDetectionConfig.Strict()));
    }

    [Fact]
    public void WordPiecesEmittedAsSeparateOperatorsAreGluedBackTogether()
    {
        // pdf_oxide assembles words from glyphs, so `1,011` never reaches the detector
        // as two pieces; our spans arrive cut at every Tj.
        var spans = new List<TextSpan>
        {
            new() { Text = "1,01", X = 251.25, Y = 667.5, Width = 18.98, Height = 9.75, FontSize = 9.75 },
            new() { Text = "1", X = 269.51, Y = 667.5, Width = 5.42, Height = 9.75, FontSize = 9.75 },
            new() { Text = "877", X = 331.5, Y = 667.5, Width = 16.27, Height = 9.75, FontSize = 9.75 },
        };
        var words = PdfSpatialTables.SpansToWords(spans);
        Assert.Equal(new[] { "1,011", "877" }, words.ConvertAll(w => w.Text).ToArray());
    }

    [Fact]
    public void ASpanHoldingSeveralWordsIsSplitProportionally()
    {
        var spans = new List<TextSpan>
        {
            new() { Text = "Quarterly revenue", X = 40, Y = 700, Width = 170, Height = 11, FontSize = 11 },
        };
        var words = PdfSpatialTables.SpansToWords(spans);
        Assert.Equal(2, words.Count);
        Assert.Equal("Quarterly", words[0].Text);
        Assert.Equal("revenue", words[1].Text);
        Assert.True(words[1].Bbox.X > words[0].Bbox.X + words[0].Bbox.Width);
    }
}
