using System.Buffers;
using System.Numerics.Tensors;

namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// 2-D convolution and batch normalisation.
/// <para>
/// Convolution is lowered to matrix multiplication via <em>im2col</em>: each output pixel's
/// receptive field is unrolled into a column, turning the whole layer into one
/// <c>[M, C·kH·kW] x [C·kH·kW, outH·outW]</c> product that reuses the tuned kernel in
/// <see cref="Linear"/>. Two shapes bypass that entirely because they dominate these
/// backbones and the unrolled buffer would be pure overhead: a 1x1 stride-1 convolution is
/// already a matrix multiply against the input, and a depthwise convolution touches one
/// channel at a time, where im2col would inflate memory traffic by the kernel area for no
/// arithmetic gain.
/// </para>
/// </summary>
internal static class Convolution
{
    /// <summary>Padding as ONNX resolves it: explicit values, or derived from an auto_pad mode.</summary>
    private readonly record struct Padding(int Top, int Left, int Bottom, int Right);

    public static Tensor Conv(
        Tensor x, Tensor weight, Tensor? bias,
        long[]? strides, long[]? pads, long[]? dilations, long group, string autoPad,
        FusedActivation activation = FusedActivation.None)
    {
        var fx = x.AsFloat();
        var fw = weight.AsFloat();
        if (fx.Rank != 4 || fw.Rank != 4)
            throw new NotSupportedException("conv: only 2-D convolution is supported");

        int batch = fx.Shape[0], channels = fx.Shape[1], height = fx.Shape[2], width = fx.Shape[3];
        int filters = fw.Shape[0], inPerGroup = fw.Shape[1], kh = fw.Shape[2], kw = fw.Shape[3];
        int g = (int)Math.Max(group, 1);

        int strideH = (int)(strides is { Length: >= 2 } ? strides[0] : 1);
        int strideW = (int)(strides is { Length: >= 2 } ? strides[1] : 1);
        int dilationH = (int)(dilations is { Length: >= 2 } ? dilations[0] : 1);
        int dilationW = (int)(dilations is { Length: >= 2 } ? dilations[1] : 1);

        var pad = ResolvePadding(pads, autoPad, height, width, kh, kw, strideH, strideW, dilationH, dilationW);

        int effKh = (kh - 1) * dilationH + 1;
        int effKw = (kw - 1) * dilationW + 1;
        int outH = (height + pad.Top + pad.Bottom - effKh) / strideH + 1;
        int outW = (width + pad.Left + pad.Right - effKw) / strideW + 1;
        if (outH <= 0 || outW <= 0)
            throw new InvalidDataException($"conv: degenerate output size {outH}x{outW}");

        var result = Tensor.AllocateFloat(batch, filters, outH, outW);

        bool depthwise = g == channels && inPerGroup == 1 && filters % g == 0;
        bool pointwise = kh == 1 && kw == 1 && strideH == 1 && strideW == 1
                         && pad is { Top: 0, Left: 0, Bottom: 0, Right: 0 }
                         && dilationH == 1 && dilationW == 1;

        if (depthwise)
            ConvDepthwise(fx, fw, result, batch, channels, height, width, outH, outW,
                kh, kw, strideH, strideW, dilationH, dilationW, pad, filters / g);
        else if (pointwise)
            ConvPointwise(fx, fw, result, batch, channels, height * width, filters, g);
        else
            ConvIm2Col(fx, fw, result, batch, channels, height, width, outH, outW,
                kh, kw, strideH, strideW, dilationH, dilationW, pad, filters, inPerGroup, g);

        ApplyBiasAndActivation(result, bias?.AsFloat(), activation, batch, filters, outH * outW);
        return result;
    }

    /// <summary>
    /// Explicit pads win; otherwise <c>SAME_UPPER</c>/<c>SAME_LOWER</c> derive the total
    /// padding that keeps the output size equal to <c>ceil(input / stride)</c>, splitting an
    /// odd remainder toward the bottom-right or top-left respectively.
    /// </summary>
    private static Padding ResolvePadding(
        long[]? pads, string autoPad, int height, int width, int kh, int kw,
        int strideH, int strideW, int dilationH, int dilationW)
    {
        if (autoPad is "SAME_UPPER" or "SAME_LOWER")
        {
            int effKh = (kh - 1) * dilationH + 1;
            int effKw = (kw - 1) * dilationW + 1;
            int outH = (height + strideH - 1) / strideH;
            int outW = (width + strideW - 1) / strideW;
            int totalH = Math.Max((outH - 1) * strideH + effKh - height, 0);
            int totalW = Math.Max((outW - 1) * strideW + effKw - width, 0);
            bool upper = autoPad == "SAME_UPPER";
            int top = upper ? totalH / 2 : totalH - totalH / 2;
            int left = upper ? totalW / 2 : totalW - totalW / 2;
            return new Padding(top, left, totalH - top, totalW - left);
        }
        if (pads is { Length: >= 4 })
            return new Padding((int)pads[0], (int)pads[1], (int)pads[2], (int)pads[3]);
        return new Padding(0, 0, 0, 0);
    }

