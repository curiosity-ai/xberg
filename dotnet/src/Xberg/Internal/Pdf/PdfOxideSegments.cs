// The structure pipeline's own span producer: a port of
//   crates/xberg/src/pdf/oxide/hierarchy.rs :: extract_segments_from_page_inner
// together with the reading-order selection and inline-script rejoining it calls.
//
// This is deliberately NOT the plain-text path. Upstream runs two different span
// pipelines over the same page: `extract_page_text_column_aware` asks pdf_oxide for
// `ReadingOrder::ColumnAware` spans and assembles them into lines of text, while the
// structure pipeline asks for `ReadingOrder::TopToBottom`, repairs the order itself, and
// keeps every span whole. The difference matters because a `SegmentData` is one span, not
// one assembled line: that is what lets a heading, a bold lead-in and the prose after it
// live in the same paragraph and still come out as separate styled runs.
using System;
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Layout;

namespace Xberg.Internal.Pdf;

internal static class PdfOxideSegments
{
    // select_reading_order gates (hierarchy.rs).
    private const float COLUMN_BRIDGE_FRACTION = 0.6f;
    private const float MIN_COLUMN_GUTTER_PTS = 8.0f;
    private const int MIN_COLUMN_SIDE_SPANS = 2;
    private const float MIN_TWO_COLUMN_CONTENT_WIDTH_PTS = 144.0f;
    private const int MIN_PROSE_LINES_PER_SIDE = 4;
    private const int MIN_PROSE_LINE_ALPHA_CHARS = 8;
    private const int MIN_PROSE_LINE_WORDS = 3;
    private const float MIN_PROSE_ALPHA_RATIO = 0.55f;
    private const float MIN_SIDE_BALANCE_RATIO = 0.15f;
    private const float MIN_VERTICAL_OVERLAP_RATIO = 0.35f;
    private const float PROSE_LINE_Y_TOLERANCE_PTS = 4.0f;

    // rejoin_inline_scripts gates (hierarchy.rs).
    private const int INLINE_SCRIPT_LOOKBACK = 8;
    private const float INLINE_SCRIPT_MIN_FONT_RATIO = 0.5f;
    private const float INLINE_SCRIPT_MAX_FONT_RATIO = 0.8f;
    private const float INLINE_SCRIPT_MIN_BASELINE_SHIFT_EM = 0.08f;
    private const float INLINE_SCRIPT_MAX_BASELINE_SHIFT_EM = 0.35f;
    private const float INLINE_SCRIPT_MAX_SUFFIX_GAP_EM = 0.12f;
    private const float INLINE_SCRIPT_SAME_BASELINE_TOLERANCE_EM = 0.02f;
    private const int INLINE_SCRIPT_MAX_CHARS = 4;
    private const float INLINE_SCRIPT_MIN_WIDTH_COVERAGE = 0.7f;
    private const float INLINE_SCRIPT_MAX_WIDTH_COVERAGE = 1.3f;

    /// <summary>
    /// One page's spans as the structure pipeline's segments.
    /// </summary>
    /// <param name="spans">The page's spans as the extractor produced them.</param>
    /// <param name="pageWidth">MediaBox width, used only by the two-column tests.</param>
    /// <param name="pageHeight">MediaBox height, likewise.</param>
    public static List<SegmentData> FromPage(IReadOnlyList<OxTextSpan> spans, float pageWidth, float pageHeight)
    {
        if (spans.Count == 0) return new List<SegmentData>();

        // The extractor hands its caller ColumnAware order because that is what the text
        // assembler wants; upstream's structure path asks for `TopToBottom` instead and then
        // repairs the order itself, so re-impose the row-band sort here.
        var work = new List<OxTextSpan>(spans.Count);
        foreach (var s in spans) work.Add(s.Clone());
        OxSpanCompare.SortSpansRowAware(work);

        ReorderPageReadingOrder(work, pageWidth, pageHeight);
        work = RejoinInlineScripts(work);

        var segments = new List<SegmentData>(work.Count);
        foreach (var span in work)
        {
            if (span.ArtifactType is not null) continue;
            if (span.Text.Trim().Length == 0) continue;
            segments.Add(new SegmentData
            {
                Text = span.Text,
                X = span.Bbox.X,
                Y = span.Bbox.Y,
                Width = span.Bbox.Width,
                Height = span.Bbox.Height,
                FontSize = span.FontSize,
                // The producer only ever grades a run Bold or Normal, which is the exact test
                // upstream makes here (`span.font_weight == FontWeight::Bold`).
                IsBold = span.FontWeight == OxFontWeight.Bold,
                IsItalic = span.IsItalic,
                IsMonospace = span.IsMonospace,
                BaselineY = span.Bbox.Y,
            });
        }
        return PdfStructure.DedupeRedrawnSegments(segments);
    }

