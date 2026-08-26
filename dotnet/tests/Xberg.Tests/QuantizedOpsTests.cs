using Xberg.Internal.Onnx;
using Xberg.Internal.Onnx.Ops;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Direct tests for the dynamically-quantized operator set.
/// </summary>
/// <remarks>
/// These are checked against the ONNX specification's own arithmetic on hand-computed cases.
/// The per-layer parity run against ONNX Runtime covers them on real data; this covers the
/// contract, including the cases TATR's graph happens not to exercise — a per-column zero point,
/// an exclusive or reversed scan, a grouped integer convolution.
/// </remarks>
public class QuantizedOpsTests
{
    private static Tensor Floats(int[] shape, params float[] values) => Tensor.FromFloats(values, shape);

    private static Tensor Bytes(int[] shape, params long[] values) =>
        Tensor.FromLongs(values, ElementType.UInt8, shape);

    // ------------------------------------------------------------------ DynamicQuantizeLinear

    /// <summary>
    /// The range always includes zero, so an all-positive tensor still quantizes zero exactly.
    /// </summary>
    /// <remarks>
    /// This is what lets padding and masking survive quantization: a padded position carries the
    /// zero point, and subtracting it gives exactly zero.
    /// </remarks>
    [Fact]
    public void DynamicQuantizeLinearRangeIncludesZero()
    {
        var (quantized, scale, zeroPoint) = Quantized.DynamicQuantizeLinear(Floats([3], 2.0f, 4.0f, 6.0f));

        // min is clamped to 0, so scale = (6 - 0) / 255 and the zero point is 0.
        Assert.Equal(0.02352941222f, scale.Floats[0], 9);
        Assert.Equal(0, zeroPoint.Longs[0]);
        Assert.Equal(new long[] { 85, 170, 255 }, quantized.Longs);
    }

    /// <summary>
    /// A symmetric range, checked against the values ONNX Runtime itself produces.
    /// </summary>
    /// <remarks>
    /// Worth spelling out because the arithmetic is not the exact-real one. Mathematically the
    /// zero point here is 127.5 and would round to 128, but <c>2/255</c> is not representable in
    /// float32 and its reciprocal lands just under 127.5, so the reference rounds down to 127 —
    /// and the extremes come out as 0 and 254 rather than 0 and 255. These are ONNX Runtime's own
    /// outputs, not a hand derivation.
    /// </remarks>
    [Fact]
    public void DynamicQuantizeLinearSpansNegativeAndPositive()
    {
        var (quantized, scale, zeroPoint) = Quantized.DynamicQuantizeLinear(Floats([2], -1.0f, 1.0f));

        Assert.Equal(0.007843137719f, scale.Floats[0], 9);
        Assert.Equal(127, zeroPoint.Longs[0]);
        Assert.Equal(new long[] { 0, 254 }, quantized.Longs);
    }

    /// <summary>An asymmetric range, again against ONNX Runtime's own output.</summary>
    [Fact]
    public void DynamicQuantizeLinearAsymmetricRange()
    {
        var (quantized, scale, zeroPoint) =
            Quantized.DynamicQuantizeLinear(Floats([4], -5.0f, 0.0f, 2.5f, 5.0f));

        Assert.Equal(0.03921568766f, scale.Floats[0], 9);
        Assert.Equal(127, zeroPoint.Longs[0]);
        Assert.Equal(new long[] { 0, 127, 191, 254 }, quantized.Longs);
    }

    [Fact]
    public void DynamicQuantizeLinearOutputIsUnsignedBytes()
    {
        var (quantized, _, zeroPoint) = Quantized.DynamicQuantizeLinear(Floats([4], -5.0f, 0.0f, 2.5f, 5.0f));
        Assert.Equal(ElementType.UInt8, quantized.Type);
        Assert.Equal(ElementType.UInt8, zeroPoint.Type);
        Assert.All(quantized.Longs, v => Assert.InRange(v, 0, 255));
    }

    /// <summary>A constant-zero tensor has no range, which must not produce a zero scale.</summary>
    [Fact]
    public void DynamicQuantizeLinearHandlesAConstantTensor()
    {
        var (quantized, scale, _) = Quantized.DynamicQuantizeLinear(Floats([3], 0.0f, 0.0f, 0.0f));
        Assert.Equal(1.0f, scale.Floats[0]);
        Assert.All(quantized.Longs, v => Assert.Equal(0, v));
    }

