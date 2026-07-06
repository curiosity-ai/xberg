using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>A structured table. `cells` is always serialized (even when empty).</summary>
public sealed class Table
{
    public List<List<string>> Cells { get; set; } = new();
    public string Markdown { get; set; } = "";
    public uint PageNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundingBox? BoundingBox { get; set; }
}

/// <summary>Future/unused per-cell table extension point (mirrors Rust `TableCell`).</summary>
public sealed class TableCell
{
    public string Content { get; set; } = "";
    public uint RowSpan { get; set; } = 1;
    public uint ColSpan { get; set; } = 1;
    public bool IsHeader { get; set; }
}
