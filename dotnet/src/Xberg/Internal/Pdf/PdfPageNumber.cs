// Positional, sequence-aware page-number detection — a port of
//   crates/xberg/src/pdf/structure/page_number.rs
// and the `mark_validated_page_numbers` pass in `pipeline.rs` that drives it.
//
// A page number is not a shape, it is a position plus a cross-page progression. Testing
// shape alone ("1-4 digits", "made of i/v/x") matches table cells, list markers, footnote
// references and stray capitals — and because furniture under 80 alphanumeric characters is
// physically deleted, every one of those matches is silent content loss.
//
// The two signals stay separate here: ClassifyPageNumberText answers "could this string ever
// be a page number?" with a deliberately modest shape confidence, and PageNumberSequence
// raises that only when the same margin position carries a near-monotonic run of values
// across pages. Deletion is gated on DeletionThreshold.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Xberg.Internal.Pdf;

/// <summary>Where on the page a paragraph sits. Page numbers live in the margins, never
/// mid-body, so the band is the first and cheapest filter against table false positives.</summary>
internal enum MarginBand { Top, Body, Bottom }

/// <summary>The pagination conventions this module recognises.</summary>
internal enum PageNumberConvention
{
    BareDigits, PageN, PageNofM, NSlashM, DashedN, BracketedN, SectionPrefixed, Roman,
}

/// <summary>A string that <em>could</em> be a page number, with what its shape alone is worth.
/// The confidence is never sufficient to delete on its own.</summary>
internal readonly record struct PageNumberCandidate(
    uint? Value, PageNumberConvention Convention, float ShapeConfidence);

internal static class PdfPageNumber
{
    // ── Band geometry ───────────────────────────────────────────────────────────────

    /// <summary>Fraction of page height from the top edge that counts as the top margin.
    /// 0.12 of an 11in page is 1.32in — a running head plus its trailing whitespace, without
    /// reaching the first body line of a normally-margined document.</summary>
    private const float TOP_BAND_MAX_Y_RATIO = 0.12f;

    /// <summary>The same distance measured from the bottom edge; footers and folios live here.</summary>
    private const float BOTTOM_BAND_MIN_Y_RATIO = 0.88f;

    /// <summary>Band for a vertical position, 0.0 at the top of the page and 1.0 at the bottom.
    /// A non-finite ratio falls through to Body, which is the safe answer because body-band
    /// candidates can never reach a deletable confidence.</summary>
    public static MarginBand Band(float yRatio) =>
        yRatio <= TOP_BAND_MAX_Y_RATIO ? MarginBand.Top
        : yRatio >= BOTTOM_BAND_MIN_Y_RATIO ? MarginBand.Bottom
        : MarginBand.Body;

    // ── Shape classification ────────────────────────────────────────────────────────

    /// <summary>Longest string still worth testing. "Page 1234 of 5678" is 17 characters.</summary>
    private const int MAX_CANDIDATE_CHARS = 24;

    /// <summary>Four digits covers every realistic pagination without matching identifiers.</summary>
    private const int MAX_PAGE_DIGITS = 4;

    private const string PAGE_KEYWORD = "page";
    private const string OF_SEPARATOR = " of ";
    private const int MIN_DASHED_CHARS = 3;

    // Shape confidences. The keyword forms are near-unambiguous; bare digits and single roman
    // letters are near worthless, because those are exactly what over-matched before.
    private const float PAGE_N_OF_M_CONFIDENCE = 0.95f;
    private const float PAGE_N_CONFIDENCE = 0.85f;
    private const float DASHED_N_CONFIDENCE = 0.85f;
    private const float N_SLASH_M_CONFIDENCE = 0.70f;
    private const float SECTION_PREFIXED_CONFIDENCE = 0.45f;
    private const float BRACKETED_N_CONFIDENCE = 0.35f;
    private const float BARE_DIGITS_CONFIDENCE = 0.30f;
    private const float LOWERCASE_ROMAN_CONFIDENCE = 0.45f;
    private const float UPPERCASE_ROMAN_CONFIDENCE = 0.35f;

    /// <summary>The corpus's worst offender: "I" alone matched 244 times as a pronoun, an
    /// initial, a list marker and a column label. Only overwhelming sequence evidence should
    /// ever let one of these be deleted.</summary>
    private const float SINGLE_LETTER_ROMAN_CONFIDENCE = 0.15f;

