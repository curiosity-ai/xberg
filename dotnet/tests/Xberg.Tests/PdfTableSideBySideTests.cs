using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the side-by-side rule in the heuristic table finder: a region wide enough to hold
/// two tables is cut at its central gutter only when both halves stand up on their own.
/// </summary>
public class PdfTableSideBySideTests
{
    private const float PageHeight = 792f;

    private static SegmentData Seg(string text, float left, float topImage, float width) =>
        new()
        {
            Text = text,
            X = left,
            Y = PageHeight - topImage - 10f,
            Width = width,
            Height = 10f,
            FontSize = 10f,
            BaselineY = PageHeight - topImage - 10f,
        };

    /// <summary>A financial table: a header row, then eight rows of a descriptor followed by
    /// one value under each remaining track.</summary>
    private static void AddFinancialTable(List<SegmentData> page, float[] tracks, string prefix)
    {
        for (int column = 0; column < tracks.Length; column++)
            page.Add(Seg(column == 0 ? "Security" : "Value", tracks[column], 118f, 30f));

        for (int row = 0; row < 8; row++)
        {
            float top = 130f + row * 12f;
            page.Add(Seg($"{prefix}-bond-{row}", tracks[0], top, 60f));
            for (int column = 1; column < tracks.Length; column++)
                page.Add(Seg($"{row + 1},{column * 113}", tracks[column], top, 30f));
        }
    }

    /// <summary>Two financial tables printed next to each other reconstruct as one seven-column
    /// grid whose rows interleave both; the central gutter is what separates them.</summary>
    [Fact]
    public void TwoTablesPrintedSideBySideAreSplitAtTheirGutter()
    {
        var page = new List<SegmentData>();
        AddFinancialTable(page, new[] { 20f, 140f, 220f }, "Cayman");
        AddFinancialTable(page, new[] { 320f, 400f, 480f, 560f }, "Ireland");

        var tables = PdfTableReconstruct.ExtractHeuristicTables(
            new List<List<SegmentData>> { page }, allowSingleColumn: false);

        Assert.Equal(2, tables.Count);
        Assert.Equal(3, tables[0].Cells[0].Count);
        Assert.Equal(4, tables[1].Cells[0].Count);
        Assert.All(tables[0].Cells, row => Assert.DoesNotContain(row, cell => cell.Contains("Ireland")));
        Assert.All(tables[1].Cells, row => Assert.DoesNotContain(row, cell => cell.Contains("Cayman")));
    }

    /// <summary>One table whose columns happen to be numerous is not two tables: no gap near
    /// its midpoint is wide enough to be a gutter, so the grid survives intact.</summary>
    [Fact]
    public void AWideSingleTableIsLeftIntact()
    {
        var page = new List<SegmentData>();
        var tracks = new[] { 20f, 100f, 160f, 220f, 280f, 340f, 400f };
        for (int column = 0; column < tracks.Length; column++)
            page.Add(Seg(column == 0 ? "Security" : $"Value{column}", tracks[column], 118f, 30f));
        for (int row = 0; row < 8; row++)
        {
            float top = 130f + row * 12f;
            page.Add(Seg($"Cayman-bond-{row}", tracks[0], top, 60f));
            for (int column = 1; column < tracks.Length; column++)
                page.Add(Seg($"{row + 1},{column * 113}", tracks[column], top, 30f));
        }

        var tables = PdfTableReconstruct.ExtractHeuristicTables(
            new List<List<SegmentData>> { page }, allowSingleColumn: false);

        var table = Assert.Single(tables);
        Assert.Equal(tracks.Length, table.Cells[0].Count);
    }
}
