// Script signals feeding ISO 32000-1:2008 §9.4.4 word boundary detection, ported
// from pdf_oxide 0.3.77:
//   src/text/word_boundary.rs      :: DocumentScript (+ detect_from_characters)
//   src/text/script_detector.rs    :: CJKScript, DocumentLanguage, detect_cjk_script,
//                                     should_split_on_script_transition, handle_japanese_text,
//                                     handle_korean_text and their small-kana helpers
//   src/text/complex_script_detector.rs :: ComplexScript, detect_complex_script,
//                                     handle_{devanagari,thai,khmer,indic}_boundary
//   src/text/rtl_detector.rs       :: RTLScript, detect_rtl_script,
//                                     should_split_at_rtl_boundary and its helpers
//   src/text/cjk_punctuation.rs    :: get_cjk_punctuation_boundary_score and its classifiers
//
// The Rust helpers live in sibling modules; they are gathered here as nested members of
// ScriptSignals so the word-boundary port is self-contained. All code points are full
// Unicode scalar values (the CJK Extension B-F ranges are astral), never UTF-16 units.
namespace Xberg.Internal.PdfOxide.Text;

/// <summary>
/// Document script profile. word_boundary.rs uses it to skip whole families of script
/// detection: a Latin-only document never pays for RTL or CJK probing, which is the
/// difference between millions and thousands of predicate calls per batch.
/// </summary>
internal enum DocumentScript
{
    /// <summary>Latin-only: only space, TJ offset and geometric gap are consulted.</summary>
    Latin,

    /// <summary>CJK-dominant: RTL detection is skipped.</summary>
    Cjk,

    /// <summary>RTL-dominant (Arabic/Hebrew): CJK detection is skipped.</summary>
    Rtl,

    /// <summary>Complex scripts (Devanagari, Brahmic, Thai, Khmer).</summary>
    Complex,

    /// <summary>Mixed or unknown: every detector runs (slowest path).</summary>
    Mixed,
}

/// <summary>Port of <c>impl DocumentScript</c> in word_boundary.rs.</summary>
internal static class DocumentScriptDetector
{
    /// <summary>Sample size for classification; the profile is computed once per extraction.</summary>
    private const int SampleSize = 1000;

    /// <summary>
    /// Port of <c>DocumentScript::detect_from_characters</c>. Classifies from the first
    /// 1000 characters only — a document's script does not change halfway through, and the
    /// profile is a dispatch hint rather than a correctness input.
    /// </summary>
    internal static DocumentScript DetectFromCharacters(IReadOnlyList<CharacterInfo> characters)
    {
        if (characters.Count == 0) return DocumentScript.Latin; // Default to Latin for empty documents

        bool hasRtl = false, hasCjk = false, hasComplex = false;
        int sample = Math.Min(characters.Count, SampleSize);

        for (int i = 0; i < sample; i++)
        {
            int code = characters[i].Code;

            if ((code >= 0x0590 && code <= 0x08FF) || (code >= 0xFB1D && code <= 0xFDFF))
                hasRtl = true;

            if ((code >= 0x4E00 && code <= 0x9FFF)      // Han
                || (code >= 0x3040 && code <= 0x309F)   // Hiragana
                || (code >= 0x30A0 && code <= 0x30FF)   // Katakana
                || (code >= 0xAC00 && code <= 0xD7AF))  // Hangul
                hasCjk = true;

            // The Brahmic South-Asian blocks beyond Devanagari were once absent here, so those
            // documents classified as Latin/Mixed and never reached the complex-script boundary
            // rules — leaking spurious spaces after matras. They share Devanagari's matra/virama
            // boundary semantics, so they must all raise the same flag.
            if ((code >= 0x0900 && code <= 0x097F)      // Devanagari
                || (code >= 0x0980 && code <= 0x09FF)   // Bengali
                || (code >= 0x0B80 && code <= 0x0BFF)   // Tamil
                || (code >= 0x0C00 && code <= 0x0C7F)   // Telugu
                || (code >= 0x0C80 && code <= 0x0CFF)   // Kannada
                || (code >= 0x0D00 && code <= 0x0D7F)   // Malayalam
                || (code >= 0x0E00 && code <= 0x0E7F)   // Thai
                || (code >= 0x1780 && code <= 0x17FF))  // Khmer
                hasComplex = true;
        }

        // Same arm order as the Rust match: a CJK or RTL signal wins over a complex-script
        // signal, and only a document carrying *both* CJK and RTL falls through to Mixed.
        if (!hasRtl && !hasCjk && !hasComplex) return DocumentScript.Latin;
        if (!hasRtl && hasCjk) return DocumentScript.Cjk;
        if (hasRtl && !hasCjk) return DocumentScript.Rtl;
        if (hasComplex) return DocumentScript.Complex;
        return DocumentScript.Mixed;
    }
}

/// <summary>
/// Per-code-point script classification and the script-specific boundary rules the
/// word-boundary detector consults. Every boundary handler is tri-state:
/// <c>true</c> = must break, <c>false</c> = must not break, <c>null</c> = defer to the
/// TJ offset / geometric signals.
/// </summary>
internal static class ScriptSignals
{
    // ========================================================================
    // CJK SCRIPT DETECTION (script_detector.rs)
    // ========================================================================

