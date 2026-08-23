// Tests for the XY-Cut reading order (pdf_oxide `pipeline/reading_order/xycut.rs`,
// `XYCutStrategy`). What is pinned here is which candidate a tie resolves to: the projection
// searches reduce with `max_by`, whose tie-break decides where a region is cut and therefore
// the order the page is read in.
using System.Collections.Generic;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

public sealed class PdfReadingOrderTests
{
    /// <summary>16 non-whitespace characters, so a 10pt span's glyph core fills its 70pt box.</summary>
    private const string Line = "sixteen chars ok";

    private static TextSpan Span(double x, double y, double height) => new()
    {
        Text = Line,
        X = x,
        Y = y,
        Width = 70.0,
        Height = height,
        FontSize = 10.0,
    };

    /// <summary>
    /// Two sparse 70pt blocks on the left and a dense one on the right, separated by gutters of
    /// the caller's width. The short blocks carry tall spans so all three project above the
    /// valley threshold and only the gutters read as valleys.
    /// </summary>
    private static List<TextSpan> ThreeBlocks(double middleX, double rightX)
    {
        var spans = new List<TextSpan>
        {
            Span(0.0, 100.0, 10.0),
            Span(0.0, 60.0, 10.0),
            Span(middleX, 90.0, 10.0),
            Span(middleX, 50.0, 10.0),
        };
        for (double y = 100.0; y >= 30.0; y -= 10.0)
        {
            spans.Add(Span(rightX, y, 2.0));
        }
        return spans;
    }

    [Fact]
    public void EquallyWideValleysCutAtTheRightmostOne()
    {
        // Both gutters are 20pt, so the two valleys tie. Cutting at the right one leaves the
        // left and middle blocks with too few spans to split again, so they read interleaved
        // by Y; cutting at the left one would read the middle block as a column of its own.
        var ordered = PdfReadingOrder.Order(ThreeBlocks(middleX: 90.0, rightX: 180.0));

        Assert.Equal(
            new[] { (0.0, 100.0), (90.0, 90.0), (0.0, 60.0), (90.0, 50.0) },
            Head(ordered, 4));
    }

    [Fact]
    public void AWiderValleyStillWinsOverALaterNarrowerOne()
    {
        // The left gutter is 40pt against the right one's 20pt, so the widest valley is the
        // earlier one and the middle block becomes its own column.
        var ordered = PdfReadingOrder.Order(ThreeBlocks(middleX: 110.0, rightX: 200.0));

        Assert.Equal(
            new[] { (0.0, 100.0), (0.0, 60.0), (110.0, 90.0), (110.0, 50.0) },
            Head(ordered, 4));
    }

    private static (double X, double Y)[] Head(List<TextSpan> spans, int count)
    {
        var head = new (double, double)[count];
        for (int i = 0; i < count; i++) head[i] = (spans[i].X, spans[i].Y);
        return head;
    }
}
