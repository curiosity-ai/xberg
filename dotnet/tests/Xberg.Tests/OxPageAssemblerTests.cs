// Fixtures mirror the Rust unit tests in crates/xberg/src/pdf/oxide/text.rs so a
// divergence here points straight at the corresponding Rust assertion.
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Cover for the consumer half of the ported pipeline: the two guarded column repairs, the
/// inline-fragment reattachment, and the separator rules that decide whether two adjacent
/// spans are joined, spaced, wrapped or paragraph-broken.
/// </summary>
public class OxPageAssemblerTests
{
    private static OxTextSpan Span(string text, float x, float y, float width, float height, float fontSize) =>
        new() { Text = text, Bbox = new OxRect(x, y, width, height), FontSize = fontSize };

    /// <summary>A span painted with a rotated text matrix: x/y stay page-space (that is what
    /// pdf_oxide reports); width is the glyph-advance run along the rotated baseline and
    /// height the font extent across it.</summary>
    private static OxTextSpan Rotated(string text, float x, float y, float width, float height, float degrees)
    {
        var span = Span(text, x, y, width, height, height);
        span.RotationDegrees = degrees;
        return span;
    }

    /// <summary>The assembly proper, as the Rust <c>assemble_page_text</c> tests call it —
    /// without the whole-page rotation repair and fragmentation rebuild the page flow layers
    /// on top, which would replace the text these fixtures are checking.</summary>
    private static string Assemble(params OxTextSpan[] spans) =>
        OxPageAssembler.AssemblePageText(spans.ToList()).Text;

    // ── Separator rules ────────────────────────────────────────────────────────

    [Fact]
    public void SpansOnOneBaselineAreSpacedByTheirGap()
    {
        Assert.Equal("one two", Assemble(
            Span("one", 100, 100, 20, 10, 10),
            Span("two", 125, 100, 20, 10, 10)));
    }

    [Fact]
    public void AdjacentSpansWithNoGapAreJoinedWithoutASpace()
    {
        Assert.Equal("onetwo", Assemble(
            Span("one", 100, 100, 20, 10, 10),
            Span("two", 120, 100, 20, 10, 10)));
    }

    [Fact]
    public void ASmallBaselineStepWrapsAndALargeOneBreaksTheParagraph()
    {
        Assert.Equal("one\ntwo", Assemble(
            Span("one", 100, 112, 20, 10, 10),
            Span("two", 100, 100, 20, 10, 10)));
        Assert.Equal("one\n\ntwo", Assemble(
            Span("one", 100, 130, 20, 10, 10),
            Span("two", 100, 100, 20, 10, 10)));
    }

    [Fact]
    public void ASplitBoundaryForcesASpaceBetweenAdjacentSpans()
    {
        var next = Span("002", 130, 100, 18, 10, 10);
        next.SplitBoundaryBefore = true;
        Assert.Equal("1.000 002", Assemble(Span("1.000", 100, 100, 30, 10, 10), next));
    }

    [Fact]
    public void ASplitBoundaryDoesNotDoubleAnExistingSpace()
    {
        var next = Span("002", 130, 100, 18, 10, 10);
        next.SplitBoundaryBefore = true;
        Assert.Equal("1.000 002", Assemble(Span("1.000 ", 100, 100, 30, 10, 10), next));
    }

    [Fact]
    public void AFarLeftResetStartsANewRowEvenWhenTheBaselineBandsOverlap()
    {
        Assert.Equal("1.000\n002", Assemble(
            Span("1.000", 500, 100, 30, 10, 10),
            Span("002", 30, 99, 18, 10, 10)));
    }

    [Fact]
    public void AModerateBacktrackDoesNotStartANewRow()
    {
        var denominator = Span("denominator", 65, 96, 55, 10, 10);
        denominator.SplitBoundaryBefore = true;
        Assert.Equal("numerator denominator", Assemble(
            Span("numerator", 100, 104, 45, 10, 10), denominator));
    }

    [Fact]
    public void AFarLeftResetDoesNotSplitRtlText()
    {
        var next = Span("العالم", 430, 100, 35, 10, 10);
        next.SplitBoundaryBefore = true;
        Assert.Equal("مرحبا العالم", Assemble(Span("مرحبا", 500, 100, 30, 10, 10), next));
    }

    [Fact]
    public void AFarLeftResetRespectsRtlSpanMetadataForAsciiText()
    {
        var previous = Span("first", 500, 100, 30, 10, 10);
        previous.RtlDrawLogical = true;
        var next = Span("second", 430, 100, 35, 10, 10);
        next.RtlDrawLogical = true;
        next.SplitBoundaryBefore = true;
        Assert.Equal("first second", Assemble(previous, next));
    }

    [Fact]
    public void AFarLeftResetDoesNotSplitRotatedText()
    {
        var previous = Span("first", 500, 100, 30, 10, 10);
        previous.RotationDegrees = 90;
        var next = Span("second", 430, 100, 35, 10, 10);
        next.RotationDegrees = 90;
        next.SplitBoundaryBefore = true;
        Assert.Equal("first second", Assemble(previous, next));
    }

