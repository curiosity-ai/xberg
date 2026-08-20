// ISO 32000-1:2008 §9.4.4 word boundary detection, ported from pdf_oxide 0.3.77:
//   src/text/word_boundary.rs :: CharacterInfo, BoundaryContext, WordBoundaryDetector,
//                                detect_word_boundaries
//   src/pipeline/config.rs    :: WordBoundaryMode
//
// A word break between two glyphs is decided from three independent signals — the TJ array
// offset (§9.4.4), the geometric gap implied by glyph positions and widths (§9.4), and the
// script of the two glyphs. Script rules run first and can veto the geometric signal, because
// scripts such as Devanagari or Arabic place real advances inside a single word.
//
// Script helpers live in OxDocumentScript.cs (ScriptSignals).
using System.Text;

namespace Xberg.Internal.PdfOxide.Text;

/// <summary>
/// Everything boundary detection needs to know about one glyph in the text stream
/// (word_boundary.rs :: CharacterInfo).
/// </summary>
internal sealed class CharacterInfo
{
    /// <summary>Unicode code point (a full scalar value, not a UTF-16 unit).</summary>
    internal int Code { get; set; }

    /// <summary>Glyph ID in the font, when known.</summary>
    internal ushort? GlyphId { get; set; }

    /// <summary>Character width in text space units.</summary>
    internal float Width { get; set; }

    /// <summary>Horizontal position in text space.</summary>
    internal float XPosition { get; set; }

    /// <summary>
    /// TJ array offset in thousandths of an em; negative values widen the gap that follows
    /// this glyph, which is how producers encode inter-word space without a space glyph.
    /// </summary>
    internal int? TjOffset { get; set; }

    /// <summary>Font size in points at the time this glyph was shown.</summary>
    internal float FontSize { get; set; }

    /// <summary>True when this glyph is a ligature (U+FB00-U+FB04) or came from one.</summary>
    internal bool IsLigature { get; set; }

    /// <summary>The ligature this glyph was expanded from, for tracking ligature expansion.</summary>
    internal Rune? OriginalLigature { get; set; }

    /// <summary>
    /// Suppresses boundaries on both sides of this glyph. Set by the extractor over
    /// email addresses and URLs so they survive as single tokens.
    /// </summary>
    internal bool ProtectedFromSplit { get; set; }

    internal CharacterInfo()
    {
    }

    internal CharacterInfo(int code, float width, float xPosition, float fontSize)
    {
        Code = code;
        Width = width;
        XPosition = xPosition;
        FontSize = fontSize;
    }
}

/// <summary>
/// Text state parameters that scale the boundary thresholds (word_boundary.rs ::
/// BoundaryContext, PDF §9.3).
/// </summary>
internal sealed class BoundaryContext
{
    /// <summary>Font size (Tf).</summary>
    internal float FontSize { get; set; }

    /// <summary>Horizontal scaling percentage (Tz), default 100.</summary>
    internal float HorizontalScaling { get; set; } = 100.0f;

    /// <summary>Word spacing (Tw), applied after a space character.</summary>
    internal float WordSpacing { get; set; }

    /// <summary>Character spacing (Tc), applied after every character.</summary>
    internal float CharSpacing { get; set; }

    /// <summary>Port of <c>BoundaryContext::new</c>: default text state for a given font size.</summary>
    internal BoundaryContext(float fontSize)
    {
        FontSize = fontSize;
    }

    /// <summary>Port of <c>BoundaryContext::effective_font_size</c>.</summary>
    internal float EffectiveFontSize() => FontSize * (HorizontalScaling / 100.0f);
}

/// <summary>
/// How the extractor wires the detector into TJ array processing
/// (pipeline/config.rs :: WordBoundaryMode).
/// </summary>
internal enum WordBoundaryMode
{
    /// <summary>
    /// Consult the detector only when the TJ offset and the geometric signal contradict
    /// each other. Backward compatible, and the Rust default.
    /// </summary>
    Tiebreaker = 0,

    /// <summary>
    /// Run the detector over the whole TJ character array before spans are created, and
    /// partition the array at the boundaries it reports.
    /// </summary>
    Primary = 1,
}

/// <summary>Port of <c>word_boundary.rs :: WordBoundaryDetector</c>.</summary>
internal sealed class WordBoundaryDetector
{
    /// <summary>Ligature code points whose expansions must never be split (U+FB00-U+FB06).</summary>
    private static readonly int[] Ligatures =
        [0xFB00, 0xFB01, 0xFB02, 0xFB03, 0xFB04, 0xFB05, 0xFB06];

