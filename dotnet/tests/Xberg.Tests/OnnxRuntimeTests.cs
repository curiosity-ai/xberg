using Xberg.Internal.Onnx;
using Xberg.Internal.Onnx.Ops;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Unit tests for the in-process ONNX runtime.
/// <para>
/// Full-model parity against ONNX Runtime lives in <c>tools/Xberg.OnnxParity</c>, which
/// needs a 169 MB downloaded model and so cannot run here. These tests cover the pieces that
/// can be checked without one: kernels against naive reference implementations, the two
/// numerical traps that actually broke during the port, and an end-to-end run of a graph
/// assembled by hand.
/// </para>
/// </summary>
public class OnnxRuntimeTests
{
    // ---- tensor and broadcasting -------------------------------------------------------

    [Fact]
    public void Tensor_ReshapeSharesStorageAndPreservesOrder()
    {
        var tensor = Tensor.FromFloats([1, 2, 3, 4, 5, 6], 2, 3);
        var reshaped = tensor.Reshaped(3, 2);

        Assert.Equal([3, 2], reshaped.Shape);
        Assert.Equal(6, reshaped.Count);
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, reshaped.Floats);
    }

    [Fact]
    public void Tensor_StridesAreRowMajor()
    {
        Assert.Equal(new[] { 12, 4, 1 }, Tensor.AllocateFloat(2, 3, 4).Strides());
    }

    [Theory]
    [InlineData(new[] { 3, 1, 5 }, new[] { 4, 5 }, new[] { 3, 4, 5 })]
    [InlineData(new[] { 1 }, new[] { 7, 2 }, new[] { 7, 2 })]
    [InlineData(new[] { 2, 3 }, new[] { 2, 3 }, new[] { 2, 3 })]
    public void Broadcast_ResultShapeFollowsNumpyRules(int[] a, int[] b, int[] expected)
    {
        Assert.Equal(expected, Broadcast.ResultShape(a, b));
    }

    [Fact]
    public void Broadcast_IncompatibleShapesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Broadcast.ResultShape([3, 4], [3, 5]));
    }

    [Fact]
    public void Binary_BroadcastsChannelBiasAcrossSpatialPlanes()
    {
        // The archetypal broadcast in these graphs: [1,C,1,1] added to [1,C,H,W].
        var x = Tensor.FromFloats([1, 2, 3, 4, 5, 6, 7, 8], 1, 2, 2, 2);
        var bias = Tensor.FromFloats([10, 20], 1, 2, 1, 1);

        var result = Elementwise.Binary(x, bias, BinaryKind.Add);

        Assert.Equal([1, 2, 2, 2], result.Shape);
        Assert.Equal(new float[] { 11, 12, 13, 14, 25, 26, 27, 28 }, result.Floats);
    }

    [Fact]
    public void Binary_BroadcastMatchesElementwiseReferenceOnAwkwardShapes()
    {
        var a = MakeRamp(3 * 1 * 5, 3, 1, 5);
        var b = MakeRamp(1 * 4 * 5, 1, 4, 5);

        var result = Elementwise.Binary(a, b, BinaryKind.Mul);

        Assert.Equal([3, 4, 5], result.Shape);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 5; k++)
                    Assert.Equal(a.Floats[i * 5 + k] * b.Floats[j * 5 + k],
                        result.Floats[(i * 4 + j) * 5 + k]);
    }

    [Fact]
    public void Binary_BroadcastsAPerRowScalarAcrossTheInnermostAxis()
    {
        // [N,D] against [N,1]. Neither operand strides in lockstep with the output on the
        // innermost axis, so the plan has to recognise that one side simply holds still —
        // otherwise the block collapses to a single element and the whole tensor is walked
        // one element at a time, which cost 11.5 ms on a single node of the decoder.
        var x = Tensor.FromFloats([1, 2, 3, 4, 5, 6], 2, 3);
        var scale = Tensor.FromFloats([10, 100], 2, 1);

        var result = Elementwise.Binary(x, scale, BinaryKind.Mul);

        Assert.Equal([2, 3], result.Shape);
        Assert.Equal(new float[] { 10, 20, 30, 400, 500, 600 }, result.Floats);
    }

    [Fact]
    public void Binary_BroadcastsAPerRowScalarOnTheLeftToo()
    {
        var scale = Tensor.FromFloats([10, 100], 2, 1);
        var x = Tensor.FromFloats([1, 2, 3, 4, 5, 6], 2, 3);

        var result = Elementwise.Binary(scale, x, BinaryKind.Sub);

        Assert.Equal([2, 3], result.Shape);
        Assert.Equal(new float[] { 9, 8, 7, 96, 95, 94 }, result.Floats);
    }

    [Fact]
    public void Binary_HeldOperandSpansSeveralTrailingAxes()
    {
        // The run extends outward through every axis the held side is constant over, so the
        // block here is the whole 3x4 tail rather than just the innermost axis.
        var x = MakeRamp(2 * 3 * 4, 2, 3, 4);
        var scale = Tensor.FromFloats([2, 3], 2, 1, 1);

        var result = Elementwise.Binary(x, scale, BinaryKind.Mul);

        Assert.Equal([2, 3, 4], result.Shape);
        for (int n = 0; n < 2; n++)
            for (int i = 0; i < 12; i++)
                Assert.Equal(x.Floats[n * 12 + i] * (n == 0 ? 2 : 3), result.Floats[n * 12 + i]);
    }

    [Fact]
    public void Expand_RepeatsAlongTheInnermostAxis()
    {
        var x = Tensor.FromFloats([7, 8], 2, 1);
        var shape = Tensor.FromLongs([2, 3], ElementType.Int64, 2);

        var result = Shapes.Expand(x, shape);

        Assert.Equal([2, 3], result.Shape);
        Assert.Equal(new float[] { 7, 7, 7, 8, 8, 8 }, result.Floats);
    }

    [Theory]
    // kernel, stride, pad, dilation — covering the padded stride-2 window the backbone uses,
    // plus configurations that clip a tap's column range at one edge, both edges, or entirely.
    [InlineData(3, 2, 1, 1)]
    [InlineData(3, 1, 0, 1)]
    [InlineData(2, 2, 0, 1)]
    [InlineData(3, 2, 0, 1)]
    [InlineData(3, 1, 1, 1)]
    [InlineData(2, 3, 1, 1)]
    [InlineData(3, 1, 2, 2)]
    [InlineData(1, 1, 0, 1)]
    public void MaxPool_MatchesTheWindowDefinitionUnderPaddingAndDilation(int k, int stride, int pad, int dilation)
    {
        const int H = 9, W = 11;
        var x = MakeRamp(H * W, 1, 1, H, W);
        // Break monotonicity, or a maximum could be right for the wrong reason.
        for (int i = 0; i < H * W; i++) x.Floats[i] = (i * 37 % 91) - 45f;

        var result = Pooling.MaxPool(x, [k, k], [stride, stride], [pad, pad, pad, pad], [dilation, dilation], "", ceilMode: false);

        int effK = (k - 1) * dilation + 1;
        int outH = (H + 2 * pad - effK) / stride + 1;
        int outW = (W + 2 * pad - effK) / stride + 1;
        Assert.Equal([1, 1, outH, outW], result.Shape);

        for (int oy = 0; oy < outH; oy++)
        {
            for (int ox = 0; ox < outW; ox++)
            {
                float expected = float.NegativeInfinity;
                for (int ky = 0; ky < k; ky++)
                {
                    for (int kx = 0; kx < k; kx++)
                    {
                        int iy = oy * stride - pad + ky * dilation;
                        int ix = ox * stride - pad + kx * dilation;
                        if ((uint)iy < (uint)H && (uint)ix < (uint)W)
                            expected = MathF.Max(expected, x.Floats[iy * W + ix]);
                    }
                }
                Assert.Equal(expected, result.Floats[oy * outW + ox]);
            }
        }
    }

    [Fact]
    public void Binary_IntegerOperandsStayIntegral()
    {
        // Shape arithmetic must not round-trip through float, where large dimensions lose
        // exactness. 2^53 + 1 is the smallest integer a double cannot represent.
        var a = Tensor.FromLongs([(1L << 53) + 1], ElementType.Int64, 1);
        var b = Tensor.FromLongs([1], ElementType.Int64, 1);

        var result = Elementwise.Binary(a, b, BinaryKind.Add);

        Assert.False(result.IsFloat);
        Assert.Equal((1L << 53) + 2, result.Longs[0]);
    }

    // ---- the two bugs the parity harness caught ---------------------------------------

    [Fact]
    public void Softmax_SurvivesLargeNegativeLogitsWithoutProducingNaN()
    {
        // RT-DETR's cross-attention produces rows around -164. Exponentiating those directly
        // underflows every term to zero and the normalisation becomes 0/0.
        var logits = Tensor.FromFloats([-164f, -165f, -166f, -164.5f], 1, 4);

        var result = Reductions.Softmax(logits, -1);

        Assert.All(result.Floats, v => Assert.False(float.IsNaN(v), "softmax produced NaN"));
        Assert.Equal(1f, result.Floats.Sum(), 5);
        // The largest logit must still win.
        Assert.Equal(0, Array.IndexOf(result.Floats, result.Floats.Max()));
    }

    [Fact]
    public void Softmax_MatchesTheDirectDefinitionForModerateValues()
    {
        var logits = Tensor.FromFloats([1f, 2f, 3f], 3);
        var result = Reductions.Softmax(logits, -1);

        double denominator = Math.Exp(1) + Math.Exp(2) + Math.Exp(3);
        Assert.Equal(Math.Exp(1) / denominator, result.Floats[0], 5);
        Assert.Equal(Math.Exp(2) / denominator, result.Floats[1], 5);
        Assert.Equal(Math.Exp(3) / denominator, result.Floats[2], 5);
    }

    [Fact]
    public void Slice_AcceptsInt64MaxAsAnOpenEndedUpperBound()
    {
        // Exporters spell "to the end of this axis" as INT64_MAX; narrowing it overflows.
        var x = MakeRamp(6, 2, 3);
        var starts = Tensor.FromLongs([1], ElementType.Int64, 1);
        var ends = Tensor.FromLongs([long.MaxValue], ElementType.Int64, 1);
        var axes = Tensor.FromLongs([1], ElementType.Int64, 1);

        var result = Shapes.Slice(x, starts, ends, axes, null);

        Assert.Equal([2, 2], result.Shape);
        Assert.Equal(new float[] { 1, 2, 4, 5 }, result.Floats);
    }

    [Fact]
    public void Slice_HandlesNegativeIndicesAndReverseSteps()
    {
        var x = MakeRamp(5, 5);
        var result = Shapes.Slice(
            x,
            Tensor.FromLongs([-1], ElementType.Int64, 1),
            Tensor.FromLongs([long.MinValue], ElementType.Int64, 1),
            Tensor.FromLongs([0], ElementType.Int64, 1),
            Tensor.FromLongs([-1], ElementType.Int64, 1));

        Assert.Equal(new float[] { 4, 3, 2, 1, 0 }, result.Floats);
    }

    // ---- structural kernels ------------------------------------------------------------

    [Fact]
    public void Transpose_MatchesIndexwiseReference()
    {
        var x = MakeRamp(2 * 3 * 4, 2, 3, 4);
        var result = Shapes.Transpose(x, [1, 0, 2]);

        Assert.Equal([3, 2, 4], result.Shape);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 4; k++)
                    Assert.Equal(x.Floats[(i * 3 + j) * 4 + k], result.Floats[(j * 2 + i) * 4 + k]);
    }

    [Fact]
    public void Gather_SplicesTheIndexShapeIntoTheGatheredAxis()
    {
        var x = MakeRamp(12, 3, 4);
        var indices = Tensor.FromLongs([2, 0], ElementType.Int64, 2);

        var result = Shapes.Gather(x, indices, 0);

        Assert.Equal([2, 4], result.Shape);
        Assert.Equal(new float[] { 8, 9, 10, 11, 0, 1, 2, 3 }, result.Floats);
    }

    [Fact]
    public void Gather_SupportsNegativeIndices()
    {
        var x = MakeRamp(4, 4);
        var result = Shapes.Gather(x, Tensor.FromLongs([-1], ElementType.Int64, 1), 0);
        Assert.Equal(new float[] { 3 }, result.Floats);
    }

    [Fact]
    public void GatherElements_PicksPerPositionAlongTheAxis()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 2, 2);
        var indices = Tensor.FromLongs([0, 0, 1, 0], ElementType.Int64, 2, 2);

        var result = Shapes.GatherElements(x, indices, 1);

        Assert.Equal(new float[] { 1, 1, 4, 3 }, result.Floats);
    }

    [Fact]
    public void Concat_JoinsAlongAnInteriorAxis()
    {
        var a = Tensor.FromFloats([1, 2, 3, 4], 2, 2);
        var b = Tensor.FromFloats([5, 6], 2, 1);

        var result = Shapes.Concat([a, b], 1);

        Assert.Equal([2, 3], result.Shape);
        Assert.Equal(new float[] { 1, 2, 5, 3, 4, 6 }, result.Floats);
    }

    [Fact]
    public void Reshape_ResolvesTheInferredAndCopiedDimensions()
    {
        var x = MakeRamp(12, 2, 6);

        var inferred = Shapes.Reshape(x, Tensor.FromLongs([3, -1], ElementType.Int64, 2), allowZero: false);
        Assert.Equal([3, 4], inferred.Shape);

        var copied = Shapes.Reshape(x, Tensor.FromLongs([0, 3, 2], ElementType.Int64, 3), allowZero: false);
        Assert.Equal([2, 3, 2], copied.Shape);
    }

    [Fact]
    public void Split_DividesUnevenlyWithTheRemainderInTheLastChunk()
    {
        var x = MakeRamp(5, 5);
        var parts = Shapes.Split(x, 0, null, 2);

        Assert.Equal(2, parts.Length);
        Assert.Equal(new float[] { 0, 1, 2 }, parts[0].Floats);
        Assert.Equal(new float[] { 3, 4 }, parts[1].Floats);
    }

    [Fact]
    public void Tile_RepeatsAlongEveryAxis()
    {
        var x = Tensor.FromFloats([1, 2], 1, 2);
        var result = Shapes.Tile(x, Tensor.FromLongs([2, 2], ElementType.Int64, 2));

        Assert.Equal([2, 4], result.Shape);
        Assert.Equal(new float[] { 1, 2, 1, 2, 1, 2, 1, 2 }, result.Floats);
    }

    // ---- arithmetic kernels ------------------------------------------------------------

    [Fact]
    public void MatMul_MatchesTheTextbookTripleLoop()
    {
        var a = MakeRamp(6, 2, 3);
        var b = MakeRamp(12, 3, 4);

        var result = Linear.MatMul(a, b);

        Assert.Equal([2, 4], result.Shape);
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                float expected = 0;
                for (int k = 0; k < 3; k++) expected += a.Floats[i * 3 + k] * b.Floats[k * 4 + j];
                Assert.Equal(expected, result.Floats[i * 4 + j], 4);
            }
        }
    }

    [Fact]
    public void MatMul_BroadcastsLeadingBatchDimensions()
    {
        var a = MakeRamp(2 * 2 * 3, 2, 2, 3);
        var b = MakeRamp(3 * 2, 1, 3, 2);

        var result = Linear.MatMul(a, b);

        Assert.Equal([2, 2, 2], result.Shape);
    }

    [Fact]
    public void MatMul_PromotesAndDemotesOneDimensionalOperands()
    {
        var vector = Tensor.FromFloats([1, 2, 3], 3);
        var matrix = MakeRamp(12, 3, 4);

        var result = Linear.MatMul(vector, matrix);

        Assert.Equal([4], result.Shape);
    }

    [Fact]
    public void Gemm_AppliesAlphaBetaAndTheTransposeFlags()
    {
        var a = Tensor.FromFloats([1, 2, 3, 4], 2, 2);
        var b = Tensor.FromFloats([1, 0, 0, 1], 2, 2);   // identity
        var c = Tensor.FromFloats([1, 1, 1, 1], 2, 2);

        var result = Linear.Gemm(a, b, c, alpha: 2f, beta: 3f, transA: false, transB: false);

        Assert.Equal(new float[] { 5, 7, 9, 11 }, result.Floats);
    }

    [Fact]
    public void Reduce_MeanAndSumOverTheTrailingAxis()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4, 5, 6], 2, 3);

        var mean = Reductions.Reduce(x, [1], keepDims: true, noopWithEmptyAxes: false, ReduceKind.Mean);
        Assert.Equal([2, 1], mean.Shape);
        Assert.Equal(new float[] { 2, 5 }, mean.Floats);

        var sum = Reductions.Reduce(x, [1], keepDims: false, noopWithEmptyAxes: false, ReduceKind.Sum);
        Assert.Equal([2], sum.Shape);
        Assert.Equal(new float[] { 6, 15 }, sum.Floats);
    }

    [Fact]
    public void Reduce_OverAnInteriorAxisUsesTheStridedPath()
    {
        var x = MakeRamp(2 * 3 * 2, 2, 3, 2);
        var sum = Reductions.Reduce(x, [1], keepDims: false, noopWithEmptyAxes: false, ReduceKind.Sum);

        Assert.Equal([2, 2], sum.Shape);
        // Column sums of each 3x2 block: (0+2+4, 1+3+5) and (6+8+10, 7+9+11).
        Assert.Equal(new float[] { 6, 9, 24, 27 }, sum.Floats);
    }

    [Fact]
    public void TopK_BreaksTiesTowardTheLowerIndex()
    {
        var x = Tensor.FromFloats([5, 5, 1], 1, 3);
        var (values, indices) = Reductions.TopK(x, 2, -1, largest: true, sorted: true);

        Assert.Equal(new float[] { 5, 5 }, values.Floats);
        Assert.Equal(new long[] { 0, 1 }, indices.Longs);
    }

    // ---- convolution paths -------------------------------------------------------------

    [Fact]
    public void Conv_PointwisePathMatchesTheGeneralDefinition()
    {
        var x = MakeRamp(1 * 2 * 2 * 2, 1, 2, 2, 2);
        var w = Tensor.FromFloats([1, 2, 3, 4], 2, 2, 1, 1);

        var result = Convolution.Conv(x, w, null, null, null, null, 1, "NOTSET");

        Assert.Equal([1, 2, 2, 2], result.Shape);
        // Output channel o, pixel p = sum over input channels c of w[o,c] * x[c,p].
        for (int o = 0; o < 2; o++)
            for (int p = 0; p < 4; p++)
                Assert.Equal(
                    w.Floats[o * 2] * x.Floats[p] + w.Floats[o * 2 + 1] * x.Floats[4 + p],
                    result.Floats[o * 4 + p], 4);
    }

    [Fact]
    public void Conv_DepthwisePathKeepsChannelsIndependent()
    {
        var x = Tensor.FromFloats([1, 1, 1, 1, 2, 2, 2, 2], 1, 2, 2, 2);
        // One 2x2 kernel of ones per channel, second channel scaled by 10.
        var w = Tensor.FromFloats([1, 1, 1, 1, 10, 10, 10, 10], 2, 1, 2, 2);

        var result = Convolution.Conv(x, w, null, null, null, null, group: 2, "NOTSET");

        Assert.Equal([1, 2, 1, 1], result.Shape);
        Assert.Equal(new float[] { 4, 80 }, result.Floats);
    }

    [Fact]
    public void Conv_GeneralPathAppliesPaddingStrideAndBias()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 1, 1, 2, 2);
        var w = Tensor.FromFloats([1, 1, 1, 1], 1, 1, 2, 2);
        var bias = Tensor.FromFloats([0.5f], 1);

        // SAME_UPPER keeps the output at 2x2; the bottom-right window sees only one real cell.
        var result = Convolution.Conv(x, w, bias, [1, 1], null, null, 1, "SAME_UPPER");

        Assert.Equal([1, 1, 2, 2], result.Shape);
        Assert.Equal(new float[] { 1 + 2 + 3 + 4 + 0.5f, 2 + 4 + 0.5f, 3 + 4 + 0.5f, 4 + 0.5f }, result.Floats);
    }

    [Fact]
    public void Conv_DilationSkipsInputCells()
    {
        var x = MakeRamp(9, 1, 1, 3, 3);
        var w = Tensor.FromFloats([1, 1, 1, 1], 1, 1, 2, 2);

        // Dilation 2 makes the 2x2 kernel span the whole 3x3 input corners: 0 + 2 + 6 + 8.
        var result = Convolution.Conv(x, w, null, null, null, [2, 2], 1, "NOTSET");

        Assert.Equal([1, 1, 1, 1], result.Shape);
        Assert.Equal(16f, result.Floats[0]);
    }

    [Fact]
    public void BatchNormalization_ReducesToTheAffineFoldOfItsParameters()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 1, 1, 2, 2);
        var scale = Tensor.FromFloats([2], 1);
        var bias = Tensor.FromFloats([1], 1);
        var mean = Tensor.FromFloats([2], 1);
        var variance = Tensor.FromFloats([4], 1);

        var result = Convolution.BatchNormalization(x, scale, bias, mean, variance, 0f);

        // (x - 2) / 2 * 2 + 1
        Assert.Equal(new float[] { 0, 1, 2, 3 }, result.Floats);
    }

    [Fact]
    public void AveragePool_ExcludesPaddingFromTheDivisorByDefault()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 1, 1, 2, 2);

        var excluded = Pooling.AveragePool(x, [2, 2], [2, 2], [0, 0, 1, 1], "NOTSET", false, countIncludePad: false);
        var included = Pooling.AveragePool(x, [2, 2], [2, 2], [0, 0, 1, 1], "NOTSET", false, countIncludePad: true);

        // The single window covers all four real cells plus padding either way.
        Assert.Equal(2.5f, excluded.Floats[0]);
        Assert.Equal(2.5f, included.Floats[0]);

        // With padding on both sides the divisor differs: 4 real cells vs a 3x3 window.
        var offset = Pooling.AveragePool(x, [3, 3], [3, 3], [1, 1, 1, 1], "NOTSET", false, countIncludePad: false);
        var offsetIncluded = Pooling.AveragePool(x, [3, 3], [3, 3], [1, 1, 1, 1], "NOTSET", false, countIncludePad: true);
        Assert.Equal(10f / 4f, offset.Floats[0]);
        Assert.Equal(10f / 9f, offsetIncluded.Floats[0], 5);
    }

    [Fact]
    public void GlobalAveragePool_CollapsesEverySpatialAxis()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4, 10, 20, 30, 40], 1, 2, 2, 2);
        var result = Pooling.GlobalAveragePool(x);

        Assert.Equal([1, 2, 1, 1], result.Shape);
        Assert.Equal(new float[] { 2.5f, 25f }, result.Floats);
    }

    // ---- resampling --------------------------------------------------------------------

    [Fact]
    public void Resize_NearestWithAsymmetricFloorDuplicatesSourcePixels()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 1, 1, 2, 2);
        var scales = Tensor.FromFloats([1, 1, 2, 2], 4);

        var result = Sampling.Resize(x, scales, null, "nearest", "asymmetric", "floor");

        Assert.Equal([1, 1, 4, 4], result.Shape);
        Assert.Equal(new float[] { 1, 1, 2, 2, 1, 1, 2, 2, 3, 3, 4, 4, 3, 3, 4, 4 }, result.Floats);
    }

    [Fact]
    public void GridSample_ReadsPixelCentresUnderTheHalfPixelConvention()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 1, 1, 2, 2);
        // With align_corners=0 the centre of the image is (0,0) in normalised space; the four
        // corner-ish coordinates below name the four pixel centres exactly.
        var grid = Tensor.FromFloats([-0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f], 1, 2, 2, 2);

        var result = Sampling.GridSample(x, grid, "bilinear", "zeros", alignCorners: false);

        Assert.Equal([1, 1, 2, 2], result.Shape);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, result.Floats);
    }

    [Fact]
    public void GridSample_ReadsZeroOutsideTheImageUnderZeroPadding()
    {
        var x = Tensor.FromFloats([1, 2, 3, 4], 1, 1, 2, 2);
        var grid = Tensor.FromFloats([-5f, -5f], 1, 1, 1, 2);

        var result = Sampling.GridSample(x, grid, "bilinear", "zeros", alignCorners: false);

        Assert.Equal(0f, result.Floats[0]);
    }

    // ---- numerics ----------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 0.8427007929497149)]
    [InlineData(-1.0, -0.8427007929497149)]
    [InlineData(2.5, 0.999593047982555)]
    public void Erf_AgreesWithTheKnownValuesToFloatPrecision(double input, double expected)
    {
        var result = Elementwise.Erf(Tensor.Scalar((float)input));
        Assert.Equal(expected, result.Floats[0], 6);
    }

    [Fact]
    public void Cast_TruncatesTowardZeroRatherThanRounding()
    {
        var x = Tensor.FromFloats([1.9f, -1.9f, 0.5f], 3);
        var result = Elementwise.Cast(x, ElementType.Int64);

        Assert.Equal(new long[] { 1, -1, 0 }, result.Longs);
    }

    [Fact]
    public void Clip_BoundsOnBothSides()
    {
        var x = Tensor.FromFloats([-5, 0, 5], 3);
        var result = Elementwise.Clip(x, -1f, 1f);
        Assert.Equal(new float[] { -1, 0, 1 }, result.Floats);
    }

    // ---- file formats and end-to-end ---------------------------------------------------

    [Fact]
    public void NpyFile_RoundTripsShapeAndValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xberg-npy-{Guid.NewGuid():N}.npy");
        try
        {
            var original = MakeRamp(6, 2, 3);
            NpyFile.Save(path, original);
            var loaded = NpyFile.Load(path);

            Assert.Equal(original.Shape, loaded.Shape);
            Assert.Equal(original.Floats, loaded.Floats);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NpyFile_RoundTripsIntegerTensors()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xberg-npy-{Guid.NewGuid():N}.npy");
        try
        {
            var original = Tensor.FromLongs([1, 2, 3, 4], ElementType.Int64, 4);
            NpyFile.Save(path, original);
            var loaded = NpyFile.Load(path);

            Assert.Equal(original.Shape, loaded.Shape);
            Assert.Equal(original.Longs, loaded.Longs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OnnxModel_ParsesAHandBuiltGraphAndRunsIt()
    {
        // A two-node graph: y = Relu(x * w + b), with w and b as initializers.
        var model = TinyOnnxModel.Build();
        var parsed = OnnxModel.Parse(model);

        Assert.Equal(3, parsed.Nodes.Length);
        Assert.Equal(["Mul", "Add", "Relu"], parsed.Nodes.Select(n => n.OpType));
        Assert.Equal(2, parsed.Initializers.Count);
        Assert.Equal(["x"], parsed.FeedInputs.Select(i => i.Name));

        var session = new OnnxSession(parsed);
        var outputs = session.Run(new Dictionary<string, Tensor>
        {
            ["x"] = Tensor.FromFloats([1, -2, 3, -4], 4),
        });

        // w = [2,2,2,2], b = [-1,-1,-1,-1]: relu([1,-5,5,-9]).
        Assert.Equal(new float[] { 1, 0, 5, 0 }, outputs["y"].Floats);
    }

    [Fact]
    public void OnnxSession_ReportsTheOffendingNodeWhenAKernelFails()
    {
        var parsed = OnnxModel.Parse(TinyOnnxModel.Build());
        var session = new OnnxSession(parsed);

        // A rank-2 input cannot broadcast against the rank-1 [4] initializer.
        var ex = Assert.Throws<InvalidOperationException>(() => session.Run(new Dictionary<string, Tensor>
        {
            ["x"] = Tensor.FromFloats([1, 2, 3, 4, 5, 6], 2, 3),
        }));

        Assert.Contains("Mul", ex.Message);
    }

    [Fact]
    public void OnnxSession_RejectsAMissingInput()
    {
        var session = new OnnxSession(OnnxModel.Parse(TinyOnnxModel.Build()));
        var ex = Assert.Throws<InvalidOperationException>(() => session.Run(new Dictionary<string, Tensor>()));
        Assert.Contains("'x'", ex.Message);
    }

    private static Tensor MakeRamp(int count, params int[] shape)
    {
        var data = new float[count];
        for (int i = 0; i < count; i++) data[i] = i;
        return Tensor.FromFloats(data, shape);
    }
}