    // ── reading order ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Repair the page's reading order: the sparse two-column fix always, then the dense
    /// band-split fix, and pdf_oxide's own XY-Cut only if neither applied and the page still
    /// looks like balanced two-column prose.
    /// </summary>
    /// <remarks>
    /// The dense repair short-circuits the XY-Cut deliberately: XY-Cut re-orders the whole
    /// span list from scratch and its valley detector uses a wider minimum gutter than the
    /// dense repair's own per-line detector, so letting it run would silently discard the band
    /// order just applied (Rust <c>reorder_page_reading_order</c>).
    /// </remarks>
    private static void ReorderPageReadingOrder(List<OxTextSpan> spans, float pageWidth, float pageHeight)
    {
        PermuteBy(spans, proxies => { PdfColumnReorder.ReorderSparseTwoColumnPage(proxies, pageWidth); return proxies; });

        bool dense = false;
        PermuteBy(spans, proxies => { dense = PdfColumnReorder.ReorderDenseTwoColumnPage(proxies, pageWidth); return proxies; });
        if (dense) return;

        if (!IsColumnAwarePage(spans, pageWidth, pageHeight)) return;
        PermuteBy(spans, PdfReadingOrder.Order);
    }

    /// <summary>
    /// Run one of the <see cref="TextSpan"/>-shaped reordering passes over a list of extractor
    /// spans, then apply the permutation it produced back to that list.
    /// </summary>
    private static void PermuteBy(List<OxTextSpan> spans, Func<List<TextSpan>, List<TextSpan>> reorder)
    {
        var proxies = OxSpanBridge.ToPdfSpans(spans);
        var origin = new Dictionary<TextSpan, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < proxies.Count; i++) origin[proxies[i]] = i;

        var ordered = reorder(proxies);
        if (ordered.Count != spans.Count) return;