    private int _tjOffsetThreshold = -100;

    // 80% of the font size. Conservative enough that ordinary letter spacing never trips it,
    // sensitive enough to catch a real word break encoded purely as position.
    private float _geometricGapRatio = 0.8f;

    private bool _cjkEnabled = true;
    private bool _detectScriptTransitions = true;
    private ScriptSignals.DocumentLanguage? _documentLanguage;
    private DocumentScript _primaryScript = DocumentScript.Mixed;
    private bool _useAdaptiveThreshold = true;

    /// <summary>Port of <c>WordBoundaryDetector::new</c>.</summary>
    internal WordBoundaryDetector()
    {
    }

    /// <summary>
    /// Port of <c>with_tj_threshold</c>. TJ offsets more negative than this are word breaks.
    /// Only consulted when adaptive thresholding is off.
    /// </summary>
    internal WordBoundaryDetector WithTjThreshold(int threshold)
    {
        _tjOffsetThreshold = threshold;
        return this;
    }

    /// <summary>Port of <c>with_geometric_gap_ratio</c>: gap threshold as a fraction of font size.</summary>
    internal WordBoundaryDetector WithGeometricGapRatio(float ratio)
    {
        _geometricGapRatio = ratio;
        return this;
    }

    /// <summary>Port of <c>with_cjk_enabled</c>.</summary>
    internal WordBoundaryDetector WithCjkEnabled(bool enabled)
    {
        _cjkEnabled = enabled;
        return this;
    }

    /// <summary>Port of <c>with_script_detection</c>.</summary>
    internal WordBoundaryDetector WithScriptDetection(bool enabled)
    {
        _detectScriptTransitions = enabled;
        return this;
    }

    /// <summary>Port of <c>with_document_language</c>: selects the CJK transition rule set.</summary>
    internal WordBoundaryDetector WithDocumentLanguage(ScriptSignals.DocumentLanguage language)
    {
        _documentLanguage = language;
        return this;
    }

    /// <summary>Port of <c>with_document_script</c>: selects the detection fast path.</summary>
    internal WordBoundaryDetector WithDocumentScript(DocumentScript script)
    {
        _primaryScript = script;
        return this;
    }

    /// <summary>Port of <c>with_adaptive_threshold</c>.</summary>
    internal WordBoundaryDetector WithAdaptiveThreshold(bool enabled)
    {
        _useAdaptiveThreshold = enabled;
        return this;
    }

    /// <summary>
    /// Port of <c>calculate_tj_threshold</c>. A fixed offset threshold cannot serve both a
    /// 8pt footnote and a 24pt heading, so the trigger is expressed as 2.5% of the scaled
    /// font size and then pushed further negative by whatever Tc/Tw the producer already
    /// applied — that spacing is deliberate, not a word break.
    /// </summary>
    private float CalculateTjThreshold(BoundaryContext context)
    {
        float fontSize = MathF.Max(context.FontSize, 1.0f);
        float hScale = MathF.Max(context.HorizontalScaling / 100.0f, 0.01f);

        float baseThreshold = -fontSize * hScale * 0.025f;
        float spacingAdjustment = (MathF.Abs(context.CharSpacing) + MathF.Abs(context.WordSpacing)) * 0.5f;

        return baseThreshold - spacingAdjustment;
    }

    /// <summary>
    /// Port of <c>WordBoundaryDetector::detect_word_boundaries</c>. Returns the indices at
    /// which a word break falls; index <c>i</c> means a break sits between characters
    /// <c>i-1</c> and <c>i</c>.
    /// </summary>
    internal List<int> DetectWordBoundaries(IReadOnlyList<CharacterInfo> characters, BoundaryContext context)
    {
        var boundaries = new List<int>();
        if (characters.Count == 0) return boundaries;

        for (int i = 1; i < characters.Count; i++)
        {
            if (IsWordBoundary(characters[i - 1], characters[i], context))
                boundaries.Add(i);
        }

        return boundaries;
    }

