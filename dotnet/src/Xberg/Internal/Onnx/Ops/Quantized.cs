namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// The dynamically-quantized operator set: <c>DynamicQuantizeLinear</c>, <c>MatMulInteger</c> and
/// <c>ConvInteger</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are what an int8-quantized export runs on. The weights are stored as bytes with a scale
/// and a zero point; each activation is quantized at run time by <c>DynamicQuantizeLinear</c>, the
/// integer matmul or convolution accumulates into int32, and a later multiply by the combined
/// scale returns it to float.
/// </para>
/// <para>
/// Accumulation runs in <c>double</c> rather than <c>float</c>. Each term is at most 255x255, and
/// over a few thousand reduction steps the sum reaches a few times 1e8 — past float's exact-integer
/// range of 2^24, but far inside double's 2^53. Integer accumulation must be exact, since the whole
/// point of the quantized path is that it reproduces a fixed-point computation.
/// </para>
/// </remarks>
internal static class Quantized
{
    /// <summary>
    /// ONNX <c>DynamicQuantizeLinear</c>: quantize a float tensor to uint8, returning the tensor,
    /// its scale and its zero point.
    /// </summary>
    /// <remarks>
    /// The range is always widened to include zero, which is what guarantees the zero point is a
    /// representable value — padding and masking depend on exact zero surviving quantization. The
    /// zero point is rounded half-to-even, as the spec's <c>saturate(round(...))</c> requires.
    /// </remarks>
    public static (Tensor Quantized, Tensor Scale, Tensor ZeroPoint) DynamicQuantizeLinear(Tensor x)
    {
        var input = x.AsFloat();
        var data = input.Floats;

        float min = 0.0f, max = 0.0f;
        for (int i = 0; i < input.Count; i++)
        {
            float value = data[i];
            if (value < min) min = value;
            if (value > max) max = value;
        }

        const float qMin = 0.0f, qMax = 255.0f;
        float scale = (max - min) / (qMax - qMin);
        if (scale == 0.0f || !float.IsFinite(scale)) scale = 1.0f;

        float zeroPointFloat = qMin - min / scale;
        zeroPointFloat = MathF.Round(Math.Clamp(zeroPointFloat, qMin, qMax), MidpointRounding.ToEven);
        long zeroPoint = (long)zeroPointFloat;

        var quantized = Tensor.AllocateLong(ElementType.UInt8, input.Shape);
        for (int i = 0; i < input.Count; i++)
        {
            float scaled = MathF.Round(data[i] / scale, MidpointRounding.ToEven) + zeroPoint;
            quantized.Longs[i] = (long)Math.Clamp(scaled, qMin, qMax);
        }

        return (quantized,
                Tensor.FromFloats([scale]),
                Tensor.FromLongs([zeroPoint], ElementType.UInt8));
    }

    /// <summary>Zero point as a scalar, treating an absent or empty tensor as zero.</summary>
    private static long ZeroPointOf(Tensor? zeroPoint) =>
        zeroPoint is { Count: > 0 } ? zeroPoint.GetLong(0) : 0;

    /// <summary>
    /// ONNX <c>MatMulInteger</c>: integer matrix multiply with per-tensor zero points, producing
    /// int32.
    /// </summary>
    /// <remarks>
    /// Broadcasting follows <c>MatMul</c>: the last two dimensions multiply and everything ahead
    /// of them broadcasts. A per-column zero point for <c>b</c> is honoured — the spec allows one
    /// per output column, which quantized transformer weights do use.
    /// </remarks>
    public static Tensor MatMulInteger(Tensor a, Tensor b, Tensor? aZeroPoint, Tensor? bZeroPoint)
    {
        int rankA = a.Shape.Length, rankB = b.Shape.Length;
        if (rankA < 2 || rankB < 2)
            throw new NotSupportedException("MatMulInteger: operands below rank 2 are not supported");

        int m = a.Shape[rankA - 2], k = a.Shape[rankA - 1];
        int kb = b.Shape[rankB - 2], n = b.Shape[rankB - 1];
        if (k != kb)
            throw new InvalidDataException($"MatMulInteger: inner dimensions {k} and {kb} disagree");

        var batchA = a.Shape[..(rankA - 2)];
        var batchB = b.Shape[..(rankB - 2)];
        var batchShape = Broadcast.ResultShape(batchA, batchB);
        int batchRank = batchShape.Length;
        int batchCount = Tensor.ElementCount(batchShape.Length == 0 ? [1] : batchShape);

        var strideA = Broadcast.StridesFor(batchA, batchRank);
        var strideB = Broadcast.StridesFor(batchB, batchRank);

        var shape = batchShape.Concat([m, n]).ToArray();
        var result = Tensor.AllocateLong(ElementType.Int32, shape);

        long zeroA = ZeroPointOf(aZeroPoint);
        // A per-column zero point for b is indexed by output column; a scalar one is not.
        bool perColumnB = bZeroPoint is { Count: > 1 };
        long scalarZeroB = perColumnB ? 0 : ZeroPointOf(bZeroPoint);

        var index = new int[batchRank];
        int offsetA = 0, offsetB = 0;

        for (int batch = 0; batch < batchCount; batch++)
        {
            int baseA = offsetA * m * k;
            int baseB = offsetB * k * n;
            int baseOut = batch * m * n;

            for (int row = 0; row < m; row++)
            {
                for (int column = 0; column < n; column++)
                {
                    long zeroB = perColumnB ? bZeroPoint!.GetLong(column) : scalarZeroB;
                    double sum = 0.0;
                    for (int inner = 0; inner < k; inner++)
                        sum += (a.GetLong(baseA + row * k + inner) - zeroA)
                             * (double)(b.GetLong(baseB + inner * n + column) - zeroB);
                    result.Longs[baseOut + row * n + column] = (long)sum;
                }
            }

            for (int d = batchRank - 1; d >= 0; d--)
            {
                index[d]++;
                offsetA += strideA[d];
                offsetB += strideB[d];
                if (index[d] < batchShape[d]) break;
                offsetA -= strideA[d] * index[d];
                offsetB -= strideB[d] * index[d];
                index[d] = 0;
            }
        }

        return result;
    }

