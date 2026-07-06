// Ported from crates/xberg/src/extractors/rtf/tables.rs
// (cells_to_text / cells_to_markdown ported from crates/xberg/src/extraction/markdown.rs)
// State machine for tracking table construction during RTF parsing.

using System.Text;
using Xberg.Types;

namespace Xberg.Internal.Rtf;

internal sealed class TableState
{
    public List<List<string>> Rows { get; } = new();
    public List<string> CurrentRow { get; } = new();
    public StringBuilder CurrentCell { get; } = new();
    public bool InRow { get; set; }

    /// <summary>Set after `\row`; another `\trowd` may follow for the same table.</summary>
    public bool ExpectingNextRow { get; set; }

    public bool CurrentCellIsEmpty => CurrentCell.Length == 0;
    public bool CurrentCellEndsWith(char c) => CurrentCell.Length > 0 && CurrentCell[^1] == c;

    public void PushCell()
    {
        string cell = CurrentCell.ToString().Trim();
        CurrentRow.Add(cell);
        CurrentCell.Clear();
    }

    public void PushRow()
    {
        if (CurrentCell.Length != 0)
            PushCell();
        InRow = false;
        ExpectingNextRow = true;
        if (CurrentRow.Count > 0)
        {
            Rows.Add(new List<string>(CurrentRow));
            CurrentRow.Clear();
        }
    }

    public void StartRow()
    {
        if (InRow)
            PushRow();
        InRow = true;
        ExpectingNextRow = false;
        CurrentCell.Clear();
        CurrentRow.Clear();
    }

    /// <summary>
    /// Finalize the table. When <paramref name="plain"/> is true the markdown field uses
    /// tab-separated text; otherwise markdown pipes. Returns null when there are no rows.
    /// </summary>
    public Table? FinalizeWithFormat(bool plain)
    {
        if (InRow || CurrentCell.Length != 0 || CurrentRow.Count > 0)
            PushRow();

        if (Rows.Count == 0)
            return null;

        string markdown = plain ? CellsToText(Rows) : CellsToMarkdown(Rows);
        return new Table
        {
            Cells = Rows,
            Markdown = markdown,
            PageNumber = 1,
            BoundingBox = null,
        };
    }

    // --- cells rendering (ported from extraction/markdown.rs) ---

    public static string CellsToText(IReadOnlyList<List<string>> cells)
    {
        if (cells.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var row in cells)
        {
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(row[i]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static string CellsToMarkdown(IReadOnlyList<List<string>> cells)
    {
        if (cells.Count == 0) return "";
        int numCols = cells[0].Count;
        if (numCols == 0) return "";

        var md = new StringBuilder();
        // Header row.
        var header = cells[0];
        md.Append('|');
        foreach (var cell in header)
        {
            md.Append(' ');
            md.Append(cell.Replace("|", "\\|"));
            md.Append(" |");
        }
        md.Append('\n');
        md.Append('|');
        for (int i = 0; i < numCols; i++)
            md.Append("------|");
        md.Append('\n');

        for (int r = 1; r < cells.Count; r++)
        {
            var row = cells[r];
            md.Append('|');
            for (int idx = 0; idx < row.Count; idx++)
            {
                if (idx >= numCols) break;
                md.Append(' ');
                md.Append(row[idx].Replace("|", "\\|"));
                md.Append(" |");
            }
            md.Append('\n');
        }
        return md.ToString();
    }
}
