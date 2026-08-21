using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>Label-heavy financial section chaining (`pdf::oxide::table`). Not yet ported: the
/// defaults below recognise no region as a financial section, so every region falls through to
/// plain per-region reconstruction.</summary>
internal static partial class PdfTableReconstruct
{
    private static partial (Table Table, List<uint> Tracks, uint Tolerance)? ReconstructLabelHeavyFinancialRegion(
        List<HocrWord> region, float pageHeight, uint pageNumber) => null;

    private static partial bool LabelHeavyFinancialSectionsAreContiguous(
        List<HocrWord> previous, List<HocrWord> next, List<uint> nextTracks, uint nextTolerance) => false;

    private static partial Table StitchLabelHeavyFinancialSections(List<Table> sections, uint pageNumber)
        => sections.Count > 0 ? sections[0] : new Table { PageNumber = pageNumber };
}
