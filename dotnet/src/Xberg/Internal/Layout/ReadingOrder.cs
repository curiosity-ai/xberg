using System.Text;

using Xberg.Internal.Pdf;

namespace Xberg.Internal.Layout;

/// <summary>
/// Layout-guided PDF reading-order reconstruction, ported from Rust
/// <c>extractors/pdf/reading_order.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Projects text spans onto layout-detected regions, performs column detection, and reorders
/// spans in natural reading order — top-to-bottom within a column, left-to-right across columns.
/// This is what multi-column academic PDFs need: native extraction reads in content-stream order,
/// which for two columns interleaves them.
/// </para>
/// <para>
/// The ordering core is a port of Docling's reading-order predictor: a predecessor graph over
/// blocks, with horizontal dilation, then a depth-first emission that guarantees every
/// predecessor precedes its successors.
/// </para>
/// <para>
/// Everything here is pure geometry over spans, segments and hints. It needs no model and no page
/// raster: a caller that has layout regions from any source can drive it.
/// </para>
/// </remarks>
internal static class ReadingOrder
{
    /// <summary>Region x-centers closer than this (in PDF points) are merged into one column.</summary>
    private const float ColumnMergeThresholdPts = 20.0f;

    /// <summary>Tolerance mirroring Docling's <c>eps</c> in its bounding-box predicates.</summary>
    private const float ReadingOrderEps = 1e-3f;

    /// <summary>Maximum horizontal expansion on either side, relative to the PDF page width.</summary>
    private const float HorizontalDilationThresholdNorm = 0.15f;

    private const float MinSegmentRegionCoverage = 0.2f;
    private const float MinChildRegionContainment = 0.8f;
    private const int MinPartialTextOwners = 2;

    /// <summary>
    /// Treat small left-edge variation as indentation or noise; a larger page-relative separation
    /// is strong evidence of distinct column origins.
    /// </summary>
    private const float MaxSingleColumnLeftSpreadNorm = 0.05f;

    /// <summary>
    /// All lines must share at least half of the narrowest line's width, so a weak overlap
    /// between adjacent columns cannot masquerade as one text flow.
    /// </summary>
    private const float MinSingleColumnCommonWidthRatio = 0.5f;

    /// <summary>
    /// Semantic children covering nearly all of a segment outrank their enclosing wrapper.
    /// </summary>
    /// <remarks>
    /// This preserves Title/ListItem/Text classification, while a partial child — a narrow caption
    /// overlapping a form, say — cannot steal text.
    /// </remarks>
    private const float MinSemanticChildSegmentCoverage = 0.8f;

    // Require several page-wide body lines before treating a Picture owner as a layout false
    // positive rather than embedded text in a real figure.
    private const int MinFalsePictureProseLines = 3;
    private const int MinFalsePictureAlphaChars = 40;
    private const int MinFalsePictureWordsPerLine = 6;
    private const float MinFalsePictureAlphaRatio = 0.65f;
    private const float MinFalsePictureLineWidthNorm = 0.75f;
    private const float MinFalsePictureOutsideSpanNorm = 0.1f;
    private const float MaxFalsePictureLineGapNorm = 0.05f;
    private const float FalsePictureBaselineToleranceRatio = 0.35f;

    // ---------------------------------------------------------------------------------------
    // Rust semantics C# does not share
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Rust's <c>f32::total_cmp</c>: a total order over floats, NaN included.
    /// </summary>
    /// <remarks>
    /// <c>float.CompareTo</c> is not the same relation — it sorts every NaN below every number and
    /// treats -0.0 and 0.0 as equal — so a sort keyed on it can order two blocks differently from
    /// upstream. This is the bit-twiddling Rust itself uses.
    /// </remarks>
    internal static int TotalCmp(float a, float b)
    {
        int left = BitConverter.SingleToInt32Bits(a);
        int right = BitConverter.SingleToInt32Bits(b);
        left ^= (int)((uint)(left >> 31) >> 1);
        right ^= (int)((uint)(right >> 31) >> 1);
        return left.CompareTo(right);
    }

    /// <summary>
    /// A stable sort, which is what Rust's <c>sort_by</c> guarantees and
    /// <see cref="List{T}.Sort(Comparison{T})"/> does not.
    /// </summary>
    /// <remarks>
    /// Several comparators here return 0 for genuinely tied blocks, so an unstable sort would
    /// produce a different — and machine-dependent — reading order for the same page.
    /// </remarks>
    internal static void StableSort<T>(List<T> list, Comparison<T> comparison)
    {
        var indexed = new (T Value, int Index)[list.Count];
        for (int k = 0; k < list.Count; k++) indexed[k] = (list[k], k);
        Array.Sort(indexed, (x, y) =>
        {
            int c = comparison(x.Value, y.Value);
            return c != 0 ? c : x.Index.CompareTo(y.Index);
        });
        for (int k = 0; k < list.Count; k++) list[k] = indexed[k].Value;
    }

    private static float RustMin(float a, float b) => ReadingOrderText.RustMin(a, b);
    private static float RustMax(float a, float b) => ReadingOrderText.RustMax(a, b);

    // ---------------------------------------------------------------------------------------
    // Blocks
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A layout block (bbox in PDF points, bottom-left origin) used by the predecessor-graph
    /// reading-order reconstruction.
    /// </summary>
    internal readonly record struct OrderBlock(float Left, float Bottom, float Right, float Top)
    {
        /// <summary>
        /// This block lies entirely above <paramref name="other"/> (bottom-left origin, so a
        /// larger y is higher).
        /// </summary>
        /// <remarks>Port of docling-core <c>BoundingBox::is_strictly_above</c> for BOTTOMLEFT.</remarks>
        public bool IsStrictlyAbove(OrderBlock other) => Bottom + ReadingOrderEps > other.Top;

        /// <summary>The two blocks' x-ranges overlap. Strict: touching edges do not count.</summary>
        public bool OverlapsHorizontally(OrderBlock other) =>
            !(Right <= other.Left || other.Right <= Left);
    }

    private static float BlockArea(OrderBlock block) => (block.Right - block.Left) * (block.Top - block.Bottom);

    private static float BlockIntersectionArea(OrderBlock left, OrderBlock right)
    {
        float width = RustMax(RustMin(left.Right, right.Right) - RustMax(left.Left, right.Left), 0.0f);
        float height = RustMax(RustMin(left.Top, right.Top) - RustMax(left.Bottom, right.Bottom), 0.0f);
        float area = width * height;
        return float.IsFinite(area) ? area : 0.0f;
    }

    private static OrderBlock UnionOrderBlocks(OrderBlock left, OrderBlock right) => new(
        RustMin(left.Left, right.Left),
        RustMin(left.Bottom, right.Bottom),
        RustMax(left.Right, right.Right),
        RustMax(left.Top, right.Top));

