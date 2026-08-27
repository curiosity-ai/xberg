using System.Text;

namespace Xberg.Internal.Pdf;

/// <summary>
/// Repairs a page whose text is drawn sideways. Ports Rust
/// <c>extractors/pdf/rotation.rs</c>.
/// <para>
/// Word gaps in a rotated run are baked into that run's own baseline, not into page-x, so
/// concatenating its fragments in page order both glues adjacent words together and can read
/// them out of order. This regroups each maximal same-rotation run into lines by cross-axis
/// proximity, sorts each line along its own advance axis, and inserts a space wherever the gap
/// looks like a word boundary rather than kerning.
/// </para>
/// </summary>
internal static class PdfRotationRepair
{
    private const double UnrotatedDegrees = 0.0;
    private const double RotationToleranceDegrees = 0.001;

    /// <summary>Largest same-baseline gap that still counts as a kerning split rather than a
    /// word boundary, as a fraction of span height.</summary>
    private const double AtomicFragmentGapRatio = 0.15;

    /// <summary>Cross-axis spans within this fraction of the taller span's height belong to the
    /// same rotated-frame line rather than a new row.</summary>
    private const double RotatedLineCrossToleranceRatio = 0.5;

    /// <summary>
    /// Minimum share of a page's characters that must be rotated before the whole-page rewrite
    /// fires. The repair replaces the <em>entire</em> page's assembly with a verbatim
    /// concatenation that has none of the paragraph, line-break or fragmentation handling the
    /// normal assembler applies — so a page carrying one rotated caption or a three-character
    /// section tab would pay that cost across all its upright text to fix a few words.
    /// </summary>
    private const double MinRotatedTextShare = 0.2;

    private static bool IsUnrotated(double rotation) =>
        double.IsFinite(rotation) && Math.Abs(rotation - UnrotatedDegrees) <= RotationToleranceDegrees;

