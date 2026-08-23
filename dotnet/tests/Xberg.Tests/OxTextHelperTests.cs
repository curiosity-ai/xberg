using System.Text;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Decision-boundary tests for the pdf_oxide text-extraction helpers and the
/// span-merging calibration constants (pdf_oxide-0.3.77 src/extractors/text.rs
/// lines 58-1190, src/config/extraction_profiles.rs).
/// </summary>
public class OxTextHelperTests
{
    // ---- corrected_space_gap (text.rs:934) ----

    [Fact]
    public void CorrectedSpaceGapUninflatesOnlyOverlaps()
    {
        // Unreliable widths + overlap: the fallback 0.55em advance is divided out.
        float corrected = OxTextHelpers.CorrectedSpaceGap(-1.0f, reliableWidths: false, bboxWidth: 10.0f, textEmpty: false);
        Assert.Equal(-1.0f + 10.0f * (1.0f - 1.0f / 1.22f), corrected, 5);
        Assert.True(corrected > -1.0f);
    }

    [Fact]
    public void CorrectedSpaceGapLeavesNonOverlappingLayoutAlone()
    {
        // A zero gap is the "SalesF"+"orce" case — inflating it would tear the word.
        Assert.Equal(0.0f, OxTextHelpers.CorrectedSpaceGap(0.0f, false, 10.0f, false));
        Assert.Equal(2.5f, OxTextHelpers.CorrectedSpaceGap(2.5f, false, 10.0f, false));
    }

    [Fact]
    public void CorrectedSpaceGapRequiresUnreliableWidthsAndNonEmptyText()
    {
        Assert.Equal(-1.0f, OxTextHelpers.CorrectedSpaceGap(-1.0f, reliableWidths: true, bboxWidth: 10.0f, textEmpty: false));
        Assert.Equal(-1.0f, OxTextHelpers.CorrectedSpaceGap(-1.0f, false, bboxWidth: 10.0f, textEmpty: true));
        Assert.Equal(-1.0f, OxTextHelpers.CorrectedSpaceGap(-1.0f, false, bboxWidth: 0.0f, textEmpty: false));
    }

    // ---- starts_with_agl_ligature (text.rs:967) ----

    [Fact]
    public void AglLigatureMatchesBareGlyphOnly()
    {
        Assert.True(OxTextHelpers.StartsWithAglLigature("ﬀ")); // ff
        Assert.True(OxTextHelpers.StartsWithAglLigature("ﬃ")); // ffi
        Assert.True(OxTextHelpers.StartsWithAglLigature("ﬆ")); // st, top of the block
        Assert.True(OxTextHelpers.StartsWithAglLigature("ffi"));
        Assert.True(OxTextHelpers.StartsWithAglLigature("fl"));
    }

    [Fact]
    public void AglLigaturePrefixOfWholeWordIsNotALigatureCluster()
    {
        Assert.False(OxTextHelpers.StartsWithAglLigature("ﬂuid")); // "ﬂuid"
        Assert.False(OxTextHelpers.StartsWithAglLigature("ffective"));
        Assert.False(OxTextHelpers.StartsWithAglLigature("﫿")); // just below the block
        Assert.False(OxTextHelpers.StartsWithAglLigature(string.Empty));
    }

    // ---- is_monospace_font (text.rs:996) ----

    [Fact]
    public void MonospaceFontNamesMatchCaseInsensitively()
    {
        Assert.True(OxTextHelpers.IsMonospaceFont("ABCDEF+Courier-Bold"));
        Assert.True(OxTextHelpers.IsMonospaceFont("CMTT10"));
        Assert.True(OxTextHelpers.IsMonospaceFont("LMMono10-Regular"));
        Assert.True(OxTextHelpers.IsMonospaceFont("Fira Code Retina"));
        Assert.True(OxTextHelpers.IsMonospaceFont("OCR B"));
    }

    [Fact]
    public void MonospaceNearMissesDoNotMatch()
    {
        Assert.False(OxTextHelpers.IsMonospaceFont("Helvetica"));
        // "fira code" is a spaced marker: the unspaced brand name misses.
        Assert.False(OxTextHelpers.IsMonospaceFont("FiraCode-Regular"));
        // The "ocr " marker carries a trailing space, so "OCR-A" misses.
        Assert.False(OxTextHelpers.IsMonospaceFont("OCR-A"));
    }

