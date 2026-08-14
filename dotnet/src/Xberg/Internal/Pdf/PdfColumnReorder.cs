namespace Xberg.Internal.Pdf;

/// <summary>
/// Two-column page repair, applied to merged spans after the XY-cut ordering and before text
/// assembly — the same position Rust's <c>pdf/oxide/text.rs :: extract_page_text_column_aware</c>
/// applies it.
/// <para>
/// A dense two-column body is not always split by the XY-cut itself, and the assembler then
/// falls through to full-page-width Y order, welding left- and right-column lines at the same
/// height into one interleaved element mid-sentence. No downstream pass can repair that: the
/// interleaving is already baked into the element text (Rust GH#1397).
/// </para>
/// </summary>
internal static class PdfColumnReorder
{
    // ── sparse (Rust GH#1345): the guarded four-span, two-column sentence ────────────
    private const double MinSparseGutterFraction = 0.05;
    private const double MinSparseGutterPts = 15.0;
    private const double MinSparseContentWidthPts = 144.0;
    private const int MinSparseWords = 2;
    private const int MinSparseWordsPerSide = 6;
    private const int MinSparseAlphaChars = 8;
    private const double MinSparseAlphaRatio = 0.55;
    private const double MinSparseVerticalOverlap = 0.5;

    /// <summary>The XY-cut does not split regions with fewer spans than this, which is why the
    /// sparse case needs its own guarded path.</summary>
    private const int XyCutMinSpansForSplit = 5;

    // ── dense (Rust GH#1397): a full page of two-column prose ───────────────────────
    private const double MinDenseContentWidthPts = 200.0;
    /// <summary>2%, not 3%: on A4 a symmetric-margin two-column layout leaves a real gutter of
    /// ~18pt, and 3% (17.85pt) leaves no headroom at all. 2% is still far above the intra-line
    /// word spacing this must never mistake for a column boundary.</summary>
    private const double MinDenseGutterFraction = 0.02;
    private const double MinDenseGutterPts = 10.0;
    private const int MinDenseSpansPerSide = 6;
    /// <summary>A genuine column is bounded by both the page margin and the gutter and cannot
    /// reach much past ~45% of the page width; a running header spans ~87%. This sits cleanly
    /// between them.</summary>
    private const double FullWidthFurnitureFraction = 0.55;
    /// <summary>Two spans on one visual line never differ in Y by more than float noise; two
    /// distinct lines are always at least a line-height apart.</summary>
    private const double LineYTolerancePts = 0.5;
    /// <summary>Independent lines that must agree on the same gutter before it is trusted —
    /// one line with a coincidentally wide internal gap is not evidence of a column.</summary>
    private const int MinDenseSplitLines = MinDenseSpansPerSide;

    /// <summary>
    /// Apply both repairs in Rust's order. Returns <c>true</c> when either reordered
    /// <paramref name="spans"/> in place.
    /// </summary>
    public static bool Apply(List<TextSpan> spans, double pageWidth)
    {
        if (pageWidth <= 0 || spans.Count < 2) return false;
        bool sparse = ReorderSparseTwoColumnPage(spans, pageWidth);
        bool dense = ReorderDenseTwoColumnPage(spans, pageWidth);
        return sparse || dense;
    }

    // ── sparse ──────────────────────────────────────────────────────────────────────

    public static bool ReorderSparseTwoColumnPage(List<TextSpan> spans, double pageWidth)
    {
        double? splitX = SparseColumnSplit(spans, pageWidth);
        if (splitX is not { } split) return false;

        var reordered = spans
            .Select((s, i) => (s, i))
            .OrderBy(t => t.s.X >= split ? 1 : 0)
            .ThenByDescending(t => t.s.Y)
            .ThenBy(t => t.s.X)
            .Select(t => t.s)
            .ToList();
        for (int i = 0; i < spans.Count; i++) spans[i] = reordered[i];
        return true;
    }

