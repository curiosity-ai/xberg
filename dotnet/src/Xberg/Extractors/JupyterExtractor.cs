using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xberg.Core;
using Xberg.Internal.Markup;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Jupyter Notebook (.ipynb) extractor. Ported from Rust `extractors/jupyter.rs`. Markdown cells
/// become headings/paragraphs, code cells become code blocks with output markers. Image decoding
/// (an OCR-adjacent concern) is omitted; images do not affect plain/json/meta/tables parity.
/// </summary>
public sealed class JupyterExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-ipynb+json" };
    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var node = JsonNode.Parse(content.ToArray()) ?? throw new JsonException("invalid notebook");
        var notebook = node.AsObject();

        var additional = new Dictionary<string, JsonNode?>();
        string? languageName = null;

        var nbMeta = notebook["metadata"] as JsonObject;
        if (nbMeta is not null)
        {
            if (nbMeta.TryGetPropertyValue("kernelspec", out var ks) && ks is not null)
                additional["kernelspec"] = ks.DeepClone();
            if (nbMeta.TryGetPropertyValue("language_info", out var li) && li is not null)
            {
                additional["language_info"] = li.DeepClone();
                if (li is JsonObject lio)
                {
                    if (lio.TryGetPropertyValue("name", out var n) && n is not null) { additional["language_name"] = n.DeepClone(); languageName = (n as JsonValue)?.ToString(); }
                    if (lio.TryGetPropertyValue("version", out var v) && v is not null) additional["language_version"] = v.DeepClone();
                    if (lio.TryGetPropertyValue("mimetype", out var mt) && mt is not null) additional["language_mimetype"] = mt.DeepClone();
                }
            }
        }
        if (notebook.TryGetPropertyValue("nbformat", out var nf) && nf is not null) additional["nbformat"] = nf.DeepClone();
        if (notebook.TryGetPropertyValue("nbformat_minor", out var nfm) && nfm is not null) additional["nbformat_minor"] = nfm.DeepClone();

        var cells = notebook["cells"] as JsonArray;
        if (cells is not null)
        {
            additional["cell_count"] = JsonValue.Create(cells.Count);
            var cellsMeta = new JsonArray();
            for (int idx = 0; idx < cells.Count; idx++)
            {
                var cell = cells[idx] as JsonObject;
                string cellType = (cell?["cell_type"] as JsonValue)?.ToString() ?? "unknown";
                var entry = new JsonObject { ["index"] = idx, ["cell_type"] = cellType };
                if (cellType == "code" && cell is not null && cell.TryGetPropertyValue("execution_count", out var ec))
                    entry["execution_count"] = ec?.DeepClone();
                var tags = (cell?["metadata"] as JsonObject)?["tags"] as JsonArray;
                if (tags is not null && tags.Count > 0)
                    entry["tags"] = tags.DeepClone();
                cellsMeta.Add(entry);
            }
            additional["cells"] = cellsMeta;
        }

        var doc = BuildInternalDocument(notebook);
        doc.MimeType = mimeType;
        doc.Metadata = new Metadata { Language = languageName };
        foreach (var (k, v) in additional)
            doc.Metadata.Additional[k] = JsonSerializer.SerializeToElement(v, Json.Options);
        return doc;
    }

    private static InternalDocument BuildInternalDocument(JsonObject notebook)
    {
        var builder = new InternalDocumentBuilder("jupyter");
        var cells = notebook["cells"] as JsonArray;
        if (cells is null) return builder.Build();

        string? kernelLang =
            (((notebook["metadata"] as JsonObject)?["kernelspec"] as JsonObject)?["language"] as JsonValue)?.ToString()
            ?? (((notebook["metadata"] as JsonObject)?["language_info"] as JsonObject)?["name"] as JsonValue)?.ToString();

        if (kernelLang is not null) builder.PushParagraph($"[kernel_language: {kernelLang}]", new(), null, null);

        foreach (var cellNode in cells)
        {
            var cell = cellNode as JsonObject;
            if (cell is null) continue;
            string cellType = (cell["cell_type"] as JsonValue)?.ToString() ?? "unknown";
            string sourceText = ExtractSource(cell["source"]);
            string trimmed = sourceText.Trim();

            if (cell["id"] is JsonValue idv && idv.TryGetValue(out string? cellId) && cellId is not null)
                builder.PushParagraph($"[cell_id: {cellId}]", new(), null, null);

            var tags = (cell["metadata"] as JsonObject)?["tags"] as JsonArray;
            if (tags is not null && tags.Count > 0)
            {
                var tagStrs = tags.Select(t => (t as JsonValue)?.ToString()).Where(s => s is not null).Select(s => s!).ToList();
                if (tagStrs.Count > 0) builder.PushParagraph($"[tags: {string.Join(",", tagStrs)}]", new(), null, null);
            }

            if (trimmed.Length == 0) continue;

            switch (cellType)
            {
                case "markdown":
                {
                    var paraBuf = new StringBuilder();
                    foreach (var line in MarkupHelpers.Lines(trimmed))
                    {
                        var heading = ParseHeadingLine(line);
                        if (heading is not null)
                        {
                            string flushed = paraBuf.ToString().Trim();
                            if (flushed.Length != 0)
                            {
                                var (stripped, anns) = ScanMarkdownInline(flushed);
                                builder.PushParagraph(stripped, anns, null, null);
                            }
                            paraBuf.Clear();
                            if (heading.Value.text.Length != 0) builder.PushHeading(heading.Value.level, heading.Value.text, null, null);
                        }
                        else
                        {
                            if (paraBuf.Length != 0) paraBuf.Append('\n');
                            paraBuf.Append(line);
                        }
                    }
                    string flushed2 = paraBuf.ToString().Trim();
                    if (flushed2.Length != 0)
                    {
                        var (stripped, anns) = ScanMarkdownInline(flushed2);
                        builder.PushParagraph(stripped, anns, null, null);
                    }
                    break;
                }
                case "code":
                {
                    uint idx = builder.PushCode(trimmed, kernelLang, null, null);
                    var attrs = new Dictionary<string, string>();
                    if (cell.TryGetPropertyValue("execution_count", out var ec))
                    {
                        if (ec is JsonValue ecv && ecv.GetValueKind() == JsonValueKind.Number) attrs["execution_count"] = ecv.ToJsonString();
                        else if (ec is null || ec.GetValueKind() == JsonValueKind.Null) attrs["execution_count"] = "null";
                    }
                    if (tags is not null && tags.Count > 0)
                    {
                        var tagStrs = tags.Select(t => (t as JsonValue)?.ToString()).Where(s => s is not null).Select(s => s!).ToList();
                        attrs["tags"] = string.Join(",", tagStrs);
                    }
                    if (attrs.Count > 0) builder.SetAttributes(idx, attrs);

                    if (cell.TryGetPropertyValue("execution_count", out var ec2))
                    {
                        if (ec2 is JsonValue ecv2 && ecv2.GetValueKind() == JsonValueKind.Number)
                            builder.PushParagraph($"execution_count: {ecv2.ToJsonString()}", new(), null, null);
                        else if (ec2 is null || ec2.GetValueKind() == JsonValueKind.Null)
                            builder.PushParagraph("execution_count: null", new(), null, null);
                    }

                    var outputs = cell["outputs"] as JsonArray;
                    if (outputs is not null)
                    {
                        foreach (var outNode in outputs)
                        {
                            var output = outNode as JsonObject;
                            if (output is null) continue;
                            string outputType = (output["output_type"] as JsonValue)?.ToString() ?? "unknown";
                            builder.PushParagraph($"[output_type: {outputType}]", new(), null, null);
                            if (output["data"] as JsonObject is JsonObject data)
                                foreach (var kv in data) builder.PushParagraph($"[mime: {kv.Key}]", new(), null, null);
                            string outputText = CollectOutputText(output).Trim();
                            if (outputText.Length != 0) builder.PushParagraph(outputText, new(), null, null);
                        }
                    }
                    break;
                }
                default:
                    builder.PushParagraph(trimmed, new(), null, null);
                    break;
            }
        }

        return builder.Build();
    }

    private static string ExtractSource(JsonNode? source)
    {
        if (source is JsonValue v && v.TryGetValue(out string? s)) return s ?? "";
        if (source is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var item in arr) if (item is JsonValue iv && iv.TryGetValue(out string? str)) sb.Append(str);
            return sb.ToString();
        }
        return "";
    }

    private static string CollectOutputText(JsonObject output)
    {
        string outputType = (output["output_type"] as JsonValue)?.ToString() ?? "";
        switch (outputType)
        {
            case "stream":
                return output.TryGetPropertyValue("text", out var t) ? ExtractSource(t) : "";
            case "execute_result":
            case "display_data":
                if (output["data"] as JsonObject is JsonObject data && data.TryGetPropertyValue("text/plain", out var p))
                    return ExtractSource(p);
                return "";
            case "error":
                string ename = (output["ename"] as JsonValue)?.ToString() ?? "Unknown";
                string evalue = (output["evalue"] as JsonValue)?.ToString() ?? "";
                return $"Error ({ename}): {evalue}";
            default:
                return "";
        }
    }

    private static (byte level, string text)? ParseHeadingLine(string line)
    {
        string trimmed = line.TrimStart();
        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
        if (hashes == 0 || hashes > 6) return null;
        string rest = trimmed.Substring(hashes);
        if (rest.Length != 0 && !rest.StartsWith(' ')) return null;
        return ((byte)hashes, rest.Trim());
    }

    // ── inline markdown scan (byte-based, mirrors the Rust byte-as-char behaviour) ──

    private static (string, List<TextAnnotation>) ScanMarkdownInline(string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        int len = b.Length;
        var outBuf = new Utf8Buf();
        var anns = new List<TextAnnotation>();
        int i = 0;

        string Slice(int s, int e) => Encoding.UTF8.GetString(b, s, e - s);

        while (i < len)
        {
            if (i + 1 < len && b[i] == (byte)'*' && b[i + 1] == (byte)'*')
            {
                int end = FindClosing2(b, i + 2, (byte)'*', (byte)'*');
                if (end >= 0)
                {
                    uint start = outBuf.Len; outBuf.Append(Slice(i + 2, end)); uint e2 = outBuf.Len;
                    anns.Add(MarkupHelpers.Annotation(start, e2, MarkupHelpers.Bold));
                    i = end + 2; continue;
                }
            }
            if (b[i] == (byte)'*' && (i == 0 || b[i - 1] != (byte)'*'))
            {
                if (i + 1 < len && b[i + 1] != (byte)'*')
                {
                    int end = FindClosingSingleStar(b, i + 1);
                    if (end >= 0)
                    {
                        uint start = outBuf.Len; outBuf.Append(Slice(i + 1, end)); uint e2 = outBuf.Len;
                        anns.Add(MarkupHelpers.Annotation(start, e2, MarkupHelpers.Italic));
                        i = end + 1; continue;
                    }
                }
            }
            if (b[i] == (byte)'`' && (i + 1 >= len || b[i + 1] != (byte)'`'))
            {
                int end = FindClosingByte(b, i + 1, (byte)'`');
                if (end >= 0)
                {
                    uint start = outBuf.Len; outBuf.Append(Slice(i + 1, end)); uint e2 = outBuf.Len;
                    anns.Add(MarkupHelpers.Annotation(start, e2, MarkupHelpers.Code));
                    i = end + 1; continue;
                }
            }
            outBuf.AppendChar((char)b[i]);
            i++;
        }
        return (outBuf.ToString(), anns);
    }

    private static int FindClosing2(byte[] b, int start, byte d0, byte d1)
    {
        int i = start;
        while (i + 1 < b.Length) { if (b[i] == d0 && b[i + 1] == d1) return i; i++; }
        return -1;
    }
    private static int FindClosingSingleStar(byte[] b, int start)
    {
        int i = start;
        while (i < b.Length) { if (b[i] == (byte)'*' && (i + 1 >= b.Length || b[i + 1] != (byte)'*')) return i; i++; }
        return -1;
    }
    private static int FindClosingByte(byte[] b, int start, byte delim)
    {
        for (int i = start; i < b.Length; i++) if (b[i] == delim) return i;
        return -1;
    }
}
