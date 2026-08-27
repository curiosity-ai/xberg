using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// CommonMark/GFM block and inline rules the parser reproduces from pulldown-cmark
/// (`firstpass.rs`, `scanners.rs`, `parse.rs`), as the Rust markdown extractor
/// (`crates/xberg/src/extractors/markdown.rs`) consumes them.
/// </summary>
public class MarkdownBlockRuleTests
{
    private static InternalDocument Extract(string md) =>
        new MarkdownExtractor().Extract(Encoding.UTF8.GetBytes(md), "text/markdown", new ExtractionConfig());

    private static List<string> TextsOf(InternalDocument doc, ElementKindTag tag) =>
        doc.Elements.Where(e => e.Kind.Tag == tag).Select(e => e.Text).ToList();

    [Fact]
    public void AMarkerOnTheItemsOwnLineOpensASublist()
    {
        // `- - text` is an outer item whose only content is a nested list, so the item text is
        // the inner item's — the dash is structure, not the first character of the text.
        var items = TextsOf(Extract("- - First list item\n\n- - Second list item\n"), ElementKindTag.ListItem);
        Assert.Equal(new[] { "First list item", "Second list item" }, items);
    }

    [Fact]
    public void AnUnderIndentedLineContinuesTheItemsParagraph()
    {
        // CommonMark §5.2 lazy continuation: the second line belongs to the bullet above it even
        // though it carries no indentation at all.
        var doc = Extract("- keeping it simple\ncontinued on the next line,\n- and a second bullet\n");
        Assert.Equal(
            new[] { "keeping it simple continued on the next line,", "and a second bullet" },
            TextsOf(doc, ElementKindTag.ListItem));
        Assert.Empty(TextsOf(doc, ElementKindTag.Paragraph));
    }

    [Fact]
    public void ASiblingMarkerEndsTheItemWhateverItsNumber()
    {
        // "ordered lists interrupt a paragraph only at 1" governs starting a list, not
        // continuing one: `2.` after `1.` is the next item, never a lazy continuation.
        var items = TextsOf(Extract("1. Wear sunglasses\n2. Drink water\n3. Use sun cream\n"), ElementKindTag.ListItem);
        Assert.Equal(new[] { "Wear sunglasses", "Drink water", "Use sun cream" }, items);
    }

    [Fact]
    public void TheMarkerTakesTheWholeSpaceRunUpToFour()
    {
        // `2.` plus two spaces is a marker of width 4, so a continuation line indented eight
        // columns has four columns of code indentation left after it, not five.
        var doc = Extract("2.  begins with 2\n\n        code line\n");
        Assert.Equal(new[] { "code line" }, TextsOf(doc, ElementKindTag.Code));
    }

    [Fact]
    public void FiveSpacesAfterTheMarkerLeaveAnIndentedCodeBlock()
    {
        // With five or more spaces the marker keeps just one, and the rest is the item's own
        // indented code block — so the item has no text of its own.
        var doc = Extract("-     code\n      code\n");
        Assert.Equal(new[] { "code\ncode" }, TextsOf(doc, ElementKindTag.Code));
        Assert.Empty(TextsOf(doc, ElementKindTag.ListItem));
    }

