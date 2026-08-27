using Xberg.Internal.Pdf;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Scoring rules from Rust <c>pdf/scan_detect.rs</c>. The score is pure, so these pin the
/// thresholds without needing a PDF.
/// </summary>
public class PdfScanDetectTests
{
    private static PageScanSignals Signals(
        double coverage, int glyphs = 100, double invisible = 0.0,
        ImageCodecClass codec = ImageCodecClass.Other,
        ProducerPrior prior = ProducerPrior.Unknown) =>
        new()
        {
            ImageCoverage = coverage,
            GlyphCount = glyphs,
            InvisibleTextRatio = invisible,
            Codec = codec,
            ProducerPrior = prior,
        };

    /// <summary>
    /// A score is a sum of single-precision weights, so 0.50 + 0.35 + 0.10 lands a few ULPs off
    /// 0.95. Compare within tolerance rather than rounding the score — the same helper upstream's
    /// own tests use, for the same reason.
    /// </summary>
    private static void AssertScore(double expected, double actual) =>
        Assert.True(Math.Abs(actual - expected) < 1e-5, $"expected score {expected}, got {actual}");

    [Fact]
    public void BelowCoverageFloor_ScoresZero()
    {
        // A page with a figure is text with a figure, never a scan — no other signal can
        // lift it, so the codec and producer are deliberately the most incriminating ones.
        AssertScore(0.0, PdfScanDetect.ScorePage(
            Signals(0.79, glyphs: 0, codec: ImageCodecClass.Ccitt, prior: ProducerPrior.Scanner)));
    }

    [Fact]
    public void FullPageRasterWithVisibleText_ScoresExactlyTheRasterFloor()
    {
        // A slide with a full-bleed background image: below every usable threshold.
        AssertScore(0.50, PdfScanDetect.ScorePage(Signals(1.0)));
    }

    [Fact]
    public void FullPageRasterWithNoTextLayer_AddsTheNoVisibleTextScore()
    {
        AssertScore(0.85, PdfScanDetect.ScorePage(Signals(1.0, glyphs: 0)));
    }

    [Fact]
    public void MostlyInvisibleTextLayer_CountsAsAnOcrSidecar()
    {
        AssertScore(0.85, PdfScanDetect.ScorePage(Signals(1.0, invisible: 0.50)));
        // Just under the threshold the text layer is treated as real.
        AssertScore(0.50, PdfScanDetect.ScorePage(Signals(1.0, invisible: 0.49)));
    }

    [Fact]
    public void BilevelCodecs_AddTheirScore()
    {
        AssertScore(0.60, PdfScanDetect.ScorePage(Signals(1.0, codec: ImageCodecClass.Ccitt)));
        AssertScore(0.60, PdfScanDetect.ScorePage(Signals(1.0, codec: ImageCodecClass.Jbig2)));
        // A photo or flate raster is not evidence of a fax-style scan.
        AssertScore(0.50, PdfScanDetect.ScorePage(Signals(1.0, codec: ImageCodecClass.Dct)));
    }

    [Fact]
    public void ScannerProducer_IsAWeakNudgeAndNeverDecisive()
    {
        AssertScore(0.55, PdfScanDetect.ScorePage(Signals(1.0, prior: ProducerPrior.Scanner)));
        // Even every signal together stays within [0, 1].
        AssertScore(1.0, PdfScanDetect.ScorePage(
            Signals(1.0, glyphs: 0, codec: ImageCodecClass.Ccitt, prior: ProducerPrior.Scanner)));
    }

    [Fact]
    public void ScannedPageNumbers_AreOneBasedAndThresholded()
    {
        var detection = new ScanDetection { PageConfidence = { 0.85, 0.5, 0.9 } };
        Assert.Equal(new uint[] { 1, 3 }, detection.ScannedPageNumbers(0.70));
        Assert.Equal(new uint[] { 1, 2, 3 }, detection.ScannedPageNumbers(0.50));
        Assert.Empty(detection.ScannedPageNumbers(0.95));
    }
}