        var permuted = new List<OxTextSpan>(spans.Count);
        foreach (var proxy in ordered)
        {
            if (!origin.TryGetValue(proxy, out int index)) return;
            permuted.Add(spans[index]);
        }
        spans.Clear();
        spans.AddRange(permuted);
    }

    /// <summary>
    /// Whether the page carries conservative geometric evidence of two prose columns, which is
    /// the only condition under which the structure path asks for column-aware ordering at all
    /// (Rust <c>select_reading_order</c>).
    /// </summary>
    private static bool IsColumnAwarePage(List<OxTextSpan> spans, float pageWidth, float pageHeight)
    {
        if (!float.IsFinite(pageWidth) || pageWidth <= 0f || !float.IsFinite(pageHeight) || pageHeight <= 0f)
            return false;

        var usable = spans.Where(IsUsableSpan).ToList();
        var (contentMin, contentMax) = ContentBounds(usable);
        if (!(contentMax > contentMin)) return false;
        float contentWidth = contentMax - contentMin;
        if (contentWidth < MIN_TWO_COLUMN_CONTENT_WIDTH_PTS) return false;

        var body = usable.Where(s => s.Bbox.Width <= contentWidth * COLUMN_BRIDGE_FRACTION).ToList();
        float gutterX = DetectGutterX(body) ?? (contentMin + contentMax) * 0.5f;

        var left = SideSupport(body.Where(s => s.Bbox.X + s.Bbox.Width <= gutterX));
        var right = SideSupport(body.Where(s => s.Bbox.X >= gutterX));
        return HasBalancedVerticalSupport(left, right);
    }

    private static bool IsUsableSpan(OxTextSpan span) =>
        span.ArtifactType is null
        && span.Text.Trim().Length != 0
        && float.IsFinite(span.Bbox.X) && float.IsFinite(span.Bbox.Y)
        && float.IsFinite(span.Bbox.Width) && float.IsFinite(span.Bbox.Height)
        && span.Bbox.Width > 0f && span.Bbox.Height > 0f;

    private static (float Min, float Max) ContentBounds(List<OxTextSpan> spans)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (var s in spans)
        {
            min = Math.Min(min, s.Bbox.X);
            max = Math.Max(max, s.Bbox.X + s.Bbox.Width);
        }
        return float.IsFinite(min) && float.IsFinite(max) ? (min, max) : (0f, 0f);
    }

    /// <summary>The widest uncovered vertical band between the column-width spans, when it sits
    /// near the middle of the content and has enough spans either side of it.</summary>
    private static float? DetectGutterX(List<OxTextSpan> spans)
    {
        if (spans.Count < MIN_COLUMN_SIDE_SPANS * 2) return null;
        var (contentMin, contentMax) = ContentBounds(spans);
        if (!(contentMax > contentMin)) return null;
        float contentWidth = contentMax - contentMin;
        if (contentWidth < MIN_TWO_COLUMN_CONTENT_WIDTH_PTS) return null;

        float bridgeWidth = contentWidth * COLUMN_BRIDGE_FRACTION;
        var extents = spans
            .Where(s => s.Bbox.Width <= bridgeWidth)
            .Select(s => (Left: s.Bbox.X, Right: s.Bbox.X + s.Bbox.Width))
            .ToList();
        if (extents.Count < MIN_COLUMN_SIDE_SPANS * 2) return null;
        extents.Sort((a, b) => a.Left.CompareTo(b.Left));

        float coverRight = extents[0].Right;
        float bestGap = 0f, bestMid = 0f;
        int leftCount = 0;
        for (int index = 1; index < extents.Count; index++)
        {
            float gap = extents[index].Left - coverRight;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestMid = (coverRight + extents[index].Left) * 0.5f;
                leftCount = index;
            }
            coverRight = Math.Max(coverRight, extents[index].Right);
        }

        int rightCount = extents.Count - leftCount;
        float relativeMid = (bestMid - contentMin) / contentWidth;
        return bestGap >= MIN_COLUMN_GUTTER_PTS
            && relativeMid >= 0.3f && relativeMid <= 0.7f
            && leftCount >= MIN_COLUMN_SIDE_SPANS
            && rightCount >= MIN_COLUMN_SIDE_SPANS
            ? bestMid : null;
    }

    /// <summary>Whether a run reads as running prose rather than a label, a number or code.</summary>
    private static bool ProseLike(string text, int monospaceSpans, int spanCount)
    {
        if (monospaceSpans * 2 >= Math.Max(spanCount, 1)) return false;
        int alpha = 0, alnum = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c)) alpha++;
            if (char.IsLetterOrDigit(c)) alnum++;
        }
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
        return alpha >= MIN_PROSE_LINE_ALPHA_CHARS
            && words >= MIN_PROSE_LINE_WORDS
            && (float)alpha / Math.Max(alnum, 1) >= MIN_PROSE_ALPHA_RATIO;
    }

    /// <summary>The distinct baselines on one side of the gutter that carry prose.</summary>
    private static List<float> SideSupport(IEnumerable<OxTextSpan> spans)
    {
        var ys = spans
            .Where(s => ProseLike(s.Text, s.IsMonospace ? 1 : 0, 1))
            .Select(s => s.Bbox.Y)
            .ToList();
        ys.Sort();
        var deduped = new List<float>(ys.Count);
        foreach (float y in ys)
            if (deduped.Count == 0 || Math.Abs(y - deduped[^1]) > PROSE_LINE_Y_TOLERANCE_PTS) deduped.Add(y);
        return deduped;
    }

    private static bool HasBalancedVerticalSupport(List<float> left, List<float> right)
    {
        if (left.Count < MIN_PROSE_LINES_PER_SIDE || right.Count < MIN_PROSE_LINES_PER_SIDE) return false;
        float balance = (float)Math.Min(left.Count, right.Count) / Math.Max(left.Count, right.Count);
        float leftLow = left.Min(), leftHigh = left.Max();
        float rightLow = right.Min(), rightHigh = right.Max();
        float overlap = Math.Max(Math.Min(leftHigh, rightHigh) - Math.Max(leftLow, rightLow), 0f);
        float shorterExtent = Math.Min(leftHigh - leftLow, rightHigh - rightLow);
        return balance >= MIN_SIDE_BALANCE_RATIO && shorterExtent > 0f
            && overlap / shorterExtent >= MIN_VERTICAL_OVERLAP_RATIO;
    }

    // ── inline scripts ──────────────────────────────────────────────────────────────

    private readonly record struct ScriptAttachment(int ScriptIndex, int InsertionIndex);

    /// <summary>
    /// Fold a super- or subscript run back into the word it belongs to.
    /// </summary>
    /// <remarks>
    /// A footnote marker or a chemical subscript arrives as its own span at a smaller size and
    /// a shifted baseline. Left alone it becomes its own segment, which splits the word around
    /// it into three and defeats every downstream test that reads whole words. Ports Rust
    /// <c>rejoin_inline_scripts</c>.
    /// </remarks>
    private static List<OxTextSpan> RejoinInlineScripts(List<OxTextSpan> spans)
    {
        var byBase = new Dictionary<int, List<ScriptAttachment>>();
        var attached = new bool[spans.Count];
        for (int scriptIndex = 0; scriptIndex < spans.Count; scriptIndex++)
        {
            if (byBase.ContainsKey(scriptIndex)) continue;
            if (FindInlineScriptBase(spans, attached, scriptIndex) is not var (baseIndex, insertionIndex)) continue;
            attached[scriptIndex] = true;
            if (!byBase.TryGetValue(baseIndex, out var list)) byBase[baseIndex] = list = new List<ScriptAttachment>();
            list.Add(new ScriptAttachment(scriptIndex, insertionIndex));
        }

        if (byBase.Count == 0) return spans;

        var repaired = new List<OxTextSpan>(spans.Count);
        for (int index = 0; index < spans.Count; index++)
        {
            if (attached[index]) continue;
            if (byBase.TryGetValue(index, out var scripts))
            {
                byBase.Remove(index);
                EmitBaseWithScripts(spans[index], scripts, spans, repaired);
            }
            else repaired.Add(spans[index]);
        }
        return repaired;
    }

    private static (int BaseIndex, int InsertionIndex)? FindInlineScriptBase(
        List<OxTextSpan> spans, bool[] attached, int scriptIndex)
    {
        if (scriptIndex < 0 || scriptIndex >= spans.Count) return null;
        var script = spans[scriptIndex];
        if (!IsCompactHorizontalAsciiSpan(script)) return null;

        int start = Math.Max(0, scriptIndex - INLINE_SCRIPT_LOOKBACK);
        (int baseIndex, int insertion, float shift, float distance, int lag)? best = null;
        for (int baseIndex = start; baseIndex < scriptIndex; baseIndex++)
        {
            if (attached[baseIndex]) continue;
            var baseSpan = spans[baseIndex];
            if (InlineScriptInsertion(baseSpan, script, baseIndex + 1 == scriptIndex) is not int insertion) continue;
            var candidate = (baseIndex, insertion,
                Math.Abs(script.Bbox.Y - baseSpan.Bbox.Y),
                HorizontalAttachmentDistance(baseSpan, script),
                scriptIndex - baseIndex);
            if (best is not { } b
                || candidate.Item3 < b.shift
                || (candidate.Item3 == b.shift && candidate.Item4 < b.distance)
                || (candidate.Item3 == b.shift && candidate.Item4 == b.distance && candidate.Item5 < b.lag))
                best = candidate;
        }
        return best is { } chosen ? (chosen.baseIndex, chosen.insertion) : null;
    }

    private static int? InlineScriptInsertion(OxTextSpan baseSpan, OxTextSpan script, bool immediatelyFollows)
    {
        if (baseSpan.ArtifactType is not null || script.ArtifactType is not null
            || !IsHorizontalLtr(baseSpan)
            || !IsAscii(baseSpan.Text)
            || !baseSpan.Text.Any(char.IsAsciiLetter)
            || !HasValidSpanGeometry(baseSpan)
            || !HasValidSpanGeometry(script))
            return null;

        float fontRatio = script.FontSize / baseSpan.FontSize;
        if (fontRatio < INLINE_SCRIPT_MIN_FONT_RATIO || fontRatio > INLINE_SCRIPT_MAX_FONT_RATIO) return null;

        float baseRight = baseSpan.Bbox.X + baseSpan.Bbox.Width;
        float gap = script.Bbox.X - baseRight;
        float baselineShift = Math.Abs(script.Bbox.Y - baseSpan.Bbox.Y);
        if (baselineShift > baseSpan.FontSize * INLINE_SCRIPT_MAX_BASELINE_SHIFT_EM) return null;

        bool sameBaselineSuffix = immediatelyFollows
            && gap >= 0f
            && gap <= baseSpan.FontSize * INLINE_SCRIPT_MAX_SUFFIX_GAP_EM
            && baselineShift <= baseSpan.FontSize * INLINE_SCRIPT_SAME_BASELINE_TOLERANCE_EM;
        bool shiftedScript = baselineShift >= baseSpan.FontSize * INLINE_SCRIPT_MIN_BASELINE_SHIFT_EM
            && baselineShift <= baseSpan.FontSize * INLINE_SCRIPT_MAX_BASELINE_SHIFT_EM;
        float normalizedRise = Math.Abs(script.TextRise) * script.FontSize / baseSpan.FontSize;
        bool explicitRise = float.IsFinite(normalizedRise)
            && normalizedRise >= INLINE_SCRIPT_MIN_BASELINE_SHIFT_EM
            && normalizedRise <= INLINE_SCRIPT_MAX_BASELINE_SHIFT_EM;
        if (!sameBaselineSuffix && !shiftedScript && !explicitRise) return null;
        if (script.Bbox.X < baseSpan.Bbox.X || gap > baseSpan.FontSize * INLINE_SCRIPT_MAX_SUFFIX_GAP_EM) return null;

        int charCount = baseSpan.Text.Length;
        if (script.Bbox.X >= baseRight) return charCount;
        var origins = CharacterOrigins(baseSpan);
        if (origins is null) return null;
        int partition = 0;
        while (partition < origins.Count && origins[partition] < script.Bbox.X) partition++;
        return partition;
    }

    private static bool IsCompactHorizontalAsciiSpan(OxTextSpan span)
    {
        int charCount = span.Text.Length;
        if (charCount == 0 || charCount > INLINE_SCRIPT_MAX_CHARS) return false;
        if (span.ArtifactType is not null || !IsAscii(span.Text)) return false;
        foreach (char c in span.Text)
        {
            if (char.IsWhiteSpace(c)) return false;
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '=' or '(' or ')' or ',' or '.')) return false;
        }
        return IsHorizontalLtr(span);
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text) if (c > 0x7F) return false;
        return true;
    }

    /// <summary>Horizontal left-to-right text painted on the page axis (Rust
    /// <c>is_horizontal_ltr</c>): the inline-script arithmetic is in raw page coordinates.</summary>
    private static bool IsHorizontalLtr(OxTextSpan span) =>
        span.Wmode == 0 && !span.RtlDrawLogical && Math.Abs(span.RotationDegrees) <= float.Epsilon;

    private static bool HasValidSpanGeometry(OxTextSpan span) =>
        float.IsFinite(span.Bbox.X) && float.IsFinite(span.Bbox.Y)
        && float.IsFinite(span.Bbox.Width) && float.IsFinite(span.Bbox.Height)
        && float.IsFinite(span.FontSize)
        && span.Bbox.Width > 0f && span.Bbox.Height > 0f && span.FontSize > 0f;

    /// <summary>Per-character left edges, from the producer's own offsets where they are
    /// trustworthy and from the advance widths, rescaled onto the bbox, where they are not.</summary>
    private static List<float>? CharacterOrigins(OxTextSpan span)
    {
        int charCount = span.Text.Length;
        float bboxRight = span.Bbox.X + span.Bbox.Width;
        if (span.CharXOffsets.Count == charCount
            && span.CharXOffsets.All(float.IsFinite)
            && StrictlyIncreasing(span.CharXOffsets)
            && span.CharXOffsets.Count > 0
            && span.CharXOffsets[0] >= span.Bbox.X
            && span.CharXOffsets[^1] <= bboxRight)
            return new List<float>(span.CharXOffsets);

        float widthSum = 0f;
        foreach (float w in span.CharWidths) widthSum += w;
        float coverage = widthSum / span.Bbox.Width;
        if (span.CharWidths.Count != charCount
            || !span.CharWidths.All(w => float.IsFinite(w) && w > 0f)
            || !float.IsFinite(widthSum) || widthSum <= 0f
            || coverage < INLINE_SCRIPT_MIN_WIDTH_COVERAGE || coverage > INLINE_SCRIPT_MAX_WIDTH_COVERAGE)
            return null;

        float scale = span.Bbox.Width / widthSum;
        float x = span.Bbox.X;
        var origins = new List<float>(charCount);
        foreach (float w in span.CharWidths)
        {
            origins.Add(x);
            x += w * scale;
        }
        return origins;
    }

    private static bool StrictlyIncreasing(List<float> values)
    {
        for (int i = 0; i + 1 < values.Count; i++) if (!(values[i] < values[i + 1])) return false;
        return true;
    }

    private static float HorizontalAttachmentDistance(OxTextSpan baseSpan, OxTextSpan script)
    {
        float baseRight = baseSpan.Bbox.X + baseSpan.Bbox.Width;
        return script.Bbox.X <= baseRight ? 0f : script.Bbox.X - baseRight;
    }

    private static void EmitBaseWithScripts(
        OxTextSpan baseSpan, List<ScriptAttachment> scripts, List<OxTextSpan> spans, List<OxTextSpan> output)
    {
        scripts.Sort((left, right) =>
        {
            int byIndex = left.InsertionIndex.CompareTo(right.InsertionIndex);
            return byIndex != 0 ? byIndex : spans[left.ScriptIndex].Bbox.X.CompareTo(spans[right.ScriptIndex].Bbox.X);
        });

        int rangeStart = 0;
        int charCount = baseSpan.Text.Length;
        foreach (var script in scripts)
        {
            var fragment = script.InsertionIndex > rangeStart
                ? SplitSpan(baseSpan, rangeStart, script.InsertionIndex) : null;
            var normalized = NormalizeScriptSpan(spans[script.ScriptIndex], baseSpan);
            if (script.InsertionIndex == charCount)
            {
                if (fragment is not null)
                {
                    AppendSpanText(fragment, normalized);
                    output.Add(fragment);
                }
                else if (output.Count > 0) AppendSpanText(output[^1], normalized);
            }
            else
            {
                if (fragment is not null) output.Add(fragment);
                output.Add(normalized);
            }
            rangeStart = script.InsertionIndex;
        }
        if (SplitSpan(baseSpan, rangeStart, charCount) is { } tail) output.Add(tail);
    }

    private static void AppendSpanText(OxTextSpan target, OxTextSpan suffix)
    {
        target.Text += suffix.Text;
        float targetRight = target.Bbox.X + target.Bbox.Width;
        float suffixRight = suffix.Bbox.X + suffix.Bbox.Width;
        target.Bbox = new OxRect(target.Bbox.X, target.Bbox.Y,
            Math.Max(targetRight, suffixRight) - target.Bbox.X, target.Bbox.Height);
        target.CharXOffsets.Clear();
        target.CharWidths.Clear();
    }

    private static OxTextSpan? SplitSpan(OxTextSpan span, int start, int end)
    {
        if (start >= end) return null;
        int charCount = span.Text.Length;
        if (start == 0 && end == charCount) return span.Clone();
        var origins = CharacterOrigins(span);
        if (origins is null) return null;

        var fragment = span.Clone();
        fragment.Text = span.Text.Substring(start, end - start);
        float x = origins[start];
        float endX = end < origins.Count ? origins[end] : span.Bbox.X + span.Bbox.Width;
        fragment.Bbox = new OxRect(x, span.Bbox.Y, Math.Max(endX - x, 0f), span.Bbox.Height);
        fragment.CharXOffsets = origins.GetRange(start, end - start);
        fragment.CharWidths = span.CharWidths.Count == charCount
            ? span.CharWidths.GetRange(start, end - start)
            : new List<float>();
        return fragment;
    }

    /// <summary>The script run wearing its base's typography, so the two read as one word.</summary>
    private static OxTextSpan NormalizeScriptSpan(OxTextSpan script, OxTextSpan baseSpan)
    {
        var normalized = script.Clone();
        normalized.Bbox = new OxRect(script.Bbox.X, baseSpan.Bbox.Y, script.Bbox.Width, baseSpan.Bbox.Height);
        normalized.FontName = baseSpan.FontName;
        normalized.FontSize = baseSpan.FontSize;
        normalized.FontWeight = baseSpan.FontWeight;
        normalized.IsItalic = baseSpan.IsItalic;
        normalized.IsMonospace = baseSpan.IsMonospace;
        normalized.Mcid = baseSpan.Mcid;
        normalized.McidScope = baseSpan.McidScope;
        normalized.HeadingLevel = baseSpan.HeadingLevel;
        normalized.TextRise = 0f;
        return normalized;
    }
}