    /// <summary>Port of <c>script_detector::CJKScript</c>.</summary>
    internal enum CjkScript
    {
        /// <summary>Han ideographs (U+4E00-U+9FFF): Chinese, Japanese Kanji, Korean Hanja.</summary>
        Han,

        /// <summary>Han Extension A (U+3400-U+4DBF).</summary>
        HanExtensionA,

        /// <summary>Han Extension B-F (U+20000-U+2EBEF), astral plane.</summary>
        HanExtensionBF,

        /// <summary>Hiragana (U+3040-U+309F).</summary>
        Hiragana,

        /// <summary>Katakana (U+30A0-U+30FF).</summary>
        Katakana,

        /// <summary>Halfwidth Katakana (U+FF61-U+FF9F).</summary>
        HalfwidthKatakana,

        /// <summary>Hangul syllables (U+AC00-U+D7AF).</summary>
        Hangul,

        /// <summary>CJK ideographic annotation symbols (U+3190-U+319F).</summary>
        CjkSymbol,
    }

    /// <summary>Port of <c>script_detector::DocumentLanguage</c>.</summary>
    internal enum DocumentLanguage
    {
        /// <summary>Japanese (Hiragana or Katakana present).</summary>
        Japanese,

        /// <summary>Korean (Hangul present).</summary>
        Korean,

        /// <summary>Chinese (Han only).</summary>
        Chinese,
    }

    /// <summary>Port of <c>script_detector::detect_cjk_script</c>.</summary>
    internal static CjkScript? DetectCjkScript(int code)
    {
        // Han covers roughly 90% of CJK text, so it is tested before everything else.
        if (code >= 0x4E00 && code <= 0x9FFF) return CjkScript.Han;

        if (code >= 0x3400 && code <= 0x4DBF) return CjkScript.HanExtensionA;
        if (code >= 0x20000 && code <= 0x2EBEF) return CjkScript.HanExtensionBF;
        if (code >= 0x3040 && code <= 0x309F) return CjkScript.Hiragana;
        if (code >= 0x30A0 && code <= 0x30FF) return CjkScript.Katakana;
        if (code >= 0xFF61 && code <= 0xFF9F) return CjkScript.HalfwidthKatakana;
        if (code >= 0xAC00 && code <= 0xD7AF) return CjkScript.Hangul;
        if (code >= 0x3190 && code <= 0x319F) return CjkScript.CjkSymbol;
        return null;
    }

    /// <summary>Port of <c>script_detector::should_split_on_script_transition</c>.</summary>
    internal static bool? ShouldSplitOnScriptTransition(
        CjkScript? prevScript,
        CjkScript? currScript,
        DocumentLanguage? language)
    {
        if (prevScript.HasValue && currScript.HasValue)
            return ShouldSplitCjkTransition(prevScript.Value, currScript.Value, language);

        // Crossing the CJK boundary in either direction is a language change, and therefore
        // a word break even though neither side carries a space glyph.
        if (prevScript.HasValue || currScript.HasValue) return true;

        return null;
    }

    private static bool? ShouldSplitCjkTransition(CjkScript prev, CjkScript curr, DocumentLanguage? language)
    {
        if (prev == curr) return null;

        return language switch
        {
            DocumentLanguage.Japanese => HandleJapaneseTransition(prev, curr),
            DocumentLanguage.Korean => HandleKoreanTransition(prev, curr),
            // Chinese or unknown: Han-to-Han carries no script signal, so defer entirely.
            _ => null,
        };
    }

    /// <summary>
    /// Port of <c>script_detector::handle_japanese_transition</c>. Japanese freely mixes
    /// Kanji, Hiragana and Katakana inside a single word (kanji + okurigana), so none of
    /// those transitions may break.
    /// </summary>
    private static bool? HandleJapaneseTransition(CjkScript prev, CjkScript curr)
    {
        bool prevHan = IsHan(prev), currHan = IsHan(curr);
        bool prevKata = prev is CjkScript.Katakana or CjkScript.HalfwidthKatakana;
        bool currKata = curr is CjkScript.Katakana or CjkScript.HalfwidthKatakana;

        if (prevHan && curr == CjkScript.Hiragana) return false;
        if (prev == CjkScript.Hiragana && currHan) return false;
        if (prevHan && currKata) return false;
        if (prevKata && currHan) return false;
        if (prev == CjkScript.Hiragana && currKata) return false;
        if (prevKata && curr == CjkScript.Hiragana) return false;
        if (prevKata && currKata) return false; // Katakana ↔ halfwidth Katakana

        return null;
    }

    /// <summary>
    /// Port of <c>script_detector::handle_korean_transition</c>. Korean mixes Hangul with
    /// Hanja inside one word, so that transition alone is not a break.
    /// </summary>
    private static bool? HandleKoreanTransition(CjkScript prev, CjkScript curr)
    {
        if (prev == CjkScript.Hangul && IsHan(curr)) return false;
        if (IsHan(prev) && curr == CjkScript.Hangul) return false;
        return null;
    }

