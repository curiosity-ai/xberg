using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the label-heavy financial path of the heuristic table finder: a statement whose
/// rows are a descriptor followed by figures on fixed tracks, printed as several sections
/// that vertical clustering hands over as separate regions.
/// </summary>
public class PdfTableFinancialTests
{
    private const float LabelX = 60f;
    private static readonly float[] ValueX = { 250f, 330f, 410f, 490f };

    private static SegmentData Seg(string text, float x, float y, float width) =>
        new() { Text = text, X = x, Y = y, Width = width, Height = 10f, FontSize = 10f, BaselineY = y };

    /// <summary>A descriptor at the left margin plus four figures parked on the value tracks.</summary>
    private static void AddValueRow(List<SegmentData> page, string first, string second, float y, string? lastValue = null)
    {
        page.Add(Seg(first, LabelX, y, 45f));
        page.Add(Seg(second, LabelX + 50f, y, 40f));
        for (int column = 0; column < 4; column++)
        {
            string text = column == 3 && lastValue is not null ? lastValue : $"{column + 1},{column}0{column}.5";
            page.Add(Seg(text, ValueX[column], y, 40f));
        }
    }

    /// <summary>A heading row: words only, all of them left of the first value track.</summary>
    private static void AddSectionLabel(List<SegmentData> page, float y)
    {
        page.Add(Seg("Deferred", LabelX, y, 45f));
        page.Add(Seg("taxes", LabelX + 52f, y, 35f));
    }

    /// <summary>First section: six rows starting at y=700, one line apart.</summary>
    private static void AddFirstSection(List<SegmentData> page)
    {
        string[] first = { "Interest", "Trading", "Fee", "Impairment", "Operating", "Profit" };
        string[] second = { "income", "revenue", "expense", "charges", "costs", "before" };
        for (int row = 0; row < 6; row++) AddValueRow(page, first[row], second[row], 700f - row * 14f);
    }

    /// <summary>Second section: a heading row at <paramref name="topY"/> plus five value rows.</summary>
    private static void AddSecondSection(List<SegmentData> page, float topY, bool withLabel)
    {
        if (withLabel) AddSectionLabel(page, topY);
        else AddValueRow(page, "Deferred", "taxes", topY);
        string[] first = { "Current", "Prior", "Movement", "Closing", "Net" };
        string[] second = { "year", "years", "recognised", "balance", "position" };
        for (int row = 0; row < 5; row++)
            AddValueRow(page, first[row], second[row], topY - (row + 1) * 14f, row == 4 ? "?" : null);
    }

    private static List<Xberg.Types.Table> Run(List<SegmentData> page) =>
        PdfTableReconstruct.ExtractHeuristicTables(
            new List<List<SegmentData>> { page }, allowSingleColumn: false);

    /// <summary>Two sections of one statement, a blank line apart, come back as the single
    /// table they print as — descriptor column plus one column per value track.</summary>
    [Fact]
    public void ConsecutiveStatementSectionsAreStitchedIntoOneTable()
    {
        var page = new List<SegmentData>();
        AddFirstSection(page);
        AddSecondSection(page, 600f, withLabel: true);

        var table = Assert.Single(Run(page));
        Assert.Equal(12, table.Cells.Count);
        Assert.All(table.Cells, row => Assert.Equal(5, row.Count));
        Assert.Equal("Interest income", table.Cells[0][0]);
        Assert.Equal("1,000.5", table.Cells[0][1]);
        // The heading row keeps its words in the descriptor column instead of spilling right.
        Assert.Equal(new[] { "Deferred taxes", "", "", "", "" }, table.Cells[6]);
        // A value cell that arrived as a lone "?" is an unmapped dash glyph.
        Assert.Equal("—", table.Cells[11][4]);
    }

    /// <summary>A section too far below the one above it is a different table, however well
    /// its columns happen to line up.</summary>
    [Fact]
    public void ASectionSeparatedByMoreThanABlankLineIsNotStitched()
    {
        var page = new List<SegmentData>();
        AddFirstSection(page);
        AddSecondSection(page, 545f, withLabel: true);

        Assert.DoesNotContain(Run(page), table => table.Cells.Count > 6);
    }

    /// <summary>Without a heading row, a following block is a continuation of nothing — the
    /// section label is what identifies it as part of the statement above.</summary>
    [Fact]
    public void ASectionThatDoesNotOpenWithALabelIsNotStitched()
    {
        var page = new List<SegmentData>();
        AddFirstSection(page);
        AddSecondSection(page, 600f, withLabel: false);

        Assert.DoesNotContain(Run(page), table => table.Cells.Count > 6);
    }
}
