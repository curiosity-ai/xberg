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
    public void NbspIndentedLineDoesNotHang()
    {
        // Regression: a line indented with non-breaking spaces (U+00A0) was mistaken for an
        // indented code block by the block-indent measure (TrimStart treats nbsp as whitespace),
        // but the inner ASCII-space scanner refused to advance, spinning the parser forever.
        string md = "---\n\n    with a continuation\n";
        var task = System.Threading.Tasks.Task.Run(() =>
            Xberg.Internal.Commonmark.MarkdownParser.Parse(md));
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "MarkdownParser hung on nbsp-indented line");
    }

    [Fact]
    public void WriterFixtureParsesQuickly()
    {
        // Full fixture that previously hung the parser (nested lists, footnotes, indented code,
        // nbsp indentation). Assert it parses to completion within a generous bound.
        string[] candidates =
        {
            "/workspace/test_documents/ground_truth/fictionbook/writer.md",
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents/ground_truth/fictionbook/writer.md"),
        };
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // fixture tree not present in this environment
        string md = File.ReadAllText(path);
        var task = System.Threading.Tasks.Task.Run(() =>
            Xberg.Internal.Commonmark.MarkdownParser.Parse(md));
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "MarkdownParser hung on writer.md fixture");
        Assert.NotEmpty(task.Result);
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

    // ── math ──────────────────────────────────────────────────────────────────

    /// <summary>Display math is a block of its own, so it becomes a Formula without delimiters.</summary>
    [Fact]
    public void DisplayMathBecomesAFormulaElement()
    {
        var doc = Extract("Before.\n\n$$A=\\pi r^{2} $$\n\nAfter.\n");
        var formula = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Formula);
        Assert.Equal("A=\\pi r^{2}", formula.Text);
        Assert.DoesNotContain(doc.Elements, e => e.Text.Contains("$$", StringComparison.Ordinal));
    }

    /// <summary>Inline math stays in the text: `$x$` reads as maths wherever the text ends up.</summary>
    [Fact]
    public void InlineMathKeepsItsDelimiters()
    {
        var doc = Extract("The identity $a^2 + b^2$ holds.\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("The identity $a^2 + b^2$ holds.", para.Text);
    }

    /// <summary>
    /// A delimiter followed by whitespace cannot open a span, which is what keeps a lone `$` in
    /// prose from swallowing the rest of the line.
    /// </summary>
    [Theory]
    [InlineData("It costs $ 5 and $ 10 today.")]
    [InlineData("Prices: $5 today.")]
    public void ALoneDollarStaysLiteral(string source)
    {
        var doc = Extract(source + "\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal(source, para.Text);
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.Formula);
    }

    /// <summary>Math inside a code span is code, not maths.</summary>
    [Fact]
    public void MathInsideACodeSpanIsNotParsed()
    {
        var doc = Extract("Use `$x$` for maths.\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Contains("$x$", para.Text);
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.Formula);
    }

    // ── raw inline HTML ───────────────────────────────────────────────────────

    /// <summary>
    /// Markdown has no syntax for subscripts or superscripts, so documents reach for HTML. The
    /// tags used to be scanned and thrown away, taking the text they wrapped with them.
    /// </summary>
    [Fact]
    public void InlineHtmlPassesThroughVerbatim()
    {
        var doc = Extract("H<sub>2</sub>O is a liquid. 2<sup>10</sup> is 1024.\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("H<sub>2</sub>O is a liquid. 2<sup>10</sup> is 1024.", para.Text);
    }

    /// <summary>An HTML comment is inline HTML too, and documents use it as a marker.</summary>
    [Fact]
    public void InlineHtmlCommentsAreKept()
    {
        var doc = Extract("<!-- image -->\nText and picture.\n");
        Assert.Contains(doc.Elements, e => e.Text.Contains("<!-- image -->", StringComparison.Ordinal));
    }

    // ── superscript / subscript ───────────────────────────────────────────────

    /// <summary>
    /// Pandoc's `^x^` and `~x~`: markers are structure, so they leave the text. Both delimiters
    /// have to start a word, which is why a document wanting `H₂O` reaches for `<sub>` instead.
    /// </summary>
    [Fact]
    public void SuperscriptAndSubscriptBecomeAnnotations()
    {
        var doc = Extract("~Subscript~ and ^superscript^\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("Subscript and superscript", para.Text);
        Assert.Contains(para.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Superscript);
        Assert.Contains(para.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Subscript);
    }

    /// <summary>
    /// A single `~` and a `^` cannot sit inside a word, so a pair separated by a space is not a
    /// span. Treating them as one silently deleted both markers and the space between them.
    /// </summary>
    [Fact]
    public void IntrawordSuperscriptAndSubscriptDoNotPair()
    {
        var doc = Extract("These are not spans: a^b c^d, a~b c~d.\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("These are not spans: a^b c^d, a~b c~d.", para.Text);
        Assert.Empty(para.Annotations);
    }

    /// <summary>A doubled `~` is still a strikethrough, and may sit inside a word.</summary>
    [Fact]
    public void DoubledTildeRemainsStrikethrough()
    {
        var doc = Extract("~~gone~~ but here.\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("gone but here.", para.Text);
        Assert.Contains(para.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Strikethrough);
    }

    /// <summary>Runs of different lengths are not partners, so `~x~~` stays literal.</summary>
    [Fact]
    public void TildeRunsPairOnlyWithTheirOwnLength()
    {
        var doc = Extract("a ~x~~ b\n");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("a ~x~~ b", para.Text);
    }

    // ── setext headings ───────────────────────────────────────────────────────

    /// <summary>Text underlined with `=` or `-` is a heading, not a paragraph.</summary>
    [Fact]
    public void SetextUnderlinesMakeHeadings()
    {
        var doc = Extract("Lorem ipsum\n===========\n\nBody.\n\nSub\n---\n");

        var headings = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading).ToList();
        Assert.Equal(2, headings.Count);
        Assert.Equal(("Lorem ipsum", 1), (headings[0].Text, (int)headings[0].Kind.Level));
        Assert.Equal(("Sub", 2), (headings[1].Text, (int)headings[1].Kind.Level));
    }

    /// <summary>
    /// A line of `---` is an underline only after paragraph content; standing alone it stays the
    /// thematic break it would otherwise be.
    /// </summary>
    [Fact]
    public void ADashRuleWithNoParagraphAboveIsNotAHeading()
    {
        var doc = Extract("---\n\nBody.\n");
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading);
    }

    /// <summary>A setext heading takes the whole paragraph above it, however many lines.</summary>
    [Fact]
    public void SetextHeadingTakesEveryLineOfItsParagraph()
    {
        var doc = Extract("First line\nsecond line\n=====\n");
        var heading = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading);
        Assert.Equal("First line second line", heading.Text);
    }
}

/// <summary>
/// How a GFM table's rows are squared up against its header.
/// </summary>
public class MarkdownTableShapeTests
{
    private static Table Parse(string markdown)
    {
        var doc = new MarkdownExtractor().Extract(
            Encoding.UTF8.GetBytes(markdown), "text/markdown", new ExtractionConfig());
        return doc.Tables.Single();
    }

    [Fact]
    public void AShortRowIsPaddedToTheHeadersWidth()
    {
        // Without the padding a row's second value slides under the fourth heading.
        var table = Parse("""
            | A | B | C | D |
            | --- | --- | --- | --- |
            | 1 | 2.78% |
            """);
        Assert.Equal(new[] { "1", "2.78%", "", "" }, table.Cells[1]);
    }

    [Fact]
    public void ALongRowLosesItsExcess()
    {
        var table = Parse("""
            | A | B |
            | --- | --- |
            | 1 | 2 | 3 | 4 |
            """);
        Assert.Equal(new[] { "1", "2" }, table.Cells[1]);
    }

    [Fact]
    public void AWellFormedRowIsUnchanged()
    {
        var table = Parse("""
            | A | B |
            | --- | --- |
            | 1 | 2 |
            """);
        Assert.Equal(new[] { "A", "B" }, table.Cells[0]);
        Assert.Equal(new[] { "1", "2" }, table.Cells[1]);
    }
}

/// <summary>
/// What a fenced code block's info string contributes.
/// </summary>
public class MarkdownFenceInfoTests
{
    private static string? Language(string markdown)
    {
        var doc = new MarkdownExtractor().Extract(
            Encoding.UTF8.GetBytes(markdown), "text/markdown", new ExtractionConfig());
        var code = doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Code);
        return code.Attributes is { } a && a.TryGetValue("language", out var lang) ? lang : null;
    }

    [Fact]
    public void OnlyTheFirstTokenIsTheLanguage()
    {
        // The rest of an info string is renderer options, not part of the language name.
        Assert.Equal("mdx-invalid", Language("```mdx-invalid chrome=no\ncode\n```\n"));
        Assert.Equal("js", Language("```js,live\ncode\n```\n"));
    }

    [Fact]
    public void PandocBracesAndALeadingDotAreNotPartOfTheName()
    {
        Assert.Equal("python", Language("```{.python}\ncode\n```\n"));
    }

    [Fact]
    public void AFenceWithNoInfoStringNamesNoLanguage()
    {
        Assert.Null(Language("```\ncode\n```\n"));
    }

    [Fact]
    public void APlainLanguageIsUnchanged()
    {
        Assert.Equal("rust", Language("```rust\ncode\n```\n"));
    }
}
