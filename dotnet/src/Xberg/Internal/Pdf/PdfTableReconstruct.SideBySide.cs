using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>Side-by-side region splitting (`pdf::oxide::table`). Not yet ported: the defaults
/// below decline every split, which leaves the parent table exactly as it was.</summary>
internal static partial class PdfTableReconstruct
{
    private static partial (List<HocrWord> Left, List<HocrWord> Right)? SplitSideBySideRegion(List<HocrWord> region)
        => null;

    private static partial bool SideTablesHaveIndependentShape(Table left, Table right) => false;

    private static partial (Table Left, Table Right) NormalizeSideBySideFinancialTables(Table left, Table right)
        => (left, right);
}
