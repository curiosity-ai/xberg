// Cluster fallback for the ruling-line tier: what `detect_tables_with_lines`
// reaches when intersection detection came up empty, i.e. a ruled table whose
// horizontal and vertical rules never physically cross.
//
//   paths → line clusters (union-find on rendered extents) → per-cluster
//   H/V coordinate clustering → row/column bands → spans → merge spans → grid
//
// Unlike the intersection pipeline, nothing here requires a corner: the row and
// column boundaries come from clustering the rules' own centre coordinates, so a
// grid of separate H rules and separate V rules still yields cells.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Xberg.Internal.Pdf;

internal static partial class PdfSpatialTables
{
    /// <summary>Minimum rendered length for a rule to contribute a row or column boundary.</summary>
    private const double ClusterMinLineLength = 5.0;

    /// <summary>Bbox inflation used when testing two paths for cluster membership.</summary>
    private const double ClusterExpansion = 3.0;

    /// <summary>
    /// pdf_oxide `TableDetectionConfig::v_split_gap`. Both Lines-strategy passes xberg
    /// runs — `strict()` and the config `extract_tables_bordered` builds — set it to 4.0.
    /// </summary>
    private const double ClusterVSplitGap = 4.0;

    private const int MaxMaskColumns = 128;
    private const double MinSplitGroupRowShare = 0.20;

    /// <summary>
    /// NaN-safe total order over coordinates: NaNs compare equal to each other and
    /// greater than every number, so no sort can observe an inconsistent comparison.
    /// </summary>
    /// <remarks>
    /// Reproduces pdf_oxide's <c>utils::safe_float_cmp</c>. Not <c>double.CompareTo</c>,
    /// which orders NaN below everything and separates -0.0 from 0.0.
    /// </remarks>
    private static int SafeCmp(double a, double b)
    {
        bool na = double.IsNaN(a), nb = double.IsNaN(b);
        if (na && nb) return 0;
        if (na) return 1;
        if (nb) return -1;
        if (a < b) return -1;
        if (a > b) return 1;
        return 0;
    }

    private static readonly IComparer<double> SafeAsc = Comparer<double>.Create(SafeCmp);

