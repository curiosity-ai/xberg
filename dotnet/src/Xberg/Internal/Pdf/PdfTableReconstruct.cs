// Ported from crates/xberg/src/table_core.rs, pdf/table_reconstruct.rs,
// pdf/oxide/table.rs (extract_tables_heuristic + region clustering), and
// pdf/structure/regions/tables.rs (compute_adaptive_column_gap).
//
// Text-layer table reconstruction with no OCR/graphics dependency: word
// bounding boxes (derived from font-metric segments) are clustered into
// vertically-contiguous regions, each region is reconstructed into a cell
// grid, then validated/cleaned by the same prose-rejection chain the Rust
// heuristic tier uses. Only the heuristic tier is ported — pdf_oxide's native
// ruling-line grid detector (extract_tables_native/_bordered) has no managed
// equivalent.
using System.Text;
using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>A word with an integer pixel bounding box (mirrors Rust `HocrWord`).</summary>
internal struct HocrWord
{
    public string Text;
    public uint Left, Top, Width, Height;
    public double Confidence;

    public readonly uint Right => Left + Width;
    public readonly uint Bottom => Top + Height;
    public readonly double YCenter => Top + Height / 2.0;
}

internal static class PdfTableReconstruct
{
    private const int MaxRegionsPerPage = 20;

    // ── Public entry: heuristic table extraction across all pages ────────────

    /// <summary>Port of `pdf::oxide::table::extract_tables_heuristic`. Runs the
    /// text-edge heuristic on every page's segments and returns detected tables.</summary>
    public static List<Table> ExtractHeuristicTables(List<List<SegmentData>> allPageSegments, bool allowSingleColumn)
    {
        var tables = new List<Table>();
        for (int pageIdx = 0; pageIdx < allPageSegments.Count; pageIdx++)
        {
            uint pageNumber = (uint)(pageIdx + 1);
            var segments = allPageSegments[pageIdx];
            if (segments.Count == 0) continue;

            float pageHeight = 0f;
            foreach (var s in segments) pageHeight = Math.Max(pageHeight, s.Y + s.Height);
            pageHeight = Math.Max(pageHeight, 792.0f);

            var words = SegmentsToWords(segments, pageHeight);
            if (words.Count < 4) continue;

            var regions = ClusterWordsIntoVerticalRegions(words);
            if (regions.Count > MaxRegionsPerPage)
                regions = regions.GetRange(0, MaxRegionsPerPage);

            foreach (var region in regions)
            {
                var table = ReconstructRegionTable(region, pageHeight, pageNumber, allowSingleColumn);
                if (table != null) tables.Add(table);
            }
        }
        return tables;
    }

    // ── Segment → word conversion (pdf/table_reconstruct.rs) ─────────────────

    private static uint RoundClamp(float v, float min) => (uint)Math.Max(MathF.Round(v, MidpointRounding.AwayFromZero), min);

    /// <summary>Port of `segments_to_words`.</summary>
    public static List<HocrWord> SegmentsToWords(List<SegmentData> segments, float pageHeight)
    {
        var words = new List<HocrWord>();
        foreach (var seg in segments)
            SplitSegmentToWords(seg, pageHeight, words);
        return words;
    }

    private static void SplitSegmentToWords(SegmentData seg, float pageHeight, List<HocrWord> outWords)
    {
        string trimmed = seg.Text.Trim();
        if (trimmed.Length == 0) return;

        uint topImage = RoundClamp(pageHeight - (seg.Y + seg.Height), 0f);

        // Fast path: single word (no interior whitespace).
        bool hasWs = false;
        foreach (char c in trimmed) if (char.IsWhiteSpace(c)) { hasWs = true; break; }
        if (!hasWs)
        {
            outWords.Add(new HocrWord
            {
                Text = trimmed,
                Left = RoundClamp(seg.X, 0f),
                Top = topImage,
                Width = RoundClamp(seg.Width, 0f),
                Height = RoundClamp(seg.Height, 0f),
                Confidence = 95.0,
            });
            return;
        }

        // Multi-word: proportional bbox per word by UTF-8 byte offset (matches Rust).
        byte[] full = Encoding.UTF8.GetBytes(seg.Text);
        float totalBytes = full.Length;
        if (totalBytes <= 0f) return;

        uint segHeight = RoundClamp(seg.Height, 0f);
        int searchStart = 0;
        foreach (string word in SplitWhitespace(seg.Text))
        {
            byte[] wb = Encoding.UTF8.GetBytes(word);
            int offset = IndexOf(full, wb, searchStart);
            if (offset < 0) continue;
            searchStart = offset + wb.Length;

            float fracStart = offset / totalBytes;
            float fracWidth = wb.Length / totalBytes;

            outWords.Add(new HocrWord
            {
                Text = word,
                Left = RoundClamp(seg.X + fracStart * seg.Width, 0f),
                Top = topImage,
                Width = RoundClamp(fracWidth * seg.Width, 1f),
                Height = segHeight,
                Confidence = 95.0,
            });
        }
    }

