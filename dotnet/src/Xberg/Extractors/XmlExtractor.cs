using System.Text;
using Xberg.Core;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// XML extractor. Parses element hierarchy into headings (elements) and paragraphs (text),
/// and records element-count / unique-element metadata. Ported from Rust `extractors/xml.rs`
/// + `extraction/xml.rs`. Uses a lenient hand-rolled tokenizer mirroring quick-xml semantics
/// (raw, un-unescaped text; no end-name checking).
/// </summary>
public sealed class XmlExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/xml",
        "text/xml",
        "image/svg+xml",
        "application/x-endnote+xml",
    };

    public int Priority => 50;

    // SVG elements whose text content is extracted (mirrors the build path's set).
    private static readonly HashSet<string> SvgTextElements = new(StringComparer.Ordinal)
    {
        "text", "tspan", "title", "desc", "textPath",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        bool isSvg = mimeType == "image/svg+xml";
        string xml = Decode(content);

        var doc = new InternalDocument("xml");
        int depth = 0;
        uint index = 0;
        var stack = new List<string>();
        int elementCount = 0;
        var unique = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in Tokenize(xml))
        {
            switch (ev.Kind)
            {
                case EventKind.Start:
                {
                    byte level = (byte)Math.Min(depth + 1, 6);
                    var attrs = FilterAttrs(ev.Attributes);
                    doc.PushElement(MakeElement(ElementKind.Heading(level), ev.Name, depth, index++, attrs));
                    stack.Add(ev.Name);
                    depth++;
                    elementCount++;
                    unique.Add(ev.Name);
                    break;
                }
                case EventKind.End:
                    depth = depth > 0 ? depth - 1 : 0;
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    break;
                case EventKind.Empty:
                {
                    byte level = (byte)Math.Min(depth + 1, 6);
                    var attrs = FilterAttrs(ev.Attributes);
                    doc.PushElement(MakeElement(ElementKind.Heading(level), ev.Name, depth, index++, attrs));
                    elementCount++;
                    unique.Add(ev.Name);
                    break;
                }
                case EventKind.Text:
                {
                    if (isSvg && !stack.Any(SvgTextElements.Contains)) break;
                    string trimmed = ev.Text.Trim();
                    if (trimmed.Length == 0) break;
                    int textDepth = depth > 0 ? depth - 1 : 0;
                    doc.PushElement(MakeElement(ElementKind.Paragraph, trimmed, textDepth, index++, null));
                    break;
                }
                case EventKind.CData:
                {
                    if (isSvg && !stack.Any(SvgTextElements.Contains)) break;
                    string trimmed = ev.Text.Trim();
                    if (trimmed.Length == 0) break;
                    doc.PushElement(MakeElement(ElementKind.Paragraph, trimmed, depth, index++, null));
                    break;
                }
            }
        }

        var uniqueSorted = unique.ToList();
        uniqueSorted.Sort(StringComparer.Ordinal);

        doc.MimeType = mimeType;
        doc.Metadata = new Metadata
        {
            Format = FormatMetadata.Xml(new XmlMetadata
            {
                ElementCount = (uint)elementCount,
                UniqueElements = uniqueSorted,
            }),
        };
        return doc;
    }

    private static InternalElement MakeElement(ElementKind kind, string text, int depth, uint index,
        Dictionary<string, string>? attrs) => new()
    {
        Id = InternalElementId.Generate(kind.Discriminant(), text, null, index),
        Kind = kind,
        Text = text,
        Depth = (ushort)depth,
        Layer = ContentLayer.Body,
        Annotations = new(),
        Attributes = attrs,
    };

    private static Dictionary<string, string>? FilterAttrs(List<(string Key, string Value)>? attrs)
    {
        if (attrs is null || attrs.Count == 0) return null;
        Dictionary<string, string>? result = null;
        foreach (var (key, value) in attrs)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0) continue;
            result ??= new Dictionary<string, string>();
            result[key] = trimmed;
        }
        return result;
    }

    private static string Decode(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return Encoding.Unicode.GetString(content[2..]);
        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(content[2..]);
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return Encoding.UTF8.GetString(content[3..]);
        return Encoding.UTF8.GetString(content);
    }

    // ── tokenizer ─────────────────────────────────────────────────────────

    private enum EventKind { Start, End, Empty, Text, CData }

    private readonly record struct XmlEvent(EventKind Kind, string Name, string Text,
        List<(string Key, string Value)>? Attributes);

    private static IEnumerable<XmlEvent> Tokenize(string s)
    {
        int i = 0;
        int n = s.Length;
        while (i < n)
        {
            if (s[i] == '<')
            {
                if (i + 1 < n && s[i + 1] == '!')
                {
                    if (Match(s, i, "<!--"))
                    {
                        int end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        i = end < 0 ? n : end + 3;
                    }
                    else if (Match(s, i, "<![CDATA["))
                    {
                        int end = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        string body = end < 0 ? s.Substring(i + 9) : s.Substring(i + 9, end - (i + 9));
                        yield return new XmlEvent(EventKind.CData, "", body, null);
                        i = end < 0 ? n : end + 3;
                    }
                    else
                    {
                        // DOCTYPE or other declaration: skip to the closing '>', honoring an internal subset.
                        i = SkipDeclaration(s, i);
                    }
                }
                else if (i + 1 < n && s[i + 1] == '?')
                {
                    int end = s.IndexOf("?>", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                }
                else if (i + 1 < n && s[i + 1] == '/')
                {
                    int gt = s.IndexOf('>', i + 2);
                    if (gt < 0) { i = n; break; }
                    string name = ParseName(s.Substring(i + 2, gt - (i + 2)).Trim());
                    yield return new XmlEvent(EventKind.End, name, "", null);
                    i = gt + 1;
                }
                else
                {
                    int gt = FindTagEnd(s, i);
                    if (gt < 0) { i = n; break; }
                    string inner = s.Substring(i + 1, gt - (i + 1));
                    bool empty = inner.EndsWith("/", StringComparison.Ordinal);
                    if (empty) inner = inner.Substring(0, inner.Length - 1);
                    var (name, attrs) = ParseTag(inner);
                    yield return new XmlEvent(empty ? EventKind.Empty : EventKind.Start, name, "", attrs);
                    i = gt + 1;
                }
            }
            else
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) lt = n;
                // quick-xml emits entity references (&name; / &#..;) as separate GeneralRef
                // events; the build path ignores them, so a run of text is split around each
                // entity into independent Text events.
                int seg = i;
                for (int j = i; j < lt; j++)
                {
                    if (s[j] == '&')
                    {
                        int semi = FindEntityEnd(s, j, lt);
                        if (semi > 0)
                        {
                            if (j > seg) yield return new XmlEvent(EventKind.Text, "", s.Substring(seg, j - seg), null);
                            j = semi;      // loop's j++ moves past ';'
                            seg = semi + 1;
                        }
                    }
                }
                if (lt > seg) yield return new XmlEvent(EventKind.Text, "", s.Substring(seg, lt - seg), null);
                i = lt;
            }
        }
    }

    private static int SkipDeclaration(string s, int i)
    {
        int n = s.Length;
        i += 2; // past "<!"
        int bracket = 0;
        while (i < n)
        {
            char c = s[i];
            if (c == '[') bracket++;
            else if (c == ']') bracket--;
            else if (c == '>' && bracket <= 0) return i + 1;
            i++;
        }
        return n;
    }

    private static int FindTagEnd(string s, int i)
    {
        // Find the closing '>' of a start/empty tag, respecting quoted attribute values.
        bool inS = false, inD = false;
        for (int j = i + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (c == '\'' && !inD) inS = !inS;
            else if (c == '"' && !inS) inD = !inD;
            else if (c == '>' && !inS && !inD) return j;
        }
        return -1;
    }

    private static (string Name, List<(string, string)>? Attributes) ParseTag(string inner)
    {
        int i = 0;
        int n = inner.Length;
        while (i < n && !char.IsWhiteSpace(inner[i])) i++;
        string name = inner.Substring(0, i);

        List<(string, string)>? attrs = null;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(inner[i])) i++;
            if (i >= n) break;

            int keyStart = i;
            while (i < n && inner[i] != '=' && !char.IsWhiteSpace(inner[i])) i++;
            string key = inner.Substring(keyStart, i - keyStart);
            if (key.Length == 0) { i++; continue; }

            while (i < n && char.IsWhiteSpace(inner[i])) i++;
            string value = "";
            if (i < n && inner[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(inner[i])) i++;
                if (i < n && (inner[i] == '"' || inner[i] == '\''))
                {
                    char q = inner[i++];
                    int vs = i;
                    while (i < n && inner[i] != q) i++;
                    value = inner.Substring(vs, i - vs);
                    if (i < n) i++;
                }
                else
                {
                    int vs = i;
                    while (i < n && !char.IsWhiteSpace(inner[i])) i++;
                    value = inner.Substring(vs, i - vs);
                }
            }
            attrs ??= new List<(string, string)>();
            attrs.Add((key, value));
        }
        return (name, attrs);
    }

    private static string ParseName(string s)
    {
        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return s.Substring(0, i);
    }

    // Return the index of the terminating ';' of an entity reference starting at '&' (index a),
    // or -1 if the run is not a well-formed entity name (then '&' is treated as literal text).
    private static int FindEntityEnd(string s, int a, int limit)
    {
        int j = a + 1;
        if (j < limit && s[j] == '#') j++;
        int nameStart = j;
        while (j < limit && s[j] != ';' && j - a <= 32)
        {
            char c = s[j];
            if (!(char.IsLetterOrDigit(c))) return -1;
            j++;
        }
        if (j < limit && s[j] == ';' && j > nameStart) return j;
        return -1;
    }

    private static bool Match(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
}