    private static PathRect UnionRect(in PathRect a, in PathRect b)
    {
        double x0 = Math.Min(a.Left, b.Left), y0 = Math.Min(a.Top, b.Top);
        double x1 = Math.Max(a.Right, b.Right), y1 = Math.Max(a.Bottom, b.Bottom);
        return new PathRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// `regular_row_ratio`, the share of rows that must carry the modal populated-cell
    /// count. xberg's two Lines-strategy passes disagree on it — `strict()` demands 0.8,
    /// the bordered config 0.5 — and they are told apart by the same knob that separates
    /// the tiers everywhere else.
    /// </summary>
    private static double RegularRowRatio(TableDetectionConfig config) =>
        config.MinTableColumns >= 3 ? 0.8 : 0.5;

    // ── Line clusters ────────────────────────────────────────────────────────

    /// <summary>A connected group of table-primitive paths and the extent they cover.</summary>
    private sealed class LineCluster
    {
        public readonly List<int> Lines = new();
        public PathRect Bbox;

        public LineCluster(int lineIdx, PathRect bbox) { Lines.Add(lineIdx); Bbox = bbox; }

        public void Add(int lineIdx, PathRect bbox)
        {
            Lines.Add(lineIdx);
            Bbox = UnionRect(Bbox, bbox);
        }
    }

    /// <summary>
    /// Group table-primitive paths into clusters of mutually near-touching rules, then
    /// split any cluster whose vertical rules sit in disjoint Y bands.
    /// </summary>
    private static List<LineCluster> GroupLinesIntoClusters(List<PdfPath> lines, TableDetectionConfig config)
    {
        var result = new List<LineCluster>();
        if (lines.Count == 0) return result;

        // Every geometric test below works on rendered extents: a rule encoded as a 1 pt
        // segment with a table-height stroke width must cluster with the rules its drawn
        // bar actually touches, not the ones near its geometric speck.
        var rendered = lines.Select(p => p.RenderedBbox()).ToList();
        var uf = new UnionFind(lines.Count);

        var validIndices = Enumerable.Range(0, lines.Count)
            .Where(i => lines[i].IsTablePrimitive())
            .OrderBy(i => rendered[i].X, SafeAsc)
            .ToList();

        for (int i = 0; i < validIndices.Count; i++)
        {
            int idxA = validIndices[i];
            var bboxA = rendered[idxA];
            var expandedA = new PathRect(
                bboxA.X - ClusterExpansion, bboxA.Y - ClusterExpansion,
                bboxA.Width + ClusterExpansion * 2.0, bboxA.Height + ClusterExpansion * 2.0);

            for (int j = i + 1; j < validIndices.Count; j++)
            {
                int idxB = validIndices[j];
                var bboxB = rendered[idxB];
                // Sorted by X, so once a candidate starts past the search window no later
                // one can reach back into it.
                if (bboxB.X > expandedA.X + expandedA.Width) break;

                var expandedB = new PathRect(
                    bboxB.X - ClusterExpansion, bboxB.Y - ClusterExpansion,
                    bboxB.Width + ClusterExpansion * 2.0, bboxB.Height + ClusterExpansion * 2.0);
                if (expandedA.Intersects(expandedB)) uf.Union(idxA, idxB);
            }
        }

        var clusterMap = new Dictionary<int, LineCluster>();
        foreach (int i in validIndices)
        {
            int root = uf.Find(i);
            if (clusterMap.TryGetValue(root, out var existing)) existing.Add(i, rendered[i]);
            else clusterMap[root] = new LineCluster(i, rendered[i]);
        }

        // Sorted by first member so the downstream table order does not depend on hash
        // iteration order.
        var rawClusters = clusterMap.Values
            .OrderBy(c => c.Lines.Count > 0 ? c.Lines[0] : int.MaxValue)
            .ToList();

        foreach (var cluster in rawClusters)
        {
            var vRanges = new List<(int Idx, double YMin, double YMax)>();
            foreach (int idx in cluster.Lines)
            {
                if (!IsVerticalLine(lines[idx], LineAxisTol)) continue;
                var r = rendered[idx];
                if (Math.Abs(r.Height) <= ClusterMinLineLength) continue;
                double yMin = r.Y, yMax = r.Y + r.Height;
                if (yMin > yMax) (yMin, yMax) = (yMax, yMin);
                vRanges.Add((idx, yMin, yMax));
            }

            if (vRanges.Count < 2) { result.Add(cluster); continue; }

            vRanges = vRanges.OrderBy(v => v.YMin, SafeAsc).ToList();
            var bands = new List<(double Min, double Max)>();
            double bandStart = vRanges[0].YMin, bandEnd = vRanges[0].YMax;
            for (int k = 1; k < vRanges.Count; k++)
            {
                if (vRanges[k].YMin > bandEnd + ClusterVSplitGap)
                {
                    bands.Add((bandStart, bandEnd));
                    bandStart = vRanges[k].YMin;
                    bandEnd = vRanges[k].YMax;
                }
                else bandEnd = Math.Max(bandEnd, vRanges[k].YMax);
            }
            bands.Add((bandStart, bandEnd));

            // One contiguous Y range means one table; a gap keeps a small bordered block
            // (an invoice header, say) from absorbing the main table below it.
            if (bands.Count < 2) { result.Add(cluster); continue; }

            var subClusters = new List<int>[bands.Count];
            for (int b = 0; b < bands.Count; b++) subClusters[b] = new List<int>();
            foreach (int idx in cluster.Lines)
            {
                var bbox = rendered[idx];
                double lineYMid = bbox.Y + bbox.Height * 0.5;
                int bestBand = 0;
                double bestDist = double.MaxValue;
                for (int bi = 0; bi < bands.Count; bi++)
                {
                    var (bMin, bMax) = bands[bi];
                    double dist = lineYMid >= bMin && lineYMid <= bMax
                        ? 0.0
                        : Math.Min(Math.Abs(lineYMid - bMin), Math.Abs(lineYMid - bMax));
                    if (dist < bestDist) { bestDist = dist; bestBand = bi; }
                }
                subClusters[bestBand].Add(idx);
            }

            foreach (var sub in subClusters)
            {
                if (sub.Count == 0) continue;
                var lc = new LineCluster(sub[0], rendered[sub[0]]);
                for (int k = 1; k < sub.Count; k++) lc.Add(sub[k], rendered[sub[k]]);
                result.Add(lc);
            }
        }

        return result;
    }

    // ── Grid model ───────────────────────────────────────────────────────────

    private sealed class ColumnCluster
    {
        public double XCenter, XMin, XMax;
    }

    private sealed class RowCluster
    {
        public double YCenter, YMin, YMax;
    }

    /// <summary>Column and row bands plus the span indices falling in each cell.</summary>
    private sealed class GridStructure
    {
        public List<ColumnCluster> Columns = new();
        public List<RowCluster> Rows = new();
        public List<List<int>[]> Cells = new();

        public bool IsRowEmpty(int rowIdx) => Cells[rowIdx].All(cell => cell.Count == 0);

        public bool IsColumnEmpty(int colIdx)
        {
            foreach (var row in Cells) if (row[colIdx].Count != 0) return false;
            return true;
        }

        public GridStructure Clone() => new()
        {
            Columns = Columns.ToList(),
            Rows = Rows.ToList(),
            Cells = Cells.Select(r => r.Select(c => new List<int>(c)).ToArray()).ToList(),
        };

        /// <summary>
        /// Drop leading and trailing empty columns, plus interior hairline columns that
        /// hold nothing — a doubled rule projects one of those and it would otherwise
        /// count against the empty-cell ratio.
        /// </summary>
        public GridStructure TrimEmptyColumns()
        {
            int numRows = Cells.Count;
            int numCols = Columns.Count;

            int firstCol = 0;
            while (firstCol < numCols && IsColumnEmpty(firstCol)) firstCol++;

            int lastCol = numCols;
            while (lastCol > firstCol && IsColumnEmpty(lastCol - 1)) lastCol--;

            if (firstCol >= lastCol) return Clone();

            var activeCols = new List<int>();
            for (int c = firstCol; c < lastCol; c++)
            {
                double colWidth = Columns[c].XMax - Columns[c].XMin;
                if (colWidth < 2.0 && IsColumnEmpty(c)) continue;
                activeCols.Add(c);
            }
            if (activeCols.Count == 0) return Clone();

            var trimmed = new GridStructure
            {
                Columns = activeCols.Select(c => Columns[c]).ToList(),
                Rows = Rows.ToList(),
            };
            for (int r = 0; r < numRows; r++)
                trimmed.Cells.Add(activeCols.Select(c => new List<int>(Cells[r][c])).ToArray());
            return trimmed;
        }
    }

    private struct CellMergeInfo
    {
        public int Colspan;
        public int Rowspan;
        public bool Covered;
    }

    /// <summary>Running-mean 1-D clustering: each value joins the first cluster within tolerance.</summary>
    private static List<double> ClusterValues(List<double> values, double tolerance)
    {
        var clusters = new List<double>();
        var counts = new List<int>();
        foreach (double v in values)
        {
            int idx = clusters.FindIndex(c => Math.Abs(v - c) < tolerance);
            if (idx >= 0)
            {
                counts[idx]++;
                clusters[idx] += (v - clusters[idx]) / counts[idx];
            }
            else
            {
                clusters.Add(v);
                counts.Add(1);
            }
        }
        return clusters;
    }

    // ── Header row above the grid ────────────────────────────────────────────

    /// <summary>
    /// The y of a new top boundary bracketing a header row that sits just above the
    /// grid's top ruling — a header boxed only by the page top, or left unruled.
    /// Null when no such row is there.
    /// </summary>
    /// <remarks>
    /// The distinct-columns and horizontal-span gates are what keep a centred title or a
    /// left-aligned caption above an already-correct table from being swallowed as a
    /// header row: their words cluster together instead of spanning the columns.
    /// </remarks>
    private static double? DetectHeaderRowAbove(List<TableSpan> spans, List<double> rowYs, List<double> colXs)
    {
        // Header band reaches at most this multiple of the median row height above the top ruling.
        const double windowRows = 1.5;
        // Header cells must reach across at least this fraction of the column extent.
        const double minSpanFrac = 0.5;

        if (rowYs.Count < 2 || colXs.Count < 2) return null;
        double gridTop = rowYs[0];
        double colLo = colXs[0], colHi = colXs[^1];
        double colExtent = colHi - colLo;
        if (colExtent <= 0.0) return null;

        var gaps = new List<double>();
        for (int i = 0; i + 1 < rowYs.Count; i++) gaps.Add(Math.Abs(rowYs[i] - rowYs[i + 1]));
        gaps = gaps.OrderBy(g => g, SafeAsc).ToList();
        double medianRowH = gaps[gaps.Count / 2];
        if (medianRowH <= 0.0) return null;
        double windowTop = gridTop + windowRows * medianRowH;

        var colsHit = new List<int>();
        double headerMaxTop = double.NegativeInfinity;
        double cxMin = double.PositiveInfinity, cxMax = double.NegativeInfinity;
        double cyMin = double.PositiveInfinity, cyMax = double.NegativeInfinity;
        foreach (var span in spans)
        {
            double cy = span.CenterY;
            if (cy <= gridTop || cy > windowTop) continue;
            double cx = span.CenterX;
            // Outside the column extent means the text is not aligned to any column.
            if (cx < colLo || cx > colHi) continue;
            int ci = -1;
            for (int c = 0; c < colXs.Count - 1; c++)
                if (cx >= colXs[c] && cx <= colXs[c + 1]) { ci = c; break; }
            if (ci < 0) continue;

            if (!colsHit.Contains(ci)) colsHit.Add(ci);
            // `Bottom` is the larger y, so the visually upper edge of the header text.
            headerMaxTop = Math.Max(headerMaxTop, span.Bbox.Bottom);
            cxMin = Math.Min(cxMin, cx);
            cxMax = Math.Max(cxMax, cx);
            cyMin = Math.Min(cyMin, cy);
            cyMax = Math.Max(cyMax, cy);
        }

        if (colsHit.Count < 2) return null;
        if (cxMax - cxMin < minSpanFrac * colExtent) return null;
        if (cyMax - cyMin > medianRowH) return null;

        return headerMaxTop + 1.0;
    }

    // ── Per-cluster detection ────────────────────────────────────────────────

    /// <summary>
    /// The cluster half of `detect_tables_with_lines`'s Lines/Lines arm: the fallback
    /// that runs when no cell could be built from rule intersections.
    /// </summary>
    internal static List<GridTable> DetectTablesInClusters(
        List<TableSpan> spans, List<PdfPath> lines, TableDetectionConfig config)
    {
        var tables = new List<GridTable>();
        foreach (var cluster in GroupLinesIntoClusters(lines, config))
            tables.AddRange(DetectTablesInCluster(spans, lines, cluster, config));
        return tables.Where(IsValidTable).ToList();
    }

    private static List<GridTable> DetectTablesInCluster(
        List<TableSpan> spans, List<PdfPath> allLines, LineCluster cluster, TableDetectionConfig config)
    {
        var empty = new List<GridTable>();

        var hYs = new List<double>();
        var vXs = new List<double>();
        foreach (int idx in cluster.Lines)
        {
            var path = allLines[idx];
            // Rendered extents: a stroke-width-encoded rule's centre and length come from
            // the drawn bar, not the geometric speck.
            var bbox = path.RenderedBbox();
            if (IsHorizontalLine(path, LineAxisTol) && bbox.Width > ClusterMinLineLength)
                hYs.Add(bbox.CenterY);
            if (IsVerticalLine(path, LineAxisTol) && Math.Abs(bbox.Height) > ClusterMinLineLength)
                vXs.Add(bbox.CenterX);
        }

        var rowYs = ClusterValues(hYs, config.RowTolerance);
        var colXs = ClusterValues(vXs, config.ColumnTolerance);
        if (rowYs.Count < 2 || colXs.Count < 2) return empty;

        // Rows read top-to-bottom, which is descending y.
        rowYs = rowYs.OrderByDescending(v => v, SafeAsc).ToList();
        colXs = colXs.OrderBy(v => v, SafeAsc).ToList();

        // A header row above the grid's top ruling adds ONE row boundary and widens the
        // span-assignment region to reach it; it never adds columns.
        var assignBbox = cluster.Bbox;
        bool insertedHeaderRow = false;
        if (DetectHeaderRowAbove(spans, rowYs, colXs) is { } headerTop)
        {
            rowYs.Insert(0, headerTop);
            insertedHeaderRow = true;
            double newHeight = Math.Max(headerTop - assignBbox.Y, assignBbox.Height);
            assignBbox = new PathRect(assignBbox.X, assignBbox.Y, assignBbox.Width, newHeight);
        }

        int numRows = rowYs.Count - 1;
        int numCols = colXs.Count - 1;
        if (numCols < config.MinTableColumns || numCols > config.MaxTableColumns) return empty;

        var cells = new List<List<int>[]>(numRows);
        for (int r = 0; r < numRows; r++)
        {
            var row = new List<int>[numCols];
            for (int c = 0; c < numCols; c++) row[c] = new List<int>();
            cells.Add(row);
        }

        bool assignedAny = false;
        for (int origIdx = 0; origIdx < spans.Count; origIdx++)
        {
            var span = spans[origIdx];
            if (!assignBbox.Intersects(span.Bbox)) continue;
            double cx = span.CenterX, cy = span.CenterY;
            int rowIdx = -1;
            for (int r = 0; r < numRows; r++) if (cy <= rowYs[r] && cy >= rowYs[r + 1]) { rowIdx = r; break; }
            int colIdx = -1;
            for (int c = 0; c < numCols; c++) if (cx >= colXs[c] && cx <= colXs[c + 1]) { colIdx = c; break; }
            if (rowIdx >= 0 && colIdx >= 0)
            {
                cells[rowIdx][colIdx].Add(origIdx);
                assignedAny = true;
            }
        }
        if (!assignedAny) return empty;

        var columns = new List<ColumnCluster>(numCols);
        for (int c = 0; c < numCols; c++)
            columns.Add(new ColumnCluster
            {
                XCenter = (colXs[c] + colXs[c + 1]) / 2.0, XMin = colXs[c], XMax = colXs[c + 1],
            });
        var allRows = new List<RowCluster>(numRows);
        for (int r = 0; r < numRows; r++)
            allRows.Add(new RowCluster
            {
                YCenter = (rowYs[r] + rowYs[r + 1]) / 2.0, YMin = rowYs[r + 1], YMax = rowYs[r],
            });

        var gridFull = new GridStructure { Columns = columns, Rows = allRows, Cells = cells };

        var tables = new List<GridTable>();
        int currentStartRow = 0;
        while (currentStartRow < numRows)
        {
            if (gridFull.IsRowEmpty(currentStartRow)) { currentStartRow++; continue; }
            int currentEndRow = currentStartRow;
            while (currentEndRow < numRows && !gridFull.IsRowEmpty(currentEndRow)) currentEndRow++;

            if (currentEndRow > currentStartRow)
            {
                var grid = new GridStructure
                {
                    Columns = columns,
                    Rows = allRows.GetRange(currentStartRow, currentEndRow - currentStartRow),
                    Cells = cells.GetRange(currentStartRow, currentEndRow - currentStartRow),
                }.TrimEmptyColumns();

                if (ValidateClusterGrid(grid, config))
                {
                    // The inserted header row is global row 0, so it lands in the first
                    // non-empty run: local row 0 when that run starts at 0.
                    int protectedHeaderRows = insertedHeaderRow && currentStartRow == 0 ? 1 : 0;
                    var mergeInfo = DetectMergedCellsVisually(
                        grid, spans, cluster, allLines, protectedHeaderRows);
                    var table = GridToTable(grid, spans, mergeInfo);

                    double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
                    foreach (var r in grid.Rows)
                    {
                        minY = Math.Min(minY, r.YMin);
                        maxY = Math.Max(maxY, r.YMax);
                    }
                    table.Bbox = new PathRect(cluster.Bbox.X, minY, cluster.Bbox.Width, maxY - minY);

                    int headerRowsDetected = 0;
                    double tableWidth = cluster.Bbox.Width;
                    for (int r = 0; r < Math.Min(table.Rows.Count, 3); r++)
                    {
                        double rowBottom = grid.Rows[r].YMin;
                        bool hasSeparator = cluster.Lines.Any(idx =>
                        {
                            var path = allLines[idx];
                            var rendered = path.RenderedBbox();
                            return IsHorizontalLine(path, LineAxisTol)
                                && rendered.Width > tableWidth * 0.8
                                && Math.Abs(rendered.CenterY - rowBottom) < config.RowTolerance;
                        });
                        if (hasSeparator) headerRowsDetected = r + 1;
                        else if (r == 0 && RowHasColspan(mergeInfo[r])) headerRowsDetected = 1;
                        else break;
                    }
                    if (headerRowsDetected > 0)
                    {
                        table.HasHeader = true;
                        for (int r = 0; r < headerRowsDetected && r < table.Rows.Count; r++)
                        {
                            table.Rows[r].IsHeader = true;
                            foreach (var cell in table.Rows[r].Cells) cell.IsHeader = true;
                        }
                    }

                    tables.Add(table);
                }
            }
            currentStartRow = currentEndRow + 1;
        }
        return tables;
    }

    private static bool RowHasColspan(CellMergeInfo[] row)
    {
        foreach (var mi in row) if (!mi.Covered && mi.Colspan > 1) return true;
        return false;
    }

    // ── Structural validation ────────────────────────────────────────────────

    /// <summary>
    /// `validate_table_structure_internal`: enough populated cells, a dominant row shape,
    /// and no sign of two independent flows having been clustered as one table.
    /// </summary>
    private static bool ValidateClusterGrid(GridStructure grid, TableDetectionConfig config)
    {
        int numCols = grid.Columns.Count;
        int totalCells = 0;
        var cellCounts = new List<int>(grid.Cells.Count);
        foreach (var row in grid.Cells)
        {
            int populated = 0;
            for (int c = 0; c < numCols && c < row.Length; c++) if (row[c].Count != 0) populated++;
            totalCells += populated;
            cellCounts.Add(populated);
        }
        if (totalCells < config.MinTableCells) return false;
        if (cellCounts.Count == 0) return false;

        // Rust's `max_by_key` keeps the LAST maximum, so ties go to the later count.
        int mostCommonCount = 0, bestFrequency = -1;
        foreach (int count in cellCounts)
        {
            int frequency = cellCounts.Count(c => c == count);
            if (frequency >= bestFrequency) { bestFrequency = frequency; mostCommonCount = count; }
        }
        if (mostCommonCount == 0) return false;

        int regularRows = cellCounts.Count(c => c == mostCommonCount);
        if ((double)regularRows / cellCounts.Count < RegularRowRatio(config)) return false;

        return !HasSplitModalColumnGroups(grid, mostCommonCount);
    }

    /// <summary>
    /// Whether the modal rows' populated columns fall into two or more disconnected
    /// co-occurrence components, each backed by a real share of those rows — the
    /// signature of two prose flows mis-clustered as one table.
    /// </summary>
    /// <remarks>
    /// Restricting the graph to modal rows is what keeps hierarchical tables, whose data
    /// rows are sparse but internally connected, from tripping it. Heuristic, not
    /// corpus-calibrated.
    /// </remarks>
    private static bool HasSplitModalColumnGroups(GridStructure grid, int mostCommonCount)
    {
        int numCols = grid.Columns.Count;

        // A meaningful split needs at least 4 columns (two groups of >= 2) and at least
        // 2 populated cells per modal row.
        if (numCols < 4 || numCols > MaxMaskColumns || mostCommonCount < 2) return false;

        var modalRows = new List<List<int>>();
        foreach (var row in grid.Cells)
        {
            var populated = new List<int>();
            for (int c = 0; c < numCols && c < row.Length; c++) if (row[c].Count != 0) populated.Add(c);
            if (populated.Count != mostCommonCount || populated.Count < 2) continue;
            modalRows.Add(populated);
        }

        // Too few modal rows and the share threshold means nothing.
        if (modalRows.Count < 4) return false;

        // Floored at 2 so a lone outlier row can never be its own "significant" component.
        int minComponentRows = Math.Max((int)Math.Ceiling(modalRows.Count * MinSplitGroupRowShare), 2);

        var adjacency = new HashSet<int>[numCols];
        for (int c = 0; c < numCols; c++) adjacency[c] = new HashSet<int>();
        var activeColumns = new SortedSet<int>();
        foreach (var row in modalRows)
            foreach (int col in row)
            {
                activeColumns.Add(col);
                foreach (int other in row) adjacency[col].Add(other);
            }

        var remaining = new SortedSet<int>(activeColumns);
        int significantComponents = 0;
        while (remaining.Count > 0)
        {
            var component = new HashSet<int>();
            var frontier = new Stack<int>();
            frontier.Push(remaining.Min);
            while (frontier.Count > 0)
            {
                int col = frontier.Pop();
                if (!component.Add(col)) continue;
                foreach (int next in adjacency[col]) if (!component.Contains(next)) frontier.Push(next);
            }
            remaining.ExceptWith(component);

            int componentRowSupport = modalRows.Count(row => row.Any(component.Contains));
            if (component.Count >= 2 && componentRowSupport >= minComponentRows)
            {
                significantComponents++;
                if (significantComponents >= 2) return true;
            }
        }
        return false;
    }

    // ── Merged cells ─────────────────────────────────────────────────────────

    /// <summary>
    /// Colspans and rowspans read off the cluster's own rules: a cell extends across the
    /// next boundary when no rule is drawn there.
    /// </summary>
    private static CellMergeInfo[][] DetectMergedCellsVisually(
        GridStructure grid, List<TableSpan> spans, LineCluster cluster,
        List<PdfPath> allLines, int protectedHeaderRows)
    {
        int numRows = grid.Cells.Count;
        int numCols = grid.Columns.Count;
        const double lineTolerance = 2.0;

        var mergeInfo = new CellMergeInfo[numRows][];
        for (int r = 0; r < numRows; r++)
        {
            mergeInfo[r] = new CellMergeInfo[numCols];
            for (int c = 0; c < numCols; c++) mergeInfo[r][c] = new CellMergeInfo { Colspan = 1, Rowspan = 1 };
        }

        for (int r = 0; r < numRows; r++)
        {
            // A header row reconstructed from the unruled strip above the grid has no
            // vertical rules in its band, which would colspan-merge its columns into one
            // and drop every cell but the first. Its cells were already verified to align
            // to separate columns.
            if (r < protectedHeaderRows) continue;

            int c = 0;
            while (c < numCols)
            {
                if (mergeInfo[r][c].Covered) { c++; continue; }

                int colspan = 1;
                double cellTextWidth = 0.0;
                foreach (int idx in grid.Cells[r][c])
                    if (idx >= 0 && idx < spans.Count)
                        cellTextWidth = Math.Max(cellTextWidth, spans[idx].Bbox.Width);
                double totalCellWidth = grid.Columns[c].XMax - grid.Columns[c].XMin;

                for (int nextC = c + 1; nextC < numCols; nextC++)
                {
                    double separatorX = grid.Columns[nextC].XMin;
                    double yMin = grid.Rows[r].YMin;
                    double yMax = grid.Rows[r].YMax;
                    bool hasSeparator = cluster.Lines.Any(idx =>
                    {
                        var path = allLines[idx];
                        // Rendered extents: a stroke-width-encoded column rule crosses
                        // every row its drawn bar spans, not just the band around its
                        // geometric midline.
                        var rendered = path.RenderedBbox();
                        return IsVerticalLine(path, lineTolerance)
                            && Math.Abs(rendered.CenterX - separatorX) < lineTolerance
                            && rendered.Y < yMax
                            && rendered.Y + rendered.Height > yMin;
                    });
                    if (!hasSeparator || cellTextWidth > totalCellWidth + 2.0)
                    {
                        colspan++;
                        totalCellWidth += grid.Columns[nextC].XMax - grid.Columns[nextC].XMin;
                    }
                    else break;
                }

                if (colspan > 1)
                {
                    mergeInfo[r][c].Colspan = colspan;
                    for (int i = 1; i < colspan; i++) mergeInfo[r][c + i].Covered = true;
                }
                c += colspan;
            }
        }

        for (int c = 0; c < numCols; c++)
        {
            int r = 0;
            while (r < numRows)
            {
                if (mergeInfo[r][c].Covered) { r++; continue; }

                int rowspan = 1;
                int currentColspan = mergeInfo[r][c].Colspan;
                for (int nextR = r + 1; nextR < numRows; nextR++)
                {
                    double separatorY = grid.Rows[nextR].YMax;
                    double xMin = grid.Columns[c].XMin;
                    double xMax = grid.Columns[c + currentColspan - 1].XMax;
                    bool hasSeparator = cluster.Lines.Any(idx =>
                    {
                        var path = allLines[idx];
                        var rendered = path.RenderedBbox();
                        return IsHorizontalLine(path, lineTolerance)
                            && Math.Abs(rendered.CenterY - separatorY) < lineTolerance
                            && rendered.X < xMax
                            && rendered.X + rendered.Width > xMin;
                    });
                    if (!hasSeparator) rowspan++;
                    else break;
                }

                if (rowspan > 1)
                {
                    mergeInfo[r][c].Rowspan = rowspan;
                    for (int i = 1; i < rowspan; i++)
                    {
                        mergeInfo[r + i][c].Covered = true;
                        for (int j = 1; j < currentColspan; j++) mergeInfo[r + i][c + j].Covered = true;
                    }
                }
                r += rowspan;
            }
        }

        return mergeInfo;
    }

    // ── Grid → table ─────────────────────────────────────────────────────────

    private static GridTable GridToTable(GridStructure grid, List<TableSpan> spans, CellMergeInfo[][] mergeInfo)
    {
        int numCols = grid.Columns.Count;
        int? headerRowIdx = DetectHeaderRow(grid, spans);

        var tableRows = new List<GridRow>(grid.Cells.Count);
        for (int rowIdx = 0; rowIdx < grid.Cells.Count; rowIdx++)
        {
            var row = grid.Cells[rowIdx];
            bool isHeader = headerRowIdx == rowIdx;
            var tableRow = new GridRow(isHeader);
            for (int colIdx = 0; colIdx < numCols && colIdx < row.Length; colIdx++)
            {
                // A covered cell contributes nothing: its content already sits in the
                // spanning cell that swallowed it.
                if (mergeInfo[rowIdx][colIdx].Covered) continue;

                var cellSpanIndices = row[colIdx];
                PathRect? cellBbox = null;
                if (cellSpanIndices.Count > 0)
                {
                    var b = spans[cellSpanIndices[0]].Bbox;
                    for (int k = 1; k < cellSpanIndices.Count; k++)
                        b = UnionRect(b, spans[cellSpanIndices[k]].Bbox);
                    cellBbox = b;
                }

                tableRow.Cells.Add(new GridCell
                {
                    Text = ExtractCellText(cellSpanIndices, spans),
                    SpanIndices = new List<int>(cellSpanIndices),
                    Bbox = cellBbox,
                    IsHeader = isHeader,
                });
            }
            tableRows.Add(tableRow);
        }

        PathRect? bbox = null;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var row in grid.Cells)
            foreach (var cell in row)
                foreach (int idx in cell)
                {
                    if (idx < 0 || idx >= spans.Count) continue;
                    var b = spans[idx].Bbox;
                    minX = Math.Min(minX, b.X);
                    minY = Math.Min(minY, b.Y);
                    maxX = Math.Max(maxX, b.X + b.Width);
                    maxY = Math.Max(maxY, b.Y + b.Height);
                }
        if (!double.IsInfinity(minX)) bbox = new PathRect(minX, minY, maxX - minX, maxY - minY);

        return new GridTable
        {
            Rows = tableRows,
            HasHeader = headerRowIdx.HasValue,
            ColCount = numCols,
            Bbox = bbox,
        };
    }

