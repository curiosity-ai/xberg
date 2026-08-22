using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers how the heuristic table finder groups a page's words into candidate regions
/// before any grid is built.
/// </summary>
public class PdfTableHeuristicRegionTests
{
    private static readonly float[] Tracks = { 60f, 220f, 300f, 380f, 460f };

    private static readonly string[] Labels =
    {
        "Untreated",
        "Buffered-acid-rinse",
        "Passivating-primer",
        "Chromate-conversion-layer",
        "Zinc-rich-barrier",
        "Two-part-epoxy-topcoat",
    };

    private static SegmentData Seg(string text, float x, float y, float width) =>
        new() { Text = text, X = x, Y = y, Width = width, Height = 10f, FontSize = 10f, BaselineY = y };

    /// <summary>Six rows of a descriptor followed by four values on fixed tracks.</summary>
    private static void AddDataRows(List<SegmentData> page, float topY)
    {
        for (int r = 0; r < 6; r++)
        {
            float y = topY - r * 14f;
            page.Add(Seg(Labels[r], Tracks[0], y, 140f));
            page.Add(Seg($"{r * 3 + 1}.{r}4", Tracks[1], y, 40f));
            page.Add(Seg($"{r * 7 + 2}.{r}91", Tracks[2], y, 40f));
            page.Add(Seg($"0.{r}0{r}", Tracks[3], y, 40f));
            page.Add(Seg($"{r * 11 + 5}.{r}", Tracks[4], y, 40f));
        }
    }

    /// <summary>
    /// Vertical clustering splits on a blank line, so a table whose column names sit one line
    /// above its numbers arrives as two regions — and the caption half, being a single row, is
    /// below the region filter's floor and would be dropped before any grid saw it. The header
    /// has to be folded into the block it labels while both are still regions.
    /// </summary>
    [Fact]
    public void AColumnCaptionOneLineAboveItsNumbersReachesTheGrid()
    {
        var page = new List<SegmentData>();
        string[] names = { "Treatment", "Ecorr", "Icorr", "Rate", "Yield" };
        for (int c = 0; c < 5; c++) page.Add(Seg(names[c], Tracks[c], 700f, 40f));
        AddDataRows(page, 676f);

        var tables = PdfTableReconstruct.ExtractHeuristicTables(
            new List<List<SegmentData>> { page }, allowSingleColumn: false);

        var table = Assert.Single(tables);
        Assert.Equal(names, table.Cells[0]);
        Assert.Equal(7, table.Cells.Count);
    }

    /// <summary>A caption whose glyphs sit under none of the value tracks labels something
    /// else on the page, and folding it in would invent a header row the page does not have.</summary>
    [Fact]
    public void AMisalignedCaptionIsLeftWhereItIs()
    {
        var page = new List<SegmentData>();
        // Caption bunched into the left margin, clear of every track but the first.
        for (int c = 0; c < 5; c++) page.Add(Seg($"Note{c}", 60f + c * 12f, 700f, 10f));
        AddDataRows(page, 676f);

        var tables = PdfTableReconstruct.ExtractHeuristicTables(
            new List<List<SegmentData>> { page }, allowSingleColumn: false);

        Assert.DoesNotContain(tables, t => t.Cells.Count > 0 && t.Cells[0].Contains("Note0"));
    }

    /// <summary>A wrapped C function signature has no braces on the page at all — the opening
    /// brace is on the line after the closing paren — so the brace-fraction rule cannot see it,
    /// yet one parameter per line is exactly the shape column detection reads as a table.</summary>
    [Fact]
    public void AWrappedFunctionSignatureIsNotATable()
    {
        var declaration = new List<List<string>>
        {
            new() { "png_structp png_create_read_struct(", "" },
            new() { "png_const_charp", "user_png_ver," },
            new() { "png_voidp", "error_ptr," },
            new() { "png_error_ptr", "*warn_fn);" },
        };

        Assert.True(PdfTableReconstruct.LooksLikeCodeListing(declaration));
    }

    /// <summary>An API reference whose rows happen to end in a comma is still a table: its
    /// trailing cells are prose, not parameter names, so no declaration is in evidence.</summary>
    [Fact]
    public void AnApiReferenceWithIncidentalPunctuationSurvives()
    {
        var reference = new List<List<string>>
        {
            new() { "void setLocale(", "" },
            new() { "language", "en, fr," },
            new() { "region", "US, CA," },
        };

        Assert.False(PdfTableReconstruct.LooksLikeCodeListing(reference));
    }
}
