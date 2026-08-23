// Ported from pdf_oxide `document.rs` — the `ReadingOrder` enum (l. 28-61), the
// ordering half of `extract_spans_filtered_with_reading_order` (l. 15710-15826),
// `order_spans_column_aware` (l. 15603-15617), `drop_offpage_spans` (l. 11612-11645),
// `order_rotated_blocks` (l. 11393-11425) and the tategaki / rotated-run gates of
// `postprocess_spans` (l. 11726-11794) — plus the strategy it delegates the
// column-aware order to, `pipeline/reading_order/xycut.rs` (`XYCutStrategy`, l. 1-2112).
//
// Geometry follows pdf_oxide's `Rect`: `Top` is the SMALLER Y (PDF user space has Y
// growing upward, ISO 32000-1 §8.3.2.3), so "higher on the page" means larger Y and
// every vertical comparison here reads descending.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Xberg.Internal.PdfOxide.Layout;

internal static class OxReadingOrder
{
    /// <summary>Reading-order mode for span extraction (`document::ReadingOrder`).</summary>
    internal enum Mode
    {
        /// <summary>Y-band descending (top of page first), then X ascending.</summary>
        TopToBottom,

        /// <summary>
        /// XY-cut projection-profile partitioning: each detected column is read fully
        /// top-to-bottom before the next. Newspapers, papers, multi-column layouts.
        /// </summary>
        ColumnAware,

        /// <summary>
        /// Logical-structure order from `/StructTreeRoot`. NOT PORTED — see
        /// <see cref="ApplyReadingOrder"/>; falls back to <see cref="ColumnAware"/>,
        /// which is also what the Rust does for an untagged or suspect tree.
        /// </summary>
        Structure,
    }

    // ── XYCutStrategy::default() ────────────────────────────────────────────────
    private const int MinSpansForSplit = 5;
    private const float ValleyThreshold = 0.3f;

    // 15pt, twice measured and twice reverted lower. A narrower valley catches the
    // tight two-column prose gutter of issue #7, but it also splits the ~12pt
    // inter-cell gaps of a real data table and reorders its digits — the same XY-cut
    // machinery orders prose columns and table cells, so sensitivity cannot be raised
    // without a prose-vs-table classifier gating it (which is what RegionKind is).
    private const float MinValleyWidth = 15.0f;
    private const bool PreferHorizontal = true;

    /// <summary>
    /// A degenerate CTM can hand us a bbox spanning astronomically many points; the cap
    /// turns that into a skipped split instead of a multi-terabyte density allocation.
    /// 100 000 bins is ~33x an A0 page, so no real layout reaches it.
    /// </summary>
    private const int MaxProjectionSize = 100_000;

    /// <summary>
    /// Only the singleton-peel pathology (many distinct-Y header/footer strips) recurses
    /// deeply enough to hit this; real layouts nest a handful of splits.
    /// </summary>
    private const uint MaxPartitionDepth = 64;

    /// <summary>Coarse region classification gating the tight-gutter column cut.</summary>
    private enum RegionKind
    {
        /// <summary>Tall stack of wide lines, or of half-column lines with real content.</summary>
        Prose,

        /// <summary>Short cells in a grid (mean chars per line &lt; 8). A tight cut here
        /// corrupts cell ordering.</summary>
        Table,

        /// <summary>Too few lines, mixed shapes, decorative bands — no tight cut.</summary>
        Mixed,
    }

    // ── Entry points ────────────────────────────────────────────────────────────

    /// <summary>
    /// The ordering half of `extract_spans_filtered_with_reading_order`: drop off-page
    /// spans, then order by <paramref name="mode"/>. Returns a new list; the input is
    /// left alone.
    /// </summary>
    /// <param name="mediaBox">
    /// The page MediaBox as `get_page_media_box` returns it — absolute corners
    /// (llx, lly, urx, ury) per ISO 32000-1 §7.7.3.3, NOT (x, y, width, height).
    /// Null skips the off-page filter, matching the Rust's `if let Ok(..)`.
    /// </param>
    /// <remarks>
    /// <see cref="Mode.Structure"/> is stubbed to <see cref="Mode.ColumnAware"/>: the
    /// Rust reorders table cells in place by structure-tree rank, which needs the
    /// struct-tree traversal and per-MCID `in_table` flags this port does not have yet.
    /// Its geometric baseline IS ColumnAware, so the fallback is the Rust's own
    /// behaviour for an untagged or `/Suspects true` document.
    /// </remarks>
    public static List<OxTextSpan> ApplyReadingOrder(
        IReadOnlyList<OxTextSpan> spans,
        Mode mode,
        (float Llx, float Lly, float Urx, float Ury)? mediaBox)
    {
        var result = new List<OxTextSpan>(spans);

        // A document reusing one big Form XObject across pages relies on the `W n`
        // clip to hide the off-page portion, which the raw extractor does not honour.
        // `extract_spans` filters via `postprocess_spans`; the reading-order path must
        // too, or it emits every page's worth of spans (measured: a stats report
        // emitted a chart's whole hidden data table, ~5x the visible label count).
        if (mediaBox is { } mb) DropOffpageSpans(result, mb.Llx, mb.Lly, mb.Urx, mb.Ury);

        if (mode == Mode.TopToBottom)
        {
            OxSpanCompare.SortSpansRowAware(result);
            return result;
        }

        // ColumnAware, and Structure until the structure-tree pass lands.
        return OrderSpansColumnAware(result);
    }

    /// <summary>
    /// Drop spans whose bbox lies ENTIRELY outside the page's MediaBox
    /// (`drop_offpage_spans`). Spans that even partially overlap are kept, so bleed and
    /// trim-mark content is never lost.
    /// </summary>
    public static void DropOffpageSpans(List<OxTextSpan> spans, float llx, float lly, float urx, float ury)
    {
        const float edgeTolerancePt = 2.0f;

        // Some producers write the MediaBox with swapped corners (e.g. `[0 792 612 0]`).
        // Taking min/max makes the bounds right either way — without it a swapped box
        // inverts the test and drops the whole page's legitimate text.
        float left = MathF.Min(llx, urx) - edgeTolerancePt;
        float right = MathF.Max(llx, urx) + edgeTolerancePt;
        float bottom = MathF.Min(lly, ury) - edgeTolerancePt;
        float top = MathF.Max(lly, ury) + edgeTolerancePt;

        spans.RemoveAll(span =>
        {
            float sx1 = span.Bbox.X;
            float sx2 = span.Bbox.X + span.Bbox.Width;
            float sy1 = span.Bbox.Y;
            float sy2 = span.Bbox.Y + span.Bbox.Height;
            return !(sx2 > left && sx1 < right && sy2 > bottom && sy1 < top);
        });
    }

    /// <summary>
    /// Geometric column-aware ordering (`order_spans_column_aware` →
    /// `XYCutStrategy::apply`): recursive XY-cut partitioning, emitting each block's
    /// spans in row order, blocks in partition order.
    /// </summary>
    public static List<OxTextSpan> OrderSpansColumnAware(IReadOnlyList<OxTextSpan> spans)
    {
        var all = spans as List<OxTextSpan> ?? spans.ToList();
        if (all.Count == 0) return new List<OxTextSpan>();

        // Multi-line heading runs are routed through synthetic-span space so the
        // splitter treats a wrapped heading as one atomic block. With no headings the
        // index-only path avoids the substitution entirely.
        var headingRuns = FindHeadingRuns(all);
        List<List<int>> indexGroups;
        if (headingRuns.Count == 0)
        {
            indexGroups = PartitionIndexed(all, Enumerable.Range(0, all.Count).ToList());
        }
        else
        {
            var (synthetic, origins) = SynthesizeForPartition(all, headingRuns);
            var synthGroups = PartitionIndexed(synthetic, Enumerable.Range(0, synthetic.Count).ToList());
            indexGroups = new List<List<int>>(synthGroups.Count);
            foreach (var group in synthGroups)
            {
                var expanded = new List<int>(group.Count);
                foreach (int si in group) expanded.AddRange(origins[si]);
                indexGroups.Add(expanded);
            }
        }

        var ordered = new List<OxTextSpan>(all.Count);
        var taken = new bool[all.Count];
        foreach (var group in indexGroups)
        {
            foreach (int i in group)
            {
                if (taken[i]) continue;
                taken[i] = true;
                ordered.Add(all[i]);
            }
        }
        return ordered;
    }

