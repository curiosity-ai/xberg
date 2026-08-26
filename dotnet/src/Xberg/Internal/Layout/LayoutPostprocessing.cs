namespace Xberg.Internal.Layout;

/// <summary>
/// Postprocessing for raw layout detections, ported from Rust
/// <c>layout/postprocessing/{nms,heuristics}.rs</c>.
/// </summary>
/// <remarks>
/// Engine-neutral: whichever backend produced the detections, these run the same. The heuristics
/// are Docling's <c>layout_postprocessor.py</c> rules — per-class thresholds, full-page picture
/// removal, overlap resolution — and the NMS is the plain greedy one that YOLO needs and RT-DETR,
/// being NMS-free, does not.
/// </remarks>
internal static class LayoutPostprocessing
{
    /// <summary>Sort by confidence, highest first, in the total order Rust uses.</summary>
    /// <remarks>
    /// Stable, so equally-confident detections keep the order the model emitted them in, and
    /// keyed on the same total order as everywhere else in the port so a NaN confidence cannot
    /// make the comparison intransitive.
    /// </remarks>
    internal static List<LayoutDetection> SortByConfidenceDesc(List<LayoutDetection> detections)
    {
        var sorted = new List<LayoutDetection>(detections);
        ReadingOrder.StableSort(sorted, (a, b) => ReadingOrder.TotalCmp(b.Confidence, a.Confidence));
        return sorted;
    }

    /// <summary>
    /// Standard greedy Non-Maximum Suppression: sort by confidence, then drop anything whose IoU
    /// with a kept, higher-confidence detection exceeds the threshold.
    /// </summary>
    public static List<LayoutDetection> GreedyNms(List<LayoutDetection> detections, float iouThreshold)
    {
        var sorted = SortByConfidenceDesc(detections);
        int n = sorted.Count;
        var keep = new bool[n];
        Array.Fill(keep, true);

        for (int i = 0; i < n; i++)
        {
            if (!keep[i]) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (!keep[j]) continue;
                if (sorted[i].Box.IntersectionOverUnion(sorted[j].Box) > iouThreshold) keep[j] = false;
            }
        }