    /// <summary>
    /// Port of <c>WordBoundaryDetector::is_word_boundary</c>. Private in Rust; internal here
    /// so the decision can be unit-tested one pair at a time.
    /// </summary>
    internal bool IsWordBoundary(CharacterInfo prevChar, CharacterInfo currChar, BoundaryContext context)
    {
        if (prevChar.ProtectedFromSplit || currChar.ProtectedFromSplit) return false;

        // An explicit space or zero-width space is the unambiguous signal and outranks
        // every script rule below.
        if (prevChar.Code == 0x20 || prevChar.Code == 0x200B) return true;

        switch (_primaryScript)
        {
            case DocumentScript.Latin:
                return IsWordBoundaryBasic(prevChar, currChar, context);

            case DocumentScript.Cjk:
                if (_detectScriptTransitions)
                {
                    if (ShouldSplitAtCjkBoundary(prevChar, currChar) is bool cjk) return cjk;
                }
                return IsWordBoundaryBasic(prevChar, currChar, context);

            case DocumentScript.Rtl:
                if (ScriptSignals.ShouldSplitAtRtlBoundary(prevChar, currChar, context) is bool rtl) return rtl;
                return IsWordBoundaryBasic(prevChar, currChar, context);

            case DocumentScript.Complex:
                if (ShouldSplitAtComplexScriptBoundary(prevChar, currChar) is bool complex) return complex;
                return IsWordBoundaryBasic(prevChar, currChar, context);

            default:
                // Mixed: every detector runs, in the upstream order.
                if (ScriptSignals.ShouldSplitAtRtlBoundary(prevChar, currChar, context) is bool mixedRtl)
                    return mixedRtl;

                if (_detectScriptTransitions)
                {
                    if (ShouldSplitAtCjkBoundary(prevChar, currChar) is bool mixedCjk) return mixedCjk;
                }

                if (ShouldSplitAtComplexScriptBoundary(prevChar, currChar) is bool mixedComplex)
                    return mixedComplex;

                return IsWordBoundaryBasic(prevChar, currChar, context);
        }
    }

    /// <summary>
    /// Port of <c>is_word_boundary_basic</c>: the TJ offset and geometric gap checks shared
    /// by every script path.
    /// </summary>
    private bool IsWordBoundaryBasic(CharacterInfo prevChar, CharacterInfo currChar, BoundaryContext context)
    {
        if (prevChar.TjOffset is int tjOffset)
        {
            float threshold = _useAdaptiveThreshold
                ? CalculateTjThreshold(context)
                : _tjOffsetThreshold;
            if (tjOffset < threshold) return true;
        }

        if (HasSignificantGeometricGap(prevChar, currChar, context)) return true;

        // Legacy path: with script transitions off, every non-punctuation CJK glyph is its
        // own word, which is the best available segmentation without transition analysis.
        if (_cjkEnabled
            && !_detectScriptTransitions
            && IsCjkCharacter(prevChar.Code)
            && !IsCjkPunctuation(prevChar.Code))
            return true;

        return false;
    }

    /// <summary>Port of <c>should_split_at_complex_script_boundary</c>.</summary>
    private bool? ShouldSplitAtComplexScriptBoundary(CharacterInfo prevChar, CharacterInfo currChar)
    {
        var prevScript = ScriptSignals.DetectComplexScript(prevChar.Code);
        var currScript = ScriptSignals.DetectComplexScript(currChar.Code);

        if (!prevScript.HasValue && !currScript.HasValue) return null;

        if (Involves(ScriptSignals.ComplexScript.Devanagari))
            return ScriptSignals.HandleDevanagariBoundary(prevChar, currChar);

        if (Involves(ScriptSignals.ComplexScript.Thai))
            return ScriptSignals.HandleThaiBoundary(prevChar, currChar);

        if (Involves(ScriptSignals.ComplexScript.Khmer))
            return ScriptSignals.HandleKhmerBoundary(prevChar, currChar);

        // Bengali, Tamil, Telugu, Kannada and Malayalam share one matra/virama rule set.
        if (Involves(ScriptSignals.ComplexScript.Tamil)
            || Involves(ScriptSignals.ComplexScript.Telugu)
            || Involves(ScriptSignals.ComplexScript.Kannada)
            || Involves(ScriptSignals.ComplexScript.Malayalam)
            || Involves(ScriptSignals.ComplexScript.Bengali))
            return ScriptSignals.HandleIndicBoundary(prevChar, currChar);

        // Any other complex script: no specific rules, let the other signals decide.
        return null;

        bool Involves(ScriptSignals.ComplexScript script) =>
            prevScript == script || currScript == script;
    }

