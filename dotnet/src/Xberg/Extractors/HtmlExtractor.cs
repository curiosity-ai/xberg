using System.Text;
using Xberg.Core;
using Xberg.Internal.Html;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// HTML document extractor. Ported from Rust `extractors/html.rs` + `extraction/html/`.
/// The Rust path uses the `html-to-markdown-rs` crate; this port reimplements the byte-level
/// walker (<see cref="HtmlWalker"/>) that produces the equivalent InternalDocument (headings
/// wrapped in section Groups, paragraphs, lists, tables, code, blockquotes, images) plus a
/// metadata scan (<see cref="HtmlMeta"/>). Markdown/HTML/Djot rendering is left to the renderers.
/// </summary>
public sealed class HtmlExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "text/html",
        "application/xhtml+xml",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string html = XmlPullReader.Decode(content);

        // The document is built from the structure the markdown converter reports, not from a
        // second walk: upstream's HTML extractor maps the converter's own document structure, and
        // that is why a `<br>` reaches the output as a markdown hard break and a table cell
        // carries its rendered markdown. The structure walker is kept for email and epub, which
        // are the paths upstream uses it for.
        string markdown = HtmlToMarkdown.ConvertWithStructure(html, out var structure);
        var doc = MapStructure(structure, markdown);

        var htmlMeta = HtmlMeta.Extract(html);
        var metadata = new Metadata();
        if (!IsEmpty(htmlMeta))
        {
            metadata.Title = htmlMeta.Title;
            metadata.Authors = htmlMeta.Author is null ? null : new List<string> { htmlMeta.Author };
            metadata.Language = htmlMeta.Language;
            metadata.Subject = htmlMeta.Description;
            metadata.Keywords = htmlMeta.Keywords.Count > 0 ? new List<string>(htmlMeta.Keywords) : null;
            metadata.Format = FormatMetadata.Html(htmlMeta);
        }

        // Mirror Rust extractors/html.rs: when Markdown output is requested, run the
        // html-to-markdown conversion port and store it as pre-rendered content so the
        // pipeline returns it verbatim (after GFM normalization).
        if (config.OutputFormat.Which == OutputFormat.Kind.Markdown)
        {
            metadata.OutputFormat = "markdown";
            doc.PreRenderedContent = HtmlToMarkdown.NormalizeHtmlMarkdown(markdown);
        }

        doc.Metadata = metadata;
        doc.MimeType = mimeType;
        return doc;
    }

    /// <summary>
    /// Build the document from the converter's structure, falling back to the whole markdown as
    /// one paragraph when the structure came back empty — which is what upstream does for a page
    /// with content but no recognised blocks.
    /// </summary>
    private static InternalDocument MapStructure(HtmlStructureCollector structure, string markdown)
    {
        var builder = new InternalDocumentBuilder("html");
        var roots = new List<int>();
        for (int i = 0; i < structure.Nodes.Count; i++)
            if (structure.Nodes[i].Parent < 0) roots.Add(i);
        WalkStructure(structure, roots, builder);
        var doc = builder.Build();

        if (doc.Elements.Count == 0 && markdown.Trim().Length > 0)
        {
            var fallback = new InternalDocumentBuilder("html");
            fallback.PushParagraph(markdown.Trim(), new(), null, null);
            return fallback.Build();
        }
        return doc;
    }

    private static void WalkStructure(HtmlStructureCollector s, List<int> indices, InternalDocumentBuilder b)
    {
        foreach (int idx in indices)
        {
            var node = s.Nodes[idx];
            switch (node.Kind)
            {
                case StructureKind.Group:
                    b.PushGroupStart(node.Label, null);
                    WalkStructure(s, node.Children, b);
                    b.PushGroupEnd();
                    break;
                case StructureKind.Heading:
                    b.PushHeading(node.Level, node.Text, null, null);
                    break;
                case StructureKind.Paragraph:
                    b.PushParagraph(node.Text, new(), null, null);
                    break;
                case StructureKind.List:
                    b.PushList(node.Ordered);
                    WalkStructure(s, node.Children, b);
                    b.EndList();
                    break;
                case StructureKind.ListItem:
                {
                    bool ordered = node.Parent >= 0
                        && s.Nodes[node.Parent] is { Kind: StructureKind.List, Ordered: true };
                    b.PushListItem(node.Text, ordered, new(), null, null);
                    WalkStructure(s, node.Children, b);
                    break;
                }
                case StructureKind.Table:
                    if (node.Cells is { Count: > 0 }) b.PushTableFromCells(node.Cells, null, null);
                    break;
                case StructureKind.Image:
                {
                    string text = node.Description ?? "";
                    if (text.Length > 0 || node.Src is not null)
                    {
                        string display = node.Src is { } src
                            ? (text.Length == 0 ? $"![]({src})" : $"![{text}]({src})")
                            : text;
                        b.PushParagraph(display, new(), null, null);
                    }
                    if (node.Src is { Length: > 0 } imageSrc)
                        b.PushUri(new ExtractedUri { Url = imageSrc, Label = node.Description, Kind = UriKind.Image });
                    break;
                }
                case StructureKind.Code:
                    b.PushCode(node.Text, node.Language, null, null);
                    break;
            }
        }
    }

    private static bool IsEmpty(HtmlMetadata m) =>
        m.Title is null && m.Description is null && m.Keywords.Count == 0 && m.Author is null &&
        m.CanonicalUrl is null && m.BaseHref is null && m.Language is null && m.TextDirection is null &&
        m.OpenGraph.Count == 0 && m.TwitterCard.Count == 0 && m.MetaTags.Count == 0 &&
        m.Headers.Count == 0 && m.Links.Count == 0 && m.Images.Count == 0 && m.StructuredData.Count == 0;
}