    /// <summary>
    /// Lift runs drawn with a rotated text matrix out of the horizontal flow and
    /// re-append them as their own blocks (the per-span rotation firewall of
    /// `postprocess_spans`).
    /// </summary>
    /// <remarks>
    /// Rotated runs — a margin stamp, axis labels, sideways table headers — break the
    /// axis-aligned assumptions of the row-band and XY-cut sorts, so interleaving them
    /// with the flow scrambles reading order. The lift is stable, so the horizontal
    /// body keeps the exact order the caller established, and the whole step is a no-op
    /// on a page with no rotated spans.
    /// </remarks>
    public static void ApplyRotationFirewall(List<OxTextSpan> spans)
    {
        if (!spans.Any(s => s.RotationDegrees != 0.0f)) return;

        var rotated = spans.Where(s => s.RotationDegrees != 0.0f).ToList();
        spans.RemoveAll(s => s.RotationDegrees != 0.0f);
        spans.AddRange(OrderRotatedBlocks(rotated));
    }

    /// <summary>
    /// Order the segregated rotated runs (`order_rotated_blocks`): grouped by rotation
    /// in first-seen order, and within a group read top-to-bottom / left-to-right in an
    /// upright frame obtained by rotating each origin back by -rotation.
    /// </summary>
    public static List<OxTextSpan> OrderRotatedBlocks(IReadOnlyList<OxTextSpan> spans)
    {
        var groups = new List<(float Deg, List<OxTextSpan> Spans)>();
        foreach (var s in spans)
        {
            float key = s.RotationDegrees;
            // 0.5° tolerance: quadrant-snapped rotations group, free-angle skew does not.
            int g = groups.FindIndex(t => MathF.Abs(t.Deg - key) < 0.5f);
            if (g >= 0) groups[g].Spans.Add(s);
            else groups.Add((key, new List<OxTextSpan> { s }));
        }

        var result = new List<OxTextSpan>(spans.Count);
        foreach (var (deg, group) in groups)
        {
            float rad = -deg * MathF.PI / 180.0f;
            float sin = MathF.Sin(rad), cos = MathF.Cos(rad);
            OxSpanCompare.SortStable(group, (a, b) =>
            {
                float ax = a.Bbox.X * cos - a.Bbox.Y * sin;
                float ay = a.Bbox.X * sin + a.Bbox.Y * cos;
                float bx = b.Bbox.X * cos - b.Bbox.Y * sin;
                float by = b.Bbox.X * sin + b.Bbox.Y * cos;
                return OxSpanCompare.RowAwareSpanCmp(ay, ax, by, bx);
            });
            result.AddRange(group);
        }
        return result;
    }

    /// <summary>
    /// The tategaki gate of `postprocess_spans`: a page whose spans were mostly emitted
    /// under WMode 1 needs <see cref="OxSpanCompare.SortVerticalTategaki"/> instead of a
    /// row-aware or XY-cut sort, which assume horizontal flow and scramble vertical text.
    /// </summary>
    /// <remarks>
    /// WMode comes from the PDF's own `/WMode` (font `/Encoding` ending in -V, or the
    /// CMap), so the vote is authoritative rather than heuristic.
    /// </remarks>
    public static bool IsTategakiPage(IReadOnlyList<OxTextSpan> spans)
    {
        if (spans.Count == 0) return false;
        int vertical = spans.Count(s => s.Wmode == 1);
        return vertical * 2 >= spans.Count;
    }

    // ── Heading runs (atomic blocks the splitter may not cut through) ────────────

    private sealed class HeadingRun
    {
        public List<int> SpanIndices = new();
        public OxRect CombinedBbox;
    }

    private static OxRect UnionBboxes(IReadOnlyList<OxTextSpan> spans, List<int> indices)
    {
        float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
        foreach (int i in indices)
        {
            var b = spans[i].Bbox;
            xMin = MathF.Min(xMin, b.Left);
            xMax = MathF.Max(xMax, b.Right);
            yMin = MathF.Min(yMin, b.Top);
            yMax = MathF.Max(yMax, b.Bottom);
        }
        if (xMin == float.MaxValue) return default;
        return OxRect.FromPoints(xMin, yMin, xMax, yMax);
    }

    private static bool IsBold(OxTextSpan s) => (int)s.FontWeight >= 600;

    /// <summary>
    /// Detect contiguous bold/large-font runs spanning two or more lines with matching
    /// X-extent, i.e. wrapped subsection headings.
    /// </summary>
    /// <remarks>
    /// Without locking these, a wrapped heading whose tail lines Y-overlap dense
    /// adjacent-column content gets bucketed across columns — line 1 glued to the body
    /// paragraph, the rest orphaned — and a markdown converter then promotes the orphan
    /// tail to a phantom heading in the wrong place.
    /// </remarks>
    private static List<HeadingRun> FindHeadingRuns(IReadOnlyList<OxTextSpan> spans)
    {
        var result = new List<HeadingRun>();
        if (spans.Count < 2) return result;

        // Median body size from NON-bold spans: bold spans usually sit at heading sizes,
        // so including them biases the median high and hides bold headings whose size
        // lies between body and the heavier tier.
        var nonBoldSizes = spans.Where(s => !IsBold(s)).Select(s => s.FontSize).Where(sz => sz > 0.0f).ToList();
        float medianBody;
        if (nonBoldSizes.Count == 0)
        {
            var sizes = spans.Select(s => s.FontSize).Where(sz => sz > 0.0f).ToList();
            if (sizes.Count == 0) return result;
            OxSpanCompare.SortStable(sizes, OxSpanCompare.SafeFloatCmp);
            medianBody = sizes[sizes.Count / 2];
        }
        else
        {
            OxSpanCompare.SortStable(nonBoldSizes, OxSpanCompare.SafeFloatCmp);
            medianBody = nonBoldSizes[nonBoldSizes.Count / 2];
        }
        float headingSizeFloor = medianBody * 1.15f;
        bool IsHeadingLike(OxTextSpan s) => IsBold(s) || s.FontSize > headingSizeFloor;

        var order = Enumerable.Range(0, spans.Count).ToList();
        OxSpanCompare.SortStable(order, (a, b) =>
        {
            int yc = OxSpanCompare.SafeFloatCmp(spans[b].Bbox.Top, spans[a].Bbox.Top);
            return yc != 0 ? yc : OxSpanCompare.SafeFloatCmp(spans[a].Bbox.Left, spans[b].Bbox.Left);
        });

        // Wrapped heading lines often re-indent slightly; a gap wider than one line is
        // a paragraph break, not a wrap.
        const float indentTolerance = 6.0f;
        const float fontEps = 0.5f;
        var runs = new List<List<int>>();
        var current = new List<int>();

        foreach (int idx in order)
        {
            var span = spans[idx];
            if (!IsHeadingLike(span))
            {
                if (current.Count > 0) { runs.Add(current); current = new List<int>(); }
                continue;
            }
            if (current.Count == 0) { current.Add(idx); continue; }

            var last = spans[current[^1]];
            bool sizeOk = MathF.Abs(span.FontSize - last.FontSize) <= fontEps;
            bool boldOk = IsBold(span) == IsBold(last);

            // Same line (two bold Tj segments of one wrapped line): fold without the
            // indent / leading checks, which only make sense across lines.
            bool sameLine = MathF.Abs(span.Bbox.Top - last.Bbox.Top) <= 1.0f;
            if (sizeOk && boldOk && sameLine) { current.Add(idx); continue; }

            // The font-size floor covers ascender-only / descender-only glyphs whose
            // bbox collapses to less than a line.
            float lineH = MathF.Max(MathF.Max(MathF.Max(last.Bbox.Height, span.Bbox.Height), last.FontSize), 1.0f);
            float leadingTolerance = lineH * 1.5f;
            float verticalGap = MathF.Abs(last.Bbox.Top - span.Bbox.Top);
            bool indentOk = span.Bbox.Left >= last.Bbox.Left - indentTolerance
                            && span.Bbox.Left <= last.Bbox.Left + indentTolerance;
            bool leadingOk = verticalGap <= leadingTolerance;

            if (sizeOk && boldOk && indentOk && leadingOk) current.Add(idx);
            else { runs.Add(current); current = new List<int> { idx }; }
        }
        if (current.Count > 0) runs.Add(current);

        // Single-line bold spans need no locking — XY-cut already handles them, and
        // locking would only add overhead.
        foreach (var spanIndices in runs)
        {
            if (spanIndices.Count < 2) continue;
            var distinctLines = new SortedSet<int>();
            foreach (int i in spanIndices) distinctLines.Add(OxSpanCompare.RoundToI32(spans[i].Bbox.Top));
            if (distinctLines.Count < 2) continue;
            result.Add(new HeadingRun { SpanIndices = spanIndices, CombinedBbox = UnionBboxes(spans, spanIndices) });
        }
        return result;
    }

