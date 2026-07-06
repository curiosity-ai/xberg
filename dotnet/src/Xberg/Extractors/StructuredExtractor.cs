using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xberg.Core;
using Xberg.Internal.Toml;
using Xberg.Internal.Yaml;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Structured data extractor (JSON, JSONL/NDJSON, YAML, TOML).
/// Ported from Rust `extractors/structured.rs` + `extraction/structured.rs`.
/// </summary>
public sealed class StructuredExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/json",
        "text/json",
        "application/csl+json",
        "application/x-ndjson",
        "application/jsonl",
        "application/x-jsonlines",
        "application/yaml",
        "application/x-yaml",
        "text/yaml",
        "text/x-yaml",
        "application/toml",
        "text/toml",
    };

    public int Priority => 50;

    // Field-name keywords (exact match, case-insensitive) that mark a text-bearing field.
    private static readonly HashSet<string> TextFieldKeywords = new(StringComparer.Ordinal)
    {
        "title", "name", "subject", "description", "content", "body", "text", "message",
        "payload", "data", "properties", "metadata", "value", "result", "summary", "label",
        "comment", "note", "info", "spec", "status", "kind", "type", "key", "id", "url",
        "path", "author", "email", "address", "version", "tag", "category", "caption",
        "heading", "abstract", "readme", "changelog", "license",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        byte[] bytes = content.ToArray();

        StructuredResult result = mimeType switch
        {
            "application/json" or "text/json" or "application/csl+json" => ParseJson(bytes),
            "application/x-ndjson" or "application/jsonl" or "application/x-jsonlines" => ParseJsonl(bytes),
            "application/yaml" or "application/x-yaml" or "text/yaml" or "text/x-yaml" => ParseYaml(bytes),
            "application/toml" or "text/toml" => ParseToml(bytes),
            _ => throw new NotSupportedException($"Unsupported MIME type: {mimeType}"),
        };

        string sourceFormat = mimeType switch
        {
            "application/json" or "text/json" or "application/csl+json" => "json",
            "application/x-ndjson" or "application/jsonl" or "application/x-jsonlines" => "jsonl",
            "application/yaml" or "application/x-yaml" or "text/yaml" or "text/x-yaml" => "yaml",
            "application/toml" or "text/toml" => "toml",
            _ => "structured",
        };

        string? language = sourceFormat switch
        {
            "json" or "jsonl" => "json",
            "yaml" => "yaml",
            "toml" => "toml",
            _ => null,
        };

        var doc = BuildInternalDocument(result, sourceFormat, language);
        doc.MimeType = mimeType;

        // Assemble metadata.additional: field_count, data_format, plus text-field entries.
        var additional = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["field_count"] = JsonSerializer.SerializeToElement(result.TextFields.Count),
            ["data_format"] = JsonSerializer.SerializeToElement(result.Format),
        };
        foreach (var (key, value) in result.Metadata)
            additional[key] = JsonSerializer.SerializeToElement(value);

        doc.Metadata = new Metadata { Additional = additional };
        return doc;
    }

    // ── document building ───────────────────────────────────────────────────

    private static InternalDocument BuildInternalDocument(StructuredResult result, string sourceFormat, string? language)
    {
        // For JSON objects, build a heading/list/paragraph structure. Rust re-parses
        // `result.content` (pretty JSON); we keep the already-parsed root element.
        if (sourceFormat == "json" && result.JsonRoot is JsonElement root && root.ValueKind == JsonValueKind.Object)
        {
            var builder = new InternalDocumentBuilder(sourceFormat);
            BuildJsonInternalStructure(root, builder, 1);
            return builder.Build();
        }

        // Fallback: a single code block with the raw content.
        var fallback = new InternalDocumentBuilder(sourceFormat);
        fallback.PushCode(result.Content, language, null, null);
        return fallback.Build();
    }

    private static void BuildJsonInternalStructure(JsonElement value, InternalDocumentBuilder builder, int depth)
    {
        byte level = (byte)Math.Min(depth, 6);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    var val = prop.Value;
                    switch (val.ValueKind)
                    {
                        case JsonValueKind.Object:
                            builder.PushHeading(level, prop.Name, null, null);
                            BuildJsonInternalStructure(val, builder, depth + 1);
                            break;
                        case JsonValueKind.Array:
                            builder.PushHeading(level, prop.Name, null, null);
                            builder.PushList(false);
                            foreach (var item in val.EnumerateArray())
                            {
                                string text = item.ValueKind == JsonValueKind.String
                                    ? item.GetString()!
                                    : SerdeJson.Compact(item);
                                builder.PushListItem(text, false, new(), null, null);
                            }
                            builder.EndList();
                            break;
                        case JsonValueKind.String:
                            builder.PushParagraph($"{prop.Name}: {val.GetString()}", new(), null, null);
                            break;
                        default:
                            builder.PushParagraph($"{prop.Name}: {SerdeJson.Compact(val)}", new(), null, null);
                            break;
                    }
                }
                break;
            case JsonValueKind.Array:
                builder.PushList(false);
                foreach (var item in value.EnumerateArray())
                {
                    string text = item.ValueKind == JsonValueKind.String ? item.GetString()! : SerdeJson.Compact(item);
                    builder.PushListItem(text, false, new(), null, null);
                }
                builder.EndList();
                break;
            case JsonValueKind.String:
                builder.PushParagraph(value.GetString()!, new(), null, null);
                break;
            default:
                builder.PushParagraph(SerdeJson.Compact(value), new(), null, null);
                break;
        }
    }

    // ── format parsers ──────────────────────────────────────────────────────

    private static StructuredResult ParseJson(byte[] data)
    {
        using var doc = JsonDocument.Parse(StripBom(data));
        var root = doc.RootElement.Clone();
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var textFields = new List<string>();
        var path = new StringBuilder();
        ExtractFromJson(root, path, meta, textFields);
        string content = SerdeJson.Pretty(root);
        return new StructuredResult(content, "json", meta, textFields, root);
    }

    private static StructuredResult ParseJsonl(byte[] data)
    {
        string text = Encoding.UTF8.GetString(StripBom(data).Span);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var textFields = new List<string>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var rawLine in text.Split('\n'))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0) continue;
                var doc = JsonDocument.Parse(trimmed);
                docs.Add(doc);
                var path = new StringBuilder();
                ExtractFromJson(doc.RootElement, path, meta, textFields);
            }

            string content;
            if (docs.Count == 0)
            {
                content = "[]";
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = 0; i < docs.Count; i++)
                {
                    sb.Append('\n');
                    SerdeJson.Indent(sb, 1);
                    SerdeJson.WriteValue(docs[i].RootElement, sb, 1);
                    if (i < docs.Count - 1) sb.Append(',');
                }
                sb.Append("\n]");
                content = sb.ToString();
            }
            return new StructuredResult(content, "jsonl", meta, textFields, null);
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    private static StructuredResult ParseYaml(byte[] data)
    {
        string text = Encoding.UTF8.GetString(StripBom(data).Span);
        var value = YamlParser.Parse(text);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var textFields = new List<string>();
        var path = new StringBuilder();
        ExtractFromNode(value, path, meta, textFields);
        return new StructuredResult(text, "yaml", meta, textFields, null);
    }

    private static StructuredResult ParseToml(byte[] data)
    {
        string text = Encoding.UTF8.GetString(StripBom(data).Span);
        var value = TomlParser.Parse(text);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var textFields = new List<string>();
        var path = new StringBuilder();
        ExtractFromNode(value, path, meta, textFields);
        return new StructuredResult(text, "toml", meta, textFields, null);
    }

    // ── metadata walks ──────────────────────────────────────────────────────

    private static void ExtractFromJson(JsonElement value, StringBuilder path,
        Dictionary<string, string> meta, List<string> textFields)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    int baseLen = path.Length;
                    if (path.Length > 0) path.Append('.');
                    path.Append(prop.Name);
                    ExtractFromJson(prop.Value, path, meta, textFields);
                    path.Length = baseLen;
                }
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    int baseLen = path.Length;
                    if (path.Length == 0) { path.Append("item_"); path.Append(i); }
                    else { path.Append('['); path.Append(i); path.Append(']'); }
                    ExtractFromJson(item, path, meta, textFields);
                    path.Length = baseLen;
                    i++;
                }
                break;
            case JsonValueKind.String:
                string s = value.GetString()!;
                if (s.Trim().Length > 0 && IsTextField(path.ToString()))
                {
                    meta[path.ToString()] = s;
                    textFields.Add(path.ToString());
                }
                break;
        }
    }

    // Walk over the JsonNode trees produced by the YAML/TOML parsers.
    private static void ExtractFromNode(JsonNode? value, StringBuilder path,
        Dictionary<string, string> meta, List<string> textFields)
    {
        switch (value)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    int baseLen = path.Length;
                    if (path.Length > 0) path.Append('.');
                    path.Append(kv.Key);
                    ExtractFromNode(kv.Value, path, meta, textFields);
                    path.Length = baseLen;
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    int baseLen = path.Length;
                    if (path.Length == 0) { path.Append("item_"); path.Append(i); }
                    else { path.Append('['); path.Append(i); path.Append(']'); }
                    ExtractFromNode(arr[i], path, meta, textFields);
                    path.Length = baseLen;
                }
                break;
            case JsonValue jv when jv.GetValueKind() == JsonValueKind.String:
                string s = jv.GetValue<string>();
                if (s.Trim().Length > 0 && IsTextField(path.ToString()))
                {
                    meta[path.ToString()] = s;
                    textFields.Add(path.ToString());
                }
                break;
        }
    }

    private static bool IsTextField(string key)
    {
        int dot = key.LastIndexOf('.');
        string leaf = dot >= 0 ? key[(dot + 1)..] : key;
        int bracket = leaf.IndexOf('[');
        if (bracket >= 0) leaf = leaf[..bracket];
        return TextFieldKeywords.Contains(leaf.ToLowerInvariant());
    }

    private static ReadOnlyMemory<byte> StripBom(byte[] data) =>
        data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF
            ? data.AsMemory(3)
            : data;

    private readonly record struct StructuredResult(
        string Content, string Format, Dictionary<string, string> Metadata,
        List<string> TextFields, JsonElement? JsonRoot);
}