    private static bool IsHan(CjkScript script) =>
        script is CjkScript.Han or CjkScript.HanExtensionA or CjkScript.HanExtensionBF;

    /// <summary>Port of <c>script_detector::infer_document_language</c>.</summary>
    internal static DocumentLanguage? InferDocumentLanguage(IReadOnlyList<(CjkScript Script, int Count)> scripts)
    {
        if (scripts.Count == 0) return null;

        bool hasHiragana = false, hasKatakana = false, hasHangul = false, hasHan = false;
        foreach (var (script, _) in scripts)
        {
            switch (script)
            {
                case CjkScript.Hiragana: hasHiragana = true; break;
                case CjkScript.Katakana:
                case CjkScript.HalfwidthKatakana: hasKatakana = true; break;
                case CjkScript.Hangul: hasHangul = true; break;
                case CjkScript.Han:
                case CjkScript.HanExtensionA:
                case CjkScript.HanExtensionBF: hasHan = true; break;
            }
        }

        if (hasHiragana || hasKatakana) return DocumentLanguage.Japanese;
        if (hasHangul) return DocumentLanguage.Korean;
        if (hasHan) return DocumentLanguage.Chinese;
        return null;
    }

    /// <summary>Port of <c>script_detector::is_small_hiragana</c> (sokuon / yōon).</summary>
    internal static bool IsSmallHiragana(int code) => code is
        0x3041 or 0x3043 or 0x3045 or 0x3047 or 0x3049 or
        0x3063 or 0x3083 or 0x3085 or 0x3087 or 0x308E;

    /// <summary>Port of <c>script_detector::is_small_katakana</c>.</summary>
    internal static bool IsSmallKatakana(int code) => code is
        0x30A1 or 0x30A3 or 0x30A5 or 0x30A7 or 0x30A9 or
        0x30C3 or 0x30E3 or 0x30E5 or 0x30E7 or 0x30EE or 0x30F5 or 0x30F6;

    /// <summary>Port of <c>script_detector::is_combining_mark</c> (dakuten / handakuten).</summary>
    internal static bool IsCombiningMark(int code) => code is 0x3099 or 0x309A or 0xFF9E or 0xFF9F;

    /// <summary>
    /// Port of <c>script_detector::is_japanese_modifier</c>. Small kana and voicing marks
    /// modify the preceding glyph, so a break in front of them would split one mora.
    /// </summary>
    internal static bool IsJapaneseModifier(int code) =>
        IsSmallHiragana(code) || IsSmallKatakana(code) || IsCombiningMark(code);

    /// <summary>Port of <c>script_detector::handle_japanese_text</c>.</summary>
    internal static bool? HandleJapaneseText(
        CharacterInfo prevChar,
        CharacterInfo currChar,
        CjkScript? prevScript,
        CjkScript? currScript)
    {
        _ = prevChar;
        if (IsJapaneseModifier(currChar.Code)) return false;
        return ShouldSplitOnScriptTransition(prevScript, currScript, DocumentLanguage.Japanese);
    }

    /// <summary>Port of <c>script_detector::handle_korean_text</c>.</summary>
    internal static bool? HandleKoreanText(
        CharacterInfo prevChar,
        CharacterInfo currChar,
        CjkScript? prevScript,
        CjkScript? currScript)
    {
        _ = prevChar;
        _ = currChar;
        return ShouldSplitOnScriptTransition(prevScript, currScript, DocumentLanguage.Korean);
    }

    // ========================================================================
    // CJK PUNCTUATION (cjk_punctuation.rs)
    // ========================================================================

    /// <summary>Port of <c>cjk_punctuation::TextDensity</c>.</summary>
    internal enum TextDensity
    {
        /// <summary>Under ~500 characters per page.</summary>
        Low,

        /// <summary>~500-2000 characters per page.</summary>
        Medium,

        /// <summary>Over ~2000 characters per page.</summary>
        High,
    }

    /// <summary>Port of <c>TextDensity::classify</c>.</summary>
    internal static TextDensity ClassifyTextDensity(int charCount, int pageCount)
    {
        if (pageCount == 0) return TextDensity.Medium;

        int charsPerPage = charCount / pageCount;
        if (charsPerPage <= 500) return TextDensity.Low;
        if (charsPerPage <= 2000) return TextDensity.Medium;
        return TextDensity.High;
    }

    /// <summary>
    /// Port of <c>TextDensity::score_multiplier</c>. Sparse layouts get a conservative
    /// multiplier so incidental punctuation does not shatter the text into single glyphs.
    /// </summary>
    internal static float ScoreMultiplier(TextDensity density) => density switch
    {
        TextDensity.Low => 0.6f,
        TextDensity.High => 1.4f,
        _ => 1.0f,
    };

    /// <summary>Port of <c>cjk_punctuation::is_sentence_ending_punctuation</c> (。！？).</summary>
    internal static bool IsSentenceEndingPunctuation(int code) => code is 0x3002 or 0xFF01 or 0xFF1F;

