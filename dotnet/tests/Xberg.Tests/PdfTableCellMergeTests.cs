using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Grid reconstruction from positioned words, upstream <c>fix(tables): merge multi-word cells
/// before detecting columns</c> and <c>fix(tables): keep grid and column positions in lockstep
/// through a repair</c> (#688).
/// </summary>
public sealed class PdfTableCellMergeTests
{
    /// <summary>A word box; heights are uniform so the median is unambiguous.</summary>
    private static HocrWord Word(string text, uint left, uint top, uint width) =>
        new() { Text = text, Left = left, Top = top, Width = width, Height = 10, Confidence = 1.0 };

    /// <summary>
    /// Column detection groups words by their left edge, so every word after the first in a
    /// multi-word cell landed at an x position matching no column start and minted a spurious,
    /// near-empty column. Those columns are exactly what the downstream structural validator
    /// rejects, so a genuine table was discarded whole.
    /// </summary>
    [Fact]
    public void AMultiWordCellDoesNotMintAColumnPerExtraWord()
    {
        // Two columns: a multi-word name at x=10, a number at x=200. The name's second and third
        // words sit at x=45 and x=75 — close enough to merge (gap ≤ 0.6 × median height).
        var words = new List<HocrWord>();
        for (uint row = 0; row < 4; row++)
        {
            uint top = 10 + row * 20;
            words.Add(Word("ABX", 10, top, 30));
            words.Add(Word("Air", 45, top, 25));
            words.Add(Word("Inc", 74, top, 25));
            words.Add(Word($"1{row}.50", 200, top, 40));
        }

        var (grid, columnPositions) = PdfTableReconstruct.ReconstructTableWithColumns(words, 20, 0.5);

        Assert.All(grid, row => Assert.Equal(2, row.Count));
        Assert.Equal(2, columnPositions.Count);
        Assert.Equal("ABX Air Inc", grid[0][0]);
        Assert.Equal("10.50", grid[0][1]);
    }

    /// <summary>
    /// A gap wider than the merge threshold is a real column boundary, not a cell that happens to
    /// hold two words, and must still be detected as one.
    /// </summary>
    [Fact]
    public void AWideGapStillSeparatesTwoColumns()
    {
        var words = new List<HocrWord>();
        for (uint row = 0; row < 4; row++)
        {
            uint top = 10 + row * 20;
            words.Add(Word("Left", 10, top, 30));
            words.Add(Word("Right", 200, top, 35));
        }

        var (grid, columnPositions) = PdfTableReconstruct.ReconstructTableWithColumns(words, 20, 0.5);

        Assert.Equal(2, columnPositions.Count);
        Assert.Equal(new[] { "Left", "Right" }, grid[0]);
    }

    /// <summary>
    /// The positions returned describe the grid returned: they are filtered by the same
    /// empty-column mask, so index <c>i</c> of one names index <c>i</c> of the other.
    /// </summary>
    [Fact]
    public void TheReturnedPositionsMatchTheReturnedGridColumnForColumn()
    {
        var words = new List<HocrWord>();
        for (uint row = 0; row < 3; row++)
        {
            uint top = 10 + row * 20;
            words.Add(Word("A", 10, top, 15));
            words.Add(Word("B", 200, top, 15));
            words.Add(Word("C", 400, top, 15));
        }

        var (grid, columnPositions) = PdfTableReconstruct.ReconstructTableWithColumns(words, 20, 0.5);

        Assert.NotEmpty(grid);
        Assert.All(grid, row => Assert.Equal(columnPositions.Count, row.Count));
    }

    [Fact]
    public void NoWordsYieldsAnEmptyGridAndNoPositions()
    {
        var (grid, columnPositions) = PdfTableReconstruct.ReconstructTableWithColumns(new List<HocrWord>(), 20, 0.5);
        Assert.Empty(grid);
        Assert.Empty(columnPositions);
    }
}
