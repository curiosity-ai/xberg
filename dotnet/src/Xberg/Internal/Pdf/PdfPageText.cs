// Assembles positioned spans into page text, mirroring the xberg oxide layer
// (crates/xberg/src/pdf/oxide/text.rs :: extract_page_text_column_aware) and the
// control-char cleanup in crates/xberg/src/pdf/text.rs.
//
// Reading order is pdf_oxide's ReadingOrder::ColumnAware (XYCutStrategy), ported
// in PdfReadingOrder. Assembly then walks the ordered spans inserting spaces and
// line/paragraph breaks from span geometry, with a fragmentation-repair pass for
// glyph-per-BT/ET PDFs (issue #962).
using System.Text;

namespace Xberg.Internal.Pdf;

public static class PdfPageText
{
    // Fragmentation-repair thresholds (crates/xberg/src/pdf/structure/constants.rs).
    private const double MaxGlyphJitterPt = 5.0;
    private const int MinDisorderCount = 3;
    private const double CoalesceThreshold = 5.0;

    /// <summary>Sort spans into ColumnAware reading order and assemble into text.</summary>
    public static string Assemble(List<TextSpan> spans) => AssembleOrdered(OrderViaRuns(spans));

    /// <summary>Assemble + line segments from ONE ordering pass (the ordering —
    /// presort, run merge, XY-cut — dominates cost on large documents).</summary>
    public static (string text, List<LineSeg> lines) AssembleWithLines(List<TextSpan> spans)
    {
        var ordering = OrderViaRuns(spans);
        return (AssembleOrdered(ordering), BuildLineSegmentsOrdered(ordering));
    }

    private static string AssembleOrdered((List<TextSpan> ordered, List<TextSpan> orderedRuns) t)
    {
        var (ordered, orderedRuns) = t;
        if (ordered.Count == 0) return "";

        // Issue #962: glyph-fragmented span lists are rebuilt from positions.
        // Detected on the MERGED runs (word-level), matching pdf_oxide which runs
        // this on merged spans — running it on raw per-glyph spans false-positives.
        if (IsFragmentedSpanList(orderedRuns))
            return RebuildTextFromFragmentedSpans(ordered);

        // Median line height for paragraph-break detection.
        var heights = ordered.Select(s => s.Height).OrderBy(h => h, Comparer<double>.Create((a, b) => a.CompareTo(b))).ToList();
        double medianHeight = heights.Count == 0 ? 1.0 : heights[heights.Count / 2];
        double paragraphGap = medianHeight * 1.5;

        var sb = new StringBuilder();
        TextSpan? prev = null;
        foreach (var span in ordered)
        {
            if (prev != null)
            {
                double prevEndX = prev.X + prev.Width;
                double yGap = Math.Abs(prev.Y - span.Y);
                double effHeight = Math.Max(Math.Max(span.Height, prev.Height), span.FontSize * 0.5);
                bool sameLine = yGap < effHeight * 0.5;
                if (sameLine)
                {
                    double xGap = span.X - prevEndX;
                    if (xGap > span.FontSize * 0.15 && !SuppressGapSpace(sb, span.Text, xGap, span.FontSize))
                        sb.Append(' ');
                }
                else if (yGap > paragraphGap) sb.Append("\n\n");
                else sb.Append('\n');
            }
            sb.Append(span.Text);
            prev = span;
        }
        return sb.ToString();
    }