    /// <summary>
    /// Collapse each heading run to ONE synthetic span carrying the union bbox; other
    /// spans pass through. <c>origins[k]</c> lists the original spans behind synthetic
    /// span k, so the partition output can be projected back.
    /// </summary>
    private static (List<OxTextSpan> Synthetic, List<List<int>> Origins) SynthesizeForPartition(
        IReadOnlyList<OxTextSpan> spans, List<HeadingRun> runs)
    {
        var inRun = new int?[spans.Count];
        for (int r = 0; r < runs.Count; r++)
            foreach (int i in runs[r].SpanIndices) inRun[i] = r;

        var synthetic = new List<OxTextSpan>(spans.Count);
        var origins = new List<List<int>>(spans.Count);
        var emitted = new bool[runs.Count];

        for (int i = 0; i < spans.Count; i++)
        {
            int? r = inRun[i];
            if (r is null)
            {
                synthetic.Add(spans[i]);
                origins.Add(new List<int> { i });
            }
            else if (!emitted[r.Value])
            {
                // Placeholder at the position of the run's first-encountered span. The
                // clone is read-only for the partition (bbox / text / font), so sharing
                // the per-glyph lists with the original is harmless.
                var run = runs[r.Value];
                var placeholder = spans[i].Clone();
                placeholder.Bbox = run.CombinedBbox;

                // Joined with single spaces so the core-width estimate in
                // IsSingleColumnRegion tracks the whole heading, not its first fragment.
                var combined = new StringBuilder();
                for (int k = 0; k < run.SpanIndices.Count; k++)
                {
                    if (k > 0) combined.Append(' ');
                    combined.Append(spans[run.SpanIndices[k]].Text);
                }
                placeholder.Text = combined.ToString();

                synthetic.Add(placeholder);
                origins.Add(new List<int>(run.SpanIndices));
                emitted[r.Value] = true;
            }
        }

        return (synthetic, origins);
    }

    // ── Recursive partition ─────────────────────────────────────────────────────

    private static List<List<int>> PartitionIndexed(IReadOnlyList<OxTextSpan> all, List<int> indices) =>
        PartitionIndexedDepth(all, indices, 0);

    private static List<List<int>> PartitionIndexedDepth(IReadOnlyList<OxTextSpan> all, List<int> indices, uint depth)
    {
        if (indices.Count == 0) return new List<List<int>>();
        if (indices.Count < MinSpansForSplit) return new List<List<int>> { SortIndices(all, indices) };
        if (depth >= MaxPartitionDepth) return new List<List<int>> { SortIndices(all, indices) };
        var regionKind = ClassifyRegionKind(all, indices);

        // Two-column-prose probe BEFORE the single-column short-circuit: a tight
        // ~10-15pt gutter sits below MinValleyWidth so the projection valley misses it,
        // and each line's bbox spans that gutter so the wide+dense heuristic calls the
        // body one column. The probe only fires when every signal agrees, and the Prose
        // gate is what keeps it off a two-column sub-region of a table.
        float? twoColumnGutter = DetectTwoColumnProse(all, indices, regionKind);
        if (twoColumnGutter is { } gutterX)
        {
            // Band-separation first: a full-width header/footer row that spans the
            // gutter would otherwise be absorbed into one column half and end up
            // mid-page in reading order.
            var band = FindVerticalSplitIndexed(all, indices);
            if (band is { } bandSplit)
            {
                var res = PartitionIndexedDepth(all, bandSplit.Above, depth + 1);
                res.AddRange(PartitionIndexedDepth(all, bandSplit.Below, depth + 1));
                return res;
            }
            var left = indices.Where(i => all[i].Bbox.Left < gutterX).ToList();
            var right = indices.Where(i => !(all[i].Bbox.Left < gutterX)).ToList();
            if (left.Count > 0 && right.Count > 0)
            {
                var res = PartitionIndexedDepth(all, left, depth + 1);
                res.AddRange(PartitionIndexedDepth(all, right, depth + 1));
                return res;
            }
        }

        // Second pass for layouts whose line-start cluster shape is masked by outlier
        // singletons (titles, captions, equation rows). It cuts at the gap cluster
        // WITHOUT peeling a band first: on these pages the vertical split fires on a
        // mid-body paragraph gap and bisects the body, after which neither half keeps
        // enough gutter signal for the column cut to reach it on recursion.
        float? narrowGutter = DetectNarrowGutterProse(all, indices, regionKind);
        if (narrowGutter is { } narrowX)
        {
            var left = indices.Where(i => all[i].Bbox.Left < narrowX).ToList();
            var right = indices.Where(i => !(all[i].Bbox.Left < narrowX)).ToList();
            if (left.Count > 0 && right.Count > 0)
            {
                var res = PartitionIndexedDepth(all, left, depth + 1);
                res.AddRange(PartitionIndexedDepth(all, right, depth + 1));
                return res;
            }
        }

        // Real body text has density dips — indented code, short last lines, paragraph
        // breaks — that would trigger spurious column or row splits. The sort inside the
        // block already gets the row order right.
        if (IsSingleColumnRegion(all, indices)) return new List<List<int>> { SortIndices(all, indices) };

        // Horizontal-first (a vertical cut line) splits columns before rows, which is
        // what makes XY-cut a column detector for Western top-down-left-to-right
        // reading order (ISO 32000-1 §14.8.4).
        var firstSplit = PreferHorizontal
            ? FindHorizontalSplitIndexed(all, indices)
            : FindVerticalSplitIndexed(all, indices);
        if (firstSplit is { } fs)
        {
            var res = PartitionIndexedDepth(all, fs.Above, depth + 1);
            res.AddRange(PartitionIndexedDepth(all, fs.Below, depth + 1));
            return res;
        }
        var secondSplit = PreferHorizontal
            ? FindVerticalSplitIndexed(all, indices)
            : FindHorizontalSplitIndexed(all, indices);
        if (secondSplit is { } ss)
        {
            var res = PartitionIndexedDepth(all, ss.Above, depth + 1);
            res.AddRange(PartitionIndexedDepth(all, ss.Below, depth + 1));
            return res;
        }

        return new List<List<int>> { SortIndices(all, indices) };
    }

