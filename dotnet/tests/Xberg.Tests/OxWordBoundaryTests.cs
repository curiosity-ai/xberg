using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Decision-boundary tests for the pdf_oxide word boundary detector
/// (pdf_oxide-0.3.77/src/text/word_boundary.rs).
/// </summary>
public class OxWordBoundaryTests
{
    private const float Font = 12.0f;

    // Default detector: geometric gap threshold = font size * 0.8.
    private const float GapThreshold = Font * 0.8f;

    private static CharacterInfo Ch(int code, float x, float width = 0.0f, int? tj = null) =>
        new(code, width, x, Font) { TjOffset = tj };

    private static BoundaryContext Ctx(float fontSize = Font) => new(fontSize);

    private static WordBoundaryDetector Detector() => new();

    // ---------------------------------------------------------------- spaces

    [Fact]
    public void AsciiSpace_CreatesBoundaryAfterIt()
    {
        var chars = new List<CharacterInfo>
        {
            Ch(0x48, 0.0f, 0.5f),   // 'H'
            Ch(0x65, 6.0f, 0.4f),   // 'e'
            Ch(0x20, 10.8f, 0.25f), // SPACE
            Ch(0x57, 16.2f, 0.7f),  // 'W'
        };

        var boundaries = WordBoundary.DetectWordBoundaries(chars, Ctx());

        Assert.Contains(3, boundaries);
    }

    [Fact]
    public void ZeroWidthSpace_CreatesBoundaryAfterIt()
    {
        Assert.True(Detector().IsWordBoundary(Ch(0x200B, 0.0f), Ch(0x41, 0.0f), Ctx()));
    }

    [Fact]
    public void ProtectedCharacters_NeverSplit()
    {
        // Emails and URLs are flagged upstream and must survive as one token even
        // across a gap far wider than the geometric threshold.
        var prev = Ch(0x41, 0.0f);
        var curr = Ch(0x42, GapThreshold * 4);
        prev.ProtectedFromSplit = true;

        Assert.False(Detector().IsWordBoundary(prev, curr, Ctx()));
    }

    // ------------------------------------------------------------ TJ offsets