    // Build line-merged runs (approximating pdf_oxide's TextExtractor::merge_adjacent_spans,
    // extractors/text.rs), order the runs with XY-cut, then flatten back to the original
    // spans in run order. Runs are used ONLY for reading-order geometry; assembly still
    // walks the original spans so spacing/line-break decisions are unchanged.
    private static (List<TextSpan> ordered, List<TextSpan> orderedRuns) OrderViaRuns(List<TextSpan> spans)
    {
        // pdf_oxide sorts spans into reading order (rounded-Y descending, X
        // ascending, column-aware) BEFORE merge_adjacent_spans (extractors/
        // text.rs :: sort_spans_by_reading_order). Without this, show-ops that
        // arrive out of visual order (TJ negative offsets, right-aligned
        // columns) merge/emit in stream order and scramble within-line text.
        spans = SortSpansByReadingOrder(spans);

        var runs = new List<TextSpan>();
        var members = new List<List<int>>();
        TextSpan? cur = null;
        List<int>? curMembers = null;

        for (int i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (cur is null)
            {
                cur = CloneRun(s);
                curMembers = new List<int> { i };
                continue;
            }
            // pdf_oxide merge gate: same baseline line and gap within the
            // font-size-aware column-boundary threshold. column_threshold =
            // max(column_boundary_threshold_pt=5.0, max(fontsize)*0.5).
            double yDiff = Math.Abs(s.Y - cur.Y);
            bool sameLine = yDiff < 1.0;
            double gap = s.X - (cur.X + cur.Width);
            double fontRef = Math.Max(cur.FontSize, s.FontSize);
            double columnThreshold = Math.Max(5.0, fontRef * 0.5);
            bool merge = sameLine && gap >= -0.5 && gap <= columnThreshold;

            if (merge)
            {
                double newRight = Math.Max(cur.X + cur.Width, s.X + s.Width);
                cur.X = Math.Min(cur.X, s.X);
                cur.Width = newRight - cur.X;
                cur.Height = Math.Max(cur.Height, s.Height);
                cur.FontSize = Math.Max(cur.FontSize, s.FontSize);
                cur.IsBold = cur.IsBold || s.IsBold;
                cur.Text += s.Text;
                curMembers!.Add(i);
            }
            else
            {
                runs.Add(cur);
                members.Add(curMembers!);
                cur = CloneRun(s);
                curMembers = new List<int> { i };
            }
        }
        if (cur != null) { runs.Add(cur); members.Add(curMembers!); }

        // Map run object -> members, order runs, flatten.
        var memberOf = new Dictionary<TextSpan, List<int>>(ReferenceEqualityComparer.Instance);
        for (int r = 0; r < runs.Count; r++) memberOf[runs[r]] = members[r];

        var orderedRuns = PdfReadingOrder.Order(runs);
        var result = new List<TextSpan>(spans.Count);
        foreach (var run in orderedRuns)
        {
            var mem = memberOf[run];
            // Emit member spans left-to-right (assembly reads them in this order).
            if (mem.Count > 1)
            {
                var sortedMem = mem.OrderBy(idx => spans[idx].X).ToList();
                foreach (var idx in sortedMem) result.Add(spans[idx]);
            }
            else result.Add(spans[mem[0]]);
        }
        return (result, orderedRuns);
    }

    /// <summary>Return spans in ColumnAware (XY-cut) reading order — the ordering the
    /// structure/heading pipeline consumes (mirrors pdf_oxide ReadingOrder::ColumnAware).</summary>
    public static List<TextSpan> OrderColumnAware(List<TextSpan> spans)
    {
        if (spans.Count == 0) return new List<TextSpan>();
        var (ordered, _) = OrderViaRuns(spans);
        return ordered;
    }

    /// <summary>One assembled visual line with aggregate font metrics.</summary>
    public sealed class LineSeg
    {
        public string Text = "";
        public double X, Y, Width, Height, FontSize;
        public bool IsBold, IsItalic, IsMonospace;
    }

    /// <summary>Assemble a page's spans into visual lines with the SAME reading-order and
    /// intra-line spacing rules as <see cref="Assemble"/> (including the glyph-fragmentation
    /// rebuild). Feeds the structure/heading pipeline so its text matches the plain path.</summary>
    public static List<LineSeg> BuildLineSegments(List<TextSpan> spans)
        => BuildLineSegmentsOrdered(OrderViaRuns(spans));