/// <summary>
/// serde_json-faithful JSON serialization (pretty with 2-space indent, and compact),
/// operating on <see cref="JsonElement"/>. Number tokens are emitted verbatim (raw text),
/// strings use serde's escape set, and non-ASCII / <c>/</c> / <c>&lt;</c> are not escaped.
/// </summary>
internal static class SerdeJson
{
    public static string Pretty(JsonElement e)
    {
        var sb = new StringBuilder();
        WriteValue(e, sb, 0);
        return sb.ToString();
    }

    public static string Compact(JsonElement e)
    {
        var sb = new StringBuilder();
        WriteCompact(e, sb);
        return sb.ToString();
    }

    public static void Indent(StringBuilder sb, int level) => sb.Append(' ', 2 * level);

    public static void WriteValue(JsonElement e, StringBuilder sb, int level)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                using (var it = e.EnumerateObject())
                {
                    if (!it.MoveNext()) { sb.Append("{}"); break; }
                    sb.Append('{');
                    bool first = true;
                    do
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('\n');
                        Indent(sb, level + 1);
                        WriteString(it.Current.Name, sb);
                        sb.Append(": ");
                        WriteValue(it.Current.Value, sb, level + 1);
                    } while (it.MoveNext());
                    sb.Append('\n');
                    Indent(sb, level);
                    sb.Append('}');
                }
                break;
            case JsonValueKind.Array:
                using (var it = e.EnumerateArray())
                {
                    if (!it.MoveNext()) { sb.Append("[]"); break; }
                    sb.Append('[');
                    bool first = true;
                    do
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('\n');
                        Indent(sb, level + 1);
                        WriteValue(it.Current, sb, level + 1);
                    } while (it.MoveNext());
                    sb.Append('\n');
                    Indent(sb, level);
                    sb.Append(']');
                }
                break;
            case JsonValueKind.String:
                WriteString(e.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                sb.Append(FormatNumber(e));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteCompact(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool firstO = true;
                foreach (var prop in e.EnumerateObject())
                {
                    if (!firstO) sb.Append(',');
                    firstO = false;
                    WriteString(prop.Name, sb);
                    sb.Append(':');
                    WriteCompact(prop.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool firstA = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstA) sb.Append(',');
                    firstA = false;
                    WriteCompact(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(e.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                sb.Append(FormatNumber(e));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    // Exact power-of-ten table (10^0 .. 10^308), each the nearest f64. Mirrors serde_json's
    // `static POW10: [f64; 309]`. Built via correctly-rounded parse of the `1e{n}` literals.
    private static readonly double[] Pow10 = BuildPow10();

    private static double[] BuildPow10()
    {
        var t = new double[309];
        for (int i = 0; i < t.Length; i++)
            t[i] = double.Parse("1e" + i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return t;
    }

    // Faithful port of serde_json's default (non-`float_roundtrip`) f64 parser: significand
    // accumulation with u64-overflow handling (parse_long_integer / parse_decimal_overflow),
    // then `f64_from_parts` (significand-as-f64 scaled by an exact power of ten).
    internal static double SerdeParseF64(string raw)
    {
        int i = 0;
        bool positive = true;
        if (i < raw.Length && raw[i] == '-') { positive = false; i++; }
        else if (i < raw.Length && raw[i] == '+') { i++; }

        ulong significand = 0;
        int exponent = 0;
        bool sigFull = false; // significand can no longer accept digits without u64 overflow

        // Integer part.
        while (i < raw.Length && raw[i] >= '0' && raw[i] <= '9')
        {
            uint digit = (uint)(raw[i] - '0');
            if (!sigFull && significand <= (ulong.MaxValue - digit) / 10)
                significand = significand * 10 + digit;
            else { sigFull = true; exponent++; } // parse_long_integer: count skipped int digits
            i++;
        }

        // Fractional part.
        if (i < raw.Length && raw[i] == '.')
        {
            i++;
            while (i < raw.Length && raw[i] >= '0' && raw[i] <= '9')
            {
                uint digit = (uint)(raw[i] - '0');
                if (!sigFull && significand <= (ulong.MaxValue - digit) / 10)
                {
                    significand = significand * 10 + digit;
                    exponent--;
                }
                else sigFull = true; // parse_decimal_overflow: ignore further fractional digits
                i++;
            }
        }

        // Exponent part.
        if (i < raw.Length && (raw[i] == 'e' || raw[i] == 'E'))
        {
            i++;
            bool posExp = true;
            if (i < raw.Length && (raw[i] == '+' || raw[i] == '-')) { posExp = raw[i] == '+'; i++; }
            int exp = 0;
            while (i < raw.Length && raw[i] >= '0' && raw[i] <= '9')
            {
                int digit = raw[i] - '0';
                if (exp <= (int.MaxValue - digit) / 10) exp = exp * 10 + digit;
                i++;
            }
            exponent += posExp ? exp : -exp;
        }

        return F64FromParts(positive, significand, exponent);
    }

    private static double F64FromParts(bool positive, ulong significand, int exponent)
    {
        double f = significand;
        while (true)
        {
            int abs = exponent == int.MinValue ? int.MaxValue : Math.Abs(exponent);
            if (abs < Pow10.Length)
            {
                double pow = Pow10[abs];
                if (exponent >= 0) f *= pow; else f /= pow;
                break;
            }
            if (f == 0.0) break;
            if (exponent >= 0) { f = double.PositiveInfinity; break; }
            f /= 1e308;
            exponent += 308;
        }
        return positive ? f : -f;
    }

    // Reproduce serde_json's number formatting: integers verbatim (itoa); floats via
    // shortest round-trip (ryu) with a mandatory decimal point and lowercase,
    // sign-stripped exponents.
    //
    // Crucially, the *value* is reparsed with serde_json's default (non-`float_roundtrip`)
    // fast-path float parser, which is NOT correctly rounded: it accumulates the significand
    // as a u64 and multiplies/divides by an exact power-of-ten table, double-rounding when the
    // significand exceeds 2^53. This yields values up to 1 ULP away from the correctly-rounded
    // double that `JsonElement.GetDouble()`/`double.Parse` produce, so we must mirror it to
    // match serde's output byte-for-byte (e.g. `498.92708999999996` → `498.92709`).
    private static string FormatNumber(JsonElement e)
    {
        string raw = e.GetRawText();
        if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) return raw; // integer

        string s = SerdeParseF64(raw).ToString(CultureInfo.InvariantCulture);
        int ei = s.IndexOfAny(new[] { 'e', 'E' });
        if (ei >= 0)
        {
            string mantissa = s.Substring(0, ei);
            string exp = s.Substring(ei + 1);
            bool neg = exp.StartsWith("-", StringComparison.Ordinal);
            exp = exp.TrimStart('+', '-').TrimStart('0');
            if (exp.Length == 0) exp = "0";
            return mantissa + "e" + (neg ? "-" : "") + exp;
        }
        return s.Contains('.') ? s : s + ".0";
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
