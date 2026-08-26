using Xberg.Internal.Onnx;
using Xberg.Internal.Onnx.Ops;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Direct tests for the ONNX kernels added for PP-DocLayout-V3.
/// </summary>
/// <remarks>
/// The model exercises these, but only along the paths its own graph takes — a single
/// <c>batch_dims</c> value, one einsum equation, one <c>Mod</c> sign. These cover the contract
/// rather than the one call site.
/// </remarks>
public class OnnxOpsTests
{
    private static Tensor Floats(int[] shape, params float[] values) => Tensor.FromFloats(values, shape);

    private static Tensor Longs(int[] shape, params long[] values) =>
        Tensor.FromLongs(values, ElementType.Int64, shape);

    private static Tensor Bools(int[] shape, params long[] values) =>
        Tensor.FromLongs(values, ElementType.Bool, shape);

    // ------------------------------------------------------------------ Where

    [Fact]
    public void WhereSelectsElementwise()
    {
        var result = Elementwise.Where(
            Bools([4], 1, 0, 1, 0),
            Floats([4], 10, 20, 30, 40),
            Floats([4], 1, 2, 3, 4));
        Assert.Equal(new[] { 10f, 2f, 30f, 4f }, result.Floats);
    }

    /// <summary>A broadcast operand is re-read rather than indexed, which is stride 0.</summary>
    [Fact]
    public void WhereBroadcastsAllThreeOperands()
    {
        var result = Elementwise.Where(
            Bools([2, 1], 1, 0),
            Floats([1, 3], 1, 2, 3),
            Floats([1], 9));
        Assert.Equal(new[] { 2, 3 }, result.Shape);
        Assert.Equal(new[] { 1f, 2f, 3f, 9f, 9f, 9f }, result.Floats);
    }

    /// <summary>
    /// Integral branches stay integral: a shape vector selected through <c>Where</c> must not
    /// round-trip through float, where a large dimension would lose exactness.
    /// </summary>
    [Fact]
    public void WhereKeepsIntegralBranchesIntegral()
    {
        long big = (1L << 53) + 1;
        var result = Elementwise.Where(Bools([2], 1, 0), Longs([2], big, 7), Longs([2], 5, big));
        Assert.False(result.IsFloat);
        Assert.Equal(new[] { big, big }, result.Longs);
    }

    // ------------------------------------------------------------------ Range

    [Fact]
    public void RangeCountsByDelta()
    {
        var result = Shapes.Range(Longs([1], 2), Longs([1], 10), Longs([1], 3));
        Assert.Equal(new long[] { 2, 5, 8 }, result.Longs);
    }

    [Fact]
    public void RangeHandlesNegativeDelta()
    {
        var result = Shapes.Range(Longs([1], 10), Longs([1], 4), Longs([1], -2));
        Assert.Equal(new long[] { 10, 8, 6 }, result.Longs);
    }

    /// <summary>A delta pointing away from the limit is an empty range, not a negative one.</summary>
    [Fact]
    public void RangeWithUnreachableLimitIsEmpty() =>
        Assert.Empty(Shapes.Range(Longs([1], 5), Longs([1], 10), Longs([1], -1)).Longs);

    [Fact]
    public void RangeSupportsFloats()
    {
        var result = Shapes.Range(Floats([1], 0f), Floats([1], 1f), Floats([1], 0.25f));
        Assert.Equal(new[] { 0f, 0.25f, 0.5f, 0.75f }, result.Floats);
    }

    // ------------------------------------------------------------------ GatherND

    [Fact]
    public void GatherNdGathersFullCoordinates()
    {
        var data = Floats([2, 2], 0, 1, 2, 3);
        var result = Indexing.GatherND(data, Longs([2, 2], 0, 0, 1, 1), 0);
        Assert.Equal(new[] { 0f, 3f }, result.Floats);
    }

