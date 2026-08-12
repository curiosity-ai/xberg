using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Byte-exact parity tests for the comrak-backed Markdown and HTML renderers
/// (<see cref="MarkdownRenderer"/>, <see cref="HtmlRenderer"/>). Expected strings are the
/// output of the upstream Rust `format_commonmark` / `format_html` for the same AST.
/// </summary>
public class RendererParityTests
{
    private static InternalDocument Build(Action<InternalDocumentBuilder> f)
    {
        var b = new InternalDocumentBuilder("test");
        f(b);
        return b.Build();
    }

    [Fact]
    public void HeadingMarkdownAndHtml()
    {
        var doc = Build(b => b.PushHeading(2, "Hello", null, null));
        Assert.Equal("## Hello\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<h2>Hello</h2>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void TitleRendersAsH1()
    {
        var doc = Build(b => b.PushTitle("My Document", null, null));
        Assert.Equal("# My Document\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<h1>My Document</h1>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void ParagraphWithBold()
    {
        var doc = Build(b => b.PushParagraph("Hello world",
            new List<TextAnnotation> { new() { Start = 0, End = 5, Kind = AnnotationKind.Bold } }, null, null));
        Assert.Equal("**Hello** world\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<p><strong>Hello</strong> world</p>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void TightBulletList()
    {
        var doc = Build(b =>
        {
            b.PushList(false);
            b.PushListItem("Alpha", false, new(), null, null);
            b.PushListItem("Beta", false, new(), null, null);
            b.EndList();
        });
        Assert.Equal("- Alpha\n- Beta\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<ul>\n<li>Alpha</li>\n<li>Beta</li>\n</ul>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void OrderedList()
    {
        var doc = Build(b =>
        {
            b.PushList(true);
            b.PushListItem("One", true, new(), null, null);
            b.PushListItem("Two", true, new(), null, null);
            b.EndList();
        });
        Assert.Equal("1. One\n2. Two\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<ol>\n<li>One</li>\n<li>Two</li>\n</ol>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void FencedCodeBlock()
    {
        var doc = Build(b => b.PushCode("fn main() {}", "rust", null, null));
        Assert.Equal("```rust\nfn main() {}\n```\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<pre lang=\"rust\"><code>fn main() {}</code></pre>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void Blockquote()
    {
        var doc = Build(b =>
        {
            b.PushQuoteStart();
            b.PushParagraph("Quoted text.", new(), null, null);
            b.PushQuoteEnd();
        });
        Assert.Equal("> Quoted text.\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<blockquote>\n<p>Quoted text.</p>\n</blockquote>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void Table()
    {
        var cells = new List<List<string>>
        {
            new() { "Name", "Age" },
            new() { "Alice", "30" },
        };
        var doc = Build(b => b.PushTableFromCells(cells, null, null));
        Assert.Equal("| Name | Age |\n| --- | --- |\n| Alice | 30 |\n", MarkdownRenderer.Render(doc));
        Assert.Equal(
            "<table>\n<thead>\n<tr>\n<th>Name</th>\n<th>Age</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>Alice</td>\n<td>30</td>\n</tr>\n</tbody>\n</table>\n",
            HtmlRenderer.Render(doc));
    }

    [Fact]
    public void LinkNotAutolinkWhenTextDiffersFromUrl()
    {
        var doc = Build(b => b.PushParagraph("click here",
            new List<TextAnnotation>
            {
                new() { Start = 0, End = 10, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = "https://example.com" } },
            }, null, null));
        Assert.Equal("[click here](https://example.com)\n", MarkdownRenderer.Render(doc));
        Assert.Equal("<p><a href=\"https://example.com\">click here</a></p>\n", HtmlRenderer.Render(doc));
    }

    [Fact]
    public void AutolinkWhenTextEqualsUrl()
    {
        var doc = Build(b => b.PushParagraph("https://example.com",
            new List<TextAnnotation>
            {
                new() { Start = 0, End = 19, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = "https://example.com" } },
            }, null, null));
        Assert.Equal("<https://example.com>\n", MarkdownRenderer.Render(doc));
    }

    [Fact]
    public void BoldLinkSameRangeNests()
    {
        var doc = Build(b => b.PushParagraph("click",
            new List<TextAnnotation>
            {
                new() { Start = 0, End = 5, Kind = AnnotationKind.Bold },
                new() { Start = 0, End = 5, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = "https://example.com" } },
            }, null, null));
        Assert.Equal("[**click**](https://example.com)\n", MarkdownRenderer.Render(doc));
    }

    [Fact]
    public void EmptyDocumentRendersEmpty()
    {
        var doc = Build(_ => { });
        Assert.Equal("", MarkdownRenderer.Render(doc));
        Assert.Equal("", HtmlRenderer.Render(doc));
    }

    // Backslash-escaping parity: comrak's format_commonmark escapes punctuation in text
    // nodes, and MarkdownRenderer's post-processing then un-escapes the subset that does not
    // affect re-parsing (`_ [ ] ( ) * =`). What survives is `\#`, `\~`, `\\` mid-paragraph.
    // The expected strings below are the byte-exact output of the upstream Rust pipeline
    // (`xberg-reference-gen`, comrak 0.53.0) for the same text nodes.
    [Fact]
    public void TextNodePunctuationEscapingMatchesComrak()
    {
        var doc = Build(b =>
        {
            b.PushParagraph("Dash - test. Under _x_ y. Star *z*. Dot 1. two. Hash # h. Tilde ~t~ end.",
                new(), null, null);
            b.PushParagraph("Second para: a~~b and c\\d and 3.5 and -5 and =eq.", new(), null, null);
        });

        Assert.Equal(
            "Dash - test. Under _x_ y. Star *z*. Dot 1. two. Hash \\# h. Tilde \\~t\\~ end.\n\n"
            + "Second para: a\\~\\~b and c\\\\d and 3.5 and -5 and =eq.\n",
            MarkdownRenderer.Render(doc));

        // HTML escaping leaves ASCII punctuation untouched (only &, <, >, " are entity-escaped).
        Assert.Equal(
            "<p>Dash - test. Under _x_ y. Star *z*. Dot 1. two. Hash # h. Tilde ~t~ end.</p>\n"
            + "<p>Second para: a~~b and c\\d and 3.5 and -5 and =eq.</p>\n",
            HtmlRenderer.Render(doc));
    }

    // At begin_content (paragraph start) comrak escapes `-`/`+`/`=` (they would otherwise
    // start a list / setext rule). Post-processing then un-escapes `=` (in the un-escape set)
    // but leaves `\-` and `\+`. Verifies the begin_content branch of outc + post-processing.
    [Fact]
    public void BeginContentEscapingMatchesComrak()
    {
        var doc = Build(b =>
        {
            b.PushParagraph("- not a list", new(), null, null);
            b.PushParagraph("+ also not", new(), null, null);
            b.PushParagraph("= not setext", new(), null, null);
        });

        Assert.Equal(
            "\\- not a list\n\n\\+ also not\n\n= not setext\n",
            MarkdownRenderer.Render(doc));
    }
}