    [Fact]
    public void StaticTjThreshold_BreaksOnlyBelowIt()
    {
        var detector = Detector().WithAdaptiveThreshold(false).WithTjThreshold(-100);

        Assert.False(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -100), Ch(0x42, 0.0f), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -101), Ch(0x42, 0.0f), Ctx()));
    }

    [Fact]
    public void AdaptiveTjThreshold_ScalesWithFontSize()
    {
        // base = -font_size * (Tz/100) * 0.025, so a 400pt font trips at roughly -10
        // while a 12pt font trips at roughly -0.3.
        var detector = Detector();
        var big = Ctx(400.0f);

        Assert.False(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -9), Ch(0x42, 0.0f), big));
        Assert.True(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -11), Ch(0x42, 0.0f), big));

        // The same -9 offset is well past the threshold at 12pt.
        Assert.True(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -9), Ch(0x42, 0.0f), Ctx()));
    }

    [Fact]
    public void AdaptiveTjThreshold_IsPushedNegativeByExplicitSpacing()
    {
        // Tc/Tw the producer already applied is deliberate spacing, not a word break,
        // so it moves the trigger further out by half its magnitude.
        var detector = Detector();
        var context = Ctx(400.0f);
        context.CharSpacing = 20.0f; // threshold becomes -10 - 10 = -20

        Assert.False(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -19), Ch(0x42, 0.0f), context));
        Assert.True(detector.IsWordBoundary(Ch(0x41, 0.0f, tj: -21), Ch(0x42, 0.0f), context));
    }

    // -------------------------------------------------------- geometric gaps

    [Fact]
    public void GeometricGap_BreaksStrictlyAboveTheWordMargin()
    {
        var detector = Detector();

        // Gap exactly at the threshold is not a break; the comparison is strict.
        Assert.False(detector.IsWordBoundary(Ch(0x61, 0.0f), Ch(0x62, GapThreshold), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x61, 0.0f), Ch(0x62, GapThreshold + 0.01f), Ctx()));
    }

    [Fact]
    public void GeometricGap_AccountsForGlyphWidthAndCharSpacing()
    {
        var detector = Detector();
        var context = Ctx();
        context.CharSpacing = 2.0f;

        // prev ends at 5 + 1 = 6; Tc of 2 is part of the advance, not of the gap.
        var prev = Ch(0x61, 5.0f, 1.0f);
        Assert.False(detector.IsWordBoundary(prev, Ch(0x62, 6.0f + 2.0f + GapThreshold - 0.1f), context));
        Assert.True(detector.IsWordBoundary(prev, Ch(0x62, 6.0f + 2.0f + GapThreshold + 0.1f), context));
    }

    [Fact]
    public void Punctuation_UsesHalfTheGapThreshold()
    {
        var detector = Detector();
        float half = GapThreshold * 0.5f;

        // A comma stays attached to the word it follows unless the gap is unusually wide.
        Assert.False(detector.IsWordBoundary(Ch(0x61, 0.0f), Ch(0x2C, half), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x61, 0.0f), Ch(0x2C, half + 0.01f), Ctx()));

        // A letter at the same position would not break at all.
        Assert.False(detector.IsWordBoundary(Ch(0x61, 0.0f), Ch(0x62, half + 0.01f), Ctx()));
    }

    [Fact]
    public void LigatureExpansion_SuppressesGeometricGap()
    {
        var prev = Ch(0x66, 0.0f); // 'f' expanded out of U+FB01
        prev.IsLigature = true;

        Assert.False(Detector().IsWordBoundary(prev, Ch(0x69, GapThreshold * 4), Ctx()));
    }

    [Fact]
    public void GeometricGapRatio_IsConfigurable()
    {
        var loose = Detector().WithGeometricGapRatio(2.0f);
        Assert.False(loose.IsWordBoundary(Ch(0x61, 0.0f), Ch(0x62, GapThreshold + 0.01f), Ctx()));
    }

    // -------------------------------------------------------------------- CJK

    [Fact]
    public void CjkToCjk_DoesNotBreakWithoutASpacingSignal()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Cjk);

        // 中 文, drawn adjacently: same script, no TJ offset, no gap.
        Assert.False(detector.IsWordBoundary(Ch(0x4E2D, 0.0f), Ch(0x6587, 0.0f), Ctx()));
    }

    [Fact]
    public void CjkToLatin_BreaksEvenWithoutAGap()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Cjk);

        Assert.True(detector.IsWordBoundary(Ch(0x6587, 0.0f), Ch(0x41, 0.0f), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x41, 0.0f), Ch(0x6587, 0.0f), Ctx()));
    }

    [Fact]
    public void AstralHanExtension_IsRecognisedAsCjk()
    {
        // U+2A700 lives outside the BMP; reading it as UTF-16 units would lose the script.
        int astral = char.ConvertToUtf32("\uD869\uDF00", 0); // U+2A700
        Assert.Equal(0x2A700, astral);
        Assert.Equal(ScriptSignals.CjkScript.HanExtensionBF, ScriptSignals.DetectCjkScript(astral));

        var detector = Detector().WithDocumentScript(DocumentScript.Cjk);
        Assert.True(detector.IsWordBoundary(Ch(astral, 0.0f), Ch(0x41, 0.0f), Ctx()));
    }

    [Fact]
    public void CjkSentenceEndingPunctuation_AlwaysBreaks()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Cjk);

        // 。 scores 1.0, 、 scores 0.9 — both clear the 0.9 confidence bar.
        Assert.True(detector.IsWordBoundary(Ch(0x3002, 0.0f), Ch(0x4E2D, 0.0f), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x3001, 0.0f), Ch(0x4E2D, 0.0f), Ctx()));

        // A corner bracket scores 0.8 and therefore defers to the other signals; with no
        // spacing signal and no script transition either, nothing breaks.
        Assert.False(detector.IsWordBoundary(Ch(0x300C, 0.0f), Ch(0x300D, 0.0f), Ctx()));
    }

    [Fact]
    public void JapaneseKanaTransitions_StayInsideOneWord()
    {
        var detector = Detector()
            .WithDocumentScript(DocumentScript.Cjk)
            .WithDocumentLanguage(ScriptSignals.DocumentLanguage.Japanese);

        // 見 + る (kanji + okurigana) and small kana are intra-word in Japanese.
        Assert.False(detector.IsWordBoundary(Ch(0x898B, 0.0f), Ch(0x308B, 0.0f), Ctx()));
        Assert.False(detector.IsWordBoundary(Ch(0x30AD, 0.0f), Ch(0x30E3, 0.0f), Ctx())); // キ + small ャ
    }

    [Fact]
    public void LegacyCjkMode_BreaksAfterEveryNonPunctuationIdeograph()
    {
        // With script transitions off, each ideograph becomes its own word.
        var legacy = Detector().WithScriptDetection(false).WithCjkEnabled(true);

        Assert.True(legacy.IsWordBoundary(Ch(0x4E2D, 0.0f), Ch(0x6587, 0.0f), Ctx()));
        Assert.False(legacy.IsWordBoundary(Ch(0x3002, 0.0f), Ch(0x6587, 0.0f), Ctx()));

        Assert.False(Detector().WithScriptDetection(false).WithCjkEnabled(false)
            .IsWordBoundary(Ch(0x4E2D, 0.0f), Ch(0x6587, 0.0f), Ctx()));
    }

    // -------------------------------------------------------------------- RTL

    [Fact]
    public void ArabicCursiveJoin_IsNotBrokenByAWideGeometricGap()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Rtl);

        // بت drawn with a gap four times the word margin: the join rule still wins.
        Assert.False(detector.IsWordBoundary(Ch(0x0628, 0.0f), Ch(0x062A, GapThreshold * 4), Ctx()));
    }

    [Fact]
    public void ArabicDiacriticAndTatweel_NeverBreak()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Rtl);

        Assert.False(detector.IsWordBoundary(Ch(0x0628, 0.0f), Ch(0x064E, GapThreshold * 4), Ctx())); // FATHA
        Assert.False(detector.IsWordBoundary(Ch(0x0628, 0.0f), Ch(0x0640, GapThreshold * 4), Ctx())); // TATWEEL
    }

    [Fact]
    public void RtlTjOffset_UsesItsOwnFixedTrigger()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Rtl);

        Assert.False(detector.IsWordBoundary(Ch(0x0628, 0.0f, tj: -50), Ch(0x062A, 0.0f), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x0628, 0.0f, tj: -51), Ch(0x062A, 0.0f), Ctx()));
    }

    [Fact]
    public void RtlToLatinTransition_Breaks_ButDigitRunsDoNot()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Rtl);

        Assert.True(detector.IsWordBoundary(Ch(0x0628, 0.0f), Ch(0x41, 0.0f), Ctx()));
        Assert.False(detector.IsWordBoundary(Ch(0x31, 0.0f), Ch(0x32, 0.0f), Ctx()));   // "12"
        Assert.False(detector.IsWordBoundary(Ch(0x06F1, 0.0f), Ch(0x32, 0.0f), Ctx())); // ١2
    }

    // --------------------------------------------------------- complex scripts

    [Fact]
    public void DevanagariMatra_NeverSplitsFromItsBase()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Complex);

        // क + ा with a wide gap: the matra carries its own advance, which a purely
        // geometric test would mistake for a word break.
        Assert.False(detector.IsWordBoundary(Ch(0x0915, 0.0f), Ch(0x093E, GapThreshold * 4), Ctx()));

        // ...and the reverse direction, matra → consonant, is intra-word too.
        Assert.False(detector.IsWordBoundary(Ch(0x093E, 0.0f), Ch(0x0916, GapThreshold * 4), Ctx()));

        // Virama forms a conjunct with the following consonant.
        Assert.False(detector.IsWordBoundary(Ch(0x094D, 0.0f), Ch(0x0916, GapThreshold * 4), Ctx()));
    }

    [Fact]
    public void IndicDiacritics_NeverSplitFromTheirBase()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Complex);

        // Tamil க + ா, then the matra → consonant direction.
        Assert.False(detector.IsWordBoundary(Ch(0x0B95, 0.0f), Ch(0x0BBE, GapThreshold * 4), Ctx()));
        Assert.False(detector.IsWordBoundary(Ch(0x0BBE, 0.0f), Ch(0x0B95, GapThreshold * 4), Ctx()));
    }

    [Fact]
    public void ThaiToneMarksAttach_AndMajorPunctuationBreaks()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Complex);

        Assert.False(detector.IsWordBoundary(Ch(0x0E01, 0.0f), Ch(0x0E48, GapThreshold * 4), Ctx()));
        Assert.True(detector.IsWordBoundary(Ch(0x0E01, 0.0f), Ch(0x0E2F, 0.0f), Ctx())); // PAIYANNOI
    }

    [Fact]
    public void KhmerCoeng_BindsTheFollowingConsonant()
    {
        var detector = Detector().WithDocumentScript(DocumentScript.Complex);

        Assert.False(detector.IsWordBoundary(Ch(0x17D2, 0.0f), Ch(0x1780, GapThreshold * 4), Ctx()));
    }

    // ------------------------------------------------------- document profile

    [Fact]
    public void DocumentScriptDetection_ClassifiesBySampledCodePoints()
    {
        Assert.Equal(DocumentScript.Latin, Profile(0x41, 0x42));
        Assert.Equal(DocumentScript.Cjk, Profile(0x41, 0x4E2D));
        Assert.Equal(DocumentScript.Rtl, Profile(0x41, 0x0628));
        Assert.Equal(DocumentScript.Complex, Profile(0x41, 0x0915));
        Assert.Equal(DocumentScript.Rtl, Profile(0x0915, 0x0628));   // RTL outranks a complex signal
        Assert.Equal(DocumentScript.Mixed, Profile(0x0628, 0x4E2D)); // RTL + CJK, no complex
        Assert.Equal(DocumentScript.Latin, DocumentScriptDetector.DetectFromCharacters([]));

        static DocumentScript Profile(params int[] codes) =>
            DocumentScriptDetector.DetectFromCharacters(
                codes.Select(c => new CharacterInfo(c, 0.0f, 0.0f, Font)).ToList());
    }

    [Fact]
    public void DocumentScriptDetection_SamplesOnlyTheFirstThousandCharacters()
    {
        var chars = new List<CharacterInfo>();
        for (int i = 0; i < 1000; i++) chars.Add(new CharacterInfo(0x41, 0.0f, 0.0f, Font));
        chars.Add(new CharacterInfo(0x4E2D, 0.0f, 0.0f, Font));

        Assert.Equal(DocumentScript.Latin, DocumentScriptDetector.DetectFromCharacters(chars));
    }

    // ------------------------------------------------------------------- mode

    [Fact]
    public void WordBoundaryMode_DefaultsToTiebreaker()
    {
        Assert.Equal(WordBoundaryMode.Tiebreaker, default(WordBoundaryMode));
    }

    [Fact]
    public void PrimaryMode_SplitsWhereTiebreakerModeDoesNot()
    {
        // 中 文 A B drawn adjacently. Neither the TJ signal nor the geometric signal fires
        // anywhere, so Tiebreaker never consults the detector and keeps one run; Primary
        // partitions on the detector's CJK→Latin boundary.
        var chars = new List<CharacterInfo>
        {
            Ch(0x4E2D, 0.0f), Ch(0x6587, 0.0f), Ch(0x41, 0.0f), Ch(0x42, 0.0f),
        };
        var detector = Detector().WithDocumentScript(DocumentScript.Cjk);

        Assert.Equal(1, CountRuns(WordBoundaryMode.Tiebreaker, chars, detector));
        Assert.Equal(2, CountRuns(WordBoundaryMode.Primary, chars, detector));

        static int CountRuns(WordBoundaryMode mode, List<CharacterInfo> chars, WordBoundaryDetector detector)
        {
            var context = Ctx();
            int runs = 1;
            for (int i = 1; i < chars.Count; i++)
            {
                bool tjSignal = chars[i - 1].TjOffset is int t && t < -100;
                bool geoSignal = chars[i].XPosition - (chars[i - 1].XPosition + chars[i - 1].Width) > GapThreshold;
                bool detected = detector.IsWordBoundary(chars[i - 1], chars[i], context);

                bool isBoundary = mode == WordBoundaryMode.Primary
                    ? detected
                    : (tjSignal == geoSignal ? tjSignal : detected);

                if (isBoundary) runs++;
            }
            return runs;
        }
    }

    // ------------------------------------------------------------- free entry

    [Fact]
    public void DetectWordBoundaries_ReturnsIndicesOfTheFollowingCharacter()
    {
        var chars = new List<CharacterInfo>
        {
            Ch(0x54, 0.0f, 0.5f),            // 'T'
            Ch(0x2D, 6.0f, 0.25f, tj: -200), // '-' with a large negative offset
            Ch(0x6F, 18.0f, 0.4f),           // 'o'
        };

        Assert.Equal(new List<int> { 2 }, WordBoundary.DetectWordBoundaries(chars, Ctx()));
        Assert.Empty(WordBoundary.DetectWordBoundaries([], Ctx()));
    }

    [Fact]
    public void IsPunctuation_CoversAsciiQuotesAndDashes()
    {
        Assert.True(WordBoundaryDetector.IsPunctuation(0x2E));   // '.'
        Assert.True(WordBoundaryDetector.IsPunctuation(0x2018)); // left single quote
        Assert.True(WordBoundaryDetector.IsPunctuation(0x2013)); // en dash
        Assert.False(WordBoundaryDetector.IsPunctuation(0x2D));  // ASCII hyphen-minus is not listed
        Assert.False(WordBoundaryDetector.IsPunctuation(0x41));
    }
}