    [Fact]
    public void ARotationChangeIsAHardBlockBoundary()
    {
        Assert.Equal("upright\n\nsideways", Assemble(
            Span("upright", 100, 100, 40, 10, 10),
            Rotated("sideways", 100, 100, 40, 10, 90)));
    }

    [Fact]
    public void ARotatedTableIsReadAlongItsOwnAxis()
    {
        Assert.Equal("Engine coolant\n18.6 quarts", Assemble(
            Rotated("Engine", 400, 100, 30, 10, 90),
            Rotated("coolant", 400, 132, 32, 10, 90),
            Rotated("18.6", 388, 100, 22, 10, 90),
            Rotated("quarts", 388, 124, 30, 10, 90)));
    }

    // ── Inline fragment reattachment ───────────────────────────────────────────

    [Fact]
    public void DetachedSubscriptsAreReinsertedIntoAChemicalFormula()
    {
        Assert.Equal("H2SO4 solution", Assemble(
            Span("H", 100, 100, 6, 10, 10),
            Span("SO", 108, 100, 12, 10, 10),
            Span("solution", 124, 100, 36, 10, 10),
            Span("2", 106, 96, 2, 6, 6),
            Span("4", 120, 96, 2, 6, 6)));
    }

    [Fact]
    public void ADetachedFinalGlyphIsReinsertedIntoItsWord()
    {
        Assert.Equal("elit\n\nTable", Assemble(
            Span("eli", 100, 100, 15, 10, 10),
            Span("Table", 40, 75, 25, 10, 10),
            Span("t", 115, 100, 5, 10, 10)));
    }

    [Fact]
    public void ADetachedFragmentOfARotatedWordRejoinsItsParent()
    {
        Assert.Equal("Motorcraft Premium", Assemble(
            Rotated("Motorcraf", 400, 100, 45, 10, 90),
            Rotated("Premium", 400, 155, 40, 10, 90),
            Rotated("t", 400, 145, 5, 10, 90)));
    }

    [Fact]
    public void AFragmentNeverAnchorsAcrossDifferingRotations()
    {
        // The upright parent is not a candidate for the rotated fragment, so the fragment
        // stays where it is and the two are separated by the rotation block break.
        Assert.Equal("Motorcraf\n\nt", Assemble(
            Span("Motorcraf", 400, 100, 45, 10, 10),
            Rotated("t", 445, 100, 5, 10, 90)));
    }

    [Fact]
    public void ALoneArticleIsAWordRatherThanADetachedGlyph()
    {
        Assert.Equal("word a", Assemble(
            Span("word", 100, 100, 30, 10, 10),
            Span("a", 133, 100, 5, 10, 10)));
    }

    // ── Glyph fragmentation rebuild ────────────────────────────────────────────

    /// <summary>Single-char spans whose x resets left on a shared baseline: the per-glyph
    /// BT/ET signature the rebuild exists for.</summary>
    private static List<OxTextSpan> DisorderSpans(int count)
    {
        const float fontSize = 12.0f;
        var spans = new List<OxTextSpan>();
        float x = 300.0f;
        for (int i = 0; i <= count; i++)
        {
            spans.Add(Span("A", x, 700, 0, 0, fontSize));
            x -= fontSize + 1.0f;
        }
        return spans;
    }

    [Fact]
    public void FragmentationFiresAtTheDisorderThresholdAndNotBelowIt()
    {
        // At the threshold the page is rebuilt from positions: left-to-right on one line.
        Assert.Equal("A A A A", OxPageAssembler.Assemble(DisorderSpans(3), 612.0f).Text);
        // One event short, the ordinary assembler runs and emits the spans as they stand —
        // right-to-left on the page, which is exactly the damage the rebuild exists to undo.
        Assert.Equal("AAA", OxPageAssembler.Assemble(DisorderSpans(2), 612.0f).Text);
    }

    [Fact]
    public void TheRebuildGroupsByChainedYProximityAndSpacesByFontSize()
    {
        var spans = new List<OxTextSpan>
        {
            Span("H", 300, 700, 6, 0, 12),
            Span("i", 306, 700, 3, 0, 12),
            Span("t", 200, 700, 3, 0, 12),
            Span("o", 100, 700, 6, 0, 12),
            Span("r", 50, 700, 3, 0, 12),
            Span("y", 50, 680, 6, 0, 12),
        };
        var assembly = OxPageAssembler.Assemble(spans, 612.0f);
        Assert.Equal("r o t Hi\ny", assembly.Text);
        Assert.Equal(new[] { "r o t Hi", "y" }, assembly.Lines.Select(l => l.Text));
    }

    // ── Sparse two-column repair ───────────────────────────────────────────────

