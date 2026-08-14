namespace Xberg.Internal.Pdf;

/// <summary>
/// What a block of spans looks like, for the column-reorder gates.
/// <para>
/// The gates admit <see cref="Prose"/> / <see cref="Reference"/> (reorder column-major) and
/// reject <see cref="Table"/> / <see cref="Form"/> (leave row-major handling alone).
/// <see cref="Mixed"/> means "not confidently any of the above" — callers fall back to their
/// prior behaviour.
/// </para>
/// </summary>
internal enum RegionClass
{
    /// <summary>Tall stack of wide lines, or narrow column lines carrying substantial prose
    /// per line. Safe to reorder column-major.</summary>
    Prose,
    /// <summary>Ragged reference/bibliography column: numbered entries or hanging-indent
    /// entries. Reordered column-major like prose.</summary>
    Reference,
    /// <summary>Short cells in a grid. A tight column reorder here corrupts cell ordering.</summary>
    Table,
    /// <summary>Label/value rows — a large intra-line gap with text on both sides.</summary>
    Form,
    /// <summary>Too few lines, mixed shapes, or otherwise not confidently classifiable.</summary>
    Mixed,
}

/// <summary>
/// Ports pdf_oxide's <c>layout::classify_region</c>. A pure read: returns
/// <see cref="RegionClass.Mixed"/> whenever the block is too small or the shape is ambiguous,
/// so callers safely fall back to prior behaviour.
/// </summary>
internal static class PdfRegionClassifier
{
    /// <summary>True for the classes the column-reorder gates accept.</summary>
    public static bool IsReorderableColumn(this RegionClass c) =>
        c is RegionClass.Prose or RegionClass.Reference;

    private sealed class LineStat
    {
        public double Top;
        public double Left;
        public double Right;
        /// <summary>Non-whitespace character count across all spans on the line.</summary>
        public int NonWsChars;
        /// <summary>Trimmed text of the leftmost span (for the numbered-entry shape).</summary>
        public string LeadText = "";
        public List<double> SpanLefts = new();
        public List<double> SpanRights = new();
    }

    public static RegionClass Classify(IReadOnlyList<TextSpan> spans, IReadOnlyList<int> indices)
    {
        // Cheap shape guards.
        if (indices.Count < 6) return RegionClass.Mixed;

        double xMin = double.MaxValue, xMax = double.MinValue;
        foreach (int i in indices)
        {
            xMin = Math.Min(xMin, spans[i].Left);
            xMax = Math.Max(xMax, spans[i].Right);
        }
        double regionWidth = xMax - xMin;
        if (regionWidth <= 10.0) return RegionClass.Mixed;

        double medH = Math.Max(MedianHeight(spans, indices), 1.0);
        var lines = ClusterLines(spans, indices, medH);
        int lineCount = lines.Count;
        // Headings, captions, single paragraphs — leave to default behaviour.
        if (lineCount < 6) return RegionClass.Mixed;

        int totalChars = 0, wideLines = 0, numberedLines = 0, formLines = 0;
        var leftEdges = new List<double>(lineCount);
        foreach (var l in lines)
        {
            totalChars += l.NonWsChars;
            double extent = Math.Max(l.Right - l.Left, 0.0);
            if (extent >= regionWidth * 0.6) wideLines++;
            if (StartsNumberedEntry(l.LeadText)) numberedLines++;
            if (LineHasLabelValueGap(l, regionWidth)) formLines++;
            leftEdges.Add(l.Left);
        }

        double meanChars = (double)totalChars / lineCount;
        bool mostlyWide = wideLines * 2 > lineCount;
        double numberedFrac = (double)numberedLines / lineCount;
        double formFrac = (double)formLines / lineCount;

        // Decision ladder, specific → general. Table and Form both mean "do not reorder as
        // prose", so a fuzzy boundary between them is harmless — only the {Prose, Reference}
        // vs {Table, Form} split is load-bearing.

        // TABLE: short content per line. Prose and reference columns always carry substantial
        // text per line, so they never fall this low.
        if (meanChars < 10.0) return RegionClass.Table;

        // FORM: many lines are label … value rows.
        if (formFrac >= 0.4) return RegionClass.Form;

        // REFERENCE: numbered entries or a hanging indent, with enough text to exclude cells.
        if (meanChars > 12.0 && (numberedFrac >= 0.3 || HasHangingIndent(leftEdges, medH)))
            return RegionClass.Reference;

        // PROSE: a tall stack of wide lines with substantial content per line.
        if (meanChars > 20.0 && mostlyWide) return RegionClass.Prose;

        return RegionClass.Mixed;
    }