    /// <summary>Port of <c>should_split_at_cjk_boundary</c>.</summary>
    private bool? ShouldSplitAtCjkBoundary(CharacterInfo prevChar, CharacterInfo currChar)
    {
        // Density is left unmeasured (Rust passes None), so only sentence-ending and
        // enumeration punctuation reach the 0.9 confidence bar.
        float prevPunctuationScore = ScriptSignals.GetCjkPunctuationBoundaryScore(prevChar.Code, null);
        if (prevPunctuationScore >= 0.9f) return true;

        var prevScript = ScriptSignals.DetectCjkScript(prevChar.Code);
        var currScript = ScriptSignals.DetectCjkScript(currChar.Code);

        if (!prevScript.HasValue && !currScript.HasValue) return null;

        return _documentLanguage switch
        {
            ScriptSignals.DocumentLanguage.Japanese =>
                ScriptSignals.HandleJapaneseText(prevChar, currChar, prevScript, currScript),
            ScriptSignals.DocumentLanguage.Korean =>
                ScriptSignals.HandleKoreanText(prevChar, currChar, prevScript, currScript),
            // Chinese or unknown: plain script transition analysis.
            _ => ScriptSignals.ShouldSplitOnScriptTransition(prevScript, currScript, _documentLanguage),
        };
    }

    /// <summary>
    /// Port of <c>is_ligature_internal_gap</c>. An expanded 'fi' leaves an f-to-i gap that
    /// looks geometric but is internal to one glyph, so it can never be a word break.
    /// </summary>
    private static bool IsLigatureInternalGap(CharacterInfo prevChar, CharacterInfo currChar) =>
        Array.IndexOf(Ligatures, prevChar.Code) >= 0
        || prevChar.IsLigature
        || Array.IndexOf(Ligatures, currChar.Code) >= 0
        || currChar.IsLigature;

    /// <summary>
    /// Port of <c>WordBoundaryDetector::is_punctuation</c>: punctuation that attaches to the
    /// preceding word and therefore gets a reduced gap threshold.
    /// </summary>
    internal static bool IsPunctuation(int code) =>
        code is 0x21 or 0x22 or 0x27 or 0x2C or 0x2E or 0x3A or 0x3B or 0x3F // ASCII
        || (code >= 0x2018 && code <= 0x201F)  // Quotation marks
        || (code >= 0x2010 && code <= 0x2015); // Hyphens and dashes

    /// <summary>Port of <c>has_significant_geometric_gap</c>.</summary>
    private bool HasSignificantGeometricGap(
        CharacterInfo prevChar,
        CharacterInfo currChar,
        BoundaryContext context)
    {
        if (IsLigatureInternalGap(prevChar, currChar)) return false;

        float prevEndX = prevChar.XPosition + prevChar.Width;
        float rawGap = currChar.XPosition - prevEndX;

        // Tc is added after every character, so it is part of the advance, not of the gap.
        float adjustedGap = rawGap - context.CharSpacing;

        float baseThreshold = context.EffectiveFontSize() * _geometricGapRatio;

        // Punctuation belongs to the word it follows, so it needs twice the evidence
        // before it is torn off.
        if (IsPunctuation(currChar.Code)) return adjustedGap > baseThreshold * 0.5f;

        return adjustedGap > baseThreshold;
    }

    /// <summary>Port of <c>is_cjk_character</c>.</summary>
    private static bool IsCjkCharacter(int code) =>
        (code >= 0x3040 && code <= 0x309F)        // Hiragana
        || (code >= 0x30A0 && code <= 0x30FF)     // Katakana
        || (code >= 0x3400 && code <= 0x4DBF)     // Extension A
        || (code >= 0x4E00 && code <= 0x9FFF)     // CJK Unified Ideographs
        || (code >= 0x20000 && code <= 0x2A6DF)   // Extension B
        || (code >= 0x2A700 && code <= 0x2B73F)   // Extension C
        || (code >= 0x2B740 && code <= 0x2B81F)   // Extension D
        || (code >= 0x2B820 && code <= 0x2CEAF)   // Extension E
        || (code >= 0x2CEB0 && code <= 0x2EBEF);  // Extension F

    /// <summary>
    /// Port of <c>is_cjk_punctuation</c>: the ideographic marks and brackets that attach to
    /// the preceding word rather than starting a new one.
    /// </summary>
    private static bool IsCjkPunctuation(int code) =>
        code is 0x3001 or 0x3002
        || (code >= 0x3008 && code <= 0x3011)
        || code is 0x3014 or 0x3015;
}

/// <summary>Module-level entry point of word_boundary.rs.</summary>
internal static class WordBoundary
{
    /// <summary>
    /// Port of the free function <c>detect_word_boundaries</c>: detection with a
    /// default-configured detector.
    /// </summary>
    internal static List<int> DetectWordBoundaries(IReadOnlyList<CharacterInfo> characters, BoundaryContext context) =>
        new WordBoundaryDetector().DetectWordBoundaries(characters, context);
}
