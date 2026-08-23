// Pure text/geometry helpers for the span-spacing decision, ported from
// pdf_oxide-0.3.77 src/extractors/text.rs lines 934-1190:
//   corrected_space_gap, starts_with_agl_ligature, is_monospace_font,
//   is_pictographic, strip_cjk_digit_boundary_spaces,
//   strip_prime_decimal_boundary_spaces, decimal_gap_has_ink,
//   gap_has_intervening_glyph.
//
// Rust iterates `chars()` (Unicode scalar values); the string helpers below
// iterate runes so the supplementary-plane ranges (CJK Ext B, emoji) match.
using System.Text;

namespace Xberg.Internal.PdfOxide.Text;

internal static class OxTextHelpers
{
    /// <summary>
    /// Recover an honest inter-glyph gap for the space-insertion decision
    /// (text.rs:934). When the previous span's font has no /Widths array,
    /// FontInfo substitutes a fixed ~0.55em advance that over-reports
    /// proportional Latin glyphs; the inflated bbox pushes the right edge past
    /// the real glyph end and drives the raw gap negative. Only then is the
    /// inflation divided out (0.55em / 0.45em ~ 1.22).
    ///
    /// The correction is deliberately gated on <c>rawGap &lt; 0</c>: inflating a
    /// non-overlapping gap manufactures a phantom word space and tears
    /// edge-to-edge runs apart (a "SalesForce" emitted as "SalesF" + "orce" with
    /// a zero gap would become "SalesF orce").
    /// </summary>
    internal static float CorrectedSpaceGap(float rawGap, bool reliableWidths, float bboxWidth, bool textEmpty)
    {
        if (!reliableWidths && rawGap < 0.0f && bboxWidth > 0.0f && !textEmpty)
        {
            return rawGap + bboxWidth * (1.0f - 1.0f / 1.22f);
        }
        return rawGap;
    }

    /// <summary>
    /// True when the cluster <em>is</em> a bare AGL Latin ligature (text.rs:967) —
    /// a lone U+FB00..U+FB06 glyph or the ASCII fallbacks "ff"/"fi"/"fl"/"ffi"/"ffl".
    /// pdfTeX emits the ligature as its own cluster between intra-word fragments
    /// ("di" - "ﬃ" - "cult"); the surrounding kerning otherwise reads as a word
    /// gap and yields "di ff cult". A cluster that merely <em>starts</em> with a
    /// ligature ("ﬂuid") is a whole word whose leading boundary is a real space,
    /// so it returns false.
    /// </summary>
    internal static bool StartsWithAglLigature(string text)
    {
        var runes = text.EnumerateRunes();
        if (!runes.MoveNext())
        {
            return false;
        }

        int first = runes.Current.Value;
        if (first >= 0xFB00 && first <= 0xFB06 && !runes.MoveNext())
        {
            return true;
        }

        return text is "ff" or "fi" or "fl" or "ffi" or "ffl";
    }

    // Monospace fonts emit one show-text op per glyph with one-em advances, which
    // fires the proportional-font space heuristic inside ordinary tokens
    // ("function add (a , b )"). Callers switch word_margin_ratio to 1.2 on a hit.
    private static readonly string[] MonoMarkers =
    [
        "mono",
        "courier",
        "consolas",
        "menlo",
        "fira code",
        "fira mono",
        "source code",
        "inconsolata",
        "cmtt",   // pdfTeX Computer Modern Typewriter
        "lmmono", // Latin Modern Mono (pdfTeX)
        "letter gothic",
        "ocr ",   // OCR-A, OCR-B
        "fixedsys",
        "terminal",
    ];

