using System.Text.Json;
using System.Text.Json.Serialization;
using Xberg.Types;

namespace Xberg.Core;

/// <summary>
/// Output format. Known variants serialize as lowercase strings ("plain", "markdown", "djot",
/// "html", "json", "structured"); <see cref="Custom"/> serializes as its bare renderer name.
/// Default = Plain. Mirrors Rust `OutputFormat` (FromStr / Display).
/// </summary>
[JsonConverter(typeof(OutputFormatConverter))]
public readonly struct OutputFormat : IEquatable<OutputFormat>
{
    public enum Kind { Plain, Markdown, Djot, Html, Json, Structured, DocTags, Custom }

    public Kind Which { get; }
    public string? CustomName { get; }

    private OutputFormat(Kind which, string? customName = null)
    {
        Which = which;
        CustomName = customName;
    }

    public static readonly OutputFormat Plain = new(Kind.Plain);
    public static readonly OutputFormat Markdown = new(Kind.Markdown);
    public static readonly OutputFormat Djot = new(Kind.Djot);
    public static readonly OutputFormat Html = new(Kind.Html);
    public static readonly OutputFormat Json = new(Kind.Json);
    public static readonly OutputFormat Structured = new(Kind.Structured);
    public static readonly OutputFormat DocTags = new(Kind.DocTags);
    public static OutputFormat Custom(string name) => new(Kind.Custom, name);

    /// <summary>Parse from a string (never fails; unknown → Custom of the lowercased string).</summary>
    public static OutputFormat FromString(string s)
    {
        string lower = s.ToLowerInvariant();
        return lower switch
        {
            "plain" or "text" => Plain,
            "markdown" or "md" => Markdown,
            "djot" => Djot,
            "html" => Html,
            "json" => Json,
            "structured" or "structured-ocr" => Structured,
            "doctags" => DocTags,
            _ => Custom(lower),
        };
    }

    public override string ToString() => Which switch
    {
        Kind.Plain => "plain",
        Kind.Markdown => "markdown",
        Kind.Djot => "djot",
        Kind.Html => "html",
        Kind.Json => "json",
        Kind.Structured => "structured",
        Kind.DocTags => "doctags",
        Kind.Custom => CustomName ?? "",
        _ => "plain",
    };

    public bool Equals(OutputFormat other) => Which == other.Which && CustomName == other.CustomName;
    public override bool Equals(object? obj) => obj is OutputFormat o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Which, CustomName);
    public static bool operator ==(OutputFormat a, OutputFormat b) => a.Equals(b);
    public static bool operator !=(OutputFormat a, OutputFormat b) => !a.Equals(b);
}

public sealed class OutputFormatConverter : JsonConverter<OutputFormat>
{
    public override OutputFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        OutputFormat.FromString(reader.GetString() ?? "plain");

    public override void Write(Utf8JsonWriter writer, OutputFormat value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Trimmed, native-only extraction configuration.</summary>
public sealed class ExtractionConfig
{
    public ResultFormat ResultFormat { get; set; } = ResultFormat.Unified;
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Plain;
    public bool IncludeDocumentStructure { get; set; }

    // Content-relevant option stubs (defaults; extractors read these later).
    public bool ExtractImages { get; set; } = true;
    public bool ExtractTables { get; set; } = true;

    /// <summary>
    /// Decode the QR codes inside every extracted image, writing them to
    /// <c>ExtractedImage.QrCodes</c> and appending their payloads to the document text.
    /// </summary>
    /// <remarks>
    /// Opt-in, as upstream has it: decoding runs the detector over every image in the document,
    /// which is real work to do on a caller's behalf without being asked.
    /// </remarks>
    [JsonPropertyName("qr_codes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QrCodes { get; set; }

    /// <summary>
    /// Limits applied to hostile input. <c>null</c> takes <see cref="SecurityLimits"/>' defaults,
    /// which is what upstream's <c>Option&lt;SecurityLimits&gt;</c> does with <c>None</c>.
    /// </summary>
    [JsonPropertyName("security_limits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityLimits? SecurityLimits { get; set; }

    /// <summary>
    /// How <c>OutputFormat.Html</c> is rendered. <c>null</c> keeps the markdown-based renderer;
    /// setting it selects <see cref="Xberg.Rendering.StyledHtmlRenderer"/>.
    /// </summary>
    [JsonPropertyName("html_output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HtmlOutputConfig? HtmlOutput { get; set; }

    /// <summary>
    /// The input's file name, when the caller knows it. Used to fall back to extension-based
    /// language detection where content-based detection — a shebang — says nothing.
    /// </summary>
    /// <remarks>
    /// Not part of the wire format, matching upstream's <c>#[serde(skip)]</c>: it describes the
    /// input rather than configuring the extraction.
    /// </remarks>
    [JsonIgnore]
    public string? SourceName { get; set; }

    /// <summary>
    /// Port-local behavioural knobs (deadlines, implementation switches). Defaults to
    /// <see cref="XbergOptions.Default"/>. Not part of the wire format: everything above mirrors
    /// upstream's config field for field, and these have no upstream counterpart.
    /// </summary>
    [JsonIgnore]
    public XbergOptions Options { get; set; } = XbergOptions.Default;
}

/// <summary>Kind of extraction input. Serialized as bare snake_case string.</summary>
public enum ExtractInputKind
{
    Bytes,
    Uri,
}

/// <summary>An extraction input: raw bytes with a MIME type, or a URI/path.</summary>
public sealed class ExtractInput
{
    public ExtractInputKind Kind { get; set; } = ExtractInputKind.Uri;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Bytes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }

    public static ExtractInput FromBytes(byte[] bytes, string mimeType, string? filename = null) => new()
    {
        Kind = ExtractInputKind.Bytes,
        Bytes = bytes,
        MimeType = mimeType,
        Filename = filename,
    };

    public static ExtractInput FromUri(string uri) => new()
    {
        Kind = ExtractInputKind.Uri,
        Uri = uri,
    };
}

public sealed class ExtractionErrorItem
{
    public long Index { get; set; }
    public uint Code { get; set; }
    public string ErrorType { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ExtractionSummary
{
    public long Inputs { get; set; }
    public long Results { get; set; }
    public long Errors { get; set; }
}

/// <summary>Batch envelope of extraction results plus per-input errors.</summary>
public sealed class ExtractionResult
{
    public List<ExtractedDocument> Results { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<ExtractionErrorItem> Errors { get; set; } = new();

    public ExtractionSummary Summary { get; set; } = new();
}
