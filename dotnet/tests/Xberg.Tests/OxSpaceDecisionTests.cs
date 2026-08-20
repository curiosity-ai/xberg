using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Decision-boundary tests for the span merger's space rule (pdf_oxide-0.3.77
/// src/extractors/text.rs lines 1190-1857). The rule chain returns early, so each test
/// asserts the reported <see cref="OxSpaceSource"/> as well as the boolean — the source
/// is what identifies which rule fired, and downstream passes key off it.
/// </summary>
public class OxSpaceDecisionTests
{
    /// <summary>
    /// Stands in for the page's font map. A name it does not carry answers null, which is
    /// upstream's <c>contains_key</c> miss.
    /// </summary>
    private sealed class Fonts : IOxSpanFonts
    {
        private readonly Dictionary<string, float> _widths;
        internal Fonts(params (string Name, float Width)[] fonts) =>
            _widths = fonts.ToDictionary(f => f.Name, f => f.Width);
        public float? SpaceGlyphWidth(string fontName) =>
            _widths.TryGetValue(fontName, out float w) ? w : null;
    }

    // A 250/1000-em space glyph at 10pt is 2.5pt, so the proportional threshold
    // (0.5 x space width) is 1.25pt and the kerning-guard ceiling (1.5 x that) is 1.875pt.
    private const string Known = "Helvetica";
    private const string Unknown = "NotOnThisPage";
    private const float Size = 10.0f;
    private static readonly Fonts PageFonts = new((Known, 250.0f));

    private static OxSpaceDecision Decide(
        string preceding,
        string following,
        float gapPt,
        bool tj = false,
        string fontName = Known,
        OxSpanMergingConfig? config = null,
        OxRect? prevBbox = null,
        OxRect? nextBbox = null,
        float prevFontSize = Size,
        float nextFontSize = Size) =>
        OxSpaceDecisionRules.ShouldInsertSpace(
            preceding, following, gapPt, Size, fontName, PageFonts, tj,
            config ?? OxSpanMergingConfig.New(), prevBbox, nextBbox, prevFontSize, nextFontSize);

    // ---- Rule 0: boundary whitespace (text.rs:1216) ----

    [Fact]
    public void BoundaryWhitespaceOutranksEveryGeometricSignal()
    {
        // A gap far past every threshold still must not double the producer's own space.
        var trailing = Decide("foo ", "bar", gapPt: 50.0f, tj: true);
        Assert.False(trailing.InsertSpace);
        Assert.Equal(OxSpaceSource.AlreadyPresent, trailing.Source);
        Assert.Equal(1.0f, trailing.Confidence);

        var leading = Decide("foo", " bar", gapPt: 50.0f, tj: true);
        Assert.False(leading.InsertSpace);
        Assert.Equal(OxSpaceSource.AlreadyPresent, leading.Source);
    }

    [Fact]
    public void HasBoundarySpaceLooksOnlyAtTheTwoAdjacentScalars()
    {
        Assert.True(OxSpaceDecisionRules.HasBoundarySpace("foo\t", "bar"));
        Assert.True(OxSpaceDecisionRules.HasBoundarySpace("foo", "\nbar"));
        Assert.False(OxSpaceDecisionRules.HasBoundarySpace("f oo", "ba r"));
        Assert.False(OxSpaceDecisionRules.HasBoundarySpace("", ""));
    }

    // ---- Rule 0.4: pictograph to letter (text.rs:1246) ----

    [Fact]
    public void APictographAbuttingALetterKeepsItsSpace()
    {
        // U+1F4C4 is non-BMP: read as UTF-16 units the boundary scalar is a lone surrogate
        // and the rule never fires.
        var d = Decide("\U0001F4C4", "README", gapPt: 0.0f);
        Assert.True(d.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, d.Source);
        Assert.Equal(0.85f, d.Confidence);

        // Overlapping glyphs are a positioning artefact, not a token boundary.
        Assert.NotEqual(0.85f, Decide("\U0001F4C4", "README", gapPt: -0.1f).Confidence);
    }

    // ---- Rule 1: TJ offset (text.rs:1611, 1624) ----

    [Fact]
    public void ATjOffsetThatTriggersReportsTjOffsetWhereGeometryAloneWouldNot()
    {
        // 1.5pt clears the 1.25pt threshold, so both signals agree: full confidence.
        var consensus = Decide("AB", "CD", gapPt: 1.5f, tj: true);
        Assert.True(consensus.InsertSpace);
        Assert.Equal(OxSpaceSource.TjOffset, consensus.Source);
        Assert.Equal(1.0f, consensus.Confidence);

        // Same geometry without the offset is the strong-geometric rule instead.
        var geometryOnly = Decide("AB", "CD", gapPt: 1.5f);
        Assert.True(geometryOnly.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, geometryOnly.Source);
        Assert.Equal(0.95f, geometryOnly.Confidence);
    }

