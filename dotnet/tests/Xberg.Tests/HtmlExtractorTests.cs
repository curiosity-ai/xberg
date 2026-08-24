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

    // ── html5ever repair: leading document whitespace ────────────────────────
    [Fact]
    public void Html_LeadingCommentSwallowsTheWhitespaceAfterIt()
    {
        // The HTML5 tree builder ignores whitespace before the first content, so the text that
        // follows an opening comment starts at its first non-whitespace character.
        Assert.Equal("## Heading\n\nbody\n", HtmlToMarkdown.Convert("<!-- image -->\n\n## Heading\n\nbody\n"));
        Assert.Equal("AA\n", HtmlToMarkdown.Convert("\n\n<!-- c -->\n\nAA\n"));
        Assert.Equal("AA\n", HtmlToMarkdown.Convert("  <!-- c -->  AA\n"));
    }

    [Fact]
    public void Html_LeadingWhitespaceGoesWithAnyRepairedDocument()
    {
        // A custom element takes the same repair, and a comment anywhere in the document is
        // enough to trigger it — the whitespace at the top goes either way.
        Assert.Equal("y AA\n", HtmlToMarkdown.Convert("\n\n<my-tag>y</my-tag>\n\nAA\n"));
        Assert.Equal("x\n\nAA\n", HtmlToMarkdown.Convert("\n\n<p>x</p>\n\nAA <!-- c -->\n"));
    }

    [Fact]
    public void Html_WhitespaceAfterTheFirstContentIsUntouched()
    {
        // Only the whitespace before the first content is dropped; once the body is open the
        // tree builder keeps character data as it comes.
        Assert.Equal("x\n\nAA\n", HtmlToMarkdown.Convert("<div>x</div>\n\n<!-- c -->\n\nAA\n"));
        Assert.Equal("x\n\nAA\n", HtmlToMarkdown.Convert("<!-- c -->\n\n<div>x</div>\n\nAA\n"));
    }

    // ── astral-tl tokenizer shape (parser/base.rs) ───────────────────────────
    [Fact]
    public void Html_QuoteInAnAttributeNameDoesNotSwallowTheTag()
    {
        // A quote opens an attribute VALUE only right after an attribute name's `=`. Elsewhere
        // — here in the name `y"` — it is an ordinary character, so the tag still ends at its
        // own `>` instead of running to the next quote in the document.
        Assert.Equal("A **z** C\n", HtmlToMarkdown.Convert("A <b x=\"1\" y\">z</b> C"));

        // The PDF shape this was found on: an XML listing whose attribute names carry stray
        // quotes. Treating each quote as a delimiter hid the tag's `>` and lost every line
        // between it and the next quote.
        Assert.Equal("A KEEPME\nEND\n",
            HtmlToMarkdown.Convert("A <KSHIM NAME=\"a\" B}\">\nKEEPME\nEND"));
    }

    [Fact]
    public void Html_ProcessingInstructionIsCharacterDataNotMarkup()
    {
        // `parse_tag` steps over the `<`, finds no identifier after it and gives up, leaving the
        // rest of the instruction to be read as text rather than skipped to the next `>`.
        Assert.Equal("?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\nHello\n",
            HtmlToMarkdown.Convert("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                + "<html><body><p>Hello</p></body></html>"));
    }

    [Fact]
    public void Html_StartTagNameEndsAtTheFirstNonIdentifierCharacter()
    {
        // `read_ident` stops at `.`, so this is a `p` element with junk attributes — and it
        // still breaks the paragraph. Splitting the name on whitespace instead would name the
        // element `p."x"`, which matches nothing and emits no break.
        Assert.Equal("head\n\ntail\n", HtmlToMarkdown.Convert("head <p.\"x\"> tail"));
    }

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

    /// <summary>
    /// Old hand-written pages write cells straight into the table. Without the row the HTML5
    /// insertion modes imply, every cell hangs off the table where no consumer looks for it —
    /// on one regression fixture that table holds 99% of the document.
    /// </summary>
    [Fact]
    public void ACellWithNoRowGetsOne()
    {
        string md = HtmlToMarkdown.Convert(
            "<html><body><p>before</p><table border=1>" +
            "<td>cell one</td><td>cell two</td></table><p>after</p></body></html>");

        Assert.Contains("| cell one | cell two |", md);
        Assert.Contains("after", md);
    }

    /// <summary>A cell left unclosed ends where the next one starts, not inside it.</summary>
    [Fact]
    public void UnclosedCellsAndRowsCloseEachOther()
    {
        string md = HtmlToMarkdown.Convert(
            "<table><tr><td>a<td>b<tr><td>c<td>d</table>");

        Assert.Contains("| a | b |", md);
        Assert.Contains("| c | d |", md);
    }

    private static HtmlMetadata Meta(string html) => HtmlMeta.Extract(html);

    /// <summary>
    /// A `</style` closed with whitespace before its bracket is still a close tag. Matching the
    /// literal `</style>` swallowed the rest of the document — on one Wikipedia fixture that was
    /// four of its five headings and every link after them.
    /// </summary>
    [Fact]
    public void ARawTextElementClosesOnWhitespaceBeforeTheBracket()
    {
        var m = Meta("<html><head><style>.a { color: red; }</style\n><title>T</title></head>" +
                     "<body><h2 id=\"one\">One</h2></body></html>");

        Assert.Equal("T", m.Title);
        Assert.Single(m.Headers);
    }

    /// <summary>
    /// `<template>` holds an inert document fragment and `<noscript>` only renders with
    /// scripting disabled, which a Markdown conversion never is. The converter skips both
    /// subtrees, so nothing inside them belongs to the document — including the images and links
    /// this pass collects. Wikipedia ships a 1×1 tracking pixel in `<noscript>` on every page,
    /// which is what this was over-counting.
    /// </summary>
    [Fact]
    public void ImagesAndLinksInsideInertSubtreesAreNotCollected()
    {
        var m = Meta("<html><body>" +
                     "<img src=\"real.png\" alt=\"real\">" +
                     "<a href=\"/real\">real</a>" +
                     "<noscript><img src=\"pixel.gif\" alt=\"\"><a href=\"/tracked\">t</a></noscript>" +
                     "<template><img src=\"tpl.png\"><a href=\"/tpl\">x</a></template>" +
                     "</body></html>");

        // The collected entries are private records, so they are checked through the shape they
        // serialize to — which is also what reaches the golden.
        string images = System.Text.Json.JsonSerializer.Serialize(m.Images);
        string links = System.Text.Json.JsonSerializer.Serialize(m.Links);
        Assert.Single(m.Images);
        Assert.Single(m.Links);
        Assert.Contains("real.png", images);
        Assert.DoesNotContain("pixel.gif", images);
        Assert.DoesNotContain("tpl.png", images);
        Assert.Contains("/real", links);
        Assert.DoesNotContain("/tracked", links);
        Assert.DoesNotContain("/tpl", links);
    }

    /// <summary>
    /// Preprocessing removes navigation and form subtrees before anything is collected, so a
    /// sidebar's heading is not one of the document's headings.
    /// </summary>
    [Fact]
    public void HeadingsInsideNavigationChromeAreNotDocumentHeadings()
    {
        var m = Meta("<html><body>" +
                     "<nav class=\"toc\"><h2>Contents</h2></nav>" +
                     "<h1 id=\"main\">Real heading</h1>" +
                     "<form><h3>Search</h3></form>" +
                     "</body></html>");

        Assert.Single(m.Headers);
    }

    /// <summary>`lang` and `dir` are read from whichever of html/head/body carries them.</summary>
    [Theory]
    [InlineData("<html lang=\"he\" dir=\"rtl\"><body>x</body></html>", "he", TextDirection.RightToLeft)]
    [InlineData("<html><body lang=\"fr\" dir=\"ltr\">x</body></html>", "fr", TextDirection.LeftToRight)]
    [InlineData("<html><body dir=\"sideways\">x</body></html>", null, null)]
    public void DocumentLanguageAndDirectionComeFromTheFirstElementThatDeclaresThem(
        string html, string? language, TextDirection? direction)
    {
        var m = Meta(html);
        Assert.Equal(language, m.Language);
        Assert.Equal(direction, m.TextDirection);
    }

    /// <summary>
    /// An image records its size only when the element states both halves of it, keeps the source
    /// as written, and treats a protocol-relative URL as relative — it inherits the page's scheme
    /// rather than naming one.
    /// </summary>
    [Fact]
    public void ImageMetadataRecordsSizeSourceAndKind()
    {
        var m = Meta("<html><body>" +
                     "<img src=\"//cdn.example/a.png?x=1&amp;y=2\" alt=\"A\" width=\"20\" height=\"10\">" +
                     "<img src=\"b.png\" alt=\"\" width=\"20\">" +
                     "</body></html>");

        string json = System.Text.Json.JsonSerializer.Serialize(m.Images, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Assert.Contains("\"src\":\"//cdn.example/a.png?x=1&amp;y=2\"", json);
        Assert.Contains("\"dimensions\":[20,10]", json);
        Assert.Contains("\"image_type\":\"relative\"", json);
        // The second image states only a width, and its alt is empty: neither is recorded.
        Assert.DoesNotContain("\"alt\":\"\"", json);
        Assert.Equal(2, m.Images.Count);
    }

    /// <summary>
    /// Head metadata is read from the children of `<head>`. A `<title>` written before the head
    /// — as some Federal Register pages are — is not one of them.
    /// </summary>
    [Fact]
    public void OnlyTheHeadsOwnTitleCounts()
    {
        Assert.Null(Meta("<HTML>\n<TITLE>Stray</TITLE>\n<HEAD></HEAD>\n<BODY>x</BODY></HTML>").Title);
        Assert.Equal("Real", Meta("<html><head><title>Real</title></head><body>x</body></html>").Title);
    }

    /// <summary>
    /// A title is trimmed but not collapsed: one written with two spaces between its halves
    /// keeps them. Its references are read as written — only a document that reaches the walk
    /// through the html5ever repair arrives with them resolved, and there the serializer's own
    /// spelling is what is recorded, so `&amp;mdash;` becomes an em dash while `&amp;nbsp;`
    /// stays as it was written.
    /// </summary>
    [Fact]
    public void ATitleKeepsItsInternalSpacingAndItsReferencesAsWritten()
    {
        Assert.Equal(
            "Understanding Output &mdash; aequitas  documentation",
            Meta("<html><head><title>\n  Understanding Output &mdash; aequitas  documentation\n</title></head></html>").Title);

        // A block misnested under an inline ancestor is one of the conditions that sends a
        // document through the repair.
        Assert.Equal(
            "A\u00b0B \u2014 C&nbsp;D",
            Meta("<html><head><title>A&deg;B &mdash; C&nbsp;D</title></head>"
                 + "<body><p><em><div>b</div></em></p></body></html>").Title);
    }

    /// <summary>
    /// A `<pre>` block is emitted as raw text, so nothing inside it is visited as an element —
    /// a link written inside preformatted text is not one of the document's links.
    /// </summary>
    [Fact]
    public void PreformattedTextContributesNoLinks()
    {
        var m = Meta("<html><body><pre>see <a href=\"http://example.com\">here</a></pre>" +
                     "<p><a href=\"/real\">real</a></p></body></html>");

        Assert.Single(m.Links);
    }

    /// <summary>
    /// Dublin Core carries the author on `DC.Publisher` as often as on `author`, and the
    /// collector accepts creator, contributor and publisher for it — a page that names none of
    /// the plain ones still has an author.
    /// </summary>
    [Fact]
    public void DublinCoreFieldsFeedTheDocumentFields()
    {
        var m = Meta("<html><head>" +
                     "<META NAME=\"DC.Title\" CONTENT=\"School Detail\">" +
                     "<META NAME=\"DC.Publisher\" CONTENT=\"National Center for Education Statistics\">" +
                     "<META NAME=\"DC.Subject\" CONTENT=\"schools, statistics\">" +
                     "<META NAME=\"DC.Language\" CONTENT=\"EN\">" +
                     "</head></html>");

        Assert.Equal("School Detail", m.Title);
        Assert.Equal("National Center for Education Statistics", m.Author);
        Assert.Equal(new[] { "schools", "statistics" }, m.Keywords);
        // A Dublin Core field with no document field of its own is kept as a meta tag.
        Assert.Equal("EN", m.MetaTags["dc_language"]);
    }

    /// <summary>
    /// Open Graph and Twitter Card properties are keyed by their suffix with hyphens folded to
    /// underscores, and an unrecognised name is kept as spelled.
    /// </summary>
    [Fact]
    public void SocialCardPropertiesAreKeyedBySuffix()
    {
        var m = Meta("<html><head>" +
                     "<meta property=\"og:site_name\" content=\"Example\">" +
                     "<meta name=\"twitter:card\" content=\"summary\">" +
                     "<meta name=\"robots\" content=\"ALL\">" +
                     "</head></html>");

        Assert.Equal("Example", m.OpenGraph["site_name"]);
        Assert.Equal("summary", m.TwitterCard["card"]);
        Assert.Equal("ALL", m.MetaTags["robots"]);
    }

    /// <summary>
    /// A heading's recorded text is the markdown its children convert to, so a permalink anchor
    /// inside one is `[label](href "title")` and emphasis keeps its markers. An anchor with no
    /// href is a link target, not a link, and contributes only its text.
    /// </summary>
    [Fact]
    public void AHeadingRecordsTheMarkdownOfItsChildren()
    {
        var m = Meta("<html><body>" +
                     "<h1>Understanding Output<a href=\"#out\" title=\"Permalink\">\u00b6</a></h1>" +
                     "<h2><a id=\"anchor\"></a>Functions</h2>" +
                     "<h3>The <em>quick</em> fox</h3>" +
                     "</body></html>");

        string json = System.Text.Json.JsonSerializer.Serialize(m.Headers, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        Assert.Contains("Understanding Output[\u00b6](#out \\\"Permalink\\\")", json);
        Assert.Contains("\"text\":\"Functions\"", json);
        Assert.Contains("The *quick* fox", json);
    }

    // \u2500\u2500 conversion rules ported from html-to-markdown-rs \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    /// <summary>
    /// A `&lt;pre&gt;` is dedented unconditionally, and a whitespace-only line keeps its spaces:
    /// the markdown has its line ends trimmed globally afterwards, but the Code element does not.
    /// </summary>
    [Fact]
    public void PreDedentsAlwaysAndKeepsBlankLineWhitespace()
    {
        var doc = Html("<html><body><pre><code>\n            aaa\n            bbb\n        </code></pre></body></html>");
        var code = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Code));
        Assert.Equal("aaa\nbbb\n        ", code.Text);
    }

    /// <summary>
    /// `&lt;cite&gt;` has an italicizing handler upstream that nothing dispatches to, so a
    /// citation keeps its own inline markup and gains none.
    /// </summary>
    [Fact]
    public void CiteAddsNoEmphasis()
    {
        Assert.Equal("x Y z\n", HtmlToMarkdown.Convert("<html><body><p>x <cite>Y</cite> z</p></body></html>"));
    }

    /// <summary>An abbreviation spells itself out: its text, then its title in parentheses.</summary>
    [Fact]
    public void AbbrAppendsItsTitle()
    {
        Assert.Equal("RA: (Right Ascension) +3\n",
            HtmlToMarkdown.Convert("<html><body><p><abbr title=\"Right Ascension\">RA:</abbr> +3</p></body></html>"));
    }

    /// <summary>
    /// A `&lt;button&gt;` renders its children and adds a block separator; being a form control
    /// removes neither the text nor the images inside it.
    /// </summary>
    [Fact]
    public void ButtonRendersItsContent()
    {
        Assert.Equal("Close\n\nafter\n",
            HtmlToMarkdown.Convert("<html><body><button type=\"button\">Close</button><p>after</p></body></html>"));
    }

    /// <summary>A `&lt;legend&gt;` is bold; a selected `&lt;option&gt;` is bulleted.</summary>
    [Fact]
    public void FieldsetLegendIsBoldAndSelectedOptionIsBulleted()
    {
        string md = HtmlToMarkdown.Convert(
            "<html><body><fieldset><legend>Colours</legend>" +
            "<select><option>red</option><option selected>green</option></select>" +
            "</fieldset></body></html>");
        Assert.Contains("**Colours**", md);
        Assert.Contains("* green", md);
    }

    /// <summary>
    /// A media element markdown cannot embed becomes a link to its source plus its fallback
    /// content; a `&lt;picture&gt;` reduces to the first `&lt;img&gt;` it holds.
    /// </summary>
    [Fact]
    public void VideoLinksItsSourceAndPictureReducesToItsImage()
    {
        string md = HtmlToMarkdown.Convert(
            "<html><body><video controls=\"controls\"><source src=\"/v.mp4\" type=\"video/mp4\"/>" +
            "<p>no video</p></video></body></html>");
        Assert.Contains("[/v.mp4](/v.mp4)", md);
        Assert.Contains("no video", md);

        Assert.Equal("![logo](/l.svg)\n",
            HtmlToMarkdown.Convert("<html><body><picture><source srcset=\"/big.svg\"/>" +
                                   "<img src=\"/l.svg\" alt=\"logo\"/></picture></body></html>"));
    }

    /// <summary>
    /// An inline `&lt;svg&gt;` becomes an image whose source is the serialized subtree as a
    /// base64 data URI, with attributes sorted and their canonical camelCase restored.
    /// </summary>
    [Fact]
    public void InlineSvgBecomesABase64DataUri()
    {
        string md = HtmlToMarkdown.Convert(
            "<html><body><svg width=\"10\" viewBox=\"0 0 1 1\" height=\"10\"><path d=\"M 0 0\"/></svg></body></html>");
        Assert.StartsWith("![SVG Image](data:image/svg+xml;base64,", md);
        int start = md.IndexOf("base64,", StringComparison.Ordinal) + "base64,".Length;
        int end = md.IndexOf(')', start);
        string svg = Encoding.UTF8.GetString(Convert.FromBase64String(md[start..end]));
        Assert.Equal("<svg height=\"10\" viewBox=\"0 0 1 1\" width=\"10\"><path d=\"M 0 0\" /></svg>", svg);
    }

    /// <summary>
    /// A `&lt;table&gt;&lt;caption&gt;` is recovered as a paragraph immediately before its table:
    /// the converter's grid carries only cells, so the caption is otherwise lost.
    /// </summary>
    [Fact]
    public void TableCaptionBecomesAParagraphBeforeItsTable()
    {
        var doc = Html("<html><body><table><caption>Basic <b>duck</b> facts</caption>" +
                       "<tr><th>Name</th></tr><tr><td>Mallard</td></tr></table></body></html>");
        int caption = doc.Elements.FindIndex(e => e.Text == "Basic duck facts");
        int table = doc.Elements.FindIndex(e => e.Kind.Tag == ElementKindTag.Table);
        Assert.True(caption >= 0 && table == caption + 1);
    }

    /// <summary>
    /// An autolink returns from the link handler before the metadata collector runs, so a link
    /// whose visible text is its own href is in the output and not in `links`.
    /// </summary>
    [Fact]
    public void AnAutolinkIsNotCollectedAsALink()
    {
        var m = Meta("<html><body><p><a href=\"https://e.example/x\">https://e.example/x</a> " +
                     "<a href=\"https://e.example/y\">Y</a></p></body></html>");
        string json = System.Text.Json.JsonSerializer.Serialize(m.Links);
        Assert.DoesNotContain("https://e.example/x", json);
        Assert.Contains("https://e.example/y", json);
    }

    /// <summary>
    /// A permalink anchor inside a heading is still a link: upstream collects it from the
    /// ordinary `&lt;a&gt;` handler, which the heading path does not bypass.
    /// </summary>
    [Fact]
    public void AHeadingsPermalinkAnchorIsAlsoCollectedAsALink()
    {
        var m = Meta("<html><body><h2>Setup<a href=\"#setup\" class=\"hash-link\">\u200b</a></h2></body></html>");
        string json = System.Text.Json.JsonSerializer.Serialize(m.Links);
        Assert.Contains("#setup", json);
        Assert.Contains("hash-link", json);
    }

    /// <summary>
    /// Asking for plain output swaps the conversion text for the plain-text walker's, which is
    /// what reaches the document when the page yields no structured blocks at all.
    /// </summary>
    [Fact]
    public void PlainOutputFallsBackToThePlainTextWalker()
    {
        const string html = "<html><body><a href=\"/start.html\"><div>This is some text.</div></a></body></html>";
        var plain = new HtmlExtractor().Extract(
            Encoding.UTF8.GetBytes(html), "text/html", new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        var markdown = new HtmlExtractor().Extract(
            Encoding.UTF8.GetBytes(html), "text/html", new ExtractionConfig { OutputFormat = OutputFormat.Markdown });

        Assert.Equal("This is some text.\n", Assert.Single(plain.Elements).Text);
        Assert.Equal("[This is some text.](/start.html)\n", Assert.Single(markdown.Elements).Text);
    }
    /// <summary>The recorded links, serialized the way the harness compares them.</summary>
    private static string LinksJson(HtmlMetadata m) =>
        System.Text.Json.JsonSerializer.Serialize(m.Links, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// `<template>` and `<noscript>` have no handler of their own: they reach the unknown
    /// handler, which renders their children. A no-JS fallback image is often a page's only
    /// copy of that image.
    /// </summary>
    [Fact]
    public void TemplateAndNoscriptContentIsDropped()
    {
        Assert.Equal("before\n\nafter\n", HtmlToMarkdown.Convert(
            "<p>before</p><noscript><img src=\"/y.png\" alt=\"\"></noscript><p>after</p>"));
        Assert.Equal("a\n\nb\n", HtmlToMarkdown.Convert(
            "<p>a</p><template><p>inside</p></template><p>b</p>"));
    }

    /// <summary>
    /// A `<title>` written in the body is content, not head metadata, and renders as its text.
    /// </summary>
    [Fact]
    public void ATitleOutsideTheHeadIsRendered()
        => Assert.Equal("a\n\nStray\n\nb\n", HtmlToMarkdown.Convert("<body><p>a</p><title>Stray</title><p>b</p></body>"));

    /// <summary>
    /// `strip_hidden_elements` walks name=value pairs rather than scanning the tag's text for
    /// the word `hidden`, so a quoted value containing it no longer takes the whole visible
    /// element with it. `data-hidden` and `aria-hidden` are different names and never matched.
    /// </summary>
    [Fact]
    public void OnlyTheHiddenAttributeItselfStripsTheElement()
    {
        Assert.Equal("kept\n", HtmlToMarkdown.Convert(
            "<p>kept</p><p hidden>gone</p>"));
        Assert.Equal("kept\n\nstays\n", HtmlToMarkdown.Convert(
            "<p>kept</p><p title=\"pages with hidden wikidata\">stays</p>"));
        Assert.Equal("kept\n\nstays\n", HtmlToMarkdown.Convert(
            "<p>kept</p><p data-hidden=\"1\" aria-hidden=\"true\">stays</p>"));
    }

    /// <summary>
    /// An inline `style` that hides the element strips it too. The last declaration for a
    /// property wins, so `display:none; display:block` is visible, and a comment before the
    /// property name does not defeat the check.
    /// </summary>
    [Fact]
    public void AnInlineStyleThatHidesTheElementStripsIt()
    {
        Assert.Equal("kept\n", HtmlToMarkdown.Convert(
            "<p>kept</p><div style=\"display: none\">gone</div>"));
        Assert.Equal("kept\n", HtmlToMarkdown.Convert(
            "<p>kept</p><div style=\"visibility:hidden\">gone</div>"));
        Assert.Equal("kept\n", HtmlToMarkdown.Convert(
            "<p>kept</p><div style=\"/* note */ display:none\">gone</div>"));
        Assert.Equal("kept\n\nshown\n", HtmlToMarkdown.Convert(
            "<p>kept</p><div style=\"display:none; display:block\">shown</div>"));
    }

    /// <summary>
    /// A stripped `<style>` or `<script>` leaves a space behind when it stood between two
    /// non-space characters — that space is what keeps the words around it apart.
    /// </summary>
    [Fact]
    public void AStrippedStyleLeavesASpaceBetweenTheWordsAroundIt()
    {
        Assert.Equal("a b\n", HtmlToMarkdown.Convert("<p><span>a</span><style>i{}</style><span>b</span></p>"));
        Assert.Equal("a b\n", HtmlToMarkdown.Convert("<p><span>a</span> <style>i{}</style> <span>b</span></p>"));
    }

    /// <summary>
    /// A `<br>` inside a table cell collapses to a single space: a GFM cell cannot hold a hard
    /// break, so neither newline style is emitted there and the source whitespace around the
    /// break is trimmed rather than leaked.
    /// </summary>
    [Fact]
    public void ABrInATableCellCollapsesToASpace()
    {
        var doc = Html("<table><tr><td><span>A</span><br /><span>B</span></td></tr></table>");
        Assert.Equal(new List<string> { "A B" }, Assert.Single(doc.Tables).Cells[0]);
    }

    /// <summary>
    /// A document whose block elements are misnested under inline ones — or that names a custom
    /// element — is re-parsed through html5ever, whose serializer resolves the character
    /// references in every attribute and writes back the canonical named forms.
    /// </summary>
    [Fact]
    public void AttributesAreCanonicalizedWhenTheDocumentNeedsRepair()
    {
        const string link = "<a href=\"/w\" title=\"Finsch&#39;s &amp;C\">dk</a> <a href=\"/x\" title='\"Q!\"'>q</a>";

        Assert.Equal("[dk](/w \"Finsch&#39;s &amp;C\") [q](/x \"\\\"Q!\\\"\")\n",
            HtmlToMarkdown.Convert("<p>" + link + "</p>"));
        Assert.Equal("[dk](/w \"Finsch's &amp;C\") [q](/x \"&quot;Q!&quot;\")\n\nblock\n",
            HtmlToMarkdown.Convert("<p>" + link + "</p><span><div>block</div></span>"));
        Assert.Equal("[dk](/w \"Finsch's &amp;C\") [q](/x \"&quot;Q!&quot;\")\n\nx\n",
            HtmlToMarkdown.Convert("<p>" + link + "</p><my-el>x</my-el>"));
    }

    /// <summary>A table ends with a single newline, not a blank line (`block/table/mod.rs`).</summary>
    [Fact]
    public void ATableEndsWithOneNewline()
        => Assert.Equal("| a |\n| --- |\n| b |\nafter\n",
            HtmlToMarkdown.Convert("<table><tr><th>a</th></tr><tr><td>b</td></tr></table>after"));

    /// <summary>
    /// The link handler rewrites a caret-only fragment label — Wikipedia's citation backlink —
    /// to an arrow before the metadata collector sees it, so the recorded text carries it too.
    /// </summary>
    [Fact]
    public void ACiteBacklinkRecordsTheArrowItRendersAs()
    {
        var m = Meta("<html><body><li><a href=\"#cite_ref-2\">^</a></li></body></html>");
        Assert.Contains("\"text\":\"\u2191\"", LinksJson(m));
    }

    /// <summary>
    /// A link's recorded text is the markdown its children produce: an abbreviation expands to
    /// `text (title)`, a `<br>` collapses to a space, and an anchor that renders to nothing
    /// falls back to its own href.
    /// </summary>
    [Fact]
    public void ALinkRecordsTheMarkdownItsChildrenProduce()
    {
        var m = Meta("<html><body>"
            + "<a href=\"/t\" title=\"T\"><abbr title=\"View this template\">v</abbr></a>"
            + "<a href=\"/b\">The Tortured Poets<br />Department</a>"
            + "<a href=\"#\"><span class=\"icon\"></span></a>"
            + "<a name=\"target\">not a link</a>"
            + "</body></html>");

        string json = LinksJson(m);
        Assert.Contains("\"text\":\"v (View this template)\"", json);
        Assert.Contains("\"text\":\"The Tortured Poets Department\"", json);
        Assert.Contains("\"text\":\"#\"", json);
        // The `name`-only anchor is a link target, not a link.
        Assert.Equal(3, m.Links.Count);
    }

    /// <summary>
    /// A table's links are recorded once, however many times the handler walks its cells. The
    /// width pre-pass and the grid walk both run with the collectors detached — the first
    /// because a column measurement is an internal detail, the second because the render has
    /// already recorded the same cells — so the render is the only pass that records, and a
    /// nested table is recorded once too rather than once per pass of its parent.
    /// </summary>
    [Fact]
    public void ATablesLinksAreRecordedOnceHoweverManyPassesWalkIt()
    {
        static int Count(string html, string href)
        {
            string json = LinksJson(Meta(html));
            string needle = "\"href\":\"" + href + "\"";
            int n = 0;
            for (int i = json.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = json.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        const string simple = "<table><tr><td><a href=\"/a\">a</a></td></tr></table>";
        Assert.Equal(1, Count(simple, "/a"));

        // Rows of differing width read as a layout table, which has no width pre-pass. The
        // nested table's link is recorded once as well.
        const string layout = "<table><tr><td><a href=\"/a\">a</a></td>"
            + "<td><table><tr><td><a href=\"/b\">b</a></td></tr></table></td></tr></table>";
        Assert.Equal(1, Count(layout, "/a"));
        Assert.Equal(1, Count(layout, "/b"));

        const string caption = "<table><caption><a href=\"/c\">c</a></caption>"
            + "<tr><td><a href=\"/a\">a</a></td></tr></table>";
        Assert.Equal(1, Count(caption, "/c"));
        Assert.Equal(1, Count(caption, "/a"));
    }

    // ── tag-open state and attribute recovery ───────────────────────────────
    /// <summary>
    /// `&lt;/` and `&lt;!` still need a name after them. A page cut mid-tag writes `&lt;/=` and
    /// the text has to survive: read as markup it swallows everything up to the next `&gt;`.
    /// </summary>
    [Fact]
    public void Html_AnEndTagWithNoNameIsCharacterData()
    {
        Assert.Equal("B</=\no:p>\n", HtmlToMarkdown.Convert("<p>B<o:p></=\no:p></p>"));
        Assert.Equal("a <! b\n", HtmlToMarkdown.Convert("<p>a &lt;! b</p>"));
    }

    /// <summary>
    /// A stray `=` between attributes is skipped one character at a time; it does not adopt the
    /// next attribute as its value, so the `href` that follows it is still found.
    /// </summary>
    [Fact]
    public void Html_AStrayEqualsBeforeAnAttributeDoesNotSwallowIt()
    {
        Assert.Equal("[x](y)\n", HtmlToMarkdown.Convert("<p><a =\nhref=y>x</a></p>"));
        Assert.Equal("[x](y)\n", HtmlToMarkdown.Convert("<p><a href=\"y\">x</a></p>"));
    }

    /// <summary>
    /// The serialized MathML in the comment carries resolved character references, but the four
    /// characters that would change the markup's own shape go back out as references.
    /// </summary>
    [Fact]
    public void Html_SerializedMathReEscapesTheStructuralCharacters()
    {
        string md = HtmlToMarkdown.Convert(
            "<p><math><mo>&#x3E;</mo><mo>&amp;</mo><mo>&#x222B;</mo></math></p>");
        Assert.Contains("<mo>&gt;</mo><mo>&amp;</mo><mo>∫</mo>", md);
    }
}