    /// <summary>Port of <c>cjk_punctuation::is_enumeration_punctuation</c> (、，；：).</summary>
    internal static bool IsEnumerationPunctuation(int code) => code is 0x3001 or 0xFF0C or 0xFF1B or 0xFF1A;

    /// <summary>Port of <c>cjk_punctuation::is_bracket_punctuation</c>.</summary>
    internal static bool IsBracketPunctuation(int code) =>
        (code >= 0x3008 && code <= 0x3011)   // Angle and corner brackets
        || (code >= 0x3014 && code <= 0x3015) // Tortoise shell brackets
        || (code >= 0xFF08 && code <= 0xFF09) // Fullwidth parentheses
        || (code >= 0xFF3B && code <= 0xFF3D) // Fullwidth square brackets
        || (code >= 0xFF5B && code <= 0xFF5D); // Fullwidth curly brackets

    /// <summary>Port of <c>cjk_punctuation::is_other_cjk_punctuation</c>.</summary>
    internal static bool IsOtherCjkPunctuation(int code) =>
        code is 0x3000 or 0x3003 or 0x30FB or 0xFF0E or 0xFF0D or 0xFF5E;

    /// <summary>Port of <c>cjk_punctuation::is_fullwidth_punctuation</c>.</summary>
    internal static bool IsFullwidthPunctuation(int code) =>
        IsSentenceEndingPunctuation(code) || IsEnumerationPunctuation(code)
        || IsBracketPunctuation(code) || IsOtherCjkPunctuation(code);

    /// <summary>Port of <c>cjk_punctuation::is_opening_bracket</c>.</summary>
    internal static bool IsOpeningBracket(int code) => code is
        0x3008 or 0x300A or 0x300C or 0x300E or 0x3010 or 0x3014 or 0xFF08 or 0xFF3B or 0xFF5B;

    /// <summary>Port of <c>cjk_punctuation::is_closing_bracket</c>.</summary>
    internal static bool IsClosingBracket(int code) => code is
        0x3009 or 0x300B or 0x300D or 0x300F or 0x3011 or 0x3015 or 0xFF09 or 0xFF3D or 0xFF5D;

    /// <summary>Port of <c>cjk_punctuation::get_base_punctuation_score</c>.</summary>
    private static float GetBasePunctuationScore(int code)
    {
        if (IsSentenceEndingPunctuation(code)) return 1.0f; // Definite boundary
        if (IsEnumerationPunctuation(code)) return 0.9f;    // Strong boundary signal
        if (IsBracketPunctuation(code)) return 0.8f;        // Paired boundary
        if (IsOtherCjkPunctuation(code)) return 0.7f;       // Context-dependent
        return 0.0f;
    }

    /// <summary>Port of <c>cjk_punctuation::get_cjk_punctuation_boundary_score</c>.</summary>
    internal static float GetCjkPunctuationBoundaryScore(int code, TextDensity? density)
    {
        float baseScore = GetBasePunctuationScore(code);
        return density.HasValue ? baseScore * ScoreMultiplier(density.Value) : baseScore;
    }

    // ========================================================================
    // COMPLEX SCRIPTS (complex_script_detector.rs)
    // ========================================================================

    /// <summary>Port of <c>complex_script_detector::ComplexScript</c>.</summary>
    internal enum ComplexScript
    {
        /// <summary>Devanagari (U+0900-U+097F).</summary>
        Devanagari,

        /// <summary>Bengali (U+0980-U+09FF).</summary>
        Bengali,

        /// <summary>Gurmukhi (U+0A00-U+0A7F).</summary>
        Gurmukhi,

        /// <summary>Gujarati (U+0A80-U+0AFF).</summary>
        Gujarati,

        /// <summary>Oriya (U+0B00-U+0B7F).</summary>
        Oriya,

        /// <summary>Tamil (U+0B80-U+0BFF).</summary>
        Tamil,

        /// <summary>Telugu (U+0C00-U+0C7F).</summary>
        Telugu,

        /// <summary>Kannada (U+0C80-U+0CFF).</summary>
        Kannada,

        /// <summary>Malayalam (U+0D00-U+0D7F).</summary>
        Malayalam,

        /// <summary>Sinhala (U+0D80-U+0DFF).</summary>
        Sinhala,

        /// <summary>Thai (U+0E00-U+0E7F).</summary>
        Thai,

        /// <summary>Lao (U+0E80-U+0EFF).</summary>
        Lao,

        /// <summary>Khmer (U+1780-U+17FF).</summary>
        Khmer,

        /// <summary>Burmese (U+1000-U+109F).</summary>
        Burmese,

        /// <summary>Mongolian (U+1800-U+18AF).</summary>
        Mongolian,
    }

