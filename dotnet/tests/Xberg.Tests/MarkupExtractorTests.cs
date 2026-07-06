using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the lightweight markup/data extractors ported from the Rust crate:
/// RST, Org, Typst, LaTeX, OPML, Jupyter, FictionBook, BibTeX, Citation, DBF.
/// </summary>
public class MarkupExtractorTests
{
    private static InternalDocument Extract(IExtractor e, string text, string mime) =>
        e.Extract(Encoding.UTF8.GetBytes(text), mime, new ExtractionConfig());

    [Fact]
    public void Rst_HeadingAndFieldList()
    {
        var doc = Extract(new RstExtractor(), ":Author: John Doe\n\nTitle\n=====\n\nA paragraph.\n", "text/x-rst");
        Assert.Equal(new List<string> { "John Doe" }, doc.Metadata.Authors);
        string plain = PlainRenderer.Render(doc);
        Assert.Contains("Title", plain);
        Assert.Contains("A paragraph.", plain);
    }

    [Fact]
    public void Rst_SimpleTable()
    {
        var doc = Extract(new RstExtractor(), "=====  =====\nName   Age\n=====  =====\nAlice  30\n=====  =====\n", "text/x-rst");
        Assert.NotEmpty(doc.Tables);
        Assert.Equal(new List<string> { "Name", "Age" }, doc.Tables[0].Cells[0]);
    }