        var result = new List<LayoutDetection>(n);
        for (int k = 0; k < n; k++) if (keep[k]) result.Add(sorted[k]);
        return result;
    }

    /// <summary>
    /// Per-class confidence thresholds from Docling's <c>layout_postprocessor.py</c>.
    /// </summary>
    /// <remarks>
    /// The specialised classes are rarer and more valuable to catch, so they clear a lower bar
    /// than the common ones.
    /// </remarks>
    private static float ClassThreshold(LayoutClass layoutClass) => layoutClass switch
    {
        LayoutClass.SectionHeader or LayoutClass.Title or LayoutClass.Code
            or LayoutClass.Form or LayoutClass.KeyValueRegion => 0.45f,
        _ => 0.50f,
    };

    /// <summary>
    /// Apply the Docling postprocessing heuristics to raw detections.
    /// </summary>
    /// <remarks>
    /// In order: per-class confidence thresholds, full-page picture removal, demotion of tiny
    /// low-confidence figures to text, up to three rounds of overlap resolution, and the
    /// key-value-region versus table tie-break.
    /// </remarks>
    public static List<LayoutDetection> ApplyHeuristics(
        List<LayoutDetection> detections, float pageWidth, float pageHeight)
    {
        var kept = detections
            .Where(d => d.Confidence >= ClassThreshold(d.ClassName))
            // A "picture" covering almost the whole page is a background graphic, not a figure.
            .Where(d => d.ClassName is not (LayoutClass.Picture or LayoutClass.Chart)
                        || d.Box.PageCoverage(pageWidth, pageHeight) < 0.9f)
            // A tiny, unconfident table or figure is far more often a stray text run.
            .Select(d =>
                d.ClassName is LayoutClass.Table or LayoutClass.Picture or LayoutClass.Chart
                && d.Box.PageCoverage(pageWidth, pageHeight) < 0.03f
                && d.Confidence < 0.7f
                    ? d with { ClassName = LayoutClass.Text }
                    : d)
            .ToList();

        // Removing one detection can expose a new overlapping pair, so this repeats until it
        // reaches a fixed point — bounded at three rounds.
        for (int round = 0; round < 3; round++)
        {
            int previousCount = kept.Count;
            ResolveOverlaps(kept);
            if (kept.Count == previousCount) break;
        }

        ResolveKvrTableOverlap(kept);
        return kept;
    }

    /// <summary>
    /// Drop the weaker of two significantly overlapping detections (IoU or containment above 0.8).
    /// </summary>
    private static void ResolveOverlaps(List<LayoutDetection> detections)
    {
        int n = detections.Count;
        var remove = new bool[n];

        for (int i = 0; i < n; i++)
        {
            if (remove[i]) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (remove[j]) continue;

                float iou = detections[i].Box.IntersectionOverUnion(detections[j].Box);
                float containmentIofJ = detections[i].Box.ContainmentOf(detections[j].Box);
                float containmentJofI = detections[j].Box.ContainmentOf(detections[i].Box);

                if (iou < 0.8f && containmentIofJ < 0.8f && containmentJofI < 0.8f) continue;

                if (PickRemoval(detections[i], detections[j], containmentIofJ) == 0) remove[i] = true;
                else remove[j] = true;
            }
        }

        for (int k = n - 1; k >= 0; k--) if (remove[k]) detections.RemoveAt(k);
    }

    /// <summary>
    /// Which of two overlapping detections to drop: 0 for <paramref name="a"/>, 1 for
    /// <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// Label preference comes before confidence. A list item and a text region of near-identical
    /// size are the same block seen two ways, and the list item is the more specific reading; a
    /// code region that contains the other keeps its contents; and a text region that a figure or
    /// table merely overlaps wins on equal confidence, because a wrongly-labelled figure loses the
    /// text inside it entirely.
    /// </remarks>
    private static int PickRemoval(LayoutDetection a, LayoutDetection b, float containmentAofB)
    {
        if (a.ClassName == LayoutClass.ListItem && b.ClassName == LayoutClass.Text)
        {
            float areaRatio = a.Box.Area / MathF.Max(b.Box.Area, 1e-6f);
            if (areaRatio >= 0.8f && areaRatio <= 1.2f) return 1;
        }
        if (b.ClassName == LayoutClass.ListItem && a.ClassName == LayoutClass.Text)
        {
            float areaRatio = b.Box.Area / MathF.Max(a.Box.Area, 1e-6f);
            if (areaRatio >= 0.8f && areaRatio <= 1.2f) return 0;
        }

        if (a.ClassName == LayoutClass.Code && containmentAofB > 0.8f) return 1;
        if (b.ClassName == LayoutClass.Code && b.Box.ContainmentOf(a.Box) > 0.8f) return 0;

        if (a.ClassName == LayoutClass.Text
            && b.ClassName is LayoutClass.Table or LayoutClass.Picture or LayoutClass.Chart
            && a.Confidence >= b.Confidence)
            return 1;
        if (b.ClassName == LayoutClass.Text
            && a.ClassName is LayoutClass.Table or LayoutClass.Picture or LayoutClass.Chart
            && b.Confidence >= a.Confidence)
            return 0;

        return a.Confidence >= b.Confidence ? 1 : 0;
    }

    /// <summary>
    /// Drop a key-value region that a table almost entirely contains at near-equal confidence.
    /// </summary>
    /// <remarks>
    /// The two classes describe the same structure, and the table reading carries cells.
    /// </remarks>
    private static void ResolveKvrTableOverlap(List<LayoutDetection> detections)
    {
        int n = detections.Count;
        var remove = new bool[n];

        for (int i = 0; i < n; i++)
        {
            if (remove[i] || detections[i].ClassName != LayoutClass.KeyValueRegion) continue;
            for (int j = 0; j < n; j++)
            {
                if (i == j || remove[j] || detections[j].ClassName != LayoutClass.Table) continue;
                float overlap = detections[j].Box.ContainmentOf(detections[i].Box);
                float confidenceDifference = MathF.Abs(detections[i].Confidence - detections[j].Confidence);
                if (overlap > 0.9f && confidenceDifference < 0.1f) { remove[i] = true; break; }
            }
        }

        for (int k = n - 1; k >= 0; k--) if (remove[k]) detections.RemoveAt(k);
    }
}