    [Fact]
    public void SparseTwoColumnProseIsReorderedByColumn()
    {
        var spans = new List<OxTextSpan>
        {
            Span("The committee reviewed the annual", 60, 712, 175, 11, 11),
            Span("approved the budget for the", 330, 712, 145, 11, 11),
            Span("report and", 60, 698, 52, 11, 11),
            Span("coming fiscal year.", 330, 698, 92, 11, 11),
        };

        Assert.True(OxPageAssembler.ReorderSparseTwoColumnPage(spans, 612.0f));
        Assert.Equal(new[]
        {
            "The committee reviewed the annual",
            "report and",
            "approved the budget for the",
            "coming fiscal year.",
        }, spans.Select(s => s.Text));
    }

    [Fact]
    public void ASparseTableKeepsItsRowOrder()
    {
        // Four prose-shaped spans, but every one ends a sentence: not one sentence
        // continuing across the gutter, so the classifier refuses.
        var spans = new List<OxTextSpan>
        {
            Span("Regional revenue for the northern market.", 60, 712, 210, 11, 11),
            Span("Annual total for the current period.", 330, 712, 190, 11, 11),
            Span("Operating expense for the northern market.", 60, 698, 220, 11, 11),
            Span("Annual cost for the current period.", 330, 698, 185, 11, 11),
        };

        Assert.False(OxPageAssembler.ReorderSparseTwoColumnPage(spans, 612.0f));
    }

    // ── Dense two-column repair ────────────────────────────────────────────────

    private static List<OxTextSpan> DenseTwoColumnSpans()
    {
        const float leftX = 60.0f, rightX = 320.0f;
        string[] leftBody =
        {
            "The committee reviewed annual budget totals",
            "and approved new funding for the coming year",
            "after several rounds of careful review by",
            "senior staff members from every department",
            "who evaluated priorities across the whole",
            "organization before reaching a final decision",
            "that reflected both short and long term goals",
            "for sustainable growth across all programs",
        };
        string[] rightBody =
        {
            "Numerous studies have examined similar",
            "programs across comparable institutions",
            "using consistent methodology and controls",
            "for measuring outcomes over multiple years",
            "researchers found consistent positive trends",
            "supporting continued investment going forward",
            "additional citations appear in the appendix",
            "for readers seeking further detail here",
        };

        var spans = new List<OxTextSpan>
        {
            Span("Funding", leftX, 830, 70, 11, 11),
            Span("References", rightX, 830, 90, 11, 11),
        };
        for (int row = 0; row < leftBody.Length; row++)
        {
            float y = 816.0f - row * 14.0f;
            spans.Add(Span(leftBody[row], leftX, y, 200, 11, 11));
            spans.Add(Span(rightBody[row], rightX, y, 190, 11, 11));
        }
        return spans;
    }

    [Fact]
    public void DenseTwoColumnProseIsReorderedByColumn()
    {
        var spans = DenseTwoColumnSpans();
        Assert.True(OxPageAssembler.ReorderDenseTwoColumnPage(spans, 612.0f));

        var texts = spans.Select(s => s.Text).ToList();
        Assert.Equal("Funding", texts[0]);
        Assert.Equal("for sustainable growth across all programs", texts[8]);
        Assert.Equal("References", texts[9]);
        Assert.Equal("for readers seeking further detail here", texts[17]);
    }

    [Fact]
    public void DenseTwoColumnProseAssemblesWithoutInterleavingOrAHeadingWeld()
    {
        string text = OxPageAssembler.Assemble(DenseTwoColumnSpans(), 612.0f).Text;

        Assert.DoesNotContain("Funding References", text);
        Assert.Contains("The committee reviewed annual budget totals", text);
        Assert.DoesNotContain(
            "budget totals Numerous studies", text.Replace("\n", " "));
    }

    [Fact]
    public void ASingleColumnPageWithWideAndNarrowLinesIsNotSplit()
    {
        // Ragged-right body: every line starts at the same margin and no line carries an
        // internal gap wide enough to read as a gutter.
        var spans = new List<OxTextSpan>();
        for (int row = 0; row < 12; row++)
            spans.Add(Span($"line {row} of an ordinary single column body of prose", 60, 700 - row * 14, 400, 11, 11));

        Assert.False(OxPageAssembler.ReorderDenseTwoColumnPage(spans, 612.0f));
    }

    // ── Line decomposition ─────────────────────────────────────────────────────

    [Fact]
    public void LinesPartitionTheAssembledTextInEmissionOrder()
    {
        var assembly = OxPageAssembler.Assemble(new List<OxTextSpan>
        {
            Span("first", 100, 130, 25, 10, 10),
            Span("line", 128, 130, 20, 10, 10),
            Span("second", 100, 100, 30, 10, 10),
        }, 612.0f);

        Assert.Equal("first line\n\nsecond", assembly.Text);
        Assert.Equal(new[] { "first line", "second" }, assembly.Lines.Select(l => l.Text));
        Assert.Equal(2, assembly.Lines[0].Spans.Count);
    }

    [Fact]
    public void RtlContentIsDetected()
    {
        Assert.True(OxPageAssembler.HasRtlOrBidiContent("مرحبا"));
        Assert.False(OxPageAssembler.HasRtlOrBidiContent("hello"));
    }
}
