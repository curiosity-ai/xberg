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
}