    /// <summary>Port of <c>complex_script_detector::detect_complex_script</c>.</summary>
    internal static ComplexScript? DetectComplexScript(int code)
    {
        // Devanagari is the most common South Asian script, so it is tested first.
        if (code >= 0x0900 && code <= 0x097F) return ComplexScript.Devanagari;

        if (code >= 0x0980 && code <= 0x09FF) return ComplexScript.Bengali;
        if (code >= 0x0A00 && code <= 0x0A7F) return ComplexScript.Gurmukhi;
        if (code >= 0x0A80 && code <= 0x0AFF) return ComplexScript.Gujarati;
        if (code >= 0x0B00 && code <= 0x0B7F) return ComplexScript.Oriya;
        if (code >= 0x0B80 && code <= 0x0BFF) return ComplexScript.Tamil;
        if (code >= 0x0C00 && code <= 0x0C7F) return ComplexScript.Telugu;
        if (code >= 0x0C80 && code <= 0x0CFF) return ComplexScript.Kannada;
        if (code >= 0x0D00 && code <= 0x0D7F) return ComplexScript.Malayalam;
        if (code >= 0x0D80 && code <= 0x0DFF) return ComplexScript.Sinhala;
        if (code >= 0x0E00 && code <= 0x0E7F) return ComplexScript.Thai;
        if (code >= 0x0E80 && code <= 0x0EFF) return ComplexScript.Lao;
        if (code >= 0x1780 && code <= 0x17FF) return ComplexScript.Khmer;
        if (code >= 0x1000 && code <= 0x109F) return ComplexScript.Burmese;
        if (code >= 0x1800 && code <= 0x18AF) return ComplexScript.Mongolian;
        return null;
    }

    /// <summary>Port of <c>complex_script_detector::is_complex_script</c>.</summary>
    internal static bool IsComplexScript(int code) => DetectComplexScript(code).HasValue;

    /// <summary>Port of <c>complex_script_detector::is_devanagari_diacritic</c>.</summary>
    internal static bool IsDevanagariDiacritic(int code) =>
        (code >= 0x0901 && code <= 0x0903)    // Candrabindu, anusvara, visarga
        || (code >= 0x093A && code <= 0x093C) // Vowel signs, nukta
        || (code >= 0x093E && code <= 0x094C) // Dependent vowel signs (matras)
        || code == 0x094D                     // Virama
        || (code >= 0x094E && code <= 0x0950) // Various marks
        || (code >= 0x0951 && code <= 0x0957) // Tone marks
        || (code >= 0x0962 && code <= 0x0963); // Vocalic L/R marks

    /// <summary>Port of <c>complex_script_detector::is_devanagari_virama</c>.</summary>
    internal static bool IsDevanagariVirama(int code) => code == 0x094D;

    /// <summary>Port of <c>complex_script_detector::is_devanagari_consonant</c>.</summary>
    internal static bool IsDevanagariConsonant(int code) => code >= 0x0915 && code <= 0x0939;

    /// <summary>Port of <c>complex_script_detector::is_devanagari_matra</c>.</summary>
    internal static bool IsDevanagariMatra(int code) => code >= 0x093E && code <= 0x094C;

    /// <summary>Port of <c>complex_script_detector::is_devanagari_anusvar_visarga</c>.</summary>
    internal static bool IsDevanagariAnusvarVisarga(int code) => code is 0x0902 or 0x0903;

    /// <summary>Port of <c>complex_script_detector::is_devanagari_nukta</c>.</summary>
    internal static bool IsDevanagariNukta(int code) => code == 0x093C;

    /// <summary>Port of <c>complex_script_detector::handle_devanagari_boundary</c>.</summary>
    internal static bool? HandleDevanagariBoundary(CharacterInfo prevChar, CharacterInfo currChar)
    {
        int prev = prevChar.Code, curr = currChar.Code;

        if (IsDevanagariVirama(prev)) return false;            // virama + consonant = conjunct
        if (IsDevanagariMatra(curr)) return false;             // matras attach to their base
        if (IsDevanagariNukta(curr)) return false;             // nukta modifies the preceding glyph
        if (IsDevanagariAnusvarVisarga(curr)) return false;    // attach to the syllable
        if (IsDevanagariDiacritic(prev) && IsDevanagariDiacritic(curr)) return false;

        // A dependent sign followed by another Devanagari glyph is intra-word continuation:
        // real word breaks in these documents carry an explicit space glyph. Without this,
        // the geometric test breaks after every matra (the matra carries its own advance).
        if (IsDevanagariDiacritic(prev) && curr >= 0x0900 && curr <= 0x097F) return false;

        return null;
    }

    /// <summary>Port of <c>complex_script_detector::is_thai_tone_mark</c>.</summary>
    internal static bool IsThaiToneMark(int code) => code >= 0x0E48 && code <= 0x0E4B;

    /// <summary>Port of <c>complex_script_detector::is_thai_vowel_modifier</c>.</summary>
    internal static bool IsThaiVowelModifier(int code) =>
        code == 0x0E31                          // MAI HAN-AKAT
        || (code >= 0x0E34 && code <= 0x0E37)   // Above vowels
        || (code >= 0x0E39 && code <= 0x0E3A);  // Below vowels