    private static IEnumerable<string> SplitWhitespace(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            yield return text.Substring(start, i - start);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (needle.Length == 0) return start;
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    // ── Region clustering (pdf/oxide/table.rs) ───────────────────────────────

    private static uint AbsDiff(uint a, uint b) => a > b ? a - b : b - a;

    private static List<List<HocrWord>> ClusterWordsIntoVerticalRegions(List<HocrWord> words)
    {
        if (words.Count < 4) return new List<List<HocrWord>>();

        var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
        uint medianHeight = Math.Max(heights[heights.Count / 2], 1u);
        uint rowTolerance = Math.Max(medianHeight / 2, 3u);
        uint rowGapSplit = (uint)(medianHeight * 1.8f);

        // Stable sort by y-center (top + height/2, integer).
        var sorted = words.OrderBy(w => w.Top + w.Height / 2).ToList();

        var regions = new List<List<HocrWord>>();
        var current = new List<HocrWord>();
        uint? lastRowYc = null;

        int idx = 0;
        while (idx < sorted.Count)
        {
            uint rowYc = sorted[idx].Top + sorted[idx].Height / 2;
            int end = idx + 1;
            while (end < sorted.Count)
            {
                uint yc = sorted[end].Top + sorted[end].Height / 2;
                if (AbsDiff(yc, rowYc) <= rowTolerance) end++;
                else break;
            }

            if (lastRowYc is uint prevYc && rowYc > prevYc && rowYc - prevYc > rowGapSplit && current.Count > 0)
            {
                regions.Add(current);
                current = new List<HocrWord>();
            }
            for (int k = idx; k < end; k++) current.Add(sorted[k]);
            lastRowYc = rowYc;
            idx = end;
        }
        if (current.Count > 0) regions.Add(current);

        // retain: >=4 words, >=3 distinct rows, >=2 distinct x columns.
        var kept = new List<List<HocrWord>>();
        foreach (var r in regions)
        {
            if (r.Count < 4) continue;

            var rowYcs = r.Select(w => w.Top + w.Height / 2).OrderBy(v => v).ToList();
            int distinctRows = DedupCount(rowYcs, rowTolerance);
            if (distinctRows < 3) continue;

            var xs = r.Select(w => w.Left).OrderBy(v => v).ToList();
            int distinctXs = DedupCount(xs, 8u);
            if (distinctXs < 2) continue;

            kept.Add(r);
        }
        return kept;
    }

    // Count of runs after `dedup_by(|a,b| abs_diff <= tol)` on a sorted list.
    private static int DedupCount(List<uint> sorted, uint tol)
    {
        if (sorted.Count == 0) return 0;
        int count = 1;
        uint last = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (AbsDiff(sorted[i], last) <= tol)
            {
                // merged into previous run; Rust dedup_by keeps the first element.
                continue;
            }
            count++;
            last = sorted[i];
        }
        return count;
    }

    // ── Adaptive column gap (pdf/structure/regions/tables.rs) ────────────────

