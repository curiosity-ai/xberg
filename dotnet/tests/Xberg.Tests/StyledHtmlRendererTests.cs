using System.Text;
using Xberg.Core;
using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The styled HTML renderer, ported from Rust <c>rendering/html_styled.rs</c>.
/// </summary>
/// <remarks>
/// Every class name and every <c>--kb-*</c> custom property is part of a stability contract, so
/// these tests pin the emitted markup rather than merely checking it parses.
/// </remarks>
public class StyledHtmlRendererTests
{
    private static InternalDocument DocOf(params InternalElement[] elements)
    {
        var doc = new InternalDocument("test");
        foreach (var e in elements) doc.PushElement(e);
        return doc;
    }

    private static InternalElement El(ElementKind kind, string text) =>
        InternalElement.TextElement(kind, text, 0);

    private static string Render(HtmlOutputConfig config, InternalDocument doc) =>
        new StyledHtmlRenderer(config).Render(doc);

    [Fact]
    public void AParagraphBecomesAPrefixedP()
    {
        string html = Render(new HtmlOutputConfig(), DocOf(El(ElementKind.Paragraph, "Hello")));
        Assert.Contains("<p class=\"kb-p\">Hello</p>", html);
    }

    [Fact]
    public void AHeadingCarriesBothItsGenericAndItsLevelClass()
    {
        string html = Render(new HtmlOutputConfig(), DocOf(El(ElementKind.Heading(2), "Title")));
        Assert.Contains("<h2 class=\"kb-h kb-h2\">Title</h2>", html);
    }

    [Fact]
    public void TheWrapperIsAlwaysPresent()
    {
        string html = Render(new HtmlOutputConfig(), DocOf());
        Assert.Equal("<div class=\"kb-doc\"><main class=\"kb-content\"></main></div>", html);
    }