    /// <summary>Port of <c>complex_script_detector::is_thai_digit</c> (Thai and Western).</summary>
    internal static bool IsThaiDigit(int code) =>
        (code >= 0x0030 && code <= 0x0039) || (code >= 0x0E50 && code <= 0x0E59);

    /// <summary>Port of <c>complex_script_detector::is_thai_major_punctuation</c>.</summary>
    internal static bool IsThaiMajorPunctuation(int code) => code is 0x0E2F or 0x0E46 or 0x0E4F;

    /// <summary>Port of <c>complex_script_detector::handle_thai_boundary</c>.</summary>
    internal static bool? HandleThaiBoundary(CharacterInfo prevChar, CharacterInfo currChar)
    {
        int prev = prevChar.Code, curr = currChar.Code;

        if (IsThaiToneMark(curr)) return false;                     // tone marks attach to their base
        if (IsThaiVowelModifier(curr)) return false;                // vowels attach to consonants
        if (IsThaiDigit(prev) && IsThaiDigit(curr)) return false;   // keep numbers whole
        if (IsThaiMajorPunctuation(curr)) return true;              // sentence/phrase markers

        return null;
    }

    /// <summary>Port of <c>complex_script_detector::is_khmer_coeng</c>.</summary>
    internal static bool IsKhmerCoeng(int code) => code == 0x17D2;

    /// <summary>Port of <c>complex_script_detector::is_khmer_vowel_inherent</c>.</summary>
    internal static bool IsKhmerVowelInherent(int code) =>
        (code >= 0x17B4 && code <= 0x17B5)      // Inherent vowels
        || (code >= 0x17B7 && code <= 0x17BD)   // Above vowels
        || (code >= 0x17BE && code <= 0x17C5)   // Below/around vowels
        || code == 0x17C6;                      // NIKAHIT

    /// <summary>Port of <c>complex_script_detector::is_khmer_tone_mark</c>.</summary>
    internal static bool IsKhmerToneMark(int code) => code >= 0x17C9 && code <= 0x17CC;

    /// <summary>Port of <c>complex_script_detector::handle_khmer_boundary</c>.</summary>
    internal static bool? HandleKhmerBoundary(CharacterInfo prevChar, CharacterInfo currChar)
    {
        int prev = prevChar.Code, curr = currChar.Code;

        if (IsKhmerCoeng(prev)) return false;           // COENG + consonant = subscript
        if (IsKhmerVowelInherent(curr)) return false;   // vowels attach to consonants
        if (IsKhmerToneMark(curr)) return false;        // tone marks attach to syllables

        return null;
    }

    /// <summary>Port of <c>complex_script_detector::is_indic_diacritic</c>.</summary>
    internal static bool IsIndicDiacritic(int code) =>
        // Bengali
        (code >= 0x0981 && code <= 0x0983) || code == 0x09BC
        || (code >= 0x09BE && code <= 0x09CD) || code == 0x09D7
        || (code >= 0x09E2 && code <= 0x09E3)
        // Tamil
        || (code >= 0x0B82 && code <= 0x0B83)
        || (code >= 0x0BBE && code <= 0x0BCD) || code == 0x0BD7
        // Telugu
        || (code >= 0x0C01 && code <= 0x0C03)
        || (code >= 0x0C3E && code <= 0x0C4D)
        || (code >= 0x0C55 && code <= 0x0C56) || (code >= 0x0C62 && code <= 0x0C63)
        // Kannada
        || (code >= 0x0C81 && code <= 0x0C83) || code == 0x0CBC
        || (code >= 0x0CBE && code <= 0x0CCD)
        || (code >= 0x0CD5 && code <= 0x0CD6) || (code >= 0x0CE2 && code <= 0x0CE3)
        // Malayalam
        || (code >= 0x0D01 && code <= 0x0D03)
        || (code >= 0x0D3E && code <= 0x0D4D) || code == 0x0D57
        || (code >= 0x0D62 && code <= 0x0D63);

    /// <summary>Port of <c>complex_script_detector::handle_indic_boundary</c>.</summary>
    internal static bool? HandleIndicBoundary(CharacterInfo prevChar, CharacterInfo currChar)
    {
        int prev = prevChar.Code, curr = currChar.Code;

        if (IsIndicDiacritic(curr)) return false;   // matras, viramas and marks attach to their base
        if (IsIndicDiacritic(prev) && IsIndicDiacritic(curr)) return false;

        // matra→consonant is always intra-word; real breaks carry an explicit space glyph.
        // This is the dominant spurious-space direction, because the matra has its own advance
        // and a purely geometric test reads that advance as a gap.
        if (IsIndicDiacritic(prev) && DetectComplexScript(curr).HasValue) return false;

        return null;
    }

    /// <summary>Port of <c>complex_script_detector::is_complex_script_mark</c>.</summary>
    internal static bool IsComplexScriptMark(int code) =>
        IsDevanagariDiacritic(code) || IsIndicDiacritic(code)
        || IsThaiToneMark(code) || IsThaiVowelModifier(code)
        || IsKhmerVowelInherent(code) || IsKhmerToneMark(code) || IsKhmerCoeng(code);