    private static double MedianHeight(IReadOnlyList<TextSpan> spans, IReadOnlyList<int> indices)
    {
        var hs = new List<double>(indices.Count);
        foreach (int i in indices)
        {
            double h = Math.Abs(spans[i].Height);
            if (h > 0.0) hs.Add(h);
        }
        if (hs.Count == 0) return 1.0;
        hs.Sort();
        return hs[hs.Count / 2];
    }

    /// <summary>Cluster spans into baseline lines, tolerant of small Y jitter.</summary>
    private static List<LineStat> ClusterLines(IReadOnlyList<TextSpan> spans, IReadOnlyList<int> indices, double medH)
    {
        var order = indices.ToList();
        order.Sort((a, b) =>
        {
            int c = spans[a].Top.CompareTo(spans[b].Top);
            return c != 0 ? c : spans[a].Left.CompareTo(spans[b].Left);
        });

        double tol = medH * 0.6;
        var lines = new List<LineStat>();
        foreach (int i in order)
        {
            var s = spans[i];
            int nonWs = s.Text.Count(c => !char.IsWhiteSpace(c));
            var last = lines.Count > 0 ? lines[^1] : null;
            if (last is not null && Math.Abs(s.Top - last.Top) <= tol)
            {
                last.Left = Math.Min(last.Left, s.Left);
                last.Right = Math.Max(last.Right, s.Right);
                last.NonWsChars += nonWs;
                // A new leftmost span on this line owns the lead text.
                if (s.Left < last.SpanLefts[0]) last.LeadText = s.Text.TrimStart();
                last.SpanLefts.Add(s.Left);
                last.SpanRights.Add(s.Right);
            }
            else
            {
                lines.Add(new LineStat
                {
                    Top = s.Top,
                    Left = s.Left,
                    Right = s.Right,
                    NonWsChars = nonWs,
                    LeadText = s.Text.TrimStart(),
                    SpanLefts = { s.Left },
                    SpanRights = { s.Right },
                });
            }
        }

        // Keep the edge lists paired and left-sorted for gap analysis.
        foreach (var l in lines)
        {
            var paired = l.SpanLefts.Zip(l.SpanRights, (lft, rgt) => (lft, rgt))
                                    .OrderBy(p => p.lft).ToList();
            l.SpanLefts = paired.Select(p => p.lft).ToList();
            l.SpanRights = paired.Select(p => p.rgt).ToList();
        }
        return lines;
    }

    /// <summary>A numbered/bracketed reference entry: <c>12.</c>, <c>12)</c>, <c>[12]</c>, <c>(12)</c>.</summary>
    private static bool StartsNumberedEntry(string lead)
    {
        if (lead.Length == 0) return false;
        if ((lead[0] == '[' || lead[0] == '(') && lead.Length > 1 && char.IsAsciiDigit(lead[1])) return true;

        int digits = 0;
        while (digits < 4 && digits < lead.Length && char.IsAsciiDigit(lead[digits])) digits++;
        if (digits is >= 1 and <= 3 && digits < lead.Length)
            return lead[digits] == '.' || lead[digits] == ')';
        return false;
    }

    /// <summary>A label … value row: one large interior gap with text on both sides.</summary>
    private static bool LineHasLabelValueGap(LineStat l, double regionWidth)
    {
        if (l.SpanLefts.Count < 2) return false;
        double threshold = regionWidth * 0.25;
        for (int w = 1; w < l.SpanLefts.Count; w++)
            if (l.SpanLefts[w] - l.SpanRights[w - 1] >= threshold) return true;
        return false;
    }

    /// <summary>
    /// A hanging-indent two-level left edge: a primary entry-start edge and a continuation
    /// edge 0.8–5 median heights past it, each carrying at least a quarter of the lines.
    /// </summary>
    private static bool HasHangingIndent(List<double> leftEdges, double medH)
    {
        if (leftEdges.Count < 6) return false;
        double l0 = leftEdges.Min();
        double nearTol = medH * 0.5;
        int loBand = leftEdges.Count(x => Math.Abs(x - l0) <= nearTol);
        int hiBand = leftEdges.Count(x => x - l0 >= medH * 0.8 && x - l0 <= medH * 5.0);
        int n = leftEdges.Count;
        return loBand * 4 >= n && hiBand * 4 >= n;
    }
}
