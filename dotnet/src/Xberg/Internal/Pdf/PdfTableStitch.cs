using System;
using System.Collections.Generic;
using System.Linq;
using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>
/// Port of `pdf/structure/pipeline.rs`'s `stitch_fragmented_tables` and friends
/// (crates/xberg/src/pdf/structure/pipeline.rs:2553-2843).
/// </summary>
/// <remarks>
/// The word-region clustering that feeds heuristic table reconstruction splits a page's
/// words at any row gap wider than <c>median_height * 1.8</c>. A table whose header wraps
/// onto several lines, or whose rows are generously leaded, therefore lands in several
/// regions, each of which independently goes through header/data post-processing — which
/// corrupts a real multi-line header and mis-promotes a lone data row to a fake header.
/// This pass reassembles those fragments after the fact: every fragment's rows are
/// column-merged into exactly one row, the topmost fragment becoming the header and the
/// rest data rows, and any trailing rows that fell below the last fragment without ever
/// becoming a fragment themselves are recovered from the raw page segments.
///
/// Upstream runs this only on the way into the structure pipeline
/// (`extract_document_structure_from_segments`); the `tables[]` array a document emits is
/// built from the unstitched fragment list in `extractors/pdf/extraction.rs`, so this must
/// not be applied to the emitted list.
/// </remarks>
internal static class PdfTableStitch
{
    /// <summary>Maximum vertical gap (PDF points) between one fragment's bottom edge and the
    /// next fragment's top edge for the two to be the same physical table.</summary>
    private const double YGapTolerancePts = 4.0;

    /// <summary>Maximum difference in a chain's shared left/right edge for two fragments to be
    /// considered the same table rather than two unrelated neighbours.</summary>
    private const double XTolerancePts = 6.0;

    /// <summary>Bound on fragments merged into one stitched chain.</summary>
    private const int MaxChainFragments = 12;

    /// <summary>Bound on additional data rows the trailing-continuation recovery pass pulls
    /// from raw page segments below a stitched chain's last known fragment.</summary>
    private const int TrailingRecoveryMaxRows = 6;

    /// <summary>Row-gap multiplier used to split recovered trailing words into per-entity
    /// bands, mirroring the region clustering's own `row_gap_split`.</summary>
    private const float TrailingRowGapMultiplier = 1.8f;

    /// <summary>
    /// Stitch table fragments that row-gap region clustering split out of one physical table
    /// back into a single table. Fragments without a bounding box pass through first, then
    /// each page's fragments in ascending page order, as upstream does.
    /// </summary>
    public static List<Table> StitchFragmentedTables(List<Table> tables, List<List<SegmentData>> allPageSegments)
    {
        var byPage = new Dictionary<uint, List<Table>>();
        var unbboxed = new List<Table>();
        foreach (var table in tables)
        {
            if (table.BoundingBox is not null)
            {
                if (!byPage.TryGetValue(table.PageNumber, out var list))
                {
                    list = new List<Table>();
                    byPage[table.PageNumber] = list;
                }
                list.Add(table);
            }
            else unbboxed.Add(table);
        }

        var result = new List<Table>(unbboxed);
        foreach (uint pageNumber in byPage.Keys.OrderBy(p => p))
            result.AddRange(StitchPageTables(byPage[pageNumber], allPageSegments));
        return result;
    }

    /// <summary>Stitch one page's table fragments.</summary>
    private static List<Table> StitchPageTables(List<Table> fragments, List<List<SegmentData>> allPageSegments)
    {
        // Topmost first. `sort_by` upstream is stable, as is OrderByDescending here.
        var sorted = fragments
            .OrderByDescending(t => t.BoundingBox is { } b ? b.Y1 : double.MinValue)
            .ToList();

        var output = new List<Table>(sorted.Count);
        int index = 0;
        while (index < sorted.Count)
        {
            int chainEnd = index + 1;
            while (chainEnd < sorted.Count
                   && chainEnd - index < MaxChainFragments
                   && FragmentsAreStitchable(sorted[chainEnd - 1], sorted[chainEnd]))
                chainEnd++;

            if (chainEnd - index >= 2)
                output.Add(MergeTableChain(sorted.GetRange(index, chainEnd - index), allPageSegments));
            else
                output.Add(sorted[index]);
            index = chainEnd;
        }
        return output;
    }

