using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the shape-plus-sequence page-number model (`pdf/structure/page_number.rs`). The point
/// of the model is that shape alone never justifies deleting anything, so most of these assert
/// that a plausible-looking string stays below the deletion threshold.
/// </summary>
public class PdfPageNumberTests
{
    private static PageNumberCandidate Classify(string text)
    {
        var candidate = PdfPageNumber.ClassifyPageNumberText(text);
        Assert.NotNull(candidate);
        return candidate!.Value;
    }

    [Fact]
    public void TheKeywordConventionsAreRecognisedAndScoreHighest()
    {
        var pageNofM = Classify("Page 3 of 12");
        Assert.Equal(PageNumberConvention.PageNofM, pageNofM.Convention);
        Assert.Equal(3u, pageNofM.Value);

        var pageN = Classify("Page 7");
        Assert.Equal(PageNumberConvention.PageN, pageN.Convention);
        Assert.True(pageN.ShapeConfidence < pageNofM.ShapeConfidence);
    }

    [Fact]
    public void APageCountBelowThePageNumberIsNotPaginationAtAll()
    {
        // "Page 9 of 2" fails the total >= current test, and no weaker rule claims a string
        // that still carries the keyword, so it is not a candidate at all.
        Assert.Null(PdfPageNumber.ClassifyPageNumberText("Page 9 of 2"));
    }

    [Fact]
    public void AllThreeDashesFlankAFolio()
    {
        foreach (string text in new[] { "- 5 -", "– 5 –", "— 5 —" })
        {
            var candidate = Classify(text);
            Assert.Equal(PageNumberConvention.DashedN, candidate.Convention);
            Assert.Equal(5u, candidate.Value);
        }
    }

    [Fact]
    public void SectionPrefixedPaginationCarriesTheSecondComponent()
    {
        var candidate = Classify("3-12");
        Assert.Equal(PageNumberConvention.SectionPrefixed, candidate.Convention);
        Assert.Equal(12u, candidate.Value);
    }

    [Fact]
    public void ProseAndOverlongRunsAreNotCandidatesAtAll()
    {
        Assert.Null(PdfPageNumber.ClassifyPageNumberText("Introduction"));
        Assert.Null(PdfPageNumber.ClassifyPageNumberText("12345"));   // five digits is an identifier
        Assert.Null(PdfPageNumber.ClassifyPageNumberText("a very long line of ordinary prose"));
        Assert.Null(PdfPageNumber.ClassifyPageNumberText(""));
    }

    [Fact]
    public void MalformedRomanNumeralsAreRejected()
    {
        // The old shape-only predicate accepted any short run of i/v/x, which is what made the
        // bare string "I" match 244 times across the corpus.
        Assert.Equal(4u, PdfPageNumber.ParseRomanNumeral("IV"));
        Assert.Equal(14u, PdfPageNumber.ParseRomanNumeral("xiv"));
        Assert.Null(PdfPageNumber.ParseRomanNumeral("IIII"));
        Assert.Null(PdfPageNumber.ParseRomanNumeral("VX"));
        Assert.Null(PdfPageNumber.ParseRomanNumeral("IC"));
        Assert.Null(PdfPageNumber.ParseRomanNumeral("Iv"));   // mixed case is a truncated word
    }

    [Fact]
    public void OnlyTheMarginsAreBands()
    {
        Assert.Equal(MarginBand.Top, PdfPageNumber.Band(0.05f));
        Assert.Equal(MarginBand.Body, PdfPageNumber.Band(0.5f));
        Assert.Equal(MarginBand.Bottom, PdfPageNumber.Band(0.95f));
    }

    [Fact]
    public void AnIsolatedCandidateNeverReachesTheDeletionThreshold()
    {
        var sequence = new PdfPageNumber.PageNumberSequence();
        sequence.Observe(0, MarginBand.Bottom, 0.5f, Classify("Page 1 of 9"));
        // Even the most unambiguous shape, seen once, is only shape evidence.
        Assert.True(sequence.ConfidenceAt(0, MarginBand.Bottom, 0.5f)
            < PdfPageNumber.PageNumberSequence.DeletionThreshold);
    }

    [Fact]
    public void AFourPageProgressionAtOneMarginSlotConfirmsEvenBareDigits()
    {
        var sequence = new PdfPageNumber.PageNumberSequence();
        for (int page = 0; page < 4; page++)
            sequence.Observe(page, MarginBand.Bottom, 0.5f, Classify((page + 1).ToString()));
        Assert.True(sequence.ConfidenceAt(2, MarginBand.Bottom, 0.5f)
            >= PdfPageNumber.PageNumberSequence.DeletionThreshold);
    }

    [Fact]
    public void ARepeatedLabelIsNotAProgressionAndStaysUndeletable()
    {
        var sequence = new PdfPageNumber.PageNumberSequence();
        for (int page = 0; page < 4; page++)
            sequence.Observe(page, MarginBand.Bottom, 0.5f, Classify("7"));
        Assert.True(sequence.ConfidenceAt(0, MarginBand.Bottom, 0.5f)
            < PdfPageNumber.PageNumberSequence.DeletionThreshold);
    }

    [Fact]
    public void ANumberedColumnInTheBodyBandIsStructurallyUndeletable()
    {
        // A numbered table column is a perfect monotonic sequence at a stable x; only the band
        // separates it from a folio, which is why the body band is capped below the threshold.
        var sequence = new PdfPageNumber.PageNumberSequence();
        for (int page = 0; page < 8; page++)
            sequence.Observe(page, MarginBand.Body, 0.5f, Classify((page + 1).ToString()));
        Assert.True(sequence.ConfidenceAt(3, MarginBand.Body, 0.5f)
            < PdfPageNumber.PageNumberSequence.DeletionThreshold);
    }

    [Fact]
    public void CandidatesAtDifferentMarginSlotsFormSeparateCohorts()
    {
        // A recto/verso book's left and right folios must each build their own progression.
        var sequence = new PdfPageNumber.PageNumberSequence();
        for (int page = 0; page < 4; page++)
            sequence.Observe(page, MarginBand.Bottom, 0.1f, Classify((page + 1).ToString()));
        Assert.Equal(0f, sequence.ConfidenceAt(0, MarginBand.Bottom, 0.9f));
    }

    [Fact]
    public void TwoConventionsSharingASlotContributeNoSequenceEvidence()
    {
        var sequence = new PdfPageNumber.PageNumberSequence();
        sequence.Observe(0, MarginBand.Bottom, 0.5f, Classify("[1]"));
        sequence.Observe(1, MarginBand.Bottom, 0.5f, Classify("Page 2 of 9"));
        Assert.True(sequence.ConfidenceAt(0, MarginBand.Bottom, 0.5f)
            < PdfPageNumber.PageNumberSequence.DeletionThreshold);
    }
}
