// PDF structure / heading pipeline (oxide heuristic path).
//
// Port of the non-layout, non-structure-tree branch of
//   crates/xberg/src/pdf/structure/pipeline.rs :: extract_document_structure_from_segments
// plus the helpers it calls in hierarchy/clustering.rs, structure/classify.rs,
// structure/paragraphs.rs, structure/assembly.rs, structure/text_repair.rs,
// structure/lines.rs, structure/layout_classify.rs, structure/regions/heading.rs.
//
// Consumes per-page font-metric segments (ColumnAware reading order) and produces a
// structured InternalDocument with Heading/Paragraph/ListItem/Code elements. This is the
// document the Markdown/Djot/HTML renderers consume, giving heading-aware output that
// matches the Rust golden. Tables/images are out of scope here (Lever 1).
using System.Text;
using System.Text.RegularExpressions;
using Xberg.Types;

namespace Xberg.Internal.Pdf;

/// <summary>Font-metric text segment (mirrors Rust `SegmentData`).</summary>
public sealed class SegmentData
{
    public string Text = "";
    public float X, Y, Width, Height;
    public float FontSize;
    public bool IsBold;
    public bool IsItalic;
    public bool IsMonospace;
    public float BaselineY;

    public SegmentData Clone() => (SegmentData)MemberwiseClone();
}

internal sealed class PdfLine
{
    public List<SegmentData> Segments = new();
    public float BaselineY;
    public float DominantFontSize;
    public bool IsBold;
    public bool IsMonospace;
}

internal sealed class PdfParagraph
{
    public string Text = "";
    public List<PdfLine> Lines = new();
    public float DominantFontSize;
    public byte? HeadingLevel;
    public bool IsBold;
    public bool IsListItem;
    public bool IsCodeBlock;
    public bool IsFormula;
    public bool IsPageFurniture;
    // (left, bottom, right, top) PDF coords.
    public (float L, float B, float R, float T)? BlockBbox;
    public int WordCount;
}

public static class PdfStructure
{
    // constants.rs
    private const int MAX_HEADING_WORD_COUNT = 20;
    private const float MAX_HEADING_DISTANCE_MULTIPLIER = 2.0f;
    private const float MIN_HEADING_FONT_RATIO = 1.15f;
    private const float MIN_HEADING_FONT_GAP = 1.5f;

    /// <summary>A document with fewer blocks than this has too little evidence for the
    /// first-paragraph H1 rescue: its one paragraph is not "the biggest text on a page of
    /// body copy", it is the whole document (Rust `MIN_BLOCKS_FOR_FONT_HEADING`).</summary>
    private const int MIN_BLOCKS_FOR_FONT_HEADING = 5;

    /// <summary>Pages that must share the enlarged opening tier before a sparse document is
    /// allowed a heading level at all (Rust `SPARSE_REPEATED_TIER_MIN_PAGES`).</summary>
    private const int SPARSE_REPEATED_TIER_MIN_PAGES = 2;
    /// <summary>Heading and body — the only split a sparse document is read as having.</summary>
    private const int SPARSE_FONT_TIER_CLUSTER_COUNT = 2;
    /// <summary>Points either side of a tier centroid that still count as that tier.</summary>
    private const float SPARSE_FONT_TIER_TOLERANCE = 0.5f;
    /// <summary>A tier repeated at the top of several pages is peer sections, not a unique
    /// document title, so H1 stays reserved for a title.</summary>
    private const byte SPARSE_REPEATED_TIER_HEADING_LEVEL = 2;
    /// <summary>Pages that must repeat an H2 tier before title inference is suppressed.</summary>
    private const int SPARSE_PEER_HEADING_MIN_PAGES = 2;
    /// <summary>Font tolerance for the repeated-peer-tier test.</summary>
    private const float SPARSE_PEER_HEADING_FONT_TOLERANCE = 0.5f;
    private const int MAX_BOLD_HEADING_WORD_COUNT = 12;
    private const float PARAGRAPH_GAP_HEIGHT_FACTOR = 1.5f;

    /// <summary>
    /// Multiple of the page's own body leading that a baseline-to-baseline advance must reach
    /// to count as a paragraph break.
    /// </summary>
    /// <remarks>
    /// <see cref="PARAGRAPH_GAP_HEIGHT_FACTOR"/> measures the whitespace band between two lines
    /// against the glyph height, which makes it blind to the commonest paragraph separator there
    /// is. With glyph height <c>h</c> and leading <c>L</c>, single spacing leaves a band of
    /// <c>L - h</c> and a blank line leaves <c>2L - h</c>; for the usual <c>L</c> of 1.1–1.3
    /// <c>h</c> that blank line is only 1.2–1.6 <c>h</c>, so a 1.5<c>h</c> band threshold asks
    /// for more vertical space than a blank line actually provides. Comparing the advance to the
    /// leading is scale-free instead: a blank line doubles the advance. The two rules are OR-ed,
    /// so no break the band rule already finds is lost.
    /// </remarks>
    private const float PARAGRAPH_BREAK_LEADING_MULTIPLE = 1.5f;

    /// <summary>
    /// Largest baseline-to-baseline gap, as a multiple of the larger paragraph's dominant font
    /// size, that a continuation merge will cross. A wrapped line sits roughly one line-height
    /// below its predecessor (leading is typically 1.0–1.6× the font size); several times that
    /// means the two paragraphs come from spatially distinct regions of the page.
    /// </summary>
    private const float MAX_CONTINUATION_LINE_GAP_MULTIPLE = 3.0f;

    // hierarchy clustering constants
    private const int KMEANS_MAX_ITERATIONS = 100;
    private const float KMEANS_CONVERGENCE_THRESHOLD = 0.01f;

    private const float REDRAWN_MIN_TOLERANCE_PTS = 1.0f;
    private const int REDRAWN_LOOKBACK = 8;

    // ── Entry point ────────────────────────────────────────────────────────────

    /// <summary>Build a structured InternalDocument from per-page segments.
    /// Returns null when no elements were produced (caller falls back to flat text).</summary>
    public static InternalDocument? Build(List<List<SegmentData>> allPageSegments, int kClusters = 4)
    {
        int pageCount = allPageSegments.Count;

        var headingMap = BuildHeadingMap(allPageSegments, kClusters);
        float? docBodyFontSize = null;
        foreach (var (fs, lvl) in headingMap) { if (lvl is null) { docBodyFontSize = fs; break; } }

        var pageHeights = new float[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            float max = 0f;
            foreach (var s in allPageSegments[i]) max = Math.Max(max, s.Y + s.Height);
            pageHeights[i] = Math.Max(max, 792.0f);
        }

        var allPageParagraphs = new List<List<PdfParagraph>>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            var segs = allPageSegments[i];
            var gapYs = ComputeParagraphGapYs(segs);
            var paras = BlocksToParagraphs(segs, headingMap, gapYs);
            // Segment-level repair runs here, before paragraphs are merged, because the
            // continuation and dehyphenation rules read the last and first characters of
            // neighbouring segments — a trailing soft hyphen or control character left in place
            // would be read as ordinary text and change the decision.
            ApplyToAllSegments(paras, PdfTextRepair.RepairSegment);
            MergeContinuationParagraphs(paras);
            RetainPageFurnitureSafely(paras);
            allPageParagraphs.Add(paras);
        }

        RefineHeadingHierarchy(allPageParagraphs);
        DemoteUnnumberedSubsections(allPageParagraphs);
        DemoteHeadingRuns(allPageParagraphs);
        SplitColonSemicolonRunInLists(allPageParagraphs);

        // strip_repeating_text default true
        MarkCrossPageRepeatingText(allPageParagraphs, pageHeights);
        MarkCrossPageRepeatingShortText(allPageParagraphs);
        MarkArxivNoise(allPageParagraphs);
        foreach (var page in allPageParagraphs) RetainPageFurnitureSafely(page);
        DeduplicateParagraphs(allPageParagraphs);
        CompactFinalHeadingHierarchy(allPageParagraphs);

        var doc = AssembleInternalDocument(allPageParagraphs);

        // Stage 5: element-level text normalization.
        foreach (var elem in doc.Elements)
        {
            if (string.IsNullOrEmpty(elem.Text)) continue;
            string t = RepairContextualLigatures(elem.Text);
            t = ExpandLigaturesWithSpaceAbsorption(t);
            t = NormalizeUnicodeText(t);
            elem.Text = t;
        }

