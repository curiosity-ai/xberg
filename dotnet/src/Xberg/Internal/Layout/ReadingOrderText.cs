using System.Text;

namespace Xberg.Internal.Layout;

/// <summary>
/// Rotation-aware assembly of an ordered span sequence into page text, ported from Rust
/// <c>extractors::pdf::rotation</c>.
/// </summary>
/// <remarks>
/// Plain concatenation in index order is correct only when every span shares the page's own
/// upright axis: the span producer bakes word gaps into a rotated run's <em>own</em> baseline,
/// not into page-x, so naive concatenation of a 90/180/270-degree run both glues adjacent words
/// together and can read the fragments out of order. Unrotated spans take the verbatim legacy
/// path, so this is byte-identical to plain concatenation whenever every span is upright — which
/// is the overwhelming majority of pages.
/// </remarks>
internal static class ReadingOrderText
{
    /// <summary>The <c>rotation_degrees</c> an unrotated span carries.</summary>
    private const float UnrotatedDegrees = 0.0f;

    private const float RotationToleranceDegrees = 0.001f;

    /// <summary>
    /// Maximum same-baseline gap that still represents a kerning-run split.
    /// </summary>
    /// <remarks>
    /// Shared with the reading-order planner's segment-level fragment reconciliation: the cutoff
    /// between kerning and a real word boundary is one decision, so it has one home.
    /// </remarks>
    internal const float AtomicFragmentGapRatio = 0.15f;

    /// <summary>
    /// Cross-axis spans within this fraction of the taller span's height are treated as the same
    /// rotated-frame line rather than a new row.
    /// </summary>
    private const float RotatedLineCrossToleranceRatio = 0.5f;

    /// <summary>Rust's <c>f32::EPSILON</c> — machine epsilon, not C#'s smallest denormal.</summary>
    internal const float F32Epsilon = 1.1920929e-7f;

    /// <summary>
    /// Rotate a span's page-space origin into its own upright reading frame.
    /// </summary>
    /// <returns>
    /// <c>(advance, cross)</c>: <c>advance</c> is the position along the span's own reading
    /// direction, <c>cross</c> the position along the axis lines stack on. The identity
    /// <c>(x, y)</c> for unrotated spans.
    /// </returns>
    internal static (float Advance, float Cross) UprightReadingOrigin(ReadingOrderSpan span)
    {
        if (IsUnrotated(span.RotationDegrees)) return (span.X, span.Y);
        double radians = -span.RotationDegrees * Math.PI / 180.0;
        float sin = (float)Math.Sin(radians);
        float cos = (float)Math.Cos(radians);
        return (span.X * cos - span.Y * sin, span.X * sin + span.Y * cos);
    }

    internal static bool IsUnrotated(float rotation) =>
        float.IsFinite(rotation) && MathF.Abs(rotation - UnrotatedDegrees) <= RotationToleranceDegrees;

    private static bool SameRotation(float left, float right) =>
        float.IsFinite(left) && float.IsFinite(right)
        && MathF.Abs(left - right) <= RotationToleranceDegrees;

    private static float SpanRotation(IReadOnlyList<ReadingOrderSpan> spans, int index) =>
        index >= 0 && index < spans.Count ? spans[index].RotationDegrees : UnrotatedDegrees;

    private static bool EndsWithWhitespace(StringBuilder text) =>
        text.Length > 0 && char.IsWhiteSpace(text[^1]);

    /// <summary>
    /// Assemble spans — already ordered by a producer of a span-index order — into page text.
    /// </summary>
    /// <remarks>
    /// Groups <paramref name="order"/> into maximal same-rotation runs first, so a mixed page
    /// (rotated body text beside an upright footer) never has one frame forced across the
    /// boundary.
    /// </remarks>
    public static string AssembleReadingOrderText(IReadOnlyList<ReadingOrderSpan> spans, IReadOnlyList<int> order)
    {
        var text = new StringBuilder();
        int runStart = 0;
        while (runStart < order.Count)
        {
            float rotation = SpanRotation(spans, order[runStart]);
            int runEnd = runStart + 1;
            while (runEnd < order.Count && SameRotation(SpanRotation(spans, order[runEnd]), rotation)) runEnd++;

            if (runStart > 0 && text.Length > 0 && !EndsWithWhitespace(text)) text.Append(' ');
            AppendRun(text, spans, order, runStart, runEnd, rotation);
            runStart = runEnd;
        }
        return text.ToString();
    }

    /// <summary>
    /// Append one maximal same-rotation run. Zero rotation is the exact legacy path; any other
    /// rotation goes through line clustering, advance-axis sorting and gap-based space insertion.
    /// </summary>
    private static void AppendRun(
        StringBuilder text, IReadOnlyList<ReadingOrderSpan> spans,
        IReadOnlyList<int> order, int start, int end, float rotation)
    {
        if (IsUnrotated(rotation))
        {
            for (int k = start; k < end; k++)
            {
                int index = order[k];
                if (index >= 0 && index < spans.Count) text.Append(spans[index].Text);
            }
            return;
        }

        int lineStart = start;
        bool firstLine = true;
        while (lineStart < end)
        {
            int anchorIndex = order[lineStart];
            if (anchorIndex < 0 || anchorIndex >= spans.Count) { lineStart++; continue; }
            var anchor = spans[anchorIndex];
            float anchorCross = UprightReadingOrigin(anchor).Cross;

            int lineEnd = lineStart + 1;
            while (lineEnd < end)
            {
                int candidateIndex = order[lineEnd];
                if (candidateIndex < 0 || candidateIndex >= spans.Count) break;
                var candidate = spans[candidateIndex];
                float candidateCross = UprightReadingOrigin(candidate).Cross;
                float tolerance = RustMax(RustMax(anchor.Height, candidate.Height), F32Epsilon)
                                  * RotatedLineCrossToleranceRatio;
                if (MathF.Abs(candidateCross - anchorCross) > tolerance) break;
                lineEnd++;
            }

            if (!firstLine && text.Length > 0 && !EndsWithWhitespace(text)) text.Append(' ');
            firstLine = false;
            AppendRotatedLine(text, spans, order, lineStart, lineEnd);
            lineStart = lineEnd;
        }
    }

