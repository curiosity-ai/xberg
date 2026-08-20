using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>
/// Conservative normalization for native PDF table grids.
/// </summary>
internal static class PdfTableNormalize
{
    private const int MinRepeatedDataRows = 8;
    private const int MinWideTableColumns = 16;

    /// <summary>
    /// Split one column boundary that the detector collapsed, and report whether anything changed.
    /// </summary>
    /// <remarks>
    /// A wide financial table can come back with two adjacent numeric columns merged into one
    /// cell — the same header twice, and every data cell holding the value twice separated by a
    /// run of spaces. The repair is deliberately narrow: it fires only when exactly one interior
    /// column shows that shape on every data row, with numeric neighbours on both sides.
    /// </remarks>
    public static bool RepairConsistentlyMergedNumericColumn(Table table)
    {
        if (table.Cells.Count == 0) return false;
        var header = table.Cells[0];
        int columnCount = header.Count;
        var dataRows = table.Cells.GetRange(1, table.Cells.Count - 1);

        if (dataRows.Count < MinRepeatedDataRows
            || columnCount < MinWideTableColumns
            || dataRows.Any(row => row.Count != columnCount)
            || (table.Columns is not null && table.Columns.Count != columnCount))
            return false;

        var candidates = new List<int>();
        for (int column = 1; column < columnCount - 1; column++)
        {
            string candidateHeader = header[column].Trim();
            if (candidateHeader.Length == 0 || candidateHeader != header[column + 1].Trim()) continue;
            bool everyRow = dataRows.All(row =>
                SplitMergedNumericCell(row[column]) is { } halves
                && halves.Left == halves.Right
                && IsNumericAtom(row[column - 1])
                && IsNumericAtom(row[column + 1]));
            if (everyRow) candidates.Add(column);
        }
        if (candidates.Count != 1) return false;
        int merged = candidates[0];

        table.Cells[0].Insert(merged, "");
        for (int r = 1; r < table.Cells.Count; r++)
        {
            var row = table.Cells[r];
            if (SplitMergedNumericCell(row[merged]) is not { } halves) return false;
            row[merged] = halves.Left;
            row.Insert(merged + 1, halves.Right);
        }
        if (table.Columns is not null && table.Columns.Count == columnCount)
            table.Columns.Insert(merged, "");
        table.Markdown = PdfTableReconstruct.TableToMarkdown(table.Cells);
        return true;
    }

    /// <summary>
    /// The two numeric halves of a cell split by a run of two or more spaces, or <c>null</c> when
    /// the cell does not have that shape.
    /// </summary>
    private static (string Left, string Right)? SplitMergedNumericCell(string cell)
    {
        int index = 0;
        while (index < cell.Length)
        {
            if (!char.IsWhiteSpace(cell[index])) { index++; continue; }
            int start = index;
            while (index < cell.Length && char.IsWhiteSpace(cell[index])) index++;
            if (index - start < 2) continue;
            string left = cell[..start].Trim();
            string right = cell[index..].Trim();
            if (IsNumericAtom(left) && IsNumericAtom(right)) return (left, right);
        }
        return null;
    }

    /// <summary>A single number, allowing the accountancy conventions around it.</summary>
    private static bool IsNumericAtom(string cell)
    {
        string value = cell.Trim();
        if (value.Length >= 2 && value.StartsWith('(') && value.EndsWith(')')) value = value[1..^1];
        if (value.Length > 0 && value[0] is '$' or '€' or '£' or '¥') value = value[1..];
        if (value.EndsWith('%')) value = value[..^1];
        if (value.Length == 0) return false;
        return double.TryParse(
            value.Replace(",", ""),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);
    }
}