        if (doc.Elements.Count == 0) return null;
        return doc;
    }

    /// <summary>Convert a page's assembled visual lines to segments. Line assembly (spacing,
    /// glyph-fragmentation rebuild) is done by PdfPageText.BuildLineSegments so the text
    /// matches the plain path exactly; here we only wrap it as SegmentData.</summary>
    public static List<SegmentData> SegmentsFromLines(List<PdfPageText.LineSeg> lines)
    {
        var segs = new List<SegmentData>();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            segs.Add(new SegmentData
            {
                Text = l.Text,
                X = (float)l.X,
                Y = (float)l.Y,
                Width = (float)l.Width,
                Height = (float)l.Height,
                FontSize = (float)l.FontSize,
                IsBold = l.IsBold,
                IsItalic = l.IsItalic,
                IsMonospace = l.IsMonospace,
                BaselineY = (float)l.Y,
            });
        }
        return DedupeRedrawnSegments(segs);
    }

    private static List<SegmentData> DedupeRedrawnSegments(List<SegmentData> segments)
    {
        var kept = new List<SegmentData>(segments.Count);
        foreach (var seg in segments)
        {
            int windowStart = Math.Max(0, kept.Count - REDRAWN_LOOKBACK);
            SegmentData? prev = null;
            for (int i = windowStart; i < kept.Count; i++)
            {
                var p = kept[i];
                float dxTol = Math.Max(Math.Min(p.Width, seg.Width) * 0.5f, REDRAWN_MIN_TOLERANCE_PTS);
                float dyTol = Math.Max(Math.Min(p.Height, seg.Height) * 0.5f, REDRAWN_MIN_TOLERANCE_PTS);
                if (p.Text == seg.Text && Math.Abs(p.X - seg.X) <= dxTol && Math.Abs(p.Y - seg.Y) <= dyTol)
                {
                    prev = p; break;
                }
            }
            if (prev != null)
            {
                prev.IsBold |= seg.IsBold;
                prev.IsItalic |= seg.IsItalic;
                if (seg.FontSize > prev.FontSize) prev.FontSize = seg.FontSize;
                continue;
            }
            kept.Add(seg);
        }
        return kept;
    }

    // ── Font clustering (hierarchy/clustering.rs) ────────────────────────────────

    private sealed class FontSizeCluster { public float Centroid; public List<int> MemberTextLens = new(); }

    private static List<(float centroid, byte? level)> BuildHeadingMap(List<List<SegmentData>> allPageSegments, int kClusters)
    {
        // Collect font sizes (blocks) from all non-empty segments; text is not needed
        // for the heuristic path (text len==0 for all → body = smallest cluster).
        var blockFonts = new List<float>();
        foreach (var page in allPageSegments)
            foreach (var seg in page)
                if (!string.IsNullOrWhiteSpace(seg.Text)) blockFonts.Add(seg.FontSize);

        if (blockFonts.Count == 0) return new();

        int paragraphCount = blockFonts.Count;

        if (paragraphCount < MIN_BLOCKS_FOR_FONT_HEADING)
        {
            // A document too small for font clustering can still be structured: a handful of
            // pages that each open at the same larger size are peer sections, and that repetition
            // is evidence the block count alone cannot supply.
            var sparse = SparseMultiPageHeadingMap(allPageSegments, blockFonts);
            if (sparse is not null) return sparse;

            // Sparsity gate: too few text blocks to establish a reliable body-font baseline.
            // Return a body-only map and skip both k-means heading promotion and the fallback
            // title promotion, so a lone larger line on a cover, title or one-line document is
            // not over-promoted to a heading — the bold pass will still call it an H2 if it
            // looks like one.
            var bodyOnly = ClusterFontSizes(blockFonts, 1);
            return bodyOnly.Select(c => (c.Centroid, (byte?)null)).ToList();
        }

        int effectiveK = paragraphCount < 20 ? Math.Min(kClusters, Math.Max(2, paragraphCount / 4)) : kClusters;

        var clusters = ClusterFontSizes(blockFonts, effectiveK);
        var map = AssignHeadingLevelsSmart(clusters, MIN_HEADING_FONT_RATIO, MIN_HEADING_FONT_GAP);

        // Rust has no cluster-level H1 fallback: its equivalent is the paragraph-level rescue in
        // RefineHeadingHierarchy, which is gated on the document having enough blocks to judge.
        // Without the same gate a one-paragraph document promotes its only text to H1, where
        // Rust leaves the cluster path empty and the bold pass calls it H2.
        bool hasAnyHeading = map.Any(m => m.level.HasValue);
        if (!hasAnyHeading && allPageSegments.Count > 0 && blockFonts.Count >= MIN_BLOCKS_FOR_FONT_HEADING)
        {
            float? firstSegFont = null;
            foreach (var s in allPageSegments[0])
                if (!string.IsNullOrWhiteSpace(s.Text)) { firstSegFont = s.FontSize; break; }
            if (firstSegFont.HasValue)
            {
                var sizes = blockFonts.OrderBy(f => f).ToList();
                float median = sizes.Count == 0 ? 0f : sizes[sizes.Count / 2];
                if (median > 0f && firstSegFont.Value >= median * 1.2f)
                {
                    for (int i = 0; i < map.Count; i++)
                        if (Math.Abs(map[i].centroid - firstSegFont.Value) < 0.5f) { map[i] = (map[i].centroid, 1); break; }
                }
            }
        }
        return map;
    }

    /// <summary>
    /// A heading map for documents below <see cref="MIN_BLOCKS_FOR_FONT_HEADING"/> whose pages
    /// each open at the same larger font tier.
    /// </summary>
    /// <remarks>
    /// The block floor exists because a lone large line is more often display prose than a
    /// section heading. Repetition breaks that tie: when two or more pages begin at the same
    /// enlarged size, those lines are peer sections and the size is a heading tier, however few
    /// blocks the document has. They are peers, not a title, so they get level 2 and H1 stays
    /// reserved. Ported from Rust <c>sparse_multi_page_heading_map</c>; the port has no
    /// structure-tree path, so every page is a heuristic page.
    /// </remarks>
    private static List<(float centroid, byte? level)>? SparseMultiPageHeadingMap(
        List<List<SegmentData>> allPageSegments, List<float> blockFonts)
    {
        if (allPageSegments.Count < SPARSE_REPEATED_TIER_MIN_PAGES) return null;

        var clusters = ClusterFontSizes(blockFonts, SPARSE_FONT_TIER_CLUSTER_COUNT);
        if (clusters.Count != SPARSE_FONT_TIER_CLUSTER_COUNT) return null;

        // Two tiers only: any block that sits between them means the sizes are a spread rather
        // than a heading/body split, and the repetition argument no longer holds.
        bool twoNarrowTiers = blockFonts.All(f =>
            !float.IsNaN(f) && !float.IsInfinity(f)
            && clusters.Any(c => Math.Abs(f - c.Centroid) <= SPARSE_FONT_TIER_TOLERANCE));
        if (!twoNarrowTiers) return null;

        float headingFont = clusters[0].Centroid;
        float bodyFont = clusters[1].Centroid;
        if (headingFont - bodyFont <= SPARSE_FONT_TIER_TOLERANCE) return null;
        if (headingFont < bodyFont * MIN_HEADING_FONT_RATIO || headingFont < bodyFont + MIN_HEADING_FONT_GAP)
            return null;

        int repeatedPages = 0;
        foreach (var page in allPageSegments)
        {
            var first = page.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Text));
            if (first is null) continue;
            if (!float.IsNaN(first.FontSize) && !float.IsInfinity(first.FontSize)
                && Math.Abs(first.FontSize - headingFont) <= SPARSE_FONT_TIER_TOLERANCE)
                repeatedPages++;
        }
        if (repeatedPages < SPARSE_REPEATED_TIER_MIN_PAGES) return null;

        return clusters
            .Select(c => (c.Centroid,
                Math.Abs(c.Centroid - headingFont) <= SPARSE_FONT_TIER_TOLERANCE
                    ? (byte?)SPARSE_REPEATED_TIER_HEADING_LEVEL
                    : null))
            .ToList();
    }

    private static List<FontSizeCluster> ClusterFontSizes(List<float> blockFonts, int k)
    {
        if (blockFonts.Count == 0) return new();
        if (k == 0) return new();
        int actualK = Math.Min(k, blockFonts.Count);

        var fontSizes = blockFonts.Where(f => !float.IsNaN(f) && !float.IsInfinity(f)).ToList();
        fontSizes.Sort((a, b) => b.CompareTo(a)); // descending
        // dedup within 0.05
        var deduped = new List<float>();
        foreach (var f in fontSizes)
            if (deduped.Count == 0 || Math.Abs(deduped[^1] - f) >= 0.05f) deduped.Add(f);
        fontSizes = deduped;

        var centroids = new List<float>();
        if (fontSizes.Count >= actualK)
        {
            int step = fontSizes.Count / actualK;
            for (int i = 0; i < actualK; i++)
            {
                int idx = i * step;
                centroids.Add(fontSizes[Math.Min(idx, fontSizes.Count - 1)]);
            }
        }
        else
        {
            centroids.AddRange(fontSizes);
            float minFont = fontSizes[^1];
            float maxFont = fontSizes[0];
            float range = maxFont - minFont;
            while (centroids.Count < actualK)
            {
                float t = (float)centroids.Count / (actualK - 1);
                centroids.Add(maxFont - t * range);
            }
            centroids.Sort((a, b) => b.CompareTo(a));
        }

        var allFonts = blockFonts;
        var prevAssign = new int[allFonts.Count];
        bool firstIter = true;

        for (int iter = 0; iter < KMEANS_MAX_ITERATIONS; iter++)
        {
            var sizeClusters = new List<float>[centroids.Count];
            for (int i = 0; i < centroids.Count; i++) sizeClusters[i] = new List<float>();
            var assign = new int[allFonts.Count];
            for (int fi = 0; fi < allFonts.Count; fi++)
            {
                float size = allFonts[fi];
                float minDist = float.PositiveInfinity; int best = 0;
                for (int i = 0; i < centroids.Count; i++)
                {
                    float d = Math.Abs(size - centroids[i]);
                    if (d < minDist) { minDist = d; best = i; }
                }
                sizeClusters[best].Add(size);
                assign[fi] = best;
            }

            int changed;
            if (firstIter) { firstIter = false; changed = 1; }
            else
            {
                changed = 0;
                for (int i = 0; i < assign.Length; i++) if (assign[i] != prevAssign[i]) changed++;
            }
            prevAssign = assign;
            if (changed == 0) break;

            var newCentroids = new List<float>(actualK);
            for (int i = 0; i < sizeClusters.Length; i++)
            {
                if (sizeClusters[i].Count > 0) newCentroids.Add(sizeClusters[i].Sum() / sizeClusters[i].Count);
                else newCentroids.Add(centroids[i]);
            }
            bool converged = true;
            for (int i = 0; i < centroids.Count; i++)
                if (Math.Abs(centroids[i] - newCentroids[i]) >= KMEANS_CONVERGENCE_THRESHOLD) { converged = false; break; }
            centroids = newCentroids;
            if (converged) break;
        }

        // Final assignment: count members per centroid (text len 0, so just membership counts).
        var memberLens = new List<int>[centroids.Count];
        for (int i = 0; i < centroids.Count; i++) memberLens[i] = new List<int>();
        foreach (var f in blockFonts)
        {
            float minDist = float.PositiveInfinity; int best = 0;
            for (int i = 0; i < centroids.Count; i++)
            {
                float d = Math.Abs(f - centroids[i]);
                if (d < minDist) { minDist = d; best = i; }
            }
            memberLens[best].Add(0);
        }

        var result = new List<FontSizeCluster>();
        for (int i = 0; i < actualK; i++)
            if (memberLens[i].Count > 0)
                result.Add(new FontSizeCluster { Centroid = centroids[i], MemberTextLens = memberLens[i] });

        result.Sort((a, b) => b.Centroid.CompareTo(a.Centroid));
        return result;
    }

    private static List<(float centroid, byte? level)> AssignHeadingLevelsSmart(List<FontSizeCluster> clusters, float minRatio, float minGap)
    {
        if (clusters.Count == 0) return new();
        if (clusters.Count == 1) return new() { (clusters[0].Centroid, (byte?)null) };

        // body = cluster with most text content; text len==0 for all → max_by_key returns LAST of equal maxima.
        int bodyIdx = 0; long bestSum = -1;
        for (int i = 0; i < clusters.Count; i++)
        {
            long sum = clusters[i].MemberTextLens.Sum(x => (long)x);
            if (sum >= bestSum) { bestSum = sum; bodyIdx = i; } // >= → keeps last on ties
        }

        float bodyCentroid = clusters[bodyIdx].Centroid;
        float minHeadingSize = bodyCentroid * minRatio;
        float minHeadingAbs = bodyCentroid + minGap;
        float threshold = Math.Min(minHeadingSize, minHeadingAbs);

        var candidates = new List<(int idx, float centroid)>();
        for (int i = 0; i < clusters.Count; i++)
            if (i != bodyIdx && clusters[i].Centroid >= threshold) candidates.Add((i, clusters[i].Centroid));
        candidates.Sort((a, b) => b.centroid.CompareTo(a.centroid));

        const int maxHeadings = 6;
        var result = new List<(float, byte?)>(clusters.Count);
        for (int i = 0; i < clusters.Count; i++)
        {
            if (i == bodyIdx) { result.Add((clusters[i].Centroid, null)); continue; }
            int pos = candidates.FindIndex(c => c.idx == i);
            if (pos >= 0 && pos < maxHeadings) result.Add((clusters[i].Centroid, (byte)(pos + 1)));
            else result.Add((clusters[i].Centroid, null));
        }
        return result;
    }

    // ── Gap detection + paragraph grouping (pipeline.rs) ─────────────────────────

    private static List<float> ComputeParagraphGapYs(List<SegmentData> segments)
    {
        if (segments.Count < 2) return new();
        var order = Enumerable.Range(0, segments.Count).ToList();
        order.Sort((a, b) => segments[b].Y.CompareTo(segments[a].Y));

        var lines = new List<(float top, float bottom, float height, bool mono, float anchor)>();
        foreach (var i in order)
        {
            var seg = segments[i];
            float tol = Math.Max(seg.Height * 0.5f, 1.0f);
            if (lines.Count > 0)
            {
                var last = lines[^1];
                if (Math.Abs(seg.Y - last.anchor) <= tol)
                {
                    lines[^1] = (Math.Max(last.top, seg.Y + seg.Height), Math.Min(last.bottom, seg.Y),
                        Math.Max(last.height, seg.Height), last.mono && seg.IsMonospace, last.anchor);
                    continue;
                }
            }
            lines.Add((seg.Y + seg.Height, seg.Y, seg.Height, seg.IsMonospace, seg.Y));
        }
        if (lines.Count < 2) return new();

        var heights = lines.Select(l => l.height).OrderBy(h => h).ToList();
        float medianHeight = heights[heights.Count / 2];
        float gapThreshold = medianHeight * PARAGRAPH_GAP_HEIGHT_FACTOR;
        float advanceThreshold = BodyLeading(lines, medianHeight) * PARAGRAPH_BREAK_LEADING_MULTIPLE;

        var gapYs = new List<float>();
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            float gap = lines[i].bottom - lines[i + 1].top;
            float advance = lines[i].anchor - lines[i + 1].anchor;
            if ((gap > gapThreshold || advance > advanceThreshold) && !(lines[i].mono && lines[i + 1].mono))
                gapYs.Add((lines[i].bottom + lines[i + 1].top) / 2.0f);
        }
        return gapYs;
    }

    /// <summary>
    /// The page's body leading, read off its own line pitch: the tightest baseline-to-baseline
    /// advance between consecutive lines, floored at the median line height.
    /// </summary>
    /// <remarks>
    /// The tightest advance is used rather than the median or the mode because a short
    /// block-structured page — a memo of five one-line blocks — has more break-sized advances
    /// than body-sized ones, so any central statistic reports the break spacing as normal and no
    /// break is ever found. The floor is what makes the minimum safe: an advance below one line
    /// height is not a wrapped line at all (stacked accents, a subscript resolved onto its own
    /// band) and must not shrink the estimate enough to split every ordinary line on the page.
    /// </remarks>
    private static float BodyLeading(
        List<(float top, float bottom, float height, bool mono, float anchor)> lines, float medianHeight)
    {
        float tightest = float.PositiveInfinity;
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            float advance = lines[i].anchor - lines[i + 1].anchor;
            if (float.IsFinite(advance) && advance > 0f && advance < tightest) tightest = advance;
        }
        return float.IsFinite(tightest) ? Math.Max(tightest, medianHeight) : medianHeight;
    }

    private static List<PdfParagraph> BlocksToParagraphs(List<SegmentData> lines, List<(float centroid, byte? level)> headingMap, List<float> gapYs)
    {
        if (lines.Count == 0) return new();
        float avgGap = PrecomputeAvgGap(headingMap);

        var paragraphs = new List<PdfParagraph>();
        var current = new List<SegmentData>();

        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            var line = lines[lineIdx];
            bool shouldBreak;
            if (current.Count == 0) shouldBreak = false;
            else
            {
                var prev = current[^1];
                bool fontChange = Math.Abs(line.FontSize - prev.FontSize) > 1.5f;
                bool boldChange = line.IsBold != prev.IsBold;
                bool startsNewLine = Math.Abs(line.BaselineY - prev.BaselineY) > 0.5f;
                bool hasSameLineFollower = lineIdx + 1 < lines.Count &&
                    Math.Abs(lines[lineIdx + 1].BaselineY - line.BaselineY) <= 0.5f;
                bool isList = LooksLikeListItem(line.Text) ||
                    (startsNewLine && hasSameLineFollower && IsBareListMarker(line.Text));
                bool crossedGap = false;
                foreach (var gapY in gapYs)
                {
                    float upper, lower;
                    if (prev.BaselineY > line.BaselineY) { upper = prev.BaselineY; lower = line.BaselineY; }
                    else { upper = line.BaselineY; lower = prev.BaselineY; }
                    if (gapY < upper && gapY > lower) { crossedGap = true; break; }
                }
                shouldBreak = fontChange || boldChange || isList || crossedGap;
            }

            if (shouldBreak && current.Count > 0)
            {
                var para = FinalizeParagraph(current, headingMap, avgGap);
                if (para != null) paragraphs.Add(para);
                current = new List<SegmentData>();
            }
            current.Add(line);
        }
        if (current.Count > 0)
        {
            var para = FinalizeParagraph(current, headingMap, avgGap);
            if (para != null) paragraphs.Add(para);
        }
        return paragraphs;
    }

    private static float PrecomputeAvgGap(List<(float centroid, byte? level)> headingMap)
    {
        if (headingMap.Count <= 1) return float.PositiveInfinity;
        var cs = headingMap.Select(h => h.centroid).OrderBy(c => c).ToList();
        var gaps = new List<float>();
        for (int i = 0; i + 1 < cs.Count; i++) gaps.Add(Math.Abs(cs[i + 1] - cs[i]));
        if (gaps.Count == 0) return float.PositiveInfinity;
        return gaps.Sum() / gaps.Count;
    }

    /// <summary>Centroid of the body cluster — the one the heading map left unlevelled.</summary>
    private static float BodyFontSize(List<(float centroid, byte? level)> headingMap)
    {
        foreach (var (c, l) in headingMap) if (l is null) return c;
        return 0f;
    }

    private static byte? FindHeadingLevel(float fontSize, List<(float centroid, byte? level)> headingMap, float avgGap)
    {
        if (headingMap.Count == 0) return null;
        if (headingMap.Count == 1) return headingMap[0].level;
        float bestDist = float.PositiveInfinity; byte? bestLevel = null;
        foreach (var (centroid, level) in headingMap)
        {
            float d = Math.Abs(fontSize - centroid);
            if (d < bestDist) { bestDist = d; bestLevel = level; }
        }
        if (bestDist > MAX_HEADING_DISTANCE_MULTIPLIER * avgGap) return null;
        return bestLevel;
    }

    private static List<PdfLine> ReconstructPdfLines(List<SegmentData> segments)
    {
        var lines = new List<PdfLine>();
        if (segments.Count == 0) return lines;
        float currentBaseline = segments[0].BaselineY;
        var cur = new List<SegmentData>();

        void Flush()
        {
            if (cur.Count == 0) return;
            float dom = 0f;
            foreach (var s in cur)
            {
                float b = s.FontSize;
                if (dom > 0f && b > dom / 2f && b < dom * 2f) dom = (dom + b) / 2f;
                else dom = Math.Max(dom, b);
            }
            bool isBold = cur.Count(s => s.IsBold) > cur.Count / 2;
            bool isMono = cur.All(s => s.IsMonospace);
            lines.Add(new PdfLine { Segments = cur.Select(s => s.Clone()).ToList(), BaselineY = currentBaseline, DominantFontSize = dom, IsBold = isBold, IsMonospace = isMono });
        }

        foreach (var seg in segments)
        {
            if (Math.Abs(seg.BaselineY - currentBaseline) > 0.5f)
            {
                Flush();
                currentBaseline = seg.BaselineY;
                cur = new List<SegmentData>();
            }
            cur.Add(seg.Clone());
        }
        Flush();
        return lines;
    }

    private static PdfParagraph? FinalizeParagraph(List<SegmentData> lines, List<(float centroid, byte? level)> headingMap, float avgGap)
    {
        if (lines.Count == 0) return null;
        string text = string.Join("\n", lines.Select(l => l.Text));
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        var first = lines[0];
        int wordCount = WordCount(trimmed);
        bool isBold = lines.Count(l => l.IsBold) > lines.Count / 2;
        var reconstructed = ReconstructPdfLines(lines);

        bool pageNumberLike = wordCount <= 10 && IsPageNumberPattern(trimmed);

        // Pass 1: font-size heading.
        byte? headingLevel = FindHeadingLevel(first.FontSize, headingMap, avgGap);
        if (headingLevel.HasValue && (wordCount > 20 || IsSeparatorText(trimmed) || pageNumberLike))
            headingLevel = null;

        // Pass 2: bold-at-body-size → H2.
        if (headingLevel is null && isBold && wordCount >= 1 && wordCount <= 8 && lines.Count == 1
            && !trimmed.EndsWith('.') && !trimmed.EndsWith(':') && !trimmed.EndsWith(',') && !trimmed.EndsWith(';')
            && !trimmed.Contains('@') && !trimmed.Contains('(') && !trimmed.Contains(',')
            && (char.IsUpper(trimmed[0]) || char.IsAsciiDigit(trimmed[0]))
            && !IsSeparatorText(trimmed) && !LooksLikeFigureLabel(trimmed))
            headingLevel = InferBoldHeadingLevel(first.FontSize, BodyFontSize(headingMap), trimmed);

        // Pass 3: font-above-body for short section-pattern paragraphs.
        if (headingLevel is null)
        {
            float bodyFont = BodyFontSize(headingMap);
            float minThreshold = bodyFont * MIN_HEADING_FONT_RATIO;
            if (bodyFont > 0f && first.FontSize >= minThreshold && first.FontSize > bodyFont + 0.5f
                && wordCount <= MAX_BOLD_HEADING_WORD_COUNT && lines.Count <= 2
                && !trimmed.EndsWith(':') && !trimmed.Contains('@')
                && (IsSectionPattern(trimmed) || IsStructuralHeadingWord(trimmed))
                && !IsSeparatorText(trimmed) && !LooksLikeFigureLabel(trimmed)
                && !LooksLikeListItem(trimmed) && !pageNumberLike)
                headingLevel = IsSectionPattern(trimmed) && StartsWithSectionNumber(trimmed)
                    ? InferSectionLevel(trimmed)
                    : (byte)2;
        }

        bool isListItem = headingLevel is null && LooksLikeListItem(trimmed);
        bool isCodeBlock = headingLevel is null && !isListItem && lines.All(l => l.IsMonospace) && lines.Count >= 2;
        bool isPageFurniture = headingLevel is null && !isListItem && !isCodeBlock && wordCount <= 10 && IsPageNumberPattern(trimmed);

        int finalWc = ComputeWordCount(trimmed, reconstructed);
        float l0 = lines.Min(l => l.X);
        float b0 = lines.Min(l => l.BaselineY);
        float r0 = lines.Max(l => l.X + l.Width);
        float t0 = lines.Max(l => l.BaselineY + l.Height);

        return new PdfParagraph
        {
            Text = trimmed,
            Lines = reconstructed,
            DominantFontSize = first.FontSize,
            HeadingLevel = headingLevel,
            IsBold = isBold,
            IsListItem = isListItem,
            IsCodeBlock = isCodeBlock,
            IsFormula = false,
            IsPageFurniture = isPageFurniture,
            BlockBbox = (l0, b0, r0, t0),
            WordCount = finalWc,
        };
    }

    private static int WordCount(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int ComputeWordCount(string text, List<PdfLine> lines)
    {
        if (text.Length > 0) return WordCount(text);
        int sum = 0;
        foreach (var l in lines) foreach (var s in l.Segments) sum += WordCount(s.Text);
        return sum;
    }

    // ── merge_continuation_paragraphs (paragraphs.rs) ────────────────────────────

    /// <summary>Apply a text repair to every segment of every paragraph, in place.</summary>
    private static void ApplyToAllSegments(List<PdfParagraph> paragraphs, Func<string, string> repair)
    {
        foreach (var para in paragraphs)
        {
            bool changed = false;
            foreach (var line in para.Lines)
                for (int i = 0; i < line.Segments.Count; i++)
                {
                    string repaired = repair(line.Segments[i].Text);
                    if (!ReferenceEquals(repaired, line.Segments[i].Text) && repaired != line.Segments[i].Text)
                    {
                        line.Segments[i].Text = repaired;
                        changed = true;
                    }
                }
            // The cached paragraph text was assembled from the segments; once they move it is
            // stale, and every reader falls back to rejoining the segments.
            if (changed) para.Text = "";
        }
    }

    private static void MergeContinuationParagraphs(List<PdfParagraph> paragraphs)
    {
        if (paragraphs.Count < 2) return;
        var old = new List<PdfParagraph>(paragraphs);
        paragraphs.Clear();
        var current = old[0];
        for (int idx = 1; idx < old.Count; idx++)
        {
            var next = old[idx];
            bool bothBody = current.HeadingLevel is null && next.HeadingLevel is null
                && !current.IsListItem && !next.IsListItem
                && !current.IsCodeBlock && !next.IsCodeBlock
                && !current.IsFormula && !next.IsFormula;
            bool fontsCompatible = Math.Abs(current.DominantFontSize - next.DominantFontSize) < 2.0f;
            // Never merge across a bold-state boundary. A bold run following non-bold prose (or
            // the reverse) is a formatting break — an emphasized heading, a list item's bold
            // lead-in — not a wrapped continuation, and absorbing it buries the heading as inline
            // bold before anything can classify it.
            bool boldCompatible = current.IsBold == next.IsBold;
            bool continuationSignal = !EndsWithSentenceTerminator(current) || StartsWithLowercaseContinuation(next);
            bool verticalGapCompatible = BaselinesWithinContinuationGap(current, next);
            // A numbered section heading starts a new element. It does not end in `.?!:;`, so the
            // continuation signal is satisfied by the *previous* heading alone — an `||` boost,
            // not a requirement — and a run of consecutive subsection headings would be rejoined
            // here even after the line grouper split them.
            bool nextStartsSection = StartsNumberedSection(next);
            if (bothBody && fontsCompatible && boldCompatible && continuationSignal
                && verticalGapCompatible && !nextStartsSection)
            {
                current.Text = "";
                current.BlockBbox = UnionBlockBbox(current.BlockBbox, next.BlockBbox);
                current.Lines.AddRange(next.Lines);
            }
            else { paragraphs.Add(current); current = next; }
        }
        paragraphs.Add(current);
    }

    /// <summary>
    /// Whether <paramref name="next"/>'s first baseline sits close enough below
    /// <paramref name="current"/>'s last to be a wrapped continuation rather than a spatially
    /// distant block — a recipient block at the top of an invoice and a legal footer at the
    /// bottom satisfy every textual signal and must still never be joined.
    /// </summary>
    /// <remarks>Paragraphs without per-line geometry are allowed to merge, gated by the other
    /// signals, since no vertical distance can be computed for them.</remarks>
    private static bool BaselinesWithinContinuationGap(PdfParagraph current, PdfParagraph next)
    {
        var currentLast = current.Lines.LastOrDefault();
        var nextFirst = next.Lines.FirstOrDefault();
        if (currentLast is null || nextFirst is null) return true;
        if (currentLast.BaselineY == 0f || nextFirst.BaselineY == 0f) return true;
        float gap = Math.Abs(currentLast.BaselineY - nextFirst.BaselineY);
        float lineHeight = Math.Max(Math.Max(current.DominantFontSize, next.DominantFontSize), 1.0f);
        return gap <= lineHeight * MAX_CONTINUATION_LINE_GAP_MULTIPLE;
    }

    /// <summary>Union of two block boxes, so a merged paragraph's box spans all of its text.</summary>
    private static (float L, float B, float R, float T)? UnionBlockBbox(
        (float L, float B, float R, float T)? current, (float L, float B, float R, float T)? next)
    {
        if (current is { } c && next is { } n)
            return (Math.Min(c.L, n.L), Math.Min(c.B, n.B), Math.Max(c.R, n.R), Math.Max(c.T, n.T));
        return current ?? next;
    }

    /// <summary>
    /// Whether a paragraph's first visual line reads as a numbered section heading
    /// ("1.3 Gasinstallatie", "IV. Results", "1. INTRODUCTION").
    /// </summary>
    /// <remarks>The line's segments are rejoined because word-processor output often splits the
    /// numbering into its own run, leaving the leading segment as a bare "1.3".</remarks>
    private static bool StartsNumberedSection(PdfParagraph para)
    {
        var firstLine = para.Lines.FirstOrDefault();
        if (firstLine is null) return false;
        string joined = string.Join(" ", firstLine.Segments
            .Select(s => s.Text.Trim())
            .Where(t => t.Length > 0));
        return IsNumberedSectionHeading(joined);
    }

    private static bool StartsWithLowercaseContinuation(PdfParagraph p)
    {
        var seg = p.Lines.FirstOrDefault()?.Segments.FirstOrDefault();
        string t = seg?.Text.TrimStart() ?? "";
        return t.Length > 0 && char.IsLower(t[0]);
    }

    private static bool EndsWithSentenceTerminator(PdfParagraph p)
    {
        var seg = p.Lines.LastOrDefault()?.Segments.LastOrDefault();
        string t = seg?.Text.TrimEnd() ?? "";
        if (t.Length == 0) return false;
        char c = t[^1];
        return c is '.' or '?' or '!' or ':' or ';' or '。' or '？' or '！';
    }

    private static void RetainPageFurnitureSafely(List<PdfParagraph> paragraphs)
    {
        int total = paragraphs.Count;
        int furniture = paragraphs.Count(p => p.IsPageFurniture);
        if (furniture == 0) return;
        if (furniture >= total)
        {
            foreach (var p in paragraphs) p.IsPageFurniture = false;
            return;
        }
        paragraphs.RemoveAll(p => p.IsPageFurniture);
    }

    // ── heading refinement (classify.rs) ─────────────────────────────────────────

    private static string ParagraphPlainText(PdfParagraph p) =>
        string.Join(" ", p.Lines.SelectMany(l => l.Segments).Select(s => s.Text));

    private static string EffectiveText(PdfParagraph p) => p.Text.Length > 0 ? p.Text : ParagraphPlainText(p);

    /// <summary>Words the lead clause must have before a colon can introduce a run-in list.</summary>
    private const int RUN_IN_LIST_MIN_LEAD_WORDS = 4;
    /// <summary>Clauses a run-in list must have; one semicolon is punctuation, not a list.</summary>
    private const int RUN_IN_LIST_MIN_ITEMS = 2;
    /// <summary>Words a single clause must have to read as an item rather than an aside.</summary>
    private const int RUN_IN_LIST_MIN_ITEM_WORDS = 3;

    /// <summary>
    /// Split "…the following: to exclude x; where y applies; unless z." into a lead paragraph
    /// and one list item per clause.
    /// </summary>
    /// <remarks>
    /// A run-in list is a list that the typesetter set as prose. Nothing in the geometry marks
    /// it — there are no bullets and no line breaks — so the only evidence is the colon and the
    /// semicolon-delimited clauses after it, and the gates are deliberately tight: a lead of
    /// several words, at least two clauses, and every clause substantial, lowercase-initial and
    /// clause-terminated. A capitalized clause is a new sentence, not an item.
    /// </remarks>
    private static void SplitColonSemicolonRunInLists(List<List<PdfParagraph>> allPageParagraphs)
    {
        foreach (var page in allPageParagraphs)
        {
            int index = 0;
            while (index < page.Count)
            {
                var replacement = TrySplitRunInList(page[index]);
                if (replacement is null) { index++; continue; }
                page.RemoveAt(index);
                page.InsertRange(index, replacement);
                index += replacement.Count;
            }
        }
    }

    private static List<PdfParagraph>? TrySplitRunInList(PdfParagraph para)
    {
        if (para.HeadingLevel.HasValue || para.IsListItem || para.IsCodeBlock
            || para.IsFormula || para.IsPageFurniture)
            return null;

        string normalized = string.Join(" ", EffectiveText(para)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        int colon = normalized.LastIndexOf(':');
        if (colon < 0) return null;

        string lead = normalized.Substring(0, colon + 1).Trim();
        if (WordCount(lead) < RUN_IN_LIST_MIN_LEAD_WORDS) return null;

        string tail = normalized.Substring(colon + 1).TrimStart();
        if (tail.Length == 0) return null;

        var items = SplitInclusive(tail, ';')
            .Select(seg => seg.Trim())
            .Where(seg => seg.Length > 0)
            .ToList();
        if (items.Count < RUN_IN_LIST_MIN_ITEMS || !items.All(IsProbableRunInListItem)) return null;

        var split = new List<PdfParagraph>(items.Count + 1) { RunInListFragment(para, lead, false) };
        foreach (var item in items) split.Add(RunInListFragment(para, item, true));
        return split;
    }

    /// <summary>Split on <paramref name="sep"/>, keeping it at the end of each piece.</summary>
    private static List<string> SplitInclusive(string text, char sep)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == sep) { parts.Add(text.Substring(start, i - start + 1)); start = i + 1; }
        if (start < text.Length) parts.Add(text.Substring(start));
        return parts;
    }

    /// <summary>
    /// Whether one semicolon-delimited clause reads as a genuine list item: substantial, a
    /// lowercase continuation of the lead sentence rather than a new capitalized one, and
    /// clause-terminated.
    /// </summary>
    private static bool IsProbableRunInListItem(string item)
    {
        if (WordCount(item) < RUN_IN_LIST_MIN_ITEM_WORDS) return false;
        if (item.Length == 0 || !char.IsLower(item[0])) return false;
        return item[^1] is ';' or '.';
    }

    /// <summary>One split-off fragment, inheriting the source's non-textual attributes.</summary>
    private static PdfParagraph RunInListFragment(PdfParagraph source, string text, bool isListItem) => new()
    {
        Text = text,
        Lines = new(),
        DominantFontSize = source.DominantFontSize,
        HeadingLevel = null,
        IsBold = source.IsBold,
        IsListItem = isListItem,
        IsCodeBlock = false,
        IsFormula = false,
        IsPageFurniture = source.IsPageFurniture,
        BlockBbox = source.BlockBbox,
        WordCount = WordCount(text),
    };

    /// <summary>
    /// Close a one-level hole in the heading hierarchy: a document with exactly one H1, no H2 at
    /// all and something at H3 or below has skipped a level, so everything below H2 moves up one.
    /// </summary>
    /// <remarks>
    /// Only that exact shape qualifies. A document with several H1s has no single title for the
    /// deeper levels to hang from, and one that already uses H2 has no hole to close.
    /// </remarks>
    private static void CompactFinalHeadingHierarchy(List<List<PdfParagraph>> allPages)
    {
        int h1Count = 0;
        bool hasH2 = false, hasDeeper = false;
        foreach (var para in allPages.SelectMany(p => p))
        {
            if (para.HeadingLevel is not byte level) continue;
            if (level == 1) h1Count++;
            if (level == 2) hasH2 = true;
            if (level >= 3) hasDeeper = true;
        }
        if (h1Count != 1 || hasH2 || !hasDeeper) return;

        foreach (var para in allPages.SelectMany(p => p))
            if (para.HeadingLevel is byte level && level >= 3) para.HeadingLevel = (byte)(level - 1);
    }

    private static void RefineHeadingHierarchy(List<List<PdfParagraph>> allPages)
    {
        int H1Count() => allPages.SelectMany(p => p).Count(p => p.HeadingLevel == 1);

        if (H1Count() == 0)
        {
            bool hasAny = allPages.SelectMany(p => p).Any(p => p.HeadingLevel.HasValue);
            if (hasAny && !HasRepeatedSparsePeerHeadingTier(allPages)) PromoteTitleHeading(allPages);

            bool stillNoH1 = !allPages.SelectMany(p => p).Any(p => p.HeadingLevel == 1);
            if (stillNoH1 && allPages.Count > 0 && allPages[0].Count > 0)
            {
                var page0 = allPages[0];
                int totalParagraphs = allPages.Sum(p => p.Count);
                float maxFont = page0.Max(p => p.DominantFontSize);
                var firstP = page0[0];
                string firstText = ParagraphPlainText(firstP);
                int firstWc = WordCount(firstText);

                // The first paragraph must also stand out from the rest of the document, not
                // merely be the largest thing on its own page.
                float? restFont = OtherParagraphsFontSize(allPages, 0, 0);
                bool clearsFontGate = restFont is not { } bodyFont
                    || bodyFont <= 0f
                    || (firstP.DominantFontSize >= bodyFont * MIN_HEADING_FONT_RATIO
                        && firstP.DominantFontSize >= bodyFont + MIN_HEADING_FONT_GAP);

                if (totalParagraphs >= MIN_BLOCKS_FOR_FONT_HEADING
                    && clearsFontGate
                    && firstP.DominantFontSize >= maxFont && firstWc <= 10 && firstWc > 0
                    && !firstP.IsPageFurniture && !LooksLikeBareUrl(firstText))
                    page0[0].HeadingLevel = 1;
            }
        }

        if (H1Count() <= 1) return;
        foreach (var page in allPages) MergeConsecutiveH1s(page);
        if (H1Count() <= 1) return;

        var firstH1 = allPages.SelectMany(p => p).FirstOrDefault(p => p.HeadingLevel == 1);
        bool firstH1IsTitle = firstH1 != null && !StartsWithSectionNumber(ParagraphPlainText(firstH1));
        if (!firstH1IsTitle) return;

        bool foundFirst = false;
        foreach (var page in allPages)
            foreach (var para in page)
                if (para.HeadingLevel == 1)
                {
                    if (!foundFirst) { foundFirst = true; continue; }
                    if (StartsWithSectionNumber(ParagraphPlainText(para))) para.HeadingLevel = 2;
                }
    }

    /// <summary>
    /// True when a sparse document repeats the same H2 tier at the start of several pages.
    /// </summary>
    /// <remarks>
    /// Those openings are peer sections. Promoting the first of them to H1 would say the
    /// document has a title and that this is it, which is exactly what the repetition denies —
    /// so title inference is skipped and they stay level peers.
    /// </remarks>
    private static bool HasRepeatedSparsePeerHeadingTier(List<List<PdfParagraph>> allPages)
    {
        int paragraphCount = allPages.Sum(p => p.Count);
        if (paragraphCount >= MIN_BLOCKS_FOR_FONT_HEADING) return false;

        var leadingH2Fonts = new List<float>();
        foreach (var page in allPages)
        {
            var first = page.FirstOrDefault(p =>
                !p.IsPageFurniture && ParagraphPlainText(p).Trim().Length > 0);
            if (first is null) continue;
            if (first.HeadingLevel == 2 && !float.IsNaN(first.DominantFontSize)
                && !float.IsInfinity(first.DominantFontSize))
                leadingH2Fonts.Add(first.DominantFontSize);
        }

        return leadingH2Fonts.Any(candidate =>
            leadingH2Fonts.Count(f => Math.Abs(f - candidate) <= SPARSE_PEER_HEADING_FONT_TOLERANCE)
                >= SPARSE_PEER_HEADING_MIN_PAGES);
    }

    /// <summary>Character-weighted mean font size of every paragraph except the excluded one —
    /// the document's body size, used to judge whether the first paragraph really stands out.</summary>
    private static float? OtherParagraphsFontSize(List<List<PdfParagraph>> allPages, int excludePage, int excludeIndex)
    {
        double weightedSum = 0;
        long totalChars = 0;
        for (int pageIdx = 0; pageIdx < allPages.Count; pageIdx++)
        {
            var page = allPages[pageIdx];
            for (int paraIdx = 0; paraIdx < page.Count; paraIdx++)
            {
                if (pageIdx == excludePage && paraIdx == excludeIndex) continue;
                int charCount = ParagraphPlainText(page[paraIdx]).Length;
                if (charCount == 0) continue;
                weightedSum += (double)page[paraIdx].DominantFontSize * charCount;
                totalChars += charCount;
            }
        }
        return totalChars == 0 ? null : (float)(weightedSum / totalChars);
    }

    private static void PromoteTitleHeading(List<List<PdfParagraph>> allPages)
    {
        if (allPages.Count == 0) return;
        var page = allPages[0];
        var headings = new List<(int idx, float fs)>();
        for (int i = 0; i < page.Count; i++) if (page[i].HeadingLevel.HasValue) headings.Add((i, page[i].DominantFontSize));
        if (headings.Count == 0) return;
        if (headings.Count == 1) { page[headings[0].idx].HeadingLevel = 1; return; }
        float maxSize = headings.Max(h => h.fs);
        float secondMax = headings.Where(h => h.fs < maxSize).Select(h => h.fs).DefaultIfEmpty(0f).Max();
        if (maxSize - secondMax >= 1.5f)
        {
            var idx = headings.First(h => h.fs == maxSize).idx;
            page[idx].HeadingLevel = 1;
        }
    }

    private static void MergeConsecutiveH1s(List<PdfParagraph> page)
    {
        int i = 0;
        while (i < page.Count)
        {
            if (page[i].HeadingLevel != 1) { i++; continue; }
            float baseFs = page[i].DominantFontSize;
            int runEnd = i + 1;
            while (runEnd < page.Count && page[runEnd].HeadingLevel == 1
                && Math.Abs(page[runEnd].DominantFontSize - baseFs) < 0.5f
                && LooksLikeTitleContinuation(page[runEnd - 1], page[runEnd]))
                runEnd++;
            if (runEnd - i > 1)
            {
                var mergedLines = page[i].Lines;
                var textParts = new List<string>();
                if (page[i].Text.Length > 0) textParts.Add(page[i].Text);
                for (int j = i + 1; j < runEnd; j++)
                {
                    mergedLines.AddRange(page[j].Lines);
                    if (page[j].Text.Length > 0) textParts.Add(page[j].Text);
                }
                page[i].Lines = mergedLines;
                if (textParts.Count > 0) page[i].Text = string.Join(" ", textParts);
                page.RemoveRange(i + 1, runEnd - (i + 1));
            }
            i++;
        }
    }

    private static bool LooksLikeTitleContinuation(PdfParagraph prev, PdfParagraph next)
    {
        string nextText = EffectiveText(next);
        if (LooksLikeStandaloneHeadingText(nextText)) return false;
        string prevText = EffectiveText(prev);
        string pt = prevText.TrimEnd();
        if (pt.Length > 0 && (pt[^1] is '.' or '!' or '?' or ':')) return false;
        return WordCount(prevText) <= 4 && WordCount(nextText) <= 4;
    }

    private static bool LooksLikeStandaloneHeadingText(string text)
    {
        string trimmed = text.Trim();
        var words = trimmed.Split(' ');
        string firstW = words.Length > 0 ? words[0] : "";
        string secondW = words.Length > 1 ? words[1] : "";
        if (firstW.Length > 0 && char.IsAsciiDigit(firstW[0])) return true;
        bool fAlpha = firstW.Any(c => char.IsAsciiLetter(c));
        bool fDigit = firstW.Any(c => char.IsAsciiDigit(c));
        if (fAlpha && fDigit) return true;
        if (secondW.Length > 0 && firstW.All(c => char.IsAsciiLetter(c)) && char.IsAsciiDigit(secondW[0])) return true;
        return false;
    }

    private static void DemoteUnnumberedSubsections(List<List<PdfParagraph>> allPages)
    {
        var h2Info = new List<(int page, int para, bool numbered)>();
        for (int pi = 0; pi < allPages.Count; pi++)
            for (int qi = 0; qi < allPages[pi].Count; qi++)
                if (allPages[pi][qi].HeadingLevel == 2)
                    h2Info.Add((pi, qi, StartsWithSectionNumber(ParagraphPlainText(allPages[pi][qi]))));

        int numberedCount = h2Info.Count(x => x.numbered);
        if (numberedCount < 3) return;

        var numberedPos = new List<int>();
        for (int i = 0; i < h2Info.Count; i++) if (h2Info[i].numbered) numberedPos.Add(i);

        for (int w = 0; w + 1 < numberedPos.Count; w++)
        {
            int start = numberedPos[w], end = numberedPos[w + 1];
            for (int k = start + 1; k < end; k++)
            {
                var (pg, pa, isNum) = h2Info[k];
                if (!isNum) allPages[pg][pa].HeadingLevel = 3;
            }
        }
    }

    private static void DemoteHeadingRuns(List<List<PdfParagraph>> allPages)
    {
        const int MAX_CONSECUTIVE = 3;
        foreach (var page in allPages)
        {
            int runStart = 0;
            while (runStart < page.Count)
            {
                if (page[runStart].HeadingLevel is not byte level) { runStart++; continue; }
                int runEnd = runStart + 1;
                while (runEnd < page.Count && page[runEnd].HeadingLevel == level) runEnd++;
                if (runEnd - runStart > MAX_CONSECUTIVE)
                    for (int k = runStart + 1; k < runEnd; k++) page[k].HeadingLevel = null;
                runStart = runEnd;
            }
        }
    }

    // ── repeating text / arxiv noise (classify.rs) ───────────────────────────────

    private static void MarkCrossPageRepeatingText(List<List<PdfParagraph>> allPages, float[] pageHeights)
    {
        if (allPages.Count < 4) return;
        const float marginFrac = 0.10f;

        var textPageCount = new Dictionary<string, int>();
        var alphanumToExact = new Dictionary<string, HashSet<string>>();
        var firstSeen = new Dictionary<string, int>();

        for (int pi = 0; pi < allPages.Count; pi++)
        {
            float ph = pi < pageHeights.Length ? pageHeights[pi] : 792.0f;
            float topMargin = ph * (1f - marginFrac);
            float bottomMargin = ph * marginFrac;
            var seen = new HashSet<string>();
            foreach (var para in allPages[pi])
            {
                if (para.IsPageFurniture) continue;
                bool inMargin = para.BlockBbox is { } bb && (bb.T > topMargin || bb.B < bottomMargin);
                if (!inMargin) continue;
                string norm = ParagraphPlainText(para).Trim().ToLowerInvariant();
                if (norm.Length == 0) continue;
                string key = new string(norm.Where(char.IsLetterOrDigit).ToArray());
                if (key.Length == 0) continue;
                if (!alphanumToExact.TryGetValue(key, out var set)) alphanumToExact[key] = set = new();
                set.Add(norm);
                if (seen.Add(key))
                {
                    if (!textPageCount.TryGetValue(key, out int c) || c == 0) firstSeen[key] = pi;
                    textPageCount[key] = c + 1;
                }
            }
        }

        int threshold = allPages.Count / 2;
        var repeating = new HashSet<string>();
        foreach (var (key, count) in textPageCount)
            if (count > threshold && alphanumToExact.TryGetValue(key, out var variants))
                foreach (var v in variants) repeating.Add(v);
        if (repeating.Count == 0) return;

        for (int pi = 0; pi < allPages.Count; pi++)
        {
            float ph = pi < pageHeights.Length ? pageHeights[pi] : 792.0f;
            float topMargin = ph * (1f - marginFrac);
            float bottomMargin = ph * marginFrac;
            foreach (var para in allPages[pi])
            {
                if (para.IsPageFurniture) continue;
                bool inMargin = para.BlockBbox is { } bb && (bb.T > topMargin || bb.B < bottomMargin);
                if (!inMargin) continue;
                string norm = ParagraphPlainText(para).Trim().ToLowerInvariant();
                if (repeating.Contains(norm))
                {
                    string key = new string(norm.Where(char.IsLetterOrDigit).ToArray());
                    if (firstSeen.TryGetValue(key, out int fp) && fp == pi) continue;
                    para.IsPageFurniture = true;
                    para.HeadingLevel = null;
                }
            }
        }
    }

    private static void MarkCrossPageRepeatingShortText(List<List<PdfParagraph>> allPages)
    {
        if (allPages.Count < 5) return;
        const int maxWords = 20;
        int threshold = (int)Math.Ceiling(allPages.Count * 0.7);

        var textPageCount = new Dictionary<string, int>();
        var firstSeen = new Dictionary<string, int>();
        for (int pi = 0; pi < allPages.Count; pi++)
        {
            var seen = new HashSet<string>();
            foreach (var para in allPages[pi])
            {
                if (para.IsPageFurniture) continue;
                string norm = ParagraphPlainText(para).Trim().ToLowerInvariant();
                if (norm.Length == 0) continue;
                if (WordCount(norm) > maxWords) continue;
                string key = new string(norm.Where(char.IsLetterOrDigit).ToArray());
                if (key.Length == 0) continue;
                if (seen.Add(key))
                {
                    if (!textPageCount.TryGetValue(key, out int c) || c == 0) firstSeen[key] = pi;
                    textPageCount[key] = c + 1;
                }
            }
        }

        var repeating = new HashSet<string>();
        foreach (var (key, count) in textPageCount) if (count >= threshold) repeating.Add(key);
        if (repeating.Count == 0) return;

        for (int pi = 0; pi < allPages.Count; pi++)
            foreach (var para in allPages[pi])
            {
                if (para.IsPageFurniture) continue;
                string norm = ParagraphPlainText(para).Trim().ToLowerInvariant();
                if (WordCount(norm) > maxWords) continue;
                string key = new string(norm.Where(char.IsLetterOrDigit).ToArray());
                if (key.Length == 0) continue;
                if (repeating.Contains(key))
                {
                    if (firstSeen.TryGetValue(key, out int fp) && fp == pi) continue;
                    para.IsPageFurniture = true;
                    para.HeadingLevel = null;
                }
            }
    }

    private static readonly Regex ArxivRe = new(@"arXiv:\d{4}\.\d{4,5}", RegexOptions.Compiled);
    private static readonly Regex ArxivTrailingRe = new(
        @"(?:\s+(?:\S+\s+){0,8})?arXiv:\d{4}\.\d{4,5}(?:v\d+)?(?:\s*\[[\w.-]+\])?\s*(?:\d{1,2}\s+\w+\s+\d{4})?\s*$",
        RegexOptions.Compiled);

    private static void MarkArxivNoise(List<List<PdfParagraph>> allPages)
    {
        for (int pi = 0; pi < Math.Min(2, allPages.Count); pi++)
            foreach (var para in allPages[pi])
            {
                if (para.IsPageFurniture) continue;
                string trimmed = ParagraphPlainText(para).Trim();
                int wc = WordCount(trimmed);
                if (!ArxivRe.IsMatch(trimmed)) continue;
                if (wc <= 25) { para.IsPageFurniture = true; para.HeadingLevel = null; }
                else
                {
                    var m = ArxivTrailingRe.Match(trimmed);
                    if (m.Success) StripTrailingTextFromParagraph(para, trimmed.Substring(m.Index).Trim());
                }
            }
    }

    private static void StripTrailingTextFromParagraph(PdfParagraph para, string noise)
    {
        for (int li = para.Lines.Count - 1; li >= 0; li--)
        {
            var line = para.Lines[li];
            for (int si = line.Segments.Count - 1; si >= 0; si--)
            {
                var seg = line.Segments[si];
                int pos = seg.Text.IndexOf(noise, StringComparison.Ordinal);
                if (pos >= 0) { seg.Text = seg.Text.Substring(0, pos).TrimEnd(); return; }
                string segTrimmed = seg.Text.Trim();
                if (segTrimmed.Length > 0 && noise.Contains(segTrimmed, StringComparison.Ordinal)) seg.Text = "";
                else return;
            }
        }
    }

    private static void DeduplicateParagraphs(List<List<PdfParagraph>> allPages)
    {
        foreach (var page in allPages)
        {
            if (page.Count < 2) continue;
            int i = 0;
            while (i + 1 < page.Count)
            {
                string a = ParagraphTextNormalized(page[i]);
                string b = ParagraphTextNormalized(page[i + 1]);
                if (a.Length >= 5 && a == b) page.RemoveAt(i + 1);
                else i++;
            }
            var seen = new HashSet<string>();
            var toRemove = new List<int>();
            for (int idx = 0; idx < page.Count; idx++)
            {
                if (!IsDedupCandidate(page[idx])) continue;
                string text = ParagraphTextNormalized(page[idx]);
                if (text.Length < 15) continue;
                if (!seen.Add(text)) toRemove.Add(idx);
            }
            for (int r = toRemove.Count - 1; r >= 0; r--) page.RemoveAt(toRemove[r]);
        }
    }

    private static string ParagraphTextNormalized(PdfParagraph p)
    {
        string raw = p.Text.Length == 0 ? ParagraphPlainText(p) : p.Text;
        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static bool IsDedupCandidate(PdfParagraph p) =>
        p.HeadingLevel is null && !p.IsListItem && !p.IsCodeBlock && !p.IsFormula && !p.IsPageFurniture;

    // ── predicates (classify.rs / pipeline.rs / layout_classify.rs / regions) ────

    private static bool IsBareListMarker(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || t.Length > 5) return false;
        if (t is "•" or "·" or "◦" or "▪" or "–" or "—" or "-" or "*") return true;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            string inner = t.Substring(1, t.Length - 2);
            return inner.Length > 0 && inner.All(char.IsLetterOrDigit);
        }
        if (t.EndsWith('.') || t.EndsWith(')'))
        {
            string body = t.Substring(0, t.Length - 1);
            return body.Length > 0 && body.Length <= 2 && body.All(char.IsLetterOrDigit);
        }
        return false;
    }

    /// <summary>
    /// Whether a line opens with a list marker. Ports <c>looks_like_list_item</c>: a bullet
    /// glyph, a dash followed by a space, or an ordered marker that has both a separator and
    /// alphabetic content after it.
    /// </summary>
    private static bool LooksLikeListItem(string text)
    {
        string t = text.TrimStart();
        if (t.Length == 0) return false;

        if (t[0] is '\u2022' or '\u00b7' or '\u25e6' or '\u25aa') return true;

        if (t[0] is '\u2013' or '\u2014')
        {
            string rest = t[1..];
            if (!rest.StartsWith(' ') && !rest.StartsWith('\t')) return false;
            string body = rest.TrimStart(' ', '\t');
            return body.Length != 0 && body[0] is not ('\r' or '\n');
        }

        if (t.StartsWith("- "))
        {
            string rest = t[2..];
            return rest.Length != 0 && char.IsLetter(rest[0]);
        }

        if (IsNumberedSectionHeading(t)) return false;

        if (PdfListMarker.Parse(t) is not { } marker) return false;
        return marker.HasContent
            && marker.HasSeparator
            && !PdfListMarker.IsProbableAuthorByline(t)
            && marker.ContentStart < t.Length
            && char.IsLetter(t[marker.ContentStart]);
    }

    private static bool IsStructuralHeadingWord(string text) => text.Trim() switch
    {
        "Abstract" or "References" or "Appendix" or "Acknowledgments" or "Acknowledgements"
        or "Conclusion" or "Conclusions" or "Bibliography" or "Contents" or "Index"
        or "Glossary" or "Summary" or "Discussion" or "Methods" or "Results" or "Methodology" => true,
        _ => false,
    };

    private static bool IsPageNumberPattern(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return false;
        if (t.All(char.IsAsciiDigit) && t.Length <= 4) return true;
        string lower = t.ToLowerInvariant();
        if (lower.StartsWith("page ")) return true;
        if ((t.StartsWith("- ") || t.StartsWith("– ")) && (t.EndsWith(" -") || t.EndsWith(" –")))
        {
            string inner = t.TrimStart('-', '–', ' ').TrimEnd('-', '–', ' ').Trim();
            if (inner.All(char.IsAsciiDigit) && inner.Length <= 4) return true;
        }
        if (t.Length <= 5 && t.All(c => c is 'i' or 'v' or 'x' or 'I' or 'V' or 'X')) return true;
        return false;
    }

    private static bool IsSeparatorText(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        int total = trimmed.Length;
        int alnum = trimmed.Count(char.IsLetterOrDigit);
        if (alnum == 0) return true;
        return total >= 6 && ((double)alnum / total) < 0.15;
    }

    private static bool LooksLikeFigureLabel(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3 && words.All(w => w.Length <= 1)) return true;
        if (words.Length >= 5)
            foreach (var w in words)
            {
                string lw = w.ToLowerInvariant();
                if (words.Count(x => x.ToLowerInvariant() == lw) >= 3) return true;
            }
        return false;
    }

    private static bool LooksLikeBareUrl(string text)
    {
        string t = text.Trim();
        return (t.StartsWith("http://") || t.StartsWith("https://") || t.StartsWith("www.")) && !t.Any(char.IsWhiteSpace);
    }

    private static bool IsSectionPattern(string text)
    {
        string t = text.Trim();
        if (t.StartsWith('§')) return true;
        int words = WordCount(t);
        if (words <= 6 && t.Where(char.IsLetter).All(char.IsUpper)) return true;
        return StartsWithSectionNumber(t);
    }

    private static bool IsNumberedSectionHeading(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return false;
        // Roman-numeral markers.
        int romanEnd = 0;
        while (romanEnd < t.Length && "IVXLCDM".IndexOf(t[romanEnd]) >= 0) romanEnd++;
        if (romanEnd > 0 && romanEnd < t.Length && (t[romanEnd] is '.' or ' ' or ')') && IsValidRoman(t.Substring(0, romanEnd)))
            return true;

        int levels = 0, idx = 0;
        while (true)
        {
            int digitLen = 0;
            while (idx + digitLen < t.Length && char.IsAsciiDigit(t[idx + digitLen])) digitLen++;
            if (digitLen == 0) break;
            levels++;
            idx += digitLen;
            if (idx < t.Length && t[idx] == '.' && idx + 1 < t.Length && char.IsAsciiDigit(t[idx + 1])) idx++;
            else break;
        }
        if (levels == 0) return false;
        if (levels >= 2) return true;
        if (idx < t.Length && (t[idx] == '.' || t[idx] == ')')) idx++;
        string remainder = idx < t.Length ? t.Substring(idx).TrimStart() : "";
        return remainder.Any(char.IsLetter) && remainder.Where(char.IsLetter).All(char.IsUpper);
    }

    /// <summary>
    /// Infer a heading level for a bold/large paragraph. Section numbering wins; otherwise the
    /// level comes from how far the font rises above body text, and a document with no body
    /// baseline at all gets H2 rather than H1 (Rust <c>infer_bold_heading_level</c>).
    /// </summary>
    internal static byte InferBoldHeadingLevel(float fontSize, float bodyFontSize, string text)
    {
        if (StartsWithSectionNumber(text)) return InferSectionLevel(text);
        if (bodyFontSize > 0f) return fontSize / bodyFontSize > 1.2f ? (byte)2 : (byte)3;
        return 2;
    }

    /// <summary>
    /// Infer heading level from section numbering depth: "1 Introduction" / "I. INTRO" /
    /// "A. Proofs" are H2, "1.1 Details" is H3, "1.1.1 Deep" is H4
    /// (Rust <c>infer_section_level</c>).
    /// </summary>
    internal static byte InferSectionLevel(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return 2;

        char firstChar = trimmed[0];
        bool isAlphaPrefix = char.IsAsciiLetter(firstChar) && trimmed.Length >= 2
            && trimmed[1] is '.' or ')' or ' ';

        int numberingEnd;
        if (isAlphaPrefix)
        {
            string afterLetter = trimmed[1..];
            int restEnd = 0;
            while (restEnd < afterLetter.Length
                   && (char.IsAsciiDigit(afterLetter[restEnd]) || afterLetter[restEnd] == '.')) restEnd++;
            if (restEnd == afterLetter.Length) restEnd = 0;
            numberingEnd = 1 + restEnd;
        }
        else
        {
            int romanEnd = 0;
            while (romanEnd < trimmed.Length && "IVXLCDM".IndexOf(trimmed[romanEnd]) >= 0) romanEnd++;
            if (romanEnd > 0 && romanEnd <= 5 && romanEnd < trimmed.Length)
            {
                char next = trimmed[romanEnd];
                if ((next is '.' or ' ' or ')') && IsValidRoman(trimmed[..romanEnd])) return 2;
            }
            int end = 0;
            while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] == '.')) end++;
            numberingEnd = end == trimmed.Length ? 0 : end;
        }

        if (numberingEnd == 0) return 2;

        string numbering = trimmed[..numberingEnd];
        int dotCount = numbering.Count(c => c == '.');
        int effectiveDots = numbering.EndsWith('.') ? Math.Max(0, dotCount - 1) : dotCount;
        return effectiveDots switch { 0 => (byte)2, 1 => (byte)3, _ => (byte)4 };
    }

    private static bool StartsWithSectionNumber(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        int digitEnd = 0;
        while (digitEnd < trimmed.Length && char.IsAsciiDigit(trimmed[digitEnd])) digitEnd++;
        if (digitEnd > 0 && digitEnd < trimmed.Length)
        {
            char next = trimmed[digitEnd];
            if (next is ' ' or '.' or ')') return true;
        }
        int romanEnd = 0;
        while (romanEnd < trimmed.Length && "IVXLCDM".IndexOf(trimmed[romanEnd]) >= 0) romanEnd++;
        if (romanEnd > 0 && romanEnd <= 5 && romanEnd < trimmed.Length)
        {
            char next = trimmed[romanEnd];
            if ((next is '.' or ' ' or ')') && IsValidRoman(trimmed.Substring(0, romanEnd))) return true;
        }
        return false;
    }

    private static bool IsValidRoman(string s) => s switch
    {
        "I" or "II" or "III" or "IV" or "V" or "VI" or "VII" or "VIII" or "IX" or "X"
        or "XI" or "XII" or "XIII" or "XIV" or "XV" or "XVI" or "XVII" or "XVIII" or "XIX" or "XX" => true,
        _ => false,
    };

    // ── assembly (assembly.rs) ───────────────────────────────────────────────────

    private static InternalDocument AssembleInternalDocument(List<List<PdfParagraph>> pages)
    {
        var builder = new InternalDocumentBuilder("pdf");
        bool hasEmitted = false;
        for (int pageIdx = 0; pageIdx < pages.Count; pageIdx++)
        {
            var paragraphs = pages[pageIdx];
            uint pageNum = (uint)(pageIdx + 1);
            bool pageHasContent = paragraphs.Count > 0;
            if (pageHasContent && hasEmitted) builder.PushPageBreak();
            AssemblePageElements(builder, paragraphs, pageNum);
            if (pageHasContent) hasEmitted = true;
        }
        return builder.Build();
    }

    private static void AssemblePageElements(InternalDocumentBuilder builder, List<PdfParagraph> paragraphs, uint page)
    {
        bool inList = false;
        foreach (var para in paragraphs)
        {
            if (para.IsListItem && !inList) { builder.PushList(ListItemIsOrdered(para)); inList = true; }
            else if (!para.IsListItem && inList) { builder.EndList(); inList = false; }
            PushParagraphElement(builder, para, page);
        }
        if (inList) builder.EndList();
    }

    private static BoundingBox? Bbox(PdfParagraph p) => p.BlockBbox is { } bb
        ? new BoundingBox { X0 = bb.L, Y0 = bb.B, X1 = bb.R, Y1 = bb.T } : null;

    private static string GetText(PdfParagraph para) =>
        FinalizeHyphens(para.Text.Length > 0 ? para.Text : JoinLineTextsPlain(para.Lines));

    private static void PushParagraphElement(InternalDocumentBuilder builder, PdfParagraph para, uint page)
    {
        var bbox = Bbox(para);
        if (para.HeadingLevel is byte level) { builder.PushHeading(level, GetText(para), page, bbox); return; }

        if (para.IsCodeBlock)
        {
            string text = para.Text.Length > 0 ? para.Text
                : string.Join("\n", para.Lines.Select(l => CollapseInnerSpaces(string.Join(" ", l.Segments.Select(s => s.Text)))));
            builder.PushCode(text, null, page, bbox);
            return;
        }

        if (para.IsFormula) { builder.PushFormula(GetText(para), page, bbox); return; }

        if (para.IsListItem)
        {
            string text = GetText(para);
            bool ordered = ListItemIsOrdered(para);
            string normalized = NormalizeListText(text);
            List<TextAnnotation> anns;
            if (para.Text.Length > 0 && para.IsBold)
                anns = new() { new TextAnnotation { Start = 0, End = (uint)Utf8Len(normalized), Kind = AnnotationKind.Bold } };
            else if (para.Text.Length == 0)
                (_, anns) = ExtractTextAndAnnotations(para);
            else anns = new();
            builder.PushListItem(normalized, ordered, anns, page, bbox);
            return;
        }

        if (para.IsPageFurniture)
        {
            string text = GetText(para);
            uint idx = builder.PushParagraph(text, new(), page, bbox);
            builder.SetLayer(idx, GuessFurnitureLayer(para));
            return;
        }

        // Default body paragraph.
        if (para.Text.Length > 0)
        {
            var anns = para.IsBold
                ? new List<TextAnnotation> { new TextAnnotation { Start = 0, End = (uint)Utf8Len(para.Text), Kind = AnnotationKind.Bold } }
                : new List<TextAnnotation>();
            builder.PushParagraph(FinalizeHyphens(para.Text), anns, page, bbox);
        }
        else
        {
            var (text, anns) = ExtractTextAndAnnotations(para);
            builder.PushParagraph(text, anns, page, bbox);
        }
    }

    private static ContentLayer GuessFurnitureLayer(PdfParagraph para)
    {
        // Page numbers etc.; header vs footer by vertical position is approximate.
        // Rust guess_furniture_layer defaults based on position; use Footer for low bbox, else Header.
        if (para.BlockBbox is { } bb) return bb.B < 200f ? ContentLayer.Footer : ContentLayer.Header;
        return ContentLayer.Header;
    }

    private static (string, List<TextAnnotation>) ExtractTextAndAnnotations(PdfParagraph para)
    {
        var all = para.Lines.SelectMany(l => l.Segments).ToList();
        if (all.Count == 0) return ("", new());
        var text = new StringBuilder();
        var annotations = new List<TextAnnotation>();
        int i = 0;
        while (i < all.Count)
        {
            bool bold = all[i].IsBold;
            bool italic = all[i].IsItalic;
            int runStart = i;
            while (i < all.Count && all[i].IsBold == bold && all[i].IsItalic == italic) i++;

            var runWords = new List<string>();
            for (int k = runStart; k < i; k++)
                foreach (var w in all[k].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    runWords.Add(w);

            if (text.Length > 0 && runWords.Count > 0)
            {
                string prevLast = all[runStart - 1].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                string nextFirst = all[runStart].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (ShouldDehyphenate(prevLast, nextFirst)) { if (text.Length > 0) text.Remove(text.Length - 1, 1); }
                else if (NeedsSpaceBetween(prevLast, nextFirst)) text.Append(' ');
            }

            int spanStart = Utf8Len(text.ToString());
            for (int wi = 0; wi < runWords.Count; wi++)
            {
                if (wi > 0)
                {
                    string prev = runWords[wi - 1];
                    if (ShouldDehyphenate(prev, runWords[wi])) { if (text.Length > 0) text.Remove(text.Length - 1, 1); }
                    else if (NeedsSpaceBetween(prev, runWords[wi])) text.Append(' ');
                }
                text.Append(runWords[wi]);
            }
            int spanEnd = Utf8Len(text.ToString());

            if (spanStart < spanEnd)
            {
                if (bold) annotations.Add(new TextAnnotation { Start = (uint)spanStart, End = (uint)spanEnd, Kind = AnnotationKind.Bold });
                if (italic) annotations.Add(new TextAnnotation { Start = (uint)spanStart, End = (uint)spanEnd, Kind = AnnotationKind.Italic });
            }
        }
        return (text.ToString(), annotations);
    }

    private static string JoinLineTextsPlain(List<PdfLine> lines)
    {
        if (lines.Count == 0) return "";
        var wordsPerLine = lines.Select(l => l.Segments.SelectMany(s => s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList()).ToList();
        var result = new StringBuilder();
        for (int li = 0; li < wordsPerLine.Count; li++)
        {
            var lineWords = wordsPerLine[li];
            for (int wi = 0; wi < lineWords.Count; wi++)
            {
                string word = lineWords[wi];
                if (result.Length == 0) { result.Append(word); continue; }
                string prevWord;
                if (wi > 0) prevWord = lineWords[wi - 1];
                else
                {
                    prevWord = "";
                    for (int p = li - 1; p >= 0; p--) { if (wordsPerLine[p].Count > 0) { prevWord = wordsPerLine[p][^1]; break; } }
                }
                if (ShouldDehyphenate(prevWord, word)) { if (result.Length > 0) result.Remove(result.Length - 1, 1); result.Append(word); }
                else if (NeedsSpaceBetween(prevWord, word)) { result.Append(' '); result.Append(word); }
                else result.Append(word);
            }
        }
        return result.ToString();
    }

    private static bool ShouldDehyphenate(string prev, string next)
    {
        if (prev.Length < 2 || !prev.EndsWith('-')) return false;
        char? beforeHyphen = prev.Length >= 2 ? prev[^2] : (char?)null;
        if (!(beforeHyphen.HasValue && char.IsLetter(beforeHyphen.Value))) return false;
        return next.Length > 0 && char.IsLower(next[0]);
    }

    private static bool NeedsSpaceBetween(string prev, string next)
    {
        bool prevCjk = prev.Length > 0 && IsCjkChar(prev[^1]);
        bool nextCjk = next.Length > 0 && IsCjkChar(next[0]);
        return !(prevCjk && nextCjk);
    }

    private static bool IsCjkChar(char c)
    {
        int cp = c;
        return (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3040 && cp <= 0x309F) || (cp >= 0x30A0 && cp <= 0x30FF)
            || (cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0xF900 && cp <= 0xFAFF);
    }

    private static string CollapseInnerSpaces(string line)
    {
        int leading = line.Length - line.TrimStart(' ').Length;
        string prefix = line.Substring(0, leading);
        string rest = line.Substring(leading);
        if (!rest.Contains("  ")) return line;
        var result = new StringBuilder(line.Length);
        result.Append(prefix);
        bool prevSpace = false;
        foreach (char ch in rest)
        {
            if (ch == ' ') { if (!prevSpace) result.Append(ch); prevSpace = true; }
            else { prevSpace = false; result.Append(ch); }
        }
        return result.ToString();
    }

    private static bool ListItemIsOrdered(PdfParagraph para)
    {
        string text = para.Text.Length > 0 ? para.Text
            : (para.Lines.FirstOrDefault()?.Segments.FirstOrDefault()?.Text ?? "");
        string t = text.TrimStart();
        int digitEnd = 0;
        while (digitEnd < t.Length && char.IsAsciiDigit(t[digitEnd])) digitEnd++;
        return digitEnd > 0 && digitEnd < t.Length && (t[digitEnd] == '.' || t[digitEnd] == ')');
    }

    private static readonly char[] DashBullets = { '–', '—', '−', '‐', '‑', '‒', '―', '➤', '►', '▶', '○', '●', '◦' };

    private static string NormalizeListText(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('•')) return trimmed.Substring(1).TrimStart();
        if (trimmed.StartsWith('·')) return trimmed.Substring(1).TrimStart();
        if (trimmed.StartsWith("* ")) return trimmed.Substring(2).TrimStart();
        if (trimmed.StartsWith("- ")) return trimmed.Substring(2);
        foreach (char ch in DashBullets) if (trimmed.StartsWith(ch)) return trimmed.Substring(1).TrimStart();
        // Numbered prefix "1. " / "1) "
        int digitEnd = 0;
        while (digitEnd < trimmed.Length && char.IsAsciiDigit(trimmed[digitEnd])) digitEnd++;
        if (digitEnd > 0 && digitEnd < trimmed.Length && (trimmed[digitEnd] == '.' || trimmed[digitEnd] == ')'))
        {
            int after = digitEnd + 1;
            if (after < trimmed.Length && trimmed[after] == ' ') return trimmed.Substring(after + 1);
        }
        return trimmed;
    }

    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    // ── text repair (text_repair.rs) ─────────────────────────────────────────────

    private static string RepairContextualLigatures(string text)
    {
        if (text.Length < 2) return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = new StringBuilder(text.Length + 16);
        bool repaired = false;
        bool prevIsAlpha = false;
        bool prevIsSpaceOrStart = true;
        int byteIdx = 0;

        // Iterate by Unicode scalar (mirrors Rust `chars()`); next-char tests read the raw
        // next UTF-8 byte cast to char, so they are only true for ASCII letters.
        foreach (var rune in text.EnumerateRunes())
        {
            string chStr = rune.ToString();
            int charLen = Encoding.UTF8.GetByteCount(chStr);
            int nextByteIdx = byteIdx + charLen;
            char ch = chStr[0];

            bool nextIsAlpha = nextByteIdx < bytes.Length && char.IsLetter((char)bytes[nextByteIdx]);
            bool nextIsLower = nextByteIdx < bytes.Length && char.IsLower((char)bytes[nextByteIdx]);
            bool nextIsVowel = nextByteIdx < bytes.Length && "aeiouAEIOU".IndexOf((char)bytes[nextByteIdx]) >= 0;

            switch (ch)
            {
                case '!' when prevIsAlpha && nextIsVowel: result.Append("ff"); repaired = true; break;
                case '!' when prevIsAlpha && nextIsAlpha: result.Append("fi"); repaired = true; break;
                case '"' when prevIsAlpha && nextIsAlpha: result.Append("ffi"); repaired = true; break;
                case '#' when prevIsAlpha && nextIsAlpha: result.Append("fi"); repaired = true; break;
                case '#' when prevIsSpaceOrStart && nextIsLower: result.Append("fi"); repaired = true; break;
                case '!' when prevIsSpaceOrStart && nextIsLower: result.Append("fi"); repaired = true; break;
                case '*' when prevIsAlpha && nextIsAlpha: result.Append("tt"); repaired = true; break;
                case ':' when prevIsAlpha && nextIsLower: result.Append("ti"); repaired = true; break;
                case 'M' when prevIsAlpha && !prevIsSpaceOrStart:
                    bool prevWasLower = byteIdx > 0 && char.IsLower((char)bytes[byteIdx - 1]);
                    if (prevWasLower && nextIsLower) { result.Append("tti"); repaired = true; }
                    else result.Append(ch);
                    break;
                default: result.Append(chStr); break;
            }

            prevIsAlpha = char.IsLetter(chStr, 0);
            prevIsSpaceOrStart = chStr.Length == 1 && char.IsWhiteSpace(ch);
            byteIdx = nextByteIdx;
        }
        return repaired ? result.ToString() : text;
    }

    private static string ExpandLigaturesWithSpaceAbsorption(string text)
    {
        if (text.IndexOfAny(new[] { 'ﬀ', 'ﬁ', 'ﬂ', 'ﬃ', 'ﬄ', 'ﬅ', 'ﬆ' }) < 0)
            return text;
        var result = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            string? expansion = ch switch
            {
                'ﬀ' => "ff", 'ﬁ' => "fi", 'ﬂ' => "fl",
                'ﬃ' => "ffi", 'ﬄ' => "ffl", 'ﬅ' => "st", 'ﬆ' => "st",
                _ => null,
            };
            if (expansion is null) { result.Append(ch); i++; continue; }
            result.Append(expansion);
            i++;
            if (i < text.Length && text[i] == ' ')
            {
                if (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_')) i++; // absorb space
            }
        }
        return result.ToString();
    }

    private static string NormalizeUnicodeText(string text)
    {
        if (text.IndexOfAny(new[] { '‘', '’', '“', '”', '⁄', '•' }) < 0)
            return text;
        return text
            .Replace('‘', '\'').Replace('’', '\'')
            .Replace('“', '"').Replace('”', '"')
            .Replace('⁄', '/')
            .Replace('•', '·');
    }

    private static string FinalizeHyphens(string text)
    {
        string collapsed = CollapseSpacedHyphens(text);
        if (collapsed.IndexOfAny(new[] { '‐', '‑' }) < 0) return collapsed;
        return collapsed.Replace('‐', '-').Replace('‑', '-');
    }

    private static string CollapseSpacedHyphens(string text)
    {
        if (text.IndexOfAny(new[] { '‐', '‑' }) < 0) return text;
        bool IsGap(char c) => c is ' ' or ' ' or '\n' or '\r' or '\t';
        var chars = text.ToCharArray();
        var result = new StringBuilder(text.Length);
        int i = 0;
        while (i < chars.Length)
        {
            if (char.IsLetterOrDigit(chars[i]))
            {
                int j = i + 1;
                while (j < chars.Length && IsGap(chars[j])) j++;
                if (j > i + 1 && j < chars.Length && (chars[j] is '‐' or '‑'))
                {
                    int k = j + 1;
                    while (k < chars.Length && IsGap(chars[k])) k++;
                    if (k > j + 1 && k < chars.Length && char.IsLetterOrDigit(chars[k]))
                    {
                        result.Append(chars[i]);
                        result.Append('-');
                        i = k;
                        continue;
                    }
                }
            }
            result.Append(chars[i]);
            i++;
        }
        return result.ToString();
    }
}