    [Fact]
    public void ATjOffsetAcceptsHalfTheGeometricBarButNotLess()
    {
        // 0.8pt misses 1.25pt but clears the relaxed 0.625pt bar tight typesetting needs.
        var relaxed = Decide("AB", "CD", gapPt: 0.8f, tj: true);
        Assert.True(relaxed.InsertSpace);
        Assert.Equal(OxSpaceSource.TjOffset, relaxed.Source);
        Assert.Equal(0.9f, relaxed.Confidence);

        // 0.5pt misses even that, and with no bboxes the tiebreaker cannot run.
        var tooTight = Decide("AB", "CD", gapPt: 0.5f, tj: true);
        Assert.False(tooTight.InsertSpace);
        Assert.Equal(OxSpaceSource.NoSpace, tooTight.Source);
    }

    // ---- Rule 7: strong geometric signal (text.rs:1670) ----

    [Fact]
    public void TheGeometricThresholdIsHalfTheFontsOwnSpaceGlyph()
    {
        var above = Decide("AB", "CD", gapPt: 1.26f);
        Assert.True(above.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, above.Source);
        Assert.Equal(0.95f, above.Confidence);

        var below = Decide("AB", "CD", gapPt: 1.24f);
        Assert.False(below.InsertSpace);
        Assert.Equal(OxSpaceSource.NoSpace, below.Source);
        Assert.Equal(1.0f, below.Confidence);
    }

    [Fact]
    public void AnUnknownFontFallsBackToAQuarterOfTheFontSize()
    {
        // No space-glyph advance to measure against, so the threshold is 10pt x 0.25.
        var above = Decide("AB", "CD", gapPt: 2.6f, fontName: Unknown);
        Assert.True(above.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, above.Source);

        var below = Decide("AB", "CD", gapPt: 2.4f, fontName: Unknown);
        Assert.False(below.InsertSpace);
        Assert.Equal(OxSpaceSource.NoSpace, below.Source);
    }

    // ---- Rule 3: intra-word kerning guard (text.rs:1495) ----

    [Fact]
    public void TheKerningGuardSuppressesANarrowLowercaseGapThatGeometryWouldAccept()
    {
        // 1.5pt is past the 1.25pt threshold but under the 1.875pt ceiling, and both sides
        // are lowercase — the "cha"+"nge" shape TJ-heavy producers emit mid-word.
        var guarded = Decide("cha", "nge", gapPt: 1.5f);
        Assert.False(guarded.InsertSpace);
        Assert.Equal(OxSpaceSource.IntraWordKerning, guarded.Source);
        Assert.Equal(0.9f, guarded.Confidence);

        // Capitals are real word boundaries often enough that they must reach consensus.
        var uppercase = Decide("CHA", "NGE", gapPt: 1.5f);
        Assert.True(uppercase.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, uppercase.Source);
    }

    [Fact]
    public void TheKerningGuardIsOffWhenTheFontIsUnknown()
    {
        // 2.6pt sits under the 3.75pt ceiling the guard would use, so only the missing
        // font keeps the space: the 0.25em fallback is already conservative enough.
        var d = Decide("cha", "nge", gapPt: 2.6f, fontName: Unknown);
        Assert.True(d.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, d.Source);
        Assert.Equal(0.95f, d.Confidence);
    }

    // ---- Rule 6: WordBoundaryDetector tiebreaker (text.rs:1638) ----

    [Fact]
    public void ACjkSentenceMarkResolvesTheTiebreakerWhenTjAndGeometryDisagree()
    {
        // TJ says break, geometry does not; the detector classifies the pair as CJK and
        // the ideographic full stop is an unambiguous boundary.
        var d = Decide(
            "。", "次", gapPt: 0.5f, tj: true,
            prevBbox: new OxRect(0.0f, 100.0f, 10.0f, 10.0f),
            nextBbox: new OxRect(10.5f, 100.0f, 10.0f, 10.0f));

        Assert.True(d.InsertSpace);
        Assert.Equal(OxSpaceSource.WordBoundaryAnalysis, d.Source);
        Assert.Equal(0.85f, d.Confidence);
    }

