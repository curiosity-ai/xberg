using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the cluster fallback of the ruling-line tier — the path
/// `detect_tables_with_lines` takes when no cell could be built from rule
/// intersections, so the grid comes from clustering the rules' own coordinates.
/// </summary>
public class PdfSpatialClusterTests
{
    private static PdfPath Line(double x1, double y1, double x2, double y2, double width = 0.75)
    {
        var ops = new List<PathOp> { PathOp.MoveTo(x1, y1), PathOp.LineTo(x2, y2) };
        return new PdfPath { Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Stroked = true, StrokeWidth = width };
    }

    private static TableSpan Word(string text, double centerX, double centerY, double w = 40, double h = 10) =>
        new() { Text = text, Bbox = new PathRect(centerX - w / 2, centerY - h / 2, w, h), FontSize = 10 };

    /// <summary>H rules at each y in <paramref name="ys"/>, spanning x0..x1.</summary>
    private static IEnumerable<PdfPath> HRules(double x0, double x1, params double[] ys) =>
        ys.Select(y => Line(x0, y, x1, y));

    /// <summary>V rules at each x in <paramref name="xs"/>, spanning y0..y1.</summary>
    private static IEnumerable<PdfPath> VRules(double y0, double y1, params double[] xs) =>
        xs.Select(x => Line(x, y0, x, y1));

