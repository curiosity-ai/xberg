using System.Text;

namespace Xberg.Internal.Pdf;

/// <summary>
/// Text repair passes applied to assembled PDF element text. Ports the subset of Rust
/// <c>pdf/structure/text_repair.rs</c> that the extractor runs over every element
/// (<c>pdf/structure/pipeline.rs</c>): contextual ligature repair, Unicode ligature
/// expansion, then Unicode punctuation normalization — in that order.
/// </summary>
internal static class PdfTextRepair
{
    /// <summary>Run the element-level repair chain, in Rust's order.</summary>
    public static string Repair(string text)
    {
        if (text.Length == 0) return text;
        string t = RepairContextualLigatures(text);
        t = ExpandLigaturesWithSpaceAbsorption(t);
        return NormalizeUnicodeText(t);
    }

    /// <summary>
    /// Repair ligature corruption using contextual heuristics. Some PDF fonts have broken
    /// ToUnicode CMaps that map ligature glyphs to punctuation: <c>!</c> → fi/ff,
    /// <c>"</c> → ffi, <c>#</c> → fi, <c>*</c> → tt, <c>:</c> → ti, and an uppercase
    /// <c>M</c> between lowercase letters → tti.
    /// <para>
    /// Every rule is gated on its neighbours, so ordinary punctuation is untouched: there is
    /// deliberately no letter + <c>!</c> + end-of-string rule, because a sentence-final
    /// exclamation mark looks exactly like the corrupted form.
    /// </para>
    /// </summary>
    public static string RepairContextualLigatures(string text)
    {
        if (text.Length < 2) return text;

        var result = new StringBuilder(text.Length + 16);
        bool repaired = false;
        bool prevIsAlpha = false;
        bool prevIsSpaceOrStart = true;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            bool nextIsAlpha = char.IsLetter(next);
            bool nextIsLower = char.IsLower(next);
            bool nextIsVowel = next is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U';

            switch (ch)
            {
                case '!' when prevIsAlpha && nextIsVowel:
                    result.Append("ff"); repaired = true; break;
                case '!' when prevIsAlpha && nextIsAlpha:
                    result.Append("fi"); repaired = true; break;
                case '"' when prevIsAlpha && nextIsAlpha:
                    result.Append("ffi"); repaired = true; break;
                case '#' when prevIsAlpha && nextIsAlpha:
                case '#' when prevIsSpaceOrStart && nextIsLower:
                    result.Append("fi"); repaired = true; break;
                case '!' when prevIsSpaceOrStart && nextIsLower:
                    result.Append("fi"); repaired = true; break;
                case '*' when prevIsAlpha && nextIsAlpha:
                    result.Append("tt"); repaired = true; break;
                case ':' when prevIsAlpha && nextIsLower:
                    result.Append("ti"); repaired = true; break;
                case 'M' when prevIsAlpha && !prevIsSpaceOrStart:
                {
                    bool prevWasLower = i > 0 && char.IsLower(text[i - 1]);
                    if (prevWasLower && nextIsLower) { result.Append("tti"); repaired = true; }
                    else result.Append(ch);
                    break;
                }
                default:
                    result.Append(ch); break;
            }

            prevIsAlpha = char.IsLetter(ch);
            prevIsSpaceOrStart = char.IsWhiteSpace(ch);
        }

        return repaired ? result.ToString() : text;
    }

    /// <summary>
    /// Expand Unicode ligature characters (U+FB00–U+FB06) to their ASCII equivalents,
    /// absorbing a spurious space between the ligature glyph and the rest of the word — PDFs
    /// often emit "ﬁ eld", which must come out as "field".
    /// </summary>
    public static string ExpandLigaturesWithSpaceAbsorption(string text)
    {
        if (text.AsSpan().IndexOfAny(Ligatures) < 0) return text;

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            string? expansion = text[i] switch
            {
                'ﬀ' => "ff",
                'ﬁ' => "fi",
                'ﬂ' => "fl",
                'ﬃ' => "ffi",
                'ﬄ' => "ffl",
                'ﬅ' => "st",
                'ﬆ' => "st",
                _ => null,
            };

            if (expansion is null) { result.Append(text[i]); continue; }

            result.Append(expansion);

            // Absorb a following space only when a word character continues after it.
            if (i + 2 < text.Length && text[i + 1] == ' '
                && (char.IsLetterOrDigit(text[i + 2]) || text[i + 2] == '_'))
                i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Normalize Unicode punctuation to the ASCII forms the ground truth tokenizes on:
    /// curly quotes to straight, fraction slash to <c>/</c>, bullet to a middle dot.
    /// </summary>
    public static string NormalizeUnicodeText(string text)
    {
        if (text.AsSpan().IndexOfAny(NormalizePunctuation) < 0) return text;

        return text
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('⁄', '/')
            .Replace('•', '·');
    }

    private static readonly char[] Ligatures =
        { 'ﬀ', 'ﬁ', 'ﬂ', 'ﬃ', 'ﬄ', 'ﬅ', 'ﬆ' };

    private static readonly char[] NormalizePunctuation =
        { '‘', '’', '“', '”', '⁄', '•' };
}
