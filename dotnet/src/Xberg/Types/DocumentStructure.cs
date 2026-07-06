using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>Content layer classification. Serialized as a bare snake_case string. Default = Body.</summary>
public enum ContentLayer
{
    Body,
    Header,
    Footer,
    Footnote,
}

/// <summary>Semantic kind of a relationship. Serialized as a bare snake_case string.</summary>
public enum RelationshipKind
{
    FootnoteReference,
    CitationReference,
    InternalLink,
    Caption,
    Label,
    TocEntry,
    CrossReference,
}

/// <summary>
/// Inline text annotation over a byte range. The <see cref="Kind"/> is an internally-tagged
/// enum (discriminator field "annotation_type"); the custom converter flattens the tag and
/// its payload as siblings of start/end.
/// </summary>
[JsonConverter(typeof(TextAnnotationConverter))]
public sealed class TextAnnotation
{
    public uint Start { get; set; }
    public uint End { get; set; }
    public AnnotationKind Kind { get; set; } = new AnnotationKind();
}

/// <summary>
/// Inline annotation kind. Internally-tagged on "annotation_type". Unit variants carry no data;
/// Link/Color/FontSize/Custom carry fields.
/// </summary>
public sealed class AnnotationKind
{
    public enum Tag
    {
        Bold,
        Italic,
        Underline,
        Strikethrough,
        Code,
        Subscript,
        Superscript,
        Link,
        Highlight,
        Color,
        FontSize,
        Custom,
    }

    public Tag Which { get; set; } = Tag.Bold;

    // Link
    public string? Url { get; set; }
    public string? Title { get; set; }

    // Color / FontSize
    public string? Value { get; set; }

    // Custom
    public string? Name { get; set; }

    public static AnnotationKind Bold => new() { Which = Tag.Bold };
    public static AnnotationKind Italic => new() { Which = Tag.Italic };

    public static string TagString(Tag t) => t switch
    {
        Tag.Bold => "bold",
        Tag.Italic => "italic",
        Tag.Underline => "underline",
        Tag.Strikethrough => "strikethrough",
        Tag.Code => "code",
        Tag.Subscript => "subscript",
        Tag.Superscript => "superscript",
        Tag.Link => "link",
        Tag.Highlight => "highlight",
        Tag.Color => "color",
        Tag.FontSize => "font_size",
        Tag.Custom => "custom",
        _ => "bold",
    };

    public static Tag ParseTag(string s) => s switch
    {
        "bold" => Tag.Bold,
        "italic" => Tag.Italic,
        "underline" => Tag.Underline,
        "strikethrough" => Tag.Strikethrough,
        "code" => Tag.Code,
        "subscript" => Tag.Subscript,
        "superscript" => Tag.Superscript,
        "link" => Tag.Link,
        "highlight" => Tag.Highlight,
        "color" => Tag.Color,
        "font_size" => Tag.FontSize,
        "custom" => Tag.Custom,
        _ => throw new JsonException($"Unknown annotation_type: {s}"),
    };
}

/// <summary>Merges TextAnnotation start/end with the internally-tagged AnnotationKind.</summary>
public sealed class TextAnnotationConverter : JsonConverter<TextAnnotation>
{
    public override TextAnnotation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var ann = new TextAnnotation
        {
            Start = root.GetProperty("start").GetUInt32(),
            End = root.GetProperty("end").GetUInt32(),
        };
        var kind = new AnnotationKind();
        if (root.TryGetProperty("annotation_type", out var at))
        {
            kind.Which = AnnotationKind.ParseTag(at.GetString()!);
            if (root.TryGetProperty("url", out var u)) kind.Url = u.GetString();
            if (root.TryGetProperty("title", out var t)) kind.Title = t.GetString();
            if (root.TryGetProperty("value", out var v)) kind.Value = v.GetString();
            if (root.TryGetProperty("name", out var n)) kind.Name = n.GetString();
        }
        ann.Kind = kind;
        return ann;
    }

    public override void Write(Utf8JsonWriter writer, TextAnnotation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("start", value.Start);
        writer.WriteNumber("end", value.End);
        var k = value.Kind;
        writer.WriteString("annotation_type", AnnotationKind.TagString(k.Which));
        switch (k.Which)
        {
            case AnnotationKind.Tag.Link:
                writer.WriteString("url", k.Url ?? "");
                if (k.Title is not null) writer.WriteString("title", k.Title);
                break;
            case AnnotationKind.Tag.Color:
            case AnnotationKind.Tag.FontSize:
                writer.WriteString("value", k.Value ?? "");
                break;
            case AnnotationKind.Tag.Custom:
                writer.WriteString("name", k.Name ?? "");
                if (k.Value is not null) writer.WriteString("value", k.Value);
                break;
        }
        writer.WriteEndObject();
    }
}