    // ---- is_pictographic (text.rs:1024) ----

    [Fact]
    public void PictographicCoversSupplementaryPlaneScalars()
    {
        Assert.True(OxTextHelpers.IsPictographic(new Rune(0x1F600))); // grinning face
        Assert.True(OxTextHelpers.IsPictographic(new Rune(0x1FAFF))); // top of Ext-A range
        Assert.True(OxTextHelpers.IsPictographic(new Rune(0x1F0A1))); // ace of spades
        Assert.True(OxTextHelpers.IsPictographic(new Rune(0x2600)));  // black sun
        Assert.True(OxTextHelpers.IsPictographic(new Rune(0xFE0F)));  // VS16
    }

    [Fact]
    public void PictographicExcludesArrowsAndMathSymbols()
    {
        Assert.False(OxTextHelpers.IsPictographic(new Rune(0x2190))); // leftwards arrow
        Assert.False(OxTextHelpers.IsPictographic(new Rune(0x21FF)));
        Assert.False(OxTextHelpers.IsPictographic(new Rune(0x27C0))); // just past Dingbats
        Assert.False(OxTextHelpers.IsPictographic(new Rune(0x1FB00))); // just past Ext-A
        Assert.False(OxTextHelpers.IsPictographic(new Rune('A')));
    }

    // ---- strip_cjk_digit_boundary_spaces (text.rs:1044) ----

    [Fact]
    public void CjkDigitBoundarySpacesAreStripped()
    {
        Assert.Equal("公元前1000年", OxTextHelpers.StripCjkDigitBoundarySpaces("公元前 1000 年"));
        Assert.Equal("10,000年", OxTextHelpers.StripCjkDigitBoundarySpaces("10,000 年"));
    }

    [Fact]
    public void CjkDigitBoundaryStripHandlesSupplementaryPlaneIdeographs()
    {
        // U+20000 is CJK Ext B — a UTF-16 char walk would test the low surrogate
        // instead and leave the space in place.
        Assert.Equal("\U000200005", OxTextHelpers.StripCjkDigitBoundarySpaces("\U00020000 5"));
    }

    [Fact]
    public void HangulDigitAndCjkWordSpacesSurvive()
    {
        // Korean is written with inter-word spaces, so "14 예" is a real boundary.
        Assert.Equal("14 예", OxTextHelpers.StripCjkDigitBoundarySpaces("14 예"));
        // CJK-to-CJK spacing is genuine term spacing.
        Assert.Equal("日本 語", OxTextHelpers.StripCjkDigitBoundarySpaces("日本 語"));
        // No space at all: returned unchanged.
        Assert.Equal("公元前1000年", OxTextHelpers.StripCjkDigitBoundarySpaces("公元前1000年"));
    }

    [Fact]
    public void BracketsHugCjkAndHangulAlike()
    {
        Assert.Equal("고양이(학명)", OxTextHelpers.StripCjkDigitBoundarySpaces("고양이 (학명 )"));
        Assert.Equal("日本[1]", OxTextHelpers.StripCjkDigitBoundarySpaces("日本 [1]"));
    }

    // ---- strip_prime_decimal_boundary_spaces (text.rs:1110) ----

    [Fact]
    public void PrimeDecimalBoundarySpacesAreStripped()
    {
        Assert.Equal("0″.28", OxTextHelpers.StripPrimeDecimalBoundarySpaces("0″ .28"));
        Assert.Equal("0″.28", OxTextHelpers.StripPrimeDecimalBoundarySpaces("0″. 28"));
        Assert.Equal("1′.47", OxTextHelpers.StripPrimeDecimalBoundarySpaces("1′ .47"));
    }

    [Fact]
    public void FeetAndInchesSpacingSurvives()
    {
        // prime -> digit is a genuine measurement boundary, not an artifact.
        Assert.Equal("5′ 6″", OxTextHelpers.StripPrimeDecimalBoundarySpaces("5′ 6″"));
        // A decimal point not preceded by a prime is left alone.
        Assert.Equal("x. 28", OxTextHelpers.StripPrimeDecimalBoundarySpaces("x. 28"));
    }

    // ---- decimal_gap_has_ink (text.rs:1149) ----

    private static readonly OxRect Left = new(0.0f, 0.0f, 10.0f, 10.0f);   // right edge at x=10
    private static readonly OxRect Right = new(20.0f, 0.0f, 10.0f, 10.0f); // gap is [10, 20]

