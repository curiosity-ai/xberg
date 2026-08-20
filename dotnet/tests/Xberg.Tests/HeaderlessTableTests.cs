using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Whether a table's first row is its header, for the formats where that is not given.
/// </summary>
public class HeaderlessTableTests
{
    private static InternalDocument Extract(string text, string mime) =>
        new StructuredExtractor().Extract(Encoding.UTF8.GetBytes(text), mime, new ExtractionConfig());

    private static string Markdown(InternalDocument doc) => Xberg.Rendering.MarkdownRenderer.Render(doc);

    private static InternalDocument Csv(string text) =>
        new CsvExtractor().Extract(Encoding.UTF8.GetBytes(text), "text/csv", new ExtractionConfig());

    private static InternalDocument Org(string text) =>
        new OrgExtractor().Extract(Encoding.UTF8.GetBytes(text), "text/x-org", new ExtractionConfig());

    [Fact]
    public void ACsvWhoseFirstRowIsDataGetsAnEmptyHeader()
    {
        // Promoting a numeric first row would relabel that record as the column names, and a GFM
        // table has no headerless form, so the header is emitted empty.
        string md = Markdown(Csv("1,2,3\na,b,c\n"));
        Assert.Contains("|  |  |  |\n| --- | --- | --- |\n| 1 | 2 | 3 |", md);
    }

    [Fact]
    public void ACsvWithATextFirstRowKeepsItAsTheHeader()
    {
        string md = Markdown(Csv("Name,City\nAlice,NYC\n"));
        Assert.Contains("| Name | City |\n| --- | --- |\n| Alice | NYC |", md);
    }

    [Fact]
    public void AnOrgTableHasAHeaderOnlyWhereTheSourceDrewARuleUnderIt()
    {
        var withRule = Org("| Name | Age |\n|------+-----|\n| Ada | 36 |\n");
        Assert.Equal(new[] { "Name", "Age" }, Assert.Single(withRule.Tables).Columns);

        var withoutRule = Org("| Ada | 36 |\n| Alan | 41 |\n");
        Assert.Null(Assert.Single(withoutRule.Tables).Columns);
    }

    [Fact]
    public void AnOrgTableIsEmittedOnce()
    {
        // Tables are parsed in place while the document is built; a second raw pass reported
        // every one of them twice.
        Assert.Single(Org("| Name | Age |\n|------+-----|\n| Ada | 36 |\n").Tables);
    }

    [Fact]
    public void ARuleBeforeAnyRowIsDecorationNotAHeaderSeparator()
    {
        Assert.Null(Assert.Single(Org("|------+-----|\n| Ada | 36 |\n").Tables).Columns);
    }
}