    /// <summary>
    /// Reading-order comparator (negative means <paramref name="a"/> precedes <paramref name="b"/>).
    /// </summary>
    /// <remarks>
    /// Port of docling's <c>PageElement.__lt__</c>: same-column (horizontally overlapping) blocks
    /// order top-to-bottom (higher bottom edge first); otherwise left-to-right.
    /// </remarks>
    private static int ReadingOrderCmp(OrderBlock a, OrderBlock b) =>
        a.OverlapsHorizontally(b) ? TotalCmp(b.Bottom, a.Bottom) : TotalCmp(a.Left, b.Left);

    /// <summary>
    /// Is there a block strictly between <paramref name="i"/> and <paramref name="j"/> that
    /// horizontally overlaps either, interrupting the <c>i → j</c> reading-order edge?
    /// </summary>
    /// <remarks>
    /// Port of docling <c>_has_sequence_interruption</c>. This is what stops a full-width heading
    /// or figure sitting between two columns from chaining blocks across them.
    /// </remarks>
    private static bool HasSequenceInterruption(IReadOnlyList<OrderBlock> blocks, int i, int j)
    {
        var bi = blocks[i];
        var bj = blocks[j];
        for (int w = 0; w < blocks.Count; w++)
        {
            if (w == i || w == j) continue;
            var bw = blocks[w];
            if ((bi.OverlapsHorizontally(bw) || bj.OverlapsHorizontally(bw))
                && bi.IsStrictlyAbove(bw) && bw.IsStrictlyAbove(bj))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Build the up/down predecessor maps over <paramref name="blocks"/>.
    /// </summary>
    /// <remarks>
    /// Port of docling <c>_init_ud_maps</c>: an edge <c>i → j</c> exists when <c>i</c> is strictly
    /// above <c>j</c>, they horizontally overlap, and no third block interrupts the pair.
    /// </remarks>
    private static (List<List<int>> Up, List<List<int>> Down) BuildUpDownMaps(IReadOnlyList<OrderBlock> blocks)
    {
        int n = blocks.Count;
        var up = new List<List<int>>(n);
        var down = new List<List<int>>(n);
        for (int k = 0; k < n; k++) { up.Add(new List<int>()); down.Add(new List<int>()); }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j
                    && blocks[i].IsStrictlyAbove(blocks[j])
                    && blocks[i].OverlapsHorizontally(blocks[j])
                    && !HasSequenceInterruption(blocks, i, j))
                {
                    down[i].Add(j);
                    up[j].Add(i);
                }
        return (up, down);
    }

    /// <summary>
    /// Expand each block horizontally toward its first predecessor and successor.
    /// </summary>
    /// <remarks>
    /// Mirrors Docling's effective <c>_do_horizontal_dilation</c> behaviour: both candidate
    /// expansions come from the original relation maps and boxes, each side is capped at 15% of
    /// the actual PDF page width, and rejecting either candidate leaves the block unchanged.
    /// </remarks>
    internal static List<OrderBlock> DilateHorizontally(
        IReadOnlyList<OrderBlock> blocks,
        IReadOnlyList<List<int>> up,
        IReadOnlyList<List<int>> down,
        float pageWidthPts)
    {
        float threshold = HorizontalDilationThresholdNorm * pageWidthPts;
        var result = new List<OrderBlock>(blocks.Count);
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            float left = block.Left;
            float right = block.Right;
            bool rejected = false;

            if (up[index].Count > 0)
            {
                var predecessor = blocks[up[index][0]];
                float dilatedLeft = RustMin(left, predecessor.Left);
                float dilatedRight = RustMax(right, predecessor.Right);
                if (left - dilatedLeft > threshold || dilatedRight - right > threshold) rejected = true;
                else { left = dilatedLeft; right = dilatedRight; }
            }

            if (!rejected && down[index].Count > 0)
            {
                var successor = blocks[down[index][0]];
                float dilatedLeft = RustMin(left, successor.Left);
                float dilatedRight = RustMax(right, successor.Right);
                if (left - dilatedLeft > threshold || dilatedRight - right > threshold) rejected = true;
                else { left = dilatedLeft; right = dilatedRight; }
            }

            result.Add(rejected ? block : block with { Left = left, Right = right });
        }
        return result;
    }

    /// <summary>
    /// Walk up the predecessor map from <paramref name="start"/>, always taking the first
    /// not-yet-visited predecessor, until reaching a block whose predecessors are all visited.
    /// </summary>
    /// <remarks>Port of docling <c>_depth_first_search_upwards</c>.</remarks>
    private static int WalkToUnvisitedRoot(int start, IReadOnlyList<List<int>> up, IReadOnlyList<bool> visited)
    {
        int k = start;
        while (true)
        {
            int next = -1;
            foreach (int p in up[k]) if (!visited[p]) { next = p; break; }
            if (next < 0) return k;
            k = next;
        }
    }