    /// <summary>Pointwise (1x1) convolution: one matrix multiply per batch item and group.</summary>
    private static void ConvPointwise(
        Tensor x, Tensor w, Tensor result, int batch, int channels, int spatial, int filters, int groups)
    {
        int inPerGroup = channels / groups;
        int outPerGroup = filters / groups;
        for (int n = 0; n < batch; n++)
        {
            for (int g = 0; g < groups; g++)
            {
                var weights = w.Floats.AsMemory(g * outPerGroup * inPerGroup, outPerGroup * inPerGroup);
                var input = x.Floats.AsMemory((n * channels + g * inPerGroup) * spatial, inPerGroup * spatial);
                var output = result.Floats.AsMemory((n * filters + g * outPerGroup) * spatial, outPerGroup * spatial);
                Linear.MultiplyInto(weights, input, output, outPerGroup, inPerGroup, spatial);
            }
        }
    }

    /// <summary>
    /// Depthwise convolution: each output channel reads exactly one input channel, so the
    /// whole layer is a stack of independent 2-D correlations with no channel reduction.
    /// The inner loop runs along a row, which keeps the input and output spans contiguous.
    /// </summary>
    private static void ConvDepthwise(
        Tensor x, Tensor w, Tensor result, int batch, int channels, int height, int width,
        int outH, int outW, int kh, int kw, int strideH, int strideW,
        int dilationH, int dilationW, Padding pad, int multiplier)
    {
        int planeIn = height * width;
        int planeOut = outH * outW;
        int filters = channels * multiplier;

        Parallel.For(0, batch * filters, plane =>
        {
            int n = plane / filters;
            int f = plane % filters;
            int c = f / multiplier;
            var src = x.Floats.AsSpan((n * channels + c) * planeIn, planeIn);
            var dst = result.Floats.AsSpan((n * filters + f) * planeOut, planeOut);
            var kernel = w.Floats.AsSpan(f * kh * kw, kh * kw);
            dst.Clear();

            for (int ky = 0; ky < kh; ky++)
            {
                for (int kx = 0; kx < kw; kx++)
                {
                    float weight = kernel[ky * kw + kx];
                    if (weight == 0f) continue;
                    for (int oy = 0; oy < outH; oy++)
                    {
                        int iy = oy * strideH - pad.Top + ky * dilationH;
                        if ((uint)iy >= (uint)height) continue;
                        int rowIn = iy * width;
                        int rowOut = oy * outW;
                        for (int ox = 0; ox < outW; ox++)
                        {
                            int ix = ox * strideW - pad.Left + kx * dilationW;
                            if ((uint)ix >= (uint)width) continue;
                            dst[rowOut + ox] += weight * src[rowIn + ix];
                        }
                    }
                }
            }
        });
    }

    /// <summary>General convolution: unroll receptive fields into columns, then one GEMM per group.</summary>
    private static void ConvIm2Col(
        Tensor x, Tensor w, Tensor result, int batch, int channels, int height, int width,
        int outH, int outW, int kh, int kw, int strideH, int strideW,
        int dilationH, int dilationW, Padding pad, int filters, int inPerGroup, int groups)
    {
        int outPerGroup = filters / groups;
        int patch = inPerGroup * kh * kw;
        int spatial = outH * outW;

        // Unrolling the whole layer at once would be the largest allocation in the model — a
        // 3x3 layer over 32 channels at 320x320 output needs 118 MB — and every byte of it
        // would be written to memory and read straight back. A tile of output pixels is
        // unrolled instead, and is also the unit of parallelism since tiles write disjoint
        // output columns.
        //
        // Sizing the tile is a genuine trade and both directions were measured. Larger tiles
        // re-read the weight matrix fewer times; smaller tiles keep the unrolled buffer near
        // cache. Growing the tile to one per core made the model slower — the extra weight
        // reuse did not pay for streaming a multi-megabyte buffer per tile — so the buffer
        // stays the thing that is sized, and weight reuse is recovered instead by letting the
        // multiply use full-width column panels when the tile loop already supplies the
        // parallelism.
        int tile = Math.Clamp(TargetTileBytes / (patch * sizeof(float)), MinTile, spatial);
        tile = Math.Min(tile, spatial);
        int tiles = (spatial + tile - 1) / tile;

        for (int n = 0; n < batch; n++)
        {
            for (int g = 0; g < groups; g++)
            {
                var weights = w.Floats.AsMemory(g * outPerGroup * patch, outPerGroup * patch);
                int outputBase = (n * filters + g * outPerGroup) * spatial;
                int capturedN = n, capturedG = g;

                Parallel.For(0, tiles, t =>
                {
                    int start = t * tile;
                    int length = Math.Min(tile, spatial - start);
                    float[] column = ArrayPool<float>.Shared.Rent(patch * length);
                    try
                    {
                        Im2ColTile(x.Floats, column, capturedN, channels, capturedG * inPerGroup, inPerGroup,
                            height, width, outH, outW, kh, kw, strideH, strideW, dilationH, dilationW, pad,
                            start, length);

                        // Rows of this tile's result are `spatial` apart in the full output,
                        // and the tile loop is already parallel, so the multiply runs serially.
                        Linear.MultiplyInto(
                            weights, column.AsMemory(0, patch * length),
                            result.Floats.AsMemory(outputBase + start, (outPerGroup - 1) * spatial + length),
                            outPerGroup, patch, length, spatial, parallel: false);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(column);
                    }
                });
            }
        }
    }

