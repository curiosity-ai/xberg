using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>Hierarchical level of an OCR element. Matches Rust `OcrElementLevel`.</summary>
public enum OcrElementLevel
{
    Word,
    Line,
    Block,
    Page,
}

/// <summary>Tag for each <see cref="ElementKind"/> variant.</summary>
public enum ElementKindTag
{
    Title,
    Heading,
    Paragraph,
    ListItem,
    Code,
    Formula,
    FootnoteDefinition,
    CommentDefinition,
    CommentRef,
    FootnoteRef,
    Citation,
    Slide,
    DefinitionTerm,
    DefinitionDescription,
    Admonition,
    RawBlock,
    MetadataBlock,
    ListStart,
    ListEnd,
    QuoteStart,
    QuoteEnd,
    GroupStart,
    GroupEnd,
    Table,
    Image,
    PageBreak,
    OcrText,
}

/// <summary>
/// Semantic role of an internal element — a discriminated union modeled as a value type
/// (readonly struct + <see cref="ElementKindTag"/> tag + payload fields). This mirrors Rust's
/// `#[derive(Copy)]` <c>ElementKind</c> enum, keeps pattern-matching in the renderers cheap
/// (<c>switch (kind.Tag)</c>), and avoids per-element heap allocations.
///
/// JSON shape matches serde's default externally-tagged enum representation
/// (unit variants -> "Title"; struct variants -> {"Heading":{"level":2}}).
/// </summary>
[JsonConverter(typeof(ElementKindConverter))]
public readonly struct ElementKind : IEquatable<ElementKind>
{
    public ElementKindTag Tag { get; }
    public byte Level { get; }          // Heading
    public bool Ordered { get; }        // ListItem, ListStart
    public uint Number { get; }         // Slide
    public uint TableIndex { get; }     // Table
    public uint ImageIndex { get; }     // Image
    public OcrElementLevel OcrLevel { get; } // OcrText

    private ElementKind(ElementKindTag tag, byte level = 0, bool ordered = false, uint number = 0,
        uint tableIndex = 0, uint imageIndex = 0, OcrElementLevel ocrLevel = OcrElementLevel.Word)
    {
        Tag = tag;
        Level = level;
        Ordered = ordered;
        Number = number;
        TableIndex = tableIndex;
        ImageIndex = imageIndex;
        OcrLevel = ocrLevel;
    }

    public static readonly ElementKind Title = new(ElementKindTag.Title);
    public static ElementKind Heading(byte level) => new(ElementKindTag.Heading, level: level);
    public static readonly ElementKind Paragraph = new(ElementKindTag.Paragraph);
    public static ElementKind ListItem(bool ordered) => new(ElementKindTag.ListItem, ordered: ordered);
    public static readonly ElementKind Code = new(ElementKindTag.Code);
    public static readonly ElementKind Formula = new(ElementKindTag.Formula);
    public static readonly ElementKind FootnoteDefinition = new(ElementKindTag.FootnoteDefinition);
    /// <summary>A reviewer's comment body. Distinct from a footnote definition: the two have the
    /// same shape but a reader needs to tell an authored note from someone else's remark.</summary>
    public static readonly ElementKind CommentDefinition = new(ElementKindTag.CommentDefinition);
    /// <summary>The point in the text a reviewer's comment is anchored to.</summary>
    public static readonly ElementKind CommentRef = new(ElementKindTag.CommentRef);
    public static readonly ElementKind FootnoteRef = new(ElementKindTag.FootnoteRef);
    public static readonly ElementKind Citation = new(ElementKindTag.Citation);
    public static ElementKind Slide(uint number) => new(ElementKindTag.Slide, number: number);
    public static readonly ElementKind DefinitionTerm = new(ElementKindTag.DefinitionTerm);
    public static readonly ElementKind DefinitionDescription = new(ElementKindTag.DefinitionDescription);
    public static readonly ElementKind Admonition = new(ElementKindTag.Admonition);
    public static readonly ElementKind RawBlock = new(ElementKindTag.RawBlock);
    public static readonly ElementKind MetadataBlock = new(ElementKindTag.MetadataBlock);
    public static ElementKind ListStart(bool ordered) => new(ElementKindTag.ListStart, ordered: ordered);
    public static readonly ElementKind ListEnd = new(ElementKindTag.ListEnd);
    public static readonly ElementKind QuoteStart = new(ElementKindTag.QuoteStart);
    public static readonly ElementKind QuoteEnd = new(ElementKindTag.QuoteEnd);
    public static readonly ElementKind GroupStart = new(ElementKindTag.GroupStart);
    public static readonly ElementKind GroupEnd = new(ElementKindTag.GroupEnd);
    public static ElementKind Table(uint tableIndex) => new(ElementKindTag.Table, tableIndex: tableIndex);
    public static ElementKind Image(uint imageIndex) => new(ElementKindTag.Image, imageIndex: imageIndex);
    public static readonly ElementKind PageBreak = new(ElementKindTag.PageBreak);
    public static ElementKind OcrText(OcrElementLevel level) => new(ElementKindTag.OcrText, ocrLevel: level);

    /// <summary>Stable string discriminant used for element-ID generation. Matches Rust `discriminant()`.</summary>
    public string Discriminant() => Tag switch
    {
        ElementKindTag.Title => "title",
        ElementKindTag.Heading => "heading",
        ElementKindTag.Paragraph => "paragraph",
        ElementKindTag.ListItem => "list_item",
        ElementKindTag.Code => "code",
        ElementKindTag.Formula => "formula",
        ElementKindTag.FootnoteDefinition => "footnote_definition",
        ElementKindTag.CommentDefinition => "comment_definition",
        ElementKindTag.CommentRef => "comment_ref",
        ElementKindTag.FootnoteRef => "footnote_ref",
        ElementKindTag.Citation => "citation",
        ElementKindTag.Slide => "slide",
        ElementKindTag.DefinitionTerm => "definition_term",
        ElementKindTag.DefinitionDescription => "definition_description",
        ElementKindTag.Admonition => "admonition",
        ElementKindTag.RawBlock => "raw_block",
        ElementKindTag.MetadataBlock => "metadata_block",
        ElementKindTag.ListStart => "list_start",
        ElementKindTag.ListEnd => "list_end",
        ElementKindTag.QuoteStart => "quote_start",
        ElementKindTag.QuoteEnd => "quote_end",
        ElementKindTag.GroupStart => "group_start",
        ElementKindTag.GroupEnd => "group_end",
        ElementKindTag.Table => "table",
        ElementKindTag.Image => "image",
        ElementKindTag.PageBreak => "page_break",
        ElementKindTag.OcrText => "ocr_text",
        _ => throw new InvalidOperationException(),
    };

    public bool IsContainerStart =>
        Tag is ElementKindTag.ListStart or ElementKindTag.QuoteStart or ElementKindTag.GroupStart;

    public bool IsContainerEnd =>
        Tag is ElementKindTag.ListEnd or ElementKindTag.QuoteEnd or ElementKindTag.GroupEnd;

    public bool Equals(ElementKind other) =>
        Tag == other.Tag && Level == other.Level && Ordered == other.Ordered && Number == other.Number
        && TableIndex == other.TableIndex && ImageIndex == other.ImageIndex && OcrLevel == other.OcrLevel;

    public override bool Equals(object? obj) => obj is ElementKind k && Equals(k);

    public override int GetHashCode() =>
        HashCode.Combine(Tag, Level, Ordered, Number, TableIndex, ImageIndex, OcrLevel);

    public static bool operator ==(ElementKind a, ElementKind b) => a.Equals(b);
    public static bool operator !=(ElementKind a, ElementKind b) => !a.Equals(b);
}

