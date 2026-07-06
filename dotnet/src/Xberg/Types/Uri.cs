using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>Kind of an extracted URI. Serialized as a bare snake_case string.</summary>
public enum UriKind
{
    Hyperlink,
    Image,
    Anchor,
    Citation,
    Reference,
    Email,
}

/// <summary>A URI/link discovered during extraction.</summary>
public sealed class ExtractedUri : IEquatable<ExtractedUri>
{
    public string Url { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Page { get; set; }

    public UriKind Kind { get; set; }

    public bool Equals(ExtractedUri? other) =>
        other is not null && Url == other.Url && Label == other.Label && Page == other.Page && Kind == other.Kind;

    public override bool Equals(object? obj) => Equals(obj as ExtractedUri);

    public override int GetHashCode() => HashCode.Combine(Url, Label, Page, Kind);
}