    [Fact]
    public void TheTiebreakerNeedsBothBoxesAndDisagreeingSignals()
    {
        // Same pair without bboxes: the tiebreaker cannot be consulted at all.
        Assert.Equal(OxSpaceSource.NoSpace, Decide("。", "次", gapPt: 0.5f, tj: true).Source);

        // Same pair with the signals agreeing (neither fires): no tiebreaker either.
        Assert.Equal(
            OxSpaceSource.NoSpace,
            Decide(
                "。", "次", gapPt: 0.5f,
                prevBbox: new OxRect(0.0f, 100.0f, 10.0f, 10.0f),
                nextBbox: new OxRect(10.5f, 100.0f, 10.0f, 10.0f)).Source);
    }

    [Fact]
    public void BoundaryCharacterWidthsSpreadTheBoxOverTheUtf8ByteCount()
    {
        // Upstream uses byte length as an O(1) stand-in for character count, so a two-glyph
        // CJK run counts as six.
        (List<CharacterInfo> chars, BoundaryContext ctx) = OxSpaceDecisionRules.BuildBoundaryCharacters(
            "日本", "語",
            new OxRect(0.0f, 100.0f, 24.0f, 10.0f), new OxRect(30.0f, 100.0f, 12.0f, 10.0f),
            Size, tjOffsetTriggered: true);

        Assert.Equal(0x672C, chars[0].Code);
        Assert.Equal(4.0f, chars[0].Width, 4);   // 24pt / 6 bytes
        Assert.Equal(20.0f, chars[0].XPosition, 4);
        Assert.Equal(-200, chars[0].TjOffset);   // the yes/no trigger stands in for an offset

        Assert.Equal(0x8A9E, chars[1].Code);
        Assert.Equal(4.0f, chars[1].Width, 4);   // 12pt / 3 bytes
        Assert.Equal(30.0f, chars[1].XPosition, 4);
        Assert.Null(chars[1].TjOffset);

        Assert.Equal(100.0f, ctx.HorizontalScaling);
        Assert.Equal(0.0f, ctx.CharSpacing);
    }

    // ---- Rule 0.5: email context (text.rs:1265) ----

    [Fact]
    public void AnEmailBoundaryDemandsTwoAndAHalfTimesTheOrdinaryThreshold()
    {
        var config = OxSpanMergingConfig.New() with { DetectEmailPatterns = true };

        // Email threshold is 1.25pt x 2.5 = 3.125pt.
        var wide = Decide("user@outlook", ".com", gapPt: 4.0f, config: config);
        Assert.True(wide.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, wide.Source);
        Assert.Equal(0.85f, wide.Confidence);

        var narrow = Decide("user@outlook", ".com", gapPt: 2.0f, config: config);
        Assert.False(narrow.InsertSpace);
        Assert.Equal(OxSpaceSource.NoSpace, narrow.Source);
        Assert.Equal(1.0f, narrow.Confidence);

        // Without the rule the same 2.0pt gap is an ordinary strong-geometric space.
        var unprotected = Decide("user@outlook", ".com", gapPt: 2.0f);
        Assert.True(unprotected.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, unprotected.Source);
        Assert.Equal(0.95f, unprotected.Confidence);
    }

    [Fact]
    public void EmailContextRecognisesTheThreeSplitPointsAndNothingElse()
    {
        Assert.True(OxSpaceDecisionRules.IsEmailContext("user@outlook", ".com"));  // before the dot
        Assert.True(OxSpaceDecisionRules.IsEmailContext("user@outlook.", "com"));  // before the TLD
        Assert.True(OxSpaceDecisionRules.IsEmailContext("user@", "outlook.com"));  // after the @

        Assert.False(OxSpaceDecisionRules.IsEmailContext("user@outlook.", "1com")); // TLD must be alphabetic
        Assert.False(OxSpaceDecisionRules.IsEmailContext("Total", ".00"));          // no @ anywhere
        Assert.False(OxSpaceDecisionRules.IsEmailContext("user@outlook", "com"));

        // Only the last 64 bytes are scanned, so an @ further back is out of the window.
        Assert.False(OxSpaceDecisionRules.IsEmailContext("a@" + new string('x', 70), ".com"));
    }

    // ---- Rule 1.5: citation context (text.rs:1355) ----

