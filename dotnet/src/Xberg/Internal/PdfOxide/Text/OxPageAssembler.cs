// Ported from xberg `crates/xberg/src/pdf/oxide/text.rs`:
//   extract_page_text_column_aware (1173), assemble_page_text (596),
//   append_span_separator (542), order_spans_with_inline_fragments (501),
//   find_inline_fragment_anchor (462), is_short_inline_fragment (437),
//   has_rtl_or_bidi_content (450), spans_overlap_on_cross_axis (428),
//   is_fragmented_span_list (336), rebuild_text_from_fragmented_spans (374),
//   reorder_sparse_two_column_page (655) with its helpers (655..765), and
//   reorder_dense_two_column_page (1104) with its helpers (843..1070);
// plus `crates/xberg/src/pdf/oxide/span_geometry.rs` (the upright-frame
// predicates) and pdf_oxide `layout/region_classifier.rs :: classify_region`,
// which the dense band reorder gates on.
//
// This is the consumer half of the ported pipeline. Upstream it receives spans
// straight out of pdf_oxide — already column-aware ordered, deduplicated and
// merged — so it does no ordering of its own beyond the two guarded column
// repairs below. Feeding it anything else (raw interpreter spans, a second
// XY-cut on top of the first) re-does work the producer already did and moves
// text that was already right.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xberg.Internal.PdfOxide.Layout;

namespace Xberg.Internal.PdfOxide.Text;

internal static class OxPageAssembler
{
    /// <summary>One assembled visual line: the emitted text and the spans that produced it.</summary>
    public sealed class AssembledLine
    {
        public string Text = "";
        public List<OxTextSpan> Spans = new();
    }

    /// <summary>A page's assembled text plus its line decomposition.</summary>
    public sealed class PageAssembly
    {
        public string Text = "";
        public List<AssembledLine> Lines = new();
    }

    // ── Upright-frame geometry (span_geometry.rs) ───────────────────────────────
    //
    // A rotated run's bbox carries page-space x/y but width/height flattened onto
    // the run's own axis, so upright gap arithmetic is wrong for it. Rotating only
    // the origin back — the extents are already in that frame — makes every
    // upright heuristic below correct again. For unrotated spans (the vast
    // majority) each of these is the identity on the bbox.

    /// <summary>Rust <c>f32::EPSILON</c>: machine epsilon, not C#'s smallest denormal.</summary>
    private const float F32Epsilon = 1.1920929e-7f;

    private static bool IsUnrotated(OxTextSpan span) => MathF.Abs(span.RotationDegrees) <= F32Epsilon;

    /// <summary>Only same-rotation spans may be compared geometrically: their bboxes are
    /// flattened onto different axes otherwise.</summary>
    private static bool HasSameRotation(OxTextSpan first, OxTextSpan second) =>
        MathF.Abs(first.RotationDegrees - second.RotationDegrees) <= F32Epsilon;

    /// <summary>Horizontal left-to-right <em>writing mode</em>, saying nothing about rotation:
    /// a rotated table header is still horizontal LTR text painted along a rotated baseline.</summary>
    private static bool IsLtrWritingMode(OxTextSpan span) => span.Wmode == 0 && !span.RtlDrawLogical;

    /// <summary>Horizontal LTR <em>and</em> painted on the page axis — for arithmetic that is
    /// expressed in raw page coordinates and cannot be lifted into the span's own frame.</summary>
    private static bool IsHorizontalLtr(OxTextSpan span) => IsLtrWritingMode(span) && IsUnrotated(span);

    private static (float Advance, float Cross) UprightOrigin(OxTextSpan span)
    {
        if (IsUnrotated(span)) return (span.Bbox.X, span.Bbox.Y);
        float radians = -span.RotationDegrees * MathF.PI / 180.0f;
        float sin = MathF.Sin(radians), cos = MathF.Cos(radians);
        return (span.Bbox.X * cos - span.Bbox.Y * sin, span.Bbox.X * sin + span.Bbox.Y * cos);
    }

    private static (float Start, float End) UprightAdvanceExtent(OxTextSpan span)
    {
        float start = UprightOrigin(span).Advance;
        return (start, start + span.Bbox.Width);
    }

    private static (float Low, float High) UprightCrossExtent(OxTextSpan span)
    {
        float low = UprightOrigin(span).Cross;
        return (low, low + span.Bbox.Height);
    }

    // ── Page flow (extract_page_text_column_aware) ──────────────────────────────

    /// <summary>
    /// Assemble one page's text from spans the ported extractor already ordered.
    /// <paramref name="spans"/> is reordered in place by the two column repairs, exactly as
    /// upstream mutates <c>page_text_data.spans</c>.
    /// </summary>
    public static PageAssembly Assemble(List<OxTextSpan> spans, float pageWidth)
    {
        if (spans.Count == 0) return new PageAssembly();

        ReorderSparseTwoColumnPage(spans, pageWidth);
        ReorderDenseTwoColumnPage(spans, pageWidth);

        // A page whose text is drawn sideways is rewritten wholesale in its own reading
        // frame, ahead of the fragmentation check — an upright page returns null here.
        // The rotation scan comes first so the overwhelming majority of pages, which carry
        // no rotated span at all, never pay for the span conversion the repair needs.
        string? rotated = spans.Any(s => !IsUnrotated(s))
            ? Xberg.Internal.Pdf.PdfRotationRepair.RepairRotatedPageText(
                Xberg.Internal.Pdf.OxSpanBridge.ToPdfSpans(spans))
            : null;

        var assembly = rotated is null && IsFragmentedSpanList(spans)
            ? RebuildTextFromFragmentedSpans(spans)
            : AssemblePageText(spans);

        // The repaired text replaces the assembly's, but the line decomposition still
        // comes from the assembly: the structure pipeline needs per-line geometry, which
        // the verbatim rotated concatenation does not carry.
        if (rotated is not null) assembly.Text = rotated;
        return assembly;
    }

    // ── Assembly (assemble_page_text / append_span_separator) ───────────────────