// ============================================================================
// Document Structure tree
// ============================================================================

public sealed class DocumentStructure
{
    public List<DocumentNode> Nodes { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFormat { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<DocumentRelationship> Relationships { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> NodeTypes { get; set; } = new();

    /// <summary>Sorted, de-duplicated snake_case node_type names. Mirrors Rust `finalize_node_types`.</summary>
    public void FinalizeNodeTypes()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in Nodes)
            set.Add(n.Content.NodeTypeString());
        NodeTypes = set.ToList();
    }
}

public sealed class DocumentRelationship
{
    public uint Source { get; set; }
    public uint Target { get; set; }
    public RelationshipKind Kind { get; set; }
}

public sealed class DocumentNode
{
    [JsonIgnore] public string Id { get; set; } = "";

    public NodeContent Content { get; set; } = NodeContent.Paragraph("");

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Parent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<uint> Children { get; set; } = new();

    public ContentLayer ContentLayer { get; set; } = ContentLayer.Body;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Page { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PageEnd { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundingBox? Bbox { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<TextAnnotation> Annotations { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Attributes { get; set; }
}

public sealed class TableGrid
{
    public uint Rows { get; set; }
    public uint Cols { get; set; }
    public List<GridCell> Cells { get; set; } = new();
}

public sealed class GridCell
{
    public string Content { get; set; } = "";
    public uint Row { get; set; }
    public uint Col { get; set; }
    public uint RowSpan { get; set; } = 1;
    public uint ColSpan { get; set; } = 1;
    public bool IsHeader { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundingBox? Bbox { get; set; }
}

/// <summary>
/// Content of a document node. Internally-tagged on "node_type"; a custom converter
/// dispatches on the tag and writes payload fields as siblings.
/// </summary>
[JsonConverter(typeof(NodeContentConverter))]
public sealed class NodeContent
{
    public enum Tag
    {
        Title, Heading, Paragraph, List, ListItem, Table, Image, Code, Quote,
        Formula, Footnote, Group, PageBreak, Slide, DefinitionList, DefinitionItem,
        Citation, Admonition, RawBlock, MetadataBlock,
    }

    public Tag Which { get; set; }

    public string? Text { get; set; }
    public byte Level { get; set; }
    public bool Ordered { get; set; }
    public TableGrid? Grid { get; set; }
    public string? Description { get; set; }
    public uint? ImageIndex { get; set; }
    public string? Src { get; set; }
    public string? Language { get; set; }
    public string? Label { get; set; }
    public byte? HeadingLevel { get; set; }
    public string? HeadingText { get; set; }
    public uint Number { get; set; }
    public string? SlideTitle { get; set; }
    public string? Term { get; set; }
    public string? Definition { get; set; }
    public string? Key { get; set; }
    public string? Kind { get; set; }
    public string? Format { get; set; }
    public string? RawContent { get; set; }
    public List<string[]>? Entries { get; set; }

    public static NodeContent Title(string text) => new() { Which = Tag.Title, Text = text };
    public static NodeContent Heading(byte level, string text) => new() { Which = Tag.Heading, Level = level, Text = text };
    public static NodeContent Paragraph(string text) => new() { Which = Tag.Paragraph, Text = text };
    public static NodeContent List(bool ordered) => new() { Which = Tag.List, Ordered = ordered };
    public static NodeContent ListItem(string text) => new() { Which = Tag.ListItem, Text = text };
    public static NodeContent Table(TableGrid grid) => new() { Which = Tag.Table, Grid = grid };
    public static NodeContent Code(string text, string? language) => new() { Which = Tag.Code, Text = text, Language = language };
    public static NodeContent Quote() => new() { Which = Tag.Quote };
    public static NodeContent Formula(string text) => new() { Which = Tag.Formula, Text = text };
    public static NodeContent Footnote(string text) => new() { Which = Tag.Footnote, Text = text };
    public static NodeContent PageBreak() => new() { Which = Tag.PageBreak };
    public static NodeContent DefinitionList() => new() { Which = Tag.DefinitionList };
    public static NodeContent DefinitionItem(string term, string definition) => new() { Which = Tag.DefinitionItem, Term = term, Definition = definition };
    public static NodeContent Citation(string key, string text) => new() { Which = Tag.Citation, Key = key, Text = text };

    public string NodeTypeString() => Which switch
    {
        Tag.Title => "title",
        Tag.Heading => "heading",
        Tag.Paragraph => "paragraph",
        Tag.List => "list",
        Tag.ListItem => "list_item",
        Tag.Table => "table",
        Tag.Image => "image",
        Tag.Code => "code",
        Tag.Quote => "quote",
        Tag.Formula => "formula",
        Tag.Footnote => "footnote",
        Tag.Group => "group",
        Tag.PageBreak => "page_break",
        Tag.Slide => "slide",
        Tag.DefinitionList => "definition_list",
        Tag.DefinitionItem => "definition_item",
        Tag.Citation => "citation",
        Tag.Admonition => "admonition",
        Tag.RawBlock => "raw_block",
        Tag.MetadataBlock => "metadata_block",
        _ => "paragraph",
    };
}

public sealed class NodeContentConverter : JsonConverter<NodeContent>
{
    public override NodeContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        string nt = root.GetProperty("node_type").GetString()!;
        var nc = new NodeContent();
        string? Str(string k) => root.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
        nc.Which = nt switch
        {
            "title" => NodeContent.Tag.Title,
            "heading" => NodeContent.Tag.Heading,
            "paragraph" => NodeContent.Tag.Paragraph,
            "list" => NodeContent.Tag.List,
            "list_item" => NodeContent.Tag.ListItem,
            "table" => NodeContent.Tag.Table,
            "image" => NodeContent.Tag.Image,
            "code" => NodeContent.Tag.Code,
            "quote" => NodeContent.Tag.Quote,
            "formula" => NodeContent.Tag.Formula,
            "footnote" => NodeContent.Tag.Footnote,
            "group" => NodeContent.Tag.Group,
            "page_break" => NodeContent.Tag.PageBreak,
            "slide" => NodeContent.Tag.Slide,
            "definition_list" => NodeContent.Tag.DefinitionList,
            "definition_item" => NodeContent.Tag.DefinitionItem,
            "citation" => NodeContent.Tag.Citation,
            "admonition" => NodeContent.Tag.Admonition,
            "raw_block" => NodeContent.Tag.RawBlock,
            "metadata_block" => NodeContent.Tag.MetadataBlock,
            _ => throw new JsonException($"Unknown node_type: {nt}"),
        };
        nc.Text = Str("text");
        if (root.TryGetProperty("level", out var lv)) nc.Level = lv.GetByte();
        if (root.TryGetProperty("ordered", out var od)) nc.Ordered = od.GetBoolean();
        if (root.TryGetProperty("grid", out var g)) nc.Grid = g.Deserialize<TableGrid>(options);
        nc.Description = Str("description");
        if (root.TryGetProperty("image_index", out var ii) && ii.ValueKind != JsonValueKind.Null) nc.ImageIndex = ii.GetUInt32();
        nc.Src = Str("src");
        nc.Language = Str("language");
        nc.Label = Str("label");
        if (root.TryGetProperty("heading_level", out var hl) && hl.ValueKind != JsonValueKind.Null) nc.HeadingLevel = hl.GetByte();
        nc.HeadingText = Str("heading_text");
        if (root.TryGetProperty("number", out var num)) nc.Number = num.GetUInt32();
        if (nc.Which == NodeContent.Tag.Slide) nc.SlideTitle = Str("title");
        nc.Term = Str("term");
        nc.Definition = Str("definition");
        nc.Key = Str("key");
        nc.Kind = Str("kind");
        nc.Format = Str("format");
        nc.RawContent = Str("content");
        return nc;
    }

    public override void Write(Utf8JsonWriter writer, NodeContent v, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("node_type", v.NodeTypeString());
        switch (v.Which)
        {
            case NodeContent.Tag.Title:
            case NodeContent.Tag.Paragraph:
            case NodeContent.Tag.ListItem:
            case NodeContent.Tag.Formula:
            case NodeContent.Tag.Footnote:
                writer.WriteString("text", v.Text ?? "");
                break;
            case NodeContent.Tag.Heading:
                writer.WriteNumber("level", v.Level);
                writer.WriteString("text", v.Text ?? "");
                break;
            case NodeContent.Tag.List:
                writer.WriteBoolean("ordered", v.Ordered);
                break;
            case NodeContent.Tag.Table:
                writer.WritePropertyName("grid");
                JsonSerializer.Serialize(writer, v.Grid ?? new TableGrid(), options);
                break;
            case NodeContent.Tag.Image:
                if (v.Description is not null) writer.WriteString("description", v.Description);
                if (v.ImageIndex is not null) writer.WriteNumber("image_index", v.ImageIndex.Value);
                if (v.Src is not null) writer.WriteString("src", v.Src);
                break;
            case NodeContent.Tag.Code:
                writer.WriteString("text", v.Text ?? "");
                if (v.Language is not null) writer.WriteString("language", v.Language);
                break;
            case NodeContent.Tag.Group:
                if (v.Label is not null) writer.WriteString("label", v.Label);
                if (v.HeadingLevel is not null) writer.WriteNumber("heading_level", v.HeadingLevel.Value);
                if (v.HeadingText is not null) writer.WriteString("heading_text", v.HeadingText);
                break;
            case NodeContent.Tag.Slide:
                writer.WriteNumber("number", v.Number);
                if (v.SlideTitle is not null) writer.WriteString("title", v.SlideTitle);
                break;
            case NodeContent.Tag.DefinitionItem:
                writer.WriteString("term", v.Term ?? "");
                writer.WriteString("definition", v.Definition ?? "");
                break;
            case NodeContent.Tag.Citation:
                writer.WriteString("key", v.Key ?? "");
                writer.WriteString("text", v.Text ?? "");
                break;
            case NodeContent.Tag.Admonition:
                writer.WriteString("kind", v.Kind ?? "note");
                if (v.SlideTitle is not null) writer.WriteString("title", v.SlideTitle);
                break;
            case NodeContent.Tag.RawBlock:
                writer.WriteString("format", v.Format ?? "");
                writer.WriteString("content", v.RawContent ?? "");
                break;
            case NodeContent.Tag.MetadataBlock:
                writer.WritePropertyName("entries");
                writer.WriteStartArray();
                foreach (var e in v.Entries ?? new())
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(e.Length > 0 ? e[0] : "");
                    writer.WriteStringValue(e.Length > 1 ? e[1] : "");
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            // Quote, PageBreak, DefinitionList: tag only.
        }
        writer.WriteEndObject();
    }
}
