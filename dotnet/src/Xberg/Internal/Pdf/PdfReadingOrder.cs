// Faithful port of pdf_oxide's XYCutStrategy (ReadingOrder::ColumnAware),
// from pdf_oxide-0.3.73/src/pipeline/reading_order/xycut.rs.
//
// Recursive XY-Cut spatial partitioning for multi-column text layout. Produces
// a flat, reading-order span sequence: columns left-to-right, rows top-to-bottom
// within a column. The oxide text layer (crates/xberg/src/pdf/oxide/text.rs ::
// extract_page_text_column_aware) calls extract_spans_with_reading_order(page,
// ColumnAware) which delegates to XYCutStrategy::apply; this is that ordering.
//
// Geometry: pdf_oxide Rect { x, y, width, height } with top()=y (lower edge in
// PDF coords), bottom()=y+height, left()=x, right()=x+width. Larger Y = higher
// on page. C# TextSpan exposes Left/Right/Top/Bottom mirroring this.
using System.Collections.Generic;

namespace Xberg.Internal.Pdf;

internal static class PdfReadingOrder
{
    private const int MaxProjectionSize = 100_000;
    private const uint MaxPartitionDepth = 64;

    // Strategy parameters (XYCutStrategy::default()).
    private const int MinSpansForSplit = 5;
    private const double ValleyThreshold = 0.3;
    private const double MinValleyWidth = 15.0;
    private const bool PreferHorizontal = true;

    private enum RegionKind { Prose, Table, Mixed }

    // pdf_oxide utils::safe_float_cmp — total order placing NaN after all numbers.
    private static int SafeCmp(double a, double b)
    {
        bool na = double.IsNaN(a), nb = double.IsNaN(b);
        if (na && nb) return 0;
        if (na) return 1;
        if (nb) return -1;
        return a.CompareTo(b);
    }

