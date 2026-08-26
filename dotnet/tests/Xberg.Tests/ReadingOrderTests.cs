using Xberg.Internal.Layout;
using Xberg.Internal.Pdf;
using Xunit;

using OrderBlock = Xberg.Internal.Layout.ReadingOrder.OrderBlock;

namespace Xberg.Tests;

/// <summary>
/// Port of the Rust <c>extractors/pdf/reading_order.rs</c> test module.
/// </summary>
/// <remarks>
/// These are upstream's own tests, fixture for fixture and assertion for assertion. Reading-order
/// output is a permutation, not text, so a golden-file comparison cannot reach it: this is the
/// verification that the port reproduces the algorithm rather than merely compiling.
/// </remarks>
public class ReadingOrderTests
{
    private static SegmentData PlannedSegment(string text, float x, float y, float width, float height) => new()
    {
        Text = text,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        FontSize = 10.0f,
        BaselineY = y,
    };

    private static LayoutRegionHint PlannedHint(
        LayoutHintClass className, float left, float bottom, float right, float top) =>
        new(className, 0.9f, left, bottom, right, top);

    private static LayoutRegionHint Hint(float left, float bottom, float right, float top) =>
        new(LayoutHintClass.Text, 0.95f, left, bottom, right, top);

    private static ReadingOrderSpan Span(string text, float x, float y, float width, float height,
                                         float rotation = 0.0f) =>
        new() { Text = text, X = x, Y = y, Width = width, Height = height, RotationDegrees = rotation };

    private static OrderBlock Block(float left, float bottom, float right, float top) =>
        new(left, bottom, right, top);

    private static List<int> Flatten(IEnumerable<LayoutSegmentGroup> groups) =>
        groups.SelectMany(group => group.SegmentIndices).ToList();

    private static List<LayoutSegmentGroup> Plan(
        IReadOnlyList<SegmentData> segments, IReadOnlyList<LayoutRegionHint> hints,
        bool noReorder, float? pageWidthPts, IReadOnlyList<bool>? wrapperOwnership = null) =>
        ReadingOrder.PlanSegmentGroupsByLayout(
            segments, hints, wrapperOwnership ?? Array.Empty<bool>(), noReorder, pageWidthPts);