    /// <summary>Detect a monospace font by name, case-insensitively (text.rs:996).</summary>
    internal static bool IsMonospaceFont(string fontName)
    {
        string lower = fontName.ToLowerInvariant();
        foreach (string marker in MonoMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True for the main emoji / pictographic blocks (text.rs:1024). Used only as
    /// a word-spacing hint, so arrows (U+2190..U+21FF) and the math-operator
    /// blocks are deliberately excluded to leave symbolic/technical text alone.
    /// </summary>
    internal static bool IsPictographic(int codepoint) =>
        (codepoint >= 0x1F300 && codepoint <= 0x1FAFF) || // Misc & Supplemental Symbols and Pictographs, Ext-A
        (codepoint >= 0x1F000 && codepoint <= 0x1F0FF) || // Mahjong / Dominoes / Playing cards
        (codepoint >= 0x2600 && codepoint <= 0x27BF) ||   // Misc Symbols + Dingbats
        codepoint == 0xFE0F;                              // VS16 emoji presentation selector

    /// <inheritdoc cref="IsPictographic(int)"/>
    internal static bool IsPictographic(Rune c) => IsPictographic(c.Value);

    // Space-less scripts only. Hangul is excluded from the digit rule because
    // Korean is written with inter-word spaces, so "14 예" is a real boundary.
    private static bool IsCjk(int c) =>
        (c >= 0x3040 && c <= 0x30FF) ||     // Hiragana + Katakana
        (c >= 0x3400 && c <= 0x4DBF) ||     // CJK Ext A
        (c >= 0x4E00 && c <= 0x9FFF) ||     // CJK Unified
        (c >= 0x20000 && c <= 0x2A6DF) ||   // CJK Ext B
        (c >= 0xFF66 && c <= 0xFF9F);       // Halfwidth Katakana

    // Hangul IS included for the bracket rule: a bracket hugs its content in
    // every script, so "고양이(학명: …)" never has a space before the paren.
    private static bool IsCjkOrHangul(int c) =>
        IsCjk(c) ||
        (c >= 0xAC00 && c <= 0xD7A3) ||     // Hangul syllables
        (c >= 0x1100 && c <= 0x11FF) ||     // Hangul Jamo
        (c >= 0x3130 && c <= 0x318F);       // Hangul Compatibility Jamo

    private static bool IsHugBracket(int c) =>
        c is '(' or ')' or '[' or ']' or '{' or '}';

    private static bool IsAsciiDigit(int c) => c >= '0' && c <= '9';

    /// <summary>
    /// Drop an ASCII space sitting directly between a CJK ideograph/kana and an
    /// ASCII digit, or between a CJK/Hangul character and a hugging bracket
    /// (text.rs:1044). Chinese and Japanese attach embedded numbers to the
    /// surrounding ideographs ("公元前1000年"); some producers — notably
    /// headless-browser print-to-PDF — emit a stray space at that transition.
    /// CJK-to-CJK and CJK-to-letter spacing is left untouched.
    /// </summary>
    internal static string StripCjkDigitBoundarySpaces(string text)
    {
        if (!text.Contains(' '))
        {
            return text;
        }

        Rune[] chars = ToRunes(text);
        var outBuf = new StringBuilder(text.Length);
        int i = 0;
        while (i < chars.Length)
        {
            Rune c = chars[i];
            if (c.Value == ' ' && i > 0 && i + 1 < chars.Length)
            {
                int p = chars[i - 1].Value;
                int n = chars[i + 1].Value;
                if ((IsCjk(p) && IsAsciiDigit(n)) || (IsAsciiDigit(p) && IsCjk(n)))
                {
                    i += 1;
                    continue;
                }
                if ((IsCjkOrHangul(p) && IsHugBracket(n)) || (IsHugBracket(p) && IsCjkOrHangul(n)))
                {
                    i += 1;
                    continue;
                }
            }
            outBuf.Append(c.ToString());
            i += 1;
        }
        return outBuf.ToString();
    }

    private static bool IsPrime(int c) => c is 0x2032 or 0x2033 or 0x2034;

    /// <summary>
    /// Repair a space the geometric word-break heuristic injected inside a
    /// prime-notation number, e.g. 0″ .28 or 0″. 28 back to 0″.28 (text.rs:1110).
    /// A prime's metric advance (w0, ISO 32000-1 §9.4.4) is narrow relative to its
    /// inked form, so the gap to the following ".NN" reads wider than a space.
    /// Feet-and-inches like 5′ 6″ are left alone: that space sits between a prime
    /// and a <em>digit</em>, which is a genuine measurement boundary.
    /// </summary>
    internal static string StripPrimeDecimalBoundarySpaces(string text)
    {
        if (!text.Contains(' '))
        {
            return text;
        }

        Rune[] chars = ToRunes(text);
        var outBuf = new StringBuilder(text.Length);
        int i = 0;
        while (i < chars.Length)
        {
            Rune c = chars[i];
            if (c.Value == ' ' && i > 0 && i + 1 < chars.Length)
            {
                int p = chars[i - 1].Value;
                int n = chars[i + 1].Value;
                if (IsPrime(p) && n == '.')
                {
                    i += 1;
                    continue;
                }
                if (p == '.' && IsAsciiDigit(n) && i >= 2 && IsPrime(chars[i - 2].Value))
                {
                    i += 1;
                    continue;
                }
            }
            outBuf.Append(c.ToString());
            i += 1;
        }
        return outBuf.ToString();
    }

    /// <summary>
    /// True when any drawn glyph run puts ink inside the horizontal gap between
    /// <paramref name="left"/> and <paramref name="right"/>, overlapping their
    /// vertical band (text.rs:1149). Two pure-digit runs merge into one decimal
    /// amount only if the gap is empty — a separator glyph in the gap (the comma
    /// of a subscript index pair, a list delimiter) proves they are distinct
    /// tokens. The pair's own boxes bound the gap exactly, so a small epsilon
    /// keeps them and their touching neighbours from counting as intruders.
    /// </summary>
    internal static bool DecimalGapHasInk(IReadOnlyList<OxRect> inkBoxes, OxRect left, OxRect right)
    {
        const float Eps = 0.01f;
        float gapStart = left.X + left.Width;
        float gapEnd = right.X;
        if (gapEnd - gapStart <= 2.0f * Eps)
        {
            return false;
        }
        float bandBottom = MathF.Min(left.Y, right.Y);
        float bandTop = MathF.Max(left.Y + left.Height, right.Y + right.Height);
        foreach (OxRect b in inkBoxes)
        {
            if (b.X + b.Width > gapStart + Eps &&
                b.X < gapEnd - Eps &&
                b.Y < bandTop &&
                b.Y + b.Height > bandBottom)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a <em>full intervening glyph</em> occupies the gap between
    /// <paramref name="left"/> and <paramref name="right"/> (text.rs:1175) — e.g.
    /// a subscript drawn between a variable and the next symbol, which inflates
    /// the gap though both share a baseline. Unlike
    /// <see cref="DecimalGapHasInk"/> it demands the box cover at least 35% of the
    /// gap width, so a descender edge merely clipping the band does not count.
    /// That keeps the narrow-word-gap rescue from splitting a sub/superscript
    /// from its base while still recovering ordinary prose word gaps.
    /// </summary>
    internal static bool GapHasInterveningGlyph(IReadOnlyList<OxRect> inkBoxes, OxRect left, OxRect right)
    {
        float gapStart = left.X + left.Width;
        float gapEnd = right.X;
        float gapW = gapEnd - gapStart;
        if (gapW <= 0.5f)
        {
            return false;
        }
        float bandBottom = MathF.Min(left.Y, right.Y);
        float bandTop = MathF.Max(left.Y + left.Height, right.Y + right.Height);
        foreach (OxRect b in inkBoxes)
        {
            float overlap = MathF.Min(b.X + b.Width, gapEnd) - MathF.Max(b.X, gapStart);
            if (overlap > gapW * 0.35f && b.Y < bandTop && b.Y + b.Height > bandBottom)
            {
                return true;
            }
        }
        return false;
    }

    private static Rune[] ToRunes(string text)
    {
        var runes = new List<Rune>(text.Length);
        foreach (Rune r in text.EnumerateRunes())
        {
            runes.Add(r);
        }
        return runes.ToArray();
    }
}