    private static bool IsSparseColumnProse(TextSpan s)
    {
        int alpha = s.Text.Count(char.IsLetter);
        int nonWs = s.Text.Count(c => !char.IsWhiteSpace(c));
        int words = s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        bool geometryIsValid = double.IsFinite(s.X) && double.IsFinite(s.Y)
            && double.IsFinite(s.Width) && double.IsFinite(s.Height) && s.Width > 0;

        return geometryIsValid
            && !s.IsMonospace
            && !PdfPageText.HasRtlOrBidiContent(s.Text)
            && !s.Text.Contains(':')
            && words >= MinSparseWords
            && alpha >= MinSparseAlphaChars
            && (double)alpha / Math.Max(nonWs, 1) >= MinSparseAlphaRatio;
    }

    private static bool SparseColumnsOverlap(List<TextSpan> left, List<TextSpan> right)
    {
        (double low, double high) Extent(List<TextSpan> side) =>
            (side.Min(s => s.Y), side.Max(s => s.Y));

        var (leftLow, leftHigh) = Extent(left);
        var (rightLow, rightHigh) = Extent(right);
        double overlap = Math.Max(Math.Min(leftHigh, rightHigh) - Math.Max(leftLow, rightLow), 0.0);
        double shorter = Math.Min(leftHigh - leftLow, rightHigh - rightLow);
        return shorter > 0.0 && overlap / shorter >= MinSparseVerticalOverlap;
    }

    /// <summary>One sentence running across both columns: the first left span opens with a
    /// capital, the three continuations open lowercase, and exactly one span — the last on the
    /// right — carries the terminal punctuation.</summary>
    private static bool SparseColumnsContinueOneSentence(List<TextSpan> left, List<TextSpan> right)
    {
        var leftByY = left.OrderByDescending(s => s.Y).ToList();
        var rightByY = right.OrderByDescending(s => s.Y).ToList();

        static char? FirstLetter(TextSpan s)
        {
            foreach (char c in s.Text) if (char.IsLetter(c)) return c;
            return null;
        }
        static bool StartsLower(TextSpan s) => FirstLetter(s) is { } c && char.IsLower(c);
        static bool StartsUpper(TextSpan s) => FirstLetter(s) is { } c && char.IsUpper(c);
        static bool HasTerminal(TextSpan s)
        {
            string t = s.Text.TrimEnd();
            return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?');
        }

        var continuations = new[] { leftByY[1], rightByY[0], rightByY[1] };
        var all = leftByY.Concat(rightByY);

        return StartsUpper(leftByY[0])
            && continuations.All(StartsLower)
            && all.Count(HasTerminal) == 1
            && HasTerminal(rightByY[1]);
    }

