using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>A detected layout region, in PDF (bottom-origin) coordinates.</summary>
internal readonly record struct LayoutHint(float Left, float Right, float Top, float Bottom, float Confidence);

/// <summary>
/// Table regions recovered from text geometry, and their reconstruction.
/// </summary>
/// <remarks>
/// Upstream reaches tables in the structured PDF path through a layout detector, with a
/// geometric fallback for the thin borderless grids the model misses. Without the detector the
/// fallback is the whole path — it is what produced the tables in the reference output, and it
/// needs nothing but the words on the page. Ports
/// <c>pdf/structure/regions/geometric_tables.rs</c> and the non-ML half of
/// <c>pdf/structure/regions/tables.rs</c>.
/// </remarks>
internal static class PdfLayoutTables
{
    // ── geometric_tables.rs ──────────────────────────────────────────────────

    private const int MinTableRows = 3;
    /// <summary>Three columns is the precision guard: a one- or two-column prose reflow cannot
    /// clear it, so it never reaches the downstream guards as a table candidate.</summary>
    private const int MinTableCols = 3;
    private const uint AnchorTolerancePts = 10;
    private const float MinAnchorRowSupport = 0.6f;
    private const float MaxRowPitchFactor = 3.5f;
    private const float RowGroupingFactor = 0.6f;
    /// <summary>The class this fallback targets — borderless invoice and metric tables — is
    /// numeric, while its dominant false positive, two-column academic body text, is not.</summary>
    private const float MinNumericWordFraction = 0.35f;
    private const int TextHeavyMaxWordsPerRow = 10;
    private const uint TextHeavyMinGutterRatio = 8;
    private const uint TextHeavyMinGutterPts = 100;
    private const float SyntheticHintConfidence = 1.0f;

    /// <summary>A visual row: the indices of the words sharing a top band, and the band's top.</summary>
    private sealed class Row
    {
        public List<int> WordIndices = new();
        public uint Top;
    }

    /// <summary>
    /// Column-aligned multi-row text bands, as synthetic Table hints in PDF (bottom-origin)
    /// coordinates.
    /// </summary>
    public static List<LayoutHint> DetectGeometricTableHints(List<HocrWord> words, float pageHeight)
    {
        var hints = new List<LayoutHint>();

        var indexed = new List<int>();
        for (int i = 0; i < words.Count; i++)
            if (words[i].Text.Trim().Length > 0) indexed.Add(i);
        if (indexed.Count < MinTableRows * MinTableCols) return hints;

        uint medianHeight = MedianWordHeight(words, indexed);
        if (medianHeight == 0) return hints;

        var rows = GroupRows(words, indexed, medianHeight);
        if (rows.Count < MinTableRows) return hints;

        uint maxRowPitch = (uint)MathF.Round(medianHeight * MaxRowPitchFactor, MidpointRounding.AwayFromZero);

        // Split rows into vertically contiguous runs, then propose each run whose columns line up
        // across it. Column consistency is the real signal: a prose reflow shares a left margin
        // but has no three internal columns.
        var run = new List<int>();
        for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            bool contiguous = run.Count == 0
                || Sub(rows[rowIdx].Top, rows[run[^1]].Top) <= maxRowPitch;
            if (!contiguous)
            {
                if (FinalizeRun(words, rows, run, pageHeight) is { } broken) hints.Add(broken);
                run.Clear();
            }
            run.Add(rowIdx);
        }
        if (FinalizeRun(words, rows, run, pageHeight) is { } last) hints.Add(last);

