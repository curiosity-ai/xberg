using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// A list item's literal source marker, upstream <c>fix(pdf): keep a list item's own marker and
/// stop splitting quantities</c>.
/// </summary>
/// <remarks>
/// The PDF pipeline strips the marker off the item text so the text reads as content, then used to
/// throw the marker away, leaving renderers to synthesize a position. On a document whose clauses
/// are cross-referenced by their printed label that renumbers the text: "B." rendered as "1.",
/// "(a)" as "1.".
/// </remarks>
public sealed class ListSourceLabelTests
{
    private static InternalDocument LabelledItem(string label, string text, bool ordered = true)
    {
        var b = new InternalDocumentBuilder("pdf");
        b.PushList(ordered);
        uint idx = b.PushListItem(text, ordered, new(), null, null);
        b.SetListItemSourceLabel(idx, label);
        b.PushListItem("Second item", ordered, new(), null, null);
        b.EndList();
        return b.Build();
    }

    [Fact]
    public void AnEmptyLabelIsIgnoredRatherThanStored()
    {
        // An empty label is a caller bug — a marker-strip that removed nothing — not a real marker.
        var element = new InternalElement { Kind = ElementKind.ListItem(true), Text = "item", Annotations = new() };
        element.SetListItemSourceLabel("");
        Assert.Null(element.ListItemSourceLabel);
        Assert.Null(element.Attributes);
    }

    [Fact]
    public void TheLabelRoundTripsThroughTheAttributeBag()
    {
        var element = new InternalElement { Kind = ElementKind.ListItem(true), Text = "item", Annotations = new() };
        element.SetListItemSourceLabel("B.");
        Assert.Equal("B.", element.ListItemSourceLabel);
        Assert.Equal("B.", element.Attributes!["list_marker"]);
    }

    /// <summary>
    /// CommonMark's ordered marker is an auto-incrementing decimal and cannot express "B.", so the
    /// item renders as a bullet with the label as leading text. Both halves are non-negotiable: the
    /// label survives, and no synthesized ordinal appears beside it.
    /// </summary>
    [Fact]
    public void MarkdownWritesTheLabelAndSynthesizesNoOrdinalBesideIt()
    {
        string markdown = MarkdownRenderer.Render(
            LabelledItem("B.", "General Provisions, Definitions, and Exhibits."));

        Assert.Contains("- B. General Provisions, Definitions, and Exhibits.", markdown);
        Assert.DoesNotContain("1.", markdown);
    }

    [Fact]
    public void PlainKeepsTheLabelAsVisibleText()
    {
        Assert.Contains("B. General Provisions.", PlainRenderer.Render(LabelledItem("B.", "General Provisions.")));
    }

    [Fact]
    public void DjotFallsBackToABulletCarryingTheLabel()
    {
        string djot = DjotRenderer.Render(LabelledItem("B.", "General Provisions."));
        Assert.Contains("- B. General Provisions.", djot);
    }

    [Fact]
    public void JsonPrefixesTheLabelOntoTheItemString()
    {
        Assert.Contains("B. General Provisions.", JsonRenderer.Render(LabelledItem("B.", "General Provisions.")));
    }

    [Fact]
    public void StyledHtmlWritesTheLabelInItsOwnSpan()
    {
        string html = new StyledHtmlRenderer(new Xberg.Core.HtmlOutputConfig())
            .Render(LabelledItem("(a)", "General Provisions."));
        Assert.Contains("list-marker\">(a)</span>", html);
    }

    /// <summary>
    /// "Two (2) additional on-street parking spaces" wrapping onto a new line makes
    /// "(2) additional …" look like a marker plus body, splitting the sentence into a list item
    /// mid-clause. A parenthesized numeric marker followed by a space and a lowercase word is
    /// prose.
    /// </summary>
    [Theory]
    [InlineData("(2) additional on-street parallel parking spaces")]
    [InlineData("(7) on-street spaces along the frontage")]
    public void AParenthesizedQuantityClarificationIsNotAListItem(string line) =>
        Assert.False(Xberg.Internal.Pdf.PdfStructure.LooksLikeListItem(line));

    /// <summary>
    /// Lettered sub-items in parentheses are genuine markers — nobody writes "two (b) items" — and
    /// a capitalized numeric one is a new sentence, so both survive the heuristic above.
    /// </summary>
    [Theory]
    [InlineData("(a) General Provisions.")]
    [InlineData("(b) further requirements apply")]
    [InlineData("(2) Second point.")]
    public void GenuineParenthesizedMarkersRemainListItems(string line) =>
        Assert.True(Xberg.Internal.Pdf.PdfStructure.LooksLikeListItem(line));

    [Fact]
    public void AnItemWithNoLabelStillGetsTheSynthesizedOrdinal()
    {
        var b = new InternalDocumentBuilder("pdf");
        b.PushList(true);
        b.PushListItem("First item", true, new(), null, null);
        b.EndList();

        Assert.Contains("1. First item", MarkdownRenderer.Render(b.Build()));
    }
}
