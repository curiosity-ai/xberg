using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>
/// Side-by-side region splitting (`pdf::oxide::table`): a wide region is cut in two at a
/// central whitespace gutter, both halves have to stand up as tables in their own right, and
/// a pair of wrapped financial tables is canonicalised so each half reads independently.
/// </summary>
internal static partial class PdfTableReconstruct
{
    /// <summary>How far off the region's horizontal midpoint a gutter may sit and still count
    /// as the seam between two side-by-side tables, in percent of the region width.</summary>
    private const ulong SideBySideCenterTolerancePercent = 8;

    /// <summary>A seam has to be at least this much of a median word height wide, so an
    /// ordinary inter-column gap cannot pass for the gutter between two tables.</summary>
    private const ulong SideBySideMinGutterHeightPercent = 100;

    /// <summary>Each half has to span this share of the region width; a lopsided cut is a
    /// column boundary inside one table, not two tables printed next to each other.</summary>
    private const ulong SideBySideMinWidthPercent = 35;

    private const int SideBySideMinWordsPerSide = 6;

    /// <summary>Share of a table's data rows that must populate a column before it counts as
    /// one of that table's tracks.</summary>
    private const int SideBySideMinTrackRowPercent = 20;

    private const int SideBySideMinNumericTracks = 2;

    /// <summary>Two halves of one table cut down the middle differ in shape; genuine
    /// side-by-side tables repeat the same grid, so their track counts stay within this.</summary>
    private const int SideBySideMaxTrackDelta = 1;

    private const int WrappedFinancialColumns = 4;
    private const int WrappedFinancialDescriptorColumns = 2;
    private const int WrappedFinancialParColumn = 2;
    private const int WrappedFinancialValueColumn = 3;

    /// <summary>Header a wrapped financial side table is rewritten to, replacing the data row
    /// that reconstruction mistook for a header.</summary>
    private static readonly string[] CanonicalFinancialHeader = { "Description", "Amount", "Value" };

    private static readonly string[] FinancialIssuerSuffixes = { "Ltd.", "DAC", "LLC", "Inc.", "PLC", "Corp." };

    private const int WrappedFinancialMaxContinuationRows = 6;
    private const int WrappedFinancialMinContinuationRows = 2;

    /// <summary>The column and numeric-track counts that identify a candidate side table.</summary>
    private readonly record struct SideTableShape(int Columns, int NumericTracks);

    private static partial (List<HocrWord> Left, List<HocrWord> Right)? SplitSideBySideRegion(List<HocrWord> region)
    {
        if (region.Count < SideBySideMinWordsPerSide * 2) return null;

        var bounds = SideBySideRegionHorizontalBounds(region);
        if (bounds is null) return null;
        (uint regionLeft, uint regionRight) = bounds.Value;
        uint regionWidth = SaturatingSub(regionRight, regionLeft);
        if (regionWidth == 0) return null;

        ulong? seam = SideBySideCentralSeam(region, regionLeft, regionWidth);
        if (seam is null) return null;

        var partition = SideBySidePartitionAtSeam(region, seam.Value);
        if (partition is null) return null;
        (var left, var right) = partition.Value;
        if (left.Count < SideBySideMinWordsPerSide || right.Count < SideBySideMinWordsPerSide) return null;

        return SideBySideBalancedSides(left, right, regionLeft, regionRight, regionWidth)
            ? (left, right)
            : null;
    }

    private static (uint Left, uint Right)? SideBySideRegionHorizontalBounds(List<HocrWord> region)
    {
        if (region.Count == 0) return null;
        uint left = uint.MaxValue;
        uint right = 0;
        foreach (var word in region)
        {
            if (word.Left < left) left = word.Left;
            uint wordRight = SideBySideSaturatingAdd(word.Left, word.Width);
            if (wordRight > right) right = wordRight;
        }
        return (left, right);
    }

