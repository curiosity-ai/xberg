using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Ports the `attach_unrepresented_tables` / `inject_unrepresented_table_elements` tests from
/// crates/xberg/src/extractors/pdf/mod.rs.
/// </summary>
public class PdfTableInjectionTests
{
    private static Table MakeTable(params string[][] rows) => new()
    {
        Cells = rows.Select(r => new List<string>(r)).ToList(),
        Markdown = "| " + string.Join(" | ", rows[0]) + " |",
        PageNumber = 1,
        BoundingBox = null,
    };

    private static InternalDocument DocWithParagraphs(params string[] paragraphs)
    {
        var doc = new InternalDocument("pdf");
        foreach (var text in paragraphs)
            doc.PushElement(InternalElement.TextElement(ElementKind.Paragraph, text, 0));
        return doc;
    }

    private static int TableElementCount(InternalDocument doc) =>
        doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.Table);

    [Fact]
    public void FlatPlainTextRetainsTableAssetWithoutDuplicateRendering()
    {
        var doc = DocWithParagraphs("Account balance 42");
        PdfExtractor.AttachUnrepresentedTables(doc, new List<Table> { MakeTable(new[] { "Account balance", "42" }) });
        PdfExtractor.InjectUnrepresentedTableElements(doc, allowInjection: false);

        Assert.Single(doc.Tables);
        Assert.Equal(0, TableElementCount(doc));
    }

    [Fact]
    public void TableElementInjectionRemainsAvailableForStructuredOutput()
    {
        var doc = new InternalDocument("pdf");
        PdfExtractor.AttachUnrepresentedTables(doc, new List<Table> { MakeTable(new[] { "Heading", "Value" }) });
        PdfExtractor.InjectUnrepresentedTableElements(doc, allowInjection: true);

        Assert.Single(doc.Tables);
        Assert.Equal(1, TableElementCount(doc));
    }

    [Fact]
    public void OutputSkipsTablesAlreadyPresentInTheElementStream()
    {
        var doc = DocWithParagraphs(
            "Persons committed to the custody of a sheriff shall be confined\nin the facilities designated by law.");
        PdfExtractor.AttachUnrepresentedTables(doc, new List<Table>
        {
            MakeTable(
                new[] { "Persons committed to the custody", "of a sheriff" },
                new[] { "shall be confined in the facilities", "designated by law" }),
        });
        PdfExtractor.InjectUnrepresentedTableElements(doc, allowInjection: true);

        Assert.Single(doc.Tables);
        Assert.Equal(0, TableElementCount(doc));
    }

    [Fact]
    public void OutputStillInjectsTablesMissingFromTheElementStream()
    {
        var doc = DocWithParagraphs("Narrative prose that shares none of the tabulated content.");
        PdfExtractor.AttachUnrepresentedTables(doc, new List<Table>
        {
            MakeTable(
                new[] { "Region", "Revenue", "Growth" },
                new[] { "North", "1200", "4 percent" },
                new[] { "South", "980", "7 percent" }),
        });
        PdfExtractor.InjectUnrepresentedTableElements(doc, allowInjection: true);

        Assert.Equal(1, TableElementCount(doc));
    }

    /// <summary>The containment check abstains below the minimum token count, so a short table
    /// cannot be suppressed by an incidental token overlap.</summary>
    [Fact]
    public void ShortTablesAreInjectedEvenWhenTheirTokensAppearInTheText()
    {
        var doc = DocWithParagraphs("total 42 balance");
        PdfExtractor.AttachUnrepresentedTables(doc, new List<Table> { MakeTable(new[] { "Total", "42" }) });
        PdfExtractor.InjectUnrepresentedTableElements(doc, allowInjection: true);

        Assert.Equal(1, TableElementCount(doc));
    }

    [Fact]
    public void AttachLeavesAnExistingTableListAlone()
    {
        var doc = new InternalDocument("pdf");
        doc.PushTable(MakeTable(new[] { "Kept", "Table" }));
        PdfExtractor.AttachUnrepresentedTables(doc, new List<Table> { MakeTable(new[] { "Other", "Table" }) });

        Assert.Single(doc.Tables);
        Assert.Equal("Kept", doc.Tables[0].Cells[0][0]);
    }

    [Fact]
    public void TokensDropEdgePunctuationAndCase()
    {
        Assert.Equal(new[] { "region", "revenue", "4" }, PdfExtractor.NormalizedPdfTokens("(Region), Revenue: 4.").ToArray());
        Assert.Empty(PdfExtractor.NormalizedPdfTokens("--- ,"));
    }
}
