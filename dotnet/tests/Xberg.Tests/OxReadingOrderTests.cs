// Tests for the pdf_oxide reading-order port: `document.rs`
// (ReadingOrder / order_spans_column_aware / drop_offpage_spans / order_rotated_blocks)
// and the `utils` comparators in `lib.rs`.
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Layout;
using Xunit;

namespace Xberg.Tests;

public class OxReadingOrderTests
{
    private static OxTextSpan Span(
        string text, float x, float y, float width, float height,
        float fontSize = 12.0f, OxFontWeight weight = OxFontWeight.Normal,
        float rotation = 0.0f, byte wmode = 0) =>
        new()
        {
            Text = text,
            Bbox = new OxRect(x, y, width, height),
            FontSize = fontSize,
            FontWeight = weight,
            RotationDegrees = rotation,
            Wmode = wmode,
        };

    /// 25 non-whitespace characters — a plausible half-column body line at 12pt.
    private const string BodyText = "The quick brown fox jumps.";

    /// Two 220pt columns at x=50 and x=300 with a 30pt gutter, ten 14pt-leading lines each.
    private static List<OxTextSpan> TwoColumnPage()
    {
        var spans = new List<OxTextSpan>();
        for (int line = 0; line < 10; line++)
        {
            float y = 700.0f - line * 14.0f;
            spans.Add(Span($"L{line} {BodyText}", 50.0f, y, 220.0f, 12.0f));
            spans.Add(Span($"R{line} {BodyText}", 300.0f, y, 220.0f, 12.0f));
        }
        return spans;
    }

    [Fact]
    public void ColumnAwareReadsColumnMajorNotRowMajor()
    {
        var ordered = OxReadingOrder.OrderSpansColumnAware(TwoColumnPage());
        var tags = ordered.Select(s => s.Text[..2]).ToList();

        // Column-major: the whole left column, then the whole right column. A row-aware
        // sort would interleave L0 R0 L1 R1 …
        Assert.Equal(
            new[] { "L0", "L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9" },
            tags.Take(10));
        Assert.Equal(
            new[] { "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9" },
            tags.Skip(10));
    }

    [Fact]
    public void FullWidthHeadingAboveTwoColumnsStaysFirst()
    {
        var spans = TwoColumnPage();
        // Full-width 18pt title sitting above both columns, appended last so only the
        // geometry can put it first.
        spans.Add(Span("H0 Quarterly Report", 50.0f, 740.0f, 470.0f, 12.0f, fontSize: 18.0f));

        var ordered = OxReadingOrder.OrderSpansColumnAware(spans);
        var tags = ordered.Select(s => s.Text[..2]).ToList();

        Assert.Equal("H0", tags[0]);
        Assert.Equal(new[] { "L0", "L1", "L2" }, tags.Skip(1).Take(3));
        Assert.Equal("R0", tags[11]);
    }

    [Fact]
    public void EqualYAndXKeepExtractionOrder()
    {
        // Identical geometry: the comparator returns Equal for every pair, so only a
        // stable sort preserves the sequence the extractor emitted.
        var spans = new List<OxTextSpan>
        {
            Span("a", 100.0f, 500.0f, 10.0f, 12.0f),
            Span("b", 100.0f, 500.0f, 10.0f, 12.0f),
            Span("c", 100.0f, 500.0f, 10.0f, 12.0f),
            Span("d", 100.0f, 500.0f, 10.0f, 12.0f),
        };

        var ordered = OxReadingOrder.ApplyReadingOrder(spans, OxReadingOrder.Mode.TopToBottom, null);

        Assert.Equal("abcd", string.Concat(ordered.Select(s => s.Text)));
    }

    [Fact]
    public void TopToBottomBandsRowsThenOrdersByX()
    {
        // 701.5 and 700 fall in the same 3pt row band, so X decides between them; the
        // 660 span is a lower band and comes after both.
        var spans = new List<OxTextSpan>
        {
            Span("right", 300.0f, 700.0f, 40.0f, 12.0f),
            Span("below", 100.0f, 660.0f, 40.0f, 12.0f),
            Span("left", 100.0f, 701.5f, 40.0f, 12.0f),
        };

        var ordered = OxReadingOrder.ApplyReadingOrder(spans, OxReadingOrder.Mode.TopToBottom, null);

        Assert.Equal(new[] { "left", "right", "below" }, ordered.Select(s => s.Text));
    }

    [Fact]
    public void StructureFallsBackToColumnAware()
    {
        var page = TwoColumnPage();

        var structure = OxReadingOrder.ApplyReadingOrder(page, OxReadingOrder.Mode.Structure, null);
        var columnAware = OxReadingOrder.ApplyReadingOrder(page, OxReadingOrder.Mode.ColumnAware, null);

        Assert.Equal(columnAware.Select(s => s.Text), structure.Select(s => s.Text));
    }

    [Fact]
    public void RotatedRunIsKeptOutOfTheHorizontalFlow()
    {
        var spans = new List<OxTextSpan>
        {
            Span("body-1", 100.0f, 700.0f, 200.0f, 12.0f),
            Span("stamp-1", 30.0f, 400.0f, 12.0f, 80.0f, rotation: 90.0f),
            Span("body-2", 100.0f, 680.0f, 200.0f, 12.0f),
            Span("stamp-2", 30.0f, 300.0f, 12.0f, 80.0f, rotation: 90.0f),
        };

        OxReadingOrder.ApplyRotationFirewall(spans);

        // The horizontal body keeps its exact order and the rotated stamp is appended as
        // its own block, ordered in an upright frame (rotating by -90° puts the y=400
        // stamp left of the y=300 one).
        Assert.Equal(new[] { "body-1", "body-2", "stamp-2", "stamp-1" }, spans.Select(s => s.Text));
    }