    /// <summary>The widest whitespace gutter that straddles the region's midpoint, as an
    /// x coordinate, or <c>null</c> when no gap there is wide enough to be a gutter.</summary>
    private static ulong? SideBySideCentralSeam(List<HocrWord> region, uint regionLeft, uint regionWidth)
    {
        ulong regionCenter = regionLeft + (ulong)regionWidth / 2;
        ulong tolerance = (ulong)regionWidth * SideBySideCenterTolerancePercent / 100;
        ulong minGutter = (ulong)SideBySideMedianWordHeight(region) * SideBySideMinGutterHeightPercent / 100;

        var intervals = SideBySideMergedHorizontalIntervals(region);
        ulong bestGap = 0;
        ulong? bestCenter = null;
        for (int i = 0; i + 1 < intervals.Count; i++)
        {
            ulong gap = SaturatingSub(intervals[i + 1].Start, intervals[i].End);
            ulong center = ((ulong)intervals[i].End + intervals[i + 1].Start) / 2;
            if (gap < minGutter) continue;
            ulong offCenter = center > regionCenter ? center - regionCenter : regionCenter - center;
            if (offCenter > tolerance) continue;
            // Equally wide gutters resolve to the rightmost candidate.
            if (bestCenter is null || gap >= bestGap)
            {
                bestGap = gap;
                bestCenter = center;
            }
        }
        return bestCenter;
    }