    /// <summary>
    /// ONNX <c>ConvInteger</c>: integer 2-D convolution with per-tensor zero points, producing
    /// int32.
    /// </summary>
    /// <remarks>
    /// The input zero point matters at the borders and is the reason padding cannot simply be
    /// skipped: a padded position contributes <c>(zeroPointX - zeroPointX)</c>, which is zero, but
    /// only because the pad value <em>is</em> the zero point. Treating a pad as a literal 0 byte
    /// would inject <c>-zeroPointX</c> into every border accumulation.
    /// </remarks>
    public static Tensor ConvInteger(
        Tensor x, Tensor weight, Tensor? xZeroPoint, Tensor? wZeroPoint,
        long[]? strides, long[]? pads, long[]? dilations, long group, string autoPad)
    {
        if (x.Shape.Length != 4 || weight.Shape.Length != 4)
            throw new NotSupportedException("ConvInteger: only 2-D convolution is supported");

        int batch = x.Shape[0], channels = x.Shape[1], height = x.Shape[2], width = x.Shape[3];
        int filters = weight.Shape[0], inPerGroup = weight.Shape[1], kh = weight.Shape[2], kw = weight.Shape[3];
        int groups = (int)Math.Max(group, 1);

        int strideH = (int)(strides is { Length: >= 2 } ? strides[0] : 1);
        int strideW = (int)(strides is { Length: >= 2 } ? strides[1] : 1);
        int dilationH = (int)(dilations is { Length: >= 2 } ? dilations[0] : 1);
        int dilationW = (int)(dilations is { Length: >= 2 } ? dilations[1] : 1);

        int effKh = (kh - 1) * dilationH + 1;
        int effKw = (kw - 1) * dilationW + 1;

        int padTop, padLeft, padBottom, padRight;
        if (autoPad is "SAME_UPPER" or "SAME_LOWER")
        {
            int sameH = (height + strideH - 1) / strideH;
            int sameW = (width + strideW - 1) / strideW;
            int totalH = Math.Max((sameH - 1) * strideH + effKh - height, 0);
            int totalW = Math.Max((sameW - 1) * strideW + effKw - width, 0);
            bool upper = autoPad == "SAME_UPPER";
            padTop = upper ? totalH / 2 : totalH - totalH / 2;
            padLeft = upper ? totalW / 2 : totalW - totalW / 2;
            padBottom = totalH - padTop;
            padRight = totalW - padLeft;
        }
        else if (pads is { Length: >= 4 })
        {
            padTop = (int)pads[0]; padLeft = (int)pads[1];
            padBottom = (int)pads[2]; padRight = (int)pads[3];
        }
        else { padTop = padLeft = padBottom = padRight = 0; }

        int outH = (height + padTop + padBottom - effKh) / strideH + 1;
        int outW = (width + padLeft + padRight - effKw) / strideW + 1;
        if (outH <= 0 || outW <= 0)
            throw new InvalidDataException($"ConvInteger: degenerate output size {outH}x{outW}");

        long zeroX = ZeroPointOf(xZeroPoint);
        bool perFilterW = wZeroPoint is { Count: > 1 };
        long scalarZeroW = perFilterW ? 0 : ZeroPointOf(wZeroPoint);

        var result = Tensor.AllocateLong(ElementType.Int32, batch, filters, outH, outW);
        int outPerGroup = filters / groups;

        for (int n = 0; n < batch; n++)
        {
            for (int f = 0; f < filters; f++)
            {
                int g = groups == 1 ? 0 : f / outPerGroup;
                long zeroW = perFilterW ? wZeroPoint!.GetLong(f) : scalarZeroW;
                int weightBase = f * inPerGroup * kh * kw;

                for (int oy = 0; oy < outH; oy++)
                {
                    for (int ox = 0; ox < outW; ox++)
                    {
                        double sum = 0.0;
                        for (int c = 0; c < inPerGroup; c++)
                        {
                            int channel = g * inPerGroup + c;
                            int planeBase = (n * channels + channel) * height * width;
                            for (int ky = 0; ky < kh; ky++)
                            {
                                int iy = oy * strideH + ky * dilationH - padTop;
                                // A padded position contributes zero because its value is the
                                // input zero point, so it can be skipped rather than materialised.
                                if (iy < 0 || iy >= height) continue;
                                for (int kx = 0; kx < kw; kx++)
                                {
                                    int ix = ox * strideW + kx * dilationW - padLeft;
                                    if (ix < 0 || ix >= width) continue;
                                    long pixel = x.GetLong(planeBase + iy * width + ix) - zeroX;
                                    long kernel = weight.GetLong(weightBase + (c * kh + ky) * kw + kx) - zeroW;
                                    sum += pixel * (double)kernel;
                                }
                            }
                        }
                        result.Longs[((n * filters + f) * outH + oy) * outW + ox] = (long)sum;
                    }
                }
            }
        }

        return result;
    }
}