    /// <summary>A 3-column, 2-row ruled box: H rules at 700/670/640, V rules at 100..400.</summary>
    private static List<PdfPath> ThreeByTwoGrid()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640));
        paths.AddRange(VRules(640, 700, 100, 200, 300, 400));
        return paths;
    }

    private static List<TableSpan> ThreeByTwoWords() => new()
    {
        Word("Item", 150, 685), Word("Qty", 250, 685), Word("Cost", 350, 685),
        // The price gets a box its four glyphs can account for. A decimal drawn far
        // wider than its digits is read as two values straddling a column boundary and
        // split at the dot, which is a different rule than the one under test here —
        // `AColumnSpanningDecimalIsSplitAtItsDot` covers that one.
        Word("Bolt", 150, 655), Word("12", 250, 655), Word("4.50", 350, 655, w: 20),
    };

    /// <summary>
    /// A run reading `N.M` whose box is far wider than its digits can account for is
    /// two values in adjacent columns, not a decimal, and is split at the dot
    /// (`table_extractor.rs:515`). The sailing-score sheets this comes from draw
    /// `1` and `10` as one positioned run straddling the boundary between two columns.
    /// </summary>
    [Fact]
    public void AColumnSpanningDecimalIsSplitAtItsDot()
    {
        var words = new List<TableSpan>
        {
            Word("Item", 150, 685), Word("Qty", 250, 685), Word("Cost", 350, 685),
            Word("Bolt", 150, 655), Word("12", 250, 655), Word("4.50", 350, 655, w: 40),
        };

        var table = Assert.Single(PdfSpatialTables.DetectTablesInClusters(
            words, ThreeByTwoGrid(), TableDetectionConfig.Strict()));

        Assert.Equal("4 50", table.Rows[1].Cells[2].Text);
    }

    [Fact]
    public void ClusteredRulesBecomeARowAndColumnGrid()
    {
        var tables = PdfSpatialTables.DetectTablesInClusters(
            ThreeByTwoWords(), ThreeByTwoGrid(), TableDetectionConfig.Strict());

        var table = Assert.Single(tables);
        Assert.Equal(3, table.ColCount);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "Item", "Qty", "Cost" }, table.Rows[0].Cells.Select(c => c.Text));
        Assert.Equal(new[] { "Bolt", "12", "4.50" }, table.Rows[1].Cells.Select(c => c.Text));
    }

    [Fact]
    public void AStrokeWidthEncodedColumnRuleStillBoundsItsColumn()
    {
        // A 1 pt horizontal segment stroked 60 pt wide paints a 60 pt tall bar: the
        // column boundary is where the bar is, not where the geometry is.
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640));
        paths.AddRange(VRules(640, 700, 100, 300, 400));
        paths.Add(Line(200, 670, 201, 670, width: 60));

        var tables = PdfSpatialTables.DetectTablesInClusters(
            ThreeByTwoWords(), paths, TableDetectionConfig.Strict());

        var table = Assert.Single(tables);
        Assert.Equal(3, table.ColCount);
        // The bar reads as a separator across both row bands, so nothing colspan-merges.
        Assert.Equal(3, table.Rows[0].Cells.Count);
        Assert.Equal(3, table.Rows[1].Cells.Count);
    }

    [Fact]
    public void ARuleTooShortToBeABoundaryIsIgnored()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640));
        paths.AddRange(VRules(640, 700, 100, 200, 300, 400));
        // A 4 pt tick between two columns is decoration, not a fourth boundary.
        paths.Add(Line(250, 660, 250, 664));

        var tables = PdfSpatialTables.DetectTablesInClusters(
            ThreeByTwoWords(), paths, TableDetectionConfig.Strict());

        Assert.Equal(3, Assert.Single(tables).ColCount);
    }

    [Fact]
    public void VerticalRulesInDisjointYBandsSplitTheCluster()
    {
        // Two bordered blocks whose rules nearly touch: without the Y-band split they
        // would share one cluster, and the lower block's single interior rule would
        // become a fourth column of the upper one.
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 645));
        paths.AddRange(VRules(645, 700, 100, 200, 300, 400));
        paths.AddRange(HRules(100, 400, 640, 620, 600));
        paths.AddRange(VRules(600, 640, 100, 250, 400));

        var spans = new List<TableSpan>
        {
            Word("A", 150, 685), Word("B", 250, 685), Word("C", 350, 685),
            Word("D", 150, 657), Word("E", 250, 657), Word("F", 350, 657),
            Word("G", 175, 630), Word("H", 325, 630),
            Word("I", 175, 610), Word("J", 325, 610),
        };

        var tables = PdfSpatialTables.DetectTablesInClusters(
            spans, paths, TableDetectionConfig.Bordered());

        Assert.Equal(2, tables.Count);
        Assert.Equal(new[] { 2, 3 }, tables.Select(t => t.ColCount).OrderBy(n => n));
    }

    [Fact]
    public void AnIrregularGridClearsTheRelaxedRowRatioButNotTheStrictOne()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640, 610, 580));
        paths.AddRange(VRules(580, 700, 100, 200, 300, 400));

        // Row fills 3, 3, 1, 2: the modal shape covers half the rows, which clears
        // `regular_row_ratio` 0.5 but not 0.8.
        var spans = new List<TableSpan>
        {
            Word("Item", 150, 685), Word("Qty", 250, 685), Word("Cost", 350, 685),
            Word("Bolt", 150, 655), Word("12", 250, 655), Word("4.50", 350, 655),
            Word("Subtotal", 150, 625),
            Word("Total", 150, 595), Word("54.00", 350, 595),
        };

        Assert.Empty(PdfSpatialTables.DetectTablesInClusters(spans, paths, TableDetectionConfig.Strict()));
        Assert.Single(PdfSpatialTables.DetectTablesInClusters(spans, paths, TableDetectionConfig.Bordered()));
    }

    [Fact]
    public void AHeaderRowAboveTheTopRulingJoinsTheGrid()
    {
        var paths = ThreeByTwoGrid();
        var withHeader = ThreeByTwoWords();
        withHeader.AddRange(new[] { Word("Part", 150, 710), Word("Count", 250, 710), Word("Price", 350, 710) });

        var baseline = PdfSpatialTables.DetectTablesInClusters(
            ThreeByTwoWords(), paths, TableDetectionConfig.Strict());
        Assert.Equal(2, Assert.Single(baseline).Rows.Count);

        var table = Assert.Single(PdfSpatialTables.DetectTablesInClusters(
            withHeader, paths, TableDetectionConfig.Strict()));
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.HasHeader);
        // The reconstructed row keeps its three cells: with no vertical rules in the
        // strip above the grid they would otherwise colspan-merge into one.
        Assert.Equal(new[] { "Part", "Count", "Price" }, table.Rows[0].Cells.Select(c => c.Text));
    }

    [Fact]
    public void ACentredTitleAboveTheGridIsNotMistakenForAHeaderRow()
    {
        var paths = ThreeByTwoGrid();
        var spans = ThreeByTwoWords();
        // One clustered phrase: it hits a single column and spans well under half the
        // column extent, so the header gate rejects it.
        spans.AddRange(new[] { Word("Parts", 240, 710, w: 30), Word("list", 270, 710, w: 25) });

        var table = Assert.Single(PdfSpatialTables.DetectTablesInClusters(
            spans, paths, TableDetectionConfig.Strict()));
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void ARowBandWithNoVerticalRuleColspanMergesIntoOneCell()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640));
        paths.AddRange(VRules(640, 700, 100, 400));
        // The interior rules stop below the top row band, so that band has no separators.
        paths.AddRange(VRules(640, 665, 200, 300));

        var tables = PdfSpatialTables.DetectTablesInClusters(
            ThreeByTwoWords(), paths, TableDetectionConfig.Strict());

        var table = Assert.Single(tables);
        Assert.Equal(3, table.ColCount);
        Assert.Single(table.Rows[0].Cells);
        Assert.Equal(3, table.Rows[1].Cells.Count);
    }

    [Fact]
    public void AnEmptyRowBandSplitsTheClusterIntoTwoTables()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640, 610, 580, 550));
        paths.AddRange(VRules(550, 700, 100, 200, 300, 400));

        // Rows 0-1 and 3-4 carry text; the 640..610 band is blank.
        var spans = new List<TableSpan>
        {
            Word("Item", 150, 685), Word("Qty", 250, 685), Word("Cost", 350, 685),
            Word("Bolt", 150, 655), Word("12", 250, 655), Word("4.50", 350, 655),
            Word("Item", 150, 595), Word("Qty", 250, 595), Word("Cost", 350, 595),
            Word("Nut", 150, 565), Word("30", 250, 565), Word("1.20", 350, 565),
        };

        var tables = PdfSpatialTables.DetectTablesInClusters(spans, paths, TableDetectionConfig.Strict());

        Assert.Equal(2, tables.Count);
        Assert.All(tables, t => Assert.Equal(2, t.Rows.Count));
    }

    [Fact]
    public void AnEmptyOuterColumnIsTrimmedAwayFromTheGrid()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 400, 700, 670, 640));
        paths.AddRange(VRules(640, 700, 100, 200, 300, 400));

        // Nothing sits in the 100..200 column, so the emitted table is 2 columns wide.
        var spans = new List<TableSpan>
        {
            Word("Qty", 250, 685), Word("Cost", 350, 685),
            Word("12", 250, 655), Word("4.50", 350, 655),
        };

        var table = Assert.Single(PdfSpatialTables.DetectTablesInClusters(
            spans, paths, TableDetectionConfig.Bordered()));
        Assert.Equal(2, table.ColCount);
        Assert.Equal(new[] { "Qty", "Cost" }, table.Rows[0].Cells.Select(c => c.Text));
    }

    [Fact]
    public void TwoDisconnectedColumnFlowsAreRejectedAsOneTable()
    {
        var paths = new List<PdfPath>();
        paths.AddRange(HRules(100, 600, 700, 670, 640, 610, 580, 550));
        paths.AddRange(VRules(550, 700, 100, 200, 300, 400, 500, 600));

        // Every row holds two cells, but the left pair and the right pair never
        // co-occur: two prose flows boxed together, not one table.
        var split = new List<TableSpan>
        {
            Word("a1", 150, 685), Word("a2", 250, 685),
            Word("b1", 450, 655), Word("b2", 550, 655),
            Word("a3", 150, 625), Word("a4", 250, 625),
            Word("b3", 450, 595), Word("b4", 550, 595),
            Word("a5", 150, 565), Word("a6", 250, 565),
        };
        Assert.Empty(PdfSpatialTables.DetectTablesInClusters(split, paths, TableDetectionConfig.Strict()));

        // One row bridging the two groups makes the column graph connected again.
        var bridged = new List<TableSpan>
        {
            Word("a1", 150, 685), Word("a2", 250, 685),
            Word("b1", 450, 655), Word("b2", 550, 655),
            Word("a3", 150, 625), Word("a4", 250, 625),
            Word("b3", 450, 595), Word("b4", 550, 595),
            Word("x1", 250, 565), Word("x2", 450, 565),
        };
        Assert.Single(PdfSpatialTables.DetectTablesInClusters(bridged, paths, TableDetectionConfig.Strict()));
    }

    [Fact]
    public void ADegenerateNaNPathDoesNotDisturbTheClusterOrdering()
    {
        var paths = ThreeByTwoGrid();
        var ops = new List<PathOp> { PathOp.MoveTo(double.NaN, 660), PathOp.LineTo(400, 660) };
        paths.Add(new PdfPath
        {
            Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Stroked = true, StrokeWidth = 0.75,
        });

        var tables = PdfSpatialTables.DetectTablesInClusters(
            ThreeByTwoWords(), paths, TableDetectionConfig.Strict());
        Assert.Equal(3, Assert.Single(tables).ColCount);
    }
}