    private static uint ComputeAdaptiveColumnGap(List<HocrWord> words, float tableWidth)
    {
        var gaps = new List<uint>();

        if (words.Count >= 4)
        {
            var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
            uint medianH = heights[heights.Count / 2];
            uint rowTolerance = Math.Max(medianH / 2, 3u);

            // (yc, left, right); stable sort by (yc, left).
            var sorted = words
                .Select(w => (Yc: w.Top + w.Height / 2, Left: w.Left, Right: w.Left + w.Width))
                .OrderBy(t => t.Yc).ThenBy(t => t.Left).ToList();

            int rowStart = 0;
            while (rowStart < sorted.Count)
            {
                uint rowYc = sorted[rowStart].Yc;
                int rowEnd = rowStart + 1;
                while (rowEnd < sorted.Count && AbsDiff(sorted[rowEnd].Yc, rowYc) <= rowTolerance) rowEnd++;

                for (int i = rowStart + 1; i < rowEnd; i++)
                {
                    uint prevRight = sorted[i - 1].Right;
                    uint currLeft = sorted[i].Left;
                    if (currLeft > prevRight) gaps.Add(currLeft - prevRight);
                }
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
            else
            {
                uint medianGap = gaps[gaps.Count / 2];
                return Math.Clamp(medianGap * 3, 20u, 60u);
            }
        }

        if (tableWidth < 200.0f) return 10u;
        if (tableWidth < 400.0f) return 15u;
        if (tableWidth < 600.0f) return 20u;
        return 30u;
    }

    // ── Region → Table (pdf/oxide/table.rs reconstruct_region_table) ─────────

    private static Table? ReconstructRegionTable(List<HocrWord> region, float pageHeight, uint pageNumber, bool allowSingleColumn)
    {
        uint regionLeft = region.Min(w => w.Left);
        uint regionRight = region.Max(w => w.Left + w.Width);
        float regionWidth = regionRight > regionLeft ? regionRight - regionLeft : 0f;
        uint colGap = ComputeAdaptiveColumnGap(region, regionWidth);

        var grid = ReconstructTable(region, colGap, 0.5);
        if (grid.Count == 0 || grid[0].Count == 0) return null;

        var cleaned = PostProcessTable(grid, layoutGuided: true, allowSingleColumn);
        if (cleaned == null) return null;
        if (cleaned.Count <= 1) return null;

        if (LooksLikeCodeListing(cleaned)) return null;
        if (!IsWellFormedTable(cleaned)) return null;

        double imgLeft = region.Min(w => (double)w.Left);
        double imgTop = region.Min(w => (double)w.Top);
        double imgRight = region.Max(w => (double)(w.Left + w.Width));
        double imgBottom = region.Max(w => (double)(w.Top + w.Height));
        BoundingBox? bbox = null;
        if (imgRight > imgLeft && imgBottom > imgTop)
            bbox = new BoundingBox { X0 = imgLeft, Y0 = pageHeight - imgBottom, X1 = imgRight, Y1 = pageHeight - imgTop };

        string markdown = TableToMarkdown(cleaned);
        if (markdown.Trim().Length == 0) return null;

        return new Table
        {
            Cells = cleaned,
            Markdown = markdown,
            PageNumber = pageNumber,
            BoundingBox = bbox,
        };
    }

    // ── Core grid reconstruction (table_core.rs) ─────────────────────────────

    private static List<uint> DetectColumns(List<HocrWord> words, uint columnThreshold)
    {
        if (words.Count == 0) return new List<uint>();
        var groups = new List<List<uint>>();
        foreach (var word in words)
        {
            uint x = word.Left;
            bool found = false;
            foreach (var group in groups)
            {
                if (group.Count > 0 && AbsDiff(x, group[0]) <= columnThreshold)
                {
                    group.Add(x);
                    found = true;
                    break;
                }
            }
            if (!found) groups.Add(new List<uint> { x });
        }
        var cols = new List<uint>();
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            var s = group.OrderBy(v => v).ToList();
            cols.Add(s[s.Count / 2]);
        }
        cols.Sort();
        return cols;
    }

