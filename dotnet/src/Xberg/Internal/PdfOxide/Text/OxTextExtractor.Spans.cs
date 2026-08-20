// Span post-processing, ported from pdf_oxide-0.3.77
// src/extractors/text.rs:
//   2745-2752  DEDUP_OVERLAP_RATIO / DEDUP_OVERLAP_CAP_PT
//   3930-4013  deduplicate_overlapping_chars
//   4014-4104  snap_superscript_baselines
//   4105-4188  sort_spans_by_reading_order / sort_spans_vertical_tategaki / simple_sort_spans
//   4189-4320  detect_span_columns / sort_spans_by_columns
//   4321-4497  deduplicate_overlapping_spans / dedup_stroke_fill_overlap
//   4530-4588  detect_rtl_draw_direction
//   4589-4668  bimodal_line_gap_thresholds / bimodal_gap_split
//   4669-5331  merge_adjacent_spans
//   5332-5375  sort_by_reading_order
//   5396-5510  split_fused_words / split_on_camelcase
//
// `merge_adjacent_spans` is the granularity the rest of the pipeline is calibrated
// to, so every guard is kept in source order with its original constant: the
// decimal merge, the cross-font and small-caps glue, the ligature/kerning
// suppression (via the space-decision seam), the per-line bimodal rescue, and the
// column-boundary and severe-overlap gates all interact, and dropping one moves
// span boundaries corpus-wide.
//
// Rust iterates `chars()` — Unicode scalar values — so every text rule here walks
// runes. Where Rust measures `str::len()` (UTF-8 bytes) the port measures UTF-8
// bytes too; those lengths feed thresholds and proportional bbox splits.
using System.Globalization;
using System.Text;
using Xberg.Internal.PdfOxide.Layout;

namespace Xberg.Internal.PdfOxide.Text;

/// <summary>
/// The space-insertion decision (`should_insert_space`, text.rs:1190), ported
/// separately as <c>OxSpaceDecisionRules.ShouldInsertSpace</c>. The merger reaches it
/// through this delegate so the two halves stay in different files.
/// </summary>
internal delegate OxSpaceDecision OxShouldInsertSpaceFn(
    string precedingText,
    string followingText,
    float gapPt,
    float fontSize,
    string fontName,
    IOxSpanFonts? fonts,
    bool tjOffsetTriggered,
    OxSpanMergingConfig config,
    OxRect? prevBbox,
    OxRect? nextBbox,
    float prevFontSize,
    float nextFontSize);

/// <summary>
/// Per-page facts the merger needs that <see cref="IOxSpanFonts"/> does not carry:
/// whether a font declared a real /Widths array (the fallback-advance correction in
/// <c>corrected_space_gap</c> keys off it), and whether the content stream was marked
/// /ReversedChars (which flips two RTL guards).
/// </summary>
internal interface IOxSpanMergeContext
{
    /// <summary>
    /// <c>FontInfo::has_explicit_widths</c> for a page font. Upstream is
    /// <c>fonts.get(name).map(..).unwrap_or(true)</c>, so a font the page never declared
    /// counts as reliable.
    /// </summary>
    bool HasExplicitWidths(string fontName);

    /// <summary>True when the page drew RTL glyphs under /ReversedChars (§14.8.2.3.3).</summary>
    bool SawReversedChars { get; }
}

internal sealed partial class OxTextExtractor
{
    // Spans, Chars, MergingConfig and SpanFonts live in OxTextExtractor.State.cs, which
    // owns the field set every section of the extractor shares.

    /// <summary>Seam to the separately-ported space decision; see <see cref="OxShouldInsertSpaceFn"/>.</summary>
    internal OxShouldInsertSpaceFn ShouldInsertSpace = UnwiredShouldInsertSpace;

    /// <summary>Seam to the page's font-width and /ReversedChars facts; null means the defaults.</summary>
    internal IOxSpanMergeContext? MergeContext;

    private static OxSpaceDecision UnwiredShouldInsertSpace(
        string precedingText, string followingText, float gapPt, float fontSize, string fontName,
        IOxSpanFonts? fonts, bool tjOffsetTriggered, OxSpanMergingConfig config,
        OxRect? prevBbox, OxRect? nextBbox, float prevFontSize, float nextFontSize) =>
        throw new InvalidOperationException(
            "OxTextExtractor.ShouldInsertSpace was never wired to OxSpaceDecisionRules.ShouldInsertSpace.");

    /// <summary>
    /// Deduplicate overlapping characters on the same line (text.rs:3930).
    /// </summary>
    /// <remarks>
    /// Some PDFs render text several times at slightly different X positions (faux
    /// bold, shadowing), which garbles extraction when every pass is kept. The
    /// threshold is a fraction of the glyph's own advance rather than an absolute
    /// point value: narrow doublets (`ll`, `rr`, `II`) are one full advance apart, and
    /// a fixed 2pt window would collapse them wherever that advance drops below ~2pt
    /// (Helvetica at &lt;=9pt).
    /// </remarks>
    internal void DeduplicateOverlappingChars()
    {
        if (Chars.Count == 0)
        {
            return;
        }

        int? prevYRounded = null;
        float? prevX = null;
        char? prevChar = null;

        // Compacted in place: the predicate only looks back at the previously KEPT
        // glyph, which an in-order visit preserves.
        int write = 0;
        for (int read = 0; read < Chars.Count; read++)
        {
            OxTextChar ch = Chars[read];
            int yRounded = OxSpanCompare.RoundToI32(ch.Bbox.Y);
            float x = ch.Bbox.X;

            bool shouldSkip = false;
            if (prevYRounded is int prevY && prevX is float prevXVal && prevChar is char prevCh)
            {
                // Reference width: the advance when known, else the bbox width, else the
                // legacy cap so inputs without advance metrics keep their old behaviour.
                float refWidth = ch.AdvanceWidth > 0.0f ? ch.AdvanceWidth
                    : ch.Bbox.Width > 0.0f ? ch.Bbox.Width
                    : DedupOverlapCapPt;
                float threshold = MathF.Min(refWidth * DedupOverlapRatio, DedupOverlapCapPt);
                shouldSkip = ch.Char == prevCh && yRounded == prevY && MathF.Abs(x - prevXVal) < threshold;
            }

            if (!shouldSkip)
            {
                prevYRounded = yRounded;
                prevX = x;
                prevChar = ch.Char;
                Chars[write++] = ch;
            }
        }
        Chars.RemoveRange(write, Chars.Count - write);
    }

    /// <summary>
    /// Snap super/subscript glyph spans onto the baseline of an adjacent base span so
    /// downstream row-aware sorting keeps them inline (text.rs:4014).
    /// </summary>
    /// <remarks>
    /// Text rise (Ts, §9.3.7) is a per-text-state vertical offset that survives into the
    /// extracted bbox, so a Y-descending sort reads a line of superscript affiliation
    /// markers as a row preceding the author names they annotate. Snapping each
    /// candidate's Y onto its matched base puts them back in one Y-band.
    /// </remarks>
    internal void SnapSuperscriptBaselines()
    {
        int n = Spans.Count;
        if (n < 2)
        {
            return;
        }

        // (x, y, width, fontSize) snapshot: reads must not see the Y values this loop writes.
        var snapshot = new (float X, float Y, float W, float Fs)[n];
        for (int i = 0; i < n; i++)
        {
            OxRect b = Spans[i].Bbox;
            snapshot[i] = (b.X, b.Y, b.Width, Spans[i].FontSize);
        }

        // A valid base `j` has `y_offset = sy - by` in [0, bfs*0.5], so `by` lies in
        // [sy - max_fs*0.5, sy]. Sorting by Y once and binary-searching that window per
        // candidate turns an O(n^2) double loop — which hung for tens of seconds on
        // hOCR layers emitting thousands of spans — into O(n log n + n*window), over a
        // strict superset of the acceptable bases, so the result is unchanged.
        float maxFs = 0.0f;
        for (int i = 0; i < n; i++)
        {
            maxFs = MathF.Max(maxFs, snapshot[i].Fs);
        }
        float maxHalfEm = maxFs * 0.5f;

        var byOrder = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            byOrder.Add(i);
        }
        OxSpanCompare.SortStable(byOrder, (a, b) => OxSpanCompare.SafeFloatCmp(snapshot[a].Y, snapshot[b].Y));
        var ysSorted = new float[n];
        for (int k = 0; k < n; k++)
        {
            ysSorted[k] = snapshot[byOrder[k]].Y;
        }