    /// <summary>Unrolled receptive field to hold per tile.</summary>
    private const int TargetTileBytes = 512 * 1024;

    /// <summary>Smallest worthwhile tile, so a very deep patch does not degenerate to one
    /// output pixel per multiply.</summary>
    private const int MinTile = 128;

    /// <summary>
    /// Fill <paramref name="column"/> so that row <c>(c,ky,kx)</c> holds the input value that
    /// kernel tap contributes to each output pixel, in output-pixel order.
    /// </summary>
    /// <summary>
    /// Unroll the receptive fields of output pixels <c>[start, start+length)</c> into
    /// <paramref name="column"/>, laid out as <c>[patch, length]</c>.
    /// <para>
    /// Every element is written — either a source value or an explicit zero for a tap that
    /// falls in the padding — so the buffer needs no pre-clearing. That matters: clearing was
    /// a second full pass over a buffer that is, for the early layers, larger than the layer's
    /// own input.
    /// </para>
    /// </summary>
    private static void Im2ColTile(
        float[] src, float[] column, int n, int channels, int channelStart, int channelCount,
        int height, int width, int outH, int outW, int kh, int kw,
        int strideH, int strideW, int dilationH, int dilationW, Padding pad,
        int start, int length)
    {
        for (int c = 0; c < channelCount; c++)
        {
            int srcPlane = (n * channels + channelStart + c) * height * width;
            for (int ky = 0; ky < kh; ky++)
            {
                for (int kx = 0; kx < kw; kx++)
                {
                    int row = ((c * kh + ky) * kw + kx) * length;
                    int ixStart = -pad.Left + kx * dilationW;

                    int position = 0;
                    while (position < length)
                    {
                        int pixel = start + position;
                        int oy = pixel / outW;
                        int ox = pixel - oy * outW;
                        // How much of this output row is still inside the tile.
                        int run = Math.Min(outW - ox, length - position);

                        int iy = oy * strideH - pad.Top + ky * dilationH;
                        if ((uint)iy >= (uint)height)
                        {
                            column.AsSpan(row + position, run).Clear();
                            position += run;
                            continue;
                        }

                        int srcRow = srcPlane + iy * width;
                        int firstIx = ox * strideW + ixStart;
                        int lastIx = (ox + run - 1) * strideW + ixStart;

                        // Unit stride with the whole run inside the image: one contiguous copy.
                        if (strideW == 1 && firstIx >= 0 && lastIx < width)
                        {
                            src.AsSpan(srcRow + firstIx, run).CopyTo(column.AsSpan(row + position, run));
                        }
                        else
                        {
                            for (int i = 0; i < run; i++)
                            {
                                int ix = (ox + i) * strideW + ixStart;
                                column[row + position + i] = (uint)ix < (uint)width ? src[srcRow + ix] : 0f;
                            }
                        }
                        position += run;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Apply the per-channel bias and any fused activation in a single pass over each plane.
    /// <para>
    /// Keeping these together is the point of fusing them. As separate graph nodes the bias
    /// and the activation each stream the whole activation tensor through memory and write a
    /// fresh one back; done here, the plane is already in cache from the multiply that
    /// produced it, and no intermediate is materialised at all.
    /// </para>
    /// </summary>
    private static void ApplyBiasAndActivation(
        Tensor result, Tensor? bias, FusedActivation activation, int batch, int channels, int spatial)
    {
        if (bias is null && activation == FusedActivation.None) return;

        Parallel.For(0, batch * channels, plane =>
        {
            var span = result.Floats.AsSpan(plane * spatial, spatial);
            if (bias is not null) TensorPrimitives.Add(span, bias.Floats[plane % channels], span);

            switch (activation)
            {
                case FusedActivation.Relu:
                    TensorPrimitives.Max(span, 0f, span);
                    break;
                case FusedActivation.Sigmoid:
                    TensorPrimitives.Sigmoid(span, span);
                    break;
                case FusedActivation.SiLU:
                    SiLUInPlace(span);
                    break;
            }
        });
    }

    /// <summary>
    /// <c>x * sigmoid(x)</c> in place, using a stack buffer for the sigmoid so the whole
    /// activation is never duplicated.
    /// </summary>
    private static void SiLUInPlace(Span<float> span)
    {
        const int ChunkSize = 1024;
        Span<float> scratch = stackalloc float[ChunkSize];
        for (int offset = 0; offset < span.Length; offset += ChunkSize)
        {
            var chunk = span.Slice(offset, Math.Min(ChunkSize, span.Length - offset));
            var buffer = scratch[..chunk.Length];
            TensorPrimitives.Sigmoid(chunk, buffer);
            TensorPrimitives.Multiply(chunk, buffer, chunk);
        }
    }

    /// <summary>
    /// Inference-mode batch normalisation, collapsed to one affine pass.
    /// <para>
    /// The four per-channel parameters fold into a single scale and shift —
    /// <c>a = scale / sqrt(var + eps)</c>, <c>b = B - mean * a</c> — so each plane costs one
    /// fused multiply-add rather than a subtract, a divide, a multiply and an add. The
    /// reciprocal square root is computed once per channel, not once per pixel.
    /// </para>
    /// </summary>
    public static Tensor BatchNormalization(Tensor x, Tensor scale, Tensor bias, Tensor mean, Tensor variance, float epsilon)
    {
        var fx = x.AsFloat();
        int channels = fx.Rank >= 2 ? fx.Shape[1] : 1;
        int batch = fx.Rank >= 1 ? fx.Shape[0] : 1;
        int spatial = 1;
        for (int i = 2; i < fx.Rank; i++) spatial *= fx.Shape[i];

        var result = Tensor.AllocateFloat(fx.Shape);
        var fs = scale.AsFloat();
        var fb = bias.AsFloat();
        var fm = mean.AsFloat();
        var fv = variance.AsFloat();

        for (int c = 0; c < channels; c++)
        {
            float a = fs.Floats[c] / MathF.Sqrt(fv.Floats[c] + epsilon);
            float b = fb.Floats[c] - fm.Floats[c] * a;
            for (int n = 0; n < batch; n++)
            {
                int offset = (n * channels + c) * spatial;
                var src = fx.Floats.AsSpan(offset, spatial);
                var dst = result.Floats.AsSpan(offset, spatial);
                TensorPrimitives.Multiply(src, a, dst);
                TensorPrimitives.Add(dst, b, dst);
            }
        }
        return result;
    }

    /// <summary>
    /// Layer normalisation over the trailing axes: normalise each row to zero mean and unit
    /// variance, then apply the learned affine. Exported graphs often spell this out as
    /// ReduceMean/Sub/Pow/Sqrt/Div nodes instead, which run through the generic kernels.
    /// </summary>
    public static Tensor LayerNormalization(Tensor x, Tensor scale, Tensor? bias, long axis, float epsilon)
    {
        var fx = x.AsFloat();
        int a = Shapes.NormalizeAxis(axis, fx.Rank);
        int rows = 1;
        for (int i = 0; i < a; i++) rows *= fx.Shape[i];
        int cols = 1;
        for (int i = a; i < fx.Rank; i++) cols *= fx.Shape[i];

        var result = Tensor.AllocateFloat(fx.Shape);
        var fs = scale.AsFloat();
        var fb = bias?.AsFloat();

        for (int r = 0; r < rows; r++)
        {
            var src = fx.Floats.AsSpan(r * cols, cols);
            var dst = result.Floats.AsSpan(r * cols, cols);
            float mean = TensorPrimitives.Sum(src) / cols;
            TensorPrimitives.Subtract(src, mean, dst);
            float variance = TensorPrimitives.SumOfSquares(dst) / cols;
            TensorPrimitives.Multiply(dst, 1f / MathF.Sqrt(variance + epsilon), dst);
            TensorPrimitives.Multiply(dst, fs.Floats, dst);
            if (fb is not null) TensorPrimitives.Add(dst, fb.Floats, dst);
        }
        return result;
    }
}