    private static List<uint> DetectRows(List<HocrWord> words, double rowThresholdRatio)
    {
        if (words.Count == 0) return new List<uint>();
        var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
        uint medianHeight = heights[heights.Count / 2];
        uint rowThreshold = (uint)(medianHeight * rowThresholdRatio);

        var groups = new List<List<double>>();
        foreach (var word in words)
        {
            double yc = word.YCenter;
            bool found = false;
            foreach (var group in groups)
            {
                if (group.Count > 0 && Math.Abs(yc - group[0]) <= rowThreshold)
                {
                    group.Add(yc);
                    found = true;
                    break;
                }
            }
            if (!found) groups.Add(new List<double> { yc });
        }
        var rows = new List<uint>();
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            var s = group.OrderBy(v => v).ToList();
            rows.Add((uint)s[s.Count / 2]);
        }
        rows.Sort();
        return rows;
    }

    private static int? FindRowIndex(List<uint> rowPositions, HocrWord word)
    {
        uint yc = (uint)word.YCenter;
        int? best = null;
        uint bestDiff = 0;
        for (int i = 0; i < rowPositions.Count; i++)
        {
            uint d = AbsDiff(rowPositions[i], yc);
            if (best == null || d < bestDiff) { best = i; bestDiff = d; }
        }
        return best;
    }

    private static int? FindColumnIndex(List<uint> colPositions, HocrWord word)
    {
        uint x = word.Left;
        int? best = null;
        uint bestDiff = 0;
        for (int i = 0; i < colPositions.Count; i++)
        {
            uint d = AbsDiff(colPositions[i], x);
            if (best == null || d < bestDiff) { best = i; bestDiff = d; }
        }
        return best;
    }

    private static List<List<string>> RemoveEmptyRowsAndColumns(List<List<string>> table)
    {
        if (table.Count == 0) return table;
        int numCols = table[0].Count;
        var nonEmptyCols = new bool[numCols];
        foreach (var row in table)
            for (int c = 0; c < row.Count && c < numCols; c++)
                if (row[c].Trim().Length > 0) nonEmptyCols[c] = true;

        var result = new List<List<string>>();
        foreach (var row in table)
        {
            if (!row.Any(cell => cell.Trim().Length > 0)) continue;
            var newRow = new List<string>();
            for (int c = 0; c < row.Count; c++)
                if (c < numCols && nonEmptyCols[c]) newRow.Add(row[c]);
            result.Add(newRow);
        }
        return result;
    }

    internal static List<List<string>> ReconstructTable(List<HocrWord> words, uint columnThreshold, double rowThresholdRatio)
    {
        if (words.Count == 0) return new List<List<string>>();
        var colPositions = DetectColumns(words, columnThreshold);
        var rowPositions = DetectRows(words, rowThresholdRatio);
        if (colPositions.Count == 0 || rowPositions.Count == 0) return new List<List<string>>();

        int numRows = rowPositions.Count, numCols = colPositions.Count;
        var cells = new List<string>[numRows, numCols];
        for (int r = 0; r < numRows; r++)
            for (int c = 0; c < numCols; c++)
                cells[r, c] = new List<string>();

        foreach (var word in words)
        {
            int? r = FindRowIndex(rowPositions, word);
            int? c = FindColumnIndex(colPositions, word);
            if (r is int ri && c is int ci && ri < numRows && ci < numCols)
                cells[ri, ci].Add(word.Text);
        }

        var result = new List<List<string>>(numRows);
        for (int r = 0; r < numRows; r++)
        {
            var row = new List<string>(numCols);
            for (int c = 0; c < numCols; c++)
                row.Add(cells[r, c].Count == 0 ? "" : string.Join(" ", cells[r, c]));
            result.Add(row);
        }
        return RemoveEmptyRowsAndColumns(result);
    }

    public static string TableToMarkdown(List<List<string>> table)
    {
        if (table.Count == 0) return "";
        int numCols = table[0].Count;
        if (numCols == 0) return "";

        var sb = new StringBuilder();
        for (int rowIdx = 0; rowIdx < table.Count; rowIdx++)
        {
            sb.Append('|');
            foreach (var cell in table[rowIdx])
            {
                sb.Append(' ');
                sb.Append(cell.Replace("|", "\\|"));
                sb.Append(" |");
            }
            sb.Append('\n');
            if (rowIdx == 0)
            {
                sb.Append('|');
                for (int i = 0; i < numCols; i++) sb.Append(" --- |");
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    // ── post_process_table (pdf/table_reconstruct.rs) ────────────────────────

    internal static List<List<string>>? PostProcessTable(List<List<string>> table, bool layoutGuided, bool allowSingleColumn)
    {
        int minColumns = allowSingleColumn ? 1 : (layoutGuided ? 2 : 3);
        return PostProcessTableInner(table, minColumns, layoutGuided);
    }

    private static int CharCount(string s)
    {
        // Rust chars().count() is a Unicode scalar count, not UTF-16 units.
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    // Rust `str::len()` is the UTF-8 byte length.
    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    private static int WordCount(string s)
    {
        int n = 0;
        foreach (var _ in SplitWhitespace(s)) n++;
        return n;
    }

    private static List<List<string>>? PostProcessTableInner(List<List<string>> table, int minColumns, bool layoutGuided)
    {
        table = table.Where(row => row.Any(cell => cell.Trim().Length > 0)).Select(r => new List<string>(r)).ToList();
        if (table.Count == 0) return null;

        int nonEmpty = 0, longCells = 0, totalChars = 0;
        foreach (var row in table)
            foreach (var cell in row)
            {
                string t = cell.Trim();
                if (t.Length == 0) continue;
                int cc = CharCount(t);
                nonEmpty++;
                totalChars += cc;
                if (cc > 60) longCells++;
            }

        if (nonEmpty > 0)
        {
            if (layoutGuided)
            {
                if (longCells > 0)
                {
                    int long100 = table.SelectMany(r => r).Count(cell =>
                    {
                        string t = cell.Trim();
                        return t.Length > 0 && CharCount(t) > 100;
                    });
                    if (long100 * 10 > nonEmpty * 7) return null;
                }
                if (totalChars / nonEmpty > 80) return null;
            }
            else
            {
                if (longCells * 2 > nonEmpty) return null;
                if (totalChars / nonEmpty > 50) return null;
            }
        }

        int colCount = table.Count > 0 ? table[0].Count : 0;
        if (colCount < minColumns) return null;

        int dataStart = 0;
        for (int idx = 0; idx < table.Count; idx++)
        {
            int digitCells = table[idx].Count(cell => cell.Any(c => c >= '0' && c <= '9'));
            if (digitCells >= 3) { dataStart = idx; break; }
        }

        var headerRows = dataStart > 0 ? table.GetRange(0, dataStart) : new List<List<string>>();
        var dataRows = table.GetRange(dataStart, table.Count - dataStart);

        if (headerRows.Count > 2)
            headerRows = headerRows.GetRange(headerRows.Count - 2, 2);

        if (headerRows.Count == 0)
        {
            if (dataRows.Count < 2) return null;
            headerRows.Add(dataRows[0]);
            dataRows = dataRows.GetRange(1, dataRows.Count - 1);
        }

        int columnCount = (headerRows.Count > 0 ? headerRows[0] : (dataRows.Count > 0 ? dataRows[0] : new List<string>())).Count;
        if (columnCount == 0) return null;

        var header = new List<string>();
        for (int i = 0; i < columnCount; i++) header.Add("");
        foreach (var row in headerRows)
            for (int idx = 0; idx < row.Count && idx < columnCount; idx++)
            {
                string t = row[idx].Trim();
                if (t.Length == 0) continue;
                if (header[idx].Length > 0) header[idx] += " ";
                header[idx] += t;
            }

        var processed = new List<List<string>> { header };
        processed.AddRange(dataRows);
        if (processed.Count <= 1) return null;

        // Remove header-only columns.
        int col = 0;
        while (col < processed[0].Count)
        {
            string headerText = processed[0][col].Trim();
            bool dataEmpty = true;
            for (int r = 1; r < processed.Count; r++)
            {
                if (col < processed[r].Count && processed[r][col].Trim().Length > 0) { dataEmpty = false; break; }
            }
            if (dataEmpty) MergeHeaderOnlyColumn(processed, col, headerText);
            else col++;

            if (processed.Count == 0 || processed[0].Count == 0) return null;
        }

        if (processed[0].Count < 2 || processed.Count <= 1) return null;

        int dataRowCount = processed.Count - 1;

        // Column sparsity check.
        if (dataRowCount > 0)
        {
            for (int c = 0; c < processed[0].Count; c++)
            {
                int emptyCount = 0;
                for (int r = 1; r < processed.Count; r++)
                    if (c >= processed[r].Count || processed[r][c].Trim().Length == 0) emptyCount++;
                bool tooSparse = layoutGuided ? emptyCount * 20 > dataRowCount * 19 : emptyCount * 4 > dataRowCount * 3;
                if (tooSparse) return null;
            }
        }

        // Overall density check.
        {
            int totalDataCells = dataRowCount * processed[0].Count;
            if (totalDataCells > 0)
            {
                int filled = 0;
                for (int r = 1; r < processed.Count; r++)
                    foreach (var cell in processed[r])
                        if (cell.Trim().Length > 0) filled++;
                bool tooSparse = layoutGuided ? filled * 20 < totalDataCells * 3 : filled * 5 < totalDataCells * 2;
                if (tooSparse) return null;
            }
        }

        // Single-word prose check (>=5 cols).
        if (processed[0].Count >= 5)
        {
            int singleWord = 0, nonEmptyCells = 0;
            for (int r = 1; r < processed.Count; r++)
                foreach (var cell in processed[r])
                {
                    string t = cell.Trim();
                    if (t.Length == 0) continue;
                    nonEmptyCells++;
                    if (WordCount(t) <= 2) singleWord++;
                }
            int threshold = layoutGuided ? 85 : 70;
            if (nonEmptyCells >= 6 && singleWord * 100 > nonEmptyCells * threshold) return null;
        }

        // Column-text-flow check (col0 → col1).
        if (processed[0].Count >= 2)
        {
            int flowRows = 0, eligibleRows = 0;
            for (int r = 1; r < processed.Count; r++)
            {
                string col0 = processed[r].Count > 0 ? processed[r][0].Trim() : "";
                string col1 = processed[r].Count > 1 ? processed[r][1].Trim() : "";
                if (col0.Length == 0 || col1.Length == 0) continue;
                eligibleRows++;
                bool endsWithoutPunct = !col0.EndsWith('.') && !col0.EndsWith('?') && !col0.EndsWith('!') && !col0.EndsWith(':');
                bool startsLower = col1.Length > 0 && char.IsLower(col1[0]);
                if (endsWithoutPunct && startsLower) flowRows++;
            }
            if (eligibleRows >= 3 && flowRows * 10 > eligibleRows * 6) return null;
        }

        // Content asymmetry check.
        {
            int numCols = processed[0].Count;
            var colCharCounts = new long[numCols];
            for (int c = 0; c < numCols; c++)
            {
                long sum = 0;
                for (int r = 1; r < processed.Count; r++)
                    if (c < processed[r].Count) sum += Utf8Len(processed[r][c].Trim());
                colCharCounts[c] = sum;
            }
            long totalCharsAsym = colCharCounts.Sum();
            if (totalCharsAsym > 0)
            {
                double maxColShare = 0.0;
                foreach (var cc in colCharCounts) maxColShare = Math.Max(maxColShare, (double)cc / totalCharsAsym);
                double dominantThreshold = layoutGuided ? 0.92 : 0.85;
                if (maxColShare > dominantThreshold) return null;

                if (!layoutGuided)
                {
                    for (int c = 0; c < numCols; c++)
                    {
                        double charShare = (double)colCharCounts[c] / totalCharsAsym;
                        int emptyInCol = 0;
                        for (int r = 1; r < processed.Count; r++)
                            if (c >= processed[r].Count || processed[r][c].Trim().Length == 0) emptyInCol++;
                        double emptyRatio = (double)emptyInCol / dataRowCount;
                        if (charShare < 0.15 && emptyRatio > 0.5) return null;
                    }
                }
            }
        }

        // Row-to-row sentence continuation check.
        if (processed.Count > 3 && processed[0].Count >= 2)
        {
            int lastCol = processed[0].Count - 1;
            int continuation = 0, eligibleTransitions = 0;
            for (int r = 1; r + 1 < processed.Count; r++)
            {
                string prevLast = lastCol < processed[r].Count ? processed[r][lastCol].Trim() : "";
                string nextFirst = processed[r + 1].Count > 0 ? processed[r + 1][0].Trim() : "";
                if (prevLast.Length == 0 || nextFirst.Length == 0) continue;
                eligibleTransitions++;
                bool endsWithoutPunct = !prevLast.EndsWith('.') && !prevLast.EndsWith('?') && !prevLast.EndsWith('!') && !prevLast.EndsWith(':') && !prevLast.EndsWith(';');
                bool startsLower = nextFirst.Length > 0 && char.IsLower(nextFirst[0]);
                if (endsWithoutPunct && startsLower) continuation++;
            }
            if (eligibleTransitions >= 3 && continuation * 10 > eligibleTransitions * 4) return null;
        }

        // High-row low-column prose check.
        {
            int numCols = processed[0].Count;
            int numDataRows = processed.Count - 1;
            if (numDataRows > 20 && numCols <= 3)
            {
                int totalDataCells = numDataRows * numCols;
                int filledCells = 0;
                for (int r = 1; r < processed.Count; r++)
                    foreach (var cell in processed[r]) if (cell.Trim().Length > 0) filledCells++;
                if (totalDataCells > 0 && filledCells * 100 > totalDataCells * 80) return null;
            }
        }

        // Uniform column width check.
        {
            int numCols = processed[0].Count;
            int numDataRows = processed.Count - 1;
            if (numCols >= 3 && numCols <= 5 && numDataRows >= 5)
            {
                var colAvg = new double[numCols];
                for (int c = 0; c < numCols; c++)
                {
                    int totalLen = 0, count = 0;
                    for (int r = 1; r < processed.Count; r++)
                    {
                        string cell = c < processed[r].Count ? processed[r][c].Trim() : "";
                        if (cell.Length > 0) { totalLen += Utf8Len(cell); count++; }
                    }
                    colAvg[c] = count > 0 ? (double)totalLen / count : 0.0;
                }
                var textColAvgs = colAvg.Where(a => a > 15.0).ToList();
                if (textColAvgs.Count >= 3)
                {
                    double minAvg = textColAvgs.Min();
                    double maxAvg = textColAvgs.Max();
                    if (minAvg > 0.0 && maxAvg <= minAvg * 2.0)
                    {
                        int totalDataCells = numDataRows * numCols;
                        int filledCells = 0;
                        for (int r = 1; r < processed.Count; r++)
                            foreach (var cell in processed[r]) if (cell.Trim().Length > 0) filledCells++;
                        double fillRate = (double)filledCells / totalDataCells;
                        if (fillRate > 0.75) return null;
                    }
                }
            }
        }

        // Normalize cells.
        for (int i = 0; i < processed[0].Count; i++)
            processed[0][i] = processed[0][i].Trim().Replace("  ", " ");
        for (int r = 1; r < processed.Count; r++)
            for (int c = 0; c < processed[r].Count; c++)
                processed[r][c] = NormalizeDataCell(processed[r][c]);

        return processed;
    }

    private static void MergeHeaderOnlyColumn(List<List<string>> table, int col, string headerText)
    {
        if (table.Count == 0 || table[0].Count == 0) return;
        string trimmed = headerText.Trim();

        if (trimmed.Length == 0 && table.Count > 1)
        {
            foreach (var row in table) if (col < row.Count) row.RemoveAt(col);
            return;
        }

        if (trimmed.Length > 0)
        {
            if (col > 0)
            {
                int target = col - 1;
                while (target > 0 && table[0][target].Trim().Length == 0) target--;
                if (table[0][target].Trim().Length > 0 || target == 0)
                {
                    if (table[0][target].Length > 0) table[0][target] += " ";
                    table[0][target] += trimmed;
                    foreach (var row in table) if (col < row.Count) row.RemoveAt(col);
                    return;
                }
            }
            if (col + 1 < table[0].Count)
            {
                if (table[0][col + 1].Trim().Length == 0)
                    table[0][col + 1] = trimmed;
                else
                    table[0][col + 1] = trimmed + " " + table[0][col + 1].Trim();
                foreach (var row in table) if (col < row.Count) row.RemoveAt(col);
                return;
            }
        }

        foreach (var row in table) if (col < row.Count) row.RemoveAt(col);
    }

    private static string NormalizeDataCell(string cell)
    {
        string text = cell.Trim();
        if (text.Length == 0) return "";

        text = text.Replace('—', '-').Replace('–', '-').Replace('−', '-');

        if (text.StartsWith("- ")) text = "-" + text.Substring(2).TrimStart();

        text = text.Replace("- ", "-");
        text = text.Replace(" -", "-");
        text = text.Replace("E-", "e-").Replace("E+", "e+");

        if (text == "-") return "";
        return text;
    }

    // ── is_well_formed_table (pdf/table_reconstruct.rs) ──────────────────────

    internal static bool IsWellFormedTable(List<List<string>> grid)
    {
        if (grid.Count < 2) return false;
        int numCols = grid[0].Count;
        if (numCols < 2) return false;

        // Check 0: cell density.
        int maxCols = grid.Max(r => r.Count);
        int totalCells = grid.Count * maxCols;
        if (totalCells > 0)
        {
            int filled = grid.SelectMany(r => r).Count(cell => cell.Trim().Length > 0);
            int emptyCells = totalCells - filled;
            if (emptyCells * 100 > totalCells * 40) return false;
        }

        var dataRows = grid.GetRange(1, grid.Count - 1);

        // Check 1: row coherence (prose detection).
        if (dataRows.Count >= 3 && numCols >= 2)
        {
            int proseLike = 0, eligible = 0;
            foreach (var row in dataRows)
            {
                string concatenated = string.Join(" ", row.Select(c => c.Trim()).Where(c => c.Length > 0));
                if (Utf8Len(concatenated) < 15) continue;
                eligible++;
                int alpha = 0;
                foreach (var rune in concatenated.EnumerateRunes())
                    if (System.Text.Rune.IsLetter(rune) || System.Text.Rune.IsWhiteSpace(rune)) alpha++;
                double alphaRatio = (double)alpha / Utf8Len(concatenated);
                if (alphaRatio > 0.8) proseLike++;
            }
            if (eligible >= 3 && proseLike * 2 > eligible) return false;
        }

        // Check 2: column semantic uniformity.
        if (numCols >= 3 && dataRows.Count >= 4)
        {
            var colStats = new (double mean, double stddev)[numCols];
            for (int c = 0; c < numCols; c++)
            {
                var lengths = new List<double>();
                foreach (var row in dataRows)
                {
                    string cell = c < row.Count ? row[c].Trim() : "";
                    if (cell.Length > 0) lengths.Add(Utf8Len(cell));
                }
                if (lengths.Count == 0) { colStats[c] = (0.0, 0.0); continue; }
                double mean = lengths.Sum() / lengths.Count;
                double variance = lengths.Sum(l => (l - mean) * (l - mean)) / lengths.Count;
                colStats[c] = (mean, Math.Sqrt(variance));
            }
            var meaningful = colStats.Where(s => s.mean > 3.0).ToList();
            if (meaningful.Count >= 3)
            {
                double minMean = meaningful.Min(s => s.mean);
                double maxMean = meaningful.Max(s => s.mean);
                bool columnsUniform = minMean > 0.0 && maxMean <= minMean * 2.0;
                bool lowVariance = meaningful.All(s => s.mean > 0.0 && s.stddev / s.mean < 0.3);
                if (columnsUniform && lowVariance) return false;
            }
        }

        // Check 3: minimum meaningful content (repetitive vocabulary).
        if (numCols >= 3)
        {
            var uniqueWords = new HashSet<string>();
            foreach (var row in dataRows)
                foreach (var cell in row)
                    foreach (var w in SplitWhitespace(cell))
                        uniqueWords.Add(w);
            int rowCount = dataRows.Count;
            if (rowCount >= 3 && uniqueWords.Count < rowCount * 2) return false;
        }

        // Check 4: repeated header detection.
        {
            var header = grid[0];
            int headerMatches = dataRows.Count(row =>
                row.Count == header.Count && row.Zip(header, (a, b) => a.Trim() == b.Trim()).All(x => x));
            if (headerMatches >= 2) return false;
        }

        return true;
    }

    // ── looks_like_code_listing (pdf/table_reconstruct.rs) ───────────────────

    internal static bool LooksLikeCodeListing(List<List<string>> tableCells)
    {
        var nonEmpty = tableCells.SelectMany(r => r).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (nonEmpty.Count == 0) return false;
        if (nonEmpty.Any(cell => cell == "{" || cell == "}")) return true;
        int braceCount = nonEmpty.Count(cell => cell.Contains('{') || cell.Contains('}'));
        return (double)braceCount / nonEmpty.Count >= 0.20;
    }
}
