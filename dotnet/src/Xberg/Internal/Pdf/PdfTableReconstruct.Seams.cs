using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>
/// The two region-shaping rules `pdf::oxide::table` applies around plain per-region
/// reconstruction: splitting a wide region into two side-by-side tables, and stitching a
/// run of label-heavy financial sections into one. Both are declared here so the page
/// driver can call them; each is replaced by its real implementation in its own file.
/// </summary>
internal static partial class PdfTableReconstruct
{
    /// <summary>A region has to reach this many columns before a side-by-side split is even
    /// considered — narrower grids are a single table, and splitting them loses columns.</summary>
    private const int SideBySideMinParentColumns = 7;

    /// <summary>A split half's column gap is derived from its own median word height rather
    /// than reused from the parent, whose gap was measured across the gutter.</summary>
    private const uint SideBySideChildGapHeightMultiplier = 6;

    /// <summary>Split a region into two side-by-side halves at a central whitespace gutter,
    /// or <c>null</c> when no gutter separates the region cleanly.</summary>
    private static partial (List<HocrWord> Left, List<HocrWord> Right)? SplitSideBySideRegion(List<HocrWord> region);

    /// <summary>Whether two candidate halves are shaped differently enough to be genuinely
    /// separate tables rather than one table the split cut down the middle.</summary>
    private static partial bool SideTablesHaveIndependentShape(Table left, Table right);

    /// <summary>Reconcile the header and continuation rows of two financial tables that were
    /// laid out side by side, so the pair reads as two independent tables.</summary>
    private static partial (Table Left, Table Right) NormalizeSideBySideFinancialTables(Table left, Table right);

    /// <summary>Reconstruct one section of a label-heavy financial statement — a region whose
    /// rows are a long descriptor followed by numbers on fixed tracks — returning the section's
    /// table, its value tracks, and the tolerance those tracks were found within. <c>null</c>
    /// when the region is not that shape.</summary>
    private static partial (Table Table, List<uint> Tracks, uint Tolerance)? ReconstructLabelHeavyFinancialRegion(
        List<HocrWord> region, float pageHeight, uint pageNumber);

    /// <summary>Whether two consecutive financial sections are close enough vertically, and
    /// aligned enough, to belong to the same statement.</summary>
    private static partial bool LabelHeavyFinancialSectionsAreContiguous(
        List<HocrWord> previous, List<HocrWord> next, List<uint> nextTracks, uint nextTolerance);

    /// <summary>Concatenate a run of financial sections into the single table they print as.</summary>
    private static partial Table StitchLabelHeavyFinancialSections(List<Table> sections, uint pageNumber);
}
