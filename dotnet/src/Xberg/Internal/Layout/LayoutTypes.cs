using System.Globalization;

namespace Xberg.Internal.Layout;

/// <summary>
/// Bounding box in image pixel coordinates: <c>(X1, Y1)</c> top-left,
/// <c>(X2, Y2)</c> bottom-right. Ports Rust <c>layout::types::BBox</c>.
/// </summary>
internal readonly record struct BBox(float X1, float Y1, float X2, float Y2)
{
    public float Width => MathF.Max(X2 - X1, 0f);
    public float Height => MathF.Max(Y2 - Y1, 0f);
    public float Area => Width * Height;

    public float IntersectionArea(in BBox other)
    {
        float x1 = MathF.Max(X1, other.X1);
        float y1 = MathF.Max(Y1, other.Y1);
        float x2 = MathF.Min(X2, other.X2);
        float y2 = MathF.Min(Y2, other.Y2);
        return MathF.Max(x2 - x1, 0f) * MathF.Max(y2 - y1, 0f);
    }

    /// <summary>Intersection over union.</summary>
    public float IntersectionOverUnion(in BBox other)
    {
        float intersection = IntersectionArea(other);
        float union = Area + other.Area - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    /// <summary>Fraction of <paramref name="other"/> lying inside this box, in 0..1.</summary>
    public float ContainmentOf(in BBox other)
    {
        float otherArea = other.Area;
        return otherArea <= 0f ? 0f : IntersectionArea(other) / otherArea;
    }

    /// <summary>Fraction of the page this box covers.</summary>
    public float PageCoverage(float pageWidth, float pageHeight)
    {
        float pageArea = pageWidth * pageHeight;
        return pageArea <= 0f ? 0f : Area / pageArea;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"[{X1:F1}, {Y1:F1}, {X2:F1}, {Y2:F1}]");
}

/// <summary>
/// The 18 canonical document layout classes. Every model backend maps its own class IDs
/// onto this shared set; models with fewer classes map to the closest equivalent.
/// Ports Rust <c>layout::types::LayoutClass</c>, including its snake_case wire names.
/// </summary>
internal enum LayoutClass
{
    Caption,
    Chart,
    Footnote,
    Formula,
    ListItem,
    PageFooter,
    PageHeader,
    Picture,
    SectionHeader,
    Table,
    Text,
    Title,
    DocumentIndex,
    Code,
    CheckboxSelected,
    CheckboxUnselected,
    Form,
    KeyValueRegion,
}

internal static class LayoutClassExtensions
{
    /// <summary>The snake_case wire-format name, matching the Rust serde output exactly.</summary>
    public static string WireName(this LayoutClass value) => value switch
    {
        LayoutClass.Caption => "caption",
        LayoutClass.Chart => "chart",
        LayoutClass.Footnote => "footnote",
        LayoutClass.Formula => "formula",
        LayoutClass.ListItem => "list_item",
        LayoutClass.PageFooter => "page_footer",
        LayoutClass.PageHeader => "page_header",
        LayoutClass.Picture => "picture",
        LayoutClass.SectionHeader => "section_header",
        LayoutClass.Table => "table",
        LayoutClass.Text => "text",
        LayoutClass.Title => "title",
        LayoutClass.DocumentIndex => "document_index",
        LayoutClass.Code => "code",
        LayoutClass.CheckboxSelected => "checkbox_selected",
        LayoutClass.CheckboxUnselected => "checkbox_unselected",
        LayoutClass.Form => "form",
        LayoutClass.KeyValueRegion => "key_value_region",
        _ => "text",
    };

    /// <summary>Map a Docling RT-DETR label ID (0-16), or null when out of range.</summary>
    public static LayoutClass? FromDoclingId(long id) => id switch
    {
        0 => LayoutClass.Caption,
        1 => LayoutClass.Footnote,
        2 => LayoutClass.Formula,
        3 => LayoutClass.ListItem,
        4 => LayoutClass.PageFooter,
        5 => LayoutClass.PageHeader,
        6 => LayoutClass.Picture,
        7 => LayoutClass.SectionHeader,
        8 => LayoutClass.Table,
        9 => LayoutClass.Text,
        10 => LayoutClass.Title,
        11 => LayoutClass.DocumentIndex,
        12 => LayoutClass.Code,
        13 => LayoutClass.CheckboxSelected,
        14 => LayoutClass.CheckboxUnselected,
        15 => LayoutClass.Form,
        16 => LayoutClass.KeyValueRegion,
        _ => null,
    };

    /// <summary>Map a DocLayNet class ID (0-10), or null when out of range.</summary>
    public static LayoutClass? FromDocLayNetId(long id) => id switch
    {
        >= 0 and <= 10 => FromDoclingId(id),
        _ => null,
    };

    /// <summary>Map a DocStructBench class ID (0-9), or null when out of range.</summary>
    public static LayoutClass? FromDocStructBenchId(long id) => id switch
    {
        0 => LayoutClass.Title,
        1 => LayoutClass.Text,
        2 => LayoutClass.Text,
        3 => LayoutClass.Picture,
        4 => LayoutClass.Caption,
        5 => LayoutClass.Table,
        6 => LayoutClass.Caption,
        7 => LayoutClass.Footnote,
        8 => LayoutClass.Formula,
        9 => LayoutClass.Caption,
        _ => null,
    };
}

/// <summary>A single layout detection: class, confidence in 0..1, and pixel bounding box.</summary>
internal sealed record LayoutDetection(LayoutClass ClassName, float Confidence, BBox Box)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{ClassName.WireName(),-20} conf={Confidence:F3}  bbox={Box}");
}

/// <summary>All detections on one page, with the page geometry the model saw.</summary>
internal sealed record DetectionResult(int PageWidth, int PageHeight, IReadOnlyList<LayoutDetection> Detections);
