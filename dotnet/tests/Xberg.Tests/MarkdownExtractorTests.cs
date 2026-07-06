using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for <see cref="MarkdownExtractor"/> and <see cref="MdxExtractor"/>. Ports the intent of
/// the Rust `extractors/markdown.rs` / `mdx.rs` / `frontmatter_utils.rs` test cases.
/// </summary>
public class MarkdownExtractorTests
{
    private static InternalDocument Extract(string md, string mime = "text/markdown")
    {
        var ex = new MarkdownExtractor();
        return ex.Extract(Encoding.UTF8.GetBytes(md), mime, new ExtractionConfig());
    }

    [Fact]
    public void FrontmatterMetadataExtracted()
    {
        var doc = Extract("---\ntitle: My Document\nauthor: John Doe\ndate: 2024-01-15\ndescription: A test document\n---\n\n# Content\n\nBody text.");
        Assert.Equal("My Document", doc.Metadata.Title);
        Assert.Equal("John Doe", doc.Metadata.CreatedBy);
        Assert.Equal("2024-01-15", doc.Metadata.CreatedAt);
        Assert.Equal("A test document", doc.Metadata.Subject);
    }

    [Fact]
    public void FrontmatterKeywordsArray()
    {
        var doc = Extract("---\ntitle: Document\nkeywords:\n  - rust\n  - markdown\n  - parsing\n---\n\nContent");
        Assert.NotNull(doc.Metadata.Keywords);
        Assert.Contains("rust", doc.Metadata.Keywords!);
        Assert.Contains("markdown", doc.Metadata.Keywords!);
    }

    [Fact]
    public void TitleFromFirstHeadingWhenNoFrontmatter()
    {
        var doc = Extract("# Main Title\n\nSome content");
        Assert.Equal("Main Title", doc.Metadata.Title);
    }

    [Fact]
    public void HeadingsAndParagraphsExtracted()
    {
        var doc = Extract("# Header\n\nThis is a paragraph with **bold** text.\n\n## Subheading\n\nMore content.");
        var kinds = doc.Elements.Select(e => e.Kind.Tag).ToList();
        Assert.Contains(ElementKindTag.Heading, kinds);
        Assert.Contains(ElementKindTag.Paragraph, kinds);
    }

    [Fact]
    public void TableExtracted()
    {
        var doc = Extract("# Tables\n\n| Header 1 | Header 2 |\n|----------|----------|\n| Cell 1   | Cell 2   |\n| Cell 3   | Cell 4   |");
        Assert.NotEmpty(doc.Tables);
        Assert.Equal(2, doc.Tables[0].Cells[0].Count);
        Assert.Equal("Header 1", doc.Tables[0].Cells[0][0]);
        Assert.NotEmpty(doc.Tables[0].Markdown);
    }

    [Fact]
    public void CodeBlockExtracted()
    {
        var doc = Extract("# Code\n\n```rust\nfn main() {}\n```");
        var code = doc.Elements.FirstOrDefault(e => e.Kind.Tag == ElementKindTag.Code);
        Assert.NotNull(code);
        Assert.Contains("fn main()", code!.Text);
        Assert.Equal("rust", code.Attributes?["language"]);
    }

    [Fact]
    public void BoldAnnotationOnParagraph()
    {
        var doc = Extract("This is **bold** here.");
        var para = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Contains(para.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Bold);
    }

    [Fact]
    public void ListItemsExtracted()
    {
        var doc = Extract("- Alpha\n- Beta\n- Gamma");
        var items = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.ListItem).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("Alpha", items[0].Text);
    }

    [Fact]
    public void LinkUriCollected()
    {
        var doc = Extract("See [Google](https://google.com) here.");
        Assert.Contains(doc.Uris, u => u.Url == "https://google.com");
    }

    [Fact]
    public void MimeTypesSupported()
    {
        var ex = new MarkdownExtractor();
        Assert.Contains("text/markdown", ex.SupportedMimeTypes);
        Assert.Contains("text/x-markdown", ex.SupportedMimeTypes);
    }

    [Fact]
    public void MdxStripsImportsAndJsx()
    {
        var ex = new MdxExtractor();
        var doc = ex.Extract(
            Encoding.UTF8.GetBytes("import { Chart } from './Chart'\n\n# Hello\n\n<Chart data={x} />\n\nText."),
            "text/mdx", new ExtractionConfig());
        var heading = doc.Elements.FirstOrDefault(e => e.Kind.Tag == ElementKindTag.Heading);
        Assert.NotNull(heading);
        Assert.Equal("Hello", heading!.Text);
        // JSX component recorded as a raw block.
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.RawBlock);
    }
}
