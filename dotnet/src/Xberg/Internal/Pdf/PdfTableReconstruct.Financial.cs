// Ported from crates/xberg/src/pdf/oxide/table.rs — the label-heavy financial
// statement path: regions whose rows are a long descriptor followed by numbers
// parked on a handful of fixed tracks. Generic column detection shreds those,
// because the descriptor's own inter-word gaps are as wide as the gap that
// separates it from the first value, so the values are re-bucketed against the
// tracks directly and consecutive sections of the same statement are stitched
// back into the one table they print as.
using Xberg.Types;

namespace Xberg.Internal.Pdf;

internal static partial class PdfTableReconstruct
{
    /// <summary>A statement section shorter than this is indistinguishable from a stray
    /// paragraph that happens to carry a couple of figures.</summary>
    private const int LabelHeavyFinancialMinRows = 5;

    /// <summary>The value tracks a section must have, exactly: these statements print a
    /// descriptor plus a fixed set of columns, and a different count means the region is
    /// some other shape that generic reconstruction should handle.</summary>
    private const int LabelHeavyFinancialTracks = 4;

    /// <summary>Values are sparse — a track only has to carry a figure on this share of the
    /// section's rows, since sub-total and heading rows leave most cells blank.</summary>
    private const int LabelHeavyFinancialMinTrackRowPercent = 30;

    /// <summary>Floor under the percentage, so short sections still need real repetition
    /// rather than two coincidentally aligned figures.</summary>
    private const int LabelHeavyFinancialMinTrackRows = 3;

    /// <summary>What makes the shape "label heavy": most rows must open with text left of
    /// the value block, not just carry numbers.</summary>
    private const int LabelHeavyFinancialMinDescriptorRowPercent = 50;

    /// <summary>Rows that fill most of the tracks; without a few of them the region is prose
    /// with occasional figures rather than a table.</summary>
    private const int LabelHeavyFinancialMinNumericRows = 4;

    /// <summary>How many tracks a row must hit to count as one of those value rows.</summary>
    private const int LabelHeavyFinancialMinValuesPerRow = 3;

    /// <summary>Vertical gap, in median word heights, still readable as a break between
    /// sections of one statement rather than the end of it.</summary>
    private const uint LabelHeavyFinancialMaxSectionGapHeights = 4;

    /// <summary>A section label runs as one phrase, so its words sit within this many word
    /// heights of each other; a wider gap means the row is really columns.</summary>
    private const uint LabelHeavyFinancialMaxLabelGapHeights = 2;

    private static partial (Table Table, List<uint> Tracks, uint Tolerance)? ReconstructLabelHeavyFinancialRegion(
        List<HocrWord> region, float pageHeight, uint pageNumber)
    {
        var found = LabelHeavyFinancialTracksOf(region);
        if (found is null) return null;
        var (tracks, rowTolerance, xTolerance) = found.Value;

        var rows = NumericRows(region, rowTolerance);
        var grid = BuildLabelHeavyFinancialGrid(rows, tracks, xTolerance);
        if (!IsLabelHeavyFinancialGrid(grid)) return null;

        var table = new Table
        {
            Cells = grid,
            Markdown = TableToMarkdown(grid),
            PageNumber = pageNumber,
            BoundingBox = RegionBoundingBox(region, pageHeight),
        };
        return (table, tracks, xTolerance);
    }

    /// <summary>Bucket each row into a descriptor cell plus one cell per value track.</summary>
    private static List<List<string>> BuildLabelHeavyFinancialGrid(
        List<List<HocrWord>> rows, List<uint> tracks, uint xTolerance)
    {
        uint numericStart = SaturatingSub(tracks[0], xTolerance * 2);
        var grid = new List<List<string>>(rows.Count);
        foreach (var unsorted in rows)
        {
            var row = unsorted.OrderBy(word => word.Left).ToList();
            // A row of pure text is a heading or a wrapped descriptor: keep it whole in the
            // first cell instead of letting a long label spill into the value columns.
            bool descriptorOnly = !row.Any(word => IsNumericWord(word.Text))
                && row.Any(word => word.Left + word.Width / 2 < numericStart);

            var cells = new List<string>[LabelHeavyFinancialTracks + 1];
            for (int index = 0; index < cells.Length; index++) cells[index] = new List<string>();

            foreach (var word in row)
            {
                uint center = word.Left + word.Width / 2;
                int column = descriptorOnly || center < numericStart ? 0 : NearestFinancialTrack(tracks, center) + 1;
                cells[column].Add(word.Text);
            }

            grid.Add(cells
                .Select((words, column) => NormalizeFinancialCell(column, string.Join(" ", words)))
                .ToList());
        }
        return grid;
    }

