using System.Numerics.Tensors;

namespace Xberg.Internal.Onnx.Ops;

/// <summary>Which reduction a Reduce* node performs.</summary>
internal enum ReduceKind { Sum, Mean, Max, Min }

/// <summary>
/// Reductions, softmax and top-k.
/// <para>
/// Reductions are reorganised into a three-part <c>[outer, reduced, inner]</c> view before
/// any arithmetic runs. When <c>inner == 1</c> — the overwhelmingly common case, since
/// layer-norm statistics and attention softmaxes reduce the last axis — each output element
/// comes from one contiguous span, so the work goes straight to a vectorised primitive
/// instead of a strided gather.
/// </para>
/// </summary>
internal static class Reductions
{
    /// <summary>
    /// Reduce over <paramref name="axes"/>, keeping or dropping the reduced dimensions.
    /// Multiple axes are handled by reducing one at a time; the graphs here never reduce
    /// more than two, so the extra passes cost less than a general strided kernel would.
    /// </summary>
    public static Tensor Reduce(Tensor x, ReadOnlySpan<long> axes, bool keepDims, bool noopWithEmptyAxes, ReduceKind kind)
    {
        if (axes.Length == 0)
        {
            // Opset 18 added `noop_with_empty_axes`: without it, no axes means reduce all.
            if (noopWithEmptyAxes) return x;
            var all = new long[x.Rank];
            for (int i = 0; i < x.Rank; i++) all[i] = i;
            return Reduce(x, all, keepDims, false, kind);
        }

        // Reduce the highest axis first so the lower axis indices stay valid as rank shrinks.
        var sorted = new int[axes.Length];
        for (int i = 0; i < axes.Length; i++) sorted[i] = Shapes.NormalizeAxis(axes[i], x.Rank);
        Array.Sort(sorted);

        var current = x.AsFloat();
        for (int i = sorted.Length - 1; i >= 0; i--)
            current = ReduceAxis(current, sorted[i], keepDims, kind);
        return current;
    }

    private static Tensor ReduceAxis(Tensor x, int axis, bool keepDims, ReduceKind kind)
    {
        int dim = x.Shape[axis];
        int outer = 1;
        for (int i = 0; i < axis; i++) outer *= x.Shape[i];
        int inner = 1;
        for (int i = axis + 1; i < x.Rank; i++) inner *= x.Shape[i];

        var shape = new List<int>(x.Rank);
        for (int i = 0; i < x.Rank; i++)
        {
            if (i == axis) { if (keepDims) shape.Add(1); }
            else shape.Add(x.Shape[i]);
        }

        var result = Tensor.AllocateFloat(shape.ToArray());
        var src = x.Floats;
        var dst = result.Floats;

        if (inner == 1)
        {
            // Contiguous runs: hand each one to a vectorised primitive whole.
            for (int o = 0; o < outer; o++)
            {
                var span = src.AsSpan(o * dim, dim);
                dst[o] = kind switch
                {
                    ReduceKind.Sum => TensorPrimitives.Sum(span),
                    ReduceKind.Mean => dim == 0 ? 0f : TensorPrimitives.Sum(span) / dim,
                    ReduceKind.Max => TensorPrimitives.Max(span),
                    ReduceKind.Min => TensorPrimitives.Min(span),
                    _ => throw new NotSupportedException($"reduce kind {kind}"),
                };
            }
            return result;
        }

        // Strided: accumulate across the reduced axis a whole inner row at a time, so the
        // per-step work is still a vector operation over `inner` contiguous elements.
        for (int o = 0; o < outer; o++)
        {
            var outRow = dst.AsSpan(o * inner, inner);
            var first = src.AsSpan(o * dim * inner, inner);
            first.CopyTo(outRow);

            for (int r = 1; r < dim; r++)
            {
                var row = src.AsSpan((o * dim + r) * inner, inner);
                switch (kind)
                {
                    case ReduceKind.Sum:
                    case ReduceKind.Mean:
                        TensorPrimitives.Add(outRow, row, outRow);
                        break;
                    case ReduceKind.Max:
                        TensorPrimitives.Max(outRow, row, outRow);
                        break;
                    case ReduceKind.Min:
                        TensorPrimitives.Min(outRow, row, outRow);
                        break;
                    default: throw new NotSupportedException($"reduce kind {kind}");
                }
            }
            if (kind == ReduceKind.Mean && dim > 1) TensorPrimitives.Divide(outRow, dim, outRow);
        }
        return result;
    }

