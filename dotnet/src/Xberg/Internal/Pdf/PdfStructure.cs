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
    private const int MAX_BOLD_HEADING_WORD_COUNT = 12;
    private const float PARAGRAPH_GAP_HEIGHT_FACTOR = 1.5f;

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
            MergeContinuationParagraphs(paras);
            RetainPageFurnitureSafely(paras);
            allPageParagraphs.Add(paras);
        }

        RefineHeadingHierarchy(allPageParagraphs);
        DemoteUnnumberedSubsections(allPageParagraphs);
        DemoteHeadingRuns(allPageParagraphs);

        // strip_repeating_text default true
        MarkCrossPageRepeatingText(allPageParagraphs, pageHeights);
        MarkCrossPageRepeatingShortText(allPageParagraphs);
        MarkArxivNoise(allPageParagraphs);
        foreach (var page in allPageParagraphs) RetainPageFurnitureSafely(page);
        DeduplicateParagraphs(allPageParagraphs);

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

        // Sparsity gate: too few text blocks to establish a reliable body-font baseline. Return
        // a body-only map and skip both k-means heading promotion and the fallback title
        // promotion, so a lone larger line on a cover, title or one-line document is not
        // over-promoted to a heading — the bold pass will still call it an H2 if it looks like
        // one. (Rust `build_heading_map`; the sparse repeated-tier branch is not ported.)
        if (paragraphCount < MIN_BLOCKS_FOR_FONT_HEADING)
        {
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
        float gapThreshold = heights[heights.Count / 2] * PARAGRAPH_GAP_HEIGHT_FACTOR;

        var gapYs = new List<float>();
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            float gap = lines[i].bottom - lines[i + 1].top;
            if (gap > gapThreshold && !(lines[i].mono && lines[i + 1].mono))
                gapYs.Add((lines[i].bottom + lines[i + 1].top) / 2.0f);
        }
        return gapYs;
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
            bool continuationSignal = !EndsWithSentenceTerminator(current) || StartsWithLowercaseContinuation(next);
            if (bothBody && fontsCompatible && continuationSignal)
            {
                current.Text = "";
                current.Lines.AddRange(next.Lines);
            }
            else { paragraphs.Add(current); current = next; }
        }
        paragraphs.Add(current);
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

    private static void RefineHeadingHierarchy(List<List<PdfParagraph>> allPages)
    {
        int H1Count() => allPages.SelectMany(p => p).Count(p => p.HeadingLevel == 1);

        if (H1Count() == 0)
        {
            bool hasAny = allPages.SelectMany(p => p).Any(p => p.HeadingLevel.HasValue);
            if (hasAny) PromoteTitleHeading(allPages);

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

    private static bool LooksLikeListItem(string text)
    {
        string t = text.TrimStart();
        if (t.Length == 0) return false;
        if (t.StartsWith('•') || t.StartsWith('·') || t.StartsWith('◦') || t.StartsWith('▪') || t.StartsWith('–') || t.StartsWith('—'))
            return true;
        if (t.StartsWith("- "))
        {
            string rest = t.Substring(2);
            return rest.Length > 0 && char.IsLetter(rest[0]);
        }
        int i = 0;
        if (t[0] == '(')
        {
            i = 1;
            if (i < t.Length && char.IsLetterOrDigit(t[i]))
            {
                while (i < t.Length && char.IsLetterOrDigit(t[i])) i++;
                if (i < t.Length && t[i] == ')')
                {
                    i++;
                    if (i < t.Length && char.IsWhiteSpace(t[i]))
                    {
                        i++;
                        return i < t.Length && char.IsLetter(t[i]);
                    }
                }
            }
            return false;
        }

        if (IsNumberedSectionHeading(t)) return false;

        if (char.IsLetterOrDigit(t[0]))
        {
            int numLen = 0; bool allDigits = true; bool allRoman = true;
            while (i < t.Length && char.IsLetterOrDigit(t[i]))
            {
                allDigits &= char.IsAsciiDigit(t[i]);
                char lc = char.ToLowerInvariant(t[i]);
                allRoman &= lc is 'i' or 'v' or 'x' or 'l' or 'c' or 'd' or 'm';
                i++; numLen++;
            }
            bool markerLike = allDigits || numLen == 1 || allRoman;
            if (numLen <= 4 && markerLike && i < t.Length && (t[i] == '.' || t[i] == ')'))
            {
                i++;
                if (i < t.Length && char.IsWhiteSpace(t[i]))
                {
                    i++;
                    return i < t.Length && char.IsLetter(t[i]);
                }
            }
        }
        return false;
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
