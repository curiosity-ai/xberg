using Xberg.Extractors;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Ports the `contains_html_markup` / `apply_text_cleanup` rules from
/// crates/xberg/src/pdf/text.rs and crates/xberg/src/pdf/oxide/text.rs.
/// </summary>
public class PdfPageTextCleanupTests
{
    [Theory]
    [InlineData("<p>", true)]
    [InlineData("</p>", true)]
    [InlineData("<br/>", true)]
    [InlineData("<div class=x", true)]
    [InlineData("<span", true)]
    [InlineData("<table", true)]
    [InlineData("<a href", true)]
    [InlineData("<img src=x />", true)]
    // The `/>` test only counts once an opening angle bracket is present at all.
    [InlineData("closing slash-gt />", false)]
    [InlineData("a < b and c > d", false)]
    [InlineData("no angle brackets at all", false)]
    [InlineData("<h1>heading</h1", false)]
    public void MarkupDetectionMatchesTheTagSet(string text, bool expected) =>
        Assert.Equal(expected, PdfExtractor.ContainsHtmlMarkup(text));

    [Fact]
    public void PageTextCarryingMarkupIsConvertedRatherThanUsedVerbatim()
    {
        string cleaned = PdfExtractor.ApplyTextCleanup("<p>First block</p><p>Second block</p>");
        Assert.DoesNotContain("<p>", cleaned);
        Assert.Contains("First block", cleaned);
        Assert.Contains("Second block", cleaned);
    }

    [Fact]
    public void PlainPageTextIsLeftAlone()
    {
        const string text = "Ordinary page text with a < b comparison.";
        Assert.Equal(text, PdfExtractor.ApplyTextCleanup(text));
    }
}

/// <summary>
/// Ports `append_missing_widget_values` from crates/xberg/src/pdf/oxide/text.rs.
/// </summary>
public class PdfWidgetValueTests
{
    [Fact]
    public void AFlattenedValueAlreadyInThePageTextIsNotAppendedAgain() =>
        Assert.Equal("Name: Jane Doe",
            PdfExtractor.AppendMissingWidgetValues("Name: Jane Doe", new List<string> { "Jane Doe" }));

    [Fact]
    public void AnInteractiveValueMissingFromThePageTextIsAppendedOnItsOwnLine() =>
        Assert.Equal("Name:\nJane Doe",
            PdfExtractor.AppendMissingWidgetValues("Name:", new List<string> { "Jane Doe" }));

    [Fact]
    public void EmptyPageTextTakesTheValueWithoutALeadingNewline() =>
        Assert.Equal("Jane Doe",
            PdfExtractor.AppendMissingWidgetValues("", new List<string> { "Jane Doe" }));

    [Fact]
    public void AValueAppendedOnceSuppressesItsOwnRepeat() =>
        Assert.Equal("Name:\nJane Doe",
            PdfExtractor.AppendMissingWidgetValues("Name:", new List<string> { "Jane Doe", "Jane Doe" }));
}