    /// <summary>
    /// Whether <paramref name="next"/> is the vertically-adjacent continuation of
    /// <paramref name="prev"/>: same page, same column count, near-zero row gap, matching
    /// left/right edges.
    /// </summary>
    private static bool FragmentsAreStitchable(Table prev, Table next)
    {
        if (prev.PageNumber != next.PageNumber) return false;
        if (prev.BoundingBox is not { } a || next.BoundingBox is not { } b) return false;

        int prevCols = prev.Cells.Count > 0 ? prev.Cells[0].Count : 0;
        int nextCols = next.Cells.Count > 0 ? next.Cells[0].Count : 0;
        if (prevCols == 0 || prevCols != nextCols) return false;

        return Math.Abs(a.Y0 - b.Y1) <= YGapTolerancePts
            && Math.Abs(a.X0 - b.X0) <= XTolerancePts
            && Math.Abs(a.X1 - b.X1) <= XTolerancePts;
    }

    /// <summary>
    /// Merge a chain of two or more stitchable fragments into one table: the topmost
    /// fragment's rows collapse into the header, every other fragment's rows into one data
    /// row apiece.
    /// </summary>
    private static Table MergeTableChain(List<Table> chain, List<List<SegmentData>> allPageSegments)
    {
        int columnCount = 0;
        foreach (var table in chain)
            if (table.Cells.Count > 0) columnCount = Math.Max(columnCount, table.Cells[0].Count);

        uint pageNumber = chain[0].PageNumber;

        BoundingBox bbox = new BoundingBox { X0 = 0.0, Y0 = 0.0, X1 = 0.0, Y1 = 0.0 };
        foreach (var table in chain)
            if (table.BoundingBox is { } first) { bbox = new BoundingBox { X0 = first.X0, Y0 = first.Y0, X1 = first.X1, Y1 = first.Y1 }; break; }

        foreach (var table in chain)
        {
            if (table.BoundingBox is not { } b) continue;
            bbox.X0 = Math.Min(bbox.X0, b.X0);
            bbox.X1 = Math.Max(bbox.X1, b.X1);
            bbox.Y0 = Math.Min(bbox.Y0, b.Y0);
            bbox.Y1 = Math.Max(bbox.Y1, b.Y1);
        }

        var rows = new List<List<string>>(chain.Count);
        foreach (var table in chain)
            rows.Add(MergeRowsColumnwise(table.Cells, columnCount));

        // `page_number.saturating_sub(1)`: an unnumbered fragment reads page 0's segments,
        // as upstream does, rather than skipping recovery.
        int pageIndex = pageNumber == 0 ? 0 : (int)pageNumber - 1;
        if (pageIndex < allPageSegments.Count)
            RecoverTrailingContinuationRows(rows, bbox, columnCount, allPageSegments[pageIndex]);

        return new Table
        {
            Cells = rows,
            Markdown = PdfTableReconstruct.TableToMarkdown(rows),
            PageNumber = pageNumber,
            BoundingBox = bbox,
            Columns = rows.Count > 0 ? new List<string>(rows[0]) : null,
        };
    }

    /// <summary>
    /// Port of `pdf::table_reconstruct::merge_rows_columnwise`: one row whose i-th cell is the
    /// space-joined text of every non-empty i-th cell across <paramref name="rows"/>.
    /// </summary>
    internal static List<string> MergeRowsColumnwise(List<List<string>> rows, int columnCount)
    {
        var merged = new List<string>(columnCount);
        for (int i = 0; i < columnCount; i++) merged.Add("");
        foreach (var row in rows)
        {
            int limit = Math.Min(row.Count, columnCount);
            for (int idx = 0; idx < limit; idx++)
            {
                string trimmed = row[idx].Trim();
                if (trimmed.Length == 0) continue;
                merged[idx] = merged[idx].Length == 0 ? trimmed : merged[idx] + " " + trimmed;
            }
        }
        return merged;
    }