    [Fact]
    public void Org_MetadataAndHeadings()
    {
        var doc = Extract(new OrgExtractor(), "#+TITLE: Doc\n#+AUTHOR: Jane\n\n* Heading\n\nText.\n", "text/x-org");
        Assert.Equal("Doc", doc.Metadata.Title);
        Assert.Equal(new List<string> { "Jane" }, doc.Metadata.Authors);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "Heading");
    }

    [Fact]
    public void Org_PipeTable()
    {
        var doc = Extract(new OrgExtractor(), "| Name | Age |\n|------+-----|\n| Alice | 30 |\n", "text/x-org");
        Assert.NotEmpty(doc.Tables);
    }

    [Fact]
    public void Typst_HeadingKeepsMarkerAndMetadata()
    {
        var doc = Extract(new TypstExtractor(), "#set document(title: \"T\")\n= Intro\nBody.\n", "application/x-typst");
        Assert.Equal("T", doc.Metadata.Title);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "= Intro");
    }

    [Fact]
    public void Latex_SectionAndBoldAnnotation()
    {
        var doc = Extract(new LatexExtractor(), "\\begin{document}\n\\section{Intro}\nHello \\textbf{world}.\n\\end{document}\n", "text/x-tex");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "Intro");
        var para = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text.Contains("world"));
        Assert.Contains(para.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Bold);
    }

    [Fact]
    public void Latex_TitleMetadata()
    {
        var doc = Extract(new LatexExtractor(), "\\title{My Title}\n\\author{Me}\n\\begin{document}\nx\n\\end{document}\n", "text/x-tex");
        Assert.Equal("My Title", doc.Metadata.Title);
        Assert.Equal("Me", doc.Metadata.CreatedBy);
    }

    [Fact]
    public void Opml_OutlineHeadingsAndMetadata()
    {
        string opml = "<opml><head><title>Feeds</title><ownerName>Bob</ownerName></head>" +
                      "<body><outline text=\"A\"><outline text=\"B\"/></outline></body></opml>";
        var doc = Extract(new OpmlExtractor(), opml, "application/xml+opml");
        Assert.Equal("Feeds", doc.Metadata.Title);
        Assert.Equal("Bob", doc.Metadata.CreatedBy);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "A");
    }

    [Fact]
    public void Jupyter_CellsAndMetadata()
    {
        string nb = "{\"cells\":[{\"cell_type\":\"markdown\",\"source\":[\"# Title\"]}," +
                    "{\"cell_type\":\"code\",\"execution_count\":1,\"source\":[\"print(1)\"],\"outputs\":[]}]," +
                    "\"metadata\":{\"language_info\":{\"name\":\"python\"}},\"nbformat\":4,\"nbformat_minor\":5}";
        var doc = Extract(new JupyterExtractor(), nb, "application/x-ipynb+json");
        Assert.Equal("python", doc.Metadata.Language);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "Title");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Code && e.Text == "print(1)");
    }

    [Fact]
    public void FictionBook_TitleAndParagraph()
    {
        string fb2 = "<?xml version=\"1.0\"?><FictionBook><description><title-info>" +
                     "<book-title>Book</book-title><lang>en</lang></title-info></description>" +
                     "<body><section><title><p>Chapter</p></title><p>Hello world.</p></section></body></FictionBook>";
        var doc = Extract(new FictionBookExtractor(), fb2, "application/x-fictionbook+xml");
        Assert.Equal("Book", doc.Metadata.Title);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text == "Hello world.");
    }

    [Fact]
    public void Bibtex_EntryBecomesCitation()
    {
        string bib = "@article{k1,\n  title = {A Title},\n  author = {Alice Smith and Bob Jones},\n  year = {2020}\n}\n";
        var doc = Extract(new BibtexExtractor(), bib, "application/x-bibtex");
        var meta = Assert.IsType<BibtexMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(1, meta.EntryCount);
        var cite = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Citation));
        Assert.StartsWith("@article{k1,", cite.Text);
        Assert.Contains("author = {Alice Smith and Bob Jones}", cite.Text);
    }

    [Fact]
    public void Citation_RisParsesTitleAndAuthors()
    {
        string ris = "TY  - JOUR\nTI  - Sample Title\nAU  - Smith, John\nAU  - Doe, Jane\nPY  - 2024\nER  -\n";
        var doc = Extract(new CitationExtractor(), ris, "application/x-research-info-systems");
        Assert.Equal(new List<string> { "Jane Doe", "John Smith" }, doc.Metadata.Authors);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Citation && e.Text == "Sample Title");
    }

    [Fact]
    public void Dbf_ParsesHeaderTable()
    {
        byte[] dbf = BuildMinimalDbf();
        var doc = new DbfExtractor().Extract(dbf, "application/x-dbf", new ExtractionConfig());
        var meta = Assert.IsType<DbfMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(2, meta.FieldCount);
        Assert.Equal(1, meta.RecordCount);
        Assert.Single(doc.Tables);
        Assert.Equal(new List<string> { "NAME", "AGE" }, doc.Tables[0].Cells[0]);
        Assert.Equal(new List<string> { "Alice", "30" }, doc.Tables[0].Cells[1]);
    }

    // Builds a tiny dBASE III file: 2 Character fields (NAME[10], AGE[3]), 1 record.
    private static byte[] BuildMinimalDbf()
    {
        int recordSize = 1 + 10 + 3;
        int headerSize = 32 + 2 * 32 + 1;
        var buf = new byte[headerSize + recordSize + 1];
        buf[0] = 0x03;
        BitConverter.GetBytes(1).CopyTo(buf, 4);          // num records
        BitConverter.GetBytes((ushort)headerSize).CopyTo(buf, 8);
        BitConverter.GetBytes((ushort)recordSize).CopyTo(buf, 10);
        WriteField(buf, 32, "NAME", 'C', 10);
        WriteField(buf, 64, "AGE", 'C', 3);
        buf[96] = 0x0D;                                    // header terminator
        int rec = headerSize;
        buf[rec] = 0x20;                                   // not deleted
        Encoding.ASCII.GetBytes("Alice     ").CopyTo(buf, rec + 1);
        Encoding.ASCII.GetBytes("30 ").CopyTo(buf, rec + 11);
        buf[^1] = 0x1A;                                    // EOF
        return buf;
    }

    private static void WriteField(byte[] buf, int off, string name, char type, byte len)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(buf, off);
        buf[off + 11] = (byte)type;
        buf[off + 16] = len;
    }
}