    /// <summary>Emit <paramref name="start"/>'s successor subtree in reading order.</summary>
    /// <remarks>Port of docling <c>_depth_first_search_downwards</c> (iterative, explicit stack).</remarks>
    private static void EmitDownwards(
        int start, List<int> order, bool[] visited,
        IReadOnlyList<List<int>> up, IReadOnlyList<List<int>> down)
    {
        var stack = new List<(int Node, int Offset)> { (start, 0) };
        while (stack.Count > 0)
        {
            var (node, offset) = stack[^1];
            int next = offset;
            bool advanced = false;
            while (next < down[node].Count)
            {
                int child = down[node][next];
                int root = WalkToUnvisitedRoot(child, up, visited);
                if (!visited[root])
                {
                    order.Add(root);
                    visited[root] = true;
                    stack[^1] = (node, next + 1);
                    stack.Add((root, 0));
                    advanced = true;
                    break;
                }
                next++;
            }
            if (!advanced) stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>
    /// Whether the page is genuinely multi-column: two content blocks sit side by side (their
    /// y-ranges overlap while their x-ranges do not).
    /// </summary>
    /// <remarks>
    /// Reordering only helps multi-column pages — single-column stream order already reads
    /// top-to-bottom, so reordering it by often-noisy layout regions is pure downside.
    /// </remarks>
    private static bool IsMultiColumn(IReadOnlyList<OrderBlock> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var a = blocks[i];
            for (int j = i + 1; j < blocks.Count; j++)
            {
                var b = blocks[j];
                bool verticalOverlap = !(a.Top <= b.Bottom || b.Top <= a.Bottom);
                if (verticalOverlap && !a.OverlapsHorizontally(b)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Order blocks in reading order via the predecessor graph, returning block indices.
    /// </summary>
    /// <remarks>
    /// Port of docling <c>ReadingOrderPredictor._predict_page</c>, including its horizontal
    /// dilation refinement when the actual PDF page width is available.
    /// </remarks>
    internal static List<int> OrderBlocksByGraph(IReadOnlyList<OrderBlock> blocks, float? pageWidthPts)
    {
        int n = blocks.Count;
        var (rawUp, rawDown) = BuildUpDownMaps(blocks);

        List<List<int>> up, down;
        if (pageWidthPts is { } width && float.IsFinite(width) && width > 0.0f)
        {
            var dilated = DilateHorizontally(blocks, rawUp, rawDown, width);
            (up, down) = BuildUpDownMaps(dilated);
        }
        else
        {
            (up, down) = (rawUp, rawDown);
        }

        foreach (var children in down)
            StableSort(children, (a, b) => ReadingOrderCmp(blocks[a], blocks[b]));

        var heads = new List<int>();
        for (int k = 0; k < n; k++) if (up[k].Count == 0) heads.Add(k);
        StableSort(heads, (a, b) => ReadingOrderCmp(blocks[a], blocks[b]));

        var visited = new bool[n];
        var order = new List<int>(n);
        foreach (int head in heads)
        {
            if (visited[head]) continue;
            order.Add(head);
            visited[head] = true;
            EmitDownwards(head, order, visited, up, down);
        }
        // Safety net: append any block the traversal missed (degenerate geometry, cycles) so no
        // content is dropped.
        for (int k = 0; k < n; k++) if (!visited[k]) order.Add(k);
        return order;
    }

    // ---------------------------------------------------------------------------------------
    // Hints and segments
    // ---------------------------------------------------------------------------------------

    private static bool IsWrapperHint(LayoutRegionHint hint) => hint.ClassName.IsWrapper();

    private static OrderBlock? HintBlock(LayoutRegionHint hint)
    {
        if (!float.IsFinite(hint.Left) || !float.IsFinite(hint.Bottom)
            || !float.IsFinite(hint.Right) || !float.IsFinite(hint.Top)
            || hint.Right <= hint.Left || hint.Top <= hint.Bottom)
            return null;
        var block = new OrderBlock(hint.Left, hint.Bottom, hint.Right, hint.Top);
        float area = BlockArea(block);
        return float.IsFinite(area) && area > 0.0f ? block : null;
    }

    private static float ConfidenceRank(LayoutRegionHint hint) =>
        float.IsFinite(hint.Confidence) ? hint.Confidence : float.NegativeInfinity;

    internal static OrderBlock? SegmentBlock(SegmentData segment)
    {
        if (!float.IsFinite(segment.X) || !float.IsFinite(segment.Y)
            || !float.IsFinite(segment.Width) || !float.IsFinite(segment.Height)
            || segment.Width <= 0.0f || segment.Height <= 0.0f)
            return null;
        var block = new OrderBlock(segment.X, segment.Y, segment.X + segment.Width, segment.Y + segment.Height);
        if (!float.IsFinite(block.Left) || !float.IsFinite(block.Bottom)
            || !float.IsFinite(block.Right) || !float.IsFinite(block.Top))
            return null;
        float area = BlockArea(block);
        return float.IsFinite(area) && area > 0.0f ? block : null;
    }

    /// <summary>Union block over the segments whose geometry is usable, ignoring the rest.</summary>
    private static OrderBlock? SegmentsUnionBlock(IReadOnlyList<int> indices, IReadOnlyList<SegmentData> segments)
    {
        OrderBlock? union = null;
        foreach (int index in indices)
        {
            if (SegmentBlock(segments[index]) is not { } block) continue;
            union = union is { } current ? UnionOrderBlocks(current, block) : block;
        }
        return union;
    }

    /// <summary>Union block over the segments, or nothing at all if any one is degenerate.</summary>
    internal static OrderBlock? StrictSegmentsUnionBlock(IReadOnlyList<int> indices, IReadOnlyList<SegmentData> segments)
    {
        OrderBlock? union = null;
        foreach (int index in indices)
        {
            if (SegmentBlock(segments[index]) is not { } block) return null;
            union = union is { } current ? UnionOrderBlocks(current, block) : block;
        }
        return union;
    }

    /// <summary>
    /// <see cref="OrderBlock"/> for a single span, or <c>null</c> for degenerate geometry.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="SegmentBlock"/> so span runs order through the same predecessor-graph
    /// machinery the segment path uses.
    /// </remarks>
    private static OrderBlock? SpanBlock(ReadingOrderSpan span)
    {
        if (!float.IsFinite(span.X) || !float.IsFinite(span.Y)
            || !float.IsFinite(span.Width) || !float.IsFinite(span.Height)
            || span.Width <= 0.0f || span.Height <= 0.0f)
            return null;
        var block = new OrderBlock(span.X, span.Y, span.X + span.Width, span.Y + span.Height);
        if (!float.IsFinite(block.Left) || !float.IsFinite(block.Bottom)
            || !float.IsFinite(block.Right) || !float.IsFinite(block.Top))
            return null;
        float area = BlockArea(block);
        return float.IsFinite(area) && area > 0.0f ? block : null;
    }

    private static OrderBlock? SpansUnionBlock(IReadOnlyList<int> indices, IReadOnlyList<ReadingOrderSpan> spans)
    {
        OrderBlock? union = null;
        foreach (int index in indices)
        {
            if (SpanBlock(spans[index]) is not { } block) return null;
            union = union is { } current ? UnionOrderBlocks(current, block) : block;
        }
        return union;
    }

    private static bool[] EligibleHints(IReadOnlyList<LayoutRegionHint> hints, IReadOnlyList<bool> wrapperOwnership)
    {
        var eligible = new bool[hints.Count];
        for (int index = 0; index < hints.Count; index++)
        {
            var hint = hints[index];
            bool ownership = index < wrapperOwnership.Count ? wrapperOwnership[index] : true;
            eligible[index] = HintBlock(hint) is not null && (!IsWrapperHint(hint) || ownership);
        }
        return eligible;
    }

    /// <summary>
    /// The wrapper that best contains a child region, if any wrapper contains nearly all of it.
    /// </summary>
    private static int? ChooseWrapperRoot(
        int childIndex, IReadOnlyList<LayoutRegionHint> hints,
        IReadOnlyList<bool> eligible, IReadOnlyList<OrderBlock?> blocks)
    {
        if (blocks[childIndex] is not { } child) return null;
        float childArea = BlockArea(child);

        var candidates = new List<(int Index, float Score, float Confidence, float Area)>();
        for (int index = 0; index < hints.Count; index++)
        {
            if (!eligible[index] || !IsWrapperHint(hints[index])) continue;
            if (blocks[index] is not { } wrapper) continue;
            float containment = BlockIntersectionArea(child, wrapper) / childArea;
            if (float.IsFinite(containment) && containment > MinChildRegionContainment)
                candidates.Add((index, containment, ConfidenceRank(hints[index]), BlockArea(wrapper)));
        }
        SortCandidates(candidates);
        return candidates.Count > 0 ? candidates[0].Index : null;
    }

    /// <summary>
    /// Rank candidates by coverage, then confidence, then the tighter region, then hint order.
    /// </summary>
    private static void SortCandidates(List<(int Index, float Score, float Confidence, float Area)> candidates) =>
        StableSort(candidates, (left, right) =>
        {
            int c = TotalCmp(right.Score, left.Score);
            if (c != 0) return c;
            c = TotalCmp(right.Confidence, left.Confidence);
            if (c != 0) return c;
            c = TotalCmp(left.Area, right.Area);
            if (c != 0) return c;
            return left.Index.CompareTo(right.Index);
        });

    private static int?[] RootHintIndices(
        IReadOnlyList<LayoutRegionHint> hints, IReadOnlyList<bool> eligible, IReadOnlyList<OrderBlock?> blocks)
    {
        var roots = new int?[hints.Count];
        for (int index = 0; index < hints.Count; index++)
        {
            if (!eligible[index]) roots[index] = null;
            else if (IsWrapperHint(hints[index])) roots[index] = index;
            else roots[index] = ChooseWrapperRoot(index, hints, eligible, blocks) ?? index;
        }
        return roots;
    }

    private static int? ChooseSegmentOwner(
        SegmentData segment, IReadOnlyList<LayoutRegionHint> hints, IReadOnlyList<bool> eligible,
        IReadOnlyList<OrderBlock?> blocks, IReadOnlyList<int?> roots)
    {
        if (SegmentBlock(segment) is not { } segmentBlock) return null;
        float segmentArea = BlockArea(segmentBlock);

        var candidates = new List<(int Index, float Score, float Confidence, float Area)>();
        for (int index = 0; index < hints.Count; index++)
        {
            if (!eligible[index]) continue;
            if (blocks[index] is not { } region) continue;
            float coverage = BlockIntersectionArea(segmentBlock, region) / segmentArea;
            if (float.IsFinite(coverage) && coverage > MinSegmentRegionCoverage)
                candidates.Add((index, coverage, ConfidenceRank(hints[index]), BlockArea(region)));
        }
        SortCandidates(candidates);
        if (candidates.Count == 0) return null;

        var winner = candidates[0];
        if (!IsWrapperHint(hints[winner.Index])) return winner.Index;

        // A wrapper won, but a semantic child covering nearly all of the segment outranks it.
        foreach (var candidate in candidates)
            if (!IsWrapperHint(hints[candidate.Index])
                && roots[candidate.Index] == winner.Index
                && candidate.Score >= MinSemanticChildSegmentCoverage)
                return candidate.Index;
        return winner.Index;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if ((c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0x3040 && c <= 0x309F)
                || (c >= 0x30A0 && c <= 0x30FF)
                || (c >= 0xAC00 && c <= 0xD7AF)
                || (c >= 0x3400 && c <= 0x4DBF)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0x20000 && c <= 0x2A6DF)
                || (c >= 0x2A700 && c <= 0x2B73F)
                || (c >= 0x2B740 && c <= 0x2B81F)
                || (c >= 0x2B820 && c <= 0x2CEAF)
                || (c >= 0x2CEB0 && c <= 0x2EBEF)
                || (c >= 0x30000 && c <= 0x3134F)
                || (c >= 0x31350 && c <= 0x323AF)
                || (c >= 0x2F800 && c <= 0x2FA1F))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether two consecutive segments are two halves of one word, split by kerning.
    /// </summary>
    /// <remarks>
    /// Everything about the pair must match — font, style, role, baseline — and the gap between
    /// them must be within the kerning cutoff. CJK is excluded because it has no inter-word gap
    /// to reason about.
    /// </remarks>
    private static bool SegmentsFormAtomicFragment(SegmentData previous, SegmentData next)
    {
        System.Text.Rune? previousLast = null, nextFirst = null;
        foreach (var rune in previous.Text.EnumerateRunes()) previousLast = rune;
        foreach (var rune in next.Text.EnumerateRunes()) { nextFirst = rune; break; }
        if (previousLast is not { } last || nextFirst is not { } first) return false;

        if (System.Text.Rune.IsWhiteSpace(last)
            || System.Text.Rune.IsWhiteSpace(first)
            || ContainsCjk(previous.Text)
            || ContainsCjk(next.Text)
            || SegmentBlock(previous) is null
            || SegmentBlock(next) is null
            || !float.IsFinite(previous.FontSize)
            || previous.FontSize <= 0.0f
            || previous.FontSize != next.FontSize
            || previous.IsBold != next.IsBold
            || previous.IsItalic != next.IsItalic
            || previous.IsMonospace != next.IsMonospace
            || previous.AssignedRole != next.AssignedRole
            || !float.IsFinite(previous.BaselineY)
            || !float.IsFinite(next.BaselineY)
            || next.X < previous.X)
            return false;

        float effectiveHeight = RustMax(RustMax(next.Height, previous.Height), next.FontSize * 0.5f);
        bool sameBaseline = MathF.Abs(previous.BaselineY - next.BaselineY) < effectiveHeight * 0.5f;
        float horizontalGap = next.X - (previous.X + previous.Width);
        float kerningLimit = next.FontSize * ReadingOrderText.AtomicFragmentGapRatio;
        return sameBaseline && horizontalGap >= -kerningLimit && horizontalGap <= kerningLimit;
    }

    /// <summary>
    /// Give every segment of one kerning-split run the same owner, when they do not already
    /// disagree about it.
    /// </summary>
    /// <remarks>
    /// A word split across a region boundary would otherwise have its halves ordered
    /// independently, which reads as two fragments rather than one word.
    /// </remarks>
    private static void ReconcileAtomicFragmentOwners(IReadOnlyList<SegmentData> segments, int?[] owners)
    {
        int runStart = 0;
        while (runStart < segments.Count)
        {
            int runEnd = runStart + 1;
            while (runEnd < segments.Count && SegmentsFormAtomicFragment(segments[runEnd - 1], segments[runEnd]))
                runEnd++;

            int? runOwner = null;
            bool conflicting = false;
            for (int k = runStart; k < runEnd; k++)
            {
                if (owners[k] is not { } owner) continue;
                if (runOwner is { } current) { if (current != owner) { conflicting = true; break; } }
                else runOwner = owner;
            }
            if (!conflicting && runOwner is { } chosen)
                for (int k = runStart; k < runEnd; k++) owners[k] = chosen;

            runStart = runEnd;
        }
    }

    private sealed class PictureTextLine
    {
        public float Baseline;
        public float Left = float.PositiveInfinity;
        public float Right = float.NegativeInfinity;
        public int AlphaChars;
        public int VisibleChars;
        public int WordCount;
        public int OwnedAlphaChars;
        public int OwnedVisibleChars;
        public int OwnedWordCount;
        public List<(float Start, float End)> Intervals = new();
        public List<int> OwnedIndices = new();
    }

    /// <summary>Rust's <c>char::is_alphabetic</c> is the Unicode Alphabetic property.</summary>
    /// <remarks>
    /// <c>Rune.IsLetter</c> covers the letter categories; Rust additionally counts Nl and
    /// Other_Alphabetic marks. The difference only moves a ratio for text made of combining marks,
    /// which is not the body prose this heuristic is looking for.
    /// </remarks>
    private static int CountAlphabetic(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes()) if (System.Text.Rune.IsLetter(rune)) count++;
        return count;
    }

    private static int CountVisible(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes()) if (!System.Text.Rune.IsWhiteSpace(rune)) count++;
        return count;
    }

    /// <summary>Rust's <c>split_whitespace().count()</c>: runs of non-whitespace.</summary>
    private static int CountWords(string text)
    {
        int count = 0;
        bool inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsWhiteSpace(rune)) inWord = false;
            else if (!inWord) { inWord = true; count++; }
        }
        return count;
    }

    private static void AddSegmentToPictureLine(
        List<PictureTextLine> lines, int index, SegmentData segment, bool isOwned)
    {
        float tolerance = RustMax(segment.FontSize, segment.Height) * FalsePictureBaselineToleranceRatio;
        int lineIndex = -1;
        for (int k = 0; k < lines.Count; k++)
            if (MathF.Abs(lines[k].Baseline - segment.BaselineY) <= tolerance) { lineIndex = k; break; }
        if (lineIndex < 0)
        {
            lines.Add(new PictureTextLine { Baseline = segment.BaselineY });
            lineIndex = lines.Count - 1;
        }

        var line = lines[lineIndex];
        line.Left = RustMin(line.Left, segment.X);
        line.Right = RustMax(line.Right, segment.X + segment.Width);
        line.AlphaChars += CountAlphabetic(segment.Text);
        line.VisibleChars += CountVisible(segment.Text);
        line.WordCount += CountWords(segment.Text);
        line.Intervals.Add((segment.X, segment.X + segment.Width));
        if (isOwned)
        {
            line.OwnedAlphaChars += CountAlphabetic(segment.Text);
            line.OwnedVisibleChars += CountVisible(segment.Text);
            line.OwnedWordCount += CountWords(segment.Text);
            line.OwnedIndices.Add(index);
        }
    }

    private static bool PictureLineIsContiguous(PictureTextLine line, float pageWidthPts)
    {
        var intervals = new List<(float Start, float End)>(line.Intervals);
        StableSort(intervals, (left, right) => TotalCmp(left.Start, right.Start));
        for (int k = 1; k < intervals.Count; k++)
            if (intervals[k].Start - intervals[k - 1].End > pageWidthPts * MaxFalsePictureLineGapNorm)
                return false;
        return true;
    }

    private static bool PictureLineIsBodyProse(PictureTextLine line, OrderBlock picture, float pageWidthPts)
    {
        float alphaRatio = (float)line.AlphaChars / Math.Max(line.VisibleChars, 1);
        float ownedAlphaRatio = (float)line.OwnedAlphaChars / Math.Max(line.OwnedVisibleChars, 1);
        return line.Right - line.Left >= pageWidthPts * MinFalsePictureLineWidthNorm
            && line.Left < picture.Left - pageWidthPts * MinFalsePictureOutsideSpanNorm
            && line.WordCount >= MinFalsePictureWordsPerLine
            && line.OwnedWordCount >= MinFalsePictureWordsPerLine
            && alphaRatio >= MinFalsePictureAlphaRatio
            && ownedAlphaRatio >= MinFalsePictureAlphaRatio
            && PictureLineIsContiguous(line, pageWidthPts);
    }

    private static List<int> PictureProseOwnedIndices(
        int owner, IReadOnlyList<SegmentData> segments, IReadOnlyList<int?> owners,
        OrderBlock picture, float pageWidthPts)
    {
        var ownedIndices = new List<int>();
        for (int index = 0; index < owners.Count; index++) if (owners[index] == owner) ownedIndices.Add(index);
        if (ownedIndices.Count == 0) return new List<int>();

        int first = ownedIndices[0];
        int last = ownedIndices[^1];

        // The run has to be interrupted by unowned text (otherwise it is just a region) and must
        // not contain a different owner (otherwise it is not one flow).
        bool anyUnowned = false;
        for (int index = first; index <= last; index++)
        {
            if (owners[index] is null) { anyUnowned = true; continue; }
            if (owners[index] != owner) return new List<int>();
        }
        if (!anyUnowned) return new List<int>();

        var lines = new List<PictureTextLine>();
        for (int index = first; index <= last; index++)
            if (owners[index] is null || owners[index] == owner)
                AddSegmentToPictureLine(lines, index, segments[index], owners[index] == owner);

        var qualifying = lines.Where(line => PictureLineIsBodyProse(line, picture, pageWidthPts)).ToList();
        int alphaChars = qualifying.Sum(line => line.AlphaChars);
        if (qualifying.Count < MinFalsePictureProseLines || alphaChars < MinFalsePictureAlphaChars)
            return new List<int>();

        return qualifying.SelectMany(line => line.OwnedIndices).ToList();
    }

    /// <summary>
    /// Release segments a Picture region claimed that are really page-wide body prose.
    /// </summary>
    /// <remarks>
    /// A layout false positive over running text would otherwise pull whole paragraphs into a
    /// figure. The bar is deliberately high — several page-wide, mostly-alphabetic, contiguous
    /// lines that start outside the picture — so text genuinely embedded in a figure stays put.
    /// </remarks>
    private static void ReconcileFalsePictureProseOwners(
        IReadOnlyList<SegmentData> segments, IReadOnlyList<LayoutRegionHint> hints,
        IReadOnlyList<OrderBlock?> blocks, int?[] owners, float? pageWidthPts)
    {
        if (pageWidthPts is not { } width || !float.IsFinite(width) || width <= 0.0f) return;
        for (int owner = 0; owner < hints.Count; owner++)
        {
            if (hints[owner].ClassName != LayoutHintClass.Picture) continue;
            if (blocks[owner] is not { } picture) continue;
            foreach (int index in PictureProseOwnedIndices(owner, segments, owners, picture, width))
                owners[index] = null;
        }
    }

    internal static bool HasSingleColumnSegmentGeometry(IReadOnlyList<SegmentData> segments, float? pageWidthPts)
    {
        if (pageWidthPts is not { } width || !float.IsFinite(width) || width <= 0.0f) return false;

        var blocks = new List<OrderBlock>(segments.Count);
        foreach (var segment in segments)
        {
            if (SegmentBlock(segment) is not { } block) return false;
            blocks.Add(block);
        }
        if (blocks.Count == 0) return false;

        float leftMin = float.PositiveInfinity, leftMax = float.NegativeInfinity;
        foreach (var block in blocks)
        {
            leftMin = RustMin(leftMin, block.Left);
            leftMax = RustMax(leftMax, block.Left);
        }
        if (leftMax - leftMin > MaxSingleColumnLeftSpreadNorm * width) return false;

        float commonLeft = float.NegativeInfinity, commonRight = float.PositiveInfinity;
        float narrowest = float.PositiveInfinity;
        foreach (var block in blocks)
        {
            commonLeft = RustMax(commonLeft, block.Left);
            commonRight = RustMin(commonRight, block.Right);
            narrowest = RustMin(narrowest, block.Right - block.Left);
        }
        float ratio = RustMax(commonRight - commonLeft, 0.0f) / narrowest;
        return float.IsFinite(ratio) && ratio >= MinSingleColumnCommonWidthRatio;
    }

    private static bool PartialOwnershipIsGenericTextFlow(
        IReadOnlyList<int?> owners, IReadOnlyList<LayoutRegionHint> hints,
        IReadOnlyList<bool> eligible, IReadOnlyList<int?> roots)
    {
        for (int index = 0; index < hints.Count; index++)
            if (eligible[index] && hints[index].ClassName != LayoutHintClass.Text) return false;

        int ownedCount = owners.Count(owner => owner is not null);
        int uncoveredCount = owners.Count - ownedCount;
        if (ownedCount == 0 || uncoveredCount == 0 || ownedCount > uncoveredCount) return false;

        var ownerIndices = new SortedSet<int>();
        foreach (var owner in owners) if (owner is { } value) ownerIndices.Add(value);
        return ownerIndices.Count >= MinPartialTextOwners
            && ownerIndices.All(owner => owner < roots.Count && roots[owner] is not null);
    }

    /// <summary>
    /// Whether a page's plain text flow should be left exactly as extracted.
    /// </summary>
    /// <remarks>
    /// Generic Text regions covering a minority of a visibly single-column page are noise, not
    /// structure: reordering by them would rearrange a page that already reads correctly.
    /// </remarks>
    private static bool ShouldPreserveNativePartialTextFlow(
        IReadOnlyList<SegmentData> segments, IReadOnlyList<int?> owners,
        IReadOnlyList<LayoutRegionHint> hints, IReadOnlyList<bool> eligible,
        IReadOnlyList<int?> roots, bool noReorder, float? pageWidthPts) =>
        !noReorder
        && PartialOwnershipIsGenericTextFlow(owners, hints, eligible, roots)
        && HasSingleColumnSegmentGeometry(segments, pageWidthPts);

    private static List<LayoutSegmentGroup> PathlessGroup(int segmentCount)
    {
        var indices = new List<int>(segmentCount);
        for (int k = 0; k < segmentCount; k++) indices.Add(k);
        return new List<LayoutSegmentGroup> { new() { SegmentIndices = indices } };
    }

    public static bool HasEligibleLayoutHints(
        IReadOnlyList<LayoutRegionHint> hints, IReadOnlyList<bool> wrapperOwnership) =>
        EligibleHints(hints, wrapperOwnership).Any(eligible => eligible);

    internal sealed class PlannedGroup
    {
        public required LayoutSegmentGroup Output;
        public required int RootId;
        public required OrderBlock? OrderBlockValue;
        public required OrderBlock? ContentBlock;
        public required int FirstSegmentIndex;
    }

    private static PlannedGroup UncoveredGroup(
        List<int> indices, IReadOnlyList<SegmentData> segments, int syntheticId) => new()
    {
        Output = new LayoutSegmentGroup
        {
            SegmentIndices = indices,
            RegionPath = new LayoutRegionPath(new LayoutRegionTag(syntheticId, null), null),
        },
        RootId = syntheticId,
        OrderBlockValue = SegmentsUnionBlock(indices, segments),
        ContentBlock = StrictSegmentsUnionBlock(indices, segments),
        FirstSegmentIndex = indices[0],
    };

    /// <summary>
    /// Order a set of blocks, keeping blocks with no usable geometry in source order at the end.
    /// </summary>
    /// <remarks>
    /// Single-column pages skip the graph entirely and sort top-to-bottom then left-to-right:
    /// running the predecessor graph over one column gains nothing and can only introduce noise.
    /// </remarks>
    private static List<int> OrderedIndices(
        IReadOnlyList<OrderBlock?> blocks, IReadOnlyList<int> firstIndices, bool noReorder, float? pageWidthPts)
    {
        if (noReorder)
        {
            var source = new List<int>(blocks.Count);
            for (int k = 0; k < blocks.Count; k++) source.Add(k);
            StableSort(source, (a, b) => firstIndices[a].CompareTo(firstIndices[b]));
            return source;
        }

        var valid = new List<int>();
        var validBlocks = new List<OrderBlock>();
        for (int k = 0; k < blocks.Count; k++)
            if (blocks[k] is { } block) { valid.Add(k); validBlocks.Add(block); }

        List<int> validOrder;
        if (IsMultiColumn(validBlocks))
        {
            validOrder = OrderBlocksByGraph(validBlocks, pageWidthPts);
        }
        else
        {
            validOrder = new List<int>(validBlocks.Count);
            for (int k = 0; k < validBlocks.Count; k++) validOrder.Add(k);
            StableSort(validOrder, (left, right) =>
            {
                int c = TotalCmp(validBlocks[right].Top, validBlocks[left].Top);
                return c != 0 ? c : TotalCmp(validBlocks[left].Left, validBlocks[right].Left);
            });
        }

        var result = validOrder.Select(index => valid[index]).ToList();
        var invalid = new List<int>();
        for (int k = 0; k < blocks.Count; k++) if (blocks[k] is null) invalid.Add(k);
        StableSort(invalid, (a, b) => firstIndices[a].CompareTo(firstIndices[b]));
        result.AddRange(invalid);
        return result;
    }

    private static OrderBlock? PlannedContentUnionBlock(IReadOnlyList<PlannedGroup> groups)
    {
        OrderBlock? union = null;
        foreach (var group in groups)
        {
            if (group.ContentBlock is not { } block) return null;
            union = union is { } current ? UnionOrderBlocks(current, block) : block;
        }
        return union;
    }

    private static bool RootPreservesWrapperGeometry(IReadOnlyList<PlannedGroup> groups) =>
        groups.Any(group => group.Output.RegionPath is { } path
                            && path.Root.ClassName is { } className
                            && className.IsWrapper());

    /// <summary>
    /// The block a root orders by: a wrapper keeps its own geometry, anything else takes the
    /// layout region's horizontal extent with its content's vertical extent.
    /// </summary>
    /// <remarks>
    /// A detected region is often taller than the text it actually holds; using the content's
    /// vertical extent is what stops an over-tall region from swallowing its neighbours in the
    /// predecessor graph.
    /// </remarks>
    internal static OrderBlock? EffectiveRootOrderBlock(
        int rootId, IReadOnlyList<PlannedGroup> groups, IReadOnlyList<OrderBlock?> rootBlocks)
    {
        OrderBlock? layoutBlock = rootId < rootBlocks.Count ? rootBlocks[rootId] : null;
        if (RootPreservesWrapperGeometry(groups)) return layoutBlock;

        var content = PlannedContentUnionBlock(groups);
        if (content is { } contentBlock && layoutBlock is { } layout)
            return new OrderBlock(layout.Left, contentBlock.Bottom, layout.Right, contentBlock.Top);
        if (content is { } onlyContent) return onlyContent;
        return layoutBlock;
    }

    internal static List<LayoutSegmentGroup> OrderPlannedGroups(
        List<PlannedGroup> groups, IReadOnlyList<OrderBlock?> rootBlocks, bool noReorder, float? pageWidthPts)
    {
        var byRoot = new SortedDictionary<int, List<PlannedGroup>>();
        foreach (var group in groups)
        {
            if (!byRoot.TryGetValue(group.RootId, out var list)) byRoot[group.RootId] = list = new List<PlannedGroup>();
            list.Add(group);
        }

        var rootIds = byRoot.Keys.ToList();
        var rootOrderBlocks = rootIds
            .Select(rootId => EffectiveRootOrderBlock(rootId, byRoot[rootId], rootBlocks))
            .ToList();
        var rootFirstIndices = rootIds
            .Select(rootId => byRoot[rootId].Min(group => group.FirstSegmentIndex))
            .ToList();

        var ordered = new List<LayoutSegmentGroup>();
        foreach (int rootPosition in OrderedIndices(rootOrderBlocks, rootFirstIndices, noReorder, pageWidthPts))
        {
            int rootId = rootIds[rootPosition];
            var children = byRoot[rootId];
            byRoot.Remove(rootId);

            var childBlocks = children.Select(group => group.OrderBlockValue).ToList();
            var childFirst = children.Select(group => group.FirstSegmentIndex).ToList();
            foreach (int index in OrderedIndices(childBlocks, childFirst, noReorder, pageWidthPts))
                ordered.Add(children[index].Output);
        }
        return ordered;
    }

    /// <summary>
    /// Build a deterministic reading-order plan without flattening layout regions.
    /// </summary>
    /// <remarks>
    /// Every post-table-filter segment appears exactly once. Table/Picture regions stay as
    /// top-level wrappers, regular regions contained by a wrapper fold into that root, and
    /// segments outside every region stay in contiguous source runs so uncovered material is
    /// interleaved by position rather than relocated to the end of the page.
    /// </remarks>
    public static List<LayoutSegmentGroup> PlanSegmentGroupsByLayout(
        IReadOnlyList<SegmentData> segments,
        IReadOnlyList<LayoutRegionHint> hints,
        IReadOnlyList<bool> wrapperOwnership,
        bool noReorder,
        float? pageWidthPts)
    {
        if (segments.Count == 0) return new List<LayoutSegmentGroup>();
        if (hints.Count == 0) return PathlessGroup(segments.Count);

        var blocks = hints.Select(HintBlock).ToList();
        var eligible = EligibleHints(hints, wrapperOwnership);
        if (!eligible.Any(value => value)) return PathlessGroup(segments.Count);

        var roots = RootHintIndices(hints, eligible, blocks);
        var owners = segments
            .Select(segment => ChooseSegmentOwner(segment, hints, eligible, blocks, roots))
            .ToArray();
        ReconcileAtomicFragmentOwners(segments, owners);
        ReconcileFalsePictureProseOwners(segments, hints, blocks, owners, pageWidthPts);

        if (owners.All(owner => owner is null)) return PathlessGroup(segments.Count);
        if (ShouldPreserveNativePartialTextFlow(segments, owners, hints, eligible, roots, noReorder, pageWidthPts))
            return PathlessGroup(segments.Count);

        var regionSegments = new SortedDictionary<int, List<int>>();
        for (int segmentIndex = 0; segmentIndex < owners.Length; segmentIndex++)
        {
            if (owners[segmentIndex] is not { } owner) continue;
            if (roots[owner] is null) continue;
            if (!regionSegments.TryGetValue(owner, out var list))
                regionSegments[owner] = list = new List<int>();
            list.Add(segmentIndex);
        }

        var groups = new List<PlannedGroup>();
        foreach (var (owner, segmentIndices) in regionSegments)
        {
            if (!noReorder)
                StableSort(segmentIndices, (left, right) =>
                {
                    var leftSegment = segments[left];
                    var rightSegment = segments[right];
                    float leftTop = leftSegment.Y + leftSegment.Height;
                    float rightTop = rightSegment.Y + rightSegment.Height;
                    int c = TotalCmp(rightTop, leftTop);
                    if (c != 0) return c;
                    c = TotalCmp(leftSegment.X, rightSegment.X);
                    return c != 0 ? c : left.CompareTo(right);
                });

            int firstSegmentIndex = segmentIndices.Min();
            var contentBlock = StrictSegmentsUnionBlock(segmentIndices, segments);
            var orderBlock = IsWrapperHint(hints[owner])
                ? SegmentsUnionBlock(segmentIndices, segments)
                : blocks[owner];

            int root = roots[owner]!.Value;
            groups.Add(new PlannedGroup
            {
                FirstSegmentIndex = firstSegmentIndex,
                Output = new LayoutSegmentGroup
                {
                    SegmentIndices = segmentIndices,
                    HintIndices = IsWrapperHint(hints[owner]) ? new List<int>() : new List<int> { owner },
                    RegionPath = new LayoutRegionPath(
                        new LayoutRegionTag(root, hints[root].ClassName),
                        root != owner ? new LayoutRegionTag(owner, hints[owner].ClassName) : null),
                },
                RootId = root,
                OrderBlockValue = orderBlock,
                ContentBlock = contentBlock,
            });
        }

        var uncovered = new List<int>();
        int nextSyntheticId = hints.Count;
        for (int segmentIndex = 0; segmentIndex < owners.Length; segmentIndex++)
        {
            if (owners[segmentIndex] is null) uncovered.Add(segmentIndex);
            else if (uncovered.Count > 0)
            {
                groups.Add(UncoveredGroup(uncovered, segments, nextSyntheticId));
                uncovered = new List<int>();
                nextSyntheticId++;
            }
        }
        if (uncovered.Count > 0) groups.Add(UncoveredGroup(uncovered, segments, nextSyntheticId));

        var rootBlocks = new List<OrderBlock?>(blocks);
        while (rootBlocks.Count < nextSyntheticId + 1) rootBlocks.Add(null);
        foreach (var group in groups)
            if (group.RootId >= hints.Count) rootBlocks[group.RootId] = group.OrderBlockValue;

        return OrderPlannedGroups(groups, rootBlocks, noReorder, pageWidthPts);
    }

    /// <summary>Flatten a layout plan back into a reordered segment list.</summary>
    public static List<SegmentData> ReorderSegmentsByLayout(
        IReadOnlyList<SegmentData> segments, IReadOnlyList<LayoutRegionHint> hints,
        float? pageWidthPts, bool noReorder = false) =>
        PlanSegmentGroupsByLayout(segments, hints, Array.Empty<bool>(), noReorder, pageWidthPts)
            .SelectMany(group => group.SegmentIndices)
            .Select(index => segments[index])
            .ToList();

    // ---------------------------------------------------------------------------------------
    // Spans
    // ---------------------------------------------------------------------------------------

    internal sealed class RegionProjection
    {
        public float Left, Bottom, Right, Top;
        public List<int> SpanIndices = new();
    }

    /// <summary>
    /// Project spans onto regions: a span belongs to the smallest region whose box contains the
    /// span's centre.
    /// </summary>
    internal static List<RegionProjection> ProjectSpansToRegions(
        IReadOnlyList<ReadingOrderSpan> spans, IReadOnlyList<LayoutRegionHint> hints)
    {
        var regions = hints
            .Select(hint => new RegionProjection
            {
                Left = hint.Left, Bottom = hint.Bottom, Right = hint.Right, Top = hint.Top,
            })
            .ToList();

        for (int spanIdx = 0; spanIdx < spans.Count; spanIdx++)
        {
            var span = spans[spanIdx];
            float centerX = span.X + span.Width / 2.0f;
            float centerY = span.Y + span.Height / 2.0f;

            int bestRegion = -1;
            float bestArea = 0.0f;
            for (int regionIdx = 0; regionIdx < regions.Count; regionIdx++)
            {
                var region = regions[regionIdx];
                if (centerX >= region.Left && centerX <= region.Right
                    && centerY >= region.Bottom && centerY <= region.Top)
                {
                    float area = (region.Right - region.Left) * (region.Top - region.Bottom);
                    if (bestRegion < 0 || area < bestArea) { bestRegion = regionIdx; bestArea = area; }
                }
            }
            if (bestRegion >= 0) regions[bestRegion].SpanIndices.Add(spanIdx);
        }

        return regions.Where(region => region.SpanIndices.Count > 0).ToList();
    }

    /// <summary>
    /// <c>(advance_start, cross_top)</c> for ordering spans within a reading-order group.
    /// </summary>
    /// <remarks>
    /// Descending <c>cross_top</c> walks rows in the span's own reading direction, then ascending
    /// <c>advance_start</c> reads along that same axis. Identical to <c>(x, y + height)</c> for
    /// unrotated spans.
    /// </remarks>
    private static (float Advance, float CrossTop) ReadingOrderKey(ReadingOrderSpan span)
    {
        var (advanceStart, crossStart) = ReadingOrderText.UprightReadingOrigin(span);
        return (advanceStart, crossStart + span.Height);
    }

    /// <summary>
    /// Reorder spans using purely geometric column detection, with no layout hints.
    /// </summary>
    /// <remarks>
    /// Deliberately left on raw page x/y rather than each span's own upright frame: unlike a
    /// single detected region, a page-wide set of same-rotation spans has no one well-defined
    /// reading flow to rotate into, so this stays rotation-blind by design.
    /// </remarks>
    internal static List<int> ReorderSpansGeometric(IReadOnlyList<ReadingOrderSpan> spans)
    {
        if (spans.Count == 0) return new List<int>();

        var xCenters = spans.Select(span => span.X + span.Width / 2.0f).ToList();
        StableSort(xCenters, TotalCmp);

        var uniqueCenters = new List<float>();
        foreach (float center in xCenters)
        {
            if (uniqueCenters.Count == 0) uniqueCenters.Add(center);
            else if (MathF.Abs(center - uniqueCenters[^1]) > ColumnMergeThresholdPts) uniqueCenters.Add(center);
        }

        var spanColumns = new List<(int Column, float TopY, int SpanIndex)>(spans.Count);
        for (int spanIdx = 0; spanIdx < spans.Count; spanIdx++)
        {
            var span = spans[spanIdx];
            float spanCenter = span.X + span.Width / 2.0f;
            int bestColumn = 0;
            float bestDistance = float.PositiveInfinity;
            for (int columnId = 0; columnId < uniqueCenters.Count; columnId++)
            {
                float distance = MathF.Abs(spanCenter - uniqueCenters[columnId]);
                if (distance < bestDistance) { bestDistance = distance; bestColumn = columnId; }
            }
            spanColumns.Add((bestColumn, span.Y + span.Height, spanIdx));
        }

        StableSort(spanColumns, (a, b) =>
        {
            int c = a.Column.CompareTo(b.Column);
            return c != 0 ? c : TotalCmp(b.TopY, a.TopY);
        });
        return spanColumns.Select(entry => entry.SpanIndex).ToList();
    }

    /// <summary>
    /// Reorder spans based on layout regions, falling back to geometric column detection when no
    /// hints are available.
    /// </summary>
    /// <remarks>
    /// Spans no region contains — a marginal note, or one whose centre falls outside every region
    /// — are grouped into maximal uncovered runs and ordered alongside the regions by the same
    /// predecessor graph, rather than dropped or relocated to the end of the page.
    /// </remarks>
    public static List<int> ReorderSpansByLayout(
        IReadOnlyList<ReadingOrderSpan> spans, IReadOnlyList<LayoutRegionHint> hints)
    {
        if (spans.Count == 0) return new List<int>();
        if (hints.Count == 0) return ReorderSpansGeometric(spans);

        var regions = ProjectSpansToRegions(spans, hints);
        if (regions.Count == 0)
        {
            var identity = new List<int>(spans.Count);
            for (int k = 0; k < spans.Count; k++) identity.Add(k);
            return identity;
        }

        foreach (var region in regions)
            StableSort(region.SpanIndices, (a, b) =>
            {
                var (advanceA, crossTopA) = ReadingOrderKey(spans[a]);
                var (advanceB, crossTopB) = ReadingOrderKey(spans[b]);
                int c = TotalCmp(crossTopB, crossTopA);
                return c != 0 ? c : TotalCmp(advanceA, advanceB);
            });

        var projected = new HashSet<int>(regions.SelectMany(region => region.SpanIndices));

        var groups = new List<List<int>>(regions.Count);
        var blocks = new List<OrderBlock?>(regions.Count);
        var firstIndices = new List<int>(regions.Count);
        foreach (var region in regions)
        {
            firstIndices.Add(region.SpanIndices.Min());
            blocks.Add(new OrderBlock(region.Left, region.Bottom, region.Right, region.Top));
            groups.Add(new List<int>(region.SpanIndices));
        }

        var uncoveredRun = new List<int>();
        for (int spanIdx = 0; spanIdx < spans.Count; spanIdx++)
        {
            if (projected.Contains(spanIdx))
            {
                if (uncoveredRun.Count > 0)
                {
                    PushUncoveredRun(uncoveredRun, spans, groups, blocks, firstIndices);
                    uncoveredRun = new List<int>();
                }
            }
            else uncoveredRun.Add(spanIdx);
        }
        if (uncoveredRun.Count > 0) PushUncoveredRun(uncoveredRun, spans, groups, blocks, firstIndices);

        return OrderedIndices(blocks, firstIndices, false, null)
            .SelectMany(index => groups[index])
            .ToList();
    }

    /// <summary>
    /// Group a maximal run of layout-uncovered spans into its own block, mirroring
    /// <see cref="UncoveredGroup"/>'s batching on the segment path.
    /// </summary>
    private static void PushUncoveredRun(
        List<int> run, IReadOnlyList<ReadingOrderSpan> spans,
        List<List<int>> groups, List<OrderBlock?> blocks, List<int> firstIndices)
    {
        firstIndices.Add(run.Min());
        blocks.Add(SpansUnionBlock(run, spans));
        groups.Add(run);
    }
}