/// <summary>
/// serde writes a fractional part for every float, so an integral value is <c>0.0</c>, not
/// <c>0</c>. Golden comparison is string-exact, so this is a correctness rule, not cosmetics.
/// </summary>
public class SerdeFloatTests
{
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(83.0, "83.0")]
    [InlineData(-1.0, "-1.0")]
    [InlineData(0.85, "0.85")]
    [InlineData(0.5, "0.5")]
    public void IntegralValues_KeepAFractionalPart(double value, string expected) =>
        Assert.Equal(expected, SerdeFloat.Format(value));

    [Fact]
    public void Exponents_UseSerdeSpelling()
    {
        // .NET writes `1E+30` / `1E-07`; serde writes `1e30` / `1e-7`.
        Assert.Equal("1e30", SerdeFloat.Format(1e30));
        Assert.Equal("1e-7", SerdeFloat.Format(1e-7));
    }

    [Fact]
    public void PdfMetadata_SerializesScannedConfidenceWithADecimalPoint()
    {
        string json = Json.Serialize(new PdfMetadata { ScannedConfidence = 0f, ScannedPages = new List<uint>() });
        Assert.Contains("\"scanned_confidence\":0.0", json);
        Assert.Contains("\"scanned_pages\":[]", json);
    }
}

/// <summary>Element-level text repair from Rust <c>pdf/structure/text_repair.rs</c>.</summary>
public class PdfTextRepairTests
{
    [Fact]
    public void LigatureGlyphs_ExpandToAscii() =>
        Assert.Equal("efficient offline", PdfTextRepair.ExpandLigaturesWithSpaceAbsorption("eﬃcient oﬀline"));

    [Fact]
    public void ASpuriousSpaceAfterALigature_IsAbsorbed()
    {
        Assert.Equal("the first floor", PdfTextRepair.ExpandLigaturesWithSpaceAbsorption("the ﬁ rst ﬂ oor"));
        // Only when a word actually continues: a real space before punctuation stays.
        Assert.Equal("fi .", PdfTextRepair.ExpandLigaturesWithSpaceAbsorption("ﬁ ."));
    }

    [Fact]
    public void TextWithoutLigatures_IsReturnedUnchanged()
    {
        const string s = "nothing to do here";
        Assert.Same(s, PdfTextRepair.ExpandLigaturesWithSpaceAbsorption(s));
    }

    [Fact]
    public void UnicodePunctuation_NormalizesToAscii() =>
        Assert.Equal("\"quoted\" and 'single' 1/2 ·",
            PdfTextRepair.NormalizeUnicodeText("“quoted” and ‘single’ 1⁄2 •"));

    [Fact]
    public void ContextualLigatures_RepairOnlyBetweenLetters()
    {
        Assert.Equal("different", PdfTextRepair.RepairContextualLigatures("di!erent"));
        Assert.Equal("efficient", PdfTextRepair.RepairContextualLigatures("e\"cient"));
        Assert.Equal("financial", PdfTextRepair.RepairContextualLigatures("#nancial"));
        // A sentence-final exclamation mark looks identical to the corrupted form, so it is
        // deliberately left alone.
        Assert.Equal("Wow!", PdfTextRepair.RepairContextualLigatures("Wow!"));
        Assert.Equal("Hello! World", PdfTextRepair.RepairContextualLigatures("Hello! World"));
    }
}

/// <summary>
/// Heading-level inference from Rust <c>pdf/structure/classify.rs</c>. These pin the rule the
/// port previously hardcoded to level 2 for every bold or section-numbered heading.
/// </summary>
public class PdfHeadingLevelTests
{
    [Theory]
    // Numbering depth sets the level, and a trailing dot is not a nesting level.
    [InlineData("1 Introduction", 2)]
    [InlineData("1. Introduction", 2)]
    [InlineData("1.1 Details", 3)]
    [InlineData("1.1. Details", 3)]
    [InlineData("1.1.1 Deep", 4)]
    [InlineData("2.3.4.5 Deeper still", 4)]
    // Roman and alphabetic prefixes are top-level sections.
    [InlineData("I. INTRO", 2)]
    [InlineData("IV. Results", 2)]
    [InlineData("A. Proofs", 2)]
    // No numbering at all falls back to a top-level section.
    [InlineData("Introduction", 2)]
    public void SectionLevel_FollowsNumberingDepth(string text, int expected) =>
        Assert.Equal((byte)expected, PdfStructure.InferSectionLevel(text));

    [Fact]
    public void BoldHeadingLevel_UsesTheRiseAboveBodyText()
    {
        // Comfortably above body size is a section heading; barely above is a sub-heading.
        Assert.Equal((byte)2, PdfStructure.InferBoldHeadingLevel(14f, 10f, "Results"));
        Assert.Equal((byte)3, PdfStructure.InferBoldHeadingLevel(11f, 10f, "Results"));
    }

    [Fact]
    public void BoldHeadingLevel_WithoutABodyBaseline_IsASectionNotATitle()
    {
        // A document with no body font to compare against must not mint an H1.
        Assert.Equal((byte)2, PdfStructure.InferBoldHeadingLevel(22f, 0f, "Dummy PDF file"));
    }