    [Fact]
    public void DecimalGapDetectsASeparatorGlyph()
    {
        OxRect comma = new(14.0f, 0.0f, 2.0f, 3.0f);
        Assert.True(OxTextHelpers.DecimalGapHasInk([Left, Right, comma], Left, Right));
    }

    [Fact]
    public void DecimalGapIgnoresTheBoundingPairAndEdgeTouchingInk()
    {
        Assert.False(OxTextHelpers.DecimalGapHasInk([Left, Right], Left, Right));
        // Ink ending exactly on the gap's left edge is inside the epsilon.
        OxRect touching = new(5.0f, 0.0f, 5.0f, 10.0f);
        Assert.False(OxTextHelpers.DecimalGapHasInk([touching], Left, Right));
    }

    [Fact]
    public void DecimalGapIgnoresInkOutsideTheVerticalBand()
    {
        OxRect below = new(14.0f, -20.0f, 2.0f, 5.0f);
        Assert.False(OxTextHelpers.DecimalGapHasInk([below], Left, Right));
    }

    [Fact]
    public void DecimalGapNarrowerThanTwoEpsilonIsEmpty()
    {
        OxRect adjacent = new(10.005f, 0.0f, 10.0f, 10.0f);
        OxRect intruder = new(10.0f, 0.0f, 0.005f, 10.0f);
        Assert.False(OxTextHelpers.DecimalGapHasInk([intruder], Left, adjacent));
    }

    // ---- gap_has_intervening_glyph (text.rs:1175) ----

    [Fact]
    public void InterveningGlyphNeedsSubstantialGapCoverage()
    {
        // 6pt of a 10pt gap = 60% coverage.
        OxRect subscript = new(12.0f, 0.0f, 6.0f, 8.0f);
        Assert.True(OxTextHelpers.GapHasInterveningGlyph([subscript], Left, Right));

        // 2pt of a 10pt gap = 20%, below the 35% bar.
        OxRect sliver = new(12.0f, 0.0f, 2.0f, 8.0f);
        Assert.False(OxTextHelpers.GapHasInterveningGlyph([sliver], Left, Right));
    }

    [Fact]
    public void InterveningGlyphIgnoresInkExactlyOnTheGapEdge()
    {
        // Right edge lands exactly on gap_start: overlap is 0, not > 0.
        OxRect flushLeft = new(5.0f, 0.0f, 5.0f, 10.0f);
        Assert.False(OxTextHelpers.GapHasInterveningGlyph([flushLeft], Left, Right));

        // Left edge lands exactly on gap_end: overlap is 0 again.
        OxRect flushRight = new(20.0f, 0.0f, 5.0f, 10.0f);
        Assert.False(OxTextHelpers.GapHasInterveningGlyph([flushRight], Left, Right));
    }

    [Fact]
    public void InterveningGlyphIgnoresGapsAtOrBelowHalfAPoint()
    {
        OxRect near = new(10.5f, 0.0f, 10.0f, 10.0f);
        OxRect intruder = new(10.0f, 0.0f, 0.5f, 10.0f);
        Assert.False(OxTextHelpers.GapHasInterveningGlyph([intruder], Left, near));
    }

    // ---- SpaceDecision (text.rs:119) ----

    [Fact]
    public void SpaceDecisionClampsConfidence()
    {
        var yes = OxSpaceDecision.Insert(OxSpaceSource.TjOffset, 1.5f);
        Assert.True(yes.InsertSpace);
        Assert.Equal(OxSpaceSource.TjOffset, yes.Source);
        Assert.Equal(1.0f, yes.Confidence);

        var no = OxSpaceDecision.NoSpace(OxSpaceSource.IntraWordKerning, -0.5f);
        Assert.False(no.InsertSpace);
        Assert.Equal(OxSpaceSource.IntraWordKerning, no.Source);
        Assert.Equal(0.0f, no.Confidence);
    }

    // ---- TextExtractionConfig (text.rs:161) ----

    [Fact]
    public void TextExtractionConfigDefaults()
    {
        var config = OxTextExtractionConfig.New();
        Assert.Null(config.Profile);
        Assert.Equal(-120.0f, config.SpaceInsertionThreshold);
        Assert.Equal(0.1f, config.WordMarginRatio);
        Assert.False(config.UseAdaptiveTjThreshold);
        Assert.Equal(WordBoundaryMode.Tiebreaker, config.WordBoundaryMode);
    }