        for (int i = 0; i < n; i++)
        {
            (float sx, float sy, _, float sfs) = snapshot[i];
            if (sfs <= 0.0f)
            {
                continue;
            }

            // Closest base in Y wins, so a candidate sandwiched between two body lines
            // snaps onto the nearer one.
            float? bestBaseY = null;
            float bestAbsOffset = float.MaxValue;
            int lo = PartitionPoint(ysSorted, y => y < sy - maxHalfEm);
            int hi = PartitionPoint(ysSorted, y => y <= sy);
            for (int k = lo; k < hi; k++)
            {
                int j = byOrder[k];
                if (i == j)
                {
                    continue;
                }
                (float bx, float by, float bw, float bfs) = snapshot[j];
                if (bfs <= sfs * 1.15f)
                {
                    continue;
                }
                float yOffset = sy - by;
                float halfEm = bfs * 0.5f;
                if (MathF.Abs(yOffset) > halfEm)
                {
                    continue;
                }
                // Subscripts are left lowered: the document-level pass that substitutes
                // U+2080..U+2089 (H2O -> H<sub>2</sub>O) needs to see their original
                // baseline, and snapping them would defeat it.
                if (yOffset < 0.0f)
                {
                    continue;
                }
                // X adjacency: the candidate's left edge must sit near the base's right
                // edge — within one base font size to the right, with a little slack to
                // the left for kerning. Combining diacritics are already excluded by the
                // size-ratio gate, since they share their base letter's font size.
                float baseRight = bx + bw;
                float dx = sx - baseRight;
                if (dx < -bfs * 0.25f || dx > bfs)
                {
                    continue;
                }
                float absOff = MathF.Abs(yOffset);
                if (absOff < bestAbsOffset)
                {
                    bestAbsOffset = absOff;
                    bestBaseY = by;
                }
            }
            if (bestBaseY is float baseY)
            {
                Spans[i].Bbox = WithY(Spans[i].Bbox, baseY);
            }
        }
    }

    /// <summary>Sort spans into reading order, top-to-bottom then left-to-right (text.rs:4105).</summary>
    internal void SortSpansByReadingOrder()
    {
        if (Spans.Count == 0)
        {
            return;
        }

        // Vertical-mode routing. Each span carries the writing mode it was emitted
        // under; a predominantly vertical page gets column-aware right-to-left
        // ordering, and the rare mixed page follows its dominant mode. Per-span wmode
        // survives either way, so export and search can still tell them apart.
        int verticalCount = 0;
        foreach (OxTextSpan s in Spans)
        {
            if (s.Wmode == 1)
            {
                verticalCount++;
            }
        }
        int total = Spans.Count;
        if (total > 0 && verticalCount * 2 >= total)
        {
            SortSpansVerticalTategaki();
            return;
        }

        List<(float Left, float Right)> columns = DetectSpanColumns();

        if (columns.Count <= 1)
        {
            SimpleSortSpans();
        }
        else
        {
            SortSpansByColumns(columns);
        }
    }

    /// <summary>
    /// Sort spans in vertical-writing (tategaki) order: right-to-left across columns,
    /// top-to-bottom within one (text.rs:4164).
    /// </summary>
    internal void SortSpansVerticalTategaki() =>
        Spans = OxSpanCompare.SortVerticalTategaki(Spans, s => s.Bbox);

    /// <summary>Simple Y-then-X sorting for single-column layouts (text.rs:4170).</summary>
    internal void SimpleSortSpans() =>
        // Y is rounded to an integer band first: comparing raw floats with a tolerance
        // is not transitive and would not be a valid total order.
        OxSpanCompare.SortStable(Spans, (a, b) =>
        {
            int aY = OxSpanCompare.RoundToI32(a.Bbox.Y);
            int bY = OxSpanCompare.RoundToI32(b.Bbox.Y);
            int c = bY.CompareTo(aY);
            return c != 0 ? c : OxSpanCompare.SafeFloatCmp(a.Bbox.X, b.Bbox.X);
        });

    /// <summary>
    /// Detect columns from the X-coordinate distribution, as (left, right) pairs sorted
    /// left-to-right (text.rs:4189).
    /// </summary>
    internal List<(float Left, float Right)> DetectSpanColumns()
    {
        var columns = new List<(float, float)>();
        if (Spans.Count == 0)
        {
            return columns;
        }

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        foreach (OxTextSpan s in Spans)
        {
            minX = MathF.Min(minX, s.Bbox.X);
            maxX = MathF.Max(maxX, s.Bbox.X + s.Bbox.Width);
        }

        float pageWidth = maxX - minX;

        const int Bins = 100;
        float binWidth = pageWidth / Bins;
        var histogram = new int[Bins];

        foreach (OxTextSpan span in Spans)
        {
            int startBin = SaturatingUsize((span.Bbox.X - minX) / binWidth);
            int endBin = SaturatingUsize((span.Bbox.X + span.Bbox.Width - minX) / binWidth);
            int last = Math.Min(endBin, Bins - 1);
            for (int i = startBin; i <= last; i++)
            {
                histogram[i] += 1;
            }
        }

        int sum = 0;
        foreach (int c in histogram)
        {
            sum += c;
        }
        float avgDensity = sum / (float)Bins;
        float gapThreshold = MathF.Max(avgDensity * 0.2f, 1.0f); // 20% of average, or at least 1

        var gaps = new List<float>();
        bool inGap = false;
        int gapStart = 0;

        for (int i = 0; i < Bins; i++)
        {
            int count = histogram[i];
            if (count <= gapThreshold)
            {
                if (!inGap)
                {
                    gapStart = i;
                    inGap = true;
                }
            }
            else if (inGap)
            {
                // 2% of page width or an absolute 15pt floor, which catches narrow gutters.
                float gapWidth = (i - gapStart) * binWidth;
                if (gapWidth > MathF.Max(pageWidth * 0.02f, 15.0f))
                {
                    gaps.Add(minX + gapStart * binWidth);
                }
                inGap = false;
            }
        }

        if (gaps.Count == 0)
        {
            columns.Add((minX, maxX));
            return columns;
        }

        float left = minX;
        foreach (float gapX in gaps)
        {
            columns.Add((left, gapX));
            left = gapX;
        }
        columns.Add((left, maxX));

        return columns;
    }

    /// <summary>
    /// Sort spans column-aware: columns left-to-right, top-to-bottom inside each
    /// (text.rs:4271).
    /// </summary>
    internal void SortSpansByColumns(IReadOnlyList<(float Left, float Right)> columns)
    {
        var columnSpans = new List<OxTextSpan>[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            columnSpans[i] = new List<OxTextSpan>();
        }

        foreach (OxTextSpan span in Spans)
        {
            float spanCenterX = span.Bbox.X + span.Bbox.Width / 2.0f;

            int colIdx = 0;
            for (int i = 0; i < columns.Count; i++)
            {
                if (spanCenterX >= columns[i].Left && spanCenterX <= columns[i].Right)
                {
                    colIdx = i;
                    break;
                }
            }

            columnSpans[colIdx].Add(span);
        }
        Spans.Clear();

        foreach (List<OxTextSpan> colSpans in columnSpans)
        {
            OxSpanCompare.SortStable(colSpans, (a, b) =>
            {
                int aY = OxSpanCompare.RoundToI32(a.Bbox.Y);
                int bY = OxSpanCompare.RoundToI32(b.Bbox.Y);
                int c = bY.CompareTo(aY);
                return c != 0 ? c : OxSpanCompare.SafeFloatCmp(a.Bbox.X, b.Bbox.X);
            });
        }

        foreach (List<OxTextSpan> colSpans in columnSpans)
        {
            Spans.AddRange(colSpans);
        }
    }

    /// <summary>
    /// Deduplicate overlapping text spans on the same line (text.rs:4321), geometrically
    /// (same Y, X within a fraction of the span's per-glyph advance) and by content
    /// (same text, same line, overlapping X).
    /// </summary>
    internal void DeduplicateOverlappingSpans()
    {
        if (Spans.Count == 0)
        {
            return;
        }

        // Phase 0: same-text stroke+fill render passes. Maps and posters draw every
        // label twice at essentially the same CTM; without this filter the merge step
        // concatenates them into "EverestEverest".
        DedupStrokeFillOverlap();

        List<OxTextSpan> spans = Spans;
        var deduplicated = new List<OxTextSpan>(spans.Count);
        int? prevYRounded = null;
        float? prevX = null;
        string? prevText = null;
        var seenContent = new Dictionary<string, (float X, float Y)>();

        foreach (OxTextSpan span in spans)
        {
            int yRounded = OxSpanCompare.RoundToI32(span.Bbox.Y);
            float x = span.Bbox.X;

            // Phase 1: geometric — position AND text must both match. The threshold
            // scales with the span's per-glyph advance so single-glyph narrow spans
            // (`l`, `r`, `I`) are never mistaken for their legitimate neighbour.
            bool geometricDuplicate = false;
            if (prevYRounded is int prevY && prevX is float prevXVal && prevText is string prevTxt)
            {
                float charCount = MathF.Max(RuneCount(span.Text), 1);
                float perGlyphWidth = MathF.Max(span.Bbox.Width / charCount, 0.1f);
                float threshold = MathF.Min(perGlyphWidth * DedupOverlapRatio, DedupOverlapCapPt);
                geometricDuplicate = yRounded == prevY
                    && MathF.Abs(x - prevXVal) < threshold
                    && string.Equals(span.Text, prevTxt, StringComparison.Ordinal);
            }

            // Phase 2: content — positions must OVERLAP, so the same word appearing
            // twice at different places on one line is kept.
            bool contentDuplicate = false;
            if (Utf8Len(span.Text) >= 5 && seenContent.TryGetValue(span.Text, out (float X, float Y) prevPos))
            {
                float yDiff = MathF.Abs(span.Bbox.Y - prevPos.Y);
                float xDiff = MathF.Abs(span.Bbox.X - prevPos.X);
                contentDuplicate = yDiff < 2.0f && xDiff < 5.0f;
            }

            if (geometricDuplicate || contentDuplicate)
            {
                continue;
            }

            prevYRounded = yRounded;
            prevX = x;
            prevText = span.Text;

            if (Utf8Len(span.Text) >= 5)
            {
                seenContent[span.Text] = (span.Bbox.X, span.Bbox.Y);
            }
            deduplicated.Add(span);
        }

        Spans = deduplicated;
    }

    /// <summary>
    /// Drop same-text spans whose bboxes overlap an earlier span heavily (text.rs:4428) —
    /// the canonical stroke+fill pattern on maps and posters, where a label is drawn
    /// once stroked and once filled at the same position.
    /// </summary>
    /// <remarks>
    /// Keyed by lowercased text into a coarse cell grid so a label repeating N times
    /// costs O(N) rather than O(N^2); an IoU &gt;= 0.7 partner sits within ~0.176 of the
    /// box width, so scanning the 3x3 neighbourhood finds every match a full scan would.
    /// </remarks>
    internal void DedupStrokeFillOverlap()
    {
        if (Spans.Count < 2)
        {
            return;
        }
        List<OxTextSpan> spans = Spans;
        const float Cell = 16.0f;
        var seen = new Dictionary<string, Dictionary<(int, int), List<OxRect>>>();
        var kept = new List<OxTextSpan>(spans.Count);
        foreach (OxTextSpan span in spans)
        {
            string trimmed = span.Text.Trim();
            // Shorter candidates rely on the positional dedup downstream.
            if (RuneCount(trimmed) < 2)
            {
                kept.Add(span);
                continue;
            }
            string key = AsciiLowercase(trimmed);
            OxRect b = span.Bbox;
            int cx = FloorToI32((b.X + b.Width * 0.5f) / Cell);
            int cy = FloorToI32((b.Y + b.Height * 0.5f) / Cell);
            bool isDup = false;
            if (seen.TryGetValue(key, out Dictionary<(int, int), List<OxRect>>? grid))
            {
                // Saturating bounds: an out-of-page bbox can push cx/cy to the i32
                // limits, where `cx + 1` would overflow.
                int gxLo = SaturatingSub(cx, 1), gxHi = SaturatingAdd(cx, 1);
                int gyLo = SaturatingSub(cy, 1), gyHi = SaturatingAdd(cy, 1);
                for (int gx = gxLo; gx <= gxHi && !isDup; gx++)
                {
                    for (int gy = gyLo; gy <= gyHi && !isDup; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out List<OxRect>? others))
                        {
                            continue;
                        }
                        foreach (OxRect other in others)
                        {
                            // IoU >= 0.7 means the two boxes are nearly the same rectangle,
                            // which is what a stroke+fill pair produces.
                            float ix1 = MathF.Max(b.X, other.X);
                            float iy1 = MathF.Max(b.Y, other.Y);
                            float ix2 = MathF.Min(b.X + b.Width, other.X + other.Width);
                            float iy2 = MathF.Min(b.Y + b.Height, other.Y + other.Height);
                            if (ix2 <= ix1 || iy2 <= iy1)
                            {
                                continue;
                            }
                            float inter = (ix2 - ix1) * (iy2 - iy1);
                            float areaA = b.Width * b.Height;
                            float areaB = other.Width * other.Height;
                            float union = areaA + areaB - inter;
                            if (union > 0.0f && inter / union >= 0.7f)
                            {
                                isDup = true;
                                break;
                            }
                        }
                    }
                }
            }
            if (!isDup)
            {
                if (!seen.TryGetValue(key, out Dictionary<(int, int), List<OxRect>>? g))
                {
                    g = new Dictionary<(int, int), List<OxRect>>();
                    seen[key] = g;
                }
                if (!g.TryGetValue((cx, cy), out List<OxRect>? cellBoxes))
                {
                    cellBoxes = new List<OxRect>();
                    g[(cx, cy)] = cellBoxes;
                }
                cellBoxes.Add(b);
                kept.Add(span);
            }
        }
        Spans = kept;
    }

    /// <summary>
    /// Mark spans whose RTL glyphs were drawn right-to-left (text.rs:4530): the producer
    /// stored the text in LOGICAL order and positioned each glyph at decreasing x
    /// (§14.8.2.3.3 method 1), so such spans must NOT be character-reversed downstream.
    /// </summary>
    /// <remarks>
    /// MUST run on raw stream order, before <see cref="SortSpansByReadingOrder"/> erases
    /// the draw direction. Draw direction is the only signal separating logical-stored
    /// from visual-stored RTL when both use base forms with no presentation forms and no
    /// /ReversedChars — indistinguishable otherwise, yet needing opposite treatment.
    /// </remarks>
    internal void DetectRtlDrawDirection()
    {
        static bool IsRtlSpan(OxTextSpan s)
        {
            bool rtl = false;
            foreach (Rune c in s.Text.EnumerateRunes())
            {
                if (c.IsAscii && char.IsAsciiLetter((char)c.Value))
                {
                    return false;
                }
                if (ScriptSignals.IsRtlText(c.Value))
                {
                    rtl = true;
                }
            }
            return rtl;
        }

        int n = Spans.Count;
        // Index of the previous RTL span in stream order; a pure-whitespace span between
        // two RTL glyphs is a word break and does not break the run.
        int? prev = null;
        for (int i = 0; i < n; i++)
        {
            if (AllRunes(Spans[i].Text, Rune.IsWhiteSpace))
            {
                continue;
            }
            if (!IsRtlSpan(Spans[i]))
            {
                prev = null;
                continue;
            }
            if (prev is int p)
            {
                bool sameLine = MathF.Abs(Spans[i].Bbox.Y - Spans[p].Bbox.Y)
                    < MathF.Max(Spans[p].FontSize, 1.0f) * 0.6f;
                // The incoming glyph sits to the LEFT of the previous one on the same
                // baseline => right-to-left placement => logical storage.
                if (sameLine && Spans[i].Bbox.X < Spans[p].Bbox.X - 0.5f)
                {
                    Spans[i].RtlDrawLogical = true;
                    Spans[p].RtlDrawLogical = true;
                }
            }
            prev = i;
        }
    }

    /// <summary>
    /// Per-line bimodal word-gap thresholds for the narrow-space rescue (text.rs:4589).
    /// </summary>
    /// <remarks>
    /// The fixed intra-word kerning guard in the space decision (0.75x the space-glyph
    /// advance) suppresses genuine but narrow word gaps on condensed/tracked lines — a
    /// bold heading or running footer typeset with no space glyph, whose inter-word gaps
    /// are ~0.18em, just under the guard. No fixed magnitude separates a 0.18em word gap
    /// from ~0.15em intra-word kerning, but within one line the intra-word gaps cluster
    /// near zero while inter-word gaps form a distinct larger cluster: a bimodal split
    /// pins the boundary regardless of absolute magnitude. Unimodal or too-short lines
    /// get null and keep the default guard, and the merge loop uses a threshold only to
    /// RESCUE a suppressed gap, never to remove a space.
    /// </remarks>
    internal static List<float?> BimodalLineGapThresholds(IReadOnlyList<OxTextSpan> spans)
    {
        int n = spans.Count;
        var outv = new List<float?>(n);
        for (int k = 0; k < n; k++)
        {
            outv.Add(null);
        }
        int i = 0;
        while (i < n)
        {
            int j = i;
            while (j + 1 < n && MathF.Abs(spans[j].Bbox.Y - spans[j + 1].Bbox.Y) < 1.0f)
            {
                j += 1;
            }
            if (j > i)
            {
                float fs = 0.0f;
                for (int k = i; k <= j; k++)
                {
                    fs = MathF.Max(fs, spans[k].FontSize);
                }
                fs = MathF.Max(fs, 1.0f);
                // ALL consecutive gaps — intra-word gaps are near-zero or slightly
                // negative, so they must be kept, not filtered — but ONLY between glyphs
                // sharing a baseline. A super/subscript sits at a ~0.15em baseline shift
                // and its horizontal gap to the base has the same ~0.10em magnitude as a
                // condensed footer's word gap; including it would let the rescue split a
                // math subscript from its variable, which advance-aware extractors do not.
                var gaps = new List<float>();
                for (int k = i; k < j; k++)
                {
                    if (MathF.Abs(spans[k].Bbox.Y - spans[k + 1].Bbox.Y) < fs * 0.04f)
                    {
                        gaps.Add(spans[k + 1].Bbox.X - (spans[k].Bbox.X + spans[k].Bbox.Width));
                    }
                }
                if (BimodalGapSplit(gaps, fs) is float split)
                {
                    for (int k = i; k <= j; k++)
                    {
                        outv[k] = split;
                    }
                }
            }
            i = j + 1;
        }
        return outv;
    }

    /// <summary>
    /// Threshold separating the intra-word from the inter-word cluster of one baseline
    /// run's gaps when the distribution is clearly bimodal, else null (text.rs:4635).
    /// <paramref name="fs"/> is the run's font size; all bounds are em fractions so
    /// headings and body text calibrate independently.
    /// </summary>
    internal static float? BimodalGapSplit(IReadOnlyList<float> gaps, float fs)
    {
        if (gaps.Count < 3)
        {
            return null;
        }
        var sorted = new List<float>(gaps);
        // partial_cmp().unwrap_or(Equal): NaN compares equal to everything rather than
        // sorting to one end.
        OxSpanCompare.SortStable(sorted, (a, b) => a < b ? -1 : a > b ? 1 : 0);
        // Return the LOWEST cluster border, not the widest jump: walking up from the
        // bottom, the first jump that leaves the intra-word cluster for a real word gap.
        // A qualifying border needs an intra-word-sized low side (< 0.10em — kerning,
        // tight side bearings or overlap), a high side that is a real if narrow word gap
        // (>= 0.09em; an explicit positive advance IS a word-boundary signal, §9.4.4),
        // and real separation between them (>= 0.08em) rather than a smooth spread.
        // Taking the lowest such border splits at every level above intra-word on a
        // multi-level condensed line, matching pdfminer/poppler. A single-word line (all
        // gaps low) yields no border and returns null. Callers feed only same-baseline
        // gaps, so a math subscript gap of the same magnitude never enters here.
        for (int w = 0; w + 1 < sorted.Count; w++)
        {
            float lo = sorted[w], hi = sorted[w + 1];
            if (lo < fs * 0.10f && hi >= fs * 0.09f && (hi - lo) >= fs * 0.08f)
            {
                return (lo + hi) * 0.5f;
            }
        }
        return null;
    }

    /// <summary>
    /// Merge adjacent text spans on the same line to reconstruct complete words
    /// (text.rs:4669).
    /// </summary>
    /// <remarks>
    /// PDF content streams break words across Tj operators for kerning, which fragments
    /// words ("Intr oduction"). Spans on the same line (Y within 1pt) and close enough
    /// horizontally are rejoined, with the space decision deciding whether a separator
    /// belongs at the seam.
    /// </remarks>
    internal void MergeAdjacentSpans()
    {
        if (Spans.Count == 0)
        {
            return;
        }

        List<OxTextSpan> spans = Spans;
        int oldLen = spans.Count;
        // Geometry of every drawn (non-whitespace) run, captured before the fold consumes
        // the list. The decimal-merge branch needs it: a separator glyph between two digit
        // runs — the comma of a subscript index pair like P_{1,0} — is often drawn
        // elsewhere in the content stream, so the fold sees the digits as adjacent and
        // only a geometric test over ALL runs can spot the ink sitting in the gap.
        var inkBoxes = new List<OxRect>();
        foreach (OxTextSpan s in spans)
        {
            if (s.Text.Trim().Length != 0)
            {
                inkBoxes.Add(s.Bbox);
            }
        }
        // Per-line bimodal word-gap thresholds, indexed to `spans`, used below to rescue
        // a narrow word gap the fixed kerning guard suppressed.
        List<float?> lineThresholds = BimodalLineGapThresholds(spans);
        var merged = new List<OxTextSpan>(oldLen);
        OxTextSpan? currentSpan = null;

        for (int spanIdx = 0; spanIdx < spans.Count; spanIdx++)
        {
            OxTextSpan span = spans[spanIdx];
            if (currentSpan is null)
            {
                currentSpan = span;
                continue;
            }

            OxTextSpan current = currentSpan;

            // Spans drawn under different writing modes must never merge, even when their
            // baselines coincide: a wmode=0 span advances along x, a wmode=1 span along y,
            // and every merge rule below (same word, same line, gap small enough to glue)
            // assumes one advance axis. Without this gate, an F1-horizontal + F2-vertical
            // pair at one Td glues into a single horizontal span and clobbers the vertical
            // glyph's wmode.
            bool wmodeCompatible = current.Wmode == span.Wmode;
            // +/-90-degree rotated runs (text-matrix rotation, not wmode) advance along Y
            // with their line axis on X, so the portrait same-line test reads
            // PERPENDICULAR geometry for them: two runs from adjacent rotated lines share
            // a baseline-Y and sit a word gap apart in X, which glued words from different
            // lines of a rotated table into one span. Rotated runs never merge here; the
            // rotated-frame reading order handles them downstream.
            static bool QuadrantVertical(float deg) =>
                MathF.Abs(deg - 90.0f) < 0.5f || MathF.Abs(deg + 90.0f) < 0.5f;
            bool rotationCompatible = !QuadrantVertical(current.RotationDegrees)
                && !QuadrantVertical(span.RotationDegrees);
            float yDiff = MathF.Abs(span.Bbox.Y - current.Bbox.Y);
            bool sameLine = yDiff < 1.0f && wmodeCompatible && rotationCompatible;

            float currentEndX = current.Bbox.X + current.Bbox.Width;
            float gap = span.Bbox.X - currentEndX;
            // Fallback-width correction: when the previous span's font has no explicit
            // /Widths array every glyph reports FontInfo's ~0.55em fallback, so for
            // proportional Latin fonts the span's bbox is systematically inflated,
            // `currentEndX` overshoots the rendered text and often swallows the real
            // inter-word gap entirely — turning a visible word boundary into a negative
            // gap that then glues the words together.
            //
            // `spaceGap` is used ONLY for the space-insertion decision. The raw `gap`
            // still drives the merge-vs-column decision, the decimal-merge heuristic and
            // every branch reasoning about actual layout, so fallback-width fonts merge
            // exactly as before and only the separator question sees the honest gap.
            bool reliableWidths = MergeContext?.HasExplicitWidths(current.FontName) ?? true;
            float spaceGap = OxTextHelpers.CorrectedSpaceGap(
                gap, reliableWidths, current.Bbox.Width, current.Text.Length == 0);

            // Column-boundary gap, font-size aware: the same 6pt gap is a gutter at 11pt
            // body text but ordinary kerning at a 36pt title, so 0.5em floors the
            // configured absolute threshold.
            float fontSizeRef = MathF.Max(current.FontSize, span.FontSize);
            float columnThreshold = MathF.Max(MergingConfig.ColumnBoundaryThresholdPt, fontSizeRef * 0.5f);
            bool largeGapIndicatesColumn = gap > columnThreshold;

            // A span carrying a split boundary came from CamelCase splitting ("the" +
            // "General"); those always merge WITH a space, never without.
            bool hasSplitBoundary = span.SplitBoundaryBefore;

            bool isSameFont = string.Equals(current.FontName, span.FontName, StringComparison.Ordinal)
                && MathF.Abs(current.FontSize - span.FontSize) < 0.01f
                && current.FontWeight == span.FontWeight
                && current.IsItalic == span.IsItalic;

            // Per §14.6, two adjacent Tj operators in different marked-content sequences
            // belong to different structure elements. Merging them would fuse their
            // identities (the merged span keeps current.mcid) and lose the boundary that
            // structure reading order, ActualText suppression and table-cell membership
            // rely on, so differing MCIDs — including one null against one set — stay apart.
            bool sameMcid = current.Mcid == span.Mcid;

            Rune? prevTailRune = LastRune(current.Text);
            Rune? currHeadRune = FirstRune(span.Text);
            // CJK ideographs satisfy is_alphabetic per Unicode, so a CJK<->Latin
            // transition in different fonts — the standard mixed-script layout — used to
            // trigger cross-font glue and concatenate "神鹰集团" + "Z" with no separator,
            // losing both tokens against pdftotext ground truth (which spaces every
            // CJK<->non-CJK boundary). Fullwidth ASCII and CJK Symbols/Punctuation are
            // deliberately excluded: those operator-style glyphs sit inline with adjacent
            // Latin/digits in CJK technical writing ("60000≤Q＜80000"), and treating them
            // as a CJK boundary would split the compound token.
            bool crossesCjkBoundary = prevTailRune is Rune p2 && currHeadRune is Rune c2
                && IsCjkChar(p2.Value) != IsCjkChar(c2.Value);

            // Cross-font word glue: same-baseline spans in different fonts/weights, tight
            // gap, both sides alphabetic, one side a single character — the drop-cap /
            // single-letter small-caps pattern, where per-letter emphasis runs would
            // otherwise corrupt proper nouns.
            //
            // Drop caps and single-letter emphasis sit TIGHT against their word (gap ~0,
            // often overlapping). A gap in word-space territory (>=~0.15em) across a font
            // change is a genuine token boundary — typically a word followed by a
            // single-letter math variable ("solution" -> "U") — and gluing those drops a
            // space poppler and PDFium keep. The 0.12em ceiling is the valley between
            // drop-cap kerning (~0) and a word space (>=~0.2em); it was 0.25em, itself a
            // full word space, until the advance fold made per-glyph advances accurate
            // enough that those ~0.24em gaps fell under the old ceiling and began gluing.
            bool crossFontWordGlue = !isSameFont
                && sameLine
                && gap > -1.0f
                && gap < fontSizeRef * 0.12f
                && current.Text.Length != 0
                && span.Text.Length != 0
                && !crossesCjkBoundary
                && prevTailRune is Rune pg && IsAlphabetic(pg)
                && currHeadRune is Rune cg && IsAlphabetic(cg)
                && (RuneCount(current.Text) == 1 || RuneCount(span.Text) == 1);

            // Small-caps / drop-cap glue: same base font and same weight/italic flags but
            // a different font size, adjacent on one baseline, both alphabetic. PDFs
            // simulate small caps by rendering the initial at body size and the remainder
            // reduced in the same font, emitted as separate Tj runs with zero gap; the
            // strict is_same_font gate rejects that on the size mismatch, and the
            // single-character glue above cannot help when both runs are multi-character.
            // §9.3.1 treats font size as a graphics-state parameter that may change
            // between Tj operators, and nothing in §9.4 makes such a change a word break.
            bool smallCapsGlue = !isSameFont
                && string.Equals(current.FontName, span.FontName, StringComparison.Ordinal)
                && current.FontWeight == span.FontWeight
                && current.IsItalic == span.IsItalic
                && sameLine
                && MathF.Abs(gap) < 1.0f
                && current.Text.Length != 0
                && span.Text.Length != 0
                && !crossesCjkBoundary
                && prevTailRune is Rune ps && IsAlphabetic(ps)
                && currHeadRune is Rune cs && IsAlphabetic(cs);

            // Same-font spans merge aggressively to reconstruct words; different fonts
            // merge only when effectively overlapping, to absorb kerning/rounding noise.
            float mergeThresholdPt = isSameFont ? MathF.Max(columnThreshold, 3.0f) : 0.5f;

            bool shouldMerge = (sameLine
                    && isSameFont
                    && sameMcid
                    && gap >= MergingConfig.SevereOverlapThresholdPt
                    && gap < mergeThresholdPt
                    && !largeGapIndicatesColumn)
                || (sameLine && hasSplitBoundary && sameMcid)
                || (crossFontWordGlue && sameMcid)
                || (smallCapsGlue && sameMcid);

            // DECIMAL VALUE MERGE: forms place the integer and decimal parts of an amount
            // in separate fixed-width boxes — "123456" then "72" with a ~10pt gap. Both
            // sides pure digits, the second exactly 1-2 digits, same line, and a
            // column-boundary-sized gap.
            //
            // Without a minimum-gap floor this also matched tightly-packed digits from
            // CJK documents that emit each glyph as its own Tj — the year "2013" as four
            // Tj operators with sub-pixel gaps was mangled into "201.3". Real split-box
            // layouts always have a gap above ~half the font size; tight letter spacing is
            // below 0.1em. A separator glyph drawn INSIDE the gap proves the runs are
            // distinct tokens (the comma of an index pair like P_{1,0}, drawn out of
            // content-stream order): a genuine split-box amount has nothing between its
            // boxes, and the gap band alone cannot make that call.
            //
            // A genuine split-box amount prints integer and cents at the SAME size; a
            // digit run markedly smaller than its neighbour is super/subscript context —
            // the exponent of a scientific-notation value beside the next mantissa — and
            // fusing those fabricates a decimal.
            bool decimalSizesMatch;
            {
                float a = current.FontSize, b = span.FontSize;
                decimalSizesMatch = a > 0.0f && b > 0.0f
                    && MathF.Min(a, b) / MathF.Max(a, b) >= 0.85f;
            }
            // The gap needs a ceiling too. Subscript index pairs draw their two digits in
            // a ~7pt font spaced ~1.5-1.7x the font size apart; too loose a ceiling lets
            // the rule invent a decimal ("1" + "0" -> "1.0"). Genuine split-box amounts
            // cluster near ~0.8-1.0x the font size, so 1.3x separates them.
            float minDecimalGap = current.FontSize * 0.4f;
            float maxDecimalGap = current.FontSize * 1.3f;
            bool decimalMerge = sameLine
                && sameMcid
                && decimalSizesMatch
                && gap > minDecimalGap
                && gap < maxDecimalGap
                && current.Text.Length != 0
                && span.Text.Length != 0
                && AllRunes(current.Text, r => r.IsAscii && char.IsAsciiDigit((char)r.Value))
                && AllRunes(span.Text, r => r.IsAscii && char.IsAsciiDigit((char)r.Value))
                && Utf8Len(span.Text) >= 1 && Utf8Len(span.Text) <= 2
                && !OxTextHelpers.DecimalGapHasInk(inkBoxes, current.Bbox, span.Bbox);

            // Pre-merge shape for the positional char_widths maintenance below: the merged
            // text is `current + [separator] + span`, so each contribution's widths must
            // land at the same position its chars occupy. Captured before any branch
            // mutates current.Text / current.Bbox.
            int currentCharsBefore = RuneCount(current.Text);
            int spanCharCount = RuneCount(span.Text);

            if (decimalMerge)
            {
                current.Text = current.Text + "." + span.Text;
            }
            else if (crossFontWordGlue)
            {
                // Mid-word font/weight change: concatenate with no space and no space
                // heuristic — these are same-word character runs.
                current.Text += span.Text;
            }
            else if (shouldMerge)
            {
                // A whitespace-only or TJ-offset-space neighbour already carries the
                // separator; adding another would double-space the seam.
                bool nextIsWhitespaceOnly = AllRunes(span.Text, Rune.IsWhiteSpace);
                bool nextIsOffsetSemanticSpace = span.OffsetSemantic && nextIsWhitespaceOnly;

                if (nextIsWhitespaceOnly)
                {
                    current.Text += span.Text;
                }
                else
                {
                    bool tjOffsetTriggeredOverride = hasSplitBoundary;
                    OxSpaceDecision spaceDecision = ShouldInsertSpace(
                        current.Text,
                        span.Text,
                        spaceGap,
                        current.FontSize,
                        current.FontName,
                        SpanFonts,
                        tjOffsetTriggeredOverride,
                        MergingConfig,
                        current.Bbox,
                        span.Bbox,
                        current.FontSize,
                        span.FontSize);

                    // Narrow-word-gap rescue. The fixed intra-word kerning guard
                    // suppresses genuine word gaps on condensed/tracked lines with no
                    // space glyph (bold headings, running footers). When this line's own
                    // gap distribution is clearly bimodal and this gap sits in the
                    // inter-word cluster, honour the boundary. It only ever ADDS a space,
                    // and only when the suppression came from the purely geometric
                    // kerning guard — never the semantic no-space rules (complex script,
                    // CJK, ligature), else Bengali/Devanagari syllables shatter. RTL is
                    // excluded too: the /ReversedChars guard below owns that decision.
                    //
                    // Two further guards keep it off dense math, whose sub/superscript
                    // gaps have the same ~0.10em magnitude as a condensed footer's word
                    // gap: never rescue across a baseline shift, and never rescue when
                    // another glyph's ink sits inside the gap — a subscript drawn between
                    // a variable and the next symbol inflates the gap though both share a
                    // baseline, and that ink marks it as not-a-word-boundary. A genuine
                    // footer word gap is empty.
                    float sameBaselineTol = MathF.Max(MathF.Max(current.FontSize, span.FontSize), 1.0f) * 0.04f;
                    bool sameBaseline = MathF.Abs(current.Bbox.Y - span.Bbox.Y) < sameBaselineTol;
                    if (spaceDecision.Source == OxSpaceSource.IntraWordKerning
                        && !(MergeContext?.SawReversedChars ?? false)
                        && sameBaseline
                        && !OxTextHelpers.GapHasInterveningGlyph(inkBoxes, current.Bbox, span.Bbox))
                    {
                        // Split only when the PER-LINE bimodal threshold fires. A uniform
                        // per-pair advance floor (how pdfminer/poppler decide) would catch
                        // a few more footer instances, but a fixed magnitude cannot tell a
                        // 0.10em condensed word gap from 0.10em loose intra-word tracking,
                        // so it also splits real words on loosely-set or scanned lines
                        // ("walking" -> "wa lking"). The per-line bimodal only fires when
                        // the intra-word cluster is genuinely tight, so it never
                        // over-splits, at the cost of the footers whose distribution is
                        // not cleanly bimodal.
                        float? thr = spanIdx < lineThresholds.Count ? lineThresholds[spanIdx] : null;
                        if (thr is float t && gap > t)
                        {
                            spaceDecision = OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.9f);
                        }
                    }

                    // ReversedChars Arabic word-shatter guard (§14.8.2.3.3). On a page
                    // that draws RTL glyphs individually under /ReversedChars, real word
                    // boundaries carry explicit space glyphs (preserved above as
                    // whitespace-only spans), so a GEOMETRIC space between two cursively
                    // adjacent Arabic letters is a positioning artifact, not a word break.
                    // Only fires on ReversedChars pages, leaving ordinary
                    // geometric-spaced Arabic producers alone.
                    if ((MergeContext?.SawReversedChars ?? false) && spaceDecision.InsertSpace)
                    {
                        bool prevAr = LastRune(current.Text) is Rune pr && ScriptSignals.IsArabicLetter(pr.Value);
                        bool nextAr = FirstRune(span.Text) is Rune nr && ScriptSignals.IsArabicLetter(nr.Value);
                        if (prevAr && nextAr)
                        {
                            spaceDecision = spaceDecision with { InsertSpace = false };
                        }
                    }

                    if (spaceDecision.InsertSpace)
                    {
                        if (nextIsOffsetSemanticSpace)
                        {
                            // Already a TJ-offset space: inserting another would double it.
                            current.Text += span.Text;
                        }
                        else
                        {
                            bool wouldCreateDoubleSpace =
                                current.Text.EndsWith(' ') && span.Text.StartsWith(' ');

                            if (wouldCreateDoubleSpace)
                            {
                                current.Text += span.Text;
                            }
                            else
                            {
                                current.Text = current.Text + " " + span.Text;
                            }
                        }
                    }
                    else
                    {
                        current.Text += span.Text;
                    }
                }
            }

            if (decimalMerge || shouldMerge || crossFontWordGlue)
            {
                // A merged span is logical-draw RTL if any of its runs was drawn
                // right-to-left (see DetectRtlDrawDirection).
                current.RtlDrawLogical |= span.RtlDrawLogical;
                float newWidth = (span.Bbox.X + span.Bbox.Width) - current.Bbox.X;
                float newHeight = MathF.Max(current.Bbox.Height, span.Bbox.Height);
                current.Bbox = WithExtents(current.Bbox, newWidth, newHeight);

                // Keep char_widths in POSITIONAL lockstep with the merged text. The
                // downstream width-based splitters fire when char_widths is shorter than
                // the char count, and to_chars pairs each glyph's char_x_offsets origin
                // with char_widths[i] — so every width entry must sit at the same index as
                // its char, not merely make the lengths match. A trailing resize after a
                // width-less contribution (a TJ-offset space span merging FIRST) shifted
                // every later width one slot left, pairing each glyph with its neighbour's
                // advance and opening phantom intra-word gaps the word-gap clusterer split
                // on ("module" -> "m|odu|le"). Each contribution is normalized at its own
                // position instead.
                float pad = current.FontSize > 0.0f ? current.FontSize * 0.25f : 1.0f;
                // 1. Normalize the accumulated widths to the pre-merge char count. A
                //    width-less contribution is split uniformly across its bbox (matching
                //    to_chars' uniform fallback); a partially populated one keeps the
                //    legacy tail pad.
                if (current.CharWidths.Count == 0 && currentCharsBefore > 0)
                {
                    float oldWidth = MathF.Max(currentEndX - current.Bbox.X, 0.0f);
                    Resize(current.CharWidths, currentCharsBefore, oldWidth / currentCharsBefore);
                }
                else if (current.CharWidths.Count != currentCharsBefore)
                {
                    Resize(current.CharWidths, currentCharsBefore, pad);
                }
                // 2. An inserted separator ('.' or ' ') gets the real geometric gap it
                //    stands in for, at its own position, with the legacy pad as the
                //    fallback for overlapping or degenerate layouts.
                int mergedCharCount = RuneCount(current.Text);
                int separatorCount = Math.Max(mergedCharCount - (currentCharsBefore + spanCharCount), 0);
                if (separatorCount > 0)
                {
                    float sepGap = span.Bbox.X - currentEndX;
                    float sepWidth = float.IsFinite(sepGap) && sepGap > 0.0f
                        ? sepGap / separatorCount
                        : pad;
                    Resize(current.CharWidths, currentCharsBefore + separatorCount, sepWidth);
                }
                // 3. Append the merged-in span's widths, normalized the same way.
                if (span.CharWidths.Count == 0 && spanCharCount > 0)
                {
                    float perChar = MathF.Max(span.Bbox.Width / spanCharCount, 0.0f);
                    for (int k = 0; k < spanCharCount; k++)
                    {
                        current.CharWidths.Add(perChar);
                    }
                }
                else
                {
                    current.CharWidths.AddRange(span.CharWidths);
                    Resize(current.CharWidths, mergedCharCount, pad);
                }

                // Preserve the merged-in glyph's TRUE origin for scrambled-RTL producers
                // (/ReversedChars plus per-glyph /ActualText Arabic, §14.8.2.3.3 /
                // §14.9.4). Those reposition glyphs out of advance order, so appended raw
                // advances collapse the merged span to advance flow and to_chars loses
                // each glyph's true x — the RTL visual-order sort then misplaces
                // zero-width marks (القهوة -> قالهوة). Once char_widths are in lockstep
                // with the (possibly space-inserted) text, stretch the advance leading
                // into the merged-in span's first glyph so to_chars reconstructs it at
                // span.bbox.x. Gated to Arabic so Latin/CJK output stays byte-identical.
                int nWidths = current.CharWidths.Count;
                int spanChars = RuneCount(span.Text);
                if (nWidths >= 2
                    && spanChars >= 1
                    && spanChars < nWidths
                    && (TouchesArabic(current.Text) || TouchesArabic(span.Text)))
                {
                    int firstIdx = nWidths - spanChars;
                    float prefix = 0.0f;
                    for (int k = 0; k < firstIdx; k++)
                    {
                        prefix += current.CharWidths[k];
                    }
                    float want = span.Bbox.X - current.Bbox.X;
                    float adjust = want - prefix;
                    if (MathF.Abs(adjust) > 0.01f)
                    {
                        current.CharWidths[firstIdx - 1] += adjust;
                    }
                }

                // After a cross-font glue the longer run's font metadata wins: the
                // single-letter side was typographic decoration, not semantic emphasis.
                if (crossFontWordGlue)
                {
                    int glueSpanChars = RuneCount(span.Text);
                    int glueCurrentCharsBefore = RuneCount(current.Text) - glueSpanChars;
                    if (glueSpanChars > glueCurrentCharsBefore)
                    {
                        current.FontName = span.FontName;
                        current.FontWeight = span.FontWeight;
                        current.IsItalic = span.IsItalic;
                    }
                }

                currentSpan = current;
            }
            else
            {
                merged.Add(current);
                currentSpan = span;
            }
        }

        if (currentSpan is OxTextSpan last)
        {
            merged.Add(last);
        }

        Spans = merged;
    }

    /// <summary>
    /// Sort extracted characters by reading order, top-to-bottom then left-to-right
    /// (text.rs:5332). PDF content streams are ordered for rendering efficiency, not for
    /// reading.
    /// </summary>
    internal void SortByReadingOrder() =>
        OxSpanCompare.SortStable(Chars, (a, b) =>
        {
            // Non-finite coordinates sort to the end rather than poisoning the order.
            if (!float.IsFinite(a.Bbox.Y))
            {
                return float.IsFinite(b.Bbox.Y) ? 1 : 0;
            }
            if (!float.IsFinite(b.Bbox.Y))
            {
                return -1;
            }

            // Y is rounded to an integer band so the comparison stays transitive.
            int aY = OxSpanCompare.RoundToI32(a.Bbox.Y);
            int bY = OxSpanCompare.RoundToI32(b.Bbox.Y);
            int c = bY.CompareTo(aY);
            if (c != 0)
            {
                return c;
            }

            if (!float.IsFinite(a.Bbox.X))
            {
                return float.IsFinite(b.Bbox.X) ? 1 : 0;
            }
            if (!float.IsFinite(b.Bbox.X))
            {
                return -1;
            }
            return a.Bbox.X < b.Bbox.X ? -1 : a.Bbox.X > b.Bbox.X ? 1 : 0;
        });

    /// <summary>
    /// Split fused words created by authoring defects (text.rs:5396) — "theGeneral" for
    /// "the General".
    /// </summary>
    /// <remarks>
    /// Per §9.4.4 text strings are as long as possible and spaces are positioning
    /// artifacts, so a producer may emit several words as one TJ string with no spacing.
    /// Only the CamelCase strategy is ported; the dictionary-based Viterbi fallback
    /// upstream lives behind a feature that is not part of this pipeline.
    /// </remarks>
    internal void SplitFusedWords()
    {
        var splitSpans = new List<OxTextSpan>();

        foreach (OxTextSpan span in Spans)
        {
            List<string> parts = SplitOnCamelcase(span.Text);

            if (parts.Count == 1)
            {
                splitSpans.Add(CloneSpan(span));
            }
            else
            {
                // Proportional bboxes by UTF-8 byte length, as upstream measures them.
                float totalChars = Utf8Len(span.Text);
                int charPos = 0;

                for (int i = 0; i < parts.Count; i++)
                {
                    string part = parts[i];
                    float partLen = Utf8Len(part);
                    float partRatio = partLen / totalChars;

                    float newWidth = span.Bbox.Width * partRatio;
                    float newX = span.Bbox.X + span.Bbox.Width * (charPos / totalChars);

                    OxTextSpan newSpan = CloneSpan(span);
                    newSpan.Text = part;
                    newSpan.Bbox = WithXWidth(span.Bbox, newX, newWidth);

                    // Every part but the first carries a split boundary, which keeps the
                    // merge pass from simply re-fusing them.
                    if (i > 0)
                    {
                        newSpan.SplitBoundaryBefore = true;
                    }

                    splitSpans.Add(newSpan);
                    charPos += Utf8Len(part);
                }
            }
        }

        Spans = splitSpans;
    }

    /// <summary>
    /// Split text at lowercase-to-uppercase transitions (text.rs:5475): "theGeneral" to
    /// ["the", "General"].
    /// </summary>
    internal List<string> SplitOnCamelcase(string text)
    {
        var parts = new List<string>();
        var currentPart = new StringBuilder();
        bool prevIsLower = false;

        foreach (Rune ch in text.EnumerateRunes())
        {
            if (prevIsLower && Rune.IsUpper(ch))
            {
                if (currentPart.Length != 0)
                {
                    parts.Add(currentPart.ToString());
                    currentPart.Clear();
                }
                currentPart.Append(ch.ToString());
                prevIsLower = false;
            }
            else
            {
                currentPart.Append(ch.ToString());
                prevIsLower = Rune.IsLower(ch);
            }
        }

        if (currentPart.Length != 0)
        {
            parts.Add(currentPart.ToString());
        }

        return parts.Count > 1 ? parts : new List<string> { text };
    }

    // ------------------------------------------------------------------
    // Small helpers that stand in for Rust idioms with no C# equivalent.
    // ------------------------------------------------------------------

    // `OxTextSpan.Clone` is a memberwise copy, so the per-glyph vectors stay shared with
    // the original; Rust's `#[derive(Clone)]` deep-copies them. The merge pass mutates
    // `char_widths` in place, so split parts sharing one list would corrupt each other.
    private static OxTextSpan CloneSpan(OxTextSpan span)
    {
        OxTextSpan copy = span.Clone();
        copy.CharWidths = new List<float>(span.CharWidths);
        copy.CharXOffsets = new List<float>(span.CharXOffsets);
        return copy;
    }

    // Rust assigns bbox extents raw; OxRect's public constructor normalizes negative
    // extents by moving the origin, which would relocate the merged span. RTL logical
    // draw order genuinely produces a negative merged width, so those go through the
    // non-normalizing corner constructor and the common case keeps exact arithmetic.
    private static OxRect WithExtents(in OxRect r, float width, float height) =>
        width >= 0.0f && height >= 0.0f
            ? new OxRect(r.X, r.Y, width, height)
            : OxRect.FromPoints(r.X, r.Y, r.X + width, r.Y + height);

    private static OxRect WithY(in OxRect r, float y) =>
        r.Width >= 0.0f && r.Height >= 0.0f
            ? new OxRect(r.X, y, r.Width, r.Height)
            : OxRect.FromPoints(r.X, y, r.X + r.Width, y + r.Height);

    private static OxRect WithXWidth(in OxRect r, float x, float width) =>
        width >= 0.0f && r.Height >= 0.0f
            ? new OxRect(x, r.Y, width, r.Height)
            : OxRect.FromPoints(x, r.Y, x + width, r.Y + r.Height);

    /// <summary>Rust's <c>as usize</c> on an f32: NaN and negatives collapse to 0, huge values saturate.</summary>
    private static int SaturatingUsize(float v)
    {
        if (float.IsNaN(v) || v <= 0.0f) return 0;
        if (v >= int.MaxValue) return int.MaxValue;
        return (int)v;
    }

    /// <summary>Rust's <c>f32::floor() as i32</c>, saturating rather than wrapping.</summary>
    private static int FloorToI32(float v) => OxSpanCompare.SaturatingI32(MathF.Floor(v));

    private static int SaturatingAdd(int a, int b)
    {
        long r = (long)a + b;
        return r > int.MaxValue ? int.MaxValue : r < int.MinValue ? int.MinValue : (int)r;
    }

    private static int SaturatingSub(int a, int b)
    {
        long r = (long)a - b;
        return r > int.MaxValue ? int.MaxValue : r < int.MinValue ? int.MinValue : (int)r;
    }

    /// <summary>Rust's <c>slice::partition_point</c>: index of the first element failing the predicate.</summary>
    private static int PartitionPoint(float[] sorted, Func<float, bool> pred)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (pred(sorted[mid])) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Rust's <c>Vec::resize</c>: truncate, or grow padding with <paramref name="value"/>.</summary>
    private static void Resize(List<float> list, int newLen, float value)
    {
        if (list.Count > newLen)
        {
            list.RemoveRange(newLen, list.Count - newLen);
        }
        else
        {
            while (list.Count < newLen)
            {
                list.Add(value);
            }
        }
    }

    /// <summary>Rust's <c>str::len()</c> — UTF-8 bytes, which is what the ported thresholds count.</summary>
    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Rust's <c>chars().count()</c> — Unicode scalar values, not UTF-16 units.</summary>
    private static int RuneCount(string s)
    {
        int n = 0;
        foreach (Rune _ in s.EnumerateRunes())
        {
            n++;
        }
        return n;
    }

    private static Rune? FirstRune(string s)
    {
        foreach (Rune r in s.EnumerateRunes())
        {
            return r;
        }
        return null;
    }

    private static Rune? LastRune(string s)
    {
        Rune? last = null;
        foreach (Rune r in s.EnumerateRunes())
        {
            last = r;
        }
        return last;
    }

    private static bool AllRunes(string s, Func<Rune, bool> pred)
    {
        foreach (Rune r in s.EnumerateRunes())
        {
            if (!pred(r))
            {
                return false;
            }
        }
        return true;
    }

    // Rust's `char::is_alphabetic` is the Unicode Alphabetic property; .NET exposes the
    // letter categories plus Nl, which covers every case these glue rules see (the
    // Other_Alphabetic combining marks it misses are never a run's first or last glyph
    // in the drop-cap / small-caps patterns these gates target).
    private static bool IsAlphabetic(Rune r) =>
        Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.LetterNumber;

    private static bool IsCjkChar(int c) =>
        (c >= 0x3040 && c <= 0x309F) ||     // Hiragana
        (c >= 0x30A0 && c <= 0x30FF) ||     // Katakana
        (c >= 0x3400 && c <= 0x4DBF) ||     // CJK Ext A
        (c >= 0x4E00 && c <= 0x9FFF) ||     // CJK Unified
        (c >= 0xAC00 && c <= 0xD7AF) ||     // Hangul syllables
        (c >= 0x20000 && c <= 0x2A6DF) ||   // CJK Ext B
        (c >= 0xFF66 && c <= 0xFF9F);       // Halfwidth Katakana

    private static bool TouchesArabic(string t)
    {
        foreach (Rune r in t.EnumerateRunes())
        {
            int c = r.Value;
            if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) || (c >= 0x08A0 && c <= 0x08FF))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Rust's <c>to_ascii_lowercase</c>: non-ASCII is left exactly as it is.</summary>
    private static string AsciiLowercase(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c >= 'A' && c <= 'Z' ? (char)(c + 32) : c);
        }
        return sb.ToString();
    }
}