    /// <summary>Index of the track closest to <paramref name="center"/>, first one on a tie.</summary>
    private static int NearestFinancialTrack(List<uint> tracks, uint center)
    {
        int best = 0;
        uint bestDistance = AbsDiff(tracks[0], center);
        for (int index = 1; index < tracks.Count; index++)
        {
            uint distance = AbsDiff(tracks[index], center);
            if (distance < bestDistance) { best = index; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>A value cell that came through as a bare "?" is a dash glyph the font's
    /// encoding could not map; in a value column that stands for a nil amount.</summary>
    private static string NormalizeFinancialCell(int column, string text) =>
        column > 0 && text == "?" ? "—" : text;

    /// <summary>The region's value tracks, plus the row and horizontal tolerances they were
    /// found within, or <c>null</c> when the region is not a financial section.</summary>
    private static (List<uint> Tracks, uint RowTolerance, uint XTolerance)? LabelHeavyFinancialTracksOf(
        List<HocrWord> region)
    {
        if (region.Count == 0) return null;
        uint medianHeight = Math.Max(MedianRegionWordHeight(region), 1u);
        uint rowTolerance = Math.Max(medianHeight / 2, 3u);
        uint xTolerance = Math.Max(medianHeight * 2, 12u);
        var rows = NumericRows(region, rowTolerance);
        if (rows.Count < LabelHeavyFinancialMinRows) return null;

        var candidates = SupportedFinancialTrackCenters(rows, xTolerance);
        if (candidates.Count != LabelHeavyFinancialTracks) return null;
        if (!HasLabelHeavyFinancialEvidence(rows, candidates, xTolerance)
            || !FinancialTrackSpacingIsStable(candidates))
            return null;

        return (candidates, rowTolerance, xTolerance);
    }

    /// <summary>Numeric word centres that repeat down enough rows to be a column, collapsed
    /// so two centres within tolerance of each other count as one track.</summary>
    private static List<uint> SupportedFinancialTrackCenters(List<List<HocrWord>> rows, uint xTolerance)
    {
        int minimumSupport = Math.Max(
            (rows.Count * LabelHeavyFinancialMinTrackRowPercent + 99) / 100,
            LabelHeavyFinancialMinTrackRows);

        var supported = rows
            .SelectMany(row => row)
            .Where(word => IsNumericWord(word.Text))
            .Select(word => word.Left + word.Width / 2)
            .Where(candidate => FinancialTrackSupport(rows, candidate, xTolerance) >= minimumSupport)
            .OrderBy(center => center)
            .ToList();

        var tracks = new List<uint>();
        foreach (uint center in supported)
            if (tracks.Count == 0 || AbsDiff(center, tracks[^1]) > xTolerance) tracks.Add(center);
        return tracks;
    }

    private static int FinancialTrackSupport(List<List<HocrWord>> rows, uint candidate, uint xTolerance) =>
        rows.Count(row => RowHasFinancialTrack(row, candidate, xTolerance));

    private static bool RowHasFinancialTrack(List<HocrWord> row, uint track, uint xTolerance) =>
        row.Any(word => IsNumericWord(word.Text) && AbsDiff(word.Left + word.Width / 2, track) <= xTolerance);

    /// <summary>Whether the region reads as a statement: mostly rows that start with a label
    /// left of the value block, and enough rows that fill the value block.</summary>
    private static bool HasLabelHeavyFinancialEvidence(List<List<HocrWord>> rows, List<uint> tracks, uint xTolerance)
    {
        uint numericStart = SaturatingSub(tracks[0], xTolerance * 2);
        int descriptorRows = rows.Count(row =>
            row.Any(word => word.Left + word.Width / 2 < numericStart && word.Text.Any(char.IsLetter)));
        int valueRows = rows.Count(row =>
            tracks.Count(track => RowHasFinancialTrack(row, track, xTolerance)) >= LabelHeavyFinancialMinValuesPerRow);
        return descriptorRows * 100 >= rows.Count * LabelHeavyFinancialMinDescriptorRowPercent
            && valueRows >= LabelHeavyFinancialMinNumericRows;
    }

    /// <summary>Printed value columns are evenly spaced; wildly uneven gaps mean the centres
    /// came from unrelated figures rather than one set of columns.</summary>
    private static bool FinancialTrackSpacingIsStable(List<uint> tracks)
    {
        if (tracks.Count < 2) return false;
        uint minimumGap = uint.MaxValue, maximumGap = 0;
        for (int index = 1; index < tracks.Count; index++)
        {
            uint gap = tracks[index] - tracks[index - 1];
            minimumGap = Math.Min(minimumGap, gap);
            maximumGap = Math.Max(maximumGap, gap);
        }
        return minimumGap > 0 && maximumGap <= minimumGap * 2;
    }

    /// <summary>Re-check the bucketed grid: the track evidence was measured on words, and
    /// bucketing can still leave a grid too sparse to be a statement.</summary>
    private static bool IsLabelHeavyFinancialGrid(List<List<string>> grid)
    {
        if (grid.Count < LabelHeavyFinancialMinRows
            || grid.Any(row => row.Count != LabelHeavyFinancialTracks + 1))
            return false;
        int numericRows = grid.Count(row =>
            row.Skip(1).Count(IsNumericWord) >= LabelHeavyFinancialMinValuesPerRow);
        return numericRows >= LabelHeavyFinancialMinNumericRows;
    }

    private static partial bool LabelHeavyFinancialSectionsAreContiguous(
        List<HocrWord> previous, List<HocrWord> next, List<uint> nextTracks, uint nextTolerance)
    {
        if (previous.Count == 0 || next.Count == 0) return false;
        uint previousBottom = previous.Max(word => word.Top + word.Height);
        uint nextTop = next.Min(word => word.Top);
        uint medianHeight = Math.Max(
            Math.Max(MedianRegionWordHeight(previous), MedianRegionWordHeight(next)), 1u);
        uint normalizedGapLimit = medianHeight * LabelHeavyFinancialMaxSectionGapHeights;
        return SaturatingSub(nextTop, previousBottom) <= normalizedGapLimit
            && StartsWithFinancialSectionLabel(next, nextTracks, nextTolerance);
    }

    /// <summary>Whether a region opens with a section heading — one run of words, all text,
    /// starting left of the value block. That heading is what makes the region a continuation
    /// of the statement above rather than an unrelated table that happens to line up.</summary>
    private static bool StartsWithFinancialSectionLabel(List<HocrWord> region, List<uint> tracks, uint trackTolerance)
    {
        uint rowTolerance = Math.Max(MedianRegionWordHeight(region) / 2, 3u);
        var rows = NumericRows(region, rowTolerance);
        if (rows.Count == 0) return false;
        var firstRow = rows[0];

        uint numericStart = SaturatingSub(tracks[0], trackTolerance * 2);
        if (firstRow.Any(word => IsNumericWord(word.Text))) return false;

        var labelWords = firstRow
            .Where(word => word.Text.Any(char.IsLetter))
            .OrderBy(word => word.Left)
            .ToList();
        if (labelWords.Count == 0) return false;
        if (labelWords[0].Left + labelWords[0].Width / 2 >= numericStart) return false;

        uint maximumLabelGap = MedianRegionWordHeight(region) * LabelHeavyFinancialMaxLabelGapHeights;
        for (int index = 1; index < labelWords.Count; index++)
        {
            var left = labelWords[index - 1];
            if (SaturatingSub(labelWords[index].Left, left.Left + left.Width) > maximumLabelGap) return false;
        }
        return true;
    }

    private static uint MedianRegionWordHeight(List<HocrWord> region)
    {
        if (region.Count == 0) return 0;
        var heights = region.Select(word => word.Height).OrderBy(height => height).ToList();
        return heights[heights.Count / 2];
    }

    private static partial Table StitchLabelHeavyFinancialSections(List<Table> sections, uint pageNumber)
    {
        var rows = new List<List<string>>();
        BoundingBox? boundingBox = null;
        foreach (var section in sections)
        {
            rows.AddRange(section.Cells);
            if (section.BoundingBox is not { } sectionBox) continue;
            boundingBox = boundingBox is { } combined
                ? new BoundingBox
                {
                    X0 = Math.Min(combined.X0, sectionBox.X0),
                    Y0 = Math.Min(combined.Y0, sectionBox.Y0),
                    X1 = Math.Max(combined.X1, sectionBox.X1),
                    Y1 = Math.Max(combined.Y1, sectionBox.Y1),
                }
                : sectionBox;
        }
        return new Table
        {
            Cells = rows,
            Markdown = TableToMarkdown(rows),
            PageNumber = pageNumber,
            BoundingBox = boundingBox,
        };
    }
}
