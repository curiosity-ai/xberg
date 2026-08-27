using System;
using System.Collections.Generic;

namespace Xberg.Internal.Text;

/// <summary>
/// Decides whether extracted text looks like it was never really decoded — the symptom of a PDF
/// whose fonts carry a broken or absent <c>/ToUnicode</c> CMap.
/// </summary>
/// <remarks>
/// <para>
/// The glyphs on such a page draw correctly and the character codes come out as ordinary Latin
/// letters, so nothing in the pipeline fails and nothing downstream can tell the difference: the
/// text is well-formed, indexable, and wrong. It reaches the index as thousands of words that
/// match no query and dilute every ranking, which is worse than extracting nothing at all — hence
/// a flag rather than a silent pass.
/// </para>
/// <para>
/// Two failure shapes, and they need different tests. A wrong CMap that is a <em>permutation</em>
/// of the alphabet (a font subset whose codes are offset — "Nachhaltige" drawn as
/// "1DFKKDOWLJH") keeps a normal letter-frequency profile, so only the vowel structure gives it
/// away. A CMap that <em>collapses</em> many codes onto one character ("Agaaaaa: AAAA AabaaAA")
/// keeps plausible vowels but destroys the frequency profile. Either alone misses half the cases.
/// </para>
/// <para>
/// Thresholds are set from the measured distribution over the project's 475-file corpus, not
/// from the two files that motivated the check, and each sits clear of the most extreme
/// legitimate document in that corpus: sheet music with hyphen-split lyrics reaches a 0.312
/// top-letter share and 3.83 bits, and a base64 attachment dump reaches a 0.245 vowel ratio with
/// 20% of its letters in long consonant runs. Nothing legitimate came within reach of both halves
/// of either rule.
/// </para>
/// </remarks>
internal static class UndecodedTextDetection
{
    /// <summary>Below this many Latin letters the statistics are noise, and a short caption
    /// legitimately looks like anything.</summary>
    private const int MinLettersToJudge = 200;

    /// <summary>Share of a document's alphabetic characters that must be Latin before the checks
    /// mean anything: they are all built on an alphabet that spells words out of vowels and
    /// consonants, which is not true of CJK, Arabic, Hebrew, Greek or Cyrillic text.</summary>
    private const double MinLatinShare = 0.9;

    // ── Collapse: many codes mapped onto one character ───────────────────────────
    // Corpus worst: 0.312 share / 3.83 bits, on sheet music whose lyrics are hyphen-split.

    private const double MaxTopLetterShare = 0.40;
    private const double MinLetterEntropyBits = 2.5;

    // ── Permutation: the alphabet is intact but shifted ──────────────────────────
    // Corpus worst pairing: 0.245 vowels with 8.7% of letters in long runs, on an email whose
    // body is a base64 attachment — and those are its two separate extremes, not one document
    // failing both. Both halves must fire, so neither extreme alone is enough.

    private const double MinVowelRatio = 0.22;
    private const double MaxLongConsonantRunShare = 0.15;

    /// <summary>A consonant run this long is essentially absent from real Latin-script prose.
    /// Counted within a word: any non-Latin character ends the run, so a run never spans the
    /// punctuation between two words.</summary>
    private const int LongConsonantRun = 5;

    private static bool IsLatinLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= 'À' && c <= 'ɏ');

    private static bool IsVowel(char c) =>
        c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y'
            or 'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'æ'
            or 'è' or 'é' or 'ê' or 'ë'
            or 'ì' or 'í' or 'î' or 'ï'
            or 'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø'
            or 'ù' or 'ú' or 'û' or 'ü'
            or 'ý' or 'ÿ' or 'œ';

    /// <summary>
    /// The reason the text looks undecoded, or <c>null</c> when it does not. The sentence is
    /// written to go straight into a warning a caller shows a user.
    /// </summary>
    public static string? Diagnose(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var counts = new Dictionary<char, int>();
        int letters = 0, alphabetic = 0, vowels = 0, inLongRun = 0, run = 0;

        foreach (char c in text)
        {
            if (char.IsLetter(c)) alphabetic++;
            if (!IsLatinLetter(c))
            {
                run = 0;
                continue;
            }

            letters++;
            char lower = char.ToLowerInvariant(c);
            counts[lower] = counts.GetValueOrDefault(lower) + 1;

            if (IsVowel(lower))
            {
                vowels++;
                run = 0;
            }
            else
            {
                run++;
                if (run >= LongConsonantRun) inLongRun++;
            }
        }

        if (letters < MinLettersToJudge) return null;
        if (alphabetic == 0 || (double)letters / alphabetic < MinLatinShare) return null;

        int top = 0;
        double entropy = 0.0;
        foreach (int n in counts.Values)
        {
            if (n > top) top = n;
            double p = (double)n / letters;
            entropy -= p * Math.Log2(p);
        }

        double topShare = (double)top / letters;
        if (topShare > MaxTopLetterShare || entropy < MinLetterEntropyBits)
        {
            return $"Extracted text has an implausible letter distribution "
                 + $"({topShare:P0} of letters are one character, {entropy:F1} bits of entropy). "
                 + "The document's fonts most likely carry a broken or missing /ToUnicode CMap, "
                 + "so the text is character codes rather than readable content.";
        }

        double vowelRatio = (double)vowels / letters;
        double longRunShare = (double)inLongRun / letters;
        if (vowelRatio < MinVowelRatio && longRunShare > MaxLongConsonantRunShare)
        {
            return $"Extracted text has an implausible vowel structure "
                 + $"({vowelRatio:P0} vowels, {longRunShare:P0} of letters in runs of "
                 + $"{LongConsonantRun}+ consonants). "
                 + "The document's fonts most likely carry a broken or missing /ToUnicode CMap, "
                 + "so the text is character codes rather than readable content.";
        }

        return null;
    }
}