    /// <summary>Shape-only match; null when the text cannot be a page number at all. A non-null
    /// result means "could be", never "is". Rules are tried most-specific first so "- 5 -" reads
    /// as a dashed folio rather than as section-prefixed "5".</summary>
    public static PageNumberCandidate? ClassifyPageNumberText(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MAX_CANDIDATE_CHARS) return null;
        return ClassifyPageNofM(trimmed)
            ?? ClassifyPageN(trimmed)
            ?? ClassifyDashedN(trimmed)
            ?? ClassifyNSlashM(trimmed)
            ?? ClassifyBracketedN(trimmed)
            ?? ClassifySectionPrefixed(trimmed)
            ?? ClassifyBareDigits(trimmed)
            ?? ClassifyRoman(trimmed);
    }

    /// <summary>The text after a leading case-insensitive "Page" keyword and its separator.</summary>
    private static string? StripPageKeyword(string text)
    {
        if (text.Length < PAGE_KEYWORD.Length) return null;
        if (!text.AsSpan(0, PAGE_KEYWORD.Length).Equals(PAGE_KEYWORD, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = text[PAGE_KEYWORD.Length..];
        if (rest.StartsWith(' ') || rest.StartsWith(' ')) return rest[1..].Trim();
        return null;
    }

    private static PageNumberCandidate? ClassifyPageNofM(string text)
    {
        if (StripPageKeyword(text) is not { } rest) return null;
        int separator = rest.ToLowerInvariant().IndexOf(OF_SEPARATOR, StringComparison.Ordinal);
        if (separator < 0) return null;
        if (ParseOrdinal(rest[..separator].Trim()) is not { } current) return null;
        if (ParseOrdinal(rest[(separator + OF_SEPARATOR.Length)..].Trim()) is not { } total) return null;
        if (total < current) return null;
        return new PageNumberCandidate(current, PageNumberConvention.PageNofM, PAGE_N_OF_M_CONFIDENCE);
    }

    private static PageNumberCandidate? ClassifyPageN(string text)
    {
        if (StripPageKeyword(text) is not { } rest) return null;
        if (ParseOrdinal(rest) is not { } value) return null;
        return new PageNumberCandidate(value, PageNumberConvention.PageN, PAGE_N_CONFIDENCE);
    }

    /// <summary>ASCII hyphen, en dash and em dash are all used to flank folios.</summary>
    private static bool IsDash(char c) => c is '-' or '–' or '—';

    private static PageNumberCandidate? ClassifyDashedN(string text)
    {
        if (text.Length < MIN_DASHED_CHARS) return null;
        if (!IsDash(text[0]) || !IsDash(text[^1])) return null;
        if (ParseOrdinal(text[1..^1].Trim()) is not { } value) return null;
        return new PageNumberCandidate(value, PageNumberConvention.DashedN, DASHED_N_CONFIDENCE);
    }

    private static PageNumberCandidate? ClassifyNSlashM(string text)
    {
        int slash = text.IndexOf('/');
        if (slash < 0) return null;
        if (ParseOrdinal(text[..slash].Trim()) is not { } current) return null;
        if (ParseOrdinal(text[(slash + 1)..].Trim()) is not { } total) return null;
        if (total < current) return null;
        return new PageNumberCandidate(current, PageNumberConvention.NSlashM, N_SLASH_M_CONFIDENCE);
    }

    private static PageNumberCandidate? ClassifyBracketedN(string text)
    {
        if (text.Length < 2 || text[0] != '[' || text[^1] != ']') return null;
        if (ParseOrdinal(text[1..^1].Trim()) is not { } value) return null;
        return new PageNumberCandidate(value, PageNumberConvention.BracketedN, BRACKETED_N_CONFIDENCE);
    }

    /// <summary>Section-prefixed pagination such as "3-12": chapter 3, page 12. The ordinal that
    /// progresses across pages is the second component.</summary>
    private static PageNumberCandidate? ClassifySectionPrefixed(string text)
    {
        int split = text.IndexOfAny(['-', '–']);
        if (split < 0) return null;
        string section = text[..split].Trim(), page = text[(split + 1)..].Trim();
        if (!IsDigitRun(section) || !IsDigitRun(page)) return null;
        if (!uint.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)) return null;
        return new PageNumberCandidate(value, PageNumberConvention.SectionPrefixed, SECTION_PREFIXED_CONFIDENCE);
    }

    private static PageNumberCandidate? ClassifyBareDigits(string text)
    {
        if (!IsDigitRun(text)) return null;
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)) return null;
        return new PageNumberCandidate(value, PageNumberConvention.BareDigits, BARE_DIGITS_CONFIDENCE);
    }

    private static PageNumberCandidate? ClassifyRoman(string text)
    {
        if (ParseRomanNumeral(text) is not { } value) return null;
        return new PageNumberCandidate(value, PageNumberConvention.Roman, RomanShapeConfidence(text));
    }

    private static float RomanShapeConfidence(string text) =>
        text.Length == 1 ? SINGLE_LETTER_ROMAN_CONFIDENCE
        : text.All(char.IsAsciiLetterLower) ? LOWERCASE_ROMAN_CONFIDENCE
        : UPPERCASE_ROMAN_CONFIDENCE;

    private static bool IsDigitRun(string text) =>
        text.Length > 0 && text.Length <= MAX_PAGE_DIGITS && text.All(char.IsAsciiDigit);

    /// <summary>Either an arabic or a roman ordinal — both appear after "Page", inside brackets
    /// and between dashes, and front matter mixes the two within one document.</summary>
    private static uint? ParseOrdinal(string text)
    {
        if (IsDigitRun(text))
            return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint v) ? v : null;
        return ParseRomanNumeral(text);
    }

    // ── Roman numerals ──────────────────────────────────────────────────────────────

    private const uint MAX_ROMAN_VALUE = 3999;
    private const int MAX_ROMAN_CHARS = 15;

    private static readonly (uint Unit, string Symbol)[] RomanCanonicalTable =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];

    private static uint? RomanLetterValue(char letter) => letter switch
    {
        'I' => 1u, 'V' => 5u, 'X' => 10u, 'L' => 50u, 'C' => 100u, 'D' => 500u, 'M' => 1000u,
        _ => null,
    };

    private static string ToCanonicalRoman(uint value)
    {
        uint remainder = value;
        var rendered = new System.Text.StringBuilder();
        foreach (var (unit, symbol) in RomanCanonicalTable)
            while (remainder >= unit) { rendered.Append(symbol); remainder -= unit; }
        return rendered.ToString();
    }

    /// <summary>
    /// A well-formed roman numeral's value, or null when it is malformed.
    /// </summary>
    /// <remarks>
    /// Well-formedness is checked by canonical round-trip rather than by "built only from
    /// numeral letters", which is what let the old predicate accept "IIII", "VX" and "IC" and,
    /// worse, treat any short run of i/v/x as pagination. Case must be uniform: real documents
    /// never mix, and "Iv" is far more likely to be a truncated word.
    /// </remarks>
    public static uint? ParseRomanNumeral(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MAX_ROMAN_CHARS) return null;
        bool allLower = trimmed.All(char.IsAsciiLetterLower);
        bool allUpper = trimmed.All(char.IsAsciiLetterUpper);
        if (!allLower && !allUpper) return null;

        string uppercased = trimmed.ToUpperInvariant();
        var letters = new List<uint>(uppercased.Length);
        foreach (char c in uppercased)
        {
            if (RomanLetterValue(c) is not { } v) return null;
            letters.Add(v);
        }
        long total = AccumulateRoman(letters);
        if (total <= 0 || total > MAX_ROMAN_VALUE) return null;
        uint value = (uint)total;
        return ToCanonicalRoman(value) == uppercased ? value : null;
    }

    /// <summary>Standard subtractive accumulation: a letter smaller than its successor is
    /// subtracted. Signed, so malformed input such as "IM" cannot underflow.</summary>
    private static long AccumulateRoman(List<uint> letters)
    {
        long total = 0;
        for (int index = 0; index < letters.Count; index++)
        {
            uint current = letters[index];
            uint next = index + 1 < letters.Count ? letters[index + 1] : 0;
            total += current < next ? -(long)current : current;
        }
        return total;
    }

    // ── Cross-page sequence evidence ────────────────────────────────────────────────

    /// <summary>Horizontal tolerance, as a fraction of page width, for treating two candidates as
    /// occupying "the same" margin position. Folios drift by a digit's width as the number grows
    /// (9 to 10); 0.08 of an 8.5in page absorbs that while keeping the recto and verso folios of
    /// a book in separate cohorts.</summary>
    private const float X_RATIO_TOLERANCE = 0.08f;

    /// <summary>A single observation cannot show progression, so two is the floor.</summary>
    private const int MIN_SEQUENCE_PAGES = 2;

    /// <summary>Four consecutive correctly-progressing folios is already overwhelming.</summary>
    private const float SEQUENCE_SATURATION_PAGES = 4.0f;

    /// <summary>Accepts a page whose folio was suppressed or failed extraction (the value jumps
    /// by 2 across a 1-page step) without accepting arbitrary increasing numbers.</summary>
    private const long SEQUENCE_STEP_TOLERANCE = 1;

    /// <summary>Weight of the shape signal — small by design: shape is what over-matched.</summary>
    private const float SHAPE_WEIGHT = 0.30f;

    /// <summary>Weight of the cross-page sequence signal, which is the only evidence that
    /// actually identifies pagination.</summary>
    private const float SEQUENCE_WEIGHT = 0.70f;

    /// <summary>A numbered column in a table is a perfect monotonic sequence at a stable x
    /// position; only the band separates it from a folio. Capping the body band below the
    /// deletion threshold makes that case structurally undeletable.</summary>
    private const float BODY_BAND_CONFIDENCE_CAP = 0.30f;

    private readonly record struct Observation(
        int PageIndex, MarginBand Band, float XRatio, uint? Value,
        PageNumberConvention Convention, float ShapeConfidence);

    /// <summary>
    /// Accumulates candidates across pages and confirms only those forming a near-monotonic
    /// sequence at a stable margin position. Observe every page before asking for a confidence.
    /// </summary>
    internal sealed class PageNumberSequence
    {
        /// <summary>Confidence at or above which a caller may delete. Deliberately high: keeping
        /// a stray page number is far cheaper than deleting a table cell. Calibrated so bare
        /// digits need a full four-page progression (0.79) while an isolated candidate of any
        /// shape tops out at 0.285.</summary>
        public const float DeletionThreshold = 0.75f;

        private readonly List<Observation> _observations = new();

        public void Observe(int pageIndex, MarginBand band, float xRatio, PageNumberCandidate candidate) =>
            _observations.Add(new Observation(
                pageIndex, band, xRatio, candidate.Value, candidate.Convention, candidate.ShapeConfidence));

        /// <summary>Final confidence that the candidate recorded at this exact position is a real
        /// page number. Call only after every page has been observed.</summary>
        public float ConfidenceAt(int pageIndex, MarginBand band, float xRatio)
        {
            var cohort = PositionalCohort(band, xRatio);
            if (!cohort.Any(o => o.PageIndex == pageIndex)) return 0f;
            float shape = MeanShapeConfidence(cohort);
            float sequence = SequenceScore(cohort);
            float confidence = Math.Clamp(shape * SHAPE_WEIGHT + sequence * SEQUENCE_WEIGHT, 0f, 1f);
            return band == MarginBand.Body ? Math.Min(confidence, BODY_BAND_CONFIDENCE_CAP) : confidence;
        }

        /// <summary>Every page carrying a candidate at this band and x position, one per page
        /// (the horizontally nearest), ordered by page index.</summary>
        private List<Observation> PositionalCohort(MarginBand band, float xRatio)
        {
            var nearestPerPage = new SortedDictionary<int, Observation>();
            foreach (var observation in _observations)
            {
                if (observation.Band != band || Math.Abs(observation.XRatio - xRatio) > X_RATIO_TOLERANCE) continue;
                if (nearestPerPage.TryGetValue(observation.PageIndex, out var best))
                {
                    if (Math.Abs(observation.XRatio - xRatio) < Math.Abs(best.XRatio - xRatio))
                        nearestPerPage[observation.PageIndex] = observation;
                }
                else nearestPerPage[observation.PageIndex] = observation;
            }
            return nearestPerPage.Values.ToList();
        }
    }

    /// <summary>Average shape confidence over the whole cohort rather than the queried candidate
    /// alone: inside a confirmed run one weak member ("i" at the head of i, ii, iii, iv) is
    /// carried by its neighbours; outside one, averaging changes nothing.</summary>
    private static float MeanShapeConfidence(List<Observation> cohort) =>
        cohort.Count == 0 ? 0f : cohort.Sum(o => o.ShapeConfidence) / cohort.Count;

    /// <summary>Fraction of consecutive cohort pairs that progress correctly, scaled by how much
    /// of the saturation window the cohort fills.</summary>
    private static float SequenceScore(List<Observation> cohort)
    {
        if (cohort.Count < MIN_SEQUENCE_PAGES) return 0f;
        int pairs = cohort.Count - 1;
        int progressive = 0;
        for (int i = 0; i + 1 < cohort.Count; i++) if (IsProgressive(cohort[i], cohort[i + 1])) progressive++;
        float ratio = (float)progressive / pairs;
        float coverage = Math.Min(cohort.Count / SEQUENCE_SATURATION_PAGES, 1f);
        return ratio * coverage;
    }

    /// <summary>
    /// Whether two cohort members advance like pagination: same convention, strictly increasing,
    /// by roughly the number of pages between them.
    /// </summary>
    /// <remarks>
    /// A repeated value — a label such as "7" on every page — is not progressive, which is how a
    /// running header is rejected. The convention must match because a document paginates one
    /// way at a time: a cohort reading "[1]" on one page and "Page 2 of 9" on the next is two
    /// unrelated things sharing a margin position, most often a citation sitting low on the page
    /// next to genuine folios.
    /// </remarks>
    private static bool IsProgressive(Observation previous, Observation next)
    {
        if (previous.Convention != next.Convention) return false;
        if (previous.Value is not { } earlier || next.Value is not { } later) return false;
        if (later <= earlier) return false;
        long pageStep = Math.Max(next.PageIndex - previous.PageIndex, 0);
        long valueStep = later - earlier;
        return Math.Abs(valueStep - pageStep) <= SEQUENCE_STEP_TOLERANCE;
    }
}