    private static List<LineSeg> BuildLineSegmentsOrdered((List<TextSpan> ordered, List<TextSpan> orderedRuns) t)
    {
        var (ordered, orderedRuns) = t;
        var lines = new List<LineSeg>();
        if (ordered.Count == 0) return lines;

        // Fragmented glyph lists: rebuild lines from positions (issue #962).
        if (IsFragmentedSpanList(orderedRuns))
        {
            var sorted = ordered.OrderByDescending(s => s.Y, Comparer<double>.Create((a, b) => a.CompareTo(b))).ToList();
            var groups = new List<List<TextSpan>>();
            foreach (var span in sorted)
            {
                bool belongs = groups.Count > 0 && Math.Abs(span.Y - groups[^1][^1].Y) <= CoalesceThreshold;
                if (belongs) groups[^1].Add(span);
                else groups.Add(new List<TextSpan> { span });
            }
            foreach (var group in groups)
            {
                group.Sort((a, b) => a.X.CompareTo(b.X));
                lines.Add(AssembleLine(group, byXThreshold: true));
            }
            return lines;
        }

        // Normal path: group consecutive same-line spans (mirrors Assemble's line logic).
        var cur = new List<TextSpan>();
        TextSpan? prev = null;
        foreach (var span in ordered)
        {
            if (prev != null)
            {
                double yGap = Math.Abs(prev.Y - span.Y);
                double effHeight = Math.Max(Math.Max(span.Height, prev.Height), span.FontSize * 0.5);
                bool sameLine = yGap < effHeight * 0.5;
                if (!sameLine) { lines.Add(AssembleLine(cur, byXThreshold: false)); cur = new List<TextSpan>(); }
            }
            cur.Add(span);
            prev = span;
        }
        if (cur.Count > 0) lines.Add(AssembleLine(cur, byXThreshold: false));
        return lines;
    }

    private static LineSeg AssembleLine(List<TextSpan> group, bool byXThreshold)
    {
        var sb = new StringBuilder();
        double minX = double.MaxValue, maxRight = double.MinValue, maxHeight = 0, maxFont = 0;
        int boldCount = 0, italicCount = 0, count = 0; bool allMono = true;
        double prevEndX = double.NegativeInfinity;
        double fontSize = group.Count == 0 ? 0 : group.Max(s => s.FontSize);
        foreach (var s in group)
        {
            if (!double.IsNegativeInfinity(prevEndX))
            {
                double gap = s.X - prevEndX;
                double threshold = byXThreshold ? fontSize * 0.5 : s.FontSize * 0.15;
                if (gap > threshold && !SuppressGapSpace(sb, s.Text, gap, s.FontSize))
                    sb.Append(' ');
            }
            // Dedupe literal space across span boundary (pdf_oxide merge guard).
            sb.Append(s.Text);
            prevEndX = s.X + s.Width;
            minX = Math.Min(minX, s.X);
            maxRight = Math.Max(maxRight, s.X + s.Width);
            maxHeight = Math.Max(maxHeight, s.Height);
            maxFont = Math.Max(maxFont, s.FontSize);
            if (s.IsBold) boldCount++;
            if (s.IsItalic) italicCount++;
            allMono &= s.IsMonospace;
            count++;
        }
        double y = group.Count > 0 ? group[0].Y : 0;
        return new LineSeg
        {
            Text = sb.ToString(),
            X = minX == double.MaxValue ? 0 : minX,
            Y = y,
            Width = (maxRight == double.MinValue ? 0 : maxRight) - (minX == double.MaxValue ? 0 : minX),
            Height = maxHeight,
            FontSize = maxFont,
            IsBold = boldCount * 2 > count,
            IsItalic = italicCount * 2 > count,
            IsMonospace = count > 0 && allMono,
        };
    }

    private static TextSpan CloneRun(TextSpan s) => new TextSpan
    {
        Text = s.Text, X = s.X, Y = s.Y, Width = s.Width, Height = s.Height,
        FontSize = s.FontSize, IsBold = s.IsBold, IsItalic = s.IsItalic, IsMonospace = s.IsMonospace,
    };

    // Port of is_fragmented_span_list (oxide/text.rs).
    private static bool IsFragmentedSpanList(List<TextSpan> spans)
    {
        int disorder = 0;
        for (int i = 0; i + 1 < spans.Count; i++)
        {
            var prev = spans[i];
            var cur = spans[i + 1];
            if (prev.Text.Length > 3 || cur.Text.Length > 3) continue;
            double yGap = Math.Abs(prev.Y - cur.Y);
            double effHeight = Math.Max(prev.Height, cur.Height);
            bool sameLine = effHeight > 0.0 ? yGap < effHeight * 0.5 : yGap <= MaxGlyphJitterPt;
            if (sameLine && cur.X < prev.X - prev.FontSize)
            {
                disorder++;
                if (disorder >= MinDisorderCount) return true;
            }
        }
        return false;
    }