    // ------------------------------------------------------------------ MatMulInteger

    [Fact]
    public void MatMulIntegerWithoutZeroPoints()
    {
        // [[1,2],[3,4]] x [[5,6],[7,8]] = [[19,22],[43,50]]
        var result = Quantized.MatMulInteger(
            Bytes([2, 2], 1, 2, 3, 4), Bytes([2, 2], 5, 6, 7, 8), null, null);
        Assert.Equal(ElementType.Int32, result.Type);
        Assert.Equal(new long[] { 19, 22, 43, 50 }, result.Longs);
    }

    [Fact]
    public void MatMulIntegerSubtractsZeroPoints()
    {
        // Both operands shifted by their zero point give (1-1)*(5-5) etc.
        var result = Quantized.MatMulInteger(
            Bytes([1, 2], 3, 5), Bytes([2, 1], 7, 9),
            Bytes([1], 1), Bytes([1], 2));
        // (3-1)*(7-2) + (5-1)*(9-2) = 10 + 28 = 38
        Assert.Equal(new long[] { 38 }, result.Longs);
    }

    /// <summary>
    /// A per-column zero point for the right operand is indexed by output column, which is what
    /// quantized transformer weights use.
    /// </summary>
    [Fact]
    public void MatMulIntegerSupportsPerColumnZeroPoints()
    {
        var result = Quantized.MatMulInteger(
            Bytes([1, 2], 2, 3), Bytes([2, 2], 4, 5, 6, 7),
            null, Bytes([2], 1, 2));
        // column 0: 2*(4-1) + 3*(6-1) = 6 + 15 = 21
        // column 1: 2*(5-2) + 3*(7-2) = 6 + 15 = 21
        Assert.Equal(new long[] { 21, 21 }, result.Longs);
    }

    [Fact]
    public void MatMulIntegerBroadcastsBatchDimensions()
    {
        var result = Quantized.MatMulInteger(
            Bytes([2, 1, 2], 1, 1, 2, 2), Bytes([1, 2, 1], 3, 4), null, null);
        Assert.Equal(new[] { 2, 1, 1 }, result.Shape);
        Assert.Equal(new long[] { 7, 14 }, result.Longs);
    }

    /// <summary>
    /// Accumulation is exact past float's 24-bit mantissa, which the reduction lengths in a
    /// quantized transformer reach.
    /// </summary>
    [Fact]
    public void MatMulIntegerAccumulatesExactlyBeyondFloatPrecision()
    {
        const int k = 4096;
        var a = Bytes([1, k], Enumerable.Repeat(255L, k).ToArray());
        var b = Bytes([k, 1], Enumerable.Repeat(255L, k).ToArray());
        var result = Quantized.MatMulInteger(a, b, null, null);
        Assert.Equal(255L * 255L * k, result.Longs[0]);
    }

    [Fact]
    public void MatMulIntegerRejectsMismatchedInnerDimensions() =>
        Assert.Throws<InvalidDataException>(() =>
            Quantized.MatMulInteger(Bytes([1, 2], 1, 2), Bytes([3, 1], 1, 2, 3), null, null));

    // ------------------------------------------------------------------ ConvInteger