    // ========================================================================
    // RTL SCRIPTS (rtl_detector.rs)
    // ========================================================================

    /// <summary>Port of <c>rtl_detector::RTLScript</c>.</summary>
    internal enum RtlScript
    {
        /// <summary>Arabic main block (U+0600-U+06FF).</summary>
        Arabic,

        /// <summary>Arabic Supplement (U+0750-U+077F).</summary>
        ArabicSupplement,

        /// <summary>Arabic Extended-A (U+08A0-U+08FF).</summary>
        ArabicExtendedA,

        /// <summary>Hebrew (U+0590-U+05FF).</summary>
        Hebrew,

        /// <summary>Arabic Presentation Forms-A (U+FB50-U+FDFF).</summary>
        PresentationFormsA,

        /// <summary>Arabic Presentation Forms-B (U+FE70-U+FEFF).</summary>
        PresentationFormsB,
    }

    /// <summary>Port of <c>rtl_detector::detect_rtl_script</c>.</summary>
    internal static RtlScript? DetectRtlScript(int code)
    {
        // Arabic main range first: it is by far the most common RTL block.
        if (code >= 0x0600 && code <= 0x06FF) return RtlScript.Arabic;

        if (code >= 0x0590 && code <= 0x05FF) return RtlScript.Hebrew;
        if (code >= 0x0750 && code <= 0x077F) return RtlScript.ArabicSupplement;
        if (code >= 0x08A0 && code <= 0x08FF) return RtlScript.ArabicExtendedA;
        if (code >= 0xFB50 && code <= 0xFDFF) return RtlScript.PresentationFormsA;
        if (code >= 0xFE70 && code <= 0xFEFF) return RtlScript.PresentationFormsB;
        return null;
    }

    /// <summary>Port of <c>rtl_detector::is_rtl_text</c>.</summary>
    internal static bool IsRtlText(int code) => DetectRtlScript(code).HasValue;

    /// <summary>Port of <c>rtl_detector::is_arabic_diacritic</c>.</summary>
    internal static bool IsArabicDiacritic(int code) =>
        (code >= 0x064B && code <= 0x0658)      // Basic Arabic diacritics
        || (code >= 0x06D6 && code <= 0x06DC)   // Small high marks
        || (code >= 0x06DF && code <= 0x06E4)
        || (code >= 0x06E7 && code <= 0x06E8)
        || (code >= 0x06EA && code <= 0x06ED);  // Small low marks

    /// <summary>Port of <c>rtl_detector::is_arabic_letter</c> (TATWEEL U+0640 deliberately excluded).</summary>
    internal static bool IsArabicLetter(int code) =>
        (code >= 0x0621 && code <= 0x063A)
        || (code >= 0x0641 && code <= 0x064A)
        || (code >= 0x0750 && code <= 0x076D)
        || (code >= 0x08A0 && code <= 0x08B4)
        || (code >= 0x08B6 && code <= 0x08BD);

    /// <summary>
    /// Port of <c>rtl_detector::is_right_joining_arabic</c>. Unicode Joining_Type = R letters
    /// join to the preceding letter but never to the following one, so the cursive connection
    /// already breaks after them — a following space is visually indistinguishable from a
    /// producer artefact (ISO 32000-1 §14.8.2.3.3).
    /// </summary>
    internal static bool IsRightJoiningArabic(int code) =>
        (code >= 0x0622 && code <= 0x0625)      // alef madda / hamza-above / waw-hamza / hamza-below
        || code == 0x0627                       // alef
        || code == 0x0629                       // teh marbuta
        || code == 0x062F || code == 0x0630     // dal, thal
        || code == 0x0631 || code == 0x0632     // reh, zain
        || code == 0x0648                       // waw
        || (code >= 0x0671 && code <= 0x0673) || code == 0x0675 // alef wasla and variants
        || (code >= 0x0688 && code <= 0x0699)   // dal / reh block variants
        || code == 0x06C0 || (code >= 0x06C3 && code <= 0x06CB) || code == 0x06CD || code == 0x06CF
        || code == 0x06D2 || code == 0x06D3     // yeh barree
        || code == 0x06EE || code == 0x06EF;    // dal / reh with inverted V

    /// <summary>Port of <c>rtl_detector::is_hebrew_diacritic</c>.</summary>
    internal static bool IsHebrewDiacritic(int code) =>
        (code >= 0x05B0 && code <= 0x05BB)      // Hebrew vowel points
        || code == 0x05BC                       // DAGESH
        || code == 0x05BD                       // METEG
        || code == 0x05BF                       // RAFE
        || (code >= 0x05C1 && code <= 0x05C2)   // SHIN DOT, SIN DOT
        || (code >= 0x05C4 && code <= 0x05C5)   // UPPER DOT, LOWER DOT
        || code == 0x05C7;                      // QAMATS QATAN

    /// <summary>Port of <c>rtl_detector::is_hebrew_letter</c>.</summary>
    internal static bool IsHebrewLetter(int code) => code >= 0x05D0 && code <= 0x05EA;