    private static bool IsSparseColumnSplit(List<TextSpan> spans, double splitX, double minGutter)
    {
        var left = spans.Where(s => s.X < splitX).ToList();
        var right = spans.Where(s => s.X >= splitX).ToList();
        if (left.Count != 2 || right.Count != 2) return false;

        static int WordCount(List<TextSpan> side) =>
            side.Sum(s => s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (WordCount(left) < MinSparseWordsPerSide || WordCount(right) < MinSparseWordsPerSide) return false;

        double leftRight = left.Max(s => s.X + s.Width);
        return splitX - leftRight >= minGutter
            && SparseColumnsOverlap(left, right)
            && SparseColumnsContinueOneSentence(left, right);
    }

    private static double? SparseColumnSplit(List<TextSpan> spans, double pageWidth)
    {
        bool hasSparseProseShape = spans.Count == XyCutMinSpansForSplit - 1 && spans.All(IsSparseColumnProse);
        if (!hasSparseProseShape) return null;

        double contentLeft = spans.Min(s => s.X);
        double contentRight = spans.Max(s => s.X + s.Width);
        if (contentRight - contentLeft < MinSparseContentWidthPts) return null;

        double minGutter = Math.Max(pageWidth * MinSparseGutterFraction, MinSparseGutterPts);
        var starts = spans.Select(s => s.X).Distinct().OrderBy(x => x).ToList();
        foreach (double splitX in starts)
            if (IsSparseColumnSplit(spans, splitX, minGutter)) return splitX;
        return null;
    }

    // ── dense ───────────────────────────────────────────────────────────────────────

    /// <summary>One visual line: span indices in left-to-right order.</summary>
    private sealed class SpanLine : List<int> { }

    public static bool ReorderDenseTwoColumnPage(List<TextSpan> spans, double pageWidth)
    {
        if (spans.Count < 2) return false;
        double contentLeft = spans.Min(s => s.X);
        double contentRight = spans.Max(s => s.X + s.Width);
        if (contentRight - contentLeft < MinDenseContentWidthPts) return false;

        var order = SpansSortedTopToBottom(spans);
        var lines = GroupIntoLines(spans, order);
        if (DetectSplitX(spans, lines, pageWidth) is not { } splitX) return false;

        double furnitureWidth = pageWidth * FullWidthFurnitureFraction;
        var bands = BuildBands(spans, lines, furnitureWidth, splitX);
        var finalOrder = EmitBandOrder(spans, bands, splitX);
        if (finalOrder is null) return false;

        var reordered = finalOrder.Select(i => spans[i]).ToList();
        for (int i = 0; i < spans.Count; i++) spans[i] = reordered[i];
        return true;
    }

    /// <summary>The single global sort the rest of the dense repair is built on.</summary>
    private static List<int> SpansSortedTopToBottom(List<TextSpan> spans)
    {
        var order = Enumerable.Range(0, spans.Count).ToList();
        order.Sort((a, b) =>
        {
            int c = spans[b].Y.CompareTo(spans[a].Y);
            return c != 0 ? c : spans[a].X.CompareTo(spans[b].X);
        });
        return order;
    }

    /// <summary>
    /// Bucket a top-to-bottom order into visual lines. A line is anchored on its topmost span,
    /// so gradual drift across many spans can never chain unrelated lines together; each line
    /// is then re-sorted left-to-right, which the per-line gutter sweep requires.
    /// </summary>
    private static List<SpanLine> GroupIntoLines(List<TextSpan> spans, List<int> order)
    {
        var lines = new List<SpanLine>();
        double anchorY = double.NaN;
        foreach (int index in order)
        {
            double y = spans[index].Y;
            if (lines.Count == 0 || Math.Abs(anchorY - y) > LineYTolerancePts)
            {
                anchorY = y;
                lines.Add(new SpanLine());
            }
            lines[^1].Add(index);
        }
        foreach (var line in lines) line.Sort((a, b) => spans[a].X.CompareTo(spans[b].X));
        return lines;
    }

    /// <summary>
    /// Widest gap at least <paramref name="minGutter"/> wide between consecutive left-to-right
    /// edges. Tracking the running rightmost edge (rather than the previous span's right edge)
    /// means a span nested inside an earlier one is never mistaken for the start of a gap.
    /// </summary>
    private static double? WidestGapMidpoint(IEnumerable<(double Left, double Right)> edges, double minGutter)
    {
        using var it = edges.GetEnumerator();
        if (!it.MoveNext()) return null;
        double runningRight = it.Current.Right;
        double bestGap = 0.0;
        double? bestSplit = null;
        while (it.MoveNext())
        {
            var (left, right) = it.Current;
            double gap = left - runningRight;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestSplit = (runningRight + left) / 2.0;
            }
            runningRight = Math.Max(runningRight, right);
        }
        return bestGap < minGutter ? null : bestSplit;
    }

    private static bool LineHasWidthFurniture(List<TextSpan> spans, SpanLine line, double furnitureWidth) =>
        line.Any(i => spans[i].Width >= furnitureWidth);