    [Fact]
    public void TextExtractionConfigBuilders()
    {
        var stat = OxTextExtractionConfig.WithSpaceThreshold(-80.0f);
        Assert.Equal(-80.0f, stat.SpaceInsertionThreshold);
        Assert.Equal(0.1f, stat.WordMarginRatio);
        Assert.False(stat.UseAdaptiveTjThreshold);

        var adaptive = OxTextExtractionConfig.WithWordMarginRatio(0.15f);
        Assert.Equal(-120.0f, adaptive.SpaceInsertionThreshold);
        Assert.Equal(0.15f, adaptive.WordMarginRatio);
        Assert.True(adaptive.UseAdaptiveTjThreshold);

        var set = OxTextExtractionConfig.New().SetWordMarginRatio(0.2f);
        Assert.Equal(0.2f, set.WordMarginRatio);
        Assert.True(set.UseAdaptiveTjThreshold);
        Assert.False(set.SetAdaptiveTjThreshold(false).UseAdaptiveTjThreshold);

        // Builders return copies: the receiver keeps Rust's by-value semantics.
        var baseline = OxTextExtractionConfig.New();
        _ = baseline.SetWordMarginRatio(0.9f);
        Assert.Equal(0.1f, baseline.WordMarginRatio);
    }

    [Fact]
    public void WithProfileAppliesProfileThresholds()
    {
        var config = OxTextExtractionConfig.New().WithProfile(OxExtractionProfile.Academic);
        Assert.Equal(OxExtractionProfile.Academic, config.Profile);
        Assert.Equal(-105.0f, config.SpaceInsertionThreshold);
        Assert.Equal(0.12f, config.WordMarginRatio);
        Assert.True(config.UseAdaptiveTjThreshold);
    }

    // ---- ExtractionProfile (config/extraction_profiles.rs) ----

    [Fact]
    public void ExtractionProfileConstants()
    {
        AssertProfile(OxExtractionProfile.Conservative, "Conservative (Default)", -120.0f, 0.1f, 0.25f, 0.5f, false, false, false, false);
        AssertProfile(OxExtractionProfile.TjHeavy, "TJ-Heavy (Lorem-Ipsum-style PDFs)", -100.0f, 0.1f, 0.25f, 0.5f, false, false, false, false);
        AssertProfile(OxExtractionProfile.Aggressive, "Aggressive", -80.0f, 0.2f, 0.15f, 0.8f, false, false, false, false);
        AssertProfile(OxExtractionProfile.Balanced, "Balanced", -100.0f, 0.15f, 0.2f, 0.65f, false, false, false, false);
        AssertProfile(OxExtractionProfile.Academic, "Academic", -105.0f, 0.12f, 0.18f, 0.6f, true, false, true, true);
        AssertProfile(OxExtractionProfile.Policy, "Policy", -110.0f, 0.18f, 0.22f, 0.7f, true, false, false, false);
        AssertProfile(OxExtractionProfile.Form, "Form", -120.0f, 0.08f, 0.2f, 0.5f, false, false, false, false);
        AssertProfile(OxExtractionProfile.Government, "Government", -105.0f, 0.14f, 0.2f, 0.65f, true, false, false, false);
        AssertProfile(OxExtractionProfile.ScannedOcr, "Scanned OCR", -85.0f, 0.2f, 0.15f, 0.75f, true, false, false, false);
        AssertProfile(OxExtractionProfile.Adaptive, "Adaptive", -100.0f, 0.15f, 0.2f, 0.65f, true, true, false, false);
    }

    [Fact]
    public void ExtractionProfileLookup()
    {
        Assert.Equal(OxExtractionProfile.Academic, OxExtractionProfile.ForDocumentType(OxDocumentType.Academic));
        Assert.Equal(OxExtractionProfile.Balanced, OxExtractionProfile.ForDocumentType(OxDocumentType.Mixed));
        Assert.Equal(OxExtractionProfile.ScannedOcr, OxExtractionProfile.ForDocumentType(OxDocumentType.ScannedOcr));
        Assert.NotNull(OxExtractionProfile.ByName("Academic"));
        Assert.Null(OxExtractionProfile.ByName("InvalidProfile"));
        Assert.Equal(9, OxExtractionProfile.AllProfiles().Length);
    }

