// Tests for the pdf_oxide span post-processing port (`extractors/text.rs`:
// merge_adjacent_spans and the dedup / sort / split passes around it).
//
// Every case builds a synthetic span list with explicit geometry, because the
// merger's branches are chosen entirely by gap-to-font-size ratios. The
// space-insertion decision is reached through the extractor's seam, so each test
// installs the smallest stub that exercises the branch under test.
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

public class OxSpanMergeTests
{
    private static OxTextSpan Span(
        string text, float x, float y, float width, float height = 12.0f,
        float fontSize = 12.0f, string fontName = "F1",
        OxFontWeight weight = OxFontWeight.Normal, bool italic = false,
        int? mcid = null, bool splitBoundary = false, bool offsetSemantic = false,
        float rotation = 0.0f, byte wmode = 0) =>
        new()
        {
            Text = text,
            Bbox = new OxRect(x, y, width, height),
            FontSize = fontSize,
            FontName = fontName,
            FontWeight = weight,
            IsItalic = italic,
            Mcid = mcid,
            SplitBoundaryBefore = splitBoundary,
            OffsetSemantic = offsetSemantic,
            RotationDegrees = rotation,
            Wmode = wmode,
        };

    /// <summary>
    /// Stand-in for `should_insert_space`: a bare geometric threshold, plus the AGL
    /// ligature inflation (text.rs:1438) so the ligature branch can be exercised.
    /// </summary>
    private static OxShouldInsertSpaceFn Geometric(float thresholdPt) =>
        (prev, next, gap, fs, fontName, fonts, tj, cfg, prevBox, nextBox, prevFs, nextFs) =>
        {
            if (tj)
            {
                return OxSpaceDecision.Insert(OxSpaceSource.TjOffset, 0.95f);
            }
            bool ligature = OxTextHelpers.StartsWithAglLigature(next)
                || (prev.Length > 0 && prev[^1] >= 'ﬀ' && prev[^1] <= 'ﬆ');
            float threshold = ligature ? thresholdPt * 1.5f : thresholdPt;
            return gap > threshold
                ? OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.8f)
                : OxSpaceDecision.NoSpace(OxSpaceSource.NoSpace, 0.5f);
        };

    /// <summary>Every boundary suppressed by the intra-word kerning guard — the only source the bimodal rescue may override.</summary>
    private static readonly OxShouldInsertSpaceFn AlwaysKerning =
        (prev, next, gap, fs, fontName, fonts, tj, cfg, prevBox, nextBox, prevFs, nextFs) =>
            OxSpaceDecision.NoSpace(OxSpaceSource.IntraWordKerning, 0.7f);

    private static OxTextExtractor Extractor(
        IEnumerable<OxTextSpan> spans, OxShouldInsertSpaceFn? decide = null,
        OxSpanMergingConfig? config = null)
    {
        var ex = new OxTextExtractor { Spans = spans.ToList() };
        if (decide is not null)
        {
            ex.ShouldInsertSpace = decide;
        }
        if (config is not null)
        {
            ex.MergingConfig = config;
        }
        return ex;
    }

    // ------------------------------------------------------------------
    // merge_adjacent_spans — the word-reconstruction core
    // ------------------------------------------------------------------

    [Fact]
    public void MergeAdjacentSpans_WordGap_MergesWithSpace()
    {
        var ex = Extractor(
            [Span("Hello", 100.0f, 700.0f, 30.0f), Span("world", 134.0f, 700.0f, 30.0f)],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        OxTextSpan only = Assert.Single(ex.Spans);
        Assert.Equal("Hello world", only.Text);
        Assert.Equal(100.0f, only.Bbox.X);
        Assert.Equal(64.0f, only.Bbox.Width);
        Assert.Equal(12.0f, only.Bbox.Height);
    }

    [Fact]
    public void MergeAdjacentSpans_KerningGap_MergesWithoutSpace()
    {
        var ex = Extractor(
            [Span("Intr", 100.0f, 700.0f, 20.0f), Span("oduction", 120.3f, 700.0f, 40.0f)],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        OxTextSpan only = Assert.Single(ex.Spans);
        Assert.Equal("Introduction", only.Text);
        Assert.Equal(100.0f, only.Bbox.X);
        Assert.Equal(60.3f, only.Bbox.Width, 4);
    }

    /// A mid-word split whose fragments slightly overlap (negative gap from
    /// fallback-advance inflation) still rejoins.
    [Fact]
    public void MergeAdjacentSpans_SlightOverlap_RejoinsSplitWord()
    {
        var ex = Extractor(
            [Span("Sales", 100.0f, 700.0f, 26.0f), Span("Force", 125.7f, 700.0f, 28.0f)],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal("SalesForce", Assert.Single(ex.Spans).Text);
    }

    /// Overlap past `severe_overlap_threshold_pt` is a genuine collision, not metric
    /// noise, so the two runs stay separate.
    [Fact]
    public void MergeAdjacentSpans_SevereOverlap_DoesNotMerge()
    {
        var ex = Extractor(
            [Span("Sales", 100.0f, 700.0f, 26.0f), Span("Force", 125.4f, 700.0f, 28.0f)],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal(["Sales", "Force"], ex.Spans.Select(s => s.Text));
    }

    /// A gap wider than the font-size-aware column threshold is a gutter: the runs
    /// belong to different columns and never merge, even inside the merge window.
    [Fact]
    public void MergeAdjacentSpans_ColumnBoundary_DoesNotMerge()
    {
        // 4pt text with a 1pt configured gutter: column threshold = max(1.0, 2.0) = 2.0,
        // merge threshold = max(2.0, 3.0) = 3.0, so a 2.5pt gap is inside the merge
        // window yet past the column boundary — the guard is the only thing rejecting it.
        OxSpanMergingConfig config = OxSpanMergingConfig.Custom(0.25f, 0.1f, 1.0f, -0.5f);

        var wide = Extractor(
            [Span("left", 100.0f, 700.0f, 10.0f, 4.0f, 4.0f), Span("right", 112.5f, 700.0f, 10.0f, 4.0f, 4.0f)],
            Geometric(2.0f), config);
        wide.MergeAdjacentSpans();
        Assert.Equal(["left", "right"], wide.Spans.Select(s => s.Text));

        var narrow = Extractor(
            [Span("left", 100.0f, 700.0f, 10.0f, 4.0f, 4.0f), Span("right", 111.5f, 700.0f, 10.0f, 4.0f, 4.0f)],
            Geometric(2.0f), config);
        narrow.MergeAdjacentSpans();
        Assert.Equal("leftright", Assert.Single(narrow.Spans).Text);
    }

    [Fact]
    public void MergeAdjacentSpans_DifferentMcid_DoesNotMerge()
    {
        var ex = Extractor(
            [Span("Cell", 100.0f, 700.0f, 20.0f, mcid: 3), Span("Next", 121.0f, 700.0f, 20.0f, mcid: 4)],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal(["Cell", "Next"], ex.Spans.Select(s => s.Text));
    }

    /// A ligature emitted as its own cluster between two intra-word fragments
    /// ("di" - U+FB03 - "cult") inflates the geometric threshold at both of its
    /// boundaries, so the surrounding kerning does not read as two word gaps.
    [Fact]
    public void MergeAdjacentSpans_LigatureCluster_SuppressesBothBoundaries()
    {
        var ex = Extractor(
            [
                Span("di", 100.0f, 700.0f, 10.0f),
                Span("ﬃ", 112.5f, 700.0f, 8.0f),
                Span("cult", 123.0f, 700.0f, 18.0f),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal("diﬃcult", Assert.Single(ex.Spans).Text);

        // The same geometry with an ordinary middle cluster keeps both word gaps.
        var control = Extractor(
            [
                Span("di", 100.0f, 700.0f, 10.0f),
                Span("xx", 112.5f, 700.0f, 8.0f),
                Span("cult", 123.0f, 700.0f, 18.0f),
            ],
            Geometric(2.0f));
        control.MergeAdjacentSpans();
        Assert.Equal("di xx cult", Assert.Single(control.Spans).Text);
    }

    /// Split-box amounts: "123456" and "72" in separate fixed-width boxes join as a
    /// decimal when nothing is drawn between the boxes.
    [Fact]
    public void MergeAdjacentSpans_DecimalMerge_EmptyGap()
    {
        var ex = Extractor(
            [
                Span("123456", 100.0f, 700.0f, 40.0f, 10.0f, 10.0f),
                Span("72", 148.0f, 700.0f, 8.0f, 10.0f, 10.0f),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        OxTextSpan only = Assert.Single(ex.Spans);
        Assert.Equal("123456.72", only.Text);
        Assert.Equal(100.0f, only.Bbox.X);
        Assert.Equal(56.0f, only.Bbox.Width);
    }

    /// The comma of a subscript index pair is drawn out of content-stream order, so the
    /// fold sees the digit runs as adjacent; ink inside the gap proves they are distinct
    /// tokens and blocks the fabricated decimal.
    [Fact]
    public void MergeAdjacentSpans_DecimalMerge_InkInGapBlocksIt()
    {
        var ex = Extractor(
            [
                Span("123456", 100.0f, 700.0f, 40.0f, 10.0f, 10.0f),
                Span("72", 148.0f, 700.0f, 8.0f, 10.0f, 10.0f),
                Span(",", 142.0f, 700.0f, 3.0f, 10.0f, 10.0f),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal(["123456", "72", ","], ex.Spans.Select(s => s.Text));
    }

    /// A condensed line typeset with no space glyph: every boundary is suppressed by the
    /// fixed kerning guard, and only the line's own bimodal gap distribution recovers the
    /// one real word gap.
    [Fact]
    public void MergeAdjacentSpans_BimodalRescue_RecoversNarrowWordGap()
    {
        static List<OxTextSpan> Line(float wordGap) =>
        [
            Span("con", 100.0f, 700.0f, 15.0f, 10.0f, 10.0f),
            Span("densed", 115.2f, 700.0f, 30.0f, 10.0f, 10.0f),
            Span("word", 145.2f + wordGap, 700.0f, 20.0f, 10.0f, 10.0f),
            Span("s", 165.4f + wordGap, 700.0f, 5.0f, 10.0f, 10.0f),
        ];

        // Gaps 0.2 / 1.3 / 0.2 at 10pt: the intra-word cluster sits at 0.02em and the
        // word gap at 0.13em, a clean split at 0.75pt.
        var rescued = Extractor(Line(1.3f), AlwaysKerning);
        rescued.MergeAdjacentSpans();
        Assert.Equal("condensed words", Assert.Single(rescued.Spans).Text);

        // Same spans with a uniform 0.2pt gap: unimodal, no threshold, nothing rescued.
        var unimodal = Extractor(Line(0.2f), AlwaysKerning);
        unimodal.MergeAdjacentSpans();
        Assert.Equal("condensedwords", Assert.Single(unimodal.Spans).Text);
    }

    /// A whitespace-only neighbour already carries the separator, so the merger
    /// concatenates it directly: the 2pt gap ahead of it would otherwise clear the
    /// geometric threshold and produce "Hello  world".
    [Fact]
    public void MergeAdjacentSpans_OffsetSemanticSpace_DoesNotDoubleSpace()
    {
        var ex = Extractor(
            [
                Span("Hello", 100.0f, 700.0f, 30.0f),
                Span(" ", 132.0f, 700.0f, 3.0f, offsetSemantic: true),
                Span("world", 135.5f, 700.0f, 30.0f),
            ],
            Geometric(1.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal("Hello world", Assert.Single(ex.Spans).Text);
    }

    /// Drop-cap / single-letter emphasis in another font sits tight against its word and
    /// is glued back without a separator; the longer run's font metadata wins.
    [Fact]
    public void MergeAdjacentSpans_CrossFontWordGlue_KeepsDominantFont()
    {
        var ex = Extractor(
            [
                Span("W", 100.0f, 700.0f, 12.0f, fontName: "Display"),
                Span("ashington", 112.5f, 700.0f, 45.0f, fontName: "Body"),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        OxTextSpan only = Assert.Single(ex.Spans);
        Assert.Equal("Washington", only.Text);
        Assert.Equal("Body", only.FontName);
    }

    /// Simulated small caps: one base font at two sizes, zero gap, both runs
    /// multi-character — glued without a separator.
    [Fact]
    public void MergeAdjacentSpans_SmallCapsGlue_Merges()
    {
        var ex = Extractor(
            [
                Span("PDF", 100.0f, 700.0f, 24.0f, fontSize: 12.0f),
                Span("ormat", 124.2f, 700.0f, 25.0f, 9.0f, 9.0f),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal("PDFormat", Assert.Single(ex.Spans).Text);
    }

    /// A rotated run advances along Y, so the portrait same-line test reads
    /// perpendicular geometry for it; such runs never merge here.
    [Fact]
    public void MergeAdjacentSpans_RotatedRuns_NeverMerge()
    {
        var ex = Extractor(
            [
                Span("row", 100.0f, 700.0f, 20.0f, rotation: 90.0f),
                Span("row", 121.0f, 700.0f, 20.0f, rotation: 90.0f),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal(["row", "row"], ex.Spans.Select(s => s.Text));
    }

    [Fact]
    public void MergeAdjacentSpans_DifferentWritingModes_NeverMerge()
    {
        var ex = Extractor(
            [
                Span("A", 100.0f, 700.0f, 12.0f),
                Span("B", 112.5f, 700.0f, 12.0f, wmode: 1),
            ],
            Geometric(2.0f));

        ex.MergeAdjacentSpans();

        Assert.Equal(["A", "B"], ex.Spans.Select(s => s.Text));
    }

    /// char_widths must stay in positional lockstep with the merged text: the inserted
    /// separator takes the geometric gap it stands in for.
    [Fact]
    public void MergeAdjacentSpans_MaintainsCharWidthsPositionally()
    {
        var left = Span("ab", 100.0f, 700.0f, 20.0f);
        left.CharWidths = [10.0f, 10.0f];
        var right = Span("cd", 124.0f, 700.0f, 20.0f);
        right.CharWidths = [10.0f, 10.0f];

        var ex = Extractor([left, right], Geometric(2.0f));
        ex.MergeAdjacentSpans();

        OxTextSpan only = Assert.Single(ex.Spans);
        Assert.Equal("ab cd", only.Text);
        Assert.Equal([10.0f, 10.0f, 4.0f, 10.0f, 10.0f], only.CharWidths);
    }

    // ------------------------------------------------------------------
    // dedup passes
    // ------------------------------------------------------------------

    /// Stroke and fill render passes of one label land at essentially the same CTM;
    /// without this filter the merger concatenates them into "EverestEverest".
    [Fact]
    public void DedupStrokeFillOverlap_DropsSecondRenderPass()
    {
        var ex = Extractor(
        [
            Span("Everest", 100.0f, 700.0f, 50.0f),
            Span("Everest", 100.1f, 700.2f, 50.0f),
            Span("Everest", 400.0f, 700.0f, 50.0f),
        ]);

        ex.DedupStrokeFillOverlap();

        Assert.Equal(2, ex.Spans.Count);
        Assert.Equal([100.0f, 400.0f], ex.Spans.Select(s => s.Bbox.X));
    }

    [Fact]
    public void DeduplicateOverlappingChars_DropsShadowPass()
    {
        var ex = new OxTextExtractor
        {
            Chars =
            [
                new OxTextChar { Char = 'A', Bbox = new OxRect(100.0f, 700.0f, 6.0f, 10.0f), AdvanceWidth = 6.0f },
                new OxTextChar { Char = 'A', Bbox = new OxRect(100.3f, 700.2f, 6.0f, 10.0f), AdvanceWidth = 6.0f },
                new OxTextChar { Char = 'A', Bbox = new OxRect(106.0f, 700.0f, 6.0f, 10.0f), AdvanceWidth = 6.0f },
            ],
        };

        ex.DeduplicateOverlappingChars();

        Assert.Equal(2, ex.Chars.Count);
        Assert.Equal([100.0f, 106.0f], ex.Chars.Select(c => c.Bbox.X));
    }

    // ------------------------------------------------------------------
    // split_fused_words
    // ------------------------------------------------------------------

    [Fact]
    public void SplitFusedWords_SplitsCamelCaseProportionally()
    {
        var ex = Extractor([Span("theGeneral", 100.0f, 700.0f, 60.0f)]);

        ex.SplitFusedWords();

        Assert.Equal(["the", "General"], ex.Spans.Select(s => s.Text));
        Assert.Equal(100.0f, ex.Spans[0].Bbox.X);
        Assert.Equal(18.0f, ex.Spans[0].Bbox.Width, 4);
        Assert.Equal(118.0f, ex.Spans[1].Bbox.X, 4);
        Assert.Equal(42.0f, ex.Spans[1].Bbox.Width, 4);
        Assert.False(ex.Spans[0].SplitBoundaryBefore);
        Assert.True(ex.Spans[1].SplitBoundaryBefore);
    }

    /// The split boundary survives the merge pass: the fragments rejoin WITH a space
    /// rather than fusing back into "theGeneral".
    [Fact]
    public void SplitFusedWords_ThenMerge_RejoinsWithSpace()
    {
        var ex = Extractor([Span("theGeneral", 100.0f, 700.0f, 60.0f)], Geometric(2.0f));

        ex.SplitFusedWords();
        ex.MergeAdjacentSpans();

        Assert.Equal("the General", Assert.Single(ex.Spans).Text);
    }

    [Fact]
    public void SplitOnCamelcase_LeavesUnfusedTextAlone()
    {
        var ex = new OxTextExtractor();

        Assert.Equal(["General"], ex.SplitOnCamelcase("General"));
        Assert.Equal(["help"], ex.SplitOnCamelcase("help"));
        Assert.Equal(["length", "This"], ex.SplitOnCamelcase("lengthThis"));
    }

    // ------------------------------------------------------------------
    // sorting
    // ------------------------------------------------------------------

    /// Y descending then X ascending, with ties keeping extraction (sequence) order.
    [Fact]
    public void SimpleSortSpans_IsStableWithinARow()
    {
        var a = Span("a", 100.0f, 700.0f, 10.0f);
        a.Sequence = 0;
        var b = Span("b", 100.0f, 700.0f, 10.0f);
        b.Sequence = 1;
        var above = Span("top", 100.0f, 720.0f, 10.0f);
        above.Sequence = 2;

        var ex = Extractor([a, b, above]);
        ex.SimpleSortSpans();

        Assert.Equal(["top", "a", "b"], ex.Spans.Select(s => s.Text));
        Assert.Equal([2, 0, 1], ex.Spans.Select(s => s.Sequence));
    }

    [Fact]
    public void SortSpansByReadingOrder_TwoColumns_ReadsColumnwise()
    {
        var spans = new List<OxTextSpan>();
        for (int line = 0; line < 6; line++)
        {
            float y = 700.0f - line * 14.0f;
            spans.Add(Span($"R{line}", 320.0f, y, 200.0f));
            spans.Add(Span($"L{line}", 50.0f, y, 200.0f));
        }

        var ex = Extractor(spans);
        ex.SortSpansByReadingOrder();

        Assert.Equal(
            ["L0", "L1", "L2", "L3", "L4", "L5", "R0", "R1", "R2", "R3", "R4", "R5"],
            ex.Spans.Select(s => s.Text));
    }

    /// A superscript citation marker keeps its own text rise in the raw bbox, which
    /// would sort a row of affiliation markers ahead of the names they annotate; it is
    /// snapped onto the base span's baseline instead.
    [Fact]
    public void SnapSuperscriptBaselines_SnapsMarkerOntoBase()
    {
        var ex = Extractor(
        [
            Span("Author", 100.0f, 700.0f, 40.0f, 10.0f, 10.0f),
            Span("1", 141.0f, 703.0f, 3.0f, 6.0f, 6.0f),
        ]);

        ex.SnapSuperscriptBaselines();

        Assert.Equal(700.0f, ex.Spans[0].Bbox.Y);
        Assert.Equal(700.0f, ex.Spans[1].Bbox.Y);
        Assert.Equal(141.0f, ex.Spans[1].Bbox.X);
    }

    /// Glyphs placed at decreasing x on one baseline mean the producer stored RTL text in
    /// logical order and positioned each glyph itself, so the run must not be reversed
    /// again downstream.
    [Fact]
    public void DetectRtlDrawDirection_MarksRightToLeftPlacement()
    {
        var ex = Extractor(
        [
            Span("\u0633", 140.0f, 700.0f, 8.0f),
            Span("\u0631", 130.0f, 700.0f, 8.0f),
            Span("\u062F", 120.0f, 700.0f, 8.0f),
        ]);

        ex.DetectRtlDrawDirection();

        Assert.All(ex.Spans, s => Assert.True(s.RtlDrawLogical));

        // Left-to-right placement of the same glyphs is visual storage and stays unmarked.
        var visual = Extractor(
        [
            Span("\u0633", 120.0f, 700.0f, 8.0f),
            Span("\u0631", 130.0f, 700.0f, 8.0f),
            Span("\u062F", 140.0f, 700.0f, 8.0f),
        ]);
        visual.DetectRtlDrawDirection();
        Assert.All(visual.Spans, s => Assert.False(s.RtlDrawLogical));
    }

    [Fact]
    public void BimodalGapSplit_NeedsThreeGapsAndACleanBorder()
    {
        Assert.Null(OxTextExtractor.BimodalGapSplit([0.2f, 1.3f], 10.0f));
        Assert.Null(OxTextExtractor.BimodalGapSplit([0.2f, 0.2f, 0.2f], 10.0f));
        Assert.Equal(0.75f, OxTextExtractor.BimodalGapSplit([0.2f, 1.3f, 0.2f], 10.0f));
    }
}