    [Fact]
    public void AMarkerFurtherLeftContinuesTheSameList()
    {
        // A line that fails a list item's indentation closes the item, not the list around it,
        // and `continue_list` then matches on the bullet character alone. So a list that opens
        // indented and continues at column zero is one list, not two.
        var doc = Extract("  * indented start\n\n* at column zero\n");
        Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.ListStart));
        Assert.Equal(new[] { "indented start", "at column zero" }, TextsOf(doc, ElementKindTag.ListItem));
    }

    [Fact]
    public void ChangingTheBulletStartsANewList()
    {
        var doc = Extract("* asterisks\n\n- minuses\n\n+ pluses\n");
        Assert.Equal(3, doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.ListStart));
    }

    [Fact]
    public void AGfmAlertBecomesAnAdmonitionRatherThanAQuote()
    {
        var doc = Extract("> [!WARNING]\n> Remember to start Docker\n");
        var admonition = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Admonition);
        Assert.Equal("warning", admonition.Attributes!["kind"]);
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.QuoteStart);
        Assert.Contains("Remember to start Docker", TextsOf(doc, ElementKindTag.Paragraph));
    }

    [Fact]
    public void AColonLineTurnsTheParagraphAboveItIntoATerm()
    {
        var doc = Extract("Categories\n\n:   Ducks and geese\n");
        Assert.Equal(new[] { "Categories" }, TextsOf(doc, ElementKindTag.DefinitionTerm));
        Assert.Equal(new[] { "Ducks and geese" }, TextsOf(doc, ElementKindTag.DefinitionDescription));
    }

    [Fact]
    public void AThematicBreakBetweenThemBlocksTheDefinition()
    {
        // pulldown looks at the block immediately above the marker; a rule is a block, so the
        // paragraph before it is no longer a candidate term.
        var doc = Extract("::: section\n\n---\n\n:::\n");
        Assert.Empty(TextsOf(doc, ElementKindTag.DefinitionTerm));
    }

    [Fact]
    public void ANumberSignFollowedByADigitIsNotAHeading()
    {
        var doc = Extract("Last broken by\n#309 is a good example.\n");
        Assert.Equal(new[] { "Last broken by #309 is a good example." }, TextsOf(doc, ElementKindTag.Paragraph));
    }

    [Fact]
    public void ABacktickFenceCannotCarryBackticksInItsInfoString()
    {
        // ```` ```cmd``` ```` on one line is a code span in a paragraph, not a fence that
        // swallows the rest of the document.
        var doc = Extract("```sudo apt-get install python3-tk```\n1. Setup your env\n");
        Assert.Empty(TextsOf(doc, ElementKindTag.Code));
        Assert.Equal(new[] { "sudo apt-get install python3-tk" }, TextsOf(doc, ElementKindTag.Paragraph));
        Assert.Equal(new[] { "Setup your env" }, TextsOf(doc, ElementKindTag.ListItem));
    }

    [Fact]
    public void OnlyATableWithALeadingBarInterruptsAParagraph()
    {
        var barless = Extract("Associated claims\nClaim ID | Claim type\n-------- | ---------\nNo results\n");
        Assert.Empty(barless.Tables);

        var heavy = Extract("This is a table\n| a | b |\n|---|---|\n| c | d |\n");
        Assert.Single(heavy.Tables);
    }

    [Fact]
    public void DisplayMathKeepsTheIndentationOfItsLines()
    {
        var doc = Extract("$$\n\\begin{align}\n  a + b &= c\n\\end{align}\n$$\n");
        Assert.Equal(new[] { "\\begin{align}\n  a + b &= c\n\\end{align}" }, TextsOf(doc, ElementKindTag.Formula));
    }
}

/// <summary>
/// Inline rules the parser reproduces from pulldown-cmark: wikilinks, the reference-link
/// fallback, email autolinks and where a line break counts as text.
/// </summary>
public class MarkdownInlineRuleTests
{
    private static InternalDocument Extract(string md) =>
        new MarkdownExtractor().Extract(Encoding.UTF8.GetBytes(md), "text/markdown", new ExtractionConfig());

    private static string PlainText(string md) =>
        string.Join("\n", Extract(md).Elements.Where(e => e.Text.Length > 0).Select(e => e.Text));

    [Fact]
    public void AWikilinkShowsItsNameAndSwallowsNoTrailingParentheses()
    {
        // `[[1]](#cite_note-1)` is a wikilink to "1" followed by literal text: popping the
        // wikilink stack disables every enclosing link, so the parentheses never attach.
        Assert.Equal("A duckling 1(#cite_note-1) or baby duck", PlainText("A duckling [[1]](#cite_note-1) or baby duck"));
    }

