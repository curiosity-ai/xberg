using System.Text;
using Xberg.Types;

namespace Xberg.Internal.Html;

/// <summary>
/// Extracts <see cref="HtmlMetadata"/> from raw HTML: head meta/title/link/base, the html lang
/// attribute, and document collections (headers, links, images, JSON-LD structured data).
/// Mirrors the metadata produced by html-to-markdown's conversion result. DOM depth for headers
/// is the count of open ancestor elements.
/// </summary>
public static class HtmlMeta
{
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr",
    };

    public static HtmlMetadata Extract(string html)
    {
        var m = new HtmlMetadata();
        int pos = 0, n = html.Length;
        int domDepth = 0;

        // Text-capture targets
        int captureHeading = 0;           // heading level currently open (0 = none)
        var headingText = new StringBuilder();
        int headingDepthAtOpen = 0;
        int inCell = 0;                   // open <td>/<th> nesting (headings in cells are reprocessed)
        bool inAnchor = false;
        var anchorText = new StringBuilder();
        string anchorHref = "";
        string? anchorTitle = null;
        List<string[]> anchorAttrs = new();
        List<string> anchorRel = new();
        bool inTitle = false;
        var titleText = new StringBuilder();

        while (pos < n)
        {
            if (html[pos] == '<')
            {
                if (string.CompareOrdinal(html, pos, "<!--", 0, 4) == 0)
                {
                    int e = html.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                    pos = e < 0 ? n : e + 3;
                    continue;
                }
                int gt = html.IndexOf('>', pos);
                if (gt < 0) break;
                string raw = html[(pos + 1)..gt];
                pos = gt + 1;
                if (raw.StartsWith('!') || raw.StartsWith('?')) continue;

                bool closing = raw.StartsWith('/');
                string content = closing ? raw[1..] : raw;
                bool selfClose = content.TrimEnd().EndsWith('/');
                content = content.TrimEnd('/').Trim();
                var (nameRaw, attrsStr) = HtmlWalker.SplitTagName(content);
                string tag = nameRaw.ToLowerInvariant();

                if (closing)
                {
                    if (tag is "td" or "th") { if (inCell > 0) inCell--; }
                    if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && captureHeading != 0)
                    {
                        string text = HtmlWalker.NormalizeWhitespace(headingText.ToString());
                        if (text.Length > 0)
                        {
                            // A heading inside a table cell is walked three times (grid + markdown +
                            // structure passes) by html-to-markdown, each recording it at depth 0.
                            if (inCell > 0)
                                for (int r = 0; r < 3; r++)
                                    m.Headers.Add(new Header { Level = captureHeading, Text = text, Depth = 0, HtmlOffset = 0 });
                            else
                                m.Headers.Add(new Header { Level = captureHeading, Text = text, Depth = headingDepthAtOpen, HtmlOffset = 0 });
                        }
                        captureHeading = 0;
                    }
                    else if (tag == "a" && inAnchor)
                    {
                        string text = HtmlWalker.NormalizeWhitespace(anchorText.ToString());
                        m.Links.Add(new Link
                        {
                            Href = anchorHref,
                            Text = text,
                            Title = anchorTitle,
                            LinkType = ClassifyLink(anchorHref),
                            Rel = anchorRel,
                            Attributes = anchorAttrs,
                        });
                        inAnchor = false;
                    }
                    else if (tag == "title" && inTitle)
                    {
                        if (m.Title is null)
                        {
                            string t = HtmlWalker.NormalizeWhitespace(titleText.ToString());
                            if (t.Length > 0) m.Title = t;
                        }
                        inTitle = false;
                    }
                    if (!Void.Contains(tag) && domDepth > 0) domDepth--;
                    continue;
                }

                // Opening / self-closing tag.
                switch (tag)
                {
                    case "html":
                    {
                        string? lang = ExtractAttrDecoded(attrsStr, "lang");
                        if (lang is not null) m.Language = lang;
                        break;
                    }
                    case "meta":
                        HandleMeta(m, attrsStr);
                        break;
                    case "base":
                    {
                        string? href = ExtractAttrDecoded(attrsStr, "href");
                        if (href is not null && m.BaseHref is null) m.BaseHref = href;
                        break;
                    }
                    case "link":
                    {
                        string? rel = ExtractAttrDecoded(attrsStr, "rel");
                        string? href = ExtractAttrDecoded(attrsStr, "href");
                        if (rel is not null && href is not null &&
                            rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(r => r.Equals("canonical", StringComparison.OrdinalIgnoreCase)))
                            m.CanonicalUrl ??= href;
                        break;
                    }
                    case "title":
                        inTitle = true; titleText.Clear();
                        break;
                    case "td": case "th":
                        if (!selfClose) inCell++;
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        captureHeading = tag[1] - '0';
                        headingText.Clear();
                        headingDepthAtOpen = domDepth;
                        break;
                    case "a":
                    {
                        inAnchor = true;
                        anchorText.Clear();
                        anchorHref = ExtractAttrDecoded(attrsStr, "href") ?? "";
                        anchorTitle = ExtractAttrDecoded(attrsStr, "title");
                        anchorAttrs = ParseAttrs(attrsStr, exclude: "href", out anchorRel);
                        break;
                    }
                    case "img":
                    {
                        string? src = ExtractAttrDecoded(attrsStr, "src");
                        string? alt = ExtractAttrDecoded(attrsStr, "alt");
                        var attrs = ParseAttrs(attrsStr, exclude: "src", out _);
                        m.Images.Add(new Img
                        {
                            Src = src ?? "",
                            Alt = alt,
                            ImageType = ClassifyImage(src ?? ""),
                            Attributes = attrs,
                        });
                        // A link wrapping an image carries the image markdown as its label text.
                        if (inAnchor) anchorText.Append("![").Append(alt ?? "").Append("](").Append(src ?? "").Append(')');
                        break;
                    }
                    case "script":
                    {
                        string? type = ExtractAttrDecoded(attrsStr, "type");
                        int close = html.IndexOf("</script>", pos, StringComparison.OrdinalIgnoreCase);
                        string body = close < 0 ? html[pos..] : html[pos..close];
                        pos = close < 0 ? n : close + "</script>".Length;
                        if (type is not null && type.Contains("ld+json", StringComparison.OrdinalIgnoreCase))
                        {
                            string rawJson = body.Trim();
                            if (rawJson.Length > 0)
                                m.StructuredData.Add(new StructuredDatum
                                {
                                    DataType = "json-ld",
                                    RawJson = rawJson,
                                    SchemaType = ExtractSchemaType(rawJson),
                                });
                        }
                        continue; // pos already advanced past </script>
                    }
                    case "style":
                    {
                        int close = html.IndexOf("</style>", pos, StringComparison.OrdinalIgnoreCase);
                        pos = close < 0 ? n : close + "</style>".Length;
                        continue;
                    }
                }

                if (!Void.Contains(tag) && !selfClose) domDepth++;
            }
            else
            {
                int lt = html.IndexOf('<', pos);
                if (lt < 0) lt = n;
                string text = html[pos..lt];
                if (captureHeading != 0) headingText.Append(HtmlWalker.DecodeEntities(text));
                else if (inAnchor) anchorText.Append(HtmlWalker.DecodeEntities(text));
                else if (inTitle) titleText.Append(HtmlWalker.DecodeEntities(text));
                pos = lt;
            }
        }
        return m;
    }

    private static void HandleMeta(HtmlMetadata m, string attrs)
    {
        string? name = ExtractAttrDecoded(attrs, "name");
        string? property = ExtractAttrDecoded(attrs, "property");
        string? contentVal = ExtractAttrDecoded(attrs, "content");
        if (contentVal is null) return;

        if (property is not null && property.StartsWith("og:", StringComparison.OrdinalIgnoreCase))
        {
            m.OpenGraph[property[3..]] = contentVal;
            return;
        }
        if (name is null) return;
        string lname = name.ToLowerInvariant();
        switch (lname)
        {
            case "description": m.Description ??= contentVal; break;
            case "author": m.Author ??= contentVal; break;
            case "keywords":
                if (m.Keywords.Count == 0)
                    foreach (var kw in contentVal.Split(','))
                    {
                        string t = kw.Trim();
                        if (t.Length > 0) m.Keywords.Add(t);
                    }
                break;
            default:
                if (lname.StartsWith("twitter:", StringComparison.Ordinal))
                    m.TwitterCard[name["twitter:".Length..]] = contentVal;
                else
                    m.MetaTags[name] = contentVal;
                break;
        }
    }

    // Mirrors html-to-markdown's LinkMetadata::classify_link (metadata/types.rs). Note the
    // scheme checks are case-sensitive there, and "//"/"/" both classify as internal.
    private static string ClassifyLink(string href)
    {
        if (href.StartsWith('#')) return "anchor";
        if (href.StartsWith("mailto:", StringComparison.Ordinal)) return "email";
        if (href.StartsWith("tel:", StringComparison.Ordinal)) return "phone";
        if (href.StartsWith("http://", StringComparison.Ordinal) ||
            href.StartsWith("https://", StringComparison.Ordinal)) return "external";
        if (href.StartsWith('/') || href.StartsWith("../", StringComparison.Ordinal) ||
            href.StartsWith("./", StringComparison.Ordinal)) return "internal";
        return "other";
    }

    private static string ClassifyImage(string src)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "data-uri";
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("//", StringComparison.Ordinal)) return "external";
        return "relative";
    }

    private static string? ExtractSchemaType(string json)
    {
        int idx = json.IndexOf("\"@type\"", StringComparison.Ordinal);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon);
        if (q1 < 0) return null;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json[(q1 + 1)..q2];
    }

    private static string? ExtractAttrDecoded(string attrs, string name)
    {
        var v = HtmlWalker.ExtractAttr(attrs, name);
        return v is null ? null : HtmlWalker.DecodeEntities(v);
    }

    private static List<string[]> ParseAttrs(string attrs, string exclude, out List<string> rel)
    {
        rel = new List<string>();
        var result = new List<string[]>();
        int i = 0, n = attrs.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(attrs[i])) i++;
            if (i >= n) break;
            int ks = i;
            while (i < n && attrs[i] != '=' && !char.IsWhiteSpace(attrs[i]) && attrs[i] != '>') i++;
            string key = attrs[ks..i];
            if (key.Length == 0) { i++; continue; }
            while (i < n && char.IsWhiteSpace(attrs[i])) i++;
            string value = "";
            if (i < n && attrs[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(attrs[i])) i++;
                if (i < n && (attrs[i] == '"' || attrs[i] == '\''))
                {
                    char q = attrs[i++];
                    int vs = i;
                    while (i < n && attrs[i] != q) i++;
                    value = attrs[vs..i];
                    if (i < n) i++;
                }
                else
                {
                    int vs = i;
                    while (i < n && !char.IsWhiteSpace(attrs[i]) && attrs[i] != '>') i++;
                    value = attrs[vs..i];
                }
            }
            value = HtmlWalker.DecodeEntities(value);
            if (key.Equals("rel", StringComparison.OrdinalIgnoreCase))
                rel.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!key.Equals(exclude, StringComparison.OrdinalIgnoreCase))
                result.Add(new[] { key, value });
        }
        // html-to-markdown collects attributes into a BTreeMap → alphabetical by key.
        result.Sort((a, b) => string.CompareOrdinal(a[0], b[0]));
        return result;
    }

    // Shapes for HtmlMetadata's List<object> collections. Serialized with the global
    // snake_case policy → level/text/depth/html_offset, link_type, image_type, etc.
    private sealed class Header
    {
        public int Level { get; set; }
        public string Text { get; set; } = "";
        public int Depth { get; set; }
        public int HtmlOffset { get; set; }
    }

    private sealed class Link
    {
        public string Href { get; set; } = "";
        public string Text { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }
        public string LinkType { get; set; } = "other";
        public List<string> Rel { get; set; } = new();
        public List<string[]> Attributes { get; set; } = new();
    }

    private sealed class Img
    {
        public string Src { get; set; } = "";
        public string? Alt { get; set; }
        public object? Dimensions { get; set; }
        public string ImageType { get; set; } = "external";
        public List<string[]> Attributes { get; set; } = new();
    }

    private sealed class StructuredDatum
    {
        public string DataType { get; set; } = "json-ld";
        public string RawJson { get; set; } = "";
        public string? SchemaType { get; set; }
    }
}