    /// <summary>
    /// Softmax over a single axis (opset 13 semantics).
    /// <para>
    /// The row maximum is subtracted before exponentiating, which is load-bearing rather than
    /// cosmetic and is why this does not simply call <c>TensorPrimitives.SoftMax</c> — that
    /// primitive exponentiates the raw values. RT-DETR's cross-attention produces logit rows
    /// around −164; every <c>exp</c> underflows to zero, the normalising sum is zero, and the
    /// whole row divides out to NaN. Shifting by the maximum makes the largest term exactly
    /// 1 and leaves the result unchanged, since the shift cancels in the ratio.
    /// </para>
    /// </summary>
    public static Tensor Softmax(Tensor x, long axis)
    {
        var f = x.AsFloat();
        int a = Shapes.NormalizeAxis(axis, f.Rank);
        int dim = f.Shape[a];
        int outer = 1;
        for (int i = 0; i < a; i++) outer *= f.Shape[i];
        int inner = 1;
        for (int i = a + 1; i < f.Rank; i++) inner *= f.Shape[i];

        var result = Tensor.AllocateFloat(f.Shape);
        var src = f.Floats;
        var dst = result.Floats;

        if (inner == 1)
        {
            for (int o = 0; o < outer; o++)
                SoftmaxRow(src.AsSpan(o * dim, dim), dst.AsSpan(o * dim, dim));
            return result;
        }

        // Strided axis: gather the row, run the same kernel, scatter it back. Softmax over a
        // non-final axis is rare enough that the copy is cheaper than a bespoke kernel.
        var buffer = new float[dim];
        var output = new float[dim];
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                int basePos = o * dim * inner + i;
                for (int r = 0; r < dim; r++) buffer[r] = src[basePos + r * inner];
                SoftmaxRow(buffer, output);
                for (int r = 0; r < dim; r++) dst[basePos + r * inner] = output[r];
            }
        }
        return result;
    }

    /// <summary>Numerically stable softmax of one contiguous row.</summary>
    private static void SoftmaxRow(ReadOnlySpan<float> row, Span<float> destination)
    {
        if (row.Length == 0) return;
        float max = TensorPrimitives.Max(row);
        // An all-negative-infinity row (a fully masked attention position) would otherwise
        // produce 0/0; ONNX Runtime yields a uniform distribution there, so match it.
        if (float.IsNegativeInfinity(max))
        {
            destination.Fill(1f / row.Length);
            return;
        }
        TensorPrimitives.Subtract(row, max, destination);
        TensorPrimitives.Exp(destination, destination);
        float sum = TensorPrimitives.Sum(destination);
        if (sum > 0f) TensorPrimitives.Divide(destination, sum, destination);
    }

    /// <summary>
    /// TopK along an axis, returning values and int64 indices.
    /// <para>
    /// Ties are broken by lower index, matching ONNX Runtime: RT-DETR's query selection
    /// feeds hundreds of near-identical scores through this, and a different tie rule would
    /// silently reorder detections.
    /// </para>
    /// </summary>
    public static (Tensor Values, Tensor Indices) TopK(Tensor x, int k, long axis, bool largest, bool sorted)
    {
        var f = x.AsFloat();
        int a = Shapes.NormalizeAxis(axis, f.Rank);
        int dim = f.Shape[a];
        k = Math.Clamp(k, 0, dim);

        int outer = 1;
        for (int i = 0; i < a; i++) outer *= f.Shape[i];
        int inner = 1;
        for (int i = a + 1; i < f.Rank; i++) inner *= f.Shape[i];

        var shape = (int[])f.Shape.Clone();
        shape[a] = k;
        var values = Tensor.AllocateFloat(shape);
        var indices = Tensor.AllocateLong(ElementType.Int64, shape);

        var order = new int[dim];
        var row = new float[dim];
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                int basePos = o * dim * inner + i;
                for (int r = 0; r < dim; r++)
                {
                    row[r] = f.Floats[basePos + r * inner];
                    order[r] = r;
                }

                Array.Sort(order, (p, q) =>
                {
                    int c = largest ? row[q].CompareTo(row[p]) : row[p].CompareTo(row[q]);
                    return c != 0 ? c : p.CompareTo(q);
                });

                // `sorted: false` still permits any order; emitting the sorted prefix is
                // both valid and what ORT does, so parity holds either way.
                _ = sorted;
                int outBase = o * k * inner + i;
                for (int r = 0; r < k; r++)
                {
                    values.Floats[outBase + r * inner] = row[order[r]];
                    indices.Longs[outBase + r * inner] = order[r];
                }
            }
        }
        return (values, indices);
    }

    /// <summary>Index of the maximum along an axis.</summary>
    public static Tensor ArgMax(Tensor x, long axis, bool keepDims, bool selectLastIndex)
    {
        var f = x.AsFloat();
        int a = Shapes.NormalizeAxis(axis, f.Rank);
        int dim = f.Shape[a];
        int outer = 1;
        for (int i = 0; i < a; i++) outer *= f.Shape[i];
        int inner = 1;
        for (int i = a + 1; i < f.Rank; i++) inner *= f.Shape[i];

        var shape = new List<int>(f.Rank);
        for (int i = 0; i < f.Rank; i++)
        {
            if (i == a) { if (keepDims) shape.Add(1); }
            else shape.Add(f.Shape[i]);
        }

        var result = Tensor.AllocateLong(ElementType.Int64, shape.ToArray());
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                int basePos = o * dim * inner + i;
                float best = float.NegativeInfinity;
                int bestIndex = 0;
                for (int r = 0; r < dim; r++)
                {
                    float v = f.Floats[basePos + r * inner];
                    if (v > best || (selectLastIndex && v == best))
                    {
                        best = v;
                        bestIndex = r;
                    }
                }
                result.Longs[o * inner + i] = bestIndex;
            }
        }
        return result;
    }
}