    [Fact]
    public void APotholeShowsTheHalfAfterThePipe()
    {
        Assert.Equal("travelled back to 21 to note", PlainText("travelled back to [[Antioch, Perth|21]] to note"));
    }

    [Fact]
    public void AWikilinkWithoutAPotholeKeepsItsTextUnprocessed()
    {
        // The display text is a fresh node over the raw source range, so it never passes through
        // the first pass — its apostrophe stays straight where the prose around it curls.
        Assert.Equal("Cynth's Dajoard, it doesn’t", PlainText("[[Cynth's Dajoard]], it doesn't"));
    }

    [Fact]
    public void AnUnparsableDestinationFallsBackToTheShortcutReference()
    {
        // `[foo](/bar and baz)` is not an inline link — the destination holds a space — so the
        // label resolves as a shortcut reference and the parentheses stay as text.
        Assert.Equal("foo(/bar and baz)", PlainText("[foo]: /url\n\n[foo](/bar and baz)"));
    }

    [Fact]
    public void AnEmailAutolinkKeepsItsBareAddressAsTheDestination()
    {
        // pulldown reports the address itself; only its own HTML writer prepends `mailto:`.
        var doc = Extract("Write to <nobody@nowhere.net>.\n");
        var uri = Assert.Single(doc.Uris);
        Assert.Equal("nobody@nowhere.net", uri.Url);
    }

    [Fact]
    public void AnImageAltTextTakesNoSpaceFromALineBreak()
    {
        // The break is its own event and the alt-text buffer does not handle it, so the space it
        // stands for lands in the paragraph instead: the image is called "BuildStatus".
        Assert.Equal("[Image: BuildStatus (./img/x.png)]", PlainText("[![Build\nStatus](./img/x.png)](https://example.com)"));
    }

    [Fact]
    public void EmphasisOpensAcrossAHardBreak()
    {
        // `**\` ends the line; the backslash is the break marker, and it is punctuation, so the
        // delimiter run before it is left-flanking and opens.
        var doc = Extract("![](media/image1.png)**\\\nThis is test 1** 0:08\n");
        var paragraph = doc.Elements.Last(e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Equal("This is test 1 0:08", paragraph.Text);
        Assert.Contains(paragraph.Annotations, a => a.Kind.Which == AnnotationKind.Tag.Bold);
    }

    /// <summary>
    /// A display equation's body is read straight out of the joined source, so a line inside it
    /// that ends in a space keeps that space. Trailing whitespace is not content in prose, but
    /// dropping it where the lines are joined took it from the equation too; it is dropped by the
    /// inline scanner, at the line ending, the way pulldown-cmark drops it.
    /// </summary>
    [Fact]
    public void ADisplayEquationKeepsALinesTrailingSpace()
    {
        var doc = Extract("$$\na = \nb\n$$\n");
        var formula = doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Formula);
        Assert.Equal("a = \nb", formula.Text);
    }

    [Fact]
    public void ProseDropsALinesTrailingSpace()
    {
        // The other half of the same rule: a soft break still reads as one space, no matter how
        // much whitespace sat on either side of it.
        Assert.Equal("one two", PlainText("one \ntwo\n"));
    }

    /// <summary>
    /// Block-level raw HTML is one event per line, and each carries its line ending. Standing on
    /// its own the run becomes one raw block per line and the endings are trimmed back off, but
    /// inside a list item the lines are appended to the item's text — where the ending is the
    /// only thing keeping a multi-line tag from collapsing onto one line.
    /// </summary>
    [Fact]
    public void BlockHtmlInsideAListItemKeepsItsLineBreaks()
    {
        var doc = Extract("1. Intro:\n\n    <summary>\n      Line one\n    </summary>\n");
        var item = doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.ListItem);
        Assert.Equal("Intro: <summary>\n   Line one\n </summary>", item.Text);
    }
}
