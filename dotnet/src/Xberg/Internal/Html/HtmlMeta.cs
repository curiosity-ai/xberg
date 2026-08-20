using System.Text;
using System.Text.Json.Serialization;
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
        string? headingId = null;
        // html-to-markdown walks a table's subtree three times — once to build the grid, once to
        // write the markdown, once for the structure — and the collector records what it sees on
        // every pass. So a table's headings, links and images each appear three times over, in
        // table order, not as three copies in a row. Collections inside a table are buffered and
        // replayed when the outermost table closes; a nested table contributes to that same
        // buffer once, since the repeat is of the whole table region rather than compounding per
        // level of nesting.
        int tableDepth = 0;
        var tableHeaders = new List<object>();
        var tableLinks = new List<object>();
        var tableImages = new List<object>();
        List<object> HeaderSink() => tableDepth > 0 ? tableHeaders : m.Headers;
        List<object> LinkSink() => tableDepth > 0 ? tableLinks : m.Links;
        List<object> ImageSink() => tableDepth > 0 ? tableImages : m.Images;

        void CloseOutermostTable()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                m.Headers.AddRange(tableHeaders);
                m.Links.AddRange(tableLinks);
                m.Images.AddRange(tableImages);
            }
            tableHeaders.Clear();
            tableLinks.Clear();
            tableImages.Clear();
        }
        bool inAnchor = false;
        var anchorText = new StringBuilder();
        string anchorHref = "";
        string? anchorTitle = null;
        List<string[]> anchorAttrs = new();
        List<string> anchorRel = new();
        bool inTitle = false;
        var titleText = new StringBuilder();
        // Preprocessing removes navigation and form subtrees before anything is collected, so a
        // sidebar's `<h2>Contents</h2>` is not one of the document's headings.
        int skipDepth = -1;
        // Head metadata is read from the children of `<head>`. A `<title>` written before the
        // head — as some Federal Register pages are — is not one of them, and a document whose
        // head holds nothing has no metadata at all.
        bool inHead = false;
        // A `<pre>` block is emitted as its raw text, so nothing inside it is visited as an
        // element: a link written inside preformatted text is not one of the document's links.
        int preDepth = 0;

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
                    if (skipDepth >= 0)
                    {
                        if (!Void.Contains(tag) && domDepth > 0)
                        {
                            domDepth--;
                            if (domDepth <= skipDepth) skipDepth = -1;
                        }
                        continue;
                    }
                    if (tag == "pre" && preDepth > 0) preDepth--;
                    if (tag == "table" && tableDepth > 0 && --tableDepth == 0) CloseOutermostTable();
                    if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && captureHeading != 0)
                    {
                        string text = HtmlWalker.NormalizeWhitespace(headingText.ToString());
                        if (text.Length > 0)
                            HeaderSink().Add(new Header
                            {
                                Level = captureHeading, Text = text, Id = headingId,
                                // A heading inside a table is recorded at depth 0 — the passes
                                // that re-walk it have no enclosing tree to count.
                                Depth = tableDepth > 0 ? 0 : headingDepthAtOpen,
                                HtmlOffset = 0,
                            });
                        captureHeading = 0;
                        headingId = null;
                    }
                    else if (tag == "a" && inAnchor)
                    {
                        string text = HtmlWalker.NormalizeWhitespace(anchorText.ToString());
                        var link = new Link
                        {
                            Href = anchorHref,
                            Text = text,
                            Title = anchorTitle,
                            LinkType = ClassifyLink(anchorHref),
                            Rel = anchorRel,
                            Attributes = anchorAttrs,
                        };
                        LinkSink().Add(link);
                        inAnchor = false;
                    }
                    else if (tag == "head") inHead = false;
                    else if (tag == "title" && inTitle)
                    {
                        if (m.Title is null)
                        {
                            // Trimmed but not collapsed: a title written with two spaces between
                            // its halves keeps them.
                            string t = titleText.ToString().Trim();
                            if (t.Length > 0) m.Title = t;
                        }
                        inTitle = false;
                    }
                    if (!Void.Contains(tag) && domDepth > 0) domDepth--;
                    continue;
                }

                if (skipDepth >= 0)
                {
                    if (!Void.Contains(tag) && !selfClose) domDepth++;
                    continue;
                }

                if (!selfClose && !Void.Contains(tag)
                    && HtmlToMarkdown.ShouldDropForPreprocessing(
                        tag,
                        ExtractAttrDecoded(attrsStr, "role"),
                        ExtractAttrDecoded(attrsStr, "aria-label"),
                        ExtractAttrDecoded(attrsStr, "class"),
                        ExtractAttrDecoded(attrsStr, "id")))
                {
                    skipDepth = domDepth;
                    domDepth++;
                    continue;
                }

                if (tag == "pre" && !selfClose) preDepth++;
                if (preDepth > 0 && tag is "a" or "img" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                {
                    if (!Void.Contains(tag) && !selfClose) domDepth++;
                    continue;
                }

                // Opening / self-closing tag.
                switch (tag)
                {
                    // `lang` and `dir` are read from whichever of these carries them, first
                    // occurrence winning: a page that sets direction on `<body>` rather than
                    // `<html>` is still declaring the document's direction.
                    case "head" when !selfClose:
                        inHead = true;
                        goto case "html";
                    case "html":
                    case "body":
                    {
                        string? lang = ExtractAttrDecoded(attrsStr, "lang");
                        if (lang is not null && m.Language is null) m.Language = lang;
                        string? dir = ExtractAttrDecoded(attrsStr, "dir");
                        if (dir is not null && m.TextDirection is null) m.TextDirection = ParseTextDirection(dir);
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
                        if (inHead) { inTitle = true; titleText.Clear(); }
                        break;
                    case "table":
                        if (!selfClose) tableDepth++;
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        captureHeading = tag[1] - '0';
                        headingText.Clear();
                        headingDepthAtOpen = domDepth;
                        headingId = ExtractAttrDecoded(attrsStr, "id");
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
                        // The source is recorded as written. A query string spelled
                        // `?a=1&amp;b=2` stays that way — the collector reads the attribute, it
                        // does not resolve the URL.
                        string? src = HtmlWalker.ExtractAttr(attrsStr, "src");
                        string? alt = HtmlWalker.ExtractAttr(attrsStr, "alt");
                        var attrs = ParseAttrs(attrsStr, exclude: "src", out _);
                        // Dimensions are recorded only when the element states both, which is
                        // what makes them a size rather than half of one.
                        uint[]? dimensions =
                            uint.TryParse(ExtractAttrDecoded(attrsStr, "width"), out uint width)
                            && uint.TryParse(ExtractAttrDecoded(attrsStr, "height"), out uint height)
                                ? [width, height]
                                : null;
                        var image = new Img
                        {
                            Src = src ?? "",
                            // An empty `alt` is the absence of alt text, not alt text that is
                            // empty, and is recorded as absent.
                            Alt = alt is { Length: > 0 } ? alt : null,
                            Title = HtmlWalker.ExtractAttr(attrsStr, "title"),
                            Dimensions = dimensions,
                            ImageType = ClassifyImage(src ?? ""),
                            Attributes = attrs,
                        };
                        ImageSink().Add(image);
                        // A link wrapping an image carries the image markdown as its label text.
                        if (inAnchor) anchorText.Append("![").Append(alt ?? "").Append("](").Append(src ?? "").Append(')');
                        break;
                    }
                    case "script":
                    {
                        string? type = ExtractAttrDecoded(attrsStr, "type");
                        var (close, after) = FindRawTextEnd(html, pos, "script");
                        string body = close < 0 ? html[pos..] : html[pos..close];
                        pos = close < 0 ? n : after;
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
                        var (close, after) = FindRawTextEnd(html, pos, "style");
                        pos = close < 0 ? n : after;
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
        // An unclosed table still had its subtree walked three times.
        if (tableDepth > 0) CloseOutermostTable();
        return m;
    }

    /// <summary>
    /// Locate the end of a raw-text element's body: the offset where <c>&lt;/name</c> starts, and
    /// the offset just past the tag's <c>&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The close tag is not always spelled <c>&lt;/style&gt;</c> — whitespace is allowed before
    /// the bracket, and pages in the corpus write <c>&lt;/style\n&gt;</c>. Matching the literal
    /// swallowed the rest of the document, taking every heading and link after it with it.
    /// </remarks>
    private static (int Start, int After) FindRawTextEnd(string html, int from, string name)
    {
        string needle = "</" + name;
        int search = from;
        while (true)
        {
            int close = html.IndexOf(needle, search, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return (-1, html.Length);
            int k = close + needle.Length;
            while (k < html.Length && char.IsWhiteSpace(html[k])) k++;
            if (k < html.Length && html[k] == '>') return (close, k + 1);
            search = close + 1;
        }
    }

    /// <summary>The `dir` attribute's value, or null when it is not one the spec defines.</summary>
    private static TextDirection? ParseTextDirection(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ltr" => TextDirection.LeftToRight,
        "rtl" => TextDirection.RightToLeft,
        "auto" => TextDirection.Auto,
        _ => null,
    };

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

    /// <summary>
    /// An image source is external only when it names its own scheme. A protocol-relative
    /// <c>//host/path</c> inherits the page's, which makes it relative.
    /// </summary>
    private static string ClassifyImage(string src)
    {
        if (src.StartsWith("data:", StringComparison.Ordinal)) return "data-uri";
        if (src.StartsWith("http://", StringComparison.Ordinal)
            || src.StartsWith("https://", StringComparison.Ordinal)) return "external";
        if (src.StartsWith('<') && src.Contains("svg", StringComparison.Ordinal)) return "inline-svg";
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
            // Attribute values are recorded as written. The collector reads the attribute; it
            // does not resolve it, so `alt="\lambda&gt;0"` keeps its reference.
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

        /// <summary>The heading's `id` attribute, which is what an in-page link targets.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Alt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>
        /// Always serialised, as null when the element states no size — the collector's own
        /// field carries no skip, so a sizeless image still names the key.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public uint[]? Dimensions { get; set; }

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
