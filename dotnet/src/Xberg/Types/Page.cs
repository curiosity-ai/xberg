using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>Unit of pagination. Serialized as a bare snake_case string.</summary>
public enum PageUnitType
{
    Page,
    Slide,
    Sheet,
}

/// <summary>Per-page content. `tables`/`image_indices` are omitted when empty (see PORT_NOTES).</summary>
public sealed class PageContent
{
    public uint PageNumber { get; set; }
    public string Content { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Table> Tables { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<uint> ImageIndices { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Hierarchy { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsBlank { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? LayoutRegions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeakerNotes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SectionName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SheetName { get; set; }
}

/// <summary>Document-level page structure summary.</summary>
public sealed class PageStructure
{
    public uint TotalCount { get; set; }
    public PageUnitType UnitType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Boundaries { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Pages { get; set; }
}