    /// <summary>
    /// Whether row 0 reads as a header: type set noticeably larger than the data rows.
    /// </summary>
    /// <remarks>
    /// pdf_oxide also takes a bold-versus-data-weight signal here, which the word-sized
    /// spans the detector is fed do not carry.
    /// </remarks>
    private static int? DetectHeaderRow(GridStructure grid, List<TableSpan> spans)
    {
        if (grid.Cells.Count < 2) return null;

        var firstRowSpans = grid.Cells[0]
            .SelectMany(cell => cell).Where(i => i >= 0 && i < spans.Count).Select(i => spans[i]).ToList();
        if (firstRowSpans.Count == 0) return null;

        var dataRowSpans = new List<TableSpan>();
        for (int r = 1; r < grid.Cells.Count; r++)
            foreach (var cell in grid.Cells[r])
                foreach (int i in cell)
                    if (i >= 0 && i < spans.Count) dataRowSpans.Add(spans[i]);
        if (dataRowSpans.Count == 0) return null;

        double firstRowAvgSize = firstRowSpans.Sum(s => s.FontSize) / firstRowSpans.Count;
        double dataAvgSize = dataRowSpans.Sum(s => s.FontSize) / dataRowSpans.Count;
        return firstRowAvgSize > dataAvgSize + 1.5 ? 0 : null;
    }
}