    /// <summary>
    /// The escape set is <c>v_htmlescape</c>'s, which is OWASP's: it covers the slash as well as
    /// the five usual characters. That is visible output, not an internal detail — a URL in an
    /// <c>href</c> comes out with <c>&amp;#x2f;</c> where a conventional encoder leaves a slash.
    /// </summary>
    [Fact]
    public void TextIsEscapedIncludingTheSlash()
    {
        string html = Render(new HtmlOutputConfig(),
            DocOf(El(ElementKind.Paragraph, "<script>a & b</script> 'q' \"d\" a/b")));
        Assert.Contains("&lt;script&gt;a &amp; b&lt;&#x2f;script&gt; &#x27;q&#x27; &quot;d&quot; a&#x2f;b", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void EmbedCssWritesTheThemeIntoAStyleBlock()
    {
        string html = Render(new HtmlOutputConfig { Theme = HtmlTheme.Default }, DocOf());
        Assert.Contains("<style>", html);
        Assert.Contains("--kb-font-family", html);
    }

    [Fact]
    public void EmbedCssFalseOmitsTheStyleBlockButKeepsTheClasses()
    {
        string html = Render(
            new HtmlOutputConfig { Theme = HtmlTheme.Default, EmbedCss = false },
            DocOf(El(ElementKind.Paragraph, "x")));
        Assert.DoesNotContain("<style>", html);
        Assert.Contains("class=\"kb-p\"", html);
    }

    [Fact]
    public void TheUnstyledThemeEmitsNoStyleBlockAtAll()
    {
        string html = Render(new HtmlOutputConfig(), DocOf(El(ElementKind.Paragraph, "x")));
        Assert.DoesNotContain("<style>", html);
    }

    [Fact]
    public void UserCssFollowsTheTheme()
    {
        string html = Render(
            new HtmlOutputConfig { Theme = HtmlTheme.Default, Css = ".kb-p { color: red; }" },
            DocOf());
        Assert.Contains(".kb-p { color: red; }", html);
        Assert.True(html.IndexOf("--kb-font-family", StringComparison.Ordinal)
                    < html.IndexOf(".kb-p { color: red; }", StringComparison.Ordinal));
    }

    /// <summary>
    /// A closing tag inside user CSS would end the style block early and let everything after it
    /// out as markup, so the closing tag is stripped. What follows it stays where it is —
    /// harmlessly, as text inside a stylesheet that is never closed early.
    /// </summary>
    [Fact]
    public void UserCssCannotCloseTheStyleBlockEarly()
    {
        string html = Render(
            new HtmlOutputConfig { Theme = HtmlTheme.Default, Css = "</style><script>evil()</script>" },
            DocOf());

        // Exactly one closing tag, the renderer's own, and it comes after the injected text.
        Assert.Equal(1, html.Split("</style>").Length - 1);
        Assert.True(html.IndexOf("evil()", StringComparison.Ordinal)
                    < html.IndexOf("</style>", StringComparison.Ordinal));
    }

    [Fact]
    public void ACustomPrefixReplacesKb()
    {
        string html = Render(new HtmlOutputConfig { ClassPrefix = "zz-" },
            DocOf(El(ElementKind.Paragraph, "x")));
        Assert.Contains("<p class=\"zz-p\">x</p>", html);
        Assert.DoesNotContain("kb-p", html);
    }

    [Fact]
    public void APrefixThatIsNotAValidClassFragmentIsRefused()
    {
        Assert.Throws<ValidationException>(() =>
            new StyledHtmlRenderer(new HtmlOutputConfig { ClassPrefix = "a b\"" }));
    }

    /// <summary>
    /// Math, not code: LaTeX in display delimiters that KaTeX and MathJax pick up as-is, and
    /// that degrades to visible source rather than to a monospace block claiming to be code.
    /// </summary>
    [Fact]
    public void AFormulaBecomesDisplayMathNotACodeBlock()
    {
        string html = Render(new HtmlOutputConfig(), DocOf(El(ElementKind.Formula, "x < y")));
        Assert.Contains("<div class=\"kb-formula kb-math\" data-math-style=\"display\">$$x &lt; y$$</div>", html);
        Assert.DoesNotContain("<pre", html);
    }

    [Fact]
    public void AListIsWrappedInItsOwnElement()
    {
        var doc = DocOf(
            El(ElementKind.ListStart(false), ""),
            El(ElementKind.ListItem(false), "one"),
            El(ElementKind.ListEnd, ""));
        string html = Render(new HtmlOutputConfig(), doc);
        Assert.Contains("<ul class=\"kb-list kb-ul\"><li class=\"kb-li\">one</li></ul>", html);
    }

    [Fact]
    public void ATableEmitsItsFirstRowAsTheHeader()
    {
        var doc = new InternalDocument("test");
        doc.Tables.Add(new Table
        {
            Cells = new List<List<string>> { new() { "A", "B" }, new() { "1", "2" } },
        });
        doc.PushElement(El(ElementKind.Table(0), ""));

        string html = Render(new HtmlOutputConfig(), doc);
        Assert.Contains("<thead class=\"kb-thead\"><tr class=\"kb-tr\">"
                        + "<th class=\"kb-th\">A</th><th class=\"kb-th\">B</th></tr></thead>", html);
        Assert.Contains("<tbody class=\"kb-tbody\"><tr class=\"kb-tr\">"
                        + "<td class=\"kb-td\">1</td><td class=\"kb-td\">2</td></tr></tbody>", html);
    }

    [Fact]
    public void ATableWithOneRowEmitsNoEmptyBody()
    {
        var doc = new InternalDocument("test");
        doc.Tables.Add(new Table { Cells = new List<List<string>> { new() { "only" } } });
        doc.PushElement(El(ElementKind.Table(0), ""));

        string html = Render(new HtmlOutputConfig(), doc);
        Assert.DoesNotContain("<tbody", html);
    }

    /// <summary>
    /// Only a raw block that declares itself HTML goes through verbatim. Most producers do not —
    /// ODP pushes speaker notes, Org pushes Org source — and writing those unescaped puts
    /// author-typed text into the output as markup.
    /// </summary>
    [Fact]
    public void OnlyARawBlockThatSaysItIsHtmlIsWrittenThrough()
    {
        var html = InternalElement.TextElement(ElementKind.RawBlock, "<b>bold</b>", 0);
        html.Attributes = new Dictionary<string, string> { ["format"] = "html" };
        var notes = InternalElement.TextElement(ElementKind.RawBlock, "<number>", 0);
        notes.Attributes = new Dictionary<string, string> { ["format"] = "odp-speaker-notes" };

        string rendered = Render(new HtmlOutputConfig(), DocOf(html, notes));
        Assert.Contains("<b>bold</b>", rendered);
        Assert.Contains("<pre class=\"kb-pre kb-raw\">&lt;number&gt;</pre>", rendered);
    }

    [Fact]
    public void ACodeBlockCarriesItsLanguageClass()
    {
        var elem = InternalElement.TextElement(ElementKind.Code, "print(1)", 0);
        elem.Attributes = new Dictionary<string, string> { ["language"] = "python" };
        string html = Render(new HtmlOutputConfig(), DocOf(elem));
        Assert.Contains("<pre class=\"kb-pre\"><code class=\"kb-code kb-lang-python\">print(1)</code></pre>", html);
    }

    /// <summary>
    /// A slide is a marker, not a container — there is no SlideEnd — so the section opens and
    /// closes in one go rather than leaving every deck's HTML unbalanced.
    /// </summary>
    [Fact]
    public void ASlideSectionIsClosedImmediately()
    {
        string html = Render(new HtmlOutputConfig(), DocOf(El(ElementKind.Slide(3), "Agenda")));
        Assert.Contains("<section class=\"kb-slide\" data-slide=\"3\">"
                        + "<h2 class=\"kb-h kb-h2\">Agenda</h2></section>", html);
    }

    [Fact]
    public void AnnotationsBecomeInlineMarkup()
    {
        var elem = InternalElement.TextElement(ElementKind.Paragraph, "bold text", 0);
        elem.Annotations.Add(new TextAnnotation
        {
            Start = 0,
            End = 4,
            Kind = new AnnotationKind { Which = AnnotationKind.Tag.Bold },
        });
        string html = Render(new HtmlOutputConfig(), DocOf(elem));
        Assert.Contains("<strong>bold</strong> text", html);
    }
}