    /// <summary>A short index tuple gathers whole slices, not single elements.</summary>
    [Fact]
    public void GatherNdGathersSlicesForPartialCoordinates()
    {
        var data = Floats([2, 2], 0, 1, 2, 3);
        var result = Indexing.GatherND(data, Longs([2, 1], 1, 0), 0);
        Assert.Equal(new[] { 2, 2 }, result.Shape);
        Assert.Equal(new[] { 2f, 3f, 0f, 1f }, result.Floats);
    }

    [Fact]
    public void GatherNdNegativeIndexCountsFromTheEnd()
    {
        var data = Floats([3], 7, 8, 9);
        Assert.Equal(new[] { 9f }, Indexing.GatherND(data, Longs([1, 1], -1), 0).Floats);
    }

    /// <summary>
    /// With one batch dimension, batch element <c>b</c> of the indices only reads batch element
    /// <c>b</c> of the data.
    /// </summary>
    [Fact]
    public void GatherNdBatchDimsStayWithinTheirBatch()
    {
        var data = Floats([2, 2], 0, 1, 2, 3);
        var result = Indexing.GatherND(data, Longs([2, 1], 1, 0), 1);
        Assert.Equal(new[] { 1f, 2f }, result.Floats);
    }

    [Fact]
    public void GatherNdRejectsOutOfRangeIndex() =>
        Assert.Throws<InvalidDataException>(() =>
            Indexing.GatherND(Floats([2], 1, 2), Longs([1, 1], 5), 0));

    // ------------------------------------------------------------------ ScatterND

    [Fact]
    public void ScatterNdReplacesNamedElements()
    {
        var result = Indexing.ScatterND(
            Floats([4], 1, 2, 3, 4), Longs([2, 1], 0, 3), Floats([2], 10, 40));
        Assert.Equal(new[] { 10f, 2f, 3f, 40f }, result.Floats);
    }

    [Fact]
    public void ScatterNdReplacesWholeSlices()
    {
        var result = Indexing.ScatterND(
            Floats([2, 2], 1, 2, 3, 4), Longs([1, 1], 1), Floats([1, 2], 30, 40));
        Assert.Equal(new[] { 1f, 2f, 30f, 40f }, result.Floats);
    }

    [Fact]
    public void ScatterNdLeavesTheInputUnchanged()
    {
        var data = Floats([2], 1, 2);
        Indexing.ScatterND(data, Longs([1, 1], 0), Floats([1], 99));
        Assert.Equal(new[] { 1f, 2f }, data.Floats);
    }

    // ------------------------------------------------------------------ EyeLike