    [Fact]
    public void ConvIntegerSimple2x2Kernel()
    {
        // A 3x3 input convolved with a 2x2 kernel of ones gives sums of 2x2 windows.
        var x = Bytes([1, 1, 3, 3], 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var w = Bytes([1, 1, 2, 2], 1, 1, 1, 1);
        var result = Quantized.ConvInteger(x, w, null, null, null, null, null, 1, "NOTSET");
        Assert.Equal(new[] { 1, 1, 2, 2 }, result.Shape);
        Assert.Equal(new long[] { 12, 16, 24, 28 }, result.Longs);
    }

    /// <summary>
    /// A padded position contributes zero because its value <em>is</em> the input zero point.
    /// Treating a pad as a literal zero byte would inject the negated zero point at every border.
    /// </summary>
    [Fact]
    public void ConvIntegerPaddingContributesZeroAtTheZeroPoint()
    {
        var x = Bytes([1, 1, 2, 2], 10, 10, 10, 10);
        var w = Bytes([1, 1, 2, 2], 1, 1, 1, 1);
        var result = Quantized.ConvInteger(
            x, w, Bytes([1], 10), null, null, new long[] { 1, 1, 1, 1 }, null, 1, "NOTSET");
        // Every input equals the zero point, so every window sums to zero regardless of padding.
        Assert.Equal(new[] { 1, 1, 3, 3 }, result.Shape);
        Assert.All(result.Longs, v => Assert.Equal(0, v));
    }

    [Fact]
    public void ConvIntegerSubtractsBothZeroPoints()
    {
        var x = Bytes([1, 1, 1, 2], 5, 7);
        var w = Bytes([1, 1, 1, 2], 3, 4);
        var result = Quantized.ConvInteger(x, w, Bytes([1], 1), Bytes([1], 2), null, null, null, 1, "NOTSET");
        // (5-1)*(3-2) + (7-1)*(4-2) = 4 + 12 = 16
        Assert.Equal(new long[] { 16 }, result.Longs);
    }

    [Fact]
    public void ConvIntegerHonoursStride()
    {
        var x = Bytes([1, 1, 1, 4], 1, 2, 3, 4);
        var w = Bytes([1, 1, 1, 2], 1, 1);
        var result = Quantized.ConvInteger(
            x, w, null, null, new long[] { 1, 2 }, null, null, 1, "NOTSET");
        Assert.Equal(new[] { 1, 1, 1, 2 }, result.Shape);
        Assert.Equal(new long[] { 3, 7 }, result.Longs);
    }

    /// <summary>Each group sees only its own slice of the input channels.</summary>
    [Fact]
    public void ConvIntegerHonoursGroups()
    {
        var x = Bytes([1, 2, 1, 1], 3, 5);
        var w = Bytes([2, 1, 1, 1], 2, 4);
        var result = Quantized.ConvInteger(x, w, null, null, null, null, null, 2, "NOTSET");
        Assert.Equal(new long[] { 6, 20 }, result.Longs);
    }

    [Fact]
    public void ConvIntegerRejectsNon2dInput() =>
        Assert.Throws<NotSupportedException>(() =>
            Quantized.ConvInteger(Bytes([1, 1, 1], 1), Bytes([1, 1, 1], 1),
                null, null, null, null, null, 1, "NOTSET"));

    // ------------------------------------------------------------------ CumSum

    [Fact]
    public void CumSumInclusiveForward()
    {
        var result = Reductions.CumSum(Floats([4], 1, 2, 3, 4), 0, exclusive: false, reverse: false);
        Assert.Equal(new[] { 1f, 3f, 6f, 10f }, result.Floats);
    }

    [Fact]
    public void CumSumExclusiveShiftsByOne()
    {
        var result = Reductions.CumSum(Floats([4], 1, 2, 3, 4), 0, exclusive: true, reverse: false);
        Assert.Equal(new[] { 0f, 1f, 3f, 6f }, result.Floats);
    }

    [Fact]
    public void CumSumReverseScansFromTheEnd()
    {
        var result = Reductions.CumSum(Floats([4], 1, 2, 3, 4), 0, exclusive: false, reverse: true);
        Assert.Equal(new[] { 10f, 9f, 7f, 4f }, result.Floats);
    }

    [Fact]
    public void CumSumRunsAlongTheNamedAxis()
    {
        var result = Reductions.CumSum(Floats([2, 3], 1, 2, 3, 4, 5, 6), 1, exclusive: false, reverse: false);
        Assert.Equal(new[] { 1f, 3f, 6f, 4f, 9f, 15f }, result.Floats);

        var down = Reductions.CumSum(Floats([2, 3], 1, 2, 3, 4, 5, 6), 0, exclusive: false, reverse: false);
        Assert.Equal(new[] { 1f, 2f, 3f, 5f, 7f, 9f }, down.Floats);
    }

    [Fact]
    public void CumSumAcceptsANegativeAxis()
    {
        var result = Reductions.CumSum(Floats([2, 2], 1, 2, 3, 4), -1, exclusive: false, reverse: false);
        Assert.Equal(new[] { 1f, 3f, 3f, 7f }, result.Floats);
    }

    [Fact]
    public void CumSumRejectsAnAxisOutOfRange() =>
        Assert.Throws<InvalidDataException>(() =>
            Reductions.CumSum(Floats([2], 1, 2), 5, exclusive: false, reverse: false));
}
