using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the OOXML office extractors (<see cref="DocxExtractor"/>, <see cref="PptxExtractor"/>,
/// <see cref="XlsxExtractor"/>) using minimal in-memory packages.
/// </summary>
public class OoxmlTests
{
    private static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }

    private static string Render(InternalDocument doc, OutputFormat fmt) =>
        Derive.DeriveExtractionResult(doc, includeDocumentStructure: false, fmt).Content;

    // ── MIME advertisement ─────────────────────────────────────────────────────
    [Fact]
    public void Extractors_AdvertiseExpectedMimeTypes()
    {
        Assert.Contains("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            new DocxExtractor().SupportedMimeTypes);
        Assert.Contains("application/vnd.openxmlformats-officedocument.presentationml.presentation",
            new PptxExtractor().SupportedMimeTypes);
        Assert.Contains("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            new XlsxExtractor().SupportedMimeTypes);
    }

    // ── XLSX ────────────────────────────────────────────────────────────────────
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static byte[] MinimalXlsx() => Zip(
        ("xl/workbook.xml",
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
        ("xl/sharedStrings.xml",
            "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<si><t>Name</t></si><si><t>Alice</t></si></sst>"),
        ("xl/worksheets/sheet1.xml",
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>42</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>7.5</v></c></row>" +
            "</sheetData></worksheet>"));

    [Fact]
    public void Xlsx_ExtractsSheetAsTableWithHeadingAndMetadata()
    {
        var doc = new XlsxExtractor().Extract(MinimalXlsx(), XlsxMime, new ExtractionConfig());

        Assert.Single(doc.Tables);
        var cells = doc.Tables[0].Cells;
        Assert.Equal(new List<string> { "Name", "42" }, cells[0]);
        Assert.Equal(new List<string> { "Alice", "7.5" }, cells[1]);

        var excel = Assert.IsType<ExcelMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(1u, excel.SheetCount);
        Assert.Equal(new List<string> { "Data" }, excel.SheetNames);
        Assert.Equal("excel", doc.Metadata.Format.FormatType);
    }

    [Fact]
    public void Xlsx_WholeNumberFloatsHaveNoTrailingDecimal()
    {
        // Cell B1 = 42 (not "42.0"); matches calamine/Rust `format_cell_to_string`.
        var doc = new XlsxExtractor().Extract(MinimalXlsx(), XlsxMime, new ExtractionConfig());
        Assert.Equal("42", doc.Tables[0].Cells[0][1]);
    }

    // ── PPTX ────────────────────────────────────────────────────────────────────
    private const string PptxMime = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    private static string SlideXml(string title, string body) =>
        "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
        "<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
        "<p:txBody><a:p><a:r><a:t>" + title + "</a:t></a:r></a:p></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:nvPr/></p:nvSpPr>" +
        "<p:txBody><a:p><a:r><a:t>" + body + "</a:t></a:r></a:p></p:txBody></p:sp>" +
        "</p:spTree></p:cSld></p:sld>";

    private static byte[] MinimalPptx() => Zip(
        ("ppt/_rels/presentation.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
        ("ppt/slides/slide1.xml", SlideXml("My Title", "Body text")));

    /// <summary>
    /// `TitlesOfParts` is one flat vector concatenating several groups; `HeadingPairs` says how
    /// many entries each group owns. Taking the vector whole puts font and theme names into the
    /// slide titles.
    /// </summary>
    [Fact]
    public void Pptx_SlideTitlesAreSlicedOutOfTitlesOfParts()
    {
        var pptx = Zip(
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", SlideXml("My Title", "Body text")),
            ("docProps/app.xml",
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" " +
                "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                "<HeadingPairs><vt:vector size=\"6\" baseType=\"variant\">" +
                "<vt:variant><vt:lpstr>Fonts Used</vt:lpstr></vt:variant><vt:variant><vt:i4>2</vt:i4></vt:variant>" +
                "<vt:variant><vt:lpstr>Theme</vt:lpstr></vt:variant><vt:variant><vt:i4>1</vt:i4></vt:variant>" +
                "<vt:variant><vt:lpstr>Slide Titles</vt:lpstr></vt:variant><vt:variant><vt:i4>2</vt:i4></vt:variant>" +
                "</vt:vector></HeadingPairs>" +
                "<TitlesOfParts><vt:vector size=\"5\" baseType=\"lpstr\">" +
                "<vt:lpstr>Calibri</vt:lpstr><vt:lpstr>Arial</vt:lpstr><vt:lpstr>Office Theme</vt:lpstr>" +
                "<vt:lpstr>First Slide</vt:lpstr><vt:lpstr>Second Slide</vt:lpstr>" +
                "</vt:vector></TitlesOfParts></Properties>"));

        var doc = new PptxExtractor().Extract(pptx, PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        Assert.Equal("First Slide, Second Slide", doc.Metadata.Additional["slide_titles"].GetString());
        Assert.Equal(new List<string> { "First Slide", "Second Slide" }, SlideNamesOf(doc));
    }

    /// <summary>With no heading pairs there is nothing to slice by, so the vector stands as-is.</summary>
    [Fact]
    public void Pptx_SlideTitlesFallBackToTheWholeVectorWithoutHeadingPairs()
    {
        var pptx = Zip(
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", SlideXml("My Title", "Body text")),
            ("docProps/app.xml",
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" " +
                "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                "<TitlesOfParts><vt:vector size=\"2\" baseType=\"lpstr\">" +
                "<vt:lpstr>Only Slide</vt:lpstr><vt:lpstr></vt:lpstr></vt:vector></TitlesOfParts></Properties>"));

        var doc = new PptxExtractor().Extract(pptx, PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        // The trailing empty entry is dropped, but only after slicing.
        Assert.Equal(new List<string> { "Only Slide" }, SlideNamesOf(doc));
    }

    private static List<string> SlideNamesOf(InternalDocument doc) =>
        Assert.IsType<PptxMetadata>(doc.Metadata.Format?.Payload).SlideNames;

    /// <summary>
    /// A chart or SmartArt frame carries only a relationship id; its text lives in another part
    /// of the package and is lost entirely unless that part is followed and read.
    /// </summary>
    [Fact]
    public void Pptx_ChartAndSmartArtTextIsResolvedFromTheirOwnParts()
    {
        const string slide =
            "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree>" +
            "<p:graphicFrame><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
            "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId2\"/>" +
            "</a:graphicData></a:graphic></p:graphicFrame>" +
            "<p:graphicFrame><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\">" +
            "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rId3\"/>" +
            "</a:graphicData></a:graphic></p:graphicFrame>" +
            "</p:spTree></p:cSld></p:sld>";

        var pptx = Zip(
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", slide),
            ("ppt/slides/_rels/slide1.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart\" Target=\"../charts/chart1.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData\" Target=\"../diagrams/data1.xml\"/></Relationships>"),
            ("ppt/charts/chart1.xml",
                "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
                "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart>" +
                "<c:title><c:tx><c:rich><a:p><a:r><a:t>Revenue by Quarter</a:t></a:r></a:p></c:rich></c:tx></c:title>" +
                "<c:plotArea><c:barChart><c:ser>" +
                "<c:cat><c:strRef><c:strCache><c:pt idx=\"0\"><c:v>Q1</c:v></c:pt></c:strCache></c:strRef></c:cat>" +
                "<c:val><c:numRef><c:numCache><c:pt idx=\"0\"><c:v>42</c:v></c:pt></c:numCache></c:numRef></c:val>" +
                "</c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>"),
            ("ppt/diagrams/data1.xml",
                "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" " +
                "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><dgm:ptLst>" +
                "<dgm:pt modelId=\"1\" type=\"node\"><dgm:t><a:p><a:r><a:t>Step One</a:t></a:r></a:p></dgm:t></dgm:pt>" +
                "<dgm:pt modelId=\"2\" type=\"node\"><dgm:t><a:p><a:r><a:t>Step Two</a:t></a:r></a:p></dgm:t></dgm:pt>" +
                "</dgm:ptLst></dgm:dataModel>"));

        var doc = new PptxExtractor().Extract(pptx, PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        var plain = Render(doc, OutputFormat.Plain);
        Assert.Contains("Revenue by Quarter", plain);
        Assert.Contains("Q1, 42", plain);
        Assert.Contains("Step One", plain);
        Assert.Contains("Step Two", plain);
    }

    [Fact]
    public void Pptx_TitleBecomesHeadingAndBodyParagraph_InJson()
    {
        var doc = new PptxExtractor().Extract(MinimalPptx(), PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Json });
        var json = Render(doc, OutputFormat.Json);
        Assert.Contains("\"heading\":\"My Title\"", json);
        Assert.Contains("\"level\":2", json);
        Assert.Contains("Body text", json);
    }

    [Fact]
    public void Pptx_PlainOutputHasNoHeadingMarkers()
    {
        var doc = new PptxExtractor().Extract(MinimalPptx(), PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        var plain = Render(doc, OutputFormat.Plain);
        Assert.Contains("My Title", plain);
        Assert.Contains("Body text", plain);
        Assert.DoesNotContain("#", plain);
    }

    // ── DOCX ────────────────────────────────────────────────────────────────────
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static byte[] MinimalDocx() => Zip(
        ("word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Chapter One</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t xml:space=\"preserve\">Hello </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>world</w:t></w:r></w:p>" +
            "</w:body></w:document>"),
        ("word/styles.xml",
            "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:style w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style>" +
            "</w:styles>"));

    [Fact]
    public void Docx_HeadingLevelFromOutlineLvl_AndParagraphText()
    {
        var doc = new DocxExtractor().Extract(MinimalDocx(), DocxMime, new ExtractionConfig());

        var heading = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Heading);
        Assert.Equal(1, heading.Kind.Level); // outlineLvl 0 → h1
        Assert.Equal("Chapter One", heading.Text);

        var para = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("Hello world", para.Text); // plain text = concatenated run text
    }

    /// <summary>
    /// Upstream <c>fix(docx): report a Heading1 paragraph as level 1</c>. With no
    /// <c>styles.xml</c> to resolve, the level comes from the trailing digit of the style id —
    /// which is already the level the author meant. Adding one to it, as the zero-based
    /// <c>w:outlineLvl</c> path correctly does, made every heading in such a document a level
    /// too deep.
    /// </summary>
    [Theory]
    [InlineData("Heading1", 1)]
    [InlineData("Heading2", 2)]
    [InlineData("Heading6", 6)]
    public void Docx_HeadingLevelFromStyleName_IsTheDigitItself(string styleId, int expected)
    {
        byte[] docx = Zip(
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                $"<w:p><w:pPr><w:pStyle w:val=\"{styleId}\"/></w:pPr><w:r><w:t>Chapter One</w:t></w:r></w:p>" +
                "</w:body></w:document>"));

        var doc = new DocxExtractor().Extract(docx, DocxMime, new ExtractionConfig());
        var heading = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Heading);
        Assert.Equal(expected, heading.Kind.Level);
    }

    [Fact]
    public void Docx_TableCellsUseRunMarkdown_AndMetadataFormatIsDocx()
    {
        byte[] docx = Zip(
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc>" +
                "<w:tc><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
                "</w:body></w:document>"));
        var doc = new DocxExtractor().Extract(docx, DocxMime, new ExtractionConfig());

        Assert.Single(doc.Tables);
        Assert.Equal("A", doc.Tables[0].Cells[0][0]);
        Assert.Equal("**B**", doc.Tables[0].Cells[0][1]); // runs_to_markdown wraps bold
        Assert.Equal("docx", doc.Metadata.Format!.FormatType);
    }

    /// <summary>
    /// Word writes a `<w:lastRenderedPageBreak/>` at the start of the first run on each page it
    /// laid out. When one lands after text the paragraph has already collected, the break belongs
    /// behind that paragraph — the paragraph's own element has not been emitted yet at the point
    /// the walk reaches the hint, so recording it there would put the page boundary a whole
    /// paragraph too early (Rust GH#1416).
    /// </summary>
    [Fact]
    public void Docx_APageBreakAfterTextInItsParagraphFollowsThatParagraph()
    {
        byte[] docx = Zip(
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:p><w:r><w:t>First page.</w:t></w:r>" +
                "<w:r><w:lastRenderedPageBreak/><w:t> Still page one.</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Second page.</w:t></w:r></w:p>" +
                "</w:body></w:document>"));
        var doc = new DocxExtractor().Extract(docx, DocxMime, new ExtractionConfig());

        var pages = doc.Metadata.Pages!;
        Assert.Equal(2u, pages.TotalCount);
        // The whole first paragraph is on page one; the boundary falls between the paragraphs.
        int end = "First page. Still page one.".Length;
        Assert.Contains($"\"ByteEnd\":{end}", System.Text.Json.JsonSerializer.Serialize(pages.Boundaries));
    }

    /// <summary>
    /// A reviewer comment leaves a `[cmt:N]` marker in the body and its body text in
    /// `word/comments.xml`; the marker becomes a `CommentRef` element and the body a
    /// `CommentDefinition`, so a consumer can tell a comment from an authored footnote.
    /// </summary>
    [Fact]
    public void Docx_CommentsBecomeCommentRefAndDefinitionElements()
    {
        byte[] docx = Zip(
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:p><w:r><w:t>Annotated sentence.</w:t></w:r>" +
                "<w:r><w:commentReference w:id=\"7\"/></w:r></w:p>" +
                "</w:body></w:document>"),
            ("word/comments.xml",
                "<w:comments xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:comment w:id=\"7\"><w:p><w:r><w:t>Please rephrase.</w:t></w:r></w:p></w:comment>" +
                "</w:comments>"));
        var doc = new DocxExtractor().Extract(docx, DocxMime, new ExtractionConfig());

        var refElem = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.CommentRef);
        Assert.Equal("7", refElem.Text);
        Assert.Equal("cmt7", refElem.Anchor);

        var def = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.CommentDefinition);
        Assert.Equal("Please rephrase.", def.Text);
        Assert.Equal("cmt7", def.Anchor);

        // The comment body is not part of the flow, but it is not dropped either: it is
        // rendered at the end, the way a footnote definition is.
        Assert.Contains("Please rephrase.", Render(doc, OutputFormat.Markdown));
        Assert.Contains("Annotated sentence.[cmt:7]", Render(doc, OutputFormat.Plain));
    }

    /// <summary>
    /// The paragraphs inside a text box are layout, not document structure: they collapse into a
    /// single paragraph whose lines are newline-separated, so a numbered `w:p` inside a shape
    /// cannot turn into a list item of the surrounding document.
    /// </summary>
    [Fact]
    public void Docx_TextBoxParagraphsCollapseIntoOneParagraph()
    {
        byte[] docx = Zip(
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                "xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>" +
                "<w:p><w:r><w:pict><v:shape><v:textbox><w:txbxContent>" +
                "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>" +
                "<w:r><w:t>First line</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Second line</w:t></w:r></w:p>" +
                "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>" +
                "</w:body></w:document>"));
        var doc = new DocxExtractor().Extract(docx, DocxMime, new ExtractionConfig());

        var paragraphs = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).ToList();
        Assert.Single(paragraphs);
        Assert.Equal("First line\nSecond line", paragraphs[0].Text);
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.ListItem);
    }

    /// <summary>
    /// `mc:AlternateContent` stores a shape twice — DrawingML in `mc:Choice`, VML in
    /// `mc:Fallback`. Only the DrawingML copy is kept, or the text box would be extracted twice.
    /// </summary>
    [Fact]
    public void Docx_AlternateContentTextBoxIsNotEmittedTwice()
    {
        const string txbx = "<w:txbxContent><w:p><w:r><w:t>Boxed</w:t></w:r></w:p></w:txbxContent>";
        byte[] docx = Zip(
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
                "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
                "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><w:body>" +
                "<w:p><w:r><mc:AlternateContent>" +
                "<mc:Choice Requires=\"wps\"><w:drawing><wps:wsp><wps:txbx>" + txbx +
                "</wps:txbx></wps:wsp></w:drawing></mc:Choice>" +
                "<mc:Fallback><w:pict><v:shape><v:textbox>" + txbx +
                "</v:textbox></v:shape></w:pict></mc:Fallback>" +
                "</mc:AlternateContent></w:r></w:p>" +
                "</w:body></w:document>"));
        var doc = new DocxExtractor().Extract(docx, DocxMime, new ExtractionConfig());

        var paragraphs = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).ToList();
        Assert.Single(paragraphs);
        Assert.Equal("Boxed", paragraphs[0].Text);
    }

    /// <summary>
    /// Two siblings of `a:r` carry text too: `a:br`, an explicit in-paragraph line break, and
    /// `a:fld`, a field — a slide number, say — whose rendered value PowerPoint caches in a
    /// nested `a:t`. Reading only `a:r` runs the two halves of a break together and loses the
    /// field outright.
    /// </summary>
    [Fact]
    public void Pptx_LineBreaksSplitRunsAndFieldsKeepTheirCachedText()
    {
        var pptx = Zip(
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml",
                "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
                "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:nvPr/></p:nvSpPr><p:txBody><a:p>" +
                "<a:r><a:t>Company A</a:t></a:r><a:br/><a:r><a:t>Product is more expensive</a:t></a:r>" +
                "</a:p></p:txBody></p:sp>" +
                "<p:sp><p:nvSpPr><p:nvPr/></p:nvSpPr><p:txBody><a:p>" +
                "<a:fld id=\"{1}\" type=\"slidenum\"><a:t>2</a:t></a:fld>" +
                "</a:p></p:txBody></p:sp>" +
                "</p:spTree></p:cSld></p:sld>"));

        var doc = new PptxExtractor().Extract(pptx, PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        var texts = doc.Elements.Select(e => e.Text).ToList();
        // The break separates the two halves rather than running them together...
        Assert.Contains(texts, t => t.StartsWith("Company A", StringComparison.Ordinal)
                                    && t.EndsWith("Product is more expensive", StringComparison.Ordinal)
                                    && t != "Company AProduct is more expensive");
        // ...and the field contributes the value PowerPoint cached for it.
        Assert.Contains("2", texts);
    }

    /// <summary>
    /// Slide shapes are laid out top-to-bottom, and only a DrawingML <c>a:xfrm</c> counts as a
    /// position. A <c>p:graphicFrame</c> writes <c>p:xfrm</c> instead, so a table reports no
    /// position and sorts ahead of the body text that shares its row.
    /// </summary>
    [Fact]
    public void Pptx_AGraphicFrameHasNoPositionAndLeadsItsRow()
    {
        const string slide =
            "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
            "<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"100\" y=\"100\"/><a:ext cx=\"10\" cy=\"10\"/></a:xfrm></p:spPr>" +
            "<p:txBody><a:p><a:r><a:t>Goals</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:nvPr/></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"100\" y=\"900\"/><a:ext cx=\"10\" cy=\"10\"/></a:xfrm></p:spPr>" +
            "<p:txBody><a:p><a:r><a:t>Bullet text</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:graphicFrame><p:xfrm><a:off x=\"5000\" y=\"900\"/><a:ext cx=\"10\" cy=\"10\"/></p:xfrm>" +
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">" +
            "<a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Cell</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl>" +
            "</a:graphicData></a:graphic></p:graphicFrame>" +
            "</p:spTree></p:cSld></p:sld>";

        var pptx = Zip(
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", slide));

        var doc = new PptxExtractor().Extract(pptx, PptxMime, new ExtractionConfig { OutputFormat = OutputFormat.Plain });
        string plain = Derive.DeriveExtractionResult(doc, includeDocumentStructure: false, OutputFormat.Plain).Content;

        Assert.True(plain.IndexOf("Cell", StringComparison.Ordinal)
            < plain.IndexOf("Bullet text", StringComparison.Ordinal), plain);
    }
}