    /// <summary>
    /// Sort one rotated-frame line by advance-axis position and join it, inserting a space
    /// wherever the gap between consecutive spans looks like a word boundary rather than kerning.
    /// </summary>
    private static void AppendRotatedLine(
        StringBuilder text, IReadOnlyList<ReadingOrderSpan> spans,
        IReadOnlyList<int> order, int start, int end)
    {
        var ordered = new List<int>(end - start);
        for (int k = start; k < end; k++) ordered.Add(order[k]);
        ReadingOrder.StableSort(ordered, (a, b) =>
        {
            float advanceA = a >= 0 && a < spans.Count ? UprightReadingOrigin(spans[a]).Advance : 0.0f;
            float advanceB = b >= 0 && b < spans.Count ? UprightReadingOrigin(spans[b]).Advance : 0.0f;
            return ReadingOrder.TotalCmp(advanceA, advanceB);
        });

        float? previousAdvanceEnd = null;
        foreach (int index in ordered)
        {
            if (index < 0 || index >= spans.Count) continue;
            var span = spans[index];
            float advanceStart = UprightReadingOrigin(span).Advance;
            if (previousAdvanceEnd is { } previousEnd)
            {
                float gap = advanceStart - previousEnd;
                float kerningLimit = RustMax(span.Height, F32Epsilon) * AtomicFragmentGapRatio;
                if (gap > kerningLimit && text.Length > 0 && !EndsWithWhitespace(text)) text.Append(' ');
            }
            text.Append(span.Text);
            previousAdvanceEnd = advanceStart + span.Width;
        }
    }

    /// <summary>True when any span on the page carries a non-zero text-matrix rotation.</summary>
    /// <remarks>
    /// Rotation is read straight off the PDF text matrix, so this is answerable without layout
    /// detection or any hint at all. It is the cheap gate in front of
    /// <see cref="RepairRotatedPageText"/>.
    /// </remarks>
    public static bool PageHasRotatedSpans(IReadOnlyList<ReadingOrderSpan> spans)
    {
        foreach (var span in spans)
            if (!IsUnrotated(span.RotationDegrees)) return true;
        return false;
    }

    /// <summary>
    /// Minimum share of a page's span text, by character count, that must carry rotation before
    /// <see cref="RepairRotatedPageText"/> rewrites the page.
    /// </summary>
    /// <remarks>
    /// The repair replaces the <em>entire</em> page's text, and its unrotated path inserts no
    /// separators at all. A page where rotation is a tiny minority — one rotated caption or axis
    /// label on an otherwise upright page — would pay that cost across the whole page to fix a
    /// few words, and lose far more than it gains.
    /// </remarks>
    private const float MinRotatedTextShare = 0.2f;

    /// <summary>
    /// True when rotated spans make up at least <see cref="MinRotatedTextShare"/> of the page's
    /// span text, measured by character count.
    /// </summary>
    /// <remarks>
    /// Character count rather than span count is the right unit: a page can carry many short
    /// unrotated fragments beside one short rotated label, and span count alone would over-weight
    /// fragmentation on either side.
    /// </remarks>
    private static bool RotationIsDominant(IReadOnlyList<ReadingOrderSpan> spans)
    {
        long rotatedChars = 0, totalChars = 0;
        foreach (var span in spans)
        {
            long chars = CountRunes(span.Text);
            totalChars += chars;
            if (!IsUnrotated(span.RotationDegrees)) rotatedChars += chars;
        }
        return totalChars > 0 && (float)rotatedChars / totalChars >= MinRotatedTextShare;
    }

    /// <summary>
    /// Repair a page whose text contains rotated runs, without layout hints.
    /// </summary>
    /// <remarks>
    /// Ordering is deliberately left at identity — the span order the caller already has —
    /// because deciding a <em>better</em> cross-region order is the part that genuinely needs
    /// layout hints. Returns <c>null</c> when the page has no rotated spans, so the caller leaves
    /// that page's text completely untouched: an upright page is never rewritten and its output
    /// cannot drift.
    /// </remarks>
    public static string? RepairRotatedPageText(IReadOnlyList<ReadingOrderSpan> spans)
    {
        if (!PageHasRotatedSpans(spans) || !RotationIsDominant(spans)) return null;
        var identity = new List<int>(spans.Count);
        for (int k = 0; k < spans.Count; k++) identity.Add(k);
        return AssembleReadingOrderText(spans, identity);
    }

    /// <summary>Rust counts <c>char</c>s, which are scalar values rather than UTF-16 units.</summary>
    internal static int CountRunes(string text)
    {
        int count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>
    /// Rust's <c>f32::max</c>, which returns the other operand when one is NaN.
    /// </summary>
    /// <remarks><c>MathF.Max</c> propagates NaN instead, which would poison a tolerance.</remarks>
    internal static float RustMax(float a, float b)
    {
        if (float.IsNaN(a)) return b;
        if (float.IsNaN(b)) return a;
        return a > b ? a : b;
    }

    /// <summary>Rust's <c>f32::min</c>, which returns the other operand when one is NaN.</summary>
    internal static float RustMin(float a, float b)
    {
        if (float.IsNaN(a)) return b;
        if (float.IsNaN(b)) return a;
        return a < b ? a : b;
    }
}