    /// <summary>The two sides of a split, in reading order.</summary>
    private readonly struct Split
    {
        public readonly List<int> Above;
        public readonly List<int> Below;
        public Split(List<int> above, List<int> below) { Above = above; Below = below; }
    }

    // ── Region classification ───────────────────────────────────────────────────

    /// <summary>
    /// Positively identify prose before a tight gutter cut is allowed. Two earlier
    /// attempts at the multi-column fix were reverted because they fired on a
    /// two-column sub-region of a real table and reordered its digits; failing to
    /// identify a table is not enough, the region has to look like prose.
    /// </summary>
    private static RegionKind ClassifyRegionKind(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        if (indices.Count < 6) return RegionKind.Mixed;

        float xMin = float.MaxValue, xMax = float.MinValue;
        foreach (int i in indices)
        {
            xMin = MathF.Min(xMin, all[i].Bbox.Left);
            xMax = MathF.Max(xMax, all[i].Bbox.Right);
        }
        float regionWidth = xMax - xMin;
        if (regionWidth <= 10.0f) return RegionKind.Mixed;

        var lines = new SortedDictionary<int, (float Left, float Right, int Chars)>();
        foreach (int i in indices)
        {
            var s = all[i];
            int yKey = OxSpanCompare.RoundToI32(s.Bbox.Top);
            int nonWs = NonWhitespaceCount(s.Text);
            if (!lines.TryGetValue(yKey, out var e)) e = (float.MaxValue, float.MinValue, 0);
            lines[yKey] = (MathF.Min(e.Left, s.Bbox.Left), MathF.Max(e.Right, s.Bbox.Right), e.Chars + nonWs);
        }

        int lineCount = lines.Count;
        // Headings, captions and single paragraphs land here — leave them to the
        // default XY-cut behaviour.
        if (lineCount < 6) return RegionKind.Mixed;

        int totalChars = 0, narrowLines = 0, wideLines = 0;
        foreach (var (left, right, chars) in lines.Values)
        {
            totalChars += chars;
            float extent = MathF.Max(right - left, 0.0f);
            if (extent < regionWidth * 0.6f) narrowLines++;
            else wideLines++;
        }
        float meanChars = (float)totalChars / lineCount;

        // Prose: a tall stack of wide lines (single-column body), or of half-column
        // lines carrying substantial content (two prose columns).
        bool mostlyWide = wideLines * 2 > lineCount;
        bool mostlyNarrow = narrowLines * 2 > lineCount;
        if (meanChars > 20.0f && (mostlyWide || mostlyNarrow)) return RegionKind.Prose;

        // The >20 chars guard was rejecting short-verse two-column bodies (bibles,
        // lexicons) along with short-cell tables. Re-admit only the short-line case
        // that carries a central gutter corridor a table cannot fake.
        if (meanChars <= 20.0f && ShortLineCentralCorridorProse(all, indices, xMin, regionWidth))
            return RegionKind.Prose;

        // Table: many narrow lines with almost no content each — digit-only cells.
        if (meanChars < 8.0f) return RegionKind.Table;

        return RegionKind.Mixed;
    }

    /// <summary>
    /// Short-line two-column-prose admission. Every gate below is a length-independent
    /// discriminator a short-cell label+data table fails: its dominant gap scatters
    /// across cell boundaries (concentration/coverage), sits off-centre, leaves the
    /// label column lopsided in char mass, or produces three or more left-edge clusters.
    /// </summary>
    private static bool ShortLineCentralCorridorProse(
        IReadOnlyList<OxTextSpan> all, List<int> indices, float xMin, float regionWidth)
    {
        if (regionWidth <= 0.0f) return false;

        var lines = new SortedDictionary<int, List<(float Left, float Right, int Chars)>>();
        foreach (int i in indices)
        {
            var s = all[i];
            int yKey = OxSpanCompare.RoundToI32(s.Bbox.Top);
            if (!lines.TryGetValue(yKey, out var list)) { list = new List<(float, float, int)>(); lines[yKey] = list; }
            list.Add((s.Bbox.Left, s.Bbox.Right, NonWhitespaceCount(s.Text)));
        }
        int totalLines = lines.Count;
        if (totalLines == 0) return false;

        // 6pt suppresses ordinary 2-5pt word spacing.
        const float minGapPt = 6.0f;
        var gapPositions = new List<float>();
        foreach (var lineSpans in lines.Values)
        {
            if (lineSpans.Count < 2) continue;
            var sorted = new List<(float Left, float Right, int Chars)>(lineSpans);
            OxSpanCompare.SortStable(sorted, (a, b) => OxSpanCompare.SafeFloatCmp(a.Left, b.Left));
            float largestGap = 0.0f, largestMid = 0.0f;
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                float gap = sorted[k + 1].Left - sorted[k].Right;
                if (gap > largestGap) { largestGap = gap; largestMid = (sorted[k].Right + sorted[k + 1].Left) * 0.5f; }
            }
            if (largestGap >= minGapPt) gapPositions.Add(largestMid);
        }
        if (gapPositions.Count == 0) return false;

        const float clusterRadiusPt = 10.0f;
        var sortedGaps = new List<float>(gapPositions);
        OxSpanCompare.SortStable(sortedGaps, OxSpanCompare.SafeFloatCmp);
        int bestSize = 0;
        float bestCenter = 0.0f;
        foreach (float pivot in sortedGaps)
        {
            float lo = pivot - clusterRadiusPt, hi = pivot + clusterRadiusPt;
            int count = 0;
            float sum = 0.0f;
            foreach (float g in sortedGaps)
                if (g >= lo && g <= hi) { count++; sum += g; }
            if (count > bestSize) { bestSize = count; bestCenter = sum / count; }
        }
        if (bestSize == 0) return false;

        // Concentration ≥ 70% of gap-bearing lines, coverage ≥ 60% of all lines.
        if (bestSize * 10 < gapPositions.Count * 7) return false;
        if (bestSize * 10 < totalLines * 6) return false;

        float gutterOffset = bestCenter - xMin;
        if (gutterOffset < regionWidth * 0.30f || gutterOffset > regionWidth * 0.70f) return false;

        int leftChars = 0, rightChars = 0;
        foreach (var lineSpans in lines.Values)
            foreach (var (l, r, chars) in lineSpans)
            {
                float mid = (l + r) * 0.5f;
                if (mid < bestCenter) leftChars += chars;
                else rightChars += chars;
            }
        int total = leftChars + rightChars;
        if (total == 0) return false;
        if (leftChars < total * 0.35f || rightChars < total * 0.35f) return false;

