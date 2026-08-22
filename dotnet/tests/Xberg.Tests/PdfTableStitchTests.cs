using Xberg.Internal.Pdf;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers `stitch_fragmented_tables` (crates/xberg/src/pdf/structure/pipeline.rs:2553-2713):
/// the pass that joins the fragments row-gap region clustering split out of one physical
/// table back into a single table on the way into the structure pipeline.
/// </summary>
public class PdfTableStitchTests
{
    private static Table Fragment(uint page, double x0, double y0, double x1, double y1, params string[][] rows)
    {
        var cells = new List<List<string>>();
        foreach (var row in rows) cells.Add(new List<string>(row));
        return new Table
        {
            Cells = cells,
            Markdown = PdfTableReconstruct.TableToMarkdown(cells),
            PageNumber = page,
            BoundingBox = new BoundingBox { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 },
        };
    }

    private static readonly List<List<SegmentData>> NoSegments = new() { new List<SegmentData>() };

    [Fact]
    public void TwoVerticallyAdjacentFragmentsCollapseIntoOneTable()
    {
        // The upper fragment's two sub-lines are one logical header row; the lower
        // fragment's two sub-lines are one logical data row.
        var upper = Fragment(1, 100, 200, 300, 240,
            new[] { "A", "", "C" },
            new[] { "", "B", "" });
        var lower = Fragment(1, 100, 160, 300, 198,
            new[] { "1", "2", "" },
            new[] { "", "", "3" });

        var stitched = PdfTableStitch.StitchFragmentedTables(new List<Table> { upper, lower }, NoSegments);

        var table = Assert.Single(stitched);
        Assert.Equal(new List<List<string>>
        {
            new() { "A", "B", "C" },
            new() { "1", "2", "3" },
        }, table.Cells);
        Assert.Equal(new List<string> { "A", "B", "C" }, table.Columns);
        Assert.Equal("| A | B | C |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n", table.Markdown);
    }

    [Fact]
    public void AStitchedChainsBoundingBoxIsTheUnionOfItsFragments()
    {
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 98, 160, 305, 198, new[] { "1", "2" });

        var table = Assert.Single(PdfTableStitch.StitchFragmentedTables(
            new List<Table> { upper, lower }, NoSegments));

        Assert.NotNull(table.BoundingBox);
        Assert.Equal(98, table.BoundingBox!.X0);
        Assert.Equal(160, table.BoundingBox.Y0);
        Assert.Equal(305, table.BoundingBox.X1);
        Assert.Equal(240, table.BoundingBox.Y1);
    }

    [Fact]
    public void FragmentsOrderTopmostFirstRegardlessOfInputOrder()
    {
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 100, 160, 300, 198, new[] { "1", "2" });

        var table = Assert.Single(PdfTableStitch.StitchFragmentedTables(
            new List<Table> { lower, upper }, NoSegments));

        Assert.Equal(new List<string> { "A", "B" }, table.Cells[0]);
        Assert.Equal(new List<string> { "1", "2" }, table.Cells[1]);
    }

    [Fact]
    public void AWideRowGapLeavesTheFragmentsSeparate()
    {
        // 12 pt of clear space is far beyond the 4 pt continuation tolerance.
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 100, 150, 300, 188, new[] { "1", "2" });

        Assert.Equal(2, PdfTableStitch.StitchFragmentedTables(
            new List<Table> { upper, lower }, NoSegments).Count);
    }

    [Fact]
    public void MisalignedEdgesLeaveTheFragmentsSeparate()
    {
        // The left edges differ by 20 pt, past the 6 pt shared-edge tolerance: two
        // unrelated tables that happen to sit close together vertically.
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 120, 160, 300, 198, new[] { "1", "2" });

        Assert.Equal(2, PdfTableStitch.StitchFragmentedTables(
            new List<Table> { upper, lower }, NoSegments).Count);
    }

    [Fact]
    public void DifferentColumnCountsLeaveTheFragmentsSeparate()
    {
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 100, 160, 300, 198, new[] { "1", "2", "3" });

        Assert.Equal(2, PdfTableStitch.StitchFragmentedTables(
            new List<Table> { upper, lower }, NoSegments).Count);
    }

    [Fact]
    public void FragmentsOnDifferentPagesAreNeverStitched()
    {
        var first = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var second = Fragment(2, 100, 160, 300, 198, new[] { "1", "2" });

        var stitched = PdfTableStitch.StitchFragmentedTables(
            new List<Table> { first, second }, new List<List<SegmentData>> { new(), new() });

        Assert.Equal(2, stitched.Count);
    }

    [Fact]
    public void AFragmentWithoutABoundingBoxPassesThroughAheadOfThePagedOnes()
    {
        var unbboxed = new Table
        {
            Cells = new List<List<string>> { new() { "x" } },
            Markdown = "| x |\n| --- |\n",
            PageNumber = 1,
        };
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 100, 160, 300, 198, new[] { "1", "2" });

        var stitched = PdfTableStitch.StitchFragmentedTables(
            new List<Table> { upper, unbboxed, lower }, NoSegments);

        Assert.Equal(2, stitched.Count);
        Assert.Null(stitched[0].BoundingBox);
        Assert.Equal(2, stitched[1].Cells.Count);
    }

    [Fact]
    public void ColumnwiseMergeJoinsEveryNonEmptyCellInRowOrder()
    {
        var merged = PdfTableStitch.MergeRowsColumnwise(new List<List<string>>
        {
            new() { "one", "  ", "x" },
            new() { "two", "b", "" },
        }, 3);

        Assert.Equal(new List<string> { "one two", "b", "x" }, merged);
    }

    [Fact]
    public void ColumnwiseMergeTruncatesRowsWiderThanTheColumnCount()
    {
        var merged = PdfTableStitch.MergeRowsColumnwise(new List<List<string>>
        {
            new() { "a", "b", "c", "d" },
        }, 2);

        Assert.Equal(new List<string> { "a", "b" }, merged);
    }

    [Fact]
    public void ATrailingRowThatNeverBecameAFragmentIsRecoveredFromThePageSegments()
    {
        // Two fragments stitch into header + one data row; a third band of words sits
        // just below the chain, inside its column span, and is pulled in as a data row.
        var upper = Fragment(1, 100, 200, 300, 240, new[] { "A", "B" });
        var lower = Fragment(1, 100, 160, 300, 198, new[] { "1", "2" });

        var segments = new List<SegmentData>
        {
            new() { Text = "3", X = 100, Y = 140, Width = 20, Height = 10, FontSize = 10, BaselineY = 140 },
            new() { Text = "4", X = 260, Y = 140, Width = 20, Height = 10, FontSize = 10, BaselineY = 140 },
        };

        var table = Assert.Single(PdfTableStitch.StitchFragmentedTables(
            new List<Table> { upper, lower }, new List<List<SegmentData>> { segments }));

        Assert.Equal(3, table.Cells.Count);
        Assert.Equal(new List<string> { "3", "4" }, table.Cells[2]);
        // The recovered band's bottom edge becomes the chain's new floor.
        Assert.Equal(140.0, table.BoundingBox!.Y0);
    }
}
