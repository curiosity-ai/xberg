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
    public static string Assemble(List<TextSpan> spans)
    {
        if (spans.Count == 0) return "";

        // Column-aware reading order (XY-cut), matching pdf_oxide. Run on
        // line-merged runs so per-glyph/per-Tj span granularity does not create
        // spurious column gutters (pdf_oxide runs XY-cut on merged word/line
        // spans, not raw glyphs). Ordering is applied to runs; the original
        // spans are then emitted in run order and assembled as before, so the
        // space/line-break logic is unchanged.
        var (ordered, orderedRuns) = OrderViaRuns(spans);

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
                    if (xGap > span.FontSize * 0.15) sb.Append(' ');
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
                if (!double.IsInfinity(prevEndX) && span.X - prevEndX > spaceThreshold) sb.Append(' ');
                sb.Append(span.Text);
                prevEndX = span.X + span.Width;
            }
        }
        return sb.ToString();
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