    private static bool SameRotation(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= RotationToleranceDegrees;

    /// <summary>Any span on the page carries a non-zero text-matrix rotation. The cheap gate in
    /// front of the repair: almost every page is entirely upright and skips it in one scan.</summary>
    public static bool PageHasRotatedSpans(IReadOnlyList<TextSpan> spans)
    {
        foreach (var s in spans) if (!IsUnrotated(s.RotationDegrees)) return true;
        return false;
    }

    /// <summary>
    /// Rotated spans make up at least <see cref="MinRotatedTextShare"/> of the page's text, by
    /// character count. Characters, not span count: a page can carry many short upright
    /// fragments beside one short rotated label, and span count would over-weight whichever
    /// side happens to be more fragmented.
    /// </summary>
    private static bool RotationIsDominant(IReadOnlyList<TextSpan> spans)
    {
        long rotated = 0, total = 0;
        foreach (var s in spans)
        {
            int chars = s.Text.Length;
            total += chars;
            if (!IsUnrotated(s.RotationDegrees)) rotated += chars;
        }
        return total > 0 && (double)rotated / total >= MinRotatedTextShare;
    }

    /// <summary>
    /// Repair a page containing rotated runs, or return <c>null</c> to leave the page's normal
    /// assembly completely untouched — which is the safety property this rests on: an upright
    /// page is never rewritten and its output cannot drift.
    /// </summary>
    public static string? RepairRotatedPageText(IReadOnlyList<TextSpan> spans)
    {
        if (!PageHasRotatedSpans(spans) || !RotationIsDominant(spans)) return null;

        // Ordering stays at identity: deciding a better cross-region order is the part that
        // genuinely needs layout hints, which the native path does not have.
        var order = Enumerable.Range(0, spans.Count).ToList();
        return AssembleReadingOrderText(spans, order);
    }

    /// <summary>Rotate a span's page-space origin into its own upright reading frame, returning
    /// (advance, cross): position along its reading direction, and along the axis lines stack
    /// on. Identity for unrotated spans.</summary>
    private static (double Advance, double Cross) UprightReadingOrigin(TextSpan span)
    {
        if (IsUnrotated(span.RotationDegrees)) return (span.X, span.Y);
        double rad = -span.RotationDegrees * Math.PI / 180.0;
        double sin = Math.Sin(rad), cos = Math.Cos(rad);
        return (span.X * cos - span.Y * sin, span.X * sin + span.Y * cos);
    }

    /// <summary>
    /// Assemble spans in <paramref name="order"/> into page text, grouping into maximal
    /// same-rotation runs first so a mixed page (rotated body beside an upright footer) never
    /// has one frame forced across the boundary.
    /// </summary>
    public static string AssembleReadingOrderText(IReadOnlyList<TextSpan> spans, IReadOnlyList<int> order)
    {
        var text = new StringBuilder();
        double paragraphGap = ParagraphGapThreshold(spans);
        int runStart = 0;
        while (runStart < order.Count)
        {
            double rotation = SpanRotation(spans, order[runStart]);
            int runEnd = runStart + 1;
            while (runEnd < order.Count && SameRotation(SpanRotation(spans, order[runEnd]), rotation)) runEnd++;

            if (runStart > 0 && text.Length > 0 && !char.IsWhiteSpace(text[^1])) text.Append(' ');
            AppendRun(text, spans, order, runStart, runEnd, rotation, paragraphGap);
            runStart = runEnd;
        }
        return text.ToString();
    }

    private static double SpanRotation(IReadOnlyList<TextSpan> spans, int index) =>
        index >= 0 && index < spans.Count ? spans[index].RotationDegrees : UnrotatedDegrees;

    /// <summary>Baseline gap above which two upright spans are a new paragraph rather than the
    /// next line. Median span height × 1.5, the same measure the page assembler uses.</summary>
    private static double ParagraphGapThreshold(IReadOnlyList<TextSpan> spans)
    {
        if (spans.Count == 0) return 1.5;
        var heights = new List<double>(spans.Count);
        foreach (var s in spans) heights.Add(s.Height);
        heights.Sort();
        return heights[heights.Count / 2] * 1.5;
    }

    /// <summary>Append one maximal same-rotation run. A rotated run is line-clustered,
    /// advance-sorted and gap-spaced; an upright run keeps the order it arrived in and is only
    /// given back the separators the page assembler would have put between its spans.</summary>
    private static void AppendRun(StringBuilder text, IReadOnlyList<TextSpan> spans,
        IReadOnlyList<int> order, int from, int to, double rotation, double paragraphGap)
    {
        if (IsUnrotated(rotation))
        {
            AppendUprightRun(text, spans, order, from, to, paragraphGap);
            return;
        }

        int lineStart = from;
        bool firstLine = true;
        while (lineStart < to)
        {
            var anchor = spans[order[lineStart]];
            double anchorCross = UprightReadingOrigin(anchor).Cross;
            int lineEnd = lineStart + 1;
            while (lineEnd < to)
            {
                var candidate = spans[order[lineEnd]];
                double candidateCross = UprightReadingOrigin(candidate).Cross;
                double tolerance = Math.Max(Math.Max(anchor.Height, candidate.Height), double.Epsilon)
                                   * RotatedLineCrossToleranceRatio;
                if (Math.Abs(candidateCross - anchorCross) > tolerance) break;
                lineEnd++;
            }

            if (!firstLine && text.Length > 0 && !char.IsWhiteSpace(text[^1])) text.Append(' ');
            firstLine = false;
            AppendRotatedLine(text, spans, order, lineStart, lineEnd);
            lineStart = lineEnd;
        }
    }

    /// <summary>
    /// Append an upright run, in the order it arrived, separating consecutive spans the way the
    /// page assembler would.
    /// </summary>
    /// <remarks>
    /// The repair replaces the whole page's assembly, so whatever it does not reproduce is lost —
    /// and concatenating an upright run verbatim loses every space and line break in it, welding
    /// "MICHAEL TRINSEY" to the catalogue number under it and every line of a narrow column to the
    /// next. That is the trade the dominance guard exists to bound, but a page can clear the guard
    /// on two long rotated captions and still be upright text almost everywhere, so the run pays it
    /// anyway. Ordering stays untouched — only the separators the concatenation dropped are put
    /// back, on the same geometry rule <c>append_span_separator</c> uses.
    /// </remarks>
    private static void AppendUprightRun(StringBuilder text, IReadOnlyList<TextSpan> spans,
        IReadOnlyList<int> order, int from, int to, double paragraphGap)
    {
        TextSpan? previous = null;
        for (int i = from; i < to; i++)
        {
            var span = spans[order[i]];
            if (previous is not null) text.Append(UprightSeparator(previous, span, paragraphGap));
            text.Append(span.Text);
            previous = span;
        }
    }

    /// <summary>The separator between two consecutive upright spans: "" (glued), " ", "\n" or
    /// "\n\n". Mirrors the tail of the page assembler's <c>SpanSeparator</c>.</summary>
    private static string UprightSeparator(TextSpan previous, TextSpan span, double paragraphGap)
    {
        if (EndsWithWhitespace(previous.Text) || StartsWithWhitespace(span.Text)) return "";

        double baselineGap = Math.Abs(previous.Y - span.Y);
        double effectiveHeight = Math.Max(Math.Max(span.Height, previous.Height), span.FontSize * 0.5);
        if (baselineGap < effectiveHeight * 0.5)
            return span.X - previous.Right > span.FontSize * 0.15 ? " " : "";
        return baselineGap > paragraphGap ? "\n\n" : "\n";
    }

    private static bool EndsWithWhitespace(string text) =>
        text.Length > 0 && char.IsWhiteSpace(text[^1]);

    private static bool StartsWithWhitespace(string text) =>
        text.Length > 0 && char.IsWhiteSpace(text[0]);

    /// <summary>Sort one rotated-frame line by advance-axis position and join it, spacing
    /// wherever the advance gap looks like a real word boundary rather than kerning.</summary>
    private static void AppendRotatedLine(StringBuilder text, IReadOnlyList<TextSpan> spans,
        IReadOnlyList<int> order, int from, int to)
    {
        var ordered = new List<int>(to - from);
        for (int i = from; i < to; i++) ordered.Add(order[i]);
        ordered.Sort((a, b) => UprightReadingOrigin(spans[a]).Advance.CompareTo(UprightReadingOrigin(spans[b]).Advance));

        double? previousAdvanceEnd = null;
        foreach (int index in ordered)
        {
            var span = spans[index];
            double advanceStart = UprightReadingOrigin(span).Advance;
            if (previousAdvanceEnd is { } previousEnd)
            {
                double gap = advanceStart - previousEnd;
                double kerningLimit = Math.Max(span.Height, double.Epsilon) * AtomicFragmentGapRatio;
                if (gap > kerningLimit && text.Length > 0 && !char.IsWhiteSpace(text[^1])) text.Append(' ');
            }
            text.Append(span.Text);
            previousAdvanceEnd = advanceStart + span.Width;
        }
    }
}
