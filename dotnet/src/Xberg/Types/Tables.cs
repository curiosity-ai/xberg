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

    /// <summary>Header cells for this fragment, i.e. the first row of <see cref="Cells"/>.
    /// <c>null</c> when no header row could be determined.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Columns { get; set; }

    /// <summary>Stable identifier shared by every <c>tables[]</c> entry representing a fragment
    /// of the same physical table. Assigned deterministically in document order; <c>null</c>
    /// when the extractor did not assign one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TableId { get; set; }
}

/// <summary>Future/unused per-cell table extension point (mirrors Rust `TableCell`).</summary>
public sealed class TableCell
{
    public string Content { get; set; } = "";
    public uint RowSpan { get; set; } = 1;
    public uint ColSpan { get; set; } = 1;
    public bool IsHeader { get; set; }
}
