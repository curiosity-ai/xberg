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

    [Fact]
    public void BelowCoverageFloor_ScoresZero()
    {
        // A page with a figure is text with a figure, never a scan — no other signal can
        // lift it, so the codec and producer are deliberately the most incriminating ones.
        Assert.Equal(0.0, PdfScanDetect.ScorePage(
            Signals(0.79, glyphs: 0, codec: ImageCodecClass.Ccitt, prior: ProducerPrior.Scanner)));
    }

    [Fact]
    public void FullPageRasterWithVisibleText_ScoresExactlyTheRasterFloor()
    {
        // A slide with a full-bleed background image: below every usable threshold.
        Assert.Equal(0.50, PdfScanDetect.ScorePage(Signals(1.0)));
    }

    [Fact]
    public void FullPageRasterWithNoTextLayer_AddsTheNoVisibleTextScore()
    {
        Assert.Equal(0.85, PdfScanDetect.ScorePage(Signals(1.0, glyphs: 0)));
    }

    [Fact]
    public void MostlyInvisibleTextLayer_CountsAsAnOcrSidecar()
    {
        Assert.Equal(0.85, PdfScanDetect.ScorePage(Signals(1.0, invisible: 0.50)));
        // Just under the threshold the text layer is treated as real.
        Assert.Equal(0.50, PdfScanDetect.ScorePage(Signals(1.0, invisible: 0.49)));
    }

    [Fact]
    public void BilevelCodecs_AddTheirScore()
    {
        Assert.Equal(0.60, PdfScanDetect.ScorePage(Signals(1.0, codec: ImageCodecClass.Ccitt)));
        Assert.Equal(0.60, PdfScanDetect.ScorePage(Signals(1.0, codec: ImageCodecClass.Jbig2)));
        // A photo or flate raster is not evidence of a fax-style scan.
        Assert.Equal(0.50, PdfScanDetect.ScorePage(Signals(1.0, codec: ImageCodecClass.Dct)));
    }

    [Fact]
    public void ScannerProducer_IsAWeakNudgeAndNeverDecisive()
    {
        Assert.Equal(0.55, PdfScanDetect.ScorePage(Signals(1.0, prior: ProducerPrior.Scanner)));
        // Even every signal together stays within [0, 1].
        Assert.Equal(1.0, PdfScanDetect.ScorePage(
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