/// <summary>
/// Serializes <see cref="ElementKind"/> in serde's default externally-tagged form:
/// unit variants as a bare string ("Title"), struct variants as {"Heading":{"level":2}}.
/// </summary>
public sealed class ElementKindConverter : JsonConverter<ElementKind>
{
    public override ElementKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string name = reader.GetString()!;
            return name switch
            {
                "Title" => ElementKind.Title,
                "Paragraph" => ElementKind.Paragraph,
                "Code" => ElementKind.Code,
                "Formula" => ElementKind.Formula,
                "FootnoteDefinition" => ElementKind.FootnoteDefinition,
                "CommentDefinition" => ElementKind.CommentDefinition,
                "CommentRef" => ElementKind.CommentRef,
                "FootnoteRef" => ElementKind.FootnoteRef,
                "Citation" => ElementKind.Citation,
                "DefinitionTerm" => ElementKind.DefinitionTerm,
                "DefinitionDescription" => ElementKind.DefinitionDescription,
                "Admonition" => ElementKind.Admonition,
                "RawBlock" => ElementKind.RawBlock,
                "MetadataBlock" => ElementKind.MetadataBlock,
                "ListEnd" => ElementKind.ListEnd,
                "QuoteStart" => ElementKind.QuoteStart,
                "QuoteEnd" => ElementKind.QuoteEnd,
                "GroupStart" => ElementKind.GroupStart,
                "GroupEnd" => ElementKind.GroupEnd,
                "PageBreak" => ElementKind.PageBreak,
                _ => throw new JsonException($"Unknown ElementKind variant: {name}"),
            };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected string or object for ElementKind");