    /// <summary>
    /// Recover trailing data rows that never became their own table fragment, by scanning the
    /// raw page segments strictly below the stitched chain's bottom edge and within its column
    /// span. A band is accepted only if reconstructing it independently yields the same column
    /// count; any mismatch stops recovery rather than skipping past it.
    /// </summary>
    private static void RecoverTrailingContinuationRows(
        List<List<string>> rows, BoundingBox bbox, int columnCount, List<SegmentData> pageSegments)
    {
        if (columnCount == 0 || pageSegments.Count == 0) return;

        float pageHeight = 0f;
        foreach (var s in pageSegments) pageHeight = Math.Max(pageHeight, s.Y + s.Height);
        pageHeight = Math.Max(pageHeight, 792.0f);

        float xLo = (float)(bbox.X0 - XTolerancePts);
        float xHi = (float)(bbox.X1 + XTolerancePts);
        float searchFloor = (float)bbox.Y0;

        for (int iteration = 0; iteration < TrailingRecoveryMaxRows; iteration++)
        {
            var bandSegments = new List<SegmentData>();
            foreach (var seg in pageSegments)
            {
                if (seg.Text.Trim().Length == 0) continue;
                if (!(seg.Y + seg.Height <= searchFloor + (float)YGapTolerancePts)) continue;
                if (!(seg.X + seg.Width >= xLo)) continue;
                if (!(seg.X <= xHi)) continue;
                bandSegments.Add(seg);
            }
            var bandWords = PdfTableReconstruct.SegmentsToWords(bandSegments, pageHeight);
            if (bandWords.Count == 0) break;

            var (entityWords, entityBottomImageY) = TakeNextEntityBand(bandWords);
            if (entityWords is null) break;

            uint colGap = PdfLayoutTables.ComputeAdaptiveColumnGap(entityWords, Math.Max(xHi - xLo, 1.0f));
            var grid = PdfTableReconstruct.ReconstructTable(entityWords, colGap, 0.5);
            if (grid.Count == 0 || grid[0].Count != columnCount) break;

            var mergedRow = MergeRowsColumnwise(grid, columnCount);
            if (mergedRow.All(cell => cell.Trim().Length == 0)) break;

            float entityBottomPdfY = pageHeight - entityBottomImageY;
            rows.Add(mergedRow);
            bbox.Y0 = Math.Min(bbox.Y0, entityBottomPdfY);
            searchFloor = entityBottomPdfY;
        }
    }

    /// <summary>
    /// Take the topmost row-gap-bounded contiguous band of words, stopping at the first gap
    /// wider than <c>median_height * 1.8</c>. Returns the band and the image-coordinate bottom
    /// edge of the last line included.
    /// </summary>
    private static (List<HocrWord>? Band, uint BottomImageY) TakeNextEntityBand(List<HocrWord> words)
    {
        if (words.Count == 0) return (null, 0);

        var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
        uint medianHeight = Math.Max(heights[heights.Count / 2], 1u);
        uint rowGapSplit = (uint)(medianHeight * TrailingRowGapMultiplier);
        uint rowTolerance = Math.Max(medianHeight / 2, 3u);

        // `sort_by_key(|w| w.top)` upstream is a stable sort, as OrderBy is here.
        var sorted = words.OrderBy(w => w.Top).ToList();

        var band = new List<HocrWord>();
        uint bandBottom = 0;
        uint? lastRowYc = null;
        int idx = 0;
        while (idx < sorted.Count)
        {
            uint rowYc = sorted[idx].Top + sorted[idx].Height / 2;
            int end = idx + 1;
            while (end < sorted.Count)
            {
                uint yc = sorted[end].Top + sorted[end].Height / 2;
                uint diff = yc > rowYc ? yc - rowYc : rowYc - yc;
                if (diff <= rowTolerance) end++;
                else break;
            }

            if (lastRowYc is { } prevYc && rowYc > prevYc && rowYc - prevYc > rowGapSplit && band.Count > 0)
                break;

            for (int i = idx; i < end; i++)
            {
                bandBottom = Math.Max(bandBottom, sorted[i].Top + sorted[i].Height);
                band.Add(sorted[i]);
            }
            lastRowYc = rowYc;
            idx = end;
        }

        return band.Count == 0 ? (null, 0) : (band, bandBottom);
    }
}