    [Fact]
    public void EyeLikeBuildsAnIdentity()
    {
        var result = Indexing.EyeLike(Floats([3, 3], new float[9]), 0, null);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }, result.Floats);
    }

    [Fact]
    public void EyeLikeShiftsByK()
    {
        var result = Indexing.EyeLike(Floats([2, 3], new float[6]), 1, null);
        Assert.Equal(new[] { 0f, 1f, 0f, 0f, 0f, 1f }, result.Floats);
    }

    [Fact]
    public void EyeLikeIgnoresTheInputValues()
    {
        var result = Indexing.EyeLike(Floats([2, 2], 5, 6, 7, 8), 0, null);
        Assert.Equal(new[] { 1f, 0f, 0f, 1f }, result.Floats);
    }

    // ------------------------------------------------------------------ Floor / Compare / Mod

    [Fact]
    public void FloorRoundsTowardNegativeInfinity()
    {
        var result = Elementwise.Floor(Floats([4], 1.7f, -1.2f, 2.0f, -0.5f));
        Assert.Equal(new[] { 1f, -2f, 2f, -1f }, result.Floats);
    }

    [Fact]
    public void FloorLeavesIntegralTensorsAlone()
    {
        var data = Longs([2], 3, -4);
        Assert.Equal(new long[] { 3, -4 }, Elementwise.Floor(data).Longs);
    }

    [Fact]
    public void CompareProducesABooleanTensor()
    {
        var result = Elementwise.Compare(Floats([3], 1, 2, 3), Floats([1], 2), CompareKind.Greater);
        Assert.Equal(ElementType.Bool, result.Type);
        Assert.Equal(new long[] { 0, 0, 1 }, result.Longs);
    }

    /// <summary>
    /// Integral operands compare as integers: two int64 values differing only beyond float's
    /// 24-bit mantissa are still unequal.
    /// </summary>
    [Fact]
    public void CompareKeepsIntegralPrecision()
    {
        long a = (1L << 53) + 1;
        var result = Elementwise.Compare(Longs([1], a), Longs([1], 1L << 53), CompareKind.Greater);
        Assert.Equal(new long[] { 1 }, result.Longs);
    }

    /// <summary>
    /// The two remainder conventions differ in sign: <c>fmod</c> follows the dividend, the
    /// default follows the divisor.
    /// </summary>
    [Fact]
    public void ModSignFollowsTheConvention()
    {
        Assert.Equal(new long[] { 1 }, Elementwise.Mod(Longs([1], -5), Longs([1], 3), fmod: false).Longs);
        Assert.Equal(new long[] { -2 }, Elementwise.Mod(Longs([1], -5), Longs([1], 3), fmod: true).Longs);
    }

    [Fact]
    public void ModWorksOnFloats()
    {
        var result = Elementwise.Mod(Floats([1], 5.5f), Floats([1], 2f), fmod: true);
        Assert.Equal(1.5f, result.Floats[0], 5);
    }

    // ------------------------------------------------------------------ Einsum

    [Fact]
    public void EinsumMatrixMultiply()
    {
        var result = EinsumKernel.Apply("ij,jk->ik",
            [Floats([2, 2], 1, 2, 3, 4), Floats([2, 2], 5, 6, 7, 8)]);
        Assert.Equal(new[] { 2, 2 }, result.Shape);
        Assert.Equal(new[] { 19f, 22f, 43f, 50f }, result.Floats);
    }

    [Fact]
    public void EinsumTransposeOfASingleOperand()
    {
        var result = EinsumKernel.Apply("ij->ji", [Floats([2, 3], 1, 2, 3, 4, 5, 6)]);
        Assert.Equal(new[] { 3, 2 }, result.Shape);
        Assert.Equal(new[] { 1f, 4f, 2f, 5f, 3f, 6f }, result.Floats);
    }

    [Fact]
    public void EinsumSumsOverALabelMissingFromTheOutput()
    {
        var result = EinsumKernel.Apply("ij->i", [Floats([2, 3], 1, 2, 3, 4, 5, 6)]);
        Assert.Equal(new[] { 6f, 15f }, result.Floats);
    }

    /// <summary>An equation with no arrow implies the labels appearing exactly once, in order.</summary>
    [Fact]
    public void EinsumImplicitOutputIsAlphabeticalSingletons()
    {
        var result = EinsumKernel.Apply("ij,jk", [Floats([2, 2], 1, 2, 3, 4), Floats([2, 2], 5, 6, 7, 8)]);
        Assert.Equal(new[] { 2, 2 }, result.Shape);
        Assert.Equal(new[] { 19f, 22f, 43f, 50f }, result.Floats);
    }

    [Fact]
    public void EinsumBatchedContraction()
    {
        // Two independent 1x2 by 2x1 dot products.
        var result = EinsumKernel.Apply("bij,bjk->bik",
            [Floats([2, 1, 2], 1, 2, 3, 4), Floats([2, 2, 1], 5, 6, 7, 8)]);
        Assert.Equal(new[] { 2, 1, 1 }, result.Shape);
        Assert.Equal(new[] { 17f, 53f }, result.Floats);
    }

    [Fact]
    public void EinsumRejectsAnEllipsis() =>
        Assert.Throws<NotSupportedException>(() =>
            EinsumKernel.Apply("...i->...", [Floats([2], 1, 2)]));

    [Fact]
    public void EinsumRejectsAnOperandCountMismatch() =>
        Assert.Throws<InvalidDataException>(() =>
            EinsumKernel.Apply("ij,jk->ik", [Floats([2, 2], 1, 2, 3, 4)]));
}