        return hints;
    }

    private static uint Sub(uint a, uint b) => a > b ? a - b : 0;

    /// <summary>
    /// Emit a hint for a run only when it has enough rows and enough word-left anchors line up
    /// across them, and the run is either numeric-dominant or a text-heavy key-value grid.
    /// </summary>
    private static LayoutHint? FinalizeRun(List<HocrWord> words, List<Row> rows, List<int> run, float pageHeight)
    {
        if (run.Count < MinTableRows) return null;

        // Each word's left edge is a candidate column anchor. A multi-word cell contributes one
        // anchor per word, but only anchors aligned across enough rows survive the support
        // filter, so a stray second-word offset in a single row drops out.
        var anchors = new List<uint>();
        var perRowLefts = new List<List<uint>>(run.Count);
        foreach (int rowIdx in run)
        {
            var lefts = rows[rowIdx].WordIndices.Select(i => words[i].Left).ToList();
            lefts.Sort();
            anchors.AddRange(lefts);
            perRowLefts.Add(lefts);
        }
        anchors.Sort();

        int minSupport = Math.Max((int)MathF.Ceiling(run.Count * MinAnchorRowSupport), 2);
        if (CountConsistentAnchors(anchors, perRowLefts, minSupport) < MinTableCols) return null;

        bool numeric = RunIsNumericDominant(words, rows, run);
        if (!numeric && !RunIsTextHeavyGrid(words, rows, run)) return null;

        return RunBoundingHint(words, rows, run, pageHeight);
    }

    /// <summary>Whether a run's words are numeric-dominant.</summary>
    private static bool RunIsNumericDominant(List<HocrWord> words, List<Row> rows, List<int> run)
    {
        int total = 0, numeric = 0;
        foreach (int rowIdx in run)
            foreach (int i in rows[rowIdx].WordIndices)
            {
                string text = words[i].Text.Trim();
                if (text.Length == 0) continue;
                total++;
                if (IsNumericToken(text)) numeric++;
            }
        return total > 0 && numeric >= total * MinNumericWordFraction;
    }

    /// <summary>
    /// A token reads as a numeric value when it carries a digit and no letter — a real number,
    /// however it is decorated. Rejects unit labels like <c>000s</c> and maths variables like
    /// <c>a1</c>, the dominant false positive on equation-dense pages.
    /// </summary>
    private static bool IsNumericToken(string text)
    {
        bool hasDigit = false;
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c)) hasDigit = true;
            else if (char.IsLetter(c)) return false;
        }
        return hasDigit;
    }

    /// <summary>Cluster anchors within tolerance and count those populated in enough rows.</summary>
    private static int CountConsistentAnchors(List<uint> sortedAnchors, List<List<uint>> perRowStarts, int minSupport)
    {
        var centers = new List<uint>();
        int clusterStart = 0;
        while (clusterStart < sortedAnchors.Count)
        {
            uint baseValue = sortedAnchors[clusterStart];
            int clusterEnd = clusterStart + 1;
            while (clusterEnd < sortedAnchors.Count && Sub(sortedAnchors[clusterEnd], baseValue) <= AnchorTolerancePts)
                clusterEnd++;
            ulong sum = 0;
            for (int i = clusterStart; i < clusterEnd; i++) sum += sortedAnchors[i];
            centers.Add((uint)(sum / (ulong)(clusterEnd - clusterStart)));
            clusterStart = clusterEnd;
        }

        return centers.Count(center =>
            perRowStarts.Count(starts => starts.Any(s => AbsDiff(s, center) <= AnchorTolerancePts)) >= minSupport);
    }

    private static uint AbsDiff(uint a, uint b) => a > b ? a - b : b - a;

    /// <summary>The run's bounding box, in PDF (bottom-origin) coordinates.</summary>
    private static LayoutHint RunBoundingHint(List<HocrWord> words, List<Row> rows, List<int> run, float pageHeight)
    {
        uint minLeft = uint.MaxValue, maxRight = 0, minTop = uint.MaxValue, maxBottom = 0;
        foreach (int rowIdx in run)
            foreach (int i in rows[rowIdx].WordIndices)
            {
                var w = words[i];
                minLeft = Math.Min(minLeft, w.Left);
                maxRight = Math.Max(maxRight, w.Left + w.Width);
                minTop = Math.Min(minTop, w.Top);
                maxBottom = Math.Max(maxBottom, w.Top + w.Height);
            }

        return new LayoutHint(
            Left: minLeft,
            Right: maxRight,
            Top: pageHeight - minTop,
            Bottom: pageHeight - maxBottom,
            Confidence: SyntheticHintConfidence);
    }

    /// <summary>Group words into visual rows by top coordinate.</summary>
    private static List<Row> GroupRows(List<HocrWord> words, List<int> indexed, uint medianHeight)
    {
        var order = new List<int>(indexed);
        order.Sort((a, b) => words[a].Top != words[b].Top
            ? words[a].Top.CompareTo(words[b].Top)
            : words[a].Left.CompareTo(words[b].Left));

        uint tolerance = Math.Max((uint)MathF.Round(medianHeight * RowGroupingFactor, MidpointRounding.AwayFromZero), 2);
        var rows = new List<Row>();
        foreach (int i in order)
        {
            if (rows.Count > 0 && Sub(words[i].Top, rows[^1].Top) <= tolerance) rows[^1].WordIndices.Add(i);
            else rows.Add(new Row { WordIndices = { i }, Top = words[i].Top });
        }
        return rows;
    }

    /// <summary>
    /// Whether a run is a text-heavy key-value grid: sparse rows whose cells are separated by
    /// gutters both far wider than the spacing inside a cell and absolutely wide.
    /// </summary>
    private static bool RunIsTextHeavyGrid(List<HocrWord> words, List<Row> rows, List<int> run)
    {
        var (medianGutter, medianWordGap, medianWordsPerRow) = RunRowSpacing(words, rows, run);
        return medianWordGap > 0
            && medianWordsPerRow <= TextHeavyMaxWordsPerRow
            && medianGutter >= TextHeavyMinGutterPts
            && medianGutter >= medianWordGap * TextHeavyMinGutterRatio;
    }

    /// <summary>
    /// Per-run spacing: the median widest inter-word gap on a row approximates the column gutter,
    /// the median narrowest positive gap the word spacing inside a cell.
    /// </summary>
    private static (uint Gutter, uint WordGap, int WordsPerRow) RunRowSpacing(
        List<HocrWord> words, List<Row> rows, List<int> run)
    {
        var maxGaps = new List<uint>();
        var minGaps = new List<uint>();
        var counts = new List<int>();
        foreach (int rowIdx in run)
        {
            var rowWords = rows[rowIdx].WordIndices.Select(i => words[i]).OrderBy(w => w.Left).ToList();
            counts.Add(rowWords.Count);
            var gaps = new List<uint>();
            for (int i = 1; i < rowWords.Count; i++)
                gaps.Add(Sub(rowWords[i].Left, rowWords[i - 1].Left + rowWords[i - 1].Width));
            if (gaps.Count > 0) maxGaps.Add(gaps.Max());
            var positive = gaps.Where(g => g > 0).ToList();
            if (positive.Count > 0) minGaps.Add(positive.Min());
        }
        return (Median(maxGaps), Median(minGaps), (int)Median(counts.Select(c => (uint)c).ToList()));
    }

    private static uint Median(List<uint> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static uint MedianWordHeight(List<HocrWord> words, List<int> indexed) =>
        Median(indexed.Select(i => words[i].Height).ToList());

    // ── tables.rs ────────────────────────────────────────────────────────────

    /// <summary>Upward margin applied when tightening a table's bbox top, in points.</summary>
    private const uint TableBboxTopTightenMarginPts = 4;

    /// <summary>
    /// Reconstruct a table for each Table hint, from the words overlapping it.
    /// </summary>
    /// <param name="prevalidatedColumns">
    /// Set for geometrically vetted hints: a regular key-value grid trips the columnar-prose
    /// heuristic, and this caller has already checked the run's columns. Every other guard applies.
    /// </param>
    public static List<Table> ExtractTablesFromLayoutHints(
        List<HocrWord> words,
        List<LayoutHint> hints,
        int pageIndex,
        float pageHeight,
        float minConfidence,
        bool allowSingleColumn,
        bool prevalidatedColumns)
    {
        var tables = new List<Table>();

        foreach (var hint in hints.Where(h => h.Confidence >= minConfidence))
        {
            float hintImgTop = Math.Max(pageHeight - hint.Top, 0.0f);
            float hintImgBottom = Math.Max(pageHeight - hint.Bottom, 0.0f);

            var tableWords = words
                .Where(w => w.Text.Trim().Length > 0)
                .Where(w => WordHintIow(w, hint.Left, hintImgTop, hint.Right, hintImgBottom) >= 0.2)
                .ToList();
            if (tableWords.Count < 4) continue;

            uint colGap = ComputeAdaptiveColumnGap(tableWords, hint.Right - hint.Left);
            var tableCells = PdfTableReconstruct.ReconstructTable(tableWords, colGap, 0.5);
            if (tableCells.Count == 0 || tableCells[0].Count == 0) continue;

            int minColumnGaps = Math.Max(tableCells[0].Count / 2, 1);
            double tightenedY1 = TightenedTop(tableWords, hint, pageHeight, hintImgTop, colGap, minColumnGaps);
            var boundingBox = new BoundingBox
            {
                X0 = hint.Left,
                Y0 = hint.Bottom,
                X1 = hint.Right,
                Y1 = tightenedY1,
            };

            var cleaned = PdfTableReconstruct.PostProcessTable(tableCells, layoutGuided: true, allowSingleColumn);
            if (cleaned is null) continue;
            tableCells = cleaned;

            if (tableCells.Count <= 1) continue;

            // A short table filling more than half the page is a page of prose that happened to
            // line up, not a table.
            float hintHeight = Math.Abs(hint.Top - hint.Bottom);
            if (tableCells.Count <= 3 && pageHeight > 0.0f && hintHeight / pageHeight > 0.5f) continue;

            int totalCells = tableCells.Sum(r => r.Count);
            int emptyCells = tableCells.SelectMany(r => r).Count(c => c.Trim().Length == 0);
            if (totalCells > 0 && (double)emptyCells / totalCells > 0.55) continue;

            int totalTextLen = tableCells.SelectMany(r => r).Sum(c => Utf8Len(c.Trim()));
            if (totalCells > 6 && totalTextLen < totalCells) continue;

            if (tableCells.Count >= 3)
            {
                int singleCellRows = tableCells.Count(r => r.Count(c => c.Trim().Length > 0) <= 1);
                if ((double)singleCellRows / tableCells.Count > 0.5) continue;
            }

            if (PdfTableReconstruct.LooksLikeCodeListing(tableCells)) continue;
            if (!PdfTableReconstruct.IsWellFormedTable(tableCells, prevalidatedColumns)) continue;

            var repaired = tableCells
                .Select(row => row.Select(PdfTextRepair.RepairBrokenWordSpacing).ToList())
                .ToList();

            tables.Add(new Table
            {
                Cells = tableCells,
                Markdown = PdfTableReconstruct.TableToMarkdown(repaired),
                PageNumber = (uint)(pageIndex + 1),
                BoundingBox = boundingBox,
            });
        }

        return tables;
    }

    private static int Utf8Len(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// The table's top, pulled down to the first row that actually shows column gaps, so a
    /// caption or a heading swept into the hint does not widen the box that suppresses paragraphs.
    /// </summary>
    private static double TightenedTop(
        List<HocrWord> tableWords, LayoutHint hint, float pageHeight, float hintImgTop, uint colGap, int minColumnGaps)
    {
        const uint SameRowTolerancePts = 5;

        var sorted = tableWords.OrderBy(w => w.Top).ToList();
        uint? firstTableRowTop = null;
        int rowStart = 0;
        while (rowStart < sorted.Count)
        {
            uint rowAnchor = sorted[rowStart].Top;
            int rowEnd = rowStart;
            while (rowEnd < sorted.Count && Sub(sorted[rowEnd].Top, rowAnchor) <= SameRowTolerancePts) rowEnd++;

            var leftRights = sorted.GetRange(rowStart, rowEnd - rowStart)
                .Select(w => (Left: w.Left, Right: w.Left + w.Width))
                .OrderBy(p => p.Left)
                .ToList();
            int nColGaps = 0;
            for (int i = 1; i < leftRights.Count; i++)
                if (Sub(leftRights[i].Left, leftRights[i - 1].Right) >= colGap) nColGaps++;
            if (nColGaps >= minColumnGaps) { firstTableRowTop = rowAnchor; break; }
            rowStart = rowEnd;
        }

        uint imgTop = firstTableRowTop ?? (uint)Math.Max(hintImgTop, 0f);
        float pdfTop = pageHeight - Sub(imgTop, TableBboxTopTightenMarginPts);
        return Math.Min(pdfTop, hint.Top);
    }

    /// <summary>Fraction of a word's area that falls inside a hint's box.</summary>
    private static double WordHintIow(HocrWord word, float left, float top, float right, float bottom)
    {
        double wordArea = (double)word.Width * word.Height;
        if (wordArea <= 0.0) return 0.0;
        double interLeft = Math.Max(word.Left, left);
        double interRight = Math.Min(word.Left + word.Width, right);
        double interTop = Math.Max(word.Top, top);
        double interBottom = Math.Min(word.Top + word.Height, bottom);
        if (interLeft >= interRight || interTop >= interBottom) return 0.0;
        return (interRight - interLeft) * (interBottom - interTop) / wordArea;
    }

    /// <summary>
    /// An adaptive column-gap threshold: the median gap between words on a row is the typical
    /// word spacing, and a column gap is wider than that.
    /// </summary>
    public static uint ComputeAdaptiveColumnGap(List<HocrWord> words, float tableWidth)
    {
        var gaps = new List<uint>();

        if (words.Count >= 4)
        {
            var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
            uint medianH = heights[heights.Count / 2];
            uint rowTolerance = Math.Max(medianH / 2, 3);

            var sorted = words
                .Select(w => (Yc: w.Top + w.Height / 2, Left: w.Left, Right: w.Left + w.Width))
                .OrderBy(t => t.Yc).ThenBy(t => t.Left)
                .ToList();

            int rowStart = 0;
            while (rowStart < sorted.Count)
            {
                uint rowYc = sorted[rowStart].Yc;
                int rowEnd = rowStart + 1;
                while (rowEnd < sorted.Count && AbsDiff(sorted[rowEnd].Yc, rowYc) <= rowTolerance) rowEnd++;

                for (int i = rowStart + 1; i < rowEnd; i++)
                    if (sorted[i].Left > sorted[i - 1].Right) gaps.Add(sorted[i].Left - sorted[i - 1].Right);

                rowStart = rowEnd;
            }
        }

        if (gaps.Count >= 3)
        {
            gaps.Sort();
            var largeGaps = gaps.Where(g => g >= 40).ToList();
            if (largeGaps.Count > 0)
            {
                uint medianGap = largeGaps[largeGaps.Count / 2];
                return Math.Clamp(medianGap / 2, 20u, 60u);
            }
            uint median = gaps[gaps.Count / 2];
            return Math.Clamp(median * 3, 20u, 60u);
        }

        if (tableWidth < 200.0f) return 10;
        if (tableWidth < 400.0f) return 15;
        if (tableWidth < 600.0f) return 20;
        return 30;
    }
}