    [Fact]
    public void BoldHeadingLevel_SectionNumberingWinsOverFontSize()
    {
        // Numbering is stronger evidence of depth than the font rise.
        Assert.Equal((byte)3, PdfStructure.InferBoldHeadingLevel(30f, 10f, "1.1 Details"));
    }
}

/// <summary>
/// Rotated-page reassembly from Rust <c>extractors/pdf/rotation.rs</c>. Word gaps in a rotated
/// run live on that run's own baseline, not on page-x, so page-order concatenation both glues
/// words and reads them out of order.
/// </summary>
public class PdfRotationRepairTests
{
    private static TextSpan RotatedWord(string text, double y, double width) =>
        new() { Text = text, X = 100, Y = y, Width = width, Height = 10, FontSize = 10, RotationDegrees = 90 };

    private static TextSpan UprightWord(string text, double x, double y, double width) =>
        new() { Text = text, X = x, Y = y, Width = width, Height = 10, FontSize = 10 };

    /// <summary>Six words whose correct reading order runs along ascending advance axis
    /// (page-y for a 90° run), with real 3pt word gaps between them.</summary>
    private static List<TextSpan> ScrambledRotatedSentence() => new()
    {
        RotatedWord("Engine", 0, 36), RotatedWord("oil", 39, 18), RotatedWord("need", 60, 24),
        RotatedWord("only", 87, 24), RotatedWord("meet", 114, 24), RotatedWord("the", 141, 18),
    };

    [Fact]
    public void RotatedRun_ReadsAlongItsAdvanceAxisAndSpacesWordGaps()
    {
        var spans = ScrambledRotatedSentence();
        // Fed back-to-front, exactly the garbled order the real fixture produces.
        var order = new List<int> { 5, 4, 3, 2, 1, 0 };
        Assert.Equal("Engine oil need only meet the",
            PdfRotationRepair.AssembleReadingOrderText(spans, order));
    }

    [Fact]
    public void KerningTightRotatedFragments_GlueRatherThanSpace()
    {
        // Two fragments of one word 0.5pt apart on a 10pt run — well under the 1.5pt cutoff.
        var spans = new List<TextSpan> { RotatedWord("Eng", 0, 18), RotatedWord("ine", 18.5, 18) };
        Assert.Equal("Engine", PdfRotationRepair.AssembleReadingOrderText(spans, new List<int> { 0, 1 }));
    }

    [Fact]
    public void AnUprightPage_IsNeverRewritten()
    {
        // The safety property the whole repair rests on: no rotation, no repair, no drift.
        var spans = new List<TextSpan> { UprightWord("Hello", 10, 700, 30), UprightWord("world", 45, 700, 30) };
        Assert.Null(PdfRotationRepair.RepairRotatedPageText(spans));
    }

    [Fact]
    public void ASingleRotatedLabelOnAnUprightPage_DoesNotTriggerTheWholePageRewrite()
    {
        // Rotation below the dominance share: repairing a three-character tab would cost the
        // upright majority its paragraph structure, which is the worse trade.
        var spans = new List<TextSpan>
        {
            UprightWord("Plenty of ordinary upright prose across the page", 10, 700, 200),
            UprightWord("and a second full line of upright body text too", 10, 686, 200),
            RotatedWord("2.1", 500, 12),
        };
        Assert.Null(PdfRotationRepair.RepairRotatedPageText(spans));
    }

    [Fact]
    public void ADominantlyRotatedPage_IsRepaired()
    {
        var spans = ScrambledRotatedSentence();
        Assert.Equal("Engine oil need only meet the", PdfRotationRepair.RepairRotatedPageText(spans));
    }

    [Fact]
    public void UprightRunsInARepairedPage_KeepTheirLineBreaks()
    {
        // The repair replaces the whole page, so an upright run inside it must still come out
        // with the separators the page assembler would have given it — concatenating verbatim
        // welds every line of a narrow column to the next.
        var spans = new List<TextSpan>
        {
            UprightWord("Trained as an", 522, 766, 47),
            UprightWord("illustrator but", 522, 755, 49),
            UprightWord("working as an art", 522, 744, 61),
            RotatedWord("Usage is obtained by payment of licensing fees.", 100, 200),
        };
        Assert.Equal(
            "Trained as an\nillustrator but\nworking as an art Usage is obtained by payment of licensing fees.",
            PdfRotationRepair.RepairRotatedPageText(spans));
    }

    [Fact]
    public void UprightSpansSharingABaseline_AreSpacedByTheirGapNotGlued()
    {
        // Same row, a real word gap between them: a space, not a line break and not a weld.
        var spans = new List<TextSpan>
        {
            UprightWord("MICHAEL", 380, 763, 60),
            UprightWord("TRINSEY", 445, 763, 60),
            RotatedWord("Usage is obtained by payment of licensing fees.", 100, 200),
        };
        Assert.StartsWith("MICHAEL TRINSEY", PdfRotationRepair.RepairRotatedPageText(spans));
    }

