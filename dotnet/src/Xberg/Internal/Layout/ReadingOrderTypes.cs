namespace Xberg.Internal.Layout;

/// <summary>
/// Simplified layout class for the markdown pipeline, ported from Rust
/// <c>pdf::structure::types::LayoutHintClass</c>.
/// </summary>
/// <remarks>
/// Deliberately decoupled from the detector's own class enum: the reading-order and paragraph
/// code has to compile and run wherever hints come from, including a caller that supplies them
/// without running any model at all.
/// </remarks>
internal enum LayoutHintClass
{
    Title,
    SectionHeader,
    Code,
    Formula,
    ListItem,
    Caption,
    Footnote,
    PageHeader,
    PageFooter,
    Table,
    Picture,
    DocumentIndex,
    Form,
    KeyValueRegion,
    Text,
    Other,
}

internal static class LayoutHintClassExtensions
{
    /// <summary>
    /// Whether the class wraps other content rather than classifying text of its own.
    /// </summary>
    /// <remarks>
    /// A wrapper establishes a reading-order boundary and adopts semantic children, but never
    /// destructively classifies the residual text inside it.
    /// </remarks>
    public static bool IsWrapper(this LayoutHintClass self) => self switch
    {
        LayoutHintClass.Table or LayoutHintClass.Picture or LayoutHintClass.DocumentIndex
            or LayoutHintClass.Form or LayoutHintClass.KeyValueRegion => true,
        _ => false,
    };

    public static string Label(this LayoutHintClass self) => self switch
    {
        LayoutHintClass.Title => "title",
        LayoutHintClass.SectionHeader => "section_header",
        LayoutHintClass.Code => "code",
        LayoutHintClass.Formula => "formula",
        LayoutHintClass.ListItem => "list_item",
        LayoutHintClass.Caption => "caption",
        LayoutHintClass.Footnote => "footnote",
        LayoutHintClass.PageHeader => "page_header",
        LayoutHintClass.PageFooter => "page_footer",
        LayoutHintClass.Table => "table",
        LayoutHintClass.Picture => "picture",
        LayoutHintClass.DocumentIndex => "document_index",
        LayoutHintClass.Form => "form",
        LayoutHintClass.KeyValueRegion => "key_value_region",
        LayoutHintClass.Text => "text",
        _ => "other",
    };
}

/// <summary>
/// One detected layout region: a class with a confidence and a bounding box in PDF coordinate
/// space (points, y=0 at the bottom of the page).
/// </summary>
/// <remarks>
/// Named for the region rather than the hint to keep it apart from <c>Pdf.LayoutHint</c>, which
/// is the geometric table-detection hint and carries no class.
/// </remarks>
internal sealed record LayoutRegionHint(
    LayoutHintClass ClassName,
    float Confidence,
    float Left,
    float Bottom,
    float Right,
    float Top);

/// <summary>One stable component of a page-local layout region path.</summary>
internal readonly record struct LayoutRegionTag(int Id, LayoutHintClass? ClassName);

/// <summary>
/// Layout ancestry, which is at most two levels: a top-level wrapper or root, and an optional
/// semantic child region contained by that wrapper.
/// </summary>
internal readonly record struct LayoutRegionPath(LayoutRegionTag Root, LayoutRegionTag? Child)
{
    public IEnumerable<LayoutRegionTag> Tags()
    {
        yield return Root;
        if (Child is { } child) yield return child;
    }
}

/// <summary>
/// One root in the page's region-preserving reading-order plan.
/// </summary>
/// <remarks>
/// <see cref="SegmentIndices"/> index the post-table-filter segment list. <see cref="HintIndices"/>
/// holds only regular classification hints: Table/Picture wrappers establish boundaries but never
/// classify residual text.
/// </remarks>
internal sealed class LayoutSegmentGroup : IEquatable<LayoutSegmentGroup>
{
    public List<int> SegmentIndices { get; init; } = new();
    public List<int> HintIndices { get; init; } = new();
    public LayoutRegionPath? RegionPath { get; init; }

    public bool Equals(LayoutSegmentGroup? other) =>
        other is not null
        && SegmentIndices.SequenceEqual(other.SegmentIndices)
        && HintIndices.SequenceEqual(other.HintIndices)
        && Nullable.Equals(RegionPath, other.RegionPath);

    public override bool Equals(object? obj) => Equals(obj as LayoutSegmentGroup);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (int index in SegmentIndices) hash.Add(index);
        foreach (int index in HintIndices) hash.Add(index);
        hash.Add(RegionPath);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A text span with bounding box information, ported from Rust
/// <c>extractors::pdf::rotation::TextSpan</c>.
/// </summary>
/// <remarks>
/// <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/> are always the
/// page-space bbox the span producer reported: for a rotated run the origin is in page
/// coordinates but the width and height are flattened onto the run's own axis, which is why
/// ordering has to go through <see cref="ReadingOrderText.UprightReadingOrigin"/>.
/// </remarks>
internal sealed class ReadingOrderSpan
{
    public string Text { get; init; } = "";
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }

    /// <summary>Text-matrix rotation in degrees. Zero for the overwhelming majority of spans.</summary>
    public float RotationDegrees { get; init; }
}