    /// <summary>A span in emission order, with whether it rejoins the span before it.</summary>
    private readonly struct OrderedSpan
    {
        public readonly OxTextSpan Span;
        public readonly bool GlueToPrevious;
        public OrderedSpan(OxTextSpan span, bool glueToPrevious) { Span = span; GlueToPrevious = glueToPrevious; }
    }

    internal static PageAssembly AssemblePageText(List<OxTextSpan> spans)
    {
        var heights = new List<float>(spans.Count);
        foreach (var span in spans) heights.Add(span.Bbox.Height);
        OxSpanCompare.SortStable(heights, OxSpanCompare.SafeFloatCmp);
        float medianHeight = heights.Count == 0 ? 1.0f : heights[heights.Count / 2];
        float paragraphGapThreshold = medianHeight * 1.5f;

        var ordered = OrderSpansWithInlineFragments(spans);

        // Row resets are only meaningful for a purely left-to-right page: in RTL or mixed
        // text a span legitimately starts left of its predecessor.
        bool allowLtrRowResets = !spans.Any(s => s.RtlDrawLogical || HasRtlOrBidiContent(s.Text));

        var assembly = new PageAssembly();
        var text = new StringBuilder(spans.Count * 20);
        var line = new AssembledLine();
        int lineStart = 0;
        OxTextSpan? prevSpan = null;

        foreach (var current in ordered)
        {
            var span = current.Span;
            if (prevSpan is not null)
            {
                string separator = SpanSeparator(prevSpan, current, paragraphGapThreshold, allowLtrRowResets);
                text.Append(separator);
                if (separator.Contains('\n'))
                {
                    line.Text = text.ToString(lineStart, text.Length - separator.Length - lineStart);
                    assembly.Lines.Add(line);
                    line = new AssembledLine();
                    lineStart = text.Length;
                }
            }
            text.Append(span.Text);
            line.Spans.Add(span);
            prevSpan = span;
        }

        if (line.Spans.Count > 0)
        {
            line.Text = text.ToString(lineStart, text.Length - lineStart);
            assembly.Lines.Add(line);
        }
        assembly.Text = text.ToString();
        return assembly;
    }

    /// <summary>
    /// The separator emitted between two adjacent spans: "" (glued), " ", "\n" or "\n\n".
    /// </summary>
    private static string SpanSeparator(
        OxTextSpan previous, OrderedSpan current, float paragraphGapThreshold, bool allowLtrRowResets)
    {
        if (current.GlueToPrevious) return "";

        var span = current.Span;

        // A change of text-matrix rotation is a hard block boundary. pdf_oxide lifts rotated
        // runs out of the horizontal flow and appends them as their own blocks, and the two
        // bboxes are flattened onto different axes, so no gap arithmetic across the boundary
        // is meaningful. This is also what keeps an upright running footer readable on a page
        // whose body is rotated.
        if (!HasSameRotation(previous, span)) return "\n\n";

        // Everything below runs in the pair's shared upright frame: identical to the raw page
        // axes when the pair is unrotated, axis-swapped when it is not.
        var (previousStart, previousEnd) = UprightAdvanceExtent(previous);
        float spanStart = UprightAdvanceExtent(span).Start;
        float previousBaseline = UprightCrossExtent(previous).Low;
        float spanBaseline = UprightCrossExtent(span).Low;
        float baselineGap = MathF.Abs(previousBaseline - spanBaseline);

        float resetThreshold = MathF.Max(previous.FontSize, span.FontSize) * RowResetMinBacktrackEms;
        bool isLtrPair = IsLtrWritingMode(previous)
            && IsLtrWritingMode(span)
            && !HasRtlOrBidiContent(previous.Text)
            && !HasRtlOrBidiContent(span.Text);
        if (allowLtrRowResets && isLtrPair && spanStart < previousStart - resetThreshold)
            return baselineGap > paragraphGapThreshold ? "\n\n" : "\n";

        if (span.SplitBoundaryBefore)
        {
            return !EndsWithWhitespace(previous.Text) && !StartsWithWhitespace(span.Text) ? " " : "";
        }

        float effectiveHeight = MathF.Max(MathF.Max(span.Bbox.Height, previous.Bbox.Height), span.FontSize * 0.5f);
        if (baselineGap < effectiveHeight * 0.5f)
            return spanStart - previousEnd > span.FontSize * 0.15f ? " " : "";
        return baselineGap > paragraphGapThreshold ? "\n\n" : "\n";
    }

    /// <summary>How far left of the previous span's start a span must begin before it counts
    /// as a new row rather than a continuation.</summary>
    private const float RowResetMinBacktrackEms = 4.0f;

    private static bool EndsWithWhitespace(string text) =>
        text.Length > 0 && char.IsWhiteSpace(text[^1]);

    private static bool StartsWithWhitespace(string text) =>
        text.Length > 0 && char.IsWhiteSpace(text[0]);

    // ── Inline fragment reattachment (order_spans_with_inline_fragments) ────────

    private const float InlineFragmentGapRatio = 0.1f;
    /// <summary>Detached glyphs are stream-local; bounding the lookup avoids quadratic work
    /// on dense pages.</summary>
    private const int MaxInlineFragmentAnchorLookback = 256;

