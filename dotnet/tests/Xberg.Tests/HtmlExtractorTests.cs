using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>Tests for <see cref="HtmlExtractor"/>, <see cref="DocbookExtractor"/> and
/// <see cref="JatsExtractor"/> (ported from the respective Rust extractor tests).</summary>
public class HtmlExtractorTests
{
    private static InternalDocument Html(string html) =>
        new HtmlExtractor().Extract(Encoding.UTF8.GetBytes(html), "text/html", new ExtractionConfig());

    private static InternalDocument Docbook(string xml) =>
        new DocbookExtractor().Extract(Encoding.UTF8.GetBytes(xml), "application/docbook+xml", new ExtractionConfig());

    private static InternalDocument Jats(string xml) =>
        new JatsExtractor().Extract(Encoding.UTF8.GetBytes(xml), "application/x-jats+xml", new ExtractionConfig());

    // ── HTML ─────────────────────────────────────────────────────────────────
    [Fact]
    public void Html_HeadingsAndParagraph()
    {
        var doc = Html("<h1>Title</h1><p>Hello world.</p>");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Kind.Level == 1 && e.Text == "Title");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text == "Hello world.");
    }

    [Fact]
    public void Html_HeadingsAreWrappedInGroups()
    {
        var doc = Html("<h1>A</h1><h2>B</h2><p>text</p>");
        // Two nested section groups → two GroupStart markers.
        Assert.Equal(2, doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.GroupStart));
        Assert.Equal(2, doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.GroupEnd));
        var h2 = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Heading && e.Kind.Level == 2);
        Assert.Equal((ushort)2, h2.Depth);
    }

    [Fact]
    public void Html_UnorderedList()
    {
        var doc = Html("<ul><li>One</li><li>Two</li></ul>");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.ListStart);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.ListItem && e.Text == "One");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.ListItem && e.Text == "Two");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.ListEnd);
    }

    [Fact]
    public void Html_Table()
    {
        var doc = Html("<table><tr><th>Name</th><th>Age</th></tr><tr><td>Alice</td><td>30</td></tr></table>");
        // Rust records each table twice: the DocumentStructure copy (page 0) and the
        // table_data copy (page i+1). Both carry identical cells.
        Assert.Equal(2, doc.Tables.Count);
        Assert.Equal(0u, doc.Tables[0].PageNumber);
        Assert.Equal(1u, doc.Tables[1].PageNumber);
        Assert.Equal(new[] { "Name", "Age" }, doc.Tables[0].Cells[0]);
        Assert.Equal(new[] { "Alice", "30" }, doc.Tables[0].Cells[1]);
    }

    [Fact]
    public void Html_ImagePlaceholderAndUri()
    {
        var doc = Html("<body><img src=\"test.png\" alt=\"test image\"></body>");
        Assert.Contains(doc.Elements, e => e.Text.Contains("![test image](test.png)"));
        Assert.Contains(doc.Uris, u => u.Url == "test.png" && u.Kind == Xberg.Types.UriKind.Image);
    }

    [Fact]
    public void Html_CodeBlock()
    {
        var doc = Html("<pre><code class=\"language-rust\">fn main() {}</code></pre>");
        var code = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Code);
        Assert.Equal("fn main() {}", code.Text);
    }

    [Fact]
    public void Html_NoScriptOrStyleLeaks()
    {
        var doc = Html("<head><style>body{color:red}</style></head><body><script>alert('x')</script><h1>Clean</h1><p>Body</p></body>");
        foreach (var e in doc.Elements)
        {
            Assert.DoesNotContain("color:red", e.Text);
            Assert.DoesNotContain("alert(", e.Text);
        }
    }

    [Fact]
    public void Html_Metadata()
    {
        var html = "<html lang=\"en\"><head><title>My Title</title>" +
            "<meta name=\"description\" content=\"A description\">" +
            "<meta name=\"author\" content=\"Jane Doe\">" +
            "<meta name=\"keywords\" content=\"a, b, c\"></head><body><h1>H</h1></body></html>";
        var doc = Html(html);
        Assert.Equal("My Title", doc.Metadata.Title);
        Assert.Equal("A description", doc.Metadata.Subject);
        Assert.Equal(new[] { "Jane Doe" }, doc.Metadata.Authors);
        Assert.Equal(new[] { "a", "b", "c" }, doc.Metadata.Keywords);
        Assert.Equal("en", doc.Metadata.Language);
        var hm = Assert.IsType<HtmlMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("My Title", hm.Title);
    }

    [Fact]
    public void Html_HeadContentDoesNotLeakIntoBody()
    {
        var doc = Html("<html><head><title>Page Title</title></head><body><p>Body text</p></body></html>");
        Assert.DoesNotContain(doc.Elements, e => e.Text.Contains("Page Title"));
        Assert.Contains(doc.Elements, e => e.Text == "Body text");
    }

    // ── DocBook ────────────────────────────────────────────────────────────
    [Fact]
    public void Docbook_TitleAndParagraph()
    {
        var doc = Docbook("<article><title>Test Article</title><para>Test content.</para></article>");
        Assert.Equal("Test Article", doc.Metadata.Title);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "Test Article");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text == "Test content.");
    }

    [Fact]
    public void Docbook_Table()
    {
        var xml = "<article><table><tgroup cols=\"2\"><thead><row><entry>Col1</entry><entry>Col2</entry></row></thead>" +
            "<tbody><row><entry>Data1</entry><entry>Data2</entry></row></tbody></tgroup></table></article>";
        var doc = Docbook(xml);
        var t = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Col1", "Col2" }, t.Cells[0]);
        Assert.Equal(new[] { "Data1", "Data2" }, t.Cells[1]);
    }

    [Fact]
    public void Docbook_InfoAuthorAndDate()
    {
        var xml = "<article><info><title>Doc</title><author><personname>John Doe</personname></author>" +
            "<date>2024</date></info><para>Body.</para></article>";
        var doc = Docbook(xml);
        Assert.Equal("Doc", doc.Metadata.Title);
        Assert.Equal(new[] { "John Doe" }, doc.Metadata.Authors);
        Assert.Equal("2024", doc.Metadata.CreatedAt);
    }

    [Fact]
    public void Docbook_InlineAnnotations()
    {
        var doc = Docbook("<article><para>This has <emphasis>italic</emphasis> text.</para></article>");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Contains(para.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Italic);
    }

    // ── JATS ──────────────────────────────────────────────────────────────
    [Fact]
    public void Jats_TitleAndBody()
    {
        var xml = "<article><front><article-meta><article-title>The Title</article-title></article-meta></front>" +
            "<body><p>Body paragraph.</p></body></article>";
        var doc = Jats(xml);
        Assert.Equal("The Title", doc.Metadata.Title);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Kind.Level == 1 && e.Text == "The Title");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text == "Body paragraph.");
    }

    [Fact]
    public void Jats_Abstract()
    {
        var xml = "<article><front><article-meta><abstract><sec><title>Background</title>" +
            "<p>The background.</p></sec></abstract></article-meta></front></article>";
        var doc = Jats(xml);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Kind.Level == 2 && e.Text == "Abstract");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Kind.Level == 3 && e.Text == "Background");
    }

    [Fact]
    public void Jats_AuthorsAndKeywordsAndDoi()
    {
        var xml = "<article><front><article-meta><article-title>T</article-title>" +
            "<contrib-group><contrib contrib-type=\"author\"><name><surname>Smith</surname><given-names>John</given-names></name></contrib></contrib-group>" +
            "<article-id pub-id-type=\"doi\">10.1/x</article-id>" +
            "<kwd-group><kwd>alpha</kwd><kwd>beta</kwd></kwd-group></article-meta></front></article>";
        var doc = Jats(xml);
        Assert.Equal(new[] { "Smith John" }, doc.Metadata.Authors);
        Assert.Equal(new[] { "alpha", "beta" }, doc.Metadata.Keywords);
        Assert.Contains(doc.Uris, u => u.Url == "https://doi.org/10.1/x" && u.Kind == Xberg.Types.UriKind.Citation);
    }

    [Fact]
    public void Jats_Table()
    {
        var xml = "<article><body><table-wrap><table><thead><tr><th>Study</th><th>Year</th></tr></thead>" +
            "<tbody><tr><td>A</td><td>2003</td></tr></tbody></table></table-wrap></body></article>";
        var doc = Jats(xml);
        var t = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Study", "Year" }, t.Cells[0]);
        Assert.Equal(new[] { "A", "2003" }, t.Cells[1]);
    }
}