    [Fact]
    public void ACitationMarkerNeedsOnlyOneSignalRatherThanConsensus()
    {
        var config = OxSpanMergingConfig.New() with { DetectCitationMarkers = true };
        // 6.5pt against 10pt body text is a 0.65 ratio, and 3pt of rise clears the 2pt
        // raise test while staying under the 5pt line-break test.
        OxRect prev = new(0.0f, 100.0f, 20.0f, 10.0f);
        OxRect next = new(22.0f, 103.0f, 10.0f, 10.0f);

        var byGap = Decide("text", "12", gapPt: 2.0f, config: config,
            prevBbox: prev, nextBbox: next, nextFontSize: 6.5f);
        Assert.True(byGap.InsertSpace);
        Assert.Equal(OxSpaceSource.TjOffset, byGap.Source);
        Assert.Equal(0.90f, byGap.Confidence);

        // The TJ offset alone is enough, with no geometric support at all.
        var byTj = Decide("text", "12", gapPt: 0.1f, tj: true, config: config,
            prevBbox: prev, nextBbox: next, nextFontSize: 6.5f);
        Assert.True(byTj.InsertSpace);
        Assert.Equal(OxSpaceSource.TjOffset, byTj.Source);
        Assert.Equal(0.90f, byTj.Confidence);

        // With the rule off, the same 2.0pt gap comes back as a strong-geometric space.
        var off = Decide("text", "12", gapPt: 2.0f,
            prevBbox: prev, nextBbox: next, nextFontSize: 6.5f);
        Assert.True(off.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, off.Source);
        Assert.Equal(0.95f, off.Confidence);
    }

    [Fact]
    public void CitationContextIsDecidedBySizeRatioWithOrWithoutBoxes()
    {
        OxRect prev = new(0.0f, 100.0f, 20.0f, 10.0f);
        OxRect raised = new(22.0f, 103.0f, 10.0f, 10.0f);
        OxRect level = new(22.0f, 100.0f, 10.0f, 10.0f);

        // The raise test can only add a hit: upstream falls through to the bare size ratio,
        // so an unraised superscript-sized run still reads as a citation.
        Assert.True(OxSpaceDecisionRules.IsCitationContext(prev, raised, 10.0f, 10.0f, 6.5f));
        Assert.True(OxSpaceDecisionRules.IsCitationContext(prev, level, 10.0f, 10.0f, 6.5f));

        // 0.5 and 0.75 are both inside the superscript band; 0.8 is body text.
        Assert.True(OxSpaceDecisionRules.IsCitationContext(prev, raised, 10.0f, 10.0f, 5.0f));
        Assert.True(OxSpaceDecisionRules.IsCitationContext(prev, raised, 10.0f, 10.0f, 7.5f));
        Assert.False(OxSpaceDecisionRules.IsCitationContext(prev, raised, 10.0f, 10.0f, 8.0f));

        // Either side counts, and without boxes the ratio is all there is.
        Assert.True(OxSpaceDecisionRules.IsCitationContext(null, null, 10.0f, 6.5f, 10.0f));
        Assert.False(OxSpaceDecisionRules.IsCitationContext(null, null, 10.0f, 10.0f, 10.0f));
    }

    // ---- Rule 1: line breaks (text.rs:1310) ----

    [Fact]
    public void AWrapInsertsASpaceUnlessTheWordWasHyphenated()
    {
        // 8pt of rise clears the 5pt line-break test, and the left edges match.
        OxRect prev = new(0.0f, 108.0f, 20.0f, 10.0f);
        OxRect next = new(1.0f, 100.0f, 20.0f, 10.0f);

        var hard = Decide("word", "next", gapPt: 0.0f, prevBbox: prev, nextBbox: next);
        Assert.True(hard.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, hard.Source);
        Assert.Equal(0.9f, hard.Confidence);

        var hyphenated = Decide("contin-", "ued", gapPt: 0.0f, prevBbox: prev, nextBbox: next);
        Assert.False(hyphenated.InsertSpace);
        Assert.Equal(OxSpaceSource.NoSpace, hyphenated.Source);
        Assert.Equal(1.0f, hyphenated.Confidence);

        // A jump to another column is not a wrap, so the rule declines and the chain
        // continues down to the kerning guard.
        OxRect otherColumn = new(300.0f, 100.0f, 20.0f, 10.0f);
        var columnJump = Decide("word", "next", gapPt: 0.0f, prevBbox: prev, nextBbox: otherColumn);
        Assert.False(columnJump.InsertSpace);
        Assert.Equal(OxSpaceSource.IntraWordKerning, columnJump.Source);
    }

    // ---- Rule 8: separate value tokens (text.rs:1690) ----

    [Fact]
    public void AdjacentValueTokensSplitOnAnyPositiveGapExceptDigitRuns()
    {
        // 0.5pt is far under the 1.25pt threshold, but "$0.00" "$0.00" are two cells.
        var currency = Decide("$0.00", "$0.00", gapPt: 0.5f);
        Assert.True(currency.InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, currency.Source);
        Assert.Equal(0.85f, currency.Confidence);

        // A number split across spans by TJ rounding must not become "123 456": digit to
        // digit needs half the geometric threshold, 0.625pt.
        Assert.False(Decide("123", "456", gapPt: 0.5f).InsertSpace);
        Assert.Equal(OxSpaceSource.GeometricGap, Decide("123", "456", gapPt: 0.7f).Source);
    }
}