    [Fact]
    public void RotationFirewallIsNoOpWithoutRotatedSpans()
    {
        var spans = new List<OxTextSpan>
        {
            Span("one", 100.0f, 700.0f, 50.0f, 12.0f),
            Span("two", 100.0f, 680.0f, 50.0f, 12.0f),
        };

        OxReadingOrder.ApplyRotationFirewall(spans);

        Assert.Equal(new[] { "one", "two" }, spans.Select(s => s.Text));
    }

    [Fact]
    public void OffpageSpansAreDroppedAndBleedIsKept()
    {
        var spans = new List<OxTextSpan>
        {
            Span("visible", 100.0f, 700.0f, 200.0f, 12.0f),
            // A page reusing one big Form XObject parks other pages' text far below the box.
            Span("offpage", 100.0f, -5000.0f, 200.0f, 12.0f),
            // Trim-mark content only partially outside must survive.
            Span("bleed", -30.0f, 700.0f, 50.0f, 12.0f),
        };

        OxReadingOrder.DropOffpageSpans(spans, 0.0f, 0.0f, 612.0f, 792.0f);

        Assert.Equal(new[] { "visible", "bleed" }, spans.Select(s => s.Text));
    }

    [Fact]
    public void SwappedMediaBoxCornersKeepThePageText()
    {
        // `[0 792 612 0]` — ury < lly. Without the min/max normalisation the test
        // inverts and the whole page is dropped.
        var spans = new List<OxTextSpan> { Span("visible", 100.0f, 700.0f, 200.0f, 12.0f) };

        OxReadingOrder.DropOffpageSpans(spans, 0.0f, 792.0f, 612.0f, 0.0f);

        Assert.Single(spans);
    }

    [Fact]
    public void ApplyReadingOrderDropsOffpageBeforeSorting()
    {
        var spans = new List<OxTextSpan>
        {
            Span("keep", 100.0f, 700.0f, 200.0f, 12.0f),
            Span("drop", 100.0f, 9000.0f, 200.0f, 12.0f),
        };

        var ordered = OxReadingOrder.ApplyReadingOrder(
            spans, OxReadingOrder.Mode.TopToBottom, (0.0f, 0.0f, 612.0f, 792.0f));

        Assert.Equal(new[] { "keep" }, ordered.Select(s => s.Text));
    }

    [Fact]
    public void SafeFloatCmpPlacesNaNLast()
    {
        Assert.Equal(-1, OxSpanCompare.SafeFloatCmp(1.0f, 2.0f));
        Assert.Equal(1, OxSpanCompare.SafeFloatCmp(2.0f, 1.0f));
        Assert.Equal(0, OxSpanCompare.SafeFloatCmp(1.5f, 1.5f));
        Assert.Equal(0, OxSpanCompare.SafeFloatCmp(float.NaN, float.NaN));
        Assert.Equal(1, OxSpanCompare.SafeFloatCmp(float.NaN, 1.0f));
        Assert.Equal(-1, OxSpanCompare.SafeFloatCmp(1.0f, float.NaN));
        // -0.0 and 0.0 must compare equal, as Rust's partial_cmp does.
        Assert.Equal(0, OxSpanCompare.SafeFloatCmp(-0.0f, 0.0f));
    }

    [Fact]
    public void RowAwareSpanCmpFallsBackToTheTotalOrderOnNonFiniteY()
    {
        // Infinite Y cannot be quantized into a band; the comparator must still order it
        // deterministically (larger Y first) rather than collapse it onto a finite band.
        Assert.True(OxSpanCompare.RowAwareSpanCmp(float.PositiveInfinity, 0.0f, 700.0f, 0.0f) < 0);
        Assert.True(OxSpanCompare.RowAwareSpanCmp(700.0f, 0.0f, float.PositiveInfinity, 0.0f) > 0);
        Assert.True(OxSpanCompare.RowAwareSpanCmp(float.NaN, 0.0f, 700.0f, 0.0f) < 0);
    }

    [Fact]
    public void TategakiReadsColumnsRightToLeftAndTopToBottom()
    {
        // Two vertical CJK columns: x=400 (right, read first) and x=300.
        var spans = new List<OxTextSpan>
        {
            Span("left-lower", 300.0f, 600.0f, 12.0f, 12.0f, wmode: 1),
            Span("right-upper", 400.0f, 700.0f, 12.0f, 12.0f, wmode: 1),
            Span("left-upper", 300.0f, 700.0f, 12.0f, 12.0f, wmode: 1),
            Span("right-lower", 400.0f, 600.0f, 12.0f, 12.0f, wmode: 1),
        };

        Assert.True(OxReadingOrder.IsTategakiPage(spans));
        var ordered = OxSpanCompare.SortVerticalTategaki(spans, s => s.Bbox);

        Assert.Equal(
            new[] { "right-upper", "right-lower", "left-upper", "left-lower" },
            ordered.Select(s => s.Text));
    }

    [Fact]
    public void TategakiVoteNeedsHalfThePageInWMode1()
    {
        var horizontal = new List<OxTextSpan>
        {
            Span("h1", 0.0f, 0.0f, 12.0f, 12.0f),
            Span("h2", 0.0f, 0.0f, 12.0f, 12.0f),
            Span("v1", 0.0f, 0.0f, 12.0f, 12.0f, wmode: 1),
        };

        Assert.False(OxReadingOrder.IsTategakiPage(horizontal));
        Assert.False(OxReadingOrder.IsTategakiPage(new List<OxTextSpan>()));
    }
}