    private static void AssertProfile(
        OxExtractionProfile p,
        string name,
        float tjOffset,
        float wordMargin,
        float spaceEm,
        float spaceCharMul,
        bool adaptive,
        bool docTypeDetect,
        bool email,
        bool citation)
    {
        Assert.Equal(name, p.Name);
        Assert.Equal(tjOffset, p.TjOffsetThreshold);
        Assert.Equal(wordMargin, p.WordMarginRatio);
        Assert.Equal(spaceEm, p.SpaceThresholdEmRatio);
        Assert.Equal(spaceCharMul, p.SpaceCharMultiplier);
        Assert.Equal(adaptive, p.UseAdaptiveThreshold);
        Assert.Equal(docTypeDetect, p.EnableDocumentTypeDetection);
        Assert.Equal(email, p.EnableEmailDetection);
        Assert.Equal(citation, p.EnableCitationDetection);
    }

    // ---- SpanMergingConfig (text.rs:445) — the transcription check ----

    [Fact]
    public void SpanMergingDefaultsMatchRust()
    {
        AssertSpanConfig(OxSpanMergingConfig.New(), 0.25f, 0.1f, 5.0f, -0.5f, adaptive: true, hasAdaptiveConfig: false);
    }

    [Fact]
    public void SpanMergingPresetsMatchRust()
    {
        AssertSpanConfig(OxSpanMergingConfig.Aggressive(), 0.15f, 0.1f, 5.0f, -0.5f, adaptive: false, hasAdaptiveConfig: false);
        AssertSpanConfig(OxSpanMergingConfig.Conservative(), 0.33f, 0.3f, 5.0f, -0.5f, adaptive: false, hasAdaptiveConfig: false);
        AssertSpanConfig(OxSpanMergingConfig.Custom(0.2f, 0.2f, 6.0f, -0.3f), 0.2f, 0.2f, 6.0f, -0.3f, adaptive: false, hasAdaptiveConfig: false);
        AssertSpanConfig(OxSpanMergingConfig.Adaptive(), 0.25f, 0.1f, 5.0f, -0.5f, adaptive: true, hasAdaptiveConfig: true);
        AssertSpanConfig(OxSpanMergingConfig.Legacy(), 0.25f, 0.1f, 5.0f, -0.5f, adaptive: false, hasAdaptiveConfig: false);

        var custom = new OxAdaptiveThresholdConfig { MedianMultiplier = 0.5f };
        var withConfig = OxSpanMergingConfig.AdaptiveWithConfig(custom);
        AssertSpanConfig(withConfig, 0.25f, 0.1f, 5.0f, -0.5f, adaptive: true, hasAdaptiveConfig: true);
        Assert.Equal(0.5f, withConfig.AdaptiveConfig!.MedianMultiplier);
    }

    [Fact]
    public void AdaptiveThresholdConfigDefaults()
    {
        var config = new OxAdaptiveThresholdConfig();
        Assert.Equal(1.5f, config.MedianMultiplier);
        Assert.Equal(0.05f, config.MinThresholdPt);
        Assert.Equal(100.0f, config.MaxThresholdPt);
        Assert.False(config.UseIqr);
        Assert.Equal(10, config.MinSamples);
    }

    private static void AssertSpanConfig(
        OxSpanMergingConfig c,
        float spaceEm,
        float conservativePt,
        float columnPt,
        float overlapPt,
        bool adaptive,
        bool hasAdaptiveConfig)
    {
        Assert.Equal(spaceEm, c.SpaceThresholdEmRatio);
        Assert.Equal(conservativePt, c.ConservativeThresholdPt);
        Assert.Equal(columnPt, c.ColumnBoundaryThresholdPt);
        Assert.Equal(overlapPt, c.SevereOverlapThresholdPt);
        Assert.Equal(adaptive, c.UseAdaptiveThreshold);
        Assert.Equal(hasAdaptiveConfig, c.AdaptiveConfig is not null);
        // Every preset shares these; a slip here would silently retune the pipeline.
        Assert.False(c.DetectEmailPatterns);
        Assert.Equal(2.5f, c.EmailThresholdMultiplier);
        Assert.False(c.DetectCitationMarkers);
        Assert.Equal(0.75f, c.CitationFontSizeRatio);
        Assert.True(c.MergeTmTjRuns);
    }
}
