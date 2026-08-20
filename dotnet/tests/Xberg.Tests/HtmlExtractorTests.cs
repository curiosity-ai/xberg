using System.Text;
using Xberg.Core;
using Xberg.Internal.Html;
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
        // One entry per table in the source. A second, unreferenced copy used to be appended,
        // which upstream removed: it came from the same conversion pass, so it only duplicated
        // what the document already had.
        Assert.Single(doc.Tables);
        Assert.Equal(0u, doc.Tables[0].PageNumber);
        Assert.Equal(new[] { "Name", "Age" }, doc.Tables[0].Cells[0]);
        Assert.Equal(new[] { "Alice", "30" }, doc.Tables[0].Cells[1]);
    }

    /// <summary>
    /// A rowspan reserves its column in the rows it covers. Advancing through each row on its own
    /// slides every cell beneath one leftwards, so the data stops lining up with its headers.
    /// </summary>
    [Fact]
    public void Html_RowspanReservesItsColumnInLaterRows()
    {
        var doc = Html(
            "<table>" +
            "<tr><td rowspan=\"2\">spans down</td><td>r1c2</td></tr>" +
            "<tr><td>r2c2</td></tr>" +
            "</table>");

        var cells = Assert.Single(doc.Tables).Cells;
        Assert.Equal(new[] { "spans down", "r1c2" }, cells[0]);
        // Not ["r2c2", ""] — column 0 is still covered by the rowspan above.
        Assert.Equal(new[] { "", "r2c2" }, cells[1]);
    }

    /// <summary>A colspan leaves the columns it covers empty, and the row stays rectangular.</summary>
    [Fact]
    public void Html_ColspanLeavesTheColumnsItCoversEmpty()
    {
        var doc = Html(
            "<table>" +
            "<tr><th>a</th><th>b</th><th>c</th></tr>" +
            "<tr><td colspan=\"2\">wide</td><td>c2</td></tr>" +
            "</table>");

        var cells = Assert.Single(doc.Tables).Cells;
        Assert.Equal(new[] { "a", "b", "c" }, cells[0]);
        Assert.Equal(new[] { "wide", "", "c2" }, cells[1]);
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

    // ── documents with no structure to walk ───────────────────────────────────

    /// <summary>
    /// A file that is plain text under an .html name still has content. Loose text is normally
    /// dropped — this walker buffers it in places upstream does not, and flushing at every block
    /// boundary was measured and costs far more than it fixes — but a document that yields no
    /// elements at all has clearly lost everything it had.
    /// </summary>
    [Fact]
    public void ADocumentWithNoMarkupFallsBackToItsText()
    {
        var doc = Html("Hazard Mitigation Technical Assistance Program\nContract No. EMW-2000-CO-0247\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Contains("Hazard Mitigation Technical Assistance Program", para.Text);
    }

    /// <summary>The fallback is only that: a document with real structure is left alone.</summary>
    [Fact]
    public void TheFallbackDoesNotFireWhenTheDocumentHasStructure()
    {
        var doc = Html("<div>loose</div><p>real</p>");
        var paragraphs = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).ToList();
        Assert.Equal(new[] { "real" }, paragraphs.Select(p => p.Text));
    }

    /// <summary>
    /// `<body>` closes an unterminated `<head>`. Reading to the end of the file instead skipped
    /// the whole document, since head content is deliberately not content.
    /// </summary>
    [Fact]
    public void AnUnclosedHeadEndsAtTheBody()
    {
        var doc = Html("<HTML>\n<HEAD>\n<TITLE>Ignored</TITLE>\n<body>\n<h2>Real heading</h2>\n<p>Body text</p>\n");

        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "Real heading");
        Assert.Contains(doc.Elements, e => e.Text == "Body text");
        Assert.DoesNotContain(doc.Elements, e => e.Text.Contains("Ignored", StringComparison.Ordinal));
    }

    /// <summary>
    /// The markdown converter resolves every name in the WHATWG table, not the few dozen the
    /// structure walker knows: a weather page writing <c>&amp;deg;</c> should read "46.84°N",
    /// not "46.84&amp;deg;N".
    /// </summary>
    [Theory]
    [InlineData("46.84&deg;N", "46.84\u00b0N")]
    [InlineData("Versi&oacute;n", "Versi\u00f3n")]
    [InlineData("a &OverBar; b", "a \u203e b")]
    [InlineData("&amp;lt; stays escaped once", "&lt; stays escaped once")]
    [InlineData("&#176; and &#x2014;", "\u00b0 and \u2014")]
    [InlineData("bare & ampersand", "bare & ampersand")]
    [InlineData("&notanentity; kept", "&notanentity; kept")]
    public void FullEntityDecoderResolvesTheWholeNamedTable(string input, string expected) =>
        Assert.Equal(expected, HtmlWalker.DecodeEntitiesFull(input));

    /// <summary>
    /// The structure walker's decoder stays small on purpose — it ports a Rust function that
    /// knows only a few dozen names, and widening it would diverge from the reference.
    /// </summary>
    [Fact]
    public void StructureDecoderKeepsUnknownNamesVerbatim()
    {
        Assert.Equal("46.84&deg;N", HtmlWalker.DecodeEntities("46.84&deg;N"));
        Assert.Equal("a & b", HtmlWalker.DecodeEntities("a &amp; b"));
    }
}