        // A real two-column body starts its left column at one X; an N-column table has
        // several cell-start X's left of the corridor. Cluster EVERY left edge, not just
        // each line's minimum, or multi-column cell starts collapse into one cluster.
        const float leftClusterRadiusPt = 30.0f;
        var clusters = new List<(float Center, int Count)>();
        foreach (var lineSpans in lines.Values)
            foreach (var (l, _, _) in lineSpans)
            {
                if (l >= bestCenter) continue;
                int idx = clusters.FindIndex(c => MathF.Abs(c.Center - l) <= leftClusterRadiusPt);
                if (idx >= 0)
                {
                    float count = clusters[idx].Count;
                    clusters[idx] = ((clusters[idx].Center * count + l) / (count + 1.0f), clusters[idx].Count + 1);
                }
                else clusters.Add((l, 1));
            }
        // Singleton clusters are noise; a lone outlier left edge must not inflate the count.
        int dominantLeftClusters = clusters.Count(c => c.Count >= 2);
        return dominantLeftClusters < 3;
    }

    /// <summary>
    /// Does this region look like two side-by-side prose columns with a tight gutter?
    /// The signal is that most lines fit inside one half of the region width and their
    /// left edges cluster into exactly two groups separated by about half that width.
    /// Returns the gutter X when so.
    /// </summary>
    private static float? DetectTwoColumnProse(IReadOnlyList<OxTextSpan> all, List<int> indices, RegionKind regionKind)
    {
        if (indices.Count < 8) return null;

        float xMin = float.MaxValue, xMax = float.MinValue;
        foreach (int i in indices)
        {
            xMin = MathF.Min(xMin, all[i].Bbox.Left);
            xMax = MathF.Max(xMax, all[i].Bbox.Right);
        }
        float regionWidth = xMax - xMin;
        // The narrowest two-column layout in the corpus is ~250pt of body.
        if (regionWidth < 200.0f) return null;

        // Per-span (not per-line) extents: the canonical interleave puts a left-column
        // span and a right-column span on the same baseline, so the LINE bbox looks wide
        // even though each side is a narrow column half.
        var linesSpans = new SortedDictionary<int, List<(float Left, float Right)>>();
        foreach (int i in indices)
        {
            var s = all[i];
            int yKey = OxSpanCompare.RoundToI32(s.Bbox.Top);
            if (!linesSpans.TryGetValue(yKey, out var list)) { list = new List<(float, float)>(); linesSpans[yKey] = list; }
            list.Add((s.Bbox.Left, s.Bbox.Right));
        }
        if (linesSpans.Count < 6) return null;

        float narrowThreshold = regionWidth * 0.6f;
        const float intraLineGapThreshold = 10.0f;
        var narrowLefts = new List<float>();
        // A line with a within-line gap counts once however many half-lines it yields,
        // so the majority threshold stays comparable to single-column reasoning.
        int narrowLineCount = 0;

        foreach (var lineSpans in linesSpans.Values)
        {
            var sorted = new List<(float Left, float Right)>(lineSpans);
            OxSpanCompare.SortStable(sorted, (a, b) => OxSpanCompare.SafeFloatCmp(a.Left, b.Left));
            float largestGap = 0.0f;
            int splitIdx = -1;
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                float gap = sorted[k + 1].Left - sorted[k].Right;
                if (gap > largestGap) { largestGap = gap; splitIdx = k; }
            }
            float lineLeft = sorted.Count > 0 ? sorted[0].Left : 0.0f;
            float lineRight = sorted.Count > 0 ? sorted[^1].Right : 0.0f;
            float lineExtent = MathF.Max(lineRight - lineLeft, 0.0f);

            if (splitIdx >= 0 && largestGap >= intraLineGapThreshold)
            {
                narrowLefts.Add(lineLeft);
                if (splitIdx + 1 < sorted.Count) narrowLefts.Add(sorted[splitIdx + 1].Left);
                narrowLineCount++;
                continue;
            }
            if (lineExtent < narrowThreshold) { narrowLefts.Add(lineLeft); narrowLineCount++; }
        }
        // Otherwise this is a single-column body with a few short last lines.
        if (narrowLineCount * 2 < linesSpans.Count) return null;

        const float clusterRadius = 30.0f;
        var clusters = new List<(float Center, int Count)>();
        foreach (float x in narrowLefts)
        {
            int idx = clusters.FindIndex(c => MathF.Abs(c.Center - x) <= clusterRadius);
            if (idx >= 0)
            {
                float count = clusters[idx].Count;
                clusters[idx] = ((clusters[idx].Center * count + x) / (count + 1.0f), clusters[idx].Count + 1);
            }
            else clusters.Add((x, 1));
        }

        // Three or more clusters means a table or a band-mixed region.
        if (clusters.Count != 2) return null;
        OxSpanCompare.SortStable(clusters, (a, b) => OxSpanCompare.SafeFloatCmp(a.Center, b.Center));
        var (c1X, c1N) = clusters[0];
        var (c2X, c2N) = clusters[1];

        // Reject lopsided shapes (a header plus one body paragraph).
        int minCluster = Math.Max(3, narrowLefts.Count / 5);
        if (c1N < minCluster || c2N < minCluster) return null;

        // For a ~12pt gutter between two ~250pt columns the centre gap is ~49% of the
        // region — well clear of this floor.
        if (c2X - c1X < regionWidth * 0.30f) return null;

        if (regionKind != RegionKind.Prose) return null;

        // Cluster centres are the two columns' left edges; without per-cluster right
        // edges the gutter centre is approximated as their midpoint. Close enough — the
        // partition itself tests each span's own left edge.
        return (c1X + c2X) * 0.5f;
    }

    /// <summary>
    /// Second-pass two-column detector for narrow gutters the line-start clustering
    /// misses: papers that emit body text at character-cluster granularity scatter
    /// outlier clusters (titles, captions, equation labels) that trip the exactly-two
    /// gate, and their gutters are narrower than <see cref="MinValleyWidth"/>.
    /// The signal that survives is that the largest within-line gap sits at roughly the
    /// same X on a strong majority of lines.
    /// </summary>
    private static float? DetectNarrowGutterProse(IReadOnlyList<OxTextSpan> all, List<int> indices, RegionKind regionKind)
    {
        if (indices.Count < 24) return null;

        float xMin = float.MaxValue, xMax = float.MinValue;
        foreach (int i in indices)
        {
            xMin = MathF.Min(xMin, all[i].Bbox.Left);
            xMax = MathF.Max(xMax, all[i].Bbox.Right);
        }
        float regionWidth = xMax - xMin;
        if (regionWidth < 200.0f) return null;

        var lines = new SortedDictionary<int, List<(float Left, float Right)>>();
        foreach (int i in indices)
        {
            var s = all[i];
            int yKey = OxSpanCompare.RoundToI32(s.Bbox.Top);
            if (!lines.TryGetValue(yKey, out var list)) { list = new List<(float, float)>(); lines[yKey] = list; }
            list.Add((s.Bbox.Left, s.Bbox.Right));
        }
        if (lines.Count < 12) return null;

        const float minGapPt = 6.0f;
        var gapPositions = new List<float>();
        foreach (var lineSpans in lines.Values)
        {
            if (lineSpans.Count < 2) continue;
            var sorted = new List<(float Left, float Right)>(lineSpans);
            OxSpanCompare.SortStable(sorted, (a, b) => OxSpanCompare.SafeFloatCmp(a.Left, b.Left));
            float largestGap = 0.0f, largestMid = 0.0f;
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                float gap = sorted[k + 1].Left - sorted[k].Right;
                if (gap > largestGap) { largestGap = gap; largestMid = (sorted[k].Right + sorted[k + 1].Left) * 0.5f; }
            }
            if (largestGap >= minGapPt) gapPositions.Add(largestMid);
        }
        // Fewer gap-bearing lines than this is statistical noise.
        if (gapPositions.Count < 12) return null;

        // Sliding two-pointer window over the sorted positions: both ends only advance,
        // so clustering is O(n) where the pivot scan was O(n²) — thesis-style pages with
        // hundreds of gap-bearing rows pay for that visibly.
        const float clusterRadiusPt = 10.0f;
        var sortedGaps = new List<float>(gapPositions);
        OxSpanCompare.SortStable(sortedGaps, OxSpanCompare.SafeFloatCmp);
        var prefix = new float[sortedGaps.Count + 1];
        for (int k = 0; k < sortedGaps.Count; k++) prefix[k + 1] = prefix[k] + sortedGaps[k];

        int bestSize = 0;
        float bestCenter = 0.0f;
        int left = 0, right = 0;
        foreach (float pivot in sortedGaps)
        {
            while (left < sortedGaps.Count && sortedGaps[left] < pivot - clusterRadiusPt) left++;
            while (right < sortedGaps.Count && sortedGaps[right] <= pivot + clusterRadiusPt) right++;
            int count = right - left;
            float sum = prefix[right] - prefix[left];
            if (count > bestSize) { bestSize = count; bestCenter = sum / count; }
        }

        // One gutter concentrates; a table's gaps spread over several cell boundaries.
        if (bestSize * 10 < gapPositions.Count * 7) return null;
        if (bestSize < 12) return null;
        if (bestSize * 5 < lines.Count) return null;

        float gutterOffset = bestCenter - xMin;
        if (gutterOffset < regionWidth * 0.2f || gutterOffset > regionWidth * 0.8f) return null;

        if (regionKind != RegionKind.Prose) return null;

        return bestCenter;
    }

    /// <summary>
    /// Does the region look like a single column of body text? When it does the caller
    /// skips both splits, which is what keeps XY-cut from fragmenting body text at the
    /// density dips indentation and short last lines create.
    /// </summary>
    /// <summary>One line's span extents, tagged with its arrival position.</summary>
    /// <remarks>
    /// <c>Seq</c> is what makes the pooled rewrite below equivalent to the
    /// <c>SortedDictionary</c>-of-<c>List</c> it replaces: grouping by Y and then sorting by
    /// left edge has to keep ties in arrival order, which a stable sort gave for free and a
    /// span sort does not. Carrying the index and breaking ties on it restores that exactly.
    /// </remarks>
    private readonly struct LineEntry
    {
        public readonly int YKey; public readonly float Left, Right, CoreRight; public readonly int Seq;
        public LineEntry(int yKey, float left, float right, float coreRight, int seq)
        { YKey = yKey; Left = left; Right = right; CoreRight = coreRight; Seq = seq; }
    }

    /// <summary>
    /// Whether a region reads as one column.
    /// </summary>
    /// <remarks>
    /// This runs once per partition call — a little over 200,000 times for a 16-document
    /// batch — and every call used to build a <c>SortedDictionary</c>, a list per line, a
    /// copy of each of those lists, and three more lists for the cluster passes. That was the
    /// single largest allocation source in PDF extraction. The logic is unchanged; only the
    /// containers are, and they now come from the array pool.
    /// </remarks>
    private static bool IsSingleColumnRegion(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        if (indices.Count < 3) return false;

        float xMin = float.MaxValue, xMax = float.MinValue;
        foreach (int i in indices)
        {
            xMin = MathF.Min(xMin, all[i].Bbox.Left);
            xMax = MathF.Max(xMax, all[i].Bbox.Right);
        }
        float regionWidth = xMax - xMin;
        if (regionWidth <= 10.0f) return true;

        int n = indices.Count;
        var pool = System.Buffers.ArrayPool<LineEntry>.Shared;
        var fpool = System.Buffers.ArrayPool<float>.Shared;
        var entries = pool.Rent(n);
        var gaps = fpool.Rent(n);
        var mins = fpool.Rent(n);
        try
        {
            // core_right (char count × em) is a conservative right edge used only where
            // adjacent bbox edges overlap, which signals extractor bbox inflation —
            // trailing whitespace and stretched advance widths make multi-column lines look
            // like one continuous run.
            for (int k = 0; k < n; k++)
            {
                var sp = all[indices[k]];
                float charCount = Math.Max(1, NonWhitespaceCount(sp.Text));
                float approxCharWidth = MathF.Max(sp.FontSize * 0.45f, 2.5f);
                entries[k] = new LineEntry(
                    OxSpanCompare.RoundToI32(sp.Bbox.Top), sp.Bbox.Left, sp.Bbox.Right,
                    sp.Bbox.Left + charCount * approxCharWidth, k);
            }

            var all_ = entries.AsSpan(0, n);
            // Ascending Y key, arrival order within a key: the iteration order the
            // SortedDictionary produced.
            all_.Sort((a, b) => a.YKey != b.YKey ? a.YKey.CompareTo(b.YKey) : a.Seq.CompareTo(b.Seq));

            int lineCount = 0;
            for (int k = 0; k < n; k++) if (k == 0 || all_[k].YKey != all_[k - 1].YKey) lineCount++;
            if (lineCount < 3) return false;

            // A real gutter recurs at roughly the same X across lines. Sparse title pages
            // also have wide inter-word gaps, but theirs are scattered.
            float maxGap = MinValleyWidth;
            int gapCount = 0, minCount = 0;
            int start = 0;
            while (start < n)
            {
                int stop = start;
                while (stop < n && all_[stop].YKey == all_[start].YKey) stop++;
                var line = all_.Slice(start, stop - start);
                line.Sort((a, b) =>
                {
                    int c = OxSpanCompare.SafeFloatCmp(a.Left, b.Left);
                    return c != 0 ? c : a.Seq.CompareTo(b.Seq);
                });

                float lineMin = float.MaxValue;
                foreach (ref readonly var e in line) lineMin = MathF.Min(lineMin, e.Left);
                mins[minCount++] = lineMin;

                for (int k = 0; k + 1 < line.Length; k++)
                {
                    float bboxGap = line[k + 1].Left - line[k].Right;
                    float effectiveGap, gapEndLeft;
                    if (bboxGap < 0.0f) { effectiveGap = line[k + 1].Left - line[k].CoreRight; gapEndLeft = line[k].CoreRight; }
                    else { effectiveGap = bboxGap; gapEndLeft = line[k].Right; }
                    if (effectiveGap >= maxGap) gaps[gapCount++] = (gapEndLeft + line[k + 1].Left) * 0.5f;
                }
                start = stop;
            }

            // A centered title/subtitle/byline block produces accidental gap clusters that
            // look like a gutter, and reading it as columns shreds the title. The
            // discriminator: a left-aligned layout — single OR multi-column — starts most
            // rows at the same left margin, so the largest cluster of per-line leftmost
            // edges covers a majority. Centered text has each line's leftmost edge
            // scattered. A cluster fraction rather than raw spread survives rows that hold
            // only right-column content, which inflate the spread without moving the margin.
            bool looksCentered;
            if (minCount < 2) looksCentered = false;
            else
            {
                const float tol = 10.0f;
                var ms = mins.AsSpan(0, minCount);
                ms.Sort(OxSpanCompare.SafeFloatCmp);
                int largest = 0;
                foreach (float a in ms)
                {
                    int lo = PartitionPointLess(ms, a - tol);
                    int hi = PartitionPointLessOrEqual(ms, a + tol);
                    largest = Math.Max(largest, hi - lo);
                }
                looksCentered = largest < minCount * 0.5f;
            }

            // A SMALL centered block (title page) is one column so its lines stay in
            // top-to-bottom order; the line cap keeps a real multi-column body out.
            if (looksCentered && lineCount <= 6) return true;

            if (gapCount > 0 && !looksCentered)
            {
                const float clusterRadius = 20.0f;
                // 20% accommodates pages where header/footer/title rows dilute the body-line
                // count but a real multi-column body still dominates.
                int minCluster = Math.Max(3, lineCount / 5);
                var gs = gaps.AsSpan(0, gapCount);
                gs.Sort(OxSpanCompare.SafeFloatCmp);
                foreach (float pos in gs)
                {
                    int lo = PartitionPointLess(gs, pos - clusterRadius);
                    int hi = PartitionPointLessOrEqual(gs, pos + clusterRadius);
                    if (hi - lo >= minCluster) return false;
                }
            }

            // No gutter anywhere: accept when most lines are wide AND densely covered.
            float widthThreshold = regionWidth * 0.6f;
            int wideDenseLines = 0;
            start = 0;
            while (start < n)
            {
                int stop = start;
                while (stop < n && all_[stop].YKey == all_[start].YKey) stop++;
                var line = all_.Slice(start, stop - start);   // already sorted by Left above
                start = stop;

                float extentLeft = line[0].Left;
                float extentRight = float.MinValue;
                foreach (ref readonly var e in line) extentRight = MathF.Max(extentRight, e.Right);
                float extent = extentRight - extentLeft;
                if (extent < widthThreshold) continue;

                // Coverage uses core_right, not bbox.right: tab-expanded table rows would
                // otherwise score 100% coverage and pass as dense body text.
                float covered = 0.0f;
                float lastEnd = float.MinValue;
                foreach (ref readonly var e in line)
                {
                    float effectiveRight = MathF.Min(e.CoreRight, extentRight);
                    float st = MathF.Max(e.Left, lastEnd);
                    if (effectiveRight > st) { covered += effectiveRight - st; lastEnd = effectiveRight; }
                }
                if (covered >= extent * 0.8f) wideDenseLines++;
            }
            return wideDenseLines * 2 >= lineCount;
        }
        finally
        {
            pool.Return(entries);
            fpool.Return(gaps);
            fpool.Return(mins);
        }
    }

    // Rust `slice::partition_point` over a list sorted with SafeFloatCmp.
    private static int PartitionPointLess(ReadOnlySpan<float> sorted, float v)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int m = lo + (hi - lo) / 2; if (sorted[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    private static int PartitionPointLessOrEqual(ReadOnlySpan<float> sorted, float v)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int m = lo + (hi - lo) / 2; if (sorted[m] <= v) lo = m + 1; else hi = m; }
        return lo;
    }

    private static int PartitionPointLess(List<float> sorted, float v)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int m = lo + (hi - lo) / 2; if (sorted[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    private static int PartitionPointLessOrEqual(List<float> sorted, float v)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int m = lo + (hi - lo) / 2; if (sorted[m] <= v) lo = m + 1; else hi = m; }
        return lo;
    }

    // ── Splits ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Vertical cut line (X axis) separating columns, left side first.
    /// </summary>
    private static Split? FindHorizontalSplitIndexed(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        var profile = HorizontalProjectionIndexed(all, indices);
        if (profile is null) return null;
        var p = profile.Value;

        float splitX;
        var valley = FindValley(p);
        if (valley is { } v)
        {
            if (v.Width < MinValleyWidth) return null;
            splitX = p.XMin + (v.Start + v.End) / 2.0f;
        }
        else
        {
            var between = FindSplitBetweenPeaks(p);
            if (between is null) return null;
            splitX = between.Value;
        }

        // Without a floor on the resulting column width the recursion sub-splits one
        // body column into slivers at internal whitespace valleys (paragraph
        // indentation, justified trailing gaps, isolated short words), turning a clean
        // column-major emit into a band-chunked stream. A real body column holds at
        // least ~6 characters at 10pt.
        const float minResultWidthPt = 60.0f;
        float leftXMin = float.MaxValue, leftXMax = float.MinValue;
        float rightXMin = float.MaxValue, rightXMax = float.MinValue;
        foreach (int i in indices)
        {
            float l = all[i].Bbox.Left, r = all[i].Bbox.Right;
            if (l < splitX) { leftXMin = MathF.Min(leftXMin, l); leftXMax = MathF.Max(leftXMax, r); }
            else { rightXMin = MathF.Min(rightXMin, l); rightXMax = MathF.Max(rightXMax, r); }
        }
        if (leftXMax - leftXMin < minResultWidthPt || rightXMax - rightXMin < minResultWidthPt) return null;

        // Partition by LEFT edge, where the glyphs actually start: extractor bboxes
        // overreach to the right, and for wide spans even the centre can drift past the
        // split.
        var left = indices.Where(i => all[i].Bbox.Left < splitX).ToList();
        var right = indices.Where(i => !(all[i].Bbox.Left < splitX)).ToList();
        if (left.Count == 0 || right.Count == 0) return null;

        // A 95/5 split is edge dips or stray content, not a column.
        int minSide = Math.Max(indices.Count / 10, 2);
        if (left.Count < minSide || right.Count < minSide) return null;

        // Table-row guard. A genuine gutter is a corridor: the left column's glyphs end
        // before it and the right column's begin after it, so the sides are X-disjoint.
        // A data table's rows start at the left margin but run the full width, so
        // bucketing by left edge throws whole rows into `left` while their right-hand
        // cells land in `right`, and taking the cut shreds the table. Requiring three
        // such rows isolates a real table from a single wide mis-split line, and makes
        // the guard purely subtractive: it only ever rejects a cut, after which the
        // recursion falls back to a row split and reads the table row-major.
        float rightEdgeMax = float.MinValue;
        float maxFont = 0.0f;
        foreach (int i in right)
        {
            rightEdgeMax = MathF.Max(rightEdgeMax, all[i].Bbox.Right);
            maxFont = MathF.Max(maxFont, MathF.Abs(all[i].Bbox.Height));
        }
        // One body em of slack lets a single straddling glyph pass.
        float overlapTol = MathF.Max(maxFont, 10.0f);
        int fullWidthLeftRows = left.Count(i =>
        {
            var s = all[i];
            // core width, not bbox.right, so a real left column's last word is not
            // mistaken for a glyph crossing the gutter.
            float nonWs = Math.Max(1, NonWhitespaceCount(s.Text));
            float approxCharWidth = MathF.Max(s.FontSize * 0.45f, 2.5f);
            return s.Bbox.Left + nonWs * approxCharWidth >= rightEdgeMax - overlapTol;
        });
        if (fullWidthLeftRows >= 3) return null;

        return new Split(left, right);
    }

    /// <summary>
    /// Fallback column split when valley detection fails because narrow table-cell spans
    /// partially fill the gutter: the deepest trough between the two strongest density
    /// peaks, accepted only when it is at most half the weaker peak.
    /// </summary>
    private static float? FindSplitBetweenPeaks(ProjectionProfile profile)
    {
        var density = profile.Density;
        int n = density.Length;
        if (n < 3) return null;

        // Box filter over the minimum valley width, so individual narrow peaks do not
        // move the mass centres.
        int smoothWindow = Math.Max((int)MinValleyWidth, 3);
        int half = smoothWindow / 2;
        var smoothed = new float[n];
        for (int i = 0; i < n; i++)
        {
            int s = Math.Max(0, i - half);
            int e = Math.Min(i + half + 1, n);
            float sum = 0.0f;
            for (int j = s; j < e; j++) sum += density[j];
            smoothed[i] = sum / (e - s);
        }

        int mid = n / 2;
        if (mid == 0) return null;
        // Rust `max_by` keeps the LAST maximum on ties; `min_by` keeps the first.
        int leftPeak = 0;
        for (int i = 1; i < mid; i++)
            if (OxSpanCompare.SafeFloatCmp(smoothed[i], smoothed[leftPeak]) >= 0) leftPeak = i;
        int rightPeak = mid;
        for (int i = mid + 1; i < n; i++)
            if (OxSpanCompare.SafeFloatCmp(smoothed[i], smoothed[rightPeak]) >= 0) rightPeak = i;

        if (smoothed[leftPeak] == 0.0f || smoothed[rightPeak] == 0.0f) return null;

        int searchStart = Math.Min(leftPeak, rightPeak) + 1;
        int searchEnd = Math.Max(leftPeak, rightPeak);
        if (searchStart >= searchEnd) return null;

        int troughPos = searchStart;
        for (int i = searchStart + 1; i < searchEnd; i++)
            if (OxSpanCompare.SafeFloatCmp(smoothed[i], smoothed[troughPos]) < 0) troughPos = i;

        float weakerPeak = MathF.Min(smoothed[leftPeak], smoothed[rightPeak]);
        if (smoothed[troughPos] > weakerPeak * 0.5f) return null;

        if (troughPos < (int)MinValleyWidth || troughPos + (int)MinValleyWidth > n) return null;

        return profile.XMin + troughPos;
    }

    /// <summary>
    /// Horizontal cut line (Y axis) separating rows. Returns (above, below) with `above`
    /// at larger Y — higher on the page, so read first.
    /// </summary>
    private static Split? FindVerticalSplitIndexed(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        var profile = VerticalProjectionIndexed(all, indices);
        if (profile is null) return null;
        var p = profile.Value;

        var valley = FindValley(p);
        if (valley is not { } v) return null;
        if (v.Width < MinValleyWidth) return null;

        float splitY = p.YMin + (v.Start + v.End) / 2.0f;

        // `Top` is the span's LOWER edge in PDF terms, so this puts a span in `above`
        // only when its lowest point already clears the cut. The cut is the midpoint of
        // an empty band, so spans should not straddle it; a tall glyph whose ascenders
        // dip into the valley falls to `below`.
        var above = indices.Where(i => all[i].Bbox.Top >= splitY).ToList();
        var below = indices.Where(i => !(all[i].Bbox.Top >= splitY)).ToList();
        if (above.Count == 0 || below.Count == 0) return null;

        // Row splits legitimately produce a singleton top partition for a lone header or
        // title, unlike column splits where a single-span column is almost always noise.
        int minSide = Math.Max(indices.Count / 10, 1);
        if (above.Count < minSide || below.Count < minSide) return null;

        return new Split(above, below);
    }

    // ── Projections and valleys ─────────────────────────────────────────────────

    private readonly struct ProjectionProfile
    {
        public readonly float[] Density;
        public readonly float XMin;
        public readonly float YMin;
        public ProjectionProfile(float[] density, float xMin, float yMin) { Density = density; XMin = xMin; YMin = yMin; }
    }

    private static ProjectionProfile? HorizontalProjectionIndexed(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        if (indices.Count == 0) return null;

        float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue;
        foreach (int i in indices)
        {
            var b = all[i].Bbox;
            xMin = MathF.Min(xMin, b.Left);
            xMax = MathF.Max(xMax, b.Right);
            yMin = MathF.Min(yMin, b.Top);
        }

        float widthF = MathF.Ceiling(xMax - xMin);
        if (!(widthF <= MaxProjectionSize)) return null;
        int width = widthF > 0.0f ? (int)widthF : 0;
        var density = new float[width];

        float regionWidth = MathF.Max(xMax - xMin, 1.0f);
        foreach (int i in indices)
        {
            var span = all[i];
            float height = span.Bbox.Bottom - span.Bbox.Top;
            int charCount = Math.Max(1, NonWhitespaceCount(span.Text));
            // 0.45em per char averages the common PDF text fonts at body size, and is
            // narrower than the 0.5em advance monospace uses.
            float approxCharWidth = MathF.Max(span.FontSize * 0.45f, 2.5f);
            float coreWidth = charCount * approxCharWidth;
            float spanWidth = span.Bbox.Right - span.Bbox.Left;

            // Full-width elements (section headers, captions, table titles) fill the
            // inter-column gutter in the density array and defeat valley detection. The
            // split still assigns them correctly by left edge.
            if (spanWidth > regionWidth * 0.55f) continue;
            // Isolated single-character cells ('G', '1') scatter across the whole X
            // range and fill the gutter too; body text spans are never one char.
            if (charCount < 2) continue;

            // Project the TEXT CORE anchored to the left edge: extractors over-estimate
            // bbox width, and only the left edge is reliable.
            float coreLeft = span.Bbox.Left;
            float coreRight = MathF.Min(coreLeft + coreWidth, span.Bbox.Right);
            int xStart = CeilToIndex(MathF.Max(coreLeft - xMin, 0.0f));
            int xEnd = CeilToIndex(coreRight - xMin);
            for (int j = xStart; j < Math.Min(xEnd, width); j++) density[j] += height;
        }

        return new ProjectionProfile(density, xMin, yMin);
    }

    private static ProjectionProfile? VerticalProjectionIndexed(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        if (indices.Count == 0) return null;

        float xMin = float.MaxValue, yMin = float.MaxValue, yMax = float.MinValue;
        foreach (int i in indices)
        {
            var b = all[i].Bbox;
            xMin = MathF.Min(xMin, b.Left);
            yMin = MathF.Min(yMin, b.Top);
            yMax = MathF.Max(yMax, b.Bottom);
        }

        float heightF = MathF.Ceiling(yMax - yMin);
        if (!(heightF <= MaxProjectionSize)) return null;
        int height = heightF > 0.0f ? (int)heightF : 0;
        var density = new float[height];

        foreach (int i in indices)
        {
            var span = all[i];
            int yStart = CeilToIndex(MathF.Max(span.Bbox.Top - yMin, 0.0f));
            int yEnd = CeilToIndex(span.Bbox.Bottom - yMin);
            float w = span.Bbox.Right - span.Bbox.Left;
            for (int j = yStart; j < Math.Min(yEnd, height); j++) density[j] += w;
        }

        return new ProjectionProfile(density, xMin, yMin);
    }

    /// <summary>Rust's <c>ceil() as usize</c>: negatives and NaN saturate to zero.</summary>
    private static int CeilToIndex(float v)
    {
        float c = MathF.Ceiling(v);
        if (!(c > 0.0f)) return 0;
        return c >= int.MaxValue ? int.MaxValue : (int)c;
    }

    /// <summary>
    /// Widest INTERIOR valley in a projection profile. Leading and trailing empty bands
    /// are page margins, not column gutters, and splitting on one is meaningless.
    /// </summary>
    private static (int Start, int End, float Width)? FindValley(ProjectionProfile profile)
    {
        var density = profile.Density;
        if (density.Length == 0) return null;

        float peak = 0.0f;
        foreach (float d in density) peak = MathF.Max(peak, d);
        if (peak == 0.0f) return null;

        int firstNonzero = -1, lastNonzero = -1;
        for (int i = 0; i < density.Length; i++) if (density[i] > 0.0f) { firstNonzero = i; break; }
        for (int i = density.Length - 1; i >= 0; i--) if (density[i] > 0.0f) { lastNonzero = i; break; }
        if (firstNonzero < 0 || lastNonzero < 0) return null;

        float threshold = peak * ValleyThreshold;
        var valleys = new List<(int Start, int End)>();
        bool inValley = false;
        int valleyStart = 0;
        for (int i = 0; i < density.Length; i++)
        {
            if (density[i] < threshold)
            {
                if (!inValley) { valleyStart = i; inValley = true; }
            }
            else if (inValley) { valleys.Add((valleyStart, i)); inValley = false; }
        }
        if (inValley) valleys.Add((valleyStart, density.Length));

        // A callout box or small figure sitting in the gutter puts a density bump in the
        // middle of what is one gap; bridging narrow interruptions re-joins the halves so
        // the gap is still recognised as a column boundary.
        int bridgeLimit = (int)MathF.Ceiling(MinValleyWidth / 2.0f);
        var merged = new List<(int Start, int End)>();
        foreach (var seg in valleys)
        {
            if (!(seg.Start > firstNonzero && seg.End <= lastNonzero + 1)) continue;
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (seg.Start <= last.End + bridgeLimit)
                {
                    merged[^1] = (last.Start, Math.Max(last.End, seg.End));
                    continue;
                }
            }
            merged.Add(seg);
        }

        (int Start, int End, float Width)? best = null;
        foreach (var (start, end) in merged)
        {
            float w = end - start;
            // Rust `max_by` keeps the LAST maximum on ties.
            if (best is null || OxSpanCompare.SafeFloatCmp(w, best.Value.Width) >= 0) best = (start, end, w);
        }
        return best;
    }

    /// <summary>Sort a block's indices into reading order: top-to-bottom, left-to-right.</summary>
    private static List<int> SortIndices(IReadOnlyList<OxTextSpan> all, List<int> indices)
    {
        var sorted = new List<int>(indices);
        OxSpanCompare.SortStable(sorted, (a, b) =>
        {
            int yc = OxSpanCompare.SafeFloatCmp(all[b].Bbox.Top, all[a].Bbox.Top);
            return yc != 0 ? yc : OxSpanCompare.SafeFloatCmp(all[a].Bbox.Left, all[b].Bbox.Left);
        });
        return sorted;
    }

    private static int NonWhitespaceCount(string text)
    {
        int n = 0;
        foreach (char c in text) if (!char.IsWhiteSpace(c)) n++;
        return n;
    }
}