    /// <summary>The region's ink projected onto the x axis, as disjoint spans in order.</summary>
    private static List<(uint Start, uint End)> SideBySideMergedHorizontalIntervals(List<HocrWord> region)
    {
        var intervals = region
            .Select(word => (Start: word.Left, End: SideBySideSaturatingAdd(word.Left, word.Width)))
            .OrderBy(interval => interval.Start)
            .ToList();

        var merged = new List<(uint Start, uint End)>();
        foreach (var interval in intervals)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            }
            else
            {
                merged.Add(interval);
            }
        }
        return merged;
    }

    private static uint SideBySideMedianWordHeight(List<HocrWord> region)
    {
        if (region.Count == 0) return 1;
        var heights = region.Select(word => word.Height).OrderBy(height => height).ToList();
        return Math.Max(heights[heights.Count / 2], 1u);
    }

    /// <summary>Split the region's words at the seam, or <c>null</c> when a word straddles it —
    /// a word crossing the gutter means the gutter is not a table boundary.</summary>
    private static (List<HocrWord> Left, List<HocrWord> Right)? SideBySidePartitionAtSeam(
        List<HocrWord> region, ulong seam)
    {
        var left = new List<HocrWord>();
        var right = new List<HocrWord>();
        foreach (var word in region)
        {
            if (SideBySideSaturatingAdd(word.Left, word.Width) <= seam) left.Add(word);
            else if (word.Left >= seam) right.Add(word);
            else return null;
        }
        return (left, right);
    }

    private static bool SideBySideBalancedSides(
        List<HocrWord> left, List<HocrWord> right, uint regionLeft, uint regionRight, uint regionWidth)
    {
        if (left.Count == 0 || right.Count == 0) return false;
        uint leftRight = left.Max(word => SideBySideSaturatingAdd(word.Left, word.Width));
        uint rightLeft = right.Min(word => word.Left);
        ulong leftWidth = SaturatingSub(leftRight, regionLeft);
        ulong rightWidth = SaturatingSub(regionRight, rightLeft);
        ulong minimum = (ulong)regionWidth * SideBySideMinWidthPercent / 100;
        return leftWidth >= minimum && rightWidth >= minimum;
    }

    private static uint SideBySideSaturatingAdd(uint a, uint b) => a > uint.MaxValue - b ? uint.MaxValue : a + b;

    private static partial bool SideTablesHaveIndependentShape(Table left, Table right)
    {
        var leftShape = SideTableShapeOf(left);
        if (leftShape is null) return false;
        var rightShape = SideTableShapeOf(right);
        if (rightShape is null) return false;
        return Math.Abs(leftShape.Value.Columns - rightShape.Value.Columns) <= SideBySideMaxTrackDelta
            && Math.Abs(leftShape.Value.NumericTracks - rightShape.Value.NumericTracks) <= SideBySideMaxTrackDelta;
    }

    /// <summary>A table's shape, or <c>null</c> when it is ragged or carries no label column
    /// alongside its numbers — either way it is not one half of a side-by-side pair.</summary>
    private static SideTableShape? SideTableShapeOf(Table table)
    {
        if (table.Cells.Count == 0) return null;
        int columns = table.Cells[0].Count;
        var rows = table.Cells.Skip(1).ToList();
        if (rows.Count == 0 || rows.Any(row => row.Count != columns)) return null;

        int minSupport = SideBySideMinTrackSupport(rows.Count);
        int descriptorTracks = 0;
        int numericTracks = 0;
        for (int column = 0; column < columns; column++)
        {
            if (rows.Count(row => SideTableIsDescriptorCell(row[column])) >= minSupport) descriptorTracks++;
            if (rows.Count(row => IsNumericWord(row[column])) >= minSupport) numericTracks++;
        }
        return descriptorTracks >= 1 && numericTracks >= SideBySideMinNumericTracks
            ? new SideTableShape(columns, numericTracks)
            : null;
    }

    private static int SideBySideMinTrackSupport(int rowCount)
    {
        int scaled = rowCount * SideBySideMinTrackRowPercent;
        return Math.Max((scaled + 99) / 100, 2);
    }

    private static bool SideTableIsDescriptorCell(string text) =>
        !IsNumericWord(text) && text.Count(char.IsLetter) >= 3;

    private static partial (Table Left, Table Right) NormalizeSideBySideFinancialTables(Table left, Table right)
    {
        bool pairedWrappedTables = IsWrappedFinancialSideTable(left) && IsWrappedFinancialSideTable(right);
        bool hasStrictPseudoHeader = HasEmptyNumericPseudoHeader(left) || HasEmptyNumericPseudoHeader(right);
        bool hasOverflowPseudoHeader =
            HasConstrainedOverflowPseudoHeader(left) || HasConstrainedOverflowPseudoHeader(right);
        if (pairedWrappedTables && hasStrictPseudoHeader && hasOverflowPseudoHeader)
        {
            NormalizeWrappedFinancialSideTable(left);
            NormalizeWrappedFinancialSideTable(right);
        }
        return (left, right);
    }

    /// <summary>A four-column table whose first row is a data row reconstruction promoted to a
    /// header, whose descriptors wrap across rows, and whose values sit on the trailing pair.</summary>
    private static bool IsWrappedFinancialSideTable(Table table)
    {
        if (table.Cells.Count == 0) return false;
        var header = table.Cells[0];
        if (IsExplicitFinancialHeader(header) || !HasWrappedPseudoHeaderEvidence(header)) return false;

        var rows = table.Cells.Skip(1).ToList();
        if (header.Count != WrappedFinancialColumns
            || rows.Count == 0
            || rows.Any(row => row.Count != WrappedFinancialColumns))
        {
            return false;
        }

        int minSupport = SideBySideMinTrackSupport(rows.Count);
        bool leadingDescriptors = true;
        for (int column = 0; column < WrappedFinancialDescriptorColumns; column++)
        {
            if (rows.Count(row => SideTableIsDescriptorCell(row[column])) < minSupport) leadingDescriptors = false;
        }
        bool trailingNumeric = true;
        for (int column = WrappedFinancialDescriptorColumns; column < WrappedFinancialColumns; column++)
        {
            if (rows.Count(row => IsNumericWord(row[column])) < minSupport) trailingNumeric = false;
        }

        return leadingDescriptors && trailingNumeric && HasBoundedFinancialContinuation(rows);
    }

    private static bool HasEmptyNumericPseudoHeader(Table table) =>
        table.Cells.Count > 0 && IsEmptyNumericPseudoHeader(table.Cells[0]);

    private static bool HasConstrainedOverflowPseudoHeader(Table table) =>
        table.Cells.Count > 0 && IsConstrainedOverflowPseudoHeader(table.Cells[0]);

    private static bool HasWrappedPseudoHeaderEvidence(List<string> row) =>
        IsEmptyNumericPseudoHeader(row) || IsConstrainedOverflowPseudoHeader(row);

    private static bool HasPopulatedPseudoHeaderDescriptors(List<string> row)
    {
        if (row.Count != WrappedFinancialColumns) return false;
        for (int column = 0; column < WrappedFinancialDescriptorColumns; column++)
        {
            if (row[column].Trim().Length == 0) return false;
        }
        return true;
    }

    /// <summary>The first row carries descriptors but no values, so it is the head of a wrapped
    /// entry rather than a column header.</summary>
    private static bool IsEmptyNumericPseudoHeader(List<string> row)
    {
        if (!HasPopulatedPseudoHeaderDescriptors(row)) return false;
        for (int column = WrappedFinancialDescriptorColumns; column < row.Count; column++)
        {
            if (row[column].Trim().Length != 0) return false;
        }
        return true;
    }

    /// <summary>The first row's value columns hold a rate that overflowed out of the descriptor
    /// (a percentage with letters in it), which no column header would carry.</summary>
    private static bool IsConstrainedOverflowPseudoHeader(List<string> row)
    {
        if (!HasPopulatedPseudoHeaderDescriptors(row)) return false;
        string overflow = row[WrappedFinancialParColumn].Trim();
        return row[WrappedFinancialValueColumn].Trim().Length == 0
            && overflow.Length != 0
            && overflow.Contains('%')
            && overflow.Any(char.IsLetter);
    }

    private static bool HasBoundedFinancialContinuation(List<List<string>> rows)
    {
        for (int start = 0; start < rows.Count; start++)
        {
            if (FinancialContinuationEnd(rows, start) is not null) return true;
        }
        return false;
    }

    /// <summary>Rewrite a wrapped financial table into the canonical three-column form: an
    /// explicit header, the pseudo-header restored as data, and each wrapped entry collapsed
    /// onto the row that carries its values.</summary>
    private static void NormalizeWrappedFinancialSideTable(Table table)
    {
        if (table.Cells.Count == 0) return;
        var rows = new List<List<string>>(table.Cells.Count + 1)
        {
            CanonicalFinancialHeader.ToList(),
            CanonicalizeWrappedFinancialRow(table.Cells[0]),
        };

        int index = 1;
        while (index < table.Cells.Count)
        {
            int? end = FinancialContinuationEnd(table.Cells, index);
            if (end is null)
            {
                rows.Add(CanonicalizeWrappedFinancialRow(table.Cells[index]));
                index += 1;
                continue;
            }
            var span = table.Cells.GetRange(index, end.Value - index + 1);
            var collapsed = CollapseFinancialRows(span);
            if (collapsed is null)
            {
                foreach (var row in span) rows.Add(CanonicalizeWrappedFinancialRow(row));
            }
            else
            {
                rows.Add(CanonicalizeWrappedFinancialRow(collapsed));
            }
            index = end.Value + 1;
        }

        PreserveRepeatedIssuerRows(rows);
        table.Cells = rows;
        table.Markdown = TableToMarkdown(table.Cells);
    }

    /// <summary>
    /// Splits an unpunctuated legal-entity prefix only when the next row repeats `Series`.
    ///
    /// The missing comma is reconstruction evidence: punctuated securities such as
    /// `RR 28 Ltd., Series 2024-A` intentionally remain a single description.
    /// </summary>
    private static void PreserveRepeatedIssuerRows(List<List<string>> rows)
    {
        int index = 1;
        while (index + 1 < rows.Count)
        {
            string descriptor = rows[index][0];
            int marker = descriptor.IndexOf(" Series ", StringComparison.Ordinal);
            if (marker < 0)
            {
                index += 1;
                continue;
            }
            string issuer = descriptor[..marker];
            string firstSeries = descriptor[(marker + " Series ".Length)..];

            bool nextIsSeries = rows[index + 1][0].TrimStart().StartsWith("Series ", StringComparison.Ordinal);
            bool issuerHasLegalSuffix =
                FinancialIssuerSuffixes.Any(suffix => issuer.EndsWith(suffix, StringComparison.Ordinal));
            if (!nextIsSeries || !issuerHasLegalSuffix)
            {
                index += 1;
                continue;
            }

            rows[index][0] = "Series " + firstSeries;
            rows.Insert(index, new List<string> { issuer, "", "" });
            index += 2;
        }
    }

    private static List<string> CanonicalizeWrappedFinancialRow(List<string> row)
    {
        string descriptor = string.Join(
            " ",
            row.Take(WrappedFinancialDescriptorColumns)
                .Select(cell => cell.Trim())
                .Where(cell => cell.Length != 0));
        return new List<string>
        {
            descriptor,
            row.Count > WrappedFinancialParColumn ? row[WrappedFinancialParColumn] : "",
            row.Count > WrappedFinancialValueColumn ? row[WrappedFinancialValueColumn] : "",
        };
    }

    private static bool FinancialRowHasNumericValues(List<string> row)
    {
        if (row.Count < WrappedFinancialDescriptorColumns) return false;
        for (int column = WrappedFinancialDescriptorColumns; column < row.Count; column++)
        {
            if (IsNumericWord(row[column])) return true;
        }
        return false;
    }

    private static bool FinancialRowHasWrappedDescriptors(List<string> row)
    {
        if (row.Count < WrappedFinancialDescriptorColumns) return false;
        for (int column = 0; column < WrappedFinancialDescriptorColumns; column++)
        {
            if (row[column].Trim().Length == 0) return false;
        }
        return true;
    }

    /// <summary>A row that carries descriptor text but no values is the continuation of the
    /// entry above it.</summary>
    private static bool FinancialRowIsContinuation(List<string> row)
    {
        if (!FinancialRowHasWrappedDescriptors(row)) return false;
        for (int column = WrappedFinancialDescriptorColumns; column < row.Count; column++)
        {
            if (row[column].Trim().Length != 0) return false;
        }
        return true;
    }

    private static bool IsExplicitFinancialHeader(List<string> row)
    {
        if (row.Count != WrappedFinancialColumns) return false;
        return row.All(cell => cell.Trim().Length != 0) && row.All(SideTableIsDescriptorCell);
    }

    /// <summary>Index of the row that terminates the wrapped entry starting at
    /// <paramref name="start"/> — the first row with values, provided the run of descriptor-only
    /// rows before it is neither too short to be a wrap nor long enough to be its own section.</summary>
    private static int? FinancialContinuationEnd(List<List<string>> rows, int start)
    {
        int continuationRows = 0;
        for (int index = start; index < rows.Count; index++)
        {
            var row = rows[index];
            if (FinancialRowIsContinuation(row))
            {
                continuationRows += 1;
                if (continuationRows > WrappedFinancialMaxContinuationRows) return null;
                continue;
            }
            bool bounded = continuationRows >= WrappedFinancialMinContinuationRows
                && continuationRows <= WrappedFinancialMaxContinuationRows;
            return bounded && FinancialRowHasNumericValues(row) ? index : null;
        }
        return null;
    }

    /// <summary>Join a wrapped entry's descriptor fragments and take the values from its
    /// terminal row, or <c>null</c> when that row is too short to carry them.</summary>
    private static List<string>? CollapseFinancialRows(List<List<string>> rows)
    {
        if (rows.Count == 0) return null;
        var terminal = rows[^1];
        if (terminal.Count <= WrappedFinancialValueColumn) return null;

        string descriptor = string.Join(
            " ",
            rows.SelectMany(row => row.Take(WrappedFinancialDescriptorColumns))
                .Select(cell => cell.Trim())
                .Where(cell => cell.Length != 0));
        return new List<string>
        {
            descriptor,
            "",
            terminal[WrappedFinancialParColumn],
            terminal[WrappedFinancialValueColumn],
        };
    }
}