    [Fact]
    public void UprightSpansAcrossAParagraphGap_AreSeparatedByABlankLine()
    {
        var spans = new List<TextSpan>
        {
            UprightWord("Heading of the section", 40, 700, 100),
            UprightWord("Body text well below it", 40, 640, 100),
            RotatedWord("Usage is obtained by payment of licensing fees.", 100, 200),
        };
        Assert.StartsWith("Heading of the section\n\nBody text well below it",
            PdfRotationRepair.RepairRotatedPageText(spans));
    }
}

/// <summary>
/// Two-column repair from Rust <c>pdf/oxide/text.rs</c> and the region classifier gating it.
/// </summary>
public class PdfColumnReorderTests
{
    /// <summary>A prose line: one wide span of <paramref name="chars"/> characters.</summary>
    private static TextSpan Line(string text, double x, double y, double width) =>
        new() { Text = text, X = x, Y = y, Width = width, Height = 10, FontSize = 10 };

    private static List<TextSpan> TwoColumnPage(int rows)
    {
        // A 612pt page: left column at x=40 (240 wide), right at x=330, gutter 50pt.
        var spans = new List<TextSpan>();
        for (int r = 0; r < rows; r++)
        {
            double y = 700 - r * 14;
            spans.Add(Line($"left row {r} carrying a substantial amount of prose text", 40, y, 240));
            spans.Add(Line($"right row {r} carrying a substantial amount of prose text", 330, y, 240));
        }
        return spans;
    }

    [Fact]
    public void ADenseTwoColumnPage_IsReadColumnMajor()
    {
        var spans = TwoColumnPage(rows: 8);
        Assert.True(PdfColumnReorder.ReorderDenseTwoColumnPage(spans, 612));

        // Every left-column span must now precede every right-column span.
        int lastLeft = spans.FindLastIndex(s => s.X < 300);
        int firstRight = spans.FindIndex(s => s.X >= 300);
        Assert.True(lastLeft < firstRight, "columns must not interleave after the repair");
    }

    [Fact]
    public void ASingleColumnPage_IsLeftAlone()
    {
        var spans = new List<TextSpan>();
        for (int r = 0; r < 10; r++)
            spans.Add(Line($"full width row {r} of ordinary prose running right across the page",
                40, 700 - r * 14, 530));
        Assert.False(PdfColumnReorder.ReorderDenseTwoColumnPage(spans, 612));
    }

    [Fact]
    public void ATooNarrowPage_IsLeftAlone()
    {
        // Below the content-width floor there is no room for two columns at all.
        var spans = TwoColumnPage(rows: 8).Where(s => s.X < 300).ToList();
        foreach (var s in spans) s.Width = 80;
        Assert.False(PdfColumnReorder.ReorderDenseTwoColumnPage(spans, 612));
    }

    [Fact]
    public void ShortCellGrid_ClassifiesAsTableNotProse()
    {
        // Digit-only cells: mean characters per line falls below the prose floor, so the
        // reorder gate must reject it rather than corrupt cell ordering.
        var spans = new List<TextSpan>();
        for (int r = 0; r < 8; r++)
            spans.Add(Line($"{r}", 40, 700 - r * 14, 20));
        var indices = Enumerable.Range(0, spans.Count).ToList();
        Assert.Equal(RegionClass.Table, PdfRegionClassifier.Classify(spans, indices));
        Assert.False(PdfRegionClassifier.Classify(spans, indices).IsReorderableColumn());
    }

    [Fact]
    public void NumberedEntries_ClassifyAsReferenceAndAreReorderable()
    {
        var spans = new List<TextSpan>();
        for (int r = 0; r < 8; r++)
            spans.Add(Line($"[{r + 1}] A cited work with a reasonably long title and authors",
                40, 700 - r * 14, 240));
        var cls = PdfRegionClassifier.Classify(spans, Enumerable.Range(0, spans.Count).ToList());
        Assert.Equal(RegionClass.Reference, cls);
        Assert.True(cls.IsReorderableColumn());
    }

    [Fact]
    public void TooFewLines_StayMixedSoCallersKeepPriorBehaviour()
    {
        var spans = new List<TextSpan> { Line("A heading", 40, 700, 100), Line("A caption", 40, 686, 100) };
        Assert.Equal(RegionClass.Mixed, PdfRegionClassifier.Classify(spans, new List<int> { 0, 1 }));
    }
}
