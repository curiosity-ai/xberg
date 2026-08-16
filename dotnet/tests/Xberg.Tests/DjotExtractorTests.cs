using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for <see cref="DjotExtractor"/>. Ports the intent of the Rust
/// <c>extractors/djot_format/extractor.rs</c> test cases plus the pipe-table behavior exercised
/// by the <c>markdown/tables.djot</c> golden fixture.
/// </summary>
public class DjotExtractorTests
{
    private static InternalDocument Extract(string djot, string mime = "text/x-djot")
    {
        var ex = new DjotExtractor();
        return ex.Extract(Encoding.UTF8.GetBytes(djot), mime, new ExtractionConfig());
    }

    private static string ParagraphText(InternalDocument doc) =>
        string.Join("\n", doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text));

    [Fact]
    public void AdvertisesDjotMimeTypes()
    {
        var mimes = new DjotExtractor().SupportedMimeTypes.ToList();
        Assert.Contains("text/djot", mimes);
        Assert.Contains("text/x-djot", mimes);
    }

    [Fact]
    public void HeadingsAndParagraphsExtracted()
    {
        var doc = Extract("# Header\n\nThis is a paragraph with *bold* and _italic_ text.\n\n## Subheading\n\nMore content here.");
        var kinds = doc.Elements.Select(e => e.Kind.Tag).ToList();
        Assert.Contains(ElementKindTag.Heading, kinds);
        Assert.Contains(ElementKindTag.Paragraph, kinds);

        string para = ParagraphText(doc);
        Assert.Contains("This is a paragraph", para);
        Assert.Contains("bold", para);
        Assert.Contains("italic", para);

        var headings = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading).ToList();
        Assert.Contains(headings, h => h.Text == "Header");
        Assert.Contains(headings, h => h.Text == "Subheading");
    }

    [Fact]
    public void StrongAndEmphasisProduceAnnotations()
    {
        // Djot: *strong* and _emphasis_.
        var doc = Extract("A *bold* and _italic_ word.");
        var para = doc.Elements.First(e => e.Kind.Tag == ElementKindTag.Paragraph);
        var tags = para.Annotations.Select(a => a.Kind.Which).ToList();
        Assert.Contains(AnnotationKind.Tag.Bold, tags);
        Assert.Contains(AnnotationKind.Tag.Italic, tags);
    }

    [Fact]
    public void TrimmedParagraphWithEmojiPreserved()
    {
        var doc = Extract("  *bold* \U0001F389 text  ");
        string para = ParagraphText(doc);
        Assert.Contains("bold", para);
        Assert.Contains("\U0001F389", para);
    }

    [Fact]
    public void CjkParagraphWithFormattingPreserved()
    {
        var doc = Extract("# CJK\n\nこれは*太字*テスト");
        string para = ParagraphText(doc);
        Assert.Contains("太字", para);
        Assert.Contains("これは", para);
    }

    [Fact]
    public void ImageUriExtracted()
    {
        var doc = Extract("![A diagram](https://example.com/diagram.png)\n\nSome text.");
        Assert.Contains(doc.Uris, u => u.Url.Contains("diagram.png"));
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Image);
    }

    [Fact]
    public void LinkUriExtracted()
    {
        var doc = Extract("See [the site](https://example.com/page).");
        Assert.Contains(doc.Uris, u => u.Url == "https://example.com/page" && u.Kind == Xberg.Types.UriKind.Hyperlink);
    }

    [Fact]
    public void CodeBlockExtracted()
    {
        var doc = Extract("```rust\nfn main() {}\n```");
        var code = doc.Elements.FirstOrDefault(e => e.Kind.Tag == ElementKindTag.Code);
        Assert.NotNull(code);
        Assert.Contains("fn main", code!.Text);
    }

    [Fact]
    public void FrontmatterMetadataExtracted()
    {
        var doc = Extract("---\ntitle: Djot Doc\nauthor: Jane\n---\n\nBody.");
        Assert.Equal("Djot Doc", doc.Metadata.Title);
        Assert.Equal("Jane", doc.Metadata.CreatedBy);
    }

    // ------------------------------------------------------------------
    // Pipe tables (the markdown/tables.djot golden fixture)
    // ------------------------------------------------------------------

    [Fact]
    public void PipeTableExtractedAsStructuredData()
    {
        var doc = Extract(
            "| Right | Left | Center | Default |\n" +
            "|------:|:-----|:------:|-------|\n" +
            "|    12 | 12   |   12   | 12      |\n" +
            "|   123 | 123  |  123   | 123     |\n");

        Assert.Single(doc.Tables);
        var t = doc.Tables[0];
        // Delimiter row is dropped; header + two data rows remain.
        Assert.Equal(3, t.Cells.Count);
        Assert.Equal(new[] { "Right", "Left", "Center", "Default" }, t.Cells[0]);
        Assert.Equal(new[] { "12", "12", "12", "12" }, t.Cells[1]);
        Assert.Equal((uint)0, t.PageNumber);
        Assert.Contains("| --- |", t.Markdown);

        // The table must also exist as an element, or every renderer walks past it and the
        // content never reaches the output.
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Table);
    }

    /// <summary>A table keeps its place in the document rather than being appended at the end.</summary>
    [Fact]
    public void TableIsEmittedWhereItAppears()
    {
        var doc = Extract("Intro paragraph.\n\n| Name | Age |\n|------|-----|\n| Alice | 30 |\n\nOutro paragraph.\n");

        var kinds = doc.Elements.Select(e => e.Kind.Tag).ToList();
        int intro = doc.Elements.FindIndex(e => e.Text.Contains("Intro", StringComparison.Ordinal));
        int table = kinds.IndexOf(ElementKindTag.Table);
        int outro = doc.Elements.FindIndex(e => e.Text.Contains("Outro", StringComparison.Ordinal));

        Assert.True(intro >= 0 && table >= 0 && outro >= 0);
        Assert.True(intro < table && table < outro);
    }

    [Fact]
    public void TableCaptionSuppressed()
    {
        var doc = Extract(
            "Intro paragraph.\n\n" +
            "| A | B |\n|---|---|\n| 1 | 2 |\n\n" +
            "^ This is a caption that must not appear as a paragraph.\n");

        string para = ParagraphText(doc);
        Assert.Contains("Intro paragraph.", para);
        Assert.DoesNotContain("caption", para);
        Assert.Single(doc.Tables);
    }

    [Fact]
    public void SmartApostropheDroppedFromCellText()
    {
        // jotdown converts a straight apostrophe into a smart-quote event that is not part of the
        // Str stream, so "Here's" collapses to "Heres" in extracted cell text.
        var doc = Extract("| Col |\n|-----|\n| Here's one |\n");
        Assert.Single(doc.Tables);
        Assert.Equal("Heres one", doc.Tables[0].Cells[1][0]);
    }

    /// <summary>
    /// Djot has no pages, and upstream pushes every table without one. The old separate pass
    /// numbered them 1, 2, 3… by position, which the goldens do not agree with.
    /// </summary>
    [Fact]
    public void EveryTableIsUnpaged()
    {
        var doc = Extract(
            "| A | B |\n|---|---|\n| 1 | 2 |\n\n" +
            "| C | D |\n|---|---|\n| 3 | 4 |\n");
        Assert.Equal(2, doc.Tables.Count);
        Assert.All(doc.Tables, t => Assert.Equal((uint)0, t.PageNumber));
        Assert.Equal(2, doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.Table));
    }

    [Fact]
    public void TableWithoutHeaderStartsWithDelimiter()
    {
        var doc = Extract(
            "|----:|:----|\n" +
            "|  12 | 12  |\n" +
            "| 123 | 123 |\n");
        Assert.Single(doc.Tables);
        // Leading delimiter row dropped → only the two data rows.
        Assert.Equal(2, doc.Tables[0].Cells.Count);
        Assert.Equal(new[] { "12", "12" }, doc.Tables[0].Cells[0]);
    }
}
