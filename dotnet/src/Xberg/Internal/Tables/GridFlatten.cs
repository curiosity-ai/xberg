// Ported from crates/xberg/src/extraction/grid_flatten.rs.

namespace Xberg.Internal.Tables;

/// <summary>
/// Span-aware placement of table cells onto a grid.
/// <para>
/// Extractors receive tables as a stream of cells carrying <c>rowspan</c> and <c>colspan</c>.
/// Placing them by a naive per-row column index shifts every cell under a rowspan leftwards into
/// the spanning column, so the data stops lining up with its headers. Reserving the columns a
/// rowspan still covers is what keeps merged-cell tables aligned.
/// </para>
/// <para>
/// This is the single home of that placement rule, so the geometry cannot drift between the
/// formats that need it.
/// </para>
/// </summary>
internal static class GridFlatten
{
    /// <summary>
    /// Resolve each cell's position on an occupancy grid, visiting rows in order and cells in
    /// document order within a row. Calls <paramref name="place"/> once per cell with its row
    /// index and resolved column, and returns the total column count.
    /// </summary>
    public static int ResolveSpanGrid<TCell>(
        IReadOnlyList<IReadOnlyList<TCell>> rows,
        Func<TCell, int> colSpan,
        Func<TCell, int> rowSpan,
        Action<int, int, TCell> place)
    {
        // occupiedUntil[c] is the first row index at which column c is free again.
        var occupiedUntil = new List<int>();

        for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            int col = 0;
            foreach (var cell in rows[rowIdx])
            {
                while (col < occupiedUntil.Count && occupiedUntil[col] > rowIdx) col++;

                int endRow = rowIdx + Math.Max(1, rowSpan(cell));
                int span = Math.Max(1, colSpan(cell));
                for (int c = col; c < col + span; c++)
                {
                    while (c >= occupiedUntil.Count) occupiedUntil.Add(0);
                    occupiedUntil[c] = endRow;
                }

                place(rowIdx, col, cell);
                col += span;
            }
        }

        return occupiedUntil.Count;
    }

    /// <summary>
    /// Flatten spanned rows into a dense, rectangular grid. The origin cell of a span holds the
    /// value; the columns and rows it covers are left empty.
    /// </summary>
    public static List<List<string>> FlattenSpannedRows<TCell>(
        IReadOnlyList<IReadOnlyList<TCell>> rows,
        Func<TCell, int> colSpan,
        Func<TCell, int> rowSpan,
        Func<TCell, string> content)
    {
        var placed = new List<(int Row, int Col, string Content)>();
        int numCols = ResolveSpanGrid(rows, colSpan, rowSpan,
            (rowIdx, col, cell) => placed.Add((rowIdx, col, content(cell))));

        var grid = new List<List<string>>(rows.Count);
        for (int r = 0; r < rows.Count; r++)
        {
            var line = new List<string>(numCols);
            for (int c = 0; c < numCols; c++) line.Add("");
            grid.Add(line);
        }

        foreach (var (r, c, text) in placed)
            if (r < grid.Count && c < numCols) grid[r][c] = text;

        return grid;
    }
}
