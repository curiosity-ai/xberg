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

        var builder = new InternalDocumentBuilder("html");
        new HtmlWalker(html, builder).Walk();
        var doc = builder.Build();

        // Rust's HTML extractor records each table twice: once from the DocumentStructure
        // walk (page_number 0, referenced by a Table element) and again from the separate
        // table_data list with page_number = i+1 (extractors/html.rs). Replicate the second
        // pass so the `tables` collection matches byte-for-byte. The duplicates are not
        // referenced by any element, so they only affect the top-level tables list.
        int structuralTableCount = doc.Tables.Count;
        for (int i = 0; i < structuralTableCount; i++)
        {
            var src = doc.Tables[i];
            doc.Tables.Add(new Table
            {
                Cells = src.Cells.Select(r => new List<string>(r)).ToList(),
                Markdown = src.Markdown,
                PageNumber = (uint)(i + 1),
                BoundingBox = null,
            });
        }

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
            string md = HtmlToMarkdown.Convert(html);
            metadata.OutputFormat = "markdown";
            doc.PreRenderedContent = HtmlToMarkdown.NormalizeHtmlMarkdown(md);
        }

        doc.Metadata = metadata;
        doc.MimeType = mimeType;
        return doc;
    }

    private static bool IsEmpty(HtmlMetadata m) =>
        m.Title is null && m.Description is null && m.Keywords.Count == 0 && m.Author is null &&
        m.CanonicalUrl is null && m.BaseHref is null && m.Language is null && m.TextDirection is null &&
        m.OpenGraph.Count == 0 && m.TwitterCard.Count == 0 && m.MetaTags.Count == 0 &&
        m.Headers.Count == 0 && m.Links.Count == 0 && m.Images.Count == 0 && m.StructuredData.Count == 0;
}