    /// <summary>
    /// Do the two spans share a line? Measured on each span's own cross axis so that a
    /// 90-degree rotated pair, whose shared baseline is a page-x column rather than a page-y
    /// row, is still recognised as one line. Only meaningful for spans of equal rotation;
    /// callers check that.
    /// </summary>
    /// <remarks>
    /// Upstream asks only for a positive box overlap, and that also holds between two
    /// <em>consecutive</em> rows of display type: a 20.5pt glyph box on a 19.8pt line pitch
    /// overhangs the row below by 0.7pt. On a title page the producer kerns per glyph, so every
    /// span is a short fragment, and that hairline lets a glyph opening one row anchor to a
    /// glyph closing the row above whenever their advance extents happen to abut — which is how
    /// "Part 10: Transport layer and network" comes out as "Pr / art 10p / : / T ans
    /// ortlayerandnetwo". So the boxes must overlap <em>and</em> the pair must pass the same
    /// baseline test <see cref="SpanSeparator"/> uses to tell a continuation from a new row,
    /// which separates the two cases with room to spare: the stacked display rows sit 19.8pt
    /// apart against a 10.25pt bound, while a subscript that genuinely belongs to the word
    /// before it — the "3" of "HCO3" — sits 3.3pt below a 5.0pt bound.
    /// </remarks>
    private static bool SpansOverlapOnCrossAxis(OxTextSpan first, OxTextSpan second)
    {
        var (firstLow, firstHigh) = UprightCrossExtent(first);
        var (secondLow, secondHigh) = UprightCrossExtent(second);
        if (MathF.Min(firstHigh, secondHigh) <= MathF.Max(firstLow, secondLow)) return false;

        float effectiveHeight = MathF.Max(
            MathF.Max(second.Bbox.Height, first.Bbox.Height), second.FontSize * 0.5f);
        return MathF.Abs(firstLow - secondLow) < effectiveHeight * 0.5f;
    }

    private static bool IsShortInlineFragment(OxTextSpan span)
    {
        if (span.Text.Length == 0) return false;
        int charCount = 0;
        bool allWhitespace = true;
        System.Text.Rune first = default;
        foreach (var rune in span.Text.EnumerateRunes())
        {
            if (charCount == 0) first = rune;
            charCount++;
            if (!System.Text.Rune.IsWhiteSpace(rune)) allWhitespace = false;
        }
        if (charCount > 3 || allWhitespace) return false;
        // A lone "a"/"A"/"I" is a word in its own right, not a glyph that fell off one.
        return !(charCount == 1 && (first.Value == 'a' || first.Value == 'A' || first.Value == 'I'));
    }

    internal static bool HasRtlOrBidiContent(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (ScriptSignals.IsRtlText(rune.Value)) return true;
        return false;
    }

    /// <summary>
    /// Find the parent word a short detached fragment should rejoin.
    /// <para>
    /// Gated on the writing mode only. Rotation is deliberately <em>not</em> a reason to
    /// refuse the join: a rotated table header is horizontal LTR text painted along a rotated
    /// baseline, and refusing to anchor its fragments is what leaves rotated tables glued and
    /// word-reversed. The candidate must still carry the same rotation as the fragment, and
    /// all gap arithmetic runs in that rotation's upright frame.
    /// </para>
    /// </summary>
    private static int? FindInlineFragmentAnchor(int index, List<OxTextSpan> spans, int?[] anchors)
    {
        var span = spans[index];
        if (span.SplitBoundaryBefore
            || !IsShortInlineFragment(span)
            || !IsLtrWritingMode(span)
            || HasRtlOrBidiContent(span.Text))
            return null;

        float spanStart = UprightAdvanceExtent(span).Start;
        int searchStart = Math.Max(0, index - MaxInlineFragmentAnchorLookback);
        int? best = null;
        float bestGap = 0.0f;
        for (int candidateIndex = searchStart; candidateIndex < index; candidateIndex++)
        {
            if (anchors[candidateIndex] is not null) continue;
            var candidate = spans[candidateIndex];
            if (!IsLtrWritingMode(candidate)
                || HasRtlOrBidiContent(candidate.Text)
                || !HasSameRotation(candidate, span)
                || !SpansOverlapOnCrossAxis(candidate, span))
                continue;
            float candidateEnd = UprightAdvanceExtent(candidate).End;
            float gap = spanStart - candidateEnd;
            float tolerance = MathF.Max(candidate.FontSize, span.FontSize) * InlineFragmentGapRatio;
            if (gap < -tolerance || gap > tolerance) continue;
            float distance = MathF.Abs(gap);
            // `min_by` keeps the FIRST of equal minima, so only a strictly smaller gap wins.
            if (best is null || distance < bestGap) { best = candidateIndex; bestGap = distance; }
        }
        return best;
    }

    private static List<OrderedSpan> OrderSpansWithInlineFragments(List<OxTextSpan> spans)
    {
        var anchors = new int?[spans.Count];
        for (int index = 0; index < spans.Count; index++)
            anchors[index] = FindInlineFragmentAnchor(index, spans, anchors);

        var children = new List<int>?[spans.Count];
        for (int index = 0; index < spans.Count; index++)
        {
            if (anchors[index] is not { } anchor) continue;
            (children[anchor] ??= new List<int>()).Add(index);
        }
        foreach (var attached in children)
        {
            if (attached is null) continue;
            // Along each fragment's own advance axis, so rotated fragments are re-inserted in
            // reading order rather than page-x order.
            OxSpanCompare.SortStable(attached, (first, second) => OxSpanCompare.SafeFloatCmp(
                UprightAdvanceExtent(spans[first]).Start, UprightAdvanceExtent(spans[second]).Start));
        }

        var ordered = new List<OrderedSpan>(spans.Count);
        for (int index = 0; index < spans.Count; index++)
        {
            if (anchors[index] is not null) continue;
            ordered.Add(new OrderedSpan(spans[index], glueToPrevious: false));
            if (children[index] is { } attached)
                foreach (int child in attached) ordered.Add(new OrderedSpan(spans[child], glueToPrevious: true));
        }
        return ordered;
    }

    // ── Glyph fragmentation (is_fragmented_span_list / rebuild_...) ─────────────
    //
    // See `crates/xberg/src/pdf/structure/constants.rs` for the thresholds.
    //
    // pdf_oxide's ColumnAware reading order groups all spans at one y-level before moving to
    // the next. For Word-exported PDFs where each glyph has its own BT…ET block with a
    // sinusoidal y-jitter, this produces groups ordered by y-level rather than by reading
    // order: "et" (y=703) appears before "H" (y=700) even though "H" comes first visually.

    private const float MaxGlyphJitterPt = 5.0f;
    private const int MinDisorderCount = 3;
    private const float CoalesceThreshold = 5.0f;