    /// <summary>
    /// Establish the page's gutter from independent per-line evidence: each line is checked in
    /// isolation for an internal gap, so a furniture line elsewhere on the page can never
    /// corrupt another line's evidence. Requires several agreeing lines and returns their
    /// median, robust to the rare line whose gap sits a little off from the rest.
    /// </summary>
    private static double? DetectSplitX(List<TextSpan> spans, List<SpanLine> lines, double pageWidth)
    {
        double minGutter = Math.Max(pageWidth * MinDenseGutterFraction, MinDenseGutterPts);
        double furnitureWidth = pageWidth * FullWidthFurnitureFraction;

        var midpoints = new List<double>();
        foreach (var line in lines)
        {
            if (LineHasWidthFurniture(spans, line, furnitureWidth)) continue;
            var edges = line.Select(i => (spans[i].Left, spans[i].Right));
            if (WidestGapMidpoint(edges, minGutter) is { } mid) midpoints.Add(mid);
        }
        if (midpoints.Count < MinDenseSplitLines) return null;

        midpoints.Sort();
        int m = midpoints.Count / 2;
        return midpoints.Count % 2 == 0 ? (midpoints[m - 1] + midpoints[m]) / 2.0 : midpoints[m];
    }

    private abstract record Band
    {
        /// <summary>Ordinary column content, offered to the per-band column reorder.</summary>
        public sealed record Content(List<int> Indices) : Band;
        /// <summary>A single boundary line, emitted where it sits and never folded into a column.</summary>
        public sealed record Boundary(SpanLine Line) : Band;
    }

    /// <summary>
    /// Furniture separating two bands rather than column content: full-width, or straddling the
    /// gutter. The straddle test catches furniture narrower than the width threshold that a
    /// whole-page projection could not tell apart from real column content.
    /// </summary>
    private static bool LineIsBoundary(List<TextSpan> spans, SpanLine line, double furnitureWidth, double splitX) =>
        line.Any(i => spans[i].Width >= furnitureWidth
                      || (spans[i].Left < splitX && spans[i].Right > splitX));

    /// <summary>Split lines into bands at boundary lines: consecutive non-boundary lines
    /// accumulate into one content band, and each boundary line becomes its own band in place
    /// so it stays between the band above and the band below.</summary>
    private static List<Band> BuildBands(List<TextSpan> spans, List<SpanLine> lines, double furnitureWidth, double splitX)
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
            if (current.Count > 0)
            {
                bands.Add(new Band.Content(current));
                current = new List<int>();
            }
            bands.Add(new Band.Boundary(line));
        }
        if (current.Count > 0) bands.Add(new Band.Content(current));
        return bands;
    }

    /// <summary>
    /// Reorder one content band column-major, scoped to that band alone: a band with too few
    /// spans on either side, or one that fails the prose/reference classification, keeps its
    /// existing order, so a table or form band is not corrupted by a prose band on the same page.
    /// </summary>
    private static List<int>? ReorderBandColumns(List<TextSpan> spans, List<int> band, double splitX)
    {
        var left = band.Where(i => spans[i].X < splitX).ToList();
        var right = band.Where(i => spans[i].X >= splitX).ToList();
        if (left.Count < MinDenseSpansPerSide || right.Count < MinDenseSpansPerSide) return null;

        if (!PdfRegionClassifier.Classify(spans, left).IsReorderableColumn()) return null;
        if (!PdfRegionClassifier.Classify(spans, right).IsReorderableColumn()) return null;

        return left.Concat(right).ToList();
    }

    /// <summary>
    /// Concatenate bands into the final order, each boundary line emitted between the band
    /// above and the band below in true document order. Returns <c>null</c> when not a single
    /// band qualified, so the caller leaves the spans untouched rather than applying a no-op.
    /// </summary>
    private static List<int>? EmitBandOrder(List<TextSpan> spans, List<Band> bands, double splitX)
    {
        bool anyReordered = false;
        var order = new List<int>();
        foreach (var band in bands)
        {
            switch (band)
            {
                case Band.Boundary b:
                    order.AddRange(b.Line);
                    break;
                case Band.Content c:
                    var reordered = ReorderBandColumns(spans, c.Indices, splitX);
                    if (reordered is not null) { anyReordered = true; order.AddRange(reordered); }
                    else order.AddRange(c.Indices);
                    break;
            }
        }
        return anyReordered ? order : null;
    }
}