    private static void AssertFragmentsRemainSeparate(List<SegmentData> segments, LayoutHintClass[] classes)
    {
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(classes[0], segments[0].X, segments[0].Y,
                        segments[0].X + segments[0].Width, segments[0].Y + segments[0].Height),
            PlannedHint(classes[1], segments[1].X, segments[1].Y,
                        segments[1].X + segments[1].Width, segments[1].Y + segments[1].Height),
        };
        var groups = Plan(segments, hints, noReorder: true, pageWidthPts: null);
        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 0, 1 }, Flatten(groups));
    }

    private static List<List<int>> Maps(int count, params (int Index, int[] Values)[] entries)
    {
        var maps = new List<List<int>>();
        for (int k = 0; k < count; k++) maps.Add(new List<int>());
        foreach (var (index, values) in entries) maps[index] = values.ToList();
        return maps;
    }

    // -------------------------------------------------------------------------------------
    // Horizontal dilation
    // -------------------------------------------------------------------------------------

    [Fact]
    public void HorizontalDilationUsesPageWidthThreshold()
    {
        var target = Block(100.0f, 100.0f, 200.0f, 120.0f);

        var acceptedBlocks = new List<OrderBlock> { target, Block(-50.0f, 200.0f, 200.0f, 220.0f) };
        var accepted = ReadingOrder.DilateHorizontally(
            acceptedBlocks, Maps(2, (0, new[] { 1 })), Maps(2), 1_000.0f);
        Assert.Equal(-50.0f, accepted[0].Left);

        var rejectedBlocks = new List<OrderBlock> { target, Block(-50.1f, 200.0f, 200.0f, 220.0f) };
        var rejected = ReadingOrder.DilateHorizontally(
            rejectedBlocks, Maps(2, (0, new[] { 1 })), Maps(2), 1_000.0f);
        Assert.Equal(target, rejected[0]);
    }

    [Fact]
    public void HorizontalDilationRollsBackPredecessorWhenSuccessorExceedsThreshold()
    {
        var target = Block(100.0f, 100.0f, 200.0f, 120.0f);
        var blocks = new List<OrderBlock>
        {
            target,
            Block(0.0f, 200.0f, 200.0f, 220.0f),
            Block(100.0f, 0.0f, 400.1f, 20.0f),
        };

        var dilated = ReadingOrder.DilateHorizontally(
            blocks, Maps(3, (0, new[] { 1 })), Maps(3, (0, new[] { 2 })), 1_000.0f);

        Assert.Equal(target, dilated[0]);
    }

    [Fact]
    public void HorizontalDilationPreservesRawBlocks()
    {
        var blocks = new List<OrderBlock>
        {
            Block(100.0f, 100.0f, 200.0f, 120.0f),
            Block(0.0f, 200.0f, 200.0f, 220.0f),
        };
        var original = new List<OrderBlock>(blocks);

        var dilated = ReadingOrder.DilateHorizontally(blocks, Maps(2, (0, new[] { 1 })), Maps(2), 1_000.0f);

        Assert.Equal(original, blocks);
        Assert.NotEqual(blocks[0], dilated[0]);
    }

    [Fact]
    public void HorizontalDilationUsesOnlyFirstNeighbors()
    {
        var blocks = new List<OrderBlock>
        {
            Block(40.0f, 40.0f, 50.0f, 50.0f),
            Block(35.0f, 60.0f, 50.0f, 70.0f),
            Block(20.0f, 80.0f, 50.0f, 90.0f),
            Block(40.0f, 20.0f, 55.0f, 30.0f),
            Block(40.0f, 0.0f, 70.0f, 10.0f),
        };

        var dilated = ReadingOrder.DilateHorizontally(
            blocks, Maps(5, (0, new[] { 1, 2 })), Maps(5, (0, new[] { 3, 4 })), 100.0f);

        Assert.Equal(35.0f, dilated[0].Left);
        Assert.Equal(55.0f, dilated[0].Right);
    }

    [Fact]
    public void InvalidPageWidthPreservesLegacyGraphOrder()
    {
        var blocks = new List<OrderBlock>
        {
            Block(0.0f, 200.0f, 100.0f, 220.0f),
            Block(0.0f, 100.0f, 100.0f, 120.0f),
            Block(200.0f, 200.0f, 300.0f, 220.0f),
            Block(200.0f, 100.0f, 300.0f, 120.0f),
        };
        var legacy = ReadingOrder.OrderBlocksByGraph(blocks, null);

        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, 0.0f, -1.0f })
            Assert.Equal(legacy, ReadingOrder.OrderBlocksByGraph(blocks, invalid));
    }

    [Fact]
    public void GraphRelationsAreRebuiltFromDilatedBlocks()
    {
        var blocks = new List<OrderBlock>
        {
            Block(0.0f, 200.0f, 120.0f, 220.0f),
            Block(80.0f, 300.0f, 160.0f, 320.0f),
            Block(120.0f, 300.0f, 240.0f, 320.0f),
            Block(160.0f, 200.0f, 280.0f, 220.0f),
        };

        Assert.Equal(new[] { 1, 0, 2, 3 }, ReadingOrder.OrderBlocksByGraph(blocks, null));
        Assert.Equal(new[] { 1, 2, 0, 3 }, ReadingOrder.OrderBlocksByGraph(blocks, 400.0f));
    }

    [Fact]
    public void SegmentPlanUsesPdfPageWidthForDilation()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("bottom-left", 10.0f, 205.0f, 10.0f, 10.0f),
            PlannedSegment("top-left", 90.0f, 305.0f, 10.0f, 10.0f),
            PlannedSegment("top-right", 200.0f, 305.0f, 10.0f, 10.0f),
            PlannedSegment("bottom-right", 250.0f, 205.0f, 10.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 200.0f, 120.0f, 220.0f),
            PlannedHint(LayoutHintClass.Text, 80.0f, 300.0f, 160.0f, 320.0f),
            PlannedHint(LayoutHintClass.Text, 120.0f, 300.0f, 240.0f, 320.0f),
            PlannedHint(LayoutHintClass.Text, 160.0f, 200.0f, 280.0f, 220.0f),
        };

        Assert.Equal(new[] { 1, 0, 2, 3 }, Flatten(Plan(segments, hints, false, null)));
        Assert.Equal(new[] { 1, 2, 0, 3 }, Flatten(Plan(segments, hints, false, 400.0f)));
    }

    // -------------------------------------------------------------------------------------
    // Region paths and ownership
    // -------------------------------------------------------------------------------------

    [Fact]
    public void PlanPreservesWrapperAndChildPaths()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("child", 20.0f, 70.0f, 20.0f, 10.0f),
            PlannedSegment("residual", 70.0f, 20.0f, 20.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Form, 0.0f, 0.0f, 100.0f, 100.0f),
            PlannedHint(LayoutHintClass.Text, 10.0f, 60.0f, 50.0f, 90.0f),
        };

        var groups = Plan(segments, hints, false, null);
        Assert.Equal(2, groups.Count);

        var child = groups.Single(group => group.HintIndices.SequenceEqual(new[] { 1 }));
        Assert.Equal(new[] { 0 }, child.SegmentIndices);
        Assert.Equal(0, child.RegionPath!.Value.Root.Id);
        Assert.Equal(1, child.RegionPath!.Value.Child!.Value.Id);

        var residual = groups.Single(group => group.SegmentIndices.SequenceEqual(new[] { 1 }));
        Assert.Equal(0, residual.RegionPath!.Value.Root.Id);
        Assert.Null(residual.RegionPath!.Value.Child);
    }

    [Fact]
    public void SegmentOwnerKeepsStrongerWrapperCoverage()
    {
        var segments = new List<SegmentData> { PlannedSegment("mostly wrapper", 0.0f, 0.0f, 100.0f, 100.0f) };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Form, 0.0f, 0.0f, 100.0f, 100.0f),
            PlannedHint(LayoutHintClass.Caption, 0.0f, 0.0f, 21.0f, 100.0f),
        };

        var groups = Plan(segments, hints, true, null);
        Assert.Single(groups);
        Assert.Equal(new[] { 0 }, groups[0].SegmentIndices);
        Assert.Empty(groups[0].HintIndices);
        var path = groups[0].RegionPath!.Value;
        Assert.Equal(0, path.Root.Id);
        Assert.Null(path.Child);
    }

    [Fact]
    public void PartialMinoritySingleColumnTextOwnershipPreservesNativeFlow()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("May 5, 2023", 10.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("To Whom it May Concern:", 10.0f, 80.0f, 90.0f, 10.0f),
            PlannedSegment("There were deliveries.", 10.0f, 60.0f, 100.0f, 10.0f),
            PlannedSegment("A total of 3 trucks were used.", 10.0f, 50.0f, 120.0f, 10.0f),
            PlannedSegment("Best Regards,", 10.0f, 30.0f, 50.0f, 10.0f),
            PlannedSegment("Mallori", 10.0f, 10.0f, 30.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 120.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 75.0f, 120.0f, 95.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.Single(groups);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, groups[0].SegmentIndices);
        Assert.Empty(groups[0].HintIndices);
        Assert.Null(groups[0].RegionPath);
    }

    [Fact]
    public void SingleColumnGeometryAcceptsLeftSpreadThreshold()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("wide", 0.0f, 20.0f, 20.0f, 10.0f),
            PlannedSegment("indented", 10.0f, 0.0f, 10.0f, 10.0f),
        };

        Assert.True(ReadingOrder.HasSingleColumnSegmentGeometry(segments, 200.0f));
        Assert.False(ReadingOrder.HasSingleColumnSegmentGeometry(
            new List<SegmentData> { segments[0], PlannedSegment("too far", 10.1f, 0.0f, 10.0f, 10.0f) },
            200.0f));
    }

    [Fact]
    public void SingleColumnGeometryAcceptsCommonWidthThreshold()
    {
        var atThreshold = new List<SegmentData>
        {
            PlannedSegment("left", 0.0f, 20.0f, 20.0f, 10.0f),
            PlannedSegment("right", 10.0f, 0.0f, 20.0f, 10.0f),
        };
        var belowThreshold = new List<SegmentData>
        {
            atThreshold[0],
            PlannedSegment("weak overlap", 10.1f, 0.0f, 20.0f, 10.0f),
        };

        Assert.True(ReadingOrder.HasSingleColumnSegmentGeometry(atThreshold, 400.0f));
        Assert.False(ReadingOrder.HasSingleColumnSegmentGeometry(belowThreshold, 400.0f));
    }

    [Fact]
    public void PartialTextOwnershipPreservesMultiColumnGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("left", 10.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("right", 210.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("uncovered left", 10.0f, 80.0f, 80.0f, 10.0f),
            PlannedSegment("uncovered right", 210.0f, 80.0f, 80.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 200.0f, 95.0f, 300.0f, 115.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.RegionPath is not null);
    }

    [Fact]
    public void OwnedLeftAndUncoveredRightPreserveLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("owned left one", 10.0f, 100.0f, 80.0f, 10.0f),
            PlannedSegment("owned left two", 10.0f, 80.0f, 80.0f, 10.0f),
            PlannedSegment("uncovered right one", 180.0f, 100.0f, 100.0f, 10.0f),
            PlannedSegment("uncovered right two", 180.0f, 80.0f, 100.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 75.0f, 100.0f, 95.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.HintIndices.Count > 0);
    }

    [Fact]
    public void StaggeredColumnsPreserveLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("owned left one", 10.0f, 120.0f, 80.0f, 10.0f),
            PlannedSegment("owned left two", 10.0f, 100.0f, 80.0f, 10.0f),
            PlannedSegment("uncovered right one", 180.0f, 60.0f, 100.0f, 10.0f),
            PlannedSegment("uncovered right two", 180.0f, 40.0f, 100.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 115.0f, 100.0f, 135.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.HintIndices.Count > 0);
    }

    [Fact]
    public void WeakHorizontalOverlapPreservesLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("owned left one", 10.0f, 100.0f, 120.0f, 10.0f),
            PlannedSegment("owned left two", 10.0f, 80.0f, 120.0f, 10.0f),
            PlannedSegment("uncovered right one", 120.0f, 100.0f, 120.0f, 10.0f),
            PlannedSegment("uncovered right two", 120.0f, 80.0f, 120.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 140.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 75.0f, 140.0f, 95.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.HintIndices.Count > 0);
    }

    [Fact]
    public void MajorityTextOwnershipPreservesLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("one", 10.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("two", 10.0f, 80.0f, 40.0f, 10.0f),
            PlannedSegment("three", 10.0f, 60.0f, 40.0f, 10.0f),
            PlannedSegment("uncovered", 10.0f, 40.0f, 40.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 75.0f, 100.0f, 95.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 55.0f, 100.0f, 75.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.HintIndices.Count > 0);
    }

    [Fact]
    public void PartialTextOwnershipHasNoSegmentCountCliff()
    {
        var segments = Enumerable.Range(0, 12)
            .Select(index => PlannedSegment("line", 10.0f, 200.0f - index * 20.0f, 40.0f, 10.0f))
            .ToList();
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 195.0f, 100.0f, 215.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 175.0f, 100.0f, 195.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.Single(groups);
        Assert.Null(groups[0].RegionPath);
    }

    [Fact]
    public void SemanticOwnerPreservesPartialLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("body", 10.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("Section", 10.0f, 80.0f, 40.0f, 10.0f),
            PlannedSegment("uncovered one", 10.0f, 60.0f, 70.0f, 10.0f),
            PlannedSegment("uncovered two", 10.0f, 40.0f, 70.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
            PlannedHint(LayoutHintClass.SectionHeader, 0.0f, 75.0f, 100.0f, 95.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.HintIndices.SequenceEqual(new[] { 1 }));
    }

    [Fact]
    public void UnownedSemanticHintPreservesPartialLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("owned one", 10.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("owned two", 10.0f, 80.0f, 40.0f, 10.0f),
            PlannedSegment("uncovered one", 10.0f, 60.0f, 70.0f, 10.0f),
            PlannedSegment("uncovered two", 10.0f, 40.0f, 70.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 75.0f, 100.0f, 95.0f),
            PlannedHint(LayoutHintClass.SectionHeader, 200.0f, 200.0f, 280.0f, 220.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group => group.HintIndices.Count > 0);
    }

    [Fact]
    public void WrapperRootPreservesPartialLayoutGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("child one", 10.0f, 100.0f, 40.0f, 10.0f),
            PlannedSegment("child two", 10.0f, 80.0f, 40.0f, 10.0f),
            PlannedSegment("uncovered one", 200.0f, 60.0f, 70.0f, 10.0f),
            PlannedSegment("uncovered two", 200.0f, 40.0f, 70.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Form, 0.0f, 70.0f, 100.0f, 120.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 95.0f, 100.0f, 115.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 75.0f, 100.0f, 95.0f),
        };

        var groups = Plan(segments, hints, false, 300.0f);

        Assert.True(groups.Count > 1);
        Assert.Contains(groups, group =>
            group.RegionPath is { } path && path.Root.ClassName == LayoutHintClass.Form);
    }

    // -------------------------------------------------------------------------------------
    // False-picture prose reconciliation
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ProseLikePictureOwnerIsDemotedIntoNativeFlow()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("The first page-wide sentence is ordinary body prose.", 10.0f, 100.0f, 280.0f, 10.0f),
            PlannedSegment("The second page-wide sentence continues the discussion.", 10.0f, 88.0f, 280.0f, 10.0f),
            PlannedSegment("A left fragment of the third sentence ", 10.0f, 76.0f, 80.0f, 10.0f),
            PlannedSegment("continues across the false picture boundary.", 95.0f, 76.0f, 195.0f, 10.0f),
            PlannedSegment("The fourth page-wide sentence completes the paragraph.", 10.0f, 64.0f, 280.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Picture, 100.0f, 60.0f, 300.0f, 115.0f),
        };

        var groups = Plan(segments, hints, false, 320.0f);

        Assert.Single(groups);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, groups[0].SegmentIndices);
        Assert.Null(groups[0].RegionPath);
    }

    [Fact]
    public void MixedPictureDemotesOnlyProseAndRetainsChartLabels()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("The first page-wide sentence is ordinary body prose.", 10.0f, 100.0f, 280.0f, 10.0f),
            PlannedSegment("The second page-wide sentence continues the discussion.", 10.0f, 88.0f, 280.0f, 10.0f),
            PlannedSegment("A left fragment of the third sentence ", 10.0f, 76.0f, 80.0f, 10.0f),
            PlannedSegment("continues across the false picture boundary.", 95.0f, 76.0f, 195.0f, 10.0f),
            PlannedSegment("The fourth page-wide sentence completes the paragraph.", 10.0f, 64.0f, 280.0f, 10.0f),
            PlannedSegment("12", 120.0f, 52.0f, 20.0f, 10.0f),
            PlannedSegment("Concentration (g)", 160.0f, 40.0f, 80.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Picture, 100.0f, 35.0f, 300.0f, 115.0f),
        };

        var groups = Plan(segments, hints, false, 320.0f);

        var picture = groups.Single(group =>
            group.RegionPath is { } path && path.Root.ClassName == LayoutHintClass.Picture);
        Assert.Equal(new[] { 5, 6 }, picture.SegmentIndices);
        Assert.Contains(groups, group => group.SegmentIndices.SequenceEqual(new[] { 0, 1, 2, 3, 4 }));
    }

    [Fact]
    public void BroadAlphabeticDiagramLabelsRemainOwnedByPicture()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("Input validation and parsing", 10.0f, 100.0f, 280.0f, 10.0f),
            PlannedSegment("Feature extraction and routing", 10.0f, 88.0f, 280.0f, 10.0f),
            PlannedSegment("Model ", 10.0f, 76.0f, 80.0f, 10.0f),
            PlannedSegment("inference and scoring stage", 110.0f, 76.0f, 180.0f, 10.0f),
            PlannedSegment("Output formatting and storage", 10.0f, 64.0f, 280.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Picture, 100.0f, 60.0f, 300.0f, 115.0f),
        };

        var groups = Plan(segments, hints, false, 320.0f);

        Assert.Contains(groups, group =>
            group.RegionPath is { } path && path.Root.ClassName == LayoutHintClass.Picture);
    }

    [Fact]
    public void SideBySideBodyTextDoesNotDemotePictureLabels()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("Series A", 220.0f, 100.0f, 30.0f, 10.0f),
            PlannedSegment("The left body column contains ordinary paragraph text.", 10.0f, 100.0f, 150.0f, 10.0f),
            PlannedSegment("Series B", 220.0f, 88.0f, 30.0f, 10.0f),
            PlannedSegment("Another body line supplies many alphabetic words here.", 10.0f, 88.0f, 150.0f, 10.0f),
            PlannedSegment("Series C", 220.0f, 76.0f, 30.0f, 10.0f),
            PlannedSegment("The final body line remains outside the figure region.", 10.0f, 76.0f, 150.0f, 10.0f),
            PlannedSegment("Axis label", 220.0f, 64.0f, 30.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Picture, 200.0f, 60.0f, 300.0f, 115.0f),
        };

        var groups = Plan(segments, hints, false, 320.0f);

        var picture = groups.Single(group =>
            group.RegionPath is { } path && path.Root.ClassName == LayoutHintClass.Picture);
        Assert.Equal(new[] { 0, 2, 4, 6 }, picture.SegmentIndices);
    }

    [Fact]
    public void FalsePictureReconciliationPreservesOtherSemanticOwners()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("A full body sentence with enough words for prose.", 10.0f, 100.0f, 280.0f, 10.0f),
            PlannedSegment("Another full body sentence continues the paragraph.", 10.0f, 88.0f, 280.0f, 10.0f),
            PlannedSegment("A left fragment of the third sentence ", 10.0f, 76.0f, 80.0f, 10.0f),
            PlannedSegment("continues across the false picture boundary.", 95.0f, 76.0f, 195.0f, 10.0f),
            PlannedSegment("A final full body sentence closes the paragraph.", 10.0f, 64.0f, 280.0f, 10.0f),
            PlannedSegment("table cell", 10.0f, 40.0f, 70.0f, 8.0f),
            PlannedSegment("Figure 1.", 10.0f, 25.0f, 70.0f, 8.0f),
            PlannedSegment("ordinary text", 10.0f, 10.0f, 70.0f, 8.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Picture, 100.0f, 60.0f, 300.0f, 115.0f),
            PlannedHint(LayoutHintClass.Table, 0.0f, 35.0f, 90.0f, 50.0f),
            PlannedHint(LayoutHintClass.Caption, 0.0f, 20.0f, 90.0f, 35.0f),
            PlannedHint(LayoutHintClass.Text, 0.0f, 5.0f, 90.0f, 20.0f),
        };

        var groups = Plan(segments, hints, false, 320.0f);

        foreach (var (index, className) in new[]
                 {
                     (5, LayoutHintClass.Table),
                     (6, LayoutHintClass.Caption),
                     (7, LayoutHintClass.Text),
                 })
            Assert.Contains(groups, group =>
                group.SegmentIndices.SequenceEqual(new[] { index })
                && group.RegionPath is { } path && path.Root.ClassName == className);
    }

    [Fact]
    public void NearFullSemanticChildOutranksWrapper()
    {
        foreach (var className in new[] { LayoutHintClass.Title, LayoutHintClass.ListItem, LayoutHintClass.Text })
        {
            var segments = new List<SegmentData> { PlannedSegment("semantic", 0.0f, 0.0f, 100.0f, 100.0f) };
            var hints = new List<LayoutRegionHint>
            {
                PlannedHint(LayoutHintClass.Form, 0.0f, 0.0f, 100.0f, 100.0f),
                PlannedHint(className, 5.0f, 0.0f, 95.0f, 100.0f),
            };

            var groups = Plan(segments, hints, true, null);
            Assert.Single(groups);
            Assert.Equal(new[] { 0 }, groups[0].SegmentIndices);
            Assert.Equal(new[] { 1 }, groups[0].HintIndices);
            var path = groups[0].RegionPath!.Value;
            Assert.Equal(0, path.Root.Id);
            Assert.Equal(1, path.Child!.Value.Id);
        }
    }

    // -------------------------------------------------------------------------------------
    // Atomic (kerning-split) fragments
    // -------------------------------------------------------------------------------------

    [Fact]
    public void SplitEliTFragmentsShareTheOwnedLayoutOwner()
    {
        var eli = PlannedSegment("eli", 100.0f, 700.0f, 15.0017f, 11.0f);
        eli.FontSize = 11.0f;
        var orphanT = PlannedSegment("t", 115.0f, 700.0f, 5.0f, 11.0f);
        orphanT.FontSize = 11.0f;
        var segments = new List<SegmentData> { eli, orphanT };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 100.0f, 700.0f, 115.0f, 711.0f),
        };

        var groups = Plan(segments, hints, true, null);

        Assert.Single(groups);
        Assert.Equal(new[] { 0, 1 }, groups[0].SegmentIndices);
        Assert.Equal(new[] { 0 }, groups[0].HintIndices);
        Assert.Equal(0, groups[0].RegionPath!.Value.Root.Id);
    }

    [Fact]
    public void SplitTAbleFragmentsShareTheOwnedLayoutOwner()
    {
        var orphanT = PlannedSegment("T", 100.0f, 700.0f, 7.224f, 11.0f);
        orphanT.FontSize = 11.0f;
        var able = PlannedSegment("able", 106.0f, 700.0f, 22.0f, 11.0f);
        able.FontSize = 11.0f;
        var segments = new List<SegmentData> { orphanT, able };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 106.0f, 700.0f, 128.0f, 711.0f),
        };

        var groups = Plan(segments, hints, true, null);

        Assert.Single(groups);
        Assert.Equal(new[] { 0, 1 }, groups[0].SegmentIndices);
        Assert.Equal(new[] { 0 }, groups[0].HintIndices);
        Assert.Equal(0, groups[0].RegionPath!.Value.Root.Id);
    }

    [Fact]
    public void AtomicFragmentsWithDistinctTextOwnersPreserveBothPaths()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("eli", 100.0f, 700.0f, 15.0f, 10.0f),
            PlannedSegment("t", 115.0f, 700.0f, 5.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 100.0f, 700.0f, 115.0f, 710.0f),
            PlannedHint(LayoutHintClass.Text, 115.0f, 700.0f, 120.0f, 710.0f),
        };

        var groups = Plan(segments, hints, true, null);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 0 }, groups[0].SegmentIndices);
        Assert.Equal(0, groups[0].RegionPath!.Value.Root.Id);
        Assert.Equal(new[] { 1 }, groups[1].SegmentIndices);
        Assert.Equal(1, groups[1].RegionPath!.Value.Root.Id);
    }

    [Fact]
    public void AtomicFragmentOwnershipRejectsSpacingAndGeometryBoundaries()
    {
        var textClasses = new[] { LayoutHintClass.Text, LayoutHintClass.Text };
        AssertFragmentsRemainSeparate(new List<SegmentData>
        {
            PlannedSegment("office", 100.0f, 700.0f, 30.0f, 10.0f),
            PlannedSegment("is", 140.0f, 700.0f, 8.0f, 10.0f),
        }, textClasses);
        AssertFragmentsRemainSeparate(new List<SegmentData>
        {
            PlannedSegment("right", 200.0f, 700.0f, 20.0f, 10.0f),
            PlannedSegment("left", 10.0f, 700.0f, 20.0f, 10.0f),
        }, textClasses);
        AssertFragmentsRemainSeparate(new List<SegmentData>
        {
            PlannedSegment("end", 100.0f, 700.0f, 15.0f, 10.0f),
            PlannedSegment("start", 115.0f, 680.0f, 20.0f, 10.0f),
        }, textClasses);
    }

    [Fact]
    public void AtomicFragmentOwnershipRejectsExcessiveOverlap()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("owned", 100.0f, 700.0f, 30.0f, 10.0f),
            PlannedSegment("orphan", 110.0f, 700.0f, 200.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 100.0f, 700.0f, 130.0f, 710.0f),
        };

        var groups = Plan(segments, hints, true, null);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 0, 1 }, Flatten(groups));
    }

    [Fact]
    public void AtomicFragmentOwnershipRejectsStyleAndTextBoundaries()
    {
        var textClasses = new[] { LayoutHintClass.Text, LayoutHintClass.Text };

        var bold = PlannedSegment("t", 115.0f, 700.0f, 5.0f, 10.0f);
        bold.IsBold = true;
        AssertFragmentsRemainSeparate(
            new List<SegmentData> { PlannedSegment("eli", 100.0f, 700.0f, 15.0f, 10.0f), bold }, textClasses);

        var differentSize = PlannedSegment("t", 115.0f, 700.0f, 5.0f, 10.0f);
        differentSize.FontSize = 11.0f;
        AssertFragmentsRemainSeparate(
            new List<SegmentData> { PlannedSegment("eli", 100.0f, 700.0f, 15.0f, 10.0f), differentSize }, textClasses);

        AssertFragmentsRemainSeparate(new List<SegmentData>
        {
            PlannedSegment("eli ", 100.0f, 700.0f, 15.0f, 10.0f),
            PlannedSegment("t", 115.0f, 700.0f, 5.0f, 10.0f),
        }, textClasses);

        AssertFragmentsRemainSeparate(new List<SegmentData>
        {
            PlannedSegment("一", 100.0f, 700.0f, 12.0f, 10.0f),
            PlannedSegment("丁", 112.0f, 700.0f, 12.0f, 10.0f),
        }, textClasses);
    }

    [Fact]
    public void AtomicFragmentOwnershipRejectsSemanticBoundaries()
    {
        AssertFragmentsRemainSeparate(new List<SegmentData>
        {
            PlannedSegment("semantic", 100.0f, 700.0f, 40.0f, 10.0f),
            PlannedSegment("boundary", 140.0f, 700.0f, 40.0f, 10.0f),
        }, new[] { LayoutHintClass.Text, LayoutHintClass.Caption });
    }

    [Fact]
    public void AtomicFragmentOwnershipRejectsInvalidGeometryWithoutDroppingSegments()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("valid", 100.0f, 700.0f, 20.0f, 10.0f),
            PlannedSegment("invalid", float.NaN, 700.0f, 10.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 100.0f, 700.0f, 120.0f, 710.0f),
        };

        var groups = Plan(segments, hints, true, null);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 0, 1 }, Flatten(groups));
    }

    // -------------------------------------------------------------------------------------
    // Degenerate geometry and fallbacks
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ValidNonOverlappingHintReturnsPathlessFallback()
    {
        var segments = new List<SegmentData> { PlannedSegment("outside", 200.0f, 200.0f, 20.0f, 10.0f) };
        var hints = new List<LayoutRegionHint> { PlannedHint(LayoutHintClass.Text, 0.0f, 0.0f, 100.0f, 100.0f) };

        var groups = Plan(segments, hints, true, null);
        Assert.Single(groups);
        Assert.Equal(new[] { 0 }, groups[0].SegmentIndices);
        Assert.Empty(groups[0].HintIndices);
        Assert.Null(groups[0].RegionPath);
    }

    [Fact]
    public void PlanKeepsUncoveredRunsDistinctAndComplete()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("outside-before", 200.0f, 80.0f, 10.0f, 10.0f),
            PlannedSegment("inside", 10.0f, 50.0f, 10.0f, 10.0f),
            PlannedSegment("outside-after", 200.0f, 20.0f, 10.0f, 10.0f),
        };
        var hints = new List<LayoutRegionHint> { PlannedHint(LayoutHintClass.Text, 0.0f, 40.0f, 100.0f, 70.0f) };

        var groups = Plan(segments, hints, true, null);

        Assert.Equal(new[] { 0, 1, 2 }, Flatten(groups));
        Assert.Equal(3, groups.Count);
        Assert.NotEqual(groups[0].RegionPath!.Value.Root.Id, groups[2].RegionPath!.Value.Root.Id);
    }

    [Fact]
    public void RootOrderUsesOwnedContentGeometryOverNoisyLayoutGeometry()
    {
        var lowContent = Block(70.0f, 100.0f, 500.0f, 112.0f);
        var highContent = Block(70.0f, 200.0f, 280.0f, 250.0f);
        var noisyLowRoot = Block(100.0f, 105.0f, 600.0f, 260.0f);

        List<ReadingOrder.PlannedGroup> MakeGroups() => new()
        {
            new ReadingOrder.PlannedGroup
            {
                Output = new LayoutSegmentGroup
                {
                    SegmentIndices = new List<int> { 0 },
                    HintIndices = new List<int> { 0 },
                },
                RootId = 0,
                OrderBlockValue = noisyLowRoot,
                ContentBlock = lowContent,
                FirstSegmentIndex = 0,
            },
            new ReadingOrder.PlannedGroup
            {
                Output = new LayoutSegmentGroup { SegmentIndices = new List<int> { 1 } },
                RootId = 1,
                OrderBlockValue = highContent,
                ContentBlock = highContent,
                FirstSegmentIndex = 1,
            },
        };

        var rootBlocks = new List<OrderBlock?> { noisyLowRoot, highContent };

        var reordered = ReadingOrder.OrderPlannedGroups(MakeGroups(), rootBlocks, false, 600.0f);
        var native = ReadingOrder.OrderPlannedGroups(MakeGroups(), rootBlocks, true, 600.0f);

        Assert.Equal(new[] { 1 }, reordered[0].SegmentIndices);
        Assert.Equal(new[] { 0 }, reordered[1].SegmentIndices);
        Assert.Equal(new[] { 0 }, native[0].SegmentIndices);
        Assert.Equal(new[] { 1 }, native[1].SegmentIndices);
    }

    [Fact]
    public void EffectiveRootGeometryPreservesLayoutWidthAndWrapperBounds()
    {
        var layout = Block(0.0f, 100.0f, 600.0f, 300.0f);
        var content = Block(200.0f, 150.0f, 400.0f, 175.0f);

        ReadingOrder.PlannedGroup MakeGroup(LayoutHintClass className, OrderBlock? contentBlock) => new()
        {
            Output = new LayoutSegmentGroup
            {
                SegmentIndices = new List<int> { 0 },
                HintIndices = new List<int> { 0 },
                RegionPath = new LayoutRegionPath(new LayoutRegionTag(0, className), null),
            },
            RootId = 0,
            OrderBlockValue = layout,
            ContentBlock = contentBlock,
            FirstSegmentIndex = 0,
        };

        var rootBlocks = new List<OrderBlock?> { layout };
        var heading = ReadingOrder.EffectiveRootOrderBlock(
            0, new List<ReadingOrder.PlannedGroup> { MakeGroup(LayoutHintClass.SectionHeader, content) }, rootBlocks);
        var wrapper = ReadingOrder.EffectiveRootOrderBlock(
            0, new List<ReadingOrder.PlannedGroup> { MakeGroup(LayoutHintClass.Picture, content) }, rootBlocks);
        var invalidContent = ReadingOrder.EffectiveRootOrderBlock(
            0, new List<ReadingOrder.PlannedGroup> { MakeGroup(LayoutHintClass.Text, null) }, rootBlocks);

        Assert.Equal(Block(0.0f, 150.0f, 600.0f, 175.0f), heading);
        Assert.Equal(layout, wrapper);
        Assert.Equal(layout, invalidContent);
    }

    [Fact]
    public void StrictContentGeometryRejectsPartiallyInvalidGroups()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("valid", 10.0f, 20.0f, 30.0f, 10.0f),
            PlannedSegment("invalid", float.NaN, 20.0f, 30.0f, 10.0f),
        };

        Assert.Equal(ReadingOrder.SegmentBlock(segments[0]),
                     ReadingOrder.StrictSegmentsUnionBlock(new[] { 0 }, segments));
        Assert.Null(ReadingOrder.StrictSegmentsUnionBlock(new[] { 0, 1 }, segments));
        Assert.Null(ReadingOrder.StrictSegmentsUnionBlock(new[] { 1 }, segments));
    }

    [Fact]
    public void PlanRejectsNonFiniteDerivedGeometry()
    {
        var segments = new List<SegmentData>
        {
            PlannedSegment("overflow", float.MaxValue, 10.0f, float.MaxValue, 10.0f),
        };

        var groups = Plan(segments, new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 0.0f, 100.0f, 100.0f),
        }, true, null);
        Assert.Single(groups);
        Assert.Equal(new[] { 0 }, groups[0].SegmentIndices);
        Assert.Empty(groups[0].HintIndices);

        groups = Plan(segments, new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 0.0f, float.PositiveInfinity, 100.0f),
        }, true, null);
        Assert.Single(groups);
        Assert.Equal(new[] { 0 }, groups[0].SegmentIndices);
        Assert.Null(groups[0].RegionPath);
    }

    [Fact]
    public void EmptyWrapperValidationPromotesChildToRoot()
    {
        var segments = new List<SegmentData> { PlannedSegment("child", 20.0f, 70.0f, 20.0f, 10.0f) };
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Picture, 0.0f, 0.0f, 100.0f, 100.0f),
            PlannedHint(LayoutHintClass.Caption, 10.0f, 60.0f, 50.0f, 90.0f),
        };

        var groups = Plan(segments, hints, true, null, wrapperOwnership: new[] { false });
        var path = groups[0].RegionPath!.Value;
        Assert.Equal(1, path.Root.Id);
        Assert.Null(path.Child);
    }

    // -------------------------------------------------------------------------------------
    // Spans
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ProjectSpansToRegionsAssignsEachSpanToItsColumn()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("Left column", 110.0f, 450.0f, 70.0f, 12.0f),
            Span("Right column", 410.0f, 450.0f, 75.0f, 12.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            Hint(100.0f, 100.0f, 200.0f, 500.0f),
            Hint(400.0f, 100.0f, 500.0f, 500.0f),
        };

        var regions = ReadingOrder.ProjectSpansToRegions(spans, hints);

        Assert.Equal(2, regions.Count);
        Assert.Equal(new[] { 0 }, regions[0].SpanIndices);
        Assert.Equal(new[] { 1 }, regions[1].SpanIndices);
    }

    [Fact]
    public void ReorderSpansTwoColumnLayout()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("A", 110.0f, 450.0f, 10.0f, 12.0f),
            Span("B", 110.0f, 200.0f, 10.0f, 12.0f),
            Span("C", 410.0f, 450.0f, 10.0f, 12.0f),
            Span("D", 410.0f, 200.0f, 10.0f, 12.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            Hint(100.0f, 100.0f, 200.0f, 500.0f),
            Hint(400.0f, 100.0f, 500.0f, 500.0f),
        };

        Assert.Equal(new[] { 0, 1, 2, 3 }, ReadingOrder.ReorderSpansByLayout(spans, hints));
    }

    /// <summary>
    /// Segment-level reorder must produce true column-major reading order from interleaved input,
    /// independent of the layout-hint ordering.
    /// </summary>
    /// <remarks>
    /// The hints here are supplied right-column-first; a correct reorder still yields A, B, C, D.
    /// An implementation that emitted segments in raw hint order would yield C, D, A, B.
    /// </remarks>
    [Fact]
    public void ReorderSegmentsTwoColumnIndependentOfHintOrder()
    {
        SegmentData Seg(string text, float x, float y) => PlannedSegment(text, x, y, 10.0f, 12.0f);

        var segments = new List<SegmentData>
        {
            Seg("A", 110.0f, 450.0f),
            Seg("C", 410.0f, 450.0f),
            Seg("B", 110.0f, 200.0f),
            Seg("D", 410.0f, 200.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            Hint(400.0f, 100.0f, 500.0f, 500.0f),
            Hint(100.0f, 100.0f, 200.0f, 500.0f),
        };

        var reordered = ReadingOrder.ReorderSegmentsByLayout(segments, hints, 500.0f);
        Assert.Equal(new[] { "A", "B", "C", "D" }, reordered.Select(segment => segment.Text));
    }

    /// <summary>
    /// A full-width title above two columns interrupts any left-to-right chaining across them.
    /// </summary>
    [Fact]
    public void ReorderSegmentsFullWidthHeadingBreaksColumns()
    {
        SegmentData Seg(string text, float x, float y) => PlannedSegment(text, x, y, 10.0f, 12.0f);

        var segments = new List<SegmentData>
        {
            Seg("Title", 50.0f, 470.0f),
            Seg("L1", 50.0f, 440.0f),
            Seg("R1", 270.0f, 440.0f),
            Seg("L2", 50.0f, 300.0f),
            Seg("R2", 270.0f, 300.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            Hint(40.0f, 460.0f, 460.0f, 490.0f),
            Hint(40.0f, 100.0f, 240.0f, 450.0f),
            Hint(260.0f, 100.0f, 460.0f, 450.0f),
        };

        var reordered = ReadingOrder.ReorderSegmentsByLayout(segments, hints, 500.0f);
        Assert.Equal(new[] { "Title", "L1", "L2", "R1", "R2" }, reordered.Select(segment => segment.Text));
    }

    /// <summary>
    /// A span that projects into no layout region must be interleaved into the reading order by
    /// position, not relocated to the end of the page.
    /// </summary>
    [Fact]
    public void ReorderSpansUncoveredSpanInterleavesBetweenRegions()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("TopSpan", 50.0f, 450.0f, 100.0f, 12.0f),
            Span("MarginalNote", 50.0f, 270.0f, 100.0f, 12.0f),
            Span("BottomSpan", 50.0f, 150.0f, 100.0f, 12.0f),
        };
        // Region A covers y in [300, 500]; region B covers y in [100, 250]. The marginal note's
        // centre (y = 276) falls in the gap between them, so it is assigned to neither.
        var hints = new List<LayoutRegionHint>
        {
            Hint(40.0f, 300.0f, 460.0f, 500.0f),
            Hint(40.0f, 100.0f, 460.0f, 250.0f),
        };

        Assert.Equal(new[] { 0, 1, 2 }, ReadingOrder.ReorderSpansByLayout(spans, hints));
    }

    [Fact]
    public void ReorderSpansMixedColumns()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("A", 110.0f, 480.0f, 10.0f, 12.0f),
            Span("B", 110.0f, 300.0f, 10.0f, 12.0f),
            Span("C", 410.0f, 470.0f, 10.0f, 12.0f),
            Span("D", 410.0f, 300.0f, 10.0f, 12.0f),
            Span("E", 410.0f, 150.0f, 10.0f, 12.0f),
            Span("X", 550.0f, 300.0f, 10.0f, 12.0f),
        };
        var hints = new List<LayoutRegionHint>
        {
            Hint(100.0f, 100.0f, 200.0f, 500.0f),
            Hint(400.0f, 100.0f, 500.0f, 500.0f),
        };

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, ReadingOrder.ReorderSpansByLayout(spans, hints));
    }

    [Fact]
    public void ReorderSpansEmptyInput() =>
        Assert.Empty(ReadingOrder.ReorderSpansByLayout(
            Array.Empty<ReadingOrderSpan>(), Array.Empty<LayoutRegionHint>()));

    [Fact]
    public void ReorderSpansNoHints()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("A", 100.0f, 100.0f, 10.0f, 12.0f),
            Span("B", 120.0f, 100.0f, 10.0f, 12.0f),
        };
        Assert.Equal(new[] { 0, 1 }, ReadingOrder.ReorderSpansByLayout(spans, Array.Empty<LayoutRegionHint>()));
    }

    /// <summary>
    /// Within a region, a heading with a higher native index than its subsections is emitted
    /// first — top-to-bottom, not native order.
    /// </summary>
    [Fact]
    public void IntraRegionSegmentOrderingHeadingBeforeSubsections()
    {
        SegmentData Seg(string text, float x, float y) => PlannedSegment(text, x, y, 80.0f, 12.0f);

        var segments = new List<SegmentData>
        {
            Seg("2.1 Algemeen", 50.0f, 200.0f),
            Seg("2.1.1 ErP label", 50.0f, 180.0f),
            Seg("2.1.2 Gascategorie", 50.0f, 160.0f),
            Seg("Table row 1", 50.0f, 140.0f),
            Seg("2 TOESTELGEGEVENS", 50.0f, 450.0f),
        };
        var hints = new List<LayoutRegionHint> { Hint(40.0f, 100.0f, 400.0f, 500.0f) };

        var reordered = ReadingOrder.ReorderSegmentsByLayout(segments, hints, 500.0f);
        Assert.Equal(
            new[] { "2 TOESTELGEGEVENS", "2.1 Algemeen", "2.1.1 ErP label", "2.1.2 Gascategorie", "Table row 1" },
            reordered.Select(segment => segment.Text));
    }

    [Fact]
    public void IntraRegionSubsectionOrdering()
    {
        SegmentData Seg(string text, float x, float y) => PlannedSegment(text, x, y, 80.0f, 12.0f);

        var segments = new List<SegmentData>
        {
            Seg("2.1.2 Gascategorie", 50.0f, 180.0f),
            Seg("2.1.1 ErP label", 50.0f, 200.0f),
        };
        var hints = new List<LayoutRegionHint> { Hint(40.0f, 100.0f, 400.0f, 500.0f) };

        var reordered = ReadingOrder.ReorderSegmentsByLayout(segments, hints, 500.0f);
        Assert.Equal(new[] { "2.1.1 ErP label", "2.1.2 Gascategorie" }, reordered.Select(segment => segment.Text));
    }

    [Fact]
    public void IntraRegionSpanOrderingHeadingBeforeSubsections()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("2.1 Algemeen", 50.0f, 200.0f, 80.0f, 12.0f),
            Span("2.1.1 ErP", 50.0f, 180.0f, 60.0f, 12.0f),
            Span("2.1.2 Gas", 50.0f, 160.0f, 60.0f, 12.0f),
            Span("2 TOESTEL", 50.0f, 450.0f, 80.0f, 12.0f),
        };
        var hints = new List<LayoutRegionHint> { Hint(40.0f, 100.0f, 400.0f, 500.0f) };

        Assert.Equal(new[] { 3, 0, 1, 2 }, ReadingOrder.ReorderSpansByLayout(spans, hints));
    }

    /// <summary>
    /// Rust's <c>f32::NAN</c>, whose sign bit is clear.
    /// </summary>
    /// <remarks>
    /// .NET's <see cref="float.NaN"/> is <c>0xffc00000</c> — a <em>negative</em> quiet NaN — while
    /// Rust's constant is <c>0x7fc00000</c>. Under either language's total order the sign bit
    /// decides whether NaN sorts above +inf or below -inf, so a test asserting where a NaN lands
    /// has to start from upstream's constant to be asserting the same thing.
    /// </remarks>
    private static readonly float RustNaN = BitConverter.Int32BitsToSingle(0x7fc0_0000);

    /// <summary>
    /// NaN coordinates must not produce a cyclic comparison.
    /// </summary>
    /// <remarks>
    /// A comparator built on <c>partial_cmp</c> defaulting to Equal makes NaN incomparable, which
    /// produces the cycle B &lt; A &lt; C &lt; B and can make a sort throw. The total order places
    /// a positive NaN after +inf, so the NaN-topped span simply sorts first.
    /// </remarks>
    [Fact]
    public void GeometricSortWithNanTopDoesNotThrow()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("A", 1.0f, RustNaN, 10.0f, 12.0f),
            Span("B", 0.0f, 5.0f, 10.0f, 12.0f),
            Span("C", 2.0f, 10.0f, 10.0f, 12.0f),
        };

        var order = ReadingOrder.ReorderSpansGeometric(spans);

        Assert.Equal(3, order.Count);
        Assert.Equal(0, order[0]);
        Assert.Equal(2, order[1]);
        Assert.Equal(1, order[2]);
    }

    [Fact]
    public void GeometricColumnFallbackTwoColumns()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("Left top", 50.0f, 450.0f, 80.0f, 12.0f),
            Span("Left bottom", 50.0f, 200.0f, 80.0f, 12.0f),
            Span("Right top", 300.0f, 450.0f, 80.0f, 12.0f),
            Span("Right bottom", 300.0f, 200.0f, 80.0f, 12.0f),
        };

        Assert.Equal(new[] { 0, 1, 2, 3 },
                     ReadingOrder.ReorderSpansByLayout(spans, Array.Empty<LayoutRegionHint>()));
    }

    // -------------------------------------------------------------------------------------
    // Rotated reading order
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Four cells of a 2x2 table rotated 90 degrees, fed in scrambled order.
    /// </summary>
    /// <remarks>
    /// Rotating -90 maps page <c>(x, y)</c> to <c>(advance, cross) = (y, -x)</c>: reading advances
    /// along page-y, and rows stack along page-x. The correct reading order is therefore row-major:
    /// A1, A2 (x=100, ascending y) then B1, B2 (x=200).
    /// </remarks>
    private static List<ReadingOrderSpan> ScrambledRotatedTable() => new()
    {
        Span("B2", 200.0f, 200.0f, 30.0f, 10.0f, 90.0f),
        Span("A1", 100.0f, 100.0f, 30.0f, 10.0f, 90.0f),
        Span("B1", 200.0f, 100.0f, 30.0f, 10.0f, 90.0f),
        Span("A2", 100.0f, 200.0f, 30.0f, 10.0f, 90.0f),
    };

    [Fact]
    public void ShouldOrderRotatedTableAlongItsOwnAxisWithinALayoutRegion()
    {
        var spans = ScrambledRotatedTable();
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 0.0f, 400.0f, 400.0f),
        };

        var order = ReadingOrder.ReorderSpansByLayout(spans, hints);

        Assert.Equal(new[] { "A1", "A2", "B1", "B2" }, order.Select(index => spans[index].Text));
    }

    [Fact]
    public void ShouldNotReverseOrGlueWordsWithinARotatedRow()
    {
        var spans = ScrambledRotatedTable();
        var hints = new List<LayoutRegionHint>
        {
            PlannedHint(LayoutHintClass.Text, 0.0f, 0.0f, 400.0f, 400.0f),
        };

        var order = ReadingOrder.ReorderSpansByLayout(spans, hints);
        int PositionOf(string text) => order.FindIndex(index => spans[index].Text == text);

        Assert.True(PositionOf("A1") < PositionOf("A2"));
        Assert.True(PositionOf("B1") < PositionOf("B2"));
        Assert.True(PositionOf("A2") < PositionOf("B1"));
    }

    /// <summary>
    /// An ordinary unrotated two-column layout is completely unaffected by the rotation-aware sort
    /// key, which reduces to the identity when the rotation is zero.
    /// </summary>
    [Fact]
    public void ShouldLeaveUnrotatedReadingOrderUnchanged()
    {
        var spans = new List<ReadingOrderSpan>
        {
            Span("Top left", 50.0f, 400.0f, 80.0f, 12.0f),
            Span("Bottom left", 50.0f, 200.0f, 80.0f, 12.0f),
            Span("Top right", 300.0f, 400.0f, 80.0f, 12.0f),
            Span("Bottom right", 300.0f, 200.0f, 80.0f, 12.0f),
        };

        Assert.Equal(new[] { 0, 1, 2, 3 },
                     ReadingOrder.ReorderSpansByLayout(spans, Array.Empty<LayoutRegionHint>()));
    }
}