    private static int RoundKey(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    // Stable sort of an index list by a comparator (Rust sort_by is stable).
    private static void StableSort(List<int> list, Comparison<int> cmp)
    {
        var ordered = list
            .Select((v, i) => (v, i))
            .OrderBy(t => t, Comparer<(int v, int i)>.Create((x, y) =>
            {
                int c = cmp(x.v, y.v);
                return c != 0 ? c : x.i.CompareTo(y.i);
            }))
            .Select(t => t.v)
            .ToList();
        for (int k = 0; k < list.Count; k++) list[k] = ordered[k];
    }

    /// <summary>Order spans in ColumnAware reading order (XYCutStrategy::apply).</summary>
    public static List<TextSpan> Order(List<TextSpan> spans)
    {
        if (spans.Count == 0) return spans;

        var headingRuns = FindHeadingRuns(spans);
        List<List<int>> indexGroups;
        if (headingRuns.Count == 0)
        {
            var indices = Enumerable.Range(0, spans.Count).ToList();
            indexGroups = PartitionIndexed(spans, indices);
        }
        else
        {
            var (synthetic, origin) = SynthesizeForPartition(spans, headingRuns);
            var synthIndices = Enumerable.Range(0, synthetic.Count).ToList();
            var synthGroups = PartitionIndexed(synthetic, synthIndices);
            indexGroups = new List<List<int>>(synthGroups.Count);
            foreach (var group in synthGroups)
            {
                var outg = new List<int>();
                foreach (var si in group) outg.AddRange(origin[si]);
                indexGroups.Add(outg);
            }
        }

        var result = new List<TextSpan>(spans.Count);
        var taken = new bool[spans.Count];
        foreach (var group in indexGroups)
            foreach (var i in group)
                if (!taken[i]) { taken[i] = true; result.Add(spans[i]); }
        return result;
    }

    // ── Heading runs ────────────────────────────────────────────────────────
    private sealed class HeadingRun
    {
        public List<int> SpanIndices = new();
        public (double l, double t, double r, double b) Bbox;
    }

    private static (double l, double t, double r, double b) UnionBboxes(List<TextSpan> spans, List<int> indices)
    {
        double xmin = double.MaxValue, ymin = double.MaxValue, xmax = double.MinValue, ymax = double.MinValue;
        foreach (var i in indices)
        {
            var s = spans[i];
            xmin = Math.Min(xmin, s.Left);
            xmax = Math.Max(xmax, s.Right);
            ymin = Math.Min(ymin, s.Top);
            ymax = Math.Max(ymax, s.Bottom);
        }
        if (xmin == double.MaxValue) return (0, 0, 0, 0);
        return (xmin, ymin, xmax, ymax);
    }

    private static List<HeadingRun> FindHeadingRuns(List<TextSpan> spans)
    {
        var runs = new List<HeadingRun>();
        if (spans.Count < 2) return runs;

        var nonBoldSizes = spans.Where(s => !s.IsBold).Select(s => s.FontSize).Where(sz => sz > 0.0).ToList();
        double medianBody;
        if (nonBoldSizes.Count == 0)
        {
            var sizes = spans.Select(s => s.FontSize).Where(sz => sz > 0.0).ToList();
            if (sizes.Count == 0) return runs;
            sizes.Sort((a, b) => SafeCmp(a, b));
            medianBody = sizes[sizes.Count / 2];
        }
        else
        {
            nonBoldSizes.Sort((a, b) => SafeCmp(a, b));
            medianBody = nonBoldSizes[nonBoldSizes.Count / 2];
        }
        double headingFloor = medianBody * 1.15;
        bool IsHeadingLike(TextSpan s) => s.IsBold || s.FontSize > headingFloor;

        var order = Enumerable.Range(0, spans.Count).ToList();
        StableSort(order, (a, b) =>
        {
            int yc = SafeCmp(spans[b].Top, spans[a].Top);
            if (yc != 0) return yc;
            return SafeCmp(spans[a].Left, spans[b].Left);
        });

        const double indentTol = 6.0, fontEps = 0.5;
        var rawRuns = new List<List<int>>();
        var current = new List<int>();
        foreach (var idx in order)
        {
            var span = spans[idx];
            if (!IsHeadingLike(span))
            {
                if (current.Count > 0) { rawRuns.Add(current); current = new List<int>(); }
                continue;
            }
            if (current.Count == 0) { current.Add(idx); continue; }

            var last = spans[current[^1]];
            bool sizeOk = Math.Abs(span.FontSize - last.FontSize) <= fontEps;
            bool boldOk = span.IsBold == last.IsBold;
            bool sameLine = Math.Abs(span.Top - last.Top) <= 1.0;
            if (sizeOk && boldOk && sameLine) { current.Add(idx); continue; }

            double lineH = Math.Max(Math.Max(Math.Max(last.Height, span.Height), last.FontSize), 1.0);
            double leadingTol = lineH * 1.5;
            double vgap = Math.Abs(last.Top - span.Top);
            bool indentOk = span.Left >= last.Left - indentTol && span.Left <= last.Left + indentTol;
            bool leadingOk = vgap <= leadingTol;
            if (sizeOk && boldOk && indentOk && leadingOk) current.Add(idx);
            else { rawRuns.Add(current); current = new List<int> { idx }; }
        }
        if (current.Count > 0) rawRuns.Add(current);

        foreach (var spanIndices in rawRuns)
        {
            if (spanIndices.Count < 2) continue;
            var distinctLines = new SortedSet<int>();
            foreach (var i in spanIndices) distinctLines.Add(RoundKey(spans[i].Top));
            if (distinctLines.Count < 2) continue;
            runs.Add(new HeadingRun { SpanIndices = spanIndices, Bbox = UnionBboxes(spans, spanIndices) });
        }
        return runs;
    }

    private static (List<TextSpan> synthetic, List<List<int>> origins) SynthesizeForPartition(
        List<TextSpan> spans, List<HeadingRun> runs)
    {
        var inRun = new int?[spans.Count];
        for (int r = 0; r < runs.Count; r++)
            foreach (var i in runs[r].SpanIndices) inRun[i] = r;

        var synthetic = new List<TextSpan>(spans.Count);
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
                var run = runs[r.Value];
                var span = spans[i];
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < run.SpanIndices.Count; k++)
                {
                    if (k > 0) sb.Append(' ');
                    sb.Append(spans[run.SpanIndices[k]].Text);
                }
                var placeholder = new TextSpan
                {
                    Text = sb.ToString(),
                    X = run.Bbox.l,
                    Y = run.Bbox.t,
                    Width = run.Bbox.r - run.Bbox.l,
                    Height = run.Bbox.b - run.Bbox.t,
                    FontSize = span.FontSize,
                    IsBold = span.IsBold,
                };
                synthetic.Add(placeholder);
                origins.Add(new List<int>(run.SpanIndices));
                emitted[r.Value] = true;
            }
            // else: later span of an already-emitted run — skip.
        }
        return (synthetic, origins);
    }

    // ── Recursive partition ──────────────────────────────────────────────────
    private static List<List<int>> PartitionIndexed(List<TextSpan> all, List<int> indices)
        => PartitionIndexedDepth(all, indices, 0);

    private static List<List<int>> PartitionIndexedDepth(List<TextSpan> all, List<int> indices, uint depth)
    {
        if (indices.Count == 0) return new List<List<int>>();
        if (indices.Count < MinSpansForSplit) return new List<List<int>> { SortIndices(all, indices) };
        if (depth >= MaxPartitionDepth) return new List<List<int>> { SortIndices(all, indices) };

        var regionKind = ClassifyRegionKind(all, indices);

        double? gutter = DetectTwoColumnProse(all, indices, regionKind);
        if (gutter is double gx)
        {
            var vsplit = FindVerticalSplitIndexed(all, indices);
            if (vsplit != null)
            {
                var res = PartitionIndexedDepth(all, vsplit.Value.Item1, depth + 1);
                res.AddRange(PartitionIndexedDepth(all, vsplit.Value.Item2, depth + 1));
                return res;
            }
            var left = indices.Where(i => all[i].Left < gx).ToList();
            var right = indices.Where(i => all[i].Left >= gx).ToList();
            if (left.Count > 0 && right.Count > 0)
            {
                var res = PartitionIndexedDepth(all, left, depth + 1);
                res.AddRange(PartitionIndexedDepth(all, right, depth + 1));
                return res;
            }
        }

        double? nGutter = DetectNarrowGutterProse(all, indices, regionKind);
        if (nGutter is double ngx)
        {
            var left = indices.Where(i => all[i].Left < ngx).ToList();
            var right = indices.Where(i => all[i].Left >= ngx).ToList();
            if (left.Count > 0 && right.Count > 0)
            {
                var res = PartitionIndexedDepth(all, left, depth + 1);
                res.AddRange(PartitionIndexedDepth(all, right, depth + 1));
                return res;
            }
        }

        if (IsSingleColumnRegion(all, indices))
            return new List<List<int>> { SortIndices(all, indices) };

        Func<List<int>, (List<int>, List<int>)?> splitH = idx => FindHorizontalSplitIndexed(all, idx);
        Func<List<int>, (List<int>, List<int>)?> splitV = idx => FindVerticalSplitIndexed(all, idx);
        var first = PreferHorizontal ? splitH : splitV;
        var second = PreferHorizontal ? splitV : splitH;

        var f = first(indices);
        if (f != null)
        {
            var res = PartitionIndexedDepth(all, f.Value.Item1, depth + 1);
            res.AddRange(PartitionIndexedDepth(all, f.Value.Item2, depth + 1));
            return res;
        }
        var s = second(indices);
        if (s != null)
        {
            var res = PartitionIndexedDepth(all, s.Value.Item1, depth + 1);
            res.AddRange(PartitionIndexedDepth(all, s.Value.Item2, depth + 1));
            return res;
        }

        return new List<List<int>> { SortIndices(all, indices) };
    }

    // ── Region classification ─────────────────────────────────────────────────
    private static RegionKind ClassifyRegionKind(List<TextSpan> all, List<int> indices)
    {
        if (indices.Count < 6) return RegionKind.Mixed;
        double xmin = double.MaxValue, xmax = double.MinValue;
        foreach (var i in indices) { xmin = Math.Min(xmin, all[i].Left); xmax = Math.Max(xmax, all[i].Right); }
        double regionWidth = xmax - xmin;
        if (regionWidth <= 10.0) return RegionKind.Mixed;

        var lines = new SortedDictionary<int, (double l, double r, int chars)>();
        foreach (var i in indices)
        {
            var s = all[i];
            int key = RoundKey(s.Top);
            int nonws = s.Text.Count(c => !char.IsWhiteSpace(c));
            if (!lines.TryGetValue(key, out var e)) e = (double.MaxValue, double.MinValue, 0);
            e.l = Math.Min(e.l, s.Left);
            e.r = Math.Max(e.r, s.Right);
            e.chars += nonws;
            lines[key] = e;
        }
        int lineCount = lines.Count;
        if (lineCount < 6) return RegionKind.Mixed;

        int totalChars = 0, narrowLines = 0, wideLines = 0;
        foreach (var (l, r, chars) in lines.Values)
        {
            totalChars += chars;
            double extent = Math.Max(r - l, 0.0);
            if (extent < regionWidth * 0.6) narrowLines++;
            else wideLines++;
        }
        double meanChars = (double)totalChars / lineCount;
        bool mostlyWide = wideLines * 2 > lineCount;
        bool mostlyNarrow = narrowLines * 2 > lineCount;
        if (meanChars > 20.0 && (mostlyWide || mostlyNarrow)) return RegionKind.Prose;

        if (meanChars <= 20.0 && ShortLineCentralCorridorProse(all, indices, xmin, regionWidth))
            return RegionKind.Prose;

        if (meanChars < 8.0) return RegionKind.Table;
        return RegionKind.Mixed;
    }

    private static bool ShortLineCentralCorridorProse(List<TextSpan> all, List<int> indices, double xmin, double regionWidth)
    {
        if (regionWidth <= 0.0) return false;
        var lines = new SortedDictionary<int, List<(double l, double r, int chars)>>();
        foreach (var i in indices)
        {
            var s = all[i];
            int key = RoundKey(s.Top);
            int nonws = s.Text.Count(c => !char.IsWhiteSpace(c));
            if (!lines.TryGetValue(key, out var list)) { list = new(); lines[key] = list; }
            list.Add((s.Left, s.Right, nonws));
        }
        int totalLines = lines.Count;
        if (totalLines == 0) return false;

        const double minGap = 6.0;
        var gapPositions = new List<double>();
        foreach (var lineSpans in lines.Values)
        {
            if (lineSpans.Count < 2) continue;
            var sorted = lineSpans.OrderBy(x => x.l, Comparer<double>.Create(SafeCmp)).ToList();
            double largestGap = 0.0, largestMid = 0.0;
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                double gap = sorted[k + 1].l - sorted[k].r;
                if (gap > largestGap) { largestGap = gap; largestMid = (sorted[k].r + sorted[k + 1].l) * 0.5; }
            }
            if (largestGap >= minGap) gapPositions.Add(largestMid);
        }
        if (gapPositions.Count == 0) return false;

        const double clusterRadius = 10.0;
        var sortedGaps = gapPositions.OrderBy(x => x, Comparer<double>.Create(SafeCmp)).ToList();
        int bestSize = 0; double bestCenter = 0.0;
        foreach (var pivot in sortedGaps)
        {
            double lo = pivot - clusterRadius, hi = pivot + clusterRadius;
            int count = 0; double sum = 0.0;
            foreach (var g in sortedGaps) if (g >= lo && g <= hi) { count++; sum += g; }
            if (count > bestSize) { bestSize = count; bestCenter = sum / count; }
        }
        if (bestSize == 0) return false;
        if (bestSize * 10 < gapPositions.Count * 7) return false;
        if (bestSize * 10 < totalLines * 6) return false;
        double gutterOffset = bestCenter - xmin;
        if (gutterOffset < regionWidth * 0.30 || gutterOffset > regionWidth * 0.70) return false;

        int leftChars = 0, rightChars = 0;
        foreach (var lineSpans in lines.Values)
            foreach (var (l, r, chars) in lineSpans)
            {
                double mid = (l + r) * 0.5;
                if (mid < bestCenter) leftChars += chars; else rightChars += chars;
            }
        int total = leftChars + rightChars;
        if (total == 0) return false;
        if (leftChars < total * 0.35 || rightChars < total * 0.35) return false;

        const double leftClusterRadius = 30.0;
        var clusters = new List<(double c, int n)>();
        foreach (var lineSpans in lines.Values)
            foreach (var (l, _, _) in lineSpans)
            {
                if (l >= bestCenter) continue;
                int idx = clusters.FindIndex(c => Math.Abs(c.c - l) <= leftClusterRadius);
                if (idx >= 0)
                {
                    double cnt = clusters[idx].n;
                    clusters[idx] = ((clusters[idx].c * cnt + l) / (cnt + 1.0), clusters[idx].n + 1);
                }
                else clusters.Add((l, 1));
            }
        int dominant = clusters.Count(c => c.n >= 2);
        if (dominant >= 3) return false;
        return true;
    }

    private static double? DetectTwoColumnProse(List<TextSpan> all, List<int> indices, RegionKind regionKind)
    {
        if (indices.Count < 8) return null;
        double xmin = double.MaxValue, xmax = double.MinValue;
        foreach (var i in indices) { xmin = Math.Min(xmin, all[i].Left); xmax = Math.Max(xmax, all[i].Right); }
        double regionWidth = xmax - xmin;
        if (regionWidth < 200.0) return null;

        var linesSpans = new SortedDictionary<int, List<(double l, double r)>>();
        foreach (var i in indices)
        {
            var s = all[i];
            int key = RoundKey(s.Top);
            if (!linesSpans.TryGetValue(key, out var list)) { list = new(); linesSpans[key] = list; }
            list.Add((s.Left, s.Right));
        }
        if (linesSpans.Count < 6) return null;

        double narrowThreshold = regionWidth * 0.6;
        const double intraGap = 10.0;
        var narrowLefts = new List<double>();
        int narrowLineCount = 0;
        foreach (var lineSpans in linesSpans.Values)
        {
            var sorted = lineSpans.OrderBy(x => x.l, Comparer<double>.Create(SafeCmp)).ToList();
            double largestGap = 0.0; int splitIdx = -1;
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                double gap = sorted[k + 1].l - sorted[k].r;
                if (gap > largestGap) { largestGap = gap; splitIdx = k; }
            }
            double lineLeft = sorted.Count > 0 ? sorted[0].l : 0.0;
            double lineRight = sorted.Count > 0 ? sorted[^1].r : 0.0;
            double lineExtent = Math.Max(lineRight - lineLeft, 0.0);

            if (splitIdx >= 0 && largestGap >= intraGap)
            {
                narrowLefts.Add(lineLeft);
                if (splitIdx + 1 < sorted.Count) narrowLefts.Add(sorted[splitIdx + 1].l);
                narrowLineCount++;
                continue;
            }
            if (lineExtent < narrowThreshold) { narrowLefts.Add(lineLeft); narrowLineCount++; }
        }
        if (narrowLineCount * 2 < linesSpans.Count) return null;

        const double clusterRadius = 30.0;
        var clusters = new List<(double c, int n)>();
        foreach (var x in narrowLefts)
        {
            int idx = clusters.FindIndex(c => Math.Abs(c.c - x) <= clusterRadius);
            if (idx >= 0)
            {
                double cnt = clusters[idx].n;
                clusters[idx] = ((clusters[idx].c * cnt + x) / (cnt + 1.0), clusters[idx].n + 1);
            }
            else clusters.Add((x, 1));
        }
        if (clusters.Count != 2) return null;
        clusters.Sort((a, b) => SafeCmp(a.c, b.c));
        var (c1x, c1n) = clusters[0];
        var (c2x, c2n) = clusters[1];
        int minCluster = Math.Max(3, narrowLefts.Count / 5);
        if (c1n < minCluster || c2n < minCluster) return null;
        double gap2 = c2x - c1x;
        if (gap2 < regionWidth * 0.30) return null;
        if (regionKind != RegionKind.Prose) return null;
        return (c1x + c2x) * 0.5;
    }

    private static double? DetectNarrowGutterProse(List<TextSpan> all, List<int> indices, RegionKind regionKind)
    {
        if (indices.Count < 24) return null;
        double xmin = double.MaxValue, xmax = double.MinValue;
        foreach (var i in indices) { xmin = Math.Min(xmin, all[i].Left); xmax = Math.Max(xmax, all[i].Right); }
        double regionWidth = xmax - xmin;
        if (regionWidth < 200.0) return null;

        var lines = new SortedDictionary<int, List<(double l, double r)>>();
        foreach (var i in indices)
        {
            var s = all[i];
            int key = RoundKey(s.Top);
            if (!lines.TryGetValue(key, out var list)) { list = new(); lines[key] = list; }
            list.Add((s.Left, s.Right));
        }
        if (lines.Count < 12) return null;

        const double minGap = 6.0;
        var gapPositions = new List<double>();
        foreach (var lineSpans in lines.Values)
        {
            if (lineSpans.Count < 2) continue;
            var sorted = lineSpans.OrderBy(x => x.l, Comparer<double>.Create(SafeCmp)).ToList();
            double largestGap = 0.0, largestMid = 0.0;
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                double gap = sorted[k + 1].l - sorted[k].r;
                if (gap > largestGap) { largestGap = gap; largestMid = (sorted[k].r + sorted[k + 1].l) * 0.5; }
            }
            if (largestGap >= minGap) gapPositions.Add(largestMid);
        }
        if (gapPositions.Count < 12) return null;

        const double clusterRadius = 10.0;
        var sortedGaps = gapPositions.OrderBy(x => x, Comparer<double>.Create(SafeCmp)).ToList();
        var prefix = new double[sortedGaps.Count + 1];
        for (int k = 0; k < sortedGaps.Count; k++) prefix[k + 1] = prefix[k] + sortedGaps[k];
        int bestSize = 0; double bestCenter = 0.0; int left = 0, right = 0;
        foreach (var pivot in sortedGaps)
        {
            while (left < sortedGaps.Count && sortedGaps[left] < pivot - clusterRadius) left++;
            while (right < sortedGaps.Count && sortedGaps[right] <= pivot + clusterRadius) right++;
            int count = right - left;
            double sum = prefix[right] - prefix[left];
            if (count > bestSize) { bestSize = count; bestCenter = sum / count; }
        }
        if (bestSize * 10 < gapPositions.Count * 7) return null;
        if (bestSize < 12) return null;
        if (bestSize * 5 < lines.Count) return null;
        double gutterOffset = bestCenter - xmin;
        if (gutterOffset < regionWidth * 0.2 || gutterOffset > regionWidth * 0.8) return null;
        if (regionKind != RegionKind.Prose) return null;
        return bestCenter;
    }

    private static bool IsSingleColumnRegion(List<TextSpan> all, List<int> indices)
    {
        if (indices.Count < 3) return false;
        double xmin = double.MaxValue, xmax = double.MinValue;
        foreach (var i in indices) { xmin = Math.Min(xmin, all[i].Left); xmax = Math.Max(xmax, all[i].Right); }
        double regionWidth = xmax - xmin;
        if (regionWidth <= 10.0) return true;

        var lines = new SortedDictionary<int, List<(double l, double r, double cr)>>();
        foreach (var i in indices)
        {
            var s = all[i];
            int key = RoundKey(s.Top);
            double charCount = Math.Max(1, s.Text.Count(c => !char.IsWhiteSpace(c)));
            double approxCw = Math.Max(s.FontSize * 0.45, 2.5);
            double coreRight = s.Left + charCount * approxCw;
            if (!lines.TryGetValue(key, out var list)) { list = new(); lines[key] = list; }
            list.Add((s.Left, s.Right, coreRight));
        }
        if (lines.Count < 3) return false;

        double maxGap = MinValleyWidth;
        var gapPositions = new List<double>();
        foreach (var lineSpans in lines.Values)
        {
            var sorted = lineSpans.OrderBy(x => x.l, Comparer<double>.Create(SafeCmp)).ToList();
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                double bboxGap = sorted[k + 1].l - sorted[k].r;
                double effectiveGap, gapEndLeft;
                if (bboxGap < 0.0) { effectiveGap = sorted[k + 1].l - sorted[k].cr; gapEndLeft = sorted[k].cr; }
                else { effectiveGap = bboxGap; gapEndLeft = sorted[k].r; }
                if (effectiveGap >= maxGap) gapPositions.Add((gapEndLeft + sorted[k + 1].l) * 0.5);
            }
        }

        bool looksCentered;
        {
            var mins = lines.Values.Select(ls => ls.Min(x => x.l)).ToList();
            if (mins.Count < 2) looksCentered = false;
            else
            {
                const double tol = 10.0;
                var sorted = mins.OrderBy(x => x, Comparer<double>.Create(SafeCmp)).ToList();
                int largest = 0;
                foreach (var a in sorted)
                {
                    int lo = LowerBound(sorted, a - tol);
                    int hi = UpperBound(sorted, a + tol);
                    largest = Math.Max(largest, hi - lo);
                }
                looksCentered = largest < mins.Count * 0.5;
            }
        }
        if (looksCentered && lines.Count <= 6) return true;

        if (gapPositions.Count > 0 && !looksCentered)
        {
            const double clusterRadius = 20.0;
            int minCluster = Math.Max(3, lines.Count / 5);
            var sortedGaps = gapPositions.OrderBy(x => x, Comparer<double>.Create(SafeCmp)).ToList();
            foreach (var pos in sortedGaps)
            {
                int lo = LowerBound(sortedGaps, pos - clusterRadius);
                int hi = UpperBound(sortedGaps, pos + clusterRadius);
                if (hi - lo >= minCluster) return false;
            }
        }

        double widthThreshold = regionWidth * 0.6;
        int wideDense = 0;
        foreach (var lineSpans in lines.Values)
        {
            var sorted = lineSpans.OrderBy(x => x.l, Comparer<double>.Create(SafeCmp)).ToList();
            double extentLeft = sorted[0].l;
            double extentRight = sorted.Max(x => x.r);
            double extent = extentRight - extentLeft;
            if (extent < widthThreshold) continue;
            double covered = 0.0, lastEnd = double.MinValue;
            foreach (var (l, _, cr) in sorted)
            {
                double effRight = Math.Min(cr, extentRight);
                double start = Math.Max(l, lastEnd);
                if (effRight > start) { covered += effRight - start; lastEnd = effRight; }
            }
            if (covered >= extent * 0.8) wideDense++;
        }
        return wideDense * 2 >= lines.Count;
    }

    // partition_point equivalents on a sorted (SafeCmp) list.
    private static int LowerBound(List<double> sorted, double v)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int m = (lo + hi) / 2; if (sorted[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }
    private static int UpperBound(List<double> sorted, double v)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int m = (lo + hi) / 2; if (sorted[m] <= v) lo = m + 1; else hi = m; }
        return lo;
    }

    // ── Splits ────────────────────────────────────────────────────────────────
    private static (List<int>, List<int>)? FindHorizontalSplitIndexed(List<TextSpan> all, List<int> indices)
    {
        var profile = HorizontalProjectionIndexed(all, indices);
        if (profile is null) return null;
        var p = profile.Value;

        double splitX;
        var valley = FindValley(p);
        if (valley != null)
        {
            var (vs, ve, vw) = valley.Value;
            if (vw < MinValleyWidth) return null;
            splitX = p.XMin + (vs + ve) / 2.0;
        }
        else
        {
            var sp = FindSplitBetweenPeaks(p);
            if (sp is null) return null;
            splitX = sp.Value;
        }

        const double minResultWidth = 60.0;
        double lxmin = double.MaxValue, lxmax = double.MinValue, rxmin = double.MaxValue, rxmax = double.MinValue;
        foreach (var i in indices)
        {
            double l = all[i].Left, r = all[i].Right;
            if (l < splitX) { lxmin = Math.Min(lxmin, l); lxmax = Math.Max(lxmax, r); }
            else { rxmin = Math.Min(rxmin, l); rxmax = Math.Max(rxmax, r); }
        }
        double leftW = lxmax - lxmin, rightW = rxmax - rxmin;
        if (leftW < minResultWidth || rightW < minResultWidth) return null;

        var left = indices.Where(i => all[i].Left < splitX).ToList();
        var right = indices.Where(i => all[i].Left >= splitX).ToList();
        if (left.Count == 0 || right.Count == 0) return null;

        int minSide = Math.Max(indices.Count / 10, 2);
        if (left.Count < minSide || right.Count < minSide) return null;

        double rightXMax = double.MinValue, maxFont = 0.0;
        foreach (var i in right) { rightXMax = Math.Max(rightXMax, all[i].Right); maxFont = Math.Max(maxFont, Math.Abs(all[i].Height)); }
        double overlapTol = Math.Max(maxFont, 10.0);
        int fullWidthLeftRows = left.Count(i =>
        {
            var s = all[i];
            double nonws = Math.Max(1, s.Text.Count(c => !char.IsWhiteSpace(c)));
            double approxCw = Math.Max(s.FontSize * 0.45, 2.5);
            return s.Left + nonws * approxCw >= rightXMax - overlapTol;
        });
        if (fullWidthLeftRows >= 3) return null;

        return (left, right);
    }

    private static double? FindSplitBetweenPeaks(ProjectionProfile profile)
    {
        var density = profile.Density;
        int n = density.Length;
        if (n < 3) return null;
        int smoothWindow = Math.Max((int)MinValleyWidth, 3);
        int half = smoothWindow / 2;
        var smoothed = new double[n];
        for (int i = 0; i < n; i++)
        {
            int s = Math.Max(0, i - half);
            int e = Math.Min(i + half + 1, n);
            double sum = 0; for (int j = s; j < e; j++) sum += density[j];
            smoothed[i] = sum / (e - s);
        }
        int mid = n / 2;
        if (mid <= 0 || mid >= n) return null;
        // Strongest peak in each half, the last of equally strong ones (`max_by`); the trough
        // between them below keeps the first of equal minima (`min_by`).
        int leftPeak = 0; for (int i = 1; i < mid; i++) if (SafeCmp(smoothed[i], smoothed[leftPeak]) >= 0) leftPeak = i;
        int rightPeak = mid; for (int i = mid + 1; i < n; i++) if (SafeCmp(smoothed[i], smoothed[rightPeak]) >= 0) rightPeak = i;
        if (smoothed[leftPeak] == 0.0 || smoothed[rightPeak] == 0.0) return null;

        int searchStart = Math.Min(leftPeak, rightPeak) + 1;
        int searchEnd = Math.Max(leftPeak, rightPeak);
        if (searchStart >= searchEnd) return null;
        int trough = searchStart;
        for (int i = searchStart + 1; i < searchEnd; i++) if (SafeCmp(smoothed[i], smoothed[trough]) < 0) trough = i;
        double weaker = Math.Min(smoothed[leftPeak], smoothed[rightPeak]);
        if (smoothed[trough] > weaker * 0.5) return null;
        if (trough < (int)MinValleyWidth || trough + (int)MinValleyWidth > n) return null;
        return profile.XMin + trough;
    }

    private static (List<int>, List<int>)? FindVerticalSplitIndexed(List<TextSpan> all, List<int> indices)
    {
        var profile = VerticalProjectionIndexed(all, indices);
        if (profile is null) return null;
        var p = profile.Value;
        var valley = FindValley(p);
        if (valley is null) return null;
        var (vs, ve, vw) = valley.Value;
        if (vw < MinValleyWidth) return null;
        double splitY = p.YMin + (vs + ve) / 2.0;

        var above = indices.Where(i => all[i].Top >= splitY).ToList();
        var below = indices.Where(i => all[i].Top < splitY).ToList();
        if (above.Count == 0 || below.Count == 0) return null;
        int minSide = Math.Max(indices.Count / 10, 1);
        if (above.Count < minSide || below.Count < minSide) return null;
        return (above, below);
    }

    // ── Projections & valley ──────────────────────────────────────────────────
    private struct ProjectionProfile
    {
        public double[] Density;
        public double XMin;
        public double YMin;
    }

    private static ProjectionProfile? HorizontalProjectionIndexed(List<TextSpan> all, List<int> indices)
    {
        if (indices.Count == 0) return null;
        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        foreach (var i in indices)
        {
            var s = all[i];
            xmin = Math.Min(xmin, s.Left); xmax = Math.Max(xmax, s.Right);
            ymin = Math.Min(ymin, s.Top); ymax = Math.Max(ymax, s.Bottom);
        }
        int width = (int)Math.Ceiling(xmax - xmin);
        if (width > MaxProjectionSize) return null;
        if (width <= 0) width = 0;
        var density = new double[Math.Max(width, 0)];
        double regionWidth = Math.Max(xmax - xmin, 1.0);
        foreach (var i in indices)
        {
            var s = all[i];
            double height = s.Bottom - s.Top;
            int charCount = Math.Max(1, s.Text.Count(c => !char.IsWhiteSpace(c)));
            double approxCw = Math.Max(s.FontSize * 0.45, 2.5);
            double coreWidth = charCount * approxCw;
            double spanWidth = s.Right - s.Left;
            if (spanWidth > regionWidth * 0.55) continue;
            if (charCount < 2) continue;
            double coreLeft = s.Left;
            double coreRight = Math.Min(coreLeft + coreWidth, s.Right);
            int xStart = (int)Math.Ceiling(Math.Max(coreLeft - xmin, 0.0));
            int xEnd = (int)Math.Ceiling(coreRight - xmin);
            for (int j = xStart; j < Math.Min(xEnd, width); j++) density[j] += height;
        }
        return new ProjectionProfile { Density = density, XMin = xmin, YMin = ymin };
    }

    private static ProjectionProfile? VerticalProjectionIndexed(List<TextSpan> all, List<int> indices)
    {
        if (indices.Count == 0) return null;
        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        foreach (var i in indices)
        {
            var s = all[i];
            xmin = Math.Min(xmin, s.Left); xmax = Math.Max(xmax, s.Right);
            ymin = Math.Min(ymin, s.Top); ymax = Math.Max(ymax, s.Bottom);
        }
        int height = (int)Math.Ceiling(ymax - ymin);
        if (height > MaxProjectionSize) return null;
        if (height <= 0) height = 0;
        var density = new double[Math.Max(height, 0)];
        foreach (var i in indices)
        {
            var s = all[i];
            int yStart = (int)Math.Ceiling(Math.Max(s.Top - ymin, 0.0));
            int yEnd = (int)Math.Ceiling(s.Bottom - ymin);
            double w = s.Right - s.Left;
            for (int j = yStart; j < Math.Min(yEnd, height); j++) density[j] += w;
        }
        return new ProjectionProfile { Density = density, XMin = xmin, YMin = ymin };
    }

    private static (int start, int end, double width)? FindValley(ProjectionProfile profile)
    {
        var density = profile.Density;
        if (density.Length == 0) return null;
        double peak = 0.0; foreach (var d in density) peak = Math.Max(peak, d);
        if (peak == 0.0) return null;

        int firstNonzero = -1, lastNonzero = -1;
        for (int i = 0; i < density.Length; i++) if (density[i] > 0.0) { firstNonzero = i; break; }
        for (int i = density.Length - 1; i >= 0; i--) if (density[i] > 0.0) { lastNonzero = i; break; }
        if (firstNonzero < 0 || lastNonzero < 0) return null;

        double threshold = peak * ValleyThreshold;
        var valleys = new List<(int, int)>();
        bool inValley = false; int valleyStart = 0;
        for (int i = 0; i < density.Length; i++)
        {
            if (density[i] < threshold) { if (!inValley) { valleyStart = i; inValley = true; } }
            else if (inValley) { valleys.Add((valleyStart, i)); inValley = false; }
        }
        if (inValley) valleys.Add((valleyStart, density.Length));

        int bridgeLimit = (int)Math.Ceiling(MinValleyWidth / 2.0);
        var interior = valleys.Where(v => v.Item1 > firstNonzero && v.Item2 <= lastNonzero + 1).ToList();
        var merged = new List<(int s, int e)>();
        foreach (var seg in interior)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (seg.Item1 <= last.e + bridgeLimit) { merged[^1] = (last.s, Math.Max(last.e, seg.Item2)); continue; }
            }
            merged.Add((seg.Item1, seg.Item2));
        }
        if (merged.Count == 0) return null;
        // Widest valley wins, and the LAST of equally wide ones: `Iterator::max_by` keeps the
        // last maximum, and on a page whose gutter ties with an interior gap the choice
        // decides which side of the region the column cut lands on.
        (int s, int e, double w)? best = null;
        foreach (var (s, e) in merged)
        {
            double w = e - s;
            if (best is null || SafeCmp(w, best.Value.w) >= 0) best = (s, e, w);
        }
        return best;
    }

    private static List<int> SortIndices(List<TextSpan> all, List<int> indices)
    {
        var sorted = new List<int>(indices);
        StableSort(sorted, (a, b) =>
        {
            int yc = SafeCmp(all[b].Top, all[a].Top);
            if (yc != 0) return yc;
            return SafeCmp(all[a].Left, all[b].Left);
        });
        return sorted;
    }
}