    // Port of rebuild_text_from_fragmented_spans (oxide/text.rs).
    private static string RebuildTextFromFragmentedSpans(List<TextSpan> spans)
    {
        if (spans.Count == 0) return "";
        var sorted = spans.OrderByDescending(s => s.Y, Comparer<double>.Create((a, b) => a.CompareTo(b))).ToList();

        var groups = new List<List<TextSpan>>();
        foreach (var span in sorted)
        {
            bool belongs = groups.Count > 0 &&
                Math.Abs(span.Y - groups[^1][^1].Y) <= CoalesceThreshold;
            if (belongs) groups[^1].Add(span);
            else groups.Add(new List<TextSpan> { span });
        }

        var sb = new StringBuilder();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            group.Sort((a, b) => a.X.CompareTo(b.X));
            if (gi > 0) sb.Append('\n');
            double fontSize = group.Count == 0 ? 0.0 : group.Max(s => s.FontSize);
            double spaceThreshold = fontSize * 0.5;
            double prevEndX = double.NegativeInfinity;
            foreach (var span in group)
            {
                if (!double.IsInfinity(prevEndX) && span.X - prevEndX > spaceThreshold &&
                    !SuppressGapSpace(sb, span.Text, span.X - prevEndX, fontSize))
                    sb.Append(' ');
                sb.Append(span.Text);
                prevEndX = span.X + span.Width;
            }
        }
        return sb.ToString();
    }

    // ── Reading-order pre-sort (pdf_oxide extractors/text.rs) ────────────────
    // Port of TextExtractor::sort_spans_by_reading_order + detect_span_columns
    // + simple_sort_spans + sort_spans_by_columns. Returns a NEW sorted list
    // (the caller's list is left untouched).
    private static int SafeCmp(double a, double b)
    {
        bool na = double.IsNaN(a), nb = double.IsNaN(b);
        if (na && nb) return 0;
        if (na) return 1;
        if (nb) return -1;
        return a.CompareTo(b);
    }

    private static int RoundKey(double v) =>
        double.IsNaN(v) ? int.MinValue : (int)Math.Round(v, MidpointRounding.AwayFromZero);

    internal static List<TextSpan> SortSpansByReadingOrder(List<TextSpan> spans)
    {
        if (spans.Count == 0) return new List<TextSpan>();
        var columns = DetectSpanColumns(spans);
        if (columns.Count <= 1)
            return SortedByYThenX(spans);

        // Multi-column: assign each span to a column by center-x, sort within
        // each column, read columns left-to-right.
        var columnSpans = new List<TextSpan>[columns.Count];
        for (int c = 0; c < columns.Count; c++) columnSpans[c] = new List<TextSpan>();
        foreach (var span in spans)
        {
            double cx = span.X + span.Width / 2.0;
            int idx = 0;
            for (int c = 0; c < columns.Count; c++)
                if (cx >= columns[c].left && cx <= columns[c].right) { idx = c; break; }
            columnSpans[idx].Add(span);
        }
        var result = new List<TextSpan>(spans.Count);
        foreach (var col in columnSpans)
            result.AddRange(SortedByYThenX(col));
        return result;
    }

    // Stable sort by rounded Y descending, then X ascending (Rust sort_by is stable).
    private static List<TextSpan> SortedByYThenX(List<TextSpan> spans) =>
        spans
            .Select((s, i) => (s, i))
            .OrderBy(t => t, Comparer<(TextSpan s, int i)>.Create((x, y) =>
            {
                // Non-finite Y sorts after all numbers (pdf_oxide NaN handling).
                bool xa = double.IsFinite(x.s.Y), yb = double.IsFinite(y.s.Y);
                if (!xa || !yb)
                {
                    int c0 = xa == yb ? 0 : (xa ? -1 : 1);
                    return c0 != 0 ? c0 : x.i.CompareTo(y.i);
                }
                int c = RoundKey(y.s.Y).CompareTo(RoundKey(x.s.Y));
                if (c == 0) c = SafeCmp(x.s.X, y.s.X);
                return c != 0 ? c : x.i.CompareTo(y.i);
            }))
            .Select(t => t.s)
            .ToList();

    // Port of detect_span_columns (X-histogram gap detection).
    private static List<(double left, double right)> DetectSpanColumns(List<TextSpan> spans)
    {
        var columns = new List<(double, double)>();
        if (spans.Count == 0) return columns;

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        foreach (var s in spans)
        {
            minX = Math.Min(minX, s.X);
            maxX = Math.Max(maxX, s.X + s.Width);
        }
        double pageWidth = maxX - minX;
        if (!(pageWidth > 0.0) || !double.IsFinite(pageWidth))
        {
            columns.Add((minX, maxX));
            return columns;
        }

        const int bins = 100;
        double binWidth = pageWidth / bins;
        var histogram = new int[bins];
        foreach (var s in spans)
        {
            double sb = (s.X - minX) / binWidth;
            double eb = (s.X + s.Width - minX) / binWidth;
            if (double.IsNaN(sb) || double.IsNaN(eb)) continue;
            int startBin = (int)Math.Clamp(sb, 0, bins - 1);
            int endBin = (int)Math.Clamp(eb, 0, bins - 1);
            for (int i = startBin; i <= endBin; i++) histogram[i]++;
        }

        double avgDensity = histogram.Sum() / (double)bins;
        double gapThreshold = Math.Max(avgDensity * 0.2, 1.0);

        var gaps = new List<double>();
        bool inGap = false; int gapStart = 0;
        for (int i = 0; i < bins; i++)
        {
            if (histogram[i] <= gapThreshold)
            {
                if (!inGap) { gapStart = i; inGap = true; }
            }
            else if (inGap)
            {
                double gapWidth = (i - gapStart) * binWidth;
                if (gapWidth > Math.Max(pageWidth * 0.02, 15.0))
                    gaps.Add(minX + gapStart * binWidth);
                inGap = false;
            }
        }

        if (gaps.Count == 0) { columns.Add((minX, maxX)); return columns; }

        double left = minX;
        foreach (var gx in gaps) { columns.Add((left, gx)); left = gx; }
        columns.Add((left, maxX));
        return columns;
    }

    // Decide whether to suppress a synthesized gap-space at a span boundary.
    //
    // pdf_oxide applies has_boundary_space (extractors/text.rs:1622) — "space
    // already present when preceding ends OR following starts with whitespace"
    // — only INSIDE merge_adjacent_spans, i.e. for spans close enough to merge
    // into one run. The xberg join layer that this method mirrors
    // (extract_page_text_column_aware) then joins the already-merged runs with a
    // plain `x_gap > font_size*0.15 -> push(' ')` rule and NO boundary guard.
    //
    // Since our assembly walks the original (pre-merge) spans, we reproduce both
    // tiers here: within the merge regime (small gap) a one-sided boundary space
    // suppresses the synthetic space (justified prose "eos  et" -> "eos et");
    // beyond it (large inter-run gap) only a two-sided literal+literal collision
    // is suppressed, so list-bullet indents keep their gap space ("•  IBM").
    private static bool SuppressGapSpace(StringBuilder preceding, string following, double xGap, double fontSize)
    {
        bool prevSpace = preceding.Length > 0 && char.IsWhiteSpace(preceding[^1]);
        bool nextSpace = following.Length > 0 && char.IsWhiteSpace(following[0]);
        if (!prevSpace && !nextSpace) return false;
        if (prevSpace && nextSpace) return true;
        double mergeThreshold = Math.Max(5.0, fontSize * 0.5);
        return xGap <= mergeThreshold;
    }

    /// <summary>Port of fix_pdf_control_chars (crates/xberg/src/pdf/text.rs).</summary>
    public static string FixControlChars(string text)
    {
        bool has = false;
        foreach (char c in text)
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') { has = true; break; }
        if (!has) return text;

        var chars = text.ToCharArray();
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < chars.Length; i++)
        {
            char ch = chars[i];
            if (ch == (char)0x02) continue;               // STX: dropped (ambiguous)
            if (ch == (char)0x03)                         // ETX: "ft" ligature after a letter
            {
                bool prevAlpha = i > 0 && char.IsLetter(chars[i - 1]);
                if (prevAlpha) sb.Append("ft");
                continue;
            }
            if (ch >= (char)0x01 && ch <= (char)0x1F && ch != '\t' && ch != '\n' && ch != '\r')
                continue;                                 // residual C0 control -> dropped
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