    /// <summary>Port of <c>rtl_detector::is_hebrew_punctuation</c> (GERESH, GERSHAYIM).</summary>
    internal static bool IsHebrewPunctuation(int code) => code is 0x05F3 or 0x05F4;

    /// <summary>Port of <c>rtl_detector::is_rtl_diacritic</c>.</summary>
    internal static bool IsRtlDiacritic(int code) => IsArabicDiacritic(code) || IsHebrewDiacritic(code);

    /// <summary>
    /// Port of <c>rtl_detector::normalize_arabic_contextual_form</c>. Only the handful of
    /// presentation forms Rust maps are mapped here; the remaining ~600 fall through
    /// unchanged, exactly as upstream.
    /// </summary>
    internal static int NormalizeArabicContextualForm(int code) => code switch
    {
        0xFB50 => 0x0671, // ALEF WASLA
        0xFE82 => 0x0627, // ALEF FINAL
        0xFE8D => 0x0627, // ALEF ISOLATED
        0xFE8E => 0x0627, // ALEF FINAL
        0xFE8F => 0x0628, // BEH ISOLATED
        0xFE90 => 0x0628, // BEH FINAL
        0xFE91 => 0x0628, // BEH INITIAL
        0xFE92 => 0x0628, // BEH MEDIAL
        _ => code,
    };

    /// <summary>Port of <c>rtl_detector::is_lam_alef_ligature</c>.</summary>
    internal static bool IsLamAlefLigature(int code) => code >= 0xFEF5 && code <= 0xFEFC;

    /// <summary>Port of <c>rtl_detector::decompose_lam_alef</c>.</summary>
    internal static (int Lam, int Alef)? DecomposeLamAlef(int code) => code switch
    {
        0xFEFB or 0xFEFC => (0x0644, 0x0627), // LAM + ALEF
        0xFEF5 or 0xFEF6 => (0x0644, 0x0622), // LAM + ALEF WITH MADDA ABOVE
        0xFEF7 or 0xFEF8 => (0x0644, 0x0623), // LAM + ALEF WITH HAMZA ABOVE
        0xFEF9 or 0xFEFA => (0x0644, 0x0625), // LAM + ALEF WITH HAMZA BELOW
        _ => null,
    };

    /// <summary>Port of <c>rtl_detector::is_eastern_arabic_digit</c>.</summary>
    internal static bool IsEasternArabicDigit(int code) => code >= 0x06F0 && code <= 0x06F9;

    /// <summary>Port of <c>rtl_detector::is_arabic_number</c> (Western and Eastern digits).</summary>
    internal static bool IsArabicNumber(int code) =>
        (code >= 0x0030 && code <= 0x0039) || (code >= 0x06F0 && code <= 0x06F9);

    /// <summary>Port of <c>rtl_detector::is_arabic_punctuation</c> (private in Rust).</summary>
    private static bool IsArabicPunctuation(int code) => code is
        0x060C or 0x061B or 0x061F or 0x066A or 0x066B or 0x066C or 0x066D;

    /// <summary>
    /// Port of <c>rtl_detector::should_split_at_rtl_boundary</c>. The <c>context</c> argument
    /// is accepted (and ignored) to mirror the Rust signature.
    /// </summary>
    internal static bool? ShouldSplitAtRtlBoundary(
        CharacterInfo prevChar,
        CharacterInfo currChar,
        BoundaryContext? context)
    {
        _ = context;

        int prev = prevChar.Code, curr = currChar.Code;
        bool prevIsRtl = IsRtlText(prev), currIsRtl = IsRtlText(curr);

        if (curr == 0x0020 || prev == 0x0020) return true;

        // Digit runs are checked before the RTL gate because Western digits are not RTL,
        // yet an Arabic number written with them must stay one token.
        if (IsArabicNumber(prev) && IsArabicNumber(curr)) return false;

        if (!prevIsRtl && !currIsRtl) return null;

        // TATWEEL is a pure elongation glyph inserted for justification, never a break.
        if (curr == 0x0640 || prev == 0x0640) return false;

        if (IsRtlDiacritic(curr)) return false;
        if (IsRtlDiacritic(prev) && IsRtlDiacritic(curr)) return false;

        // In RTL runs producers use much smaller offsets than in Latin, so the RTL path
        // keeps its own fixed -50 trigger rather than the adaptive Latin threshold.
        if (prevChar.TjOffset is int tj && tj < -50) return true;

        if (prevIsRtl != currIsRtl && !(IsArabicNumber(prev) && IsArabicNumber(curr))) return true;

        if (IsArabicPunctuation(curr) || IsHebrewPunctuation(curr)) return true;

        // Consecutive letters of one cursive script are joined; never break inside the join.
        if ((IsArabicLetter(prev) || IsArabicLetter(NormalizeArabicContextualForm(prev)))
            && (IsArabicLetter(curr) || IsArabicLetter(NormalizeArabicContextualForm(curr))))
            return false;

        if (IsHebrewLetter(prev) && IsHebrewLetter(curr)) return false;

        if (prevIsRtl && currIsRtl) return false;

        return null;
    }
}