        reader.Read();
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException("Expected variant name");
        string variant = reader.GetString()!;
        using var doc = JsonDocument.ParseValue(ref reader);
        JsonElement payload = doc.RootElement;

        ElementKind result = variant switch
        {
            "Heading" => ElementKind.Heading(payload.GetProperty("level").GetByte()),
            "ListItem" => ElementKind.ListItem(payload.GetProperty("ordered").GetBoolean()),
            "ListStart" => ElementKind.ListStart(payload.GetProperty("ordered").GetBoolean()),
            "Slide" => ElementKind.Slide(payload.GetProperty("number").GetUInt32()),
            "Table" => ElementKind.Table(payload.GetProperty("table_index").GetUInt32()),
            "Image" => ElementKind.Image(payload.GetProperty("image_index").GetUInt32()),
            "OcrText" => ElementKind.OcrText(Enum.Parse<OcrElementLevel>(payload.GetProperty("level").GetString()!)),
            _ => throw new JsonException($"Unknown ElementKind struct variant: {variant}"),
        };

        reader.Read(); // consume EndObject
        return result;
    }

    public override void Write(Utf8JsonWriter writer, ElementKind value, JsonSerializerOptions options)
    {
        switch (value.Tag)
        {
            case ElementKindTag.Heading:
                writer.WriteStartObject();
                writer.WritePropertyName("Heading");
                writer.WriteStartObject();
                writer.WriteNumber("level", value.Level);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case ElementKindTag.ListItem:
                WriteOrdered(writer, "ListItem", value.Ordered);
                break;
            case ElementKindTag.ListStart:
                WriteOrdered(writer, "ListStart", value.Ordered);
                break;
            case ElementKindTag.Slide:
                writer.WriteStartObject();
                writer.WritePropertyName("Slide");
                writer.WriteStartObject();
                writer.WriteNumber("number", value.Number);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case ElementKindTag.Table:
                writer.WriteStartObject();
                writer.WritePropertyName("Table");
                writer.WriteStartObject();
                writer.WriteNumber("table_index", value.TableIndex);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case ElementKindTag.Image:
                writer.WriteStartObject();
                writer.WritePropertyName("Image");
                writer.WriteStartObject();
                writer.WriteNumber("image_index", value.ImageIndex);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case ElementKindTag.OcrText:
                writer.WriteStartObject();
                writer.WritePropertyName("OcrText");
                writer.WriteStartObject();
                writer.WriteString("level", value.OcrLevel.ToString());
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.Tag.ToString());
                break;
        }
    }

    private static void WriteOrdered(Utf8JsonWriter writer, string variant, bool ordered)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(variant);
        writer.WriteStartObject();
        writer.WriteBoolean("ordered", ordered);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
