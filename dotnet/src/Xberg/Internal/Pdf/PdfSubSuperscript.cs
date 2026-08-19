// Ported from pdf_oxide's `merge_sub_superscript_spans` (document.rs).

namespace Xberg.Internal.Pdf;

/// <summary>
/// Reattaches super- and subscript spans to the text they belong to.
/// <para>
/// A producer sets `H₂SO₄` as three runs — `H SO`, then a small raised `2` and `4` — and the
/// reading-order sort, which works in baseline bands, has no reason to keep a raised glyph next
/// to the word it modifies. Left alone the digits drift: a formula loses its subscripts and they
/// resurface somewhere else on the page, or at the end of the document.
/// </para>
/// <para>
/// Each candidate is merged into the nearest preceding span whose advance edge it sits at, which
/// is where a super- or subscript is by definition.
/// </para>
/// </summary>
internal static class PdfSubSuperscript
{
    /// <summary>How far back to look for a span's base.</summary>
    private const int SearchLimit = 30;

    public static void Merge(List<TextSpan> spans)
    {
        int n = spans.Count;
        if (n < 2) return;

        double maxFontSize = 0;
        foreach (var s in spans) maxFontSize = Math.Max(maxFontSize, s.FontSize);
        if (maxFontSize <= 0) return;

        // (baseIndex, subIndex), in the order the subs appear.
        var toMerge = new List<(int Base, int Sub)>();
        var alreadySub = new HashSet<int>();

        for (int i = 0; i < n; i++)
        {
            var sub = spans[i];
            if (sub.Text.Length == 0) continue;

            bool indexCluster = IsIndexCluster(sub.Text);
            if (sub.Text.Length > 3 && !indexCluster) continue;

            // A run the producer explicitly raised or lowered is authoritative: it may be full
            // size and carry characters no charset gate would accept.
            bool riseFlagged = Math.Abs(sub.TextRiseRatio) >= 0.10;
            if (!riseFlagged && !indexCluster && !sub.Text.All(IsSubChar)) continue;
            if (!riseFlagged && sub.FontSize >= maxFontSize * 0.80) continue;

            // Digits may share the base's baseline: some producers put the visual rise in the
            // glyph's own outline rather than the text position, so there is no offset to see.
            bool numericSub = !riseFlagged && sub.Text.All(c => char.IsAsciiDigit(c) || c == ',');

            int bestIndex = -1;
            double bestDistance = double.MaxValue;
            int from = Math.Max(0, i - SearchLimit);

            for (int j = i - 1; j >= from; j--)
            {
                if (alreadySub.Contains(j)) continue;
                var baseSpan = spans[j];

                if (!riseFlagged && baseSpan.FontSize < sub.FontSize * 1.25) continue;
                if (!IsValidBase(baseSpan.Text)) continue;

                double xDistance = sub.X - (baseSpan.X + baseSpan.Width);
                double baseFontSize = Math.Max(1.0, baseSpan.FontSize);
                // A real super- or subscript sits at the base's advance edge, give or take a
                // fraction of an em; anything further along the line belongs to another word.
                if (xDistance < -0.1 * baseFontSize || xDistance > 0.25 * baseFontSize) continue;

                double yDistance = Math.Abs(baseSpan.Y - sub.Y);
                double yFloor = numericSub ? 0.0 : baseSpan.FontSize * 0.12;
                // The floor keeps same-line small caps out; the ceiling keeps a marker on the
                // next baseline from being dragged up.
                if (yDistance < yFloor || yDistance > baseSpan.FontSize * 0.75) continue;

                double score = Math.Abs(xDistance);
                if (score < bestDistance) { bestDistance = score; bestIndex = j; }
            }

            if (bestIndex >= 0) { toMerge.Add((bestIndex, i)); alreadySub.Add(i); }
        }

        if (toMerge.Count == 0) return;

        foreach (var (baseIndex, subIndex) in toMerge)
        {
            var baseSpan = spans[baseIndex];
            var sub = spans[subIndex];
            baseSpan.Text += sub.Text;
            // The width has to cover the sub as well, or the gap to whatever follows reads as
            // wider than it is and a spurious space appears.
            double subRight = sub.X + sub.Width;
            if (subRight > baseSpan.X + baseSpan.Width) baseSpan.Width = subRight - baseSpan.X;
        }

        var merged = new HashSet<int>(toMerge.Select(p => p.Sub));
        for (int i = n - 1; i >= 0; i--)
            if (merged.Contains(i)) spans.RemoveAt(i);
    }

    /// <summary>
    /// A comma-joined run of digits the producer set as one super- or subscript: an F-statistic's
    /// degrees of freedom, or a multi-affiliation marker. Longer than a plain sub but still one.
    /// </summary>
    private static bool IsIndexCluster(string text) =>
        text.Length >= 3
        && text.Contains(',')
        && text.All(c => char.IsAsciiDigit(c) || c == ',')
        && text[0] != ','
        && text[^1] != ',';

    /// <summary>
    /// Characters a super- or subscript is made of: plain ASCII as the extractor produces it, and
    /// the Unicode super/subscript codepoints where a font maps to them directly.
    /// </summary>
    private static bool IsSubChar(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '²' or '³' or '¹'
        || (c >= '⁰' && c <= '₟');

    /// <summary>
    /// Whether a span can host a super- or subscript. Single characters always can — a formula's
    /// variable. Two characters can unless they are an ordinary lowercase word. Longer spans can
    /// only when they end in an acronym, since a producer often emits a whole wrapped line as one
    /// span and the subscript belongs to the acronym at its end.
    /// </summary>
    private static bool IsValidBase(string text) => text.Length switch
    {
        0 => false,
        1 => true,
        2 => !char.IsAsciiLetterLower(text[0]) || !char.IsAsciiLetterLower(text[1]),
        _ => text.Reverse().TakeWhile(char.IsAsciiLetterUpper).Count() >= 2,
    };
}