    private static bool IsFragmentedSpanList(List<OxTextSpan> spans)
    {
        int disorderCount = 0;
        for (int i = 0; i + 1 < spans.Count; i++)
        {
            var prev = spans[i];
            var cur = spans[i + 1];

            // Per-glyph BT/ET always produces single-character spans; multi-character spans
            // are word-level and cannot be glyph artifacts.
            if (RuneCount(prev.Text) > 3 || RuneCount(cur.Text) > 3) continue;

            float yGap = MathF.Abs(prev.Bbox.Y - cur.Bbox.Y);
            float effHeight = MathF.Max(prev.Bbox.Height, cur.Bbox.Height);
            bool sameLine = effHeight > 0.0f ? yGap < effHeight * 0.5f : yGap <= MaxGlyphJitterPt;

            if (sameLine && cur.Bbox.X < prev.Bbox.X - prev.FontSize)
            {
                disorderCount++;
                if (disorderCount >= MinDisorderCount) return true;
            }
        }
        return false;
    }

    private static PageAssembly RebuildTextFromFragmentedSpans(List<OxTextSpan> spans)
    {
        var assembly = new PageAssembly();
        if (spans.Count == 0) return assembly;

        var sorted = new List<OxTextSpan>(spans);
        OxSpanCompare.SortStable(sorted, (a, b) => OxSpanCompare.SafeFloatCmp(b.Bbox.Y, a.Bbox.Y));

        // Chained y-proximity: a span within COALESCE_THRESHOLD of the PREVIOUS span (not of
        // the group's anchor) continues the same visual line.
        var groups = new List<List<OxTextSpan>>();
        foreach (var span in sorted)
        {
            bool belongs = groups.Count > 0 &&
                MathF.Abs(span.Bbox.Y - groups[^1][^1].Bbox.Y) <= CoalesceThreshold;
            if (belongs) groups[^1].Add(span);
            else groups.Add(new List<OxTextSpan> { span });
        }

        var text = new StringBuilder();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            OxSpanCompare.SortStable(group, (a, b) => OxSpanCompare.SafeFloatCmp(a.Bbox.X, b.Bbox.X));
            if (gi > 0) text.Append('\n');
            int lineStart = text.Length;

            float fontSize = 0.0f;
            foreach (var span in group) fontSize = MathF.Max(fontSize, span.FontSize);
            float spaceThreshold = fontSize * 0.5f;
            float prevEndX = float.NegativeInfinity;
            foreach (var span in group)
            {
                if (!float.IsInfinity(prevEndX) && span.Bbox.X - prevEndX > spaceThreshold) text.Append(' ');
                text.Append(span.Text);
                prevEndX = span.Bbox.X + span.Bbox.Width;
            }
            assembly.Lines.Add(new AssembledLine
            {
                Text = text.ToString(lineStart, text.Length - lineStart),
                Spans = group,
            });
        }
        assembly.Text = text.ToString();
        return assembly;
    }

    // ── Sparse two-column repair (reorder_sparse_two_column_page) ───────────────
    //
    // pdf_oxide's XY-Cut does not split regions with fewer than five spans. These guards
    // cover the four-span sentence case without reclassifying sparse tables or forms as
    // prose columns.

    private const float MinSparseColumnGutterFraction = 0.05f;
    private const float MinSparseColumnGutterPts = 15.0f;
    private const float MinSparseColumnContentWidthPts = 144.0f;
    private const int MinSparseColumnWords = 2;
    private const int MinSparseColumnWordsPerSide = 6;
    private const int MinSparseColumnAlphaChars = 8;
    private const float MinSparseColumnAlphaRatio = 0.55f;
    private const float MinSparseColumnVerticalOverlap = 0.5f;
    private const int XyCutMinSpansForSplit = 5;

    private static bool IsSparseColumnProse(OxTextSpan span)
    {
        int alphaChars = 0, nonWhitespaceChars = 0;
        foreach (var rune in span.Text.EnumerateRunes())
        {
            if (System.Text.Rune.IsLetter(rune)) alphaChars++;
            if (!System.Text.Rune.IsWhiteSpace(rune)) nonWhitespaceChars++;
        }
        int wordCount = WordCount(span.Text);
        bool geometryIsValid = float.IsFinite(span.Bbox.X)
            && float.IsFinite(span.Bbox.Y)
            && float.IsFinite(span.Bbox.Width)
            && float.IsFinite(span.Bbox.Height)
            && span.Bbox.Width > 0.0f;

        return geometryIsValid
            && !span.IsMonospace
            && IsHorizontalLtr(span)
            && !HasRtlOrBidiContent(span.Text)
            && !span.Text.Contains(':')
            && wordCount >= MinSparseColumnWords
            && alphaChars >= MinSparseColumnAlphaChars
            && (float)alphaChars / Math.Max(1, nonWhitespaceChars) >= MinSparseColumnAlphaRatio;
    }

    private static bool SparseColumnsOverlap(List<OxTextSpan> left, List<OxTextSpan> right)
    {
        static (float Low, float High) Extent(List<OxTextSpan> side)
        {
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            foreach (var span in side) { low = MathF.Min(low, span.Bbox.Y); high = MathF.Max(high, span.Bbox.Y); }
            return (low, high);
        }
        var (leftLow, leftHigh) = Extent(left);
        var (rightLow, rightHigh) = Extent(right);
        float overlap = MathF.Max(MathF.Min(leftHigh, rightHigh) - MathF.Max(leftLow, rightLow), 0.0f);
        float shorterExtent = MathF.Min(leftHigh - leftLow, rightHigh - rightLow);

        return shorterExtent > 0.0f && overlap / shorterExtent >= MinSparseColumnVerticalOverlap;
    }

    private static bool SparseColumnsContinueOneSentence(List<OxTextSpan> left, List<OxTextSpan> right)
    {
        var leftByY = new List<OxTextSpan>(left);
        var rightByY = new List<OxTextSpan>(right);
        OxSpanCompare.SortStable(leftByY, (a, b) => OxSpanCompare.SafeFloatCmp(b.Bbox.Y, a.Bbox.Y));
        OxSpanCompare.SortStable(rightByY, (a, b) => OxSpanCompare.SafeFloatCmp(b.Bbox.Y, a.Bbox.Y));

        static System.Text.Rune? FirstLetter(OxTextSpan span)
        {
            foreach (var rune in span.Text.EnumerateRunes())
                if (System.Text.Rune.IsLetter(rune)) return rune;
            return null;
        }
        static bool StartsLowercase(OxTextSpan span) =>
            FirstLetter(span) is { } c && System.Text.Rune.IsLower(c);
        static bool StartsUppercase(OxTextSpan span) =>
            FirstLetter(span) is { } c && System.Text.Rune.IsUpper(c);
        static bool HasTerminal(OxTextSpan span)
        {
            string trimmed = span.Text.TrimEnd();
            return trimmed.Length > 0 && (trimmed[^1] == '.' || trimmed[^1] == '!' || trimmed[^1] == '?');
        }

        var continuations = new[] { leftByY[1], rightByY[0], rightByY[1] };
        int terminalCount = leftByY.Count(HasTerminal) + rightByY.Count(HasTerminal);

        return StartsUppercase(leftByY[0])
            && continuations.All(StartsLowercase)
            && terminalCount == 1
            && HasTerminal(rightByY[1]);
    }

    private static bool IsSparseColumnSplit(List<OxTextSpan> spans, float splitX, float minGutter)
    {
        var left = spans.Where(s => s.Bbox.X < splitX).ToList();
        var right = spans.Where(s => s.Bbox.X >= splitX).ToList();
        if (left.Count != 2 || right.Count != 2) return false;
        if (left.Sum(s => WordCount(s.Text)) < MinSparseColumnWordsPerSide
            || right.Sum(s => WordCount(s.Text)) < MinSparseColumnWordsPerSide)
            return false;
        float leftRight = float.NegativeInfinity;
        foreach (var span in left) leftRight = MathF.Max(leftRight, span.Bbox.X + span.Bbox.Width);

        return splitX - leftRight >= minGutter
            && SparseColumnsOverlap(left, right)
            && SparseColumnsContinueOneSentence(left, right);
    }

    private static float? SparseColumnSplit(List<OxTextSpan> spans, float pageWidth)
    {
        bool hasSparseProseShape = spans.Count == XyCutMinSpansForSplit - 1 && spans.All(IsSparseColumnProse);
        float contentLeft = float.PositiveInfinity, contentRight = float.NegativeInfinity;
        foreach (var span in spans)
        {
            contentLeft = MathF.Min(contentLeft, span.Bbox.X);
            contentRight = MathF.Max(contentRight, span.Bbox.X + span.Bbox.Width);
        }
        if (!hasSparseProseShape || contentRight - contentLeft < MinSparseColumnContentWidthPts) return null;

        float minGutter = MathF.Max(pageWidth * MinSparseColumnGutterFraction, MinSparseColumnGutterPts);
        var starts = spans.Select(s => s.Bbox.X).ToList();
        OxSpanCompare.SortStable(starts, OxSpanCompare.SafeFloatCmp);
        for (int i = starts.Count - 1; i > 0; i--)
            if (MathF.Abs(starts[i] - starts[i - 1]) <= F32Epsilon) starts.RemoveAt(i);

        foreach (float splitX in starts)
            if (IsSparseColumnSplit(spans, splitX, minGutter)) return splitX;
        return null;
    }

    /// <summary>
    /// Reorder the guarded four-span, two-column sentence shape. Returns true only when the
    /// sparse prose classifier matched and reordered the spans.
    /// </summary>
    internal static bool ReorderSparseTwoColumnPage(List<OxTextSpan> spans, float pageWidth)
    {
        if (SparseColumnSplit(spans, pageWidth) is not { } splitX) return false;
        OxSpanCompare.SortStable(spans, (left, right) =>
        {
            int leftColumn = left.Bbox.X >= splitX ? 1 : 0;
            int rightColumn = right.Bbox.X >= splitX ? 1 : 0;
            int cmp = leftColumn.CompareTo(rightColumn);
            if (cmp != 0) return cmp;
            cmp = OxSpanCompare.SafeFloatCmp(right.Bbox.Y, left.Bbox.Y);
            return cmp != 0 ? cmp : OxSpanCompare.SafeFloatCmp(left.Bbox.X, right.Bbox.X);
        });
        return true;
    }

    // ── Dense two-column repair (reorder_dense_two_column_page) ─────────────────
    //
    // A dense two-column body (a full page of prose, not the guarded four-span sentence
    // above) is never split by pdf_oxide's own ColumnAware XY-Cut on some documents, so the
    // span-level assembler falls through to full-page-width Y order — welding left- and
    // right-column lines at the same height into one element, mid-sentence. No downstream
    // pass can repair that: the interleaving is baked into the element text by then.

    private const float MinDenseColumnContentWidthPts = 200.0f;
    // 2%, not 3%. On A4 (595pt) with columns at x=37.6 and x=306.6, symmetric margins put
    // the left column's right edge at 288.4, so the real gutter is ~18.2pt — against a 3%
    // threshold of 17.85pt that is a 0.35pt margin. 2% gives 11.9pt, still far above the
    // intra-line word spacing (~3-5pt at a 10pt font) this must not mistake for a gutter.
    private const float MinDenseColumnGutterFraction = 0.02f;
    private const float MinDenseColumnGutterPts = 10.0f;
    private const int MinDenseColumnSpansPerSide = 6;
    // A full-width furniture span (running header/footer, full-width title) spans nearly the
    // whole printable width regardless of the columns beneath it, whereas a genuine column is
    // bounded by the page margins AND the gutter and can never reach much past ~45% of the
    // page width. 0.55 sits well clear of both.
    private const float FullWidthFurnitureFraction = 0.55f;
    // Two spans on the same visual line never differ in y by more than sub-point float noise;
    // two distinct lines are always at least a line-height apart.
    private const float LineYTolerancePts = 0.5f;
    // A single line with a coincidentally wide internal gap (heavy justification, a dotted
    // table-of-contents leader) must not be read as a real gutter on a single-column page.
    private const int MinDenseColumnSplitLines = MinDenseColumnSpansPerSide;

    /// <summary>Sort every span index top-to-bottom, then left-to-right — the single global
    /// sort the rest of the dense repair is built on.</summary>
    private static List<int> SpansSortedTopToBottom(List<OxTextSpan> spans)
    {
        var order = Enumerable.Range(0, spans.Count).ToList();
        OxSpanCompare.SortStable(order, (a, b) =>
        {
            int cmp = OxSpanCompare.SafeFloatCmp(spans[b].Bbox.Y, spans[a].Bbox.Y);
            return cmp != 0 ? cmp : OxSpanCompare.SafeFloatCmp(spans[a].Bbox.X, spans[b].Bbox.X);
        });
        return order;
    }

    /// <summary>
    /// Bucket a top-to-bottom-sorted order into visual lines. A line is anchored on its
    /// topmost span, so gradual y-drift across many spans can never chain unrelated lines
    /// together; each line is then re-sorted left-to-right, which the per-line gutter sweep
    /// requires.
    /// </summary>
    private static List<List<int>> GroupIntoLines(List<OxTextSpan> spans, List<int> order)
    {
        var lines = new List<List<int>>();
        float anchorY = float.NaN;
        foreach (int index in order)
        {
            float y = spans[index].Bbox.Y;
            if (lines.Count == 0 || MathF.Abs(anchorY - y) > LineYTolerancePts)
            {
                anchorY = y;
                lines.Add(new List<int>());
            }
            lines[^1].Add(index);
        }
        foreach (var line in lines)
            OxSpanCompare.SortStable(line, (a, b) => OxSpanCompare.SafeFloatCmp(spans[a].Bbox.X, spans[b].Bbox.X));
        return lines;
    }

    /// <summary>
    /// Widest gap at least <paramref name="minGutter"/> wide between consecutive left-to-right
    /// sorted edges, or null. Tracking the running rightmost edge (rather than the previous
    /// span's right edge) means a span nested inside an earlier one can never be mistaken for
    /// the start of a gap.
    /// </summary>
    private static float? WidestGapMidpoint(IEnumerable<(float Left, float Right)> edges, float minGutter)
    {
        using var it = edges.GetEnumerator();
        if (!it.MoveNext()) return null;
        float runningRight = it.Current.Right;
        float bestGap = 0.0f;
        float? bestSplit = null;
        while (it.MoveNext())
        {
            var (left, right) = it.Current;
            float gap = left - runningRight;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestSplit = (runningRight + left) / 2.0f;
            }
            runningRight = MathF.Max(runningRight, right);
        }
        return bestGap < minGutter ? null : bestSplit;
    }

    private static bool LineHasWidthFurniture(List<OxTextSpan> spans, List<int> line, float furnitureWidth) =>
        line.Any(index => spans[index].Bbox.Width >= furnitureWidth);

    /// <summary>
    /// Establish the page's gutter x-position from independent per-line evidence. Because the
    /// check is per line, a furniture line elsewhere on the page — even one narrower than
    /// <see cref="FullWidthFurnitureFraction"/> that crosses the gutter without an internal
    /// gap of its own — can never corrupt another line's evidence.
    /// </summary>
    private static float? DetectSplitX(List<OxTextSpan> spans, List<List<int>> lines, float pageWidth)
    {
        float minGutter = MathF.Max(pageWidth * MinDenseColumnGutterFraction, MinDenseColumnGutterPts);
        float furnitureWidth = pageWidth * FullWidthFurnitureFraction;

        var midpoints = new List<float>();
        foreach (var line in lines)
        {
            if (LineHasWidthFurniture(spans, line, furnitureWidth)) continue;
            var edges = line.Select(index => (spans[index].Bbox.Left, spans[index].Bbox.Right));
            if (WidestGapMidpoint(edges, minGutter) is { } midpoint) midpoints.Add(midpoint);
        }
        if (midpoints.Count < MinDenseColumnSplitLines) return null;

        OxSpanCompare.SortStable(midpoints, OxSpanCompare.SafeFloatCmp);
        int mid = midpoints.Count / 2;
        return midpoints.Count % 2 == 0 ? (midpoints[mid - 1] + midpoints[mid]) / 2.0f : midpoints[mid];
    }

    /// <summary>A page region between two consecutive boundary (furniture) lines, in document
    /// order. A boundary band is a single line emitted where it already sits.</summary>
    private readonly struct Band
    {
        public readonly List<int> Indices;
        public readonly bool IsBoundary;
        public Band(List<int> indices, bool isBoundary) { Indices = indices; IsBoundary = isBoundary; }
    }

    /// <summary>
    /// True if the line is furniture separating two bands rather than column content:
    /// full-width, or straddling the page's gutter. The straddle test is what per-line
    /// segmentation adds — it catches furniture narrower than the width threshold that a
    /// whole-page projection could not tell apart from real column content.
    /// </summary>
    private static bool LineIsBoundary(List<OxTextSpan> spans, List<int> line, float furnitureWidth, float splitX) =>
        line.Any(index =>
        {
            var bbox = spans[index].Bbox;
            return bbox.Width >= furnitureWidth || (bbox.Left < splitX && bbox.Right > splitX);
        });

    private static List<Band> BuildBands(
        List<OxTextSpan> spans, List<List<int>> lines, float furnitureWidth, float splitX)
    {
        var bands = new List<Band>();
        var current = new List<int>();
        foreach (var line in lines)
        {
            if (!LineIsBoundary(spans, line, furnitureWidth, splitX))
            {
                current.AddRange(line);
                continue;
            }
            if (current.Count > 0) { bands.Add(new Band(current, isBoundary: false)); current = new List<int>(); }
            bands.Add(new Band(new List<int>(line), isBoundary: true));
        }
        if (current.Count > 0) bands.Add(new Band(current, isBoundary: false));
        return bands;
    }

    /// <summary>
    /// Try to reorder one content band column-major. A band with too few spans on either side,
    /// or that fails the prose/reference classification, stays in its existing order — a table
    /// or form band is not corrupted by a prose band elsewhere on the same page.
    /// </summary>
    private static List<int>? ReorderBandColumns(List<OxTextSpan> spans, List<int> band, float splitX)
    {
        var left = new List<int>();
        var right = new List<int>();
        foreach (int index in band)
        {
            if (spans[index].Bbox.X < splitX) left.Add(index);
            else right.Add(index);
        }
        if (left.Count < MinDenseColumnSpansPerSide || right.Count < MinDenseColumnSpansPerSide) return null;
        if (!RegionClassifier.IsReorderableColumn(RegionClassifier.Classify(spans, left))
            || !RegionClassifier.IsReorderableColumn(RegionClassifier.Classify(spans, right)))
            return null;
        left.AddRange(right);
        return left;
    }

    /// <summary>
    /// Concatenate bands into the final emission order. Each boundary line is emitted between
    /// the band above it and the band below it, in true document order. Returns null if not a
    /// single band qualified, so the caller can leave the spans completely untouched rather
    /// than apply a no-op permutation.
    /// </summary>
    private static List<int>? EmitBandOrder(List<OxTextSpan> spans, List<Band> bands, float splitX)
    {
        bool anyReordered = false;
        var order = new List<int>();
        foreach (var band in bands)
        {
            if (band.IsBoundary) { order.AddRange(band.Indices); continue; }
            var reordered = ReorderBandColumns(spans, band.Indices, splitX);
            if (reordered is not null) { anyReordered = true; order.AddRange(reordered); }
            else order.AddRange(band.Indices);
        }
        return anyReordered ? order : null;
    }

    private static void ApplySpanOrder(List<OxTextSpan> spans, List<int> order)
    {
        var taken = new List<OxTextSpan>(spans);
        for (int i = 0; i < spans.Count; i++) spans[i] = taken[order[i]];
    }

    /// <summary>
    /// Reorder a dense two-column page that pdf_oxide's own ColumnAware reading order fails to
    /// split. The page is first segmented into horizontal bands at gutter-crossing lines, then
    /// column detection runs independently per band, so furniture strictly between the columns
    /// lands at its true interleaved position rather than a global placeholder.
    /// </summary>
    /// <remarks>
    /// Known limitations, inherited from upstream: the gutter x-position is detected once for
    /// the whole page and reused for every band; many small bands can starve individual bands
    /// of the spans-per-side the reorder gate needs; and columns whose lines are not
    /// row-aligned at all starve the per-line gutter evidence.
    /// </remarks>
    internal static bool ReorderDenseTwoColumnPage(List<OxTextSpan> spans, float pageWidth)
    {
        float contentLeft = float.PositiveInfinity, contentRight = float.NegativeInfinity;
        foreach (var span in spans)
        {
            contentLeft = MathF.Min(contentLeft, span.Bbox.X);
            contentRight = MathF.Max(contentRight, span.Bbox.X + span.Bbox.Width);
        }
        if (spans.Count < 2 || contentRight - contentLeft < MinDenseColumnContentWidthPts) return false;

        var order = SpansSortedTopToBottom(spans);
        var lines = GroupIntoLines(spans, order);
        if (DetectSplitX(spans, lines, pageWidth) is not { } splitX) return false;

        float furnitureWidth = pageWidth * FullWidthFurnitureFraction;
        var bands = BuildBands(spans, lines, furnitureWidth, splitX);
        if (EmitBandOrder(spans, bands, splitX) is not { } finalOrder) return false;

        ApplySpanOrder(spans, finalOrder);
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Rust <c>str::chars().count()</c>: Unicode scalars, not UTF-16 units.</summary>
    private static int RuneCount(string text)
    {
        int count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>Rust <c>str::split_whitespace().count()</c>.</summary>
    private static int WordCount(string text)
    {
        int count = 0;
        bool inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsWhiteSpace(rune)) inWord = false;
            else if (!inWord) { inWord = true; count++; }
        }
        return count;
    }

    // ── Region classification (pdf_oxide layout/region_classifier.rs) ───────────
    //
    // The gates that decide "reorder this band column-major" cannot tell a two-column prose
    // or reference body from a table or form using thresholds alone: loosen a gate to admit
    // ragged reference columns and it swallows tables. This classifies the shape positively
    // instead, and returns Mixed (= caller keeps its prior behaviour) on any ambiguity.
    internal static class RegionClassifier
    {
        public enum RegionClass { Prose, Reference, Table, Form, Mixed }

        /// <summary>The classes the column-reorder gates accept.</summary>
        public static bool IsReorderableColumn(RegionClass c) =>
            c == RegionClass.Prose || c == RegionClass.Reference;

        private sealed class LineStat
        {
            public float Top, Left, Right;
            public int NonWsChars;
            public string LeadText = "";
            public List<(float Left, float Right)> Spans = new();
        }

        public static RegionClass Classify(List<OxTextSpan> spans, List<int> indices)
        {
            if (indices.Count < 6) return RegionClass.Mixed;

            float xMin = float.MaxValue, xMax = float.MinValue;
            foreach (int i in indices)
            {
                xMin = MathF.Min(xMin, spans[i].Bbox.Left);
                xMax = MathF.Max(xMax, spans[i].Bbox.Right);
            }
            float regionWidth = xMax - xMin;
            if (regionWidth <= 10.0f) return RegionClass.Mixed;

            float medHeight = MathF.Max(MedianHeight(spans, indices), 1.0f);
            var lines = ClusterLines(spans, indices, medHeight);
            int lineCount = lines.Count;
            // Headings, captions, single paragraphs — leave to default behaviour.
            if (lineCount < 6) return RegionClass.Mixed;

            int totalChars = 0, wideLines = 0, numberedLines = 0, formLines = 0;
            var leftEdges = new List<float>(lineCount);
            foreach (var l in lines)
            {
                totalChars += l.NonWsChars;
                float extent = MathF.Max(l.Right - l.Left, 0.0f);
                if (extent >= regionWidth * 0.6f) wideLines++;
                if (StartsNumberedEntry(l.LeadText)) numberedLines++;
                if (LineHasLabelValueGap(l, regionWidth)) formLines++;
                leftEdges.Add(l.Left);
            }
            float meanChars = (float)totalChars / lineCount;
            bool mostlyWide = wideLines * 2 > lineCount;
            float numberedFrac = (float)numberedLines / lineCount;
            float formFrac = (float)formLines / lineCount;

            // TABLE: short content per line. A prose or reference column always carries
            // substantial text per line, so it never falls this low.
            if (meanChars < 10.0f) return RegionClass.Table;

            // FORM: label … value rows. Distinguishes a tax form — whose label text is long
            // enough to otherwise read as prose — from a real prose body.
            if (formFrac >= 0.4f) return RegionClass.Form;

            // REFERENCE: numbered entries or a hanging-indent two-level left edge, with enough
            // text per line to exclude table cells.
            if (meanChars > 12.0f && (numberedFrac >= 0.3f || HasHangingIndent(leftEdges, medHeight)))
                return RegionClass.Reference;

            if (meanChars > 20.0f && mostlyWide) return RegionClass.Prose;

            return RegionClass.Mixed;
        }

        private static float MedianHeight(List<OxTextSpan> spans, List<int> indices)
        {
            var hs = new List<float>(indices.Count);
            foreach (int i in indices)
            {
                float h = MathF.Abs(spans[i].Bbox.Height);
                if (h > 0.0f) hs.Add(h);
            }
            if (hs.Count == 0) return 1.0f;
            OxSpanCompare.SortStable(hs, OxSpanCompare.SafeFloatCmp);
            return hs[hs.Count / 2];
        }

        private static List<LineStat> ClusterLines(List<OxTextSpan> spans, List<int> indices, float medHeight)
        {
            var order = new List<int>(indices);
            OxSpanCompare.SortStable(order, (a, b) =>
            {
                int cmp = OxSpanCompare.SafeFloatCmp(spans[a].Bbox.Top, spans[b].Bbox.Top);
                return cmp != 0 ? cmp : OxSpanCompare.SafeFloatCmp(spans[a].Bbox.Left, spans[b].Bbox.Left);
            });

            float tol = medHeight * 0.6f;
            var lines = new List<LineStat>();
            foreach (int i in order)
            {
                var s = spans[i];
                int nonWs = 0;
                foreach (var rune in s.Text.EnumerateRunes())
                    if (!System.Text.Rune.IsWhiteSpace(rune)) nonWs++;
                var last = lines.Count > 0 ? lines[^1] : null;
                if (last is not null && MathF.Abs(s.Bbox.Top - last.Top) <= tol)
                {
                    last.Left = MathF.Min(last.Left, s.Bbox.Left);
                    last.Right = MathF.Max(last.Right, s.Bbox.Right);
                    last.NonWsChars += nonWs;
                    // A new leftmost span on this line owns its lead text.
                    if (s.Bbox.Left < last.Spans[0].Left) last.LeadText = s.Text.TrimStart();
                    last.Spans.Add((s.Bbox.Left, s.Bbox.Right));
                }
                else
                {
                    lines.Add(new LineStat
                    {
                        Top = s.Bbox.Top,
                        Left = s.Bbox.Left,
                        Right = s.Bbox.Right,
                        NonWsChars = nonWs,
                        LeadText = s.Text.TrimStart(),
                        Spans = new List<(float, float)> { (s.Bbox.Left, s.Bbox.Right) },
                    });
                }
            }
            // Keep each line's span edges left-sorted for the gap analysis below.
            foreach (var l in lines)
                OxSpanCompare.SortStable(l.Spans, (a, b) => OxSpanCompare.SafeFloatCmp(a.Left, b.Left));
            return lines;
        }

        /// <summary>A numbered/bracketed reference entry start: <c>12.</c>, <c>12)</c>,
        /// <c>[12]</c>, <c>(12)</c>.</summary>
        private static bool StartsNumberedEntry(string lead)
        {
            if (lead.Length == 0) return false;
            if ((lead[0] == '[' || lead[0] == '(') && lead.Length > 1 && lead[1] is >= '0' and <= '9') return true;
            int digits = 0;
            while (digits < 4 && digits < lead.Length && lead[digits] is >= '0' and <= '9') digits++;
            if (digits is >= 1 and <= 3 && digits < lead.Length)
                return lead[digits] == '.' || lead[digits] == ')';
            return false;
        }

        /// <summary>A label … value row: one large interior horizontal gap with real text on
        /// both sides.</summary>
        private static bool LineHasLabelValueGap(LineStat l, float regionWidth)
        {
            if (l.Spans.Count < 2) return false;
            float threshold = regionWidth * 0.25f;
            for (int w = 1; w < l.Spans.Count; w++)
                if (l.Spans[w].Left - l.Spans[w - 1].Right >= threshold) return true;
            return false;
        }

        /// <summary>
        /// A hanging-indent two-level left edge: a primary entry-start edge and a secondary
        /// continuation edge, both carrying a meaningful share of lines. Reference lists and
        /// prose with first-line indents both produce this, and for the reorder gate that
        /// distinction does not matter — both reorder column-major.
        /// </summary>
        private static bool HasHangingIndent(List<float> leftEdges, float medHeight)
        {
            if (leftEdges.Count < 6) return false;
            float l0 = float.MaxValue;
            foreach (float x in leftEdges) l0 = MathF.Min(l0, x);
            float nearTol = medHeight * 0.5f;
            int loBand = 0, hiBand = 0;
            foreach (float x in leftEdges)
            {
                if (MathF.Abs(x - l0) <= nearTol) loBand++;
                float d = x - l0;
                if (d >= medHeight * 0.8f && d <= medHeight * 5.0f) hiBand++;
            }
            int n = leftEdges.Count;
            return loBand * 4 >= n && hiBand * 4 >= n;
        }
    }
}
