namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// Structural kernels: the ops that move, slice and re-index data without arithmetic.
/// <para>
/// They dominate node counts in transformer exports (RT-DETR spends over a third of its
/// 2676 nodes here) but almost none of the runtime, because most operate on shape vectors
/// of a few elements. The ones that do touch bulk data — <see cref="Transpose"/>,
/// <see cref="Gather"/>, <see cref="Concat"/> — copy in contiguous runs wherever the layout
/// allows, which is what keeps them off the profile.
/// </para>
/// </summary>
internal static class Shapes
{
    /// <summary>Resolve a possibly negative axis against a rank, as ONNX defines it.</summary>
    public static int NormalizeAxis(long axis, int rank)
    {
        int a = (int)axis;
        if (a < 0) a += rank;
        if (a < 0 || a >= Math.Max(rank, 1))
            throw new InvalidDataException($"axis {axis} out of range for rank {rank}");
        return a;
    }

    /// <summary>
    /// Reshape, resolving ONNX's two wildcards: <c>0</c> copies the input dimension at that
    /// position, and a single <c>-1</c> absorbs whatever is left over.
    /// </summary>
    public static Tensor Reshape(Tensor x, Tensor shapeTensor, bool allowZero)
    {
        var requested = shapeTensor.ToIntArray();
        var shape = new int[requested.Length];
        int inferIndex = -1;
        int known = 1;

        for (int i = 0; i < requested.Length; i++)
        {
            int d = requested[i];
            if (d == -1)
            {
                if (inferIndex >= 0) throw new InvalidDataException("reshape: more than one -1 dimension");
                inferIndex = i;
                shape[i] = 1;
                continue;
            }
            if (d == 0 && !allowZero)
            {
                if (i >= x.Rank) throw new InvalidDataException($"reshape: dimension {i} copies a missing input dim");
                d = x.Shape[i];
            }
            shape[i] = d;
            known *= d;
        }

        if (inferIndex >= 0)
        {
            if (known == 0) throw new InvalidDataException("reshape: cannot infer a dimension against a zero-sized shape");
            shape[inferIndex] = x.Count / known;
        }
        return x.Reshaped(shape);
    }

    /// <summary>The input's shape, as the int64 vector every downstream shape node expects.</summary>
    public static Tensor Shape(Tensor x, long start, long? end)
    {
        int rank = x.Rank;
        int from = ClampShapeBound(start, rank);
        int to = end is { } e ? ClampShapeBound(e, rank) : rank;
        if (to < from) to = from;
        var data = new long[to - from];
        for (int i = from; i < to; i++) data[i - from] = x.Shape[i];
        return Tensor.FromLongs(data, ElementType.Int64, data.Length);
    }

    private static int ClampShapeBound(long value, int rank)
    {
        long v = value < 0 ? value + rank : value;
        return (int)Math.Clamp(v, 0, rank);
    }

    /// <summary>Insert size-1 dimensions at each position in <paramref name="axes"/>.</summary>
    public static Tensor Unsqueeze(Tensor x, ReadOnlySpan<long> axes)
    {
        int rank = x.Rank + axes.Length;
        var inserted = new bool[rank];
        foreach (long a in axes)
        {
            int axis = (int)(a < 0 ? a + rank : a);
            if (axis < 0 || axis >= rank) throw new InvalidDataException($"unsqueeze: axis {a} out of range");
            inserted[axis] = true;
        }

        var shape = new int[rank];
        int src = 0;
        for (int i = 0; i < rank; i++) shape[i] = inserted[i] ? 1 : x.Shape[src++];
        return x.Reshaped(shape);
    }

    /// <summary>Drop the listed size-1 dimensions, or every size-1 dimension when none are listed.</summary>
    public static Tensor Squeeze(Tensor x, ReadOnlySpan<long> axes)
    {
        var drop = new bool[x.Rank];
        if (axes.Length == 0)
        {
            for (int i = 0; i < x.Rank; i++) drop[i] = x.Shape[i] == 1;
        }
        else
        {
            foreach (long a in axes) drop[NormalizeAxis(a, x.Rank)] = true;
        }

        var shape = new List<int>(x.Rank);
        for (int i = 0; i < x.Rank; i++) if (!drop[i]) shape.Add(x.Shape[i]);
        return x.Reshaped(shape.ToArray());
    }

    /// <summary>Collapse to 2-D: everything before <paramref name="axis"/> times everything from it.</summary>
    public static Tensor Flatten(Tensor x, long axis)
    {
        int a = (int)(axis < 0 ? axis + x.Rank : axis);
        if (a < 0 || a > x.Rank) throw new InvalidDataException($"flatten: axis {axis} out of range");
        int rows = 1, cols = 1;
        for (int i = 0; i < a; i++) rows *= x.Shape[i];
        for (int i = a; i < x.Rank; i++) cols *= x.Shape[i];
        return x.Reshaped(rows, cols);
    }

    /// <summary>
    /// Permute dimensions. The trailing dimensions often survive the permutation unmoved
    /// (<c>[0,2,1,3]</c> on a four-axis attention tensor keeps the head dimension innermost),
    /// so the copy is done in runs of that length rather than element by element.
    /// </summary>
    public static Tensor Transpose(Tensor x, ReadOnlySpan<long> perm)
    {
        int rank = x.Rank;
        var permutation = new int[rank];
        if (perm.Length == 0)
        {
            for (int i = 0; i < rank; i++) permutation[i] = rank - 1 - i;
        }
        else
        {
            if (perm.Length != rank) throw new InvalidDataException("transpose: perm length must match rank");
            for (int i = 0; i < rank; i++) permutation[i] = NormalizeAxis(perm[i], rank);
        }

        var srcStrides = x.Strides();
        var shape = new int[rank];
        var strides = new int[rank];
        for (int i = 0; i < rank; i++)
        {
            shape[i] = x.Shape[permutation[i]];
            strides[i] = srcStrides[permutation[i]];
        }

        // How many trailing output dimensions still read contiguously from the source.
        int run = 1, expected = 1;
        for (int i = rank - 1; i >= 0; i--)
        {
            if (strides[i] != expected) break;
            run *= shape[i];
            expected = run;
        }
        if (run == 0) run = 1;

        // The permutation swapped the last two axes: the innermost output dimension strides
        // through the source while the one before it is contiguous. Walking that element by
        // element touches a fresh cache line per value and uses one float of each, which is
        // how a copy of a few megabytes ends up costing tens of milliseconds. Blocking it
        // makes both sides read and write in cache-line-sized runs.
        if (run == 1 && rank >= 2 && strides[rank - 2] == 1 && strides[rank - 1] > 1)
            return TransposeInnerPair(x, shape, strides, rank);

        return Gathered(x, shape, strides, run);
    }

    /// <summary>Square block edge for the tiled transpose, in elements. At 32 floats a row of
    /// a block is two cache lines, so a whole block stays comfortably in L1.</summary>
    private const int TransposeBlock = 32;

    /// <summary>
    /// Materialise a permutation whose innermost two output axes are a matrix transpose,
    /// tiling so neither side strides through memory a line at a time.
    /// </summary>
    private static Tensor TransposeInnerPair(Tensor x, int[] shape, int[] strides, int rank)
    {
        int rows = shape[rank - 2];          // contiguous in the source
        int columns = shape[rank - 1];       // strided in the source
        int columnStride = strides[rank - 1];

        int outer = 1;
        for (int i = 0; i < rank - 2; i++) outer *= shape[i];
        int plane = rows * columns;

        var result = x.IsFloat ? Tensor.AllocateFloat(shape) : Tensor.AllocateLong(x.Type, shape);
        var outerIndex = new int[Math.Max(rank - 2, 1)];
        int sourceBase = 0;

        for (int o = 0; o < outer; o++)
        {
            int destinationBase = o * plane;
            for (int i0 = 0; i0 < rows; i0 += TransposeBlock)
            {
                int iEnd = Math.Min(i0 + TransposeBlock, rows);
                for (int j0 = 0; j0 < columns; j0 += TransposeBlock)
                {
                    int jEnd = Math.Min(j0 + TransposeBlock, columns);
                    for (int i = i0; i < iEnd; i++)
                    {
                        int destination = destinationBase + i * columns + j0;
                        int source = sourceBase + i + j0 * columnStride;
                        if (x.IsFloat)
                        {
                            for (int j = j0; j < jEnd; j++, destination++, source += columnStride)
                                result.Floats[destination] = x.Floats[source];
                        }
                        else
                        {
                            for (int j = j0; j < jEnd; j++, destination++, source += columnStride)
                                result.Longs[destination] = x.Longs[source];
                        }
                    }
                }
            }

            // Advance the outer coordinates, which may themselves be permuted.
            for (int d = rank - 3; d >= 0; d--)
            {
                outerIndex[d]++;
                sourceBase += strides[d];
                if (outerIndex[d] < shape[d]) break;
                sourceBase -= strides[d] * outerIndex[d];
                outerIndex[d] = 0;
            }
        }
        return result;
    }

    /// <summary>
    /// Materialise a strided view into a fresh contiguous tensor, copying
    /// <paramref name="run"/> elements at a time.
    /// </summary>
    private static Tensor Gathered(Tensor x, int[] shape, int[] strides, int run)
    {
        int total = Tensor.ElementCount(shape);
        int rank = shape.Length;
        int blocks = run > 0 ? total / run : 0;
        var index = new int[Math.Max(rank, 1)];
        int offset = 0;

        if (x.IsFloat)
        {
            var result = Tensor.AllocateFloat(shape);
            var src = x.Floats.AsSpan();
            var dst = result.Floats.AsSpan();
            for (int b = 0; b < blocks; b++)
            {
                src.Slice(offset, run).CopyTo(dst.Slice(b * run, run));
                Advance(shape, strides, index, rank, run, ref offset);
            }
            return result;
        }
        else
        {
            var result = Tensor.AllocateLong(x.Type, shape);
            var src = x.Longs.AsSpan();
            var dst = result.Longs.AsSpan();
            for (int b = 0; b < blocks; b++)
            {
                src.Slice(offset, run).CopyTo(dst.Slice(b * run, run));
                Advance(shape, strides, index, rank, run, ref offset);
            }
            return result;
        }
    }

    /// <summary>Odometer over the dimensions outside the copied run.</summary>
    private static void Advance(int[] shape, int[] strides, int[] index, int rank, int run, ref int offset)
    {
        // Dimensions fully inside the run are not iterated: the block copy covered them.
        int outerRank = rank;
        int consumed = 1;
        while (outerRank > 0 && consumed * shape[outerRank - 1] <= run)
        {
            consumed *= shape[outerRank - 1];
            outerRank--;
        }

        for (int d = outerRank - 1; d >= 0; d--)
        {
            index[d]++;
            offset += strides[d];
            if (index[d] < shape[d]) return;
            offset -= strides[d] * index[d];
            index[d] = 0;
        }
    }

    /// <summary>Join tensors along <paramref name="axis"/>.</summary>
    public static Tensor Concat(IReadOnlyList<Tensor> inputs, long axis)
    {
        if (inputs.Count == 0) throw new InvalidDataException("concat: no inputs");
        var first = inputs[0];
        int rank = first.Rank;
        int a = NormalizeAxis(axis, rank);

        var shape = (int[])first.Shape.Clone();
        int axisTotal = 0;
        foreach (var t in inputs) axisTotal += t.Rank > a ? t.Shape[a] : 1;
        shape[a] = axisTotal;

        // Everything before the axis iterates; everything from the axis on copies contiguously.
        int outer = 1;
        for (int i = 0; i < a; i++) outer *= shape[i];

        bool isFloat = first.IsFloat;
        var result = isFloat ? Tensor.AllocateFloat(shape) : Tensor.AllocateLong(first.Type, shape);

        int dstPos = 0;
        var chunkSizes = new int[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            int chunk = 1;
            for (int d = a; d < inputs[i].Rank; d++) chunk *= inputs[i].Shape[d];
            chunkSizes[i] = chunk;
        }

        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                int chunk = chunkSizes[i];
                if (chunk == 0) continue;
                if (isFloat)
                    inputs[i].Floats.AsSpan(o * chunk, chunk).CopyTo(result.Floats.AsSpan(dstPos, chunk));
                else
                    inputs[i].Longs.AsSpan(o * chunk, chunk).CopyTo(result.Longs.AsSpan(dstPos, chunk));
                dstPos += chunk;
            }
        }
        return result;
    }

    /// <summary>
    /// Slice with the opset-10+ signature: starts, ends, and optional axes and steps, all
    /// as tensors. Bounds follow Python semantics — negative indices count from the end and
    /// out-of-range values clamp rather than fail.
    /// </summary>
    public static Tensor Slice(Tensor x, Tensor starts, Tensor ends, Tensor? axesTensor, Tensor? stepsTensor)
    {
        int rank = x.Rank;
        // Bounds stay 64-bit until after clamping: exporters spell "to the end" as
        // INT64_MAX, which does not survive a narrowing conversion.
        var startArr = ToLongArray(starts);
        var endArr = ToLongArray(ends);
        var axes = axesTensor is null
            ? Enumerable.Range(0, startArr.Length).ToArray()
            : axesTensor.ToIntArray();
        var steps = stepsTensor is null
            ? Enumerable.Repeat(1, startArr.Length).ToArray()
            : stepsTensor.ToIntArray();

        var begin = new int[rank];
        var step = new int[rank];
        var count = new int[rank];
        for (int i = 0; i < rank; i++)
        {
            begin[i] = 0;
            step[i] = 1;
            count[i] = x.Shape[i];
        }

        for (int i = 0; i < axes.Length; i++)
        {
            int axis = NormalizeAxis(axes[i], rank);
            int dim = x.Shape[axis];
            int s = steps[i];
            if (s == 0) throw new InvalidDataException("slice: step must not be zero");

            long rawStart = startArr[i];
            long rawEnd = endArr[i];
            // Only genuinely negative indices count from the end; INT64_MIN-style sentinels
            // would wrap, so the adjustment is guarded against a magnitude beyond the axis.
            if (rawStart < 0 && rawStart > long.MinValue + dim) rawStart += dim;
            if (rawEnd < 0 && rawEnd > long.MinValue + dim) rawEnd += dim;

            int lo, hi, n;
            if (s > 0)
            {
                lo = (int)Math.Clamp(rawStart, 0, dim);
                hi = (int)Math.Clamp(rawEnd, 0, dim);
                n = Math.Max(0, (hi - lo + s - 1) / s);
            }
            else
            {
                // A reversed slice clamps to [-1, dim-1]: -1 means "past the front edge".
                lo = (int)Math.Clamp(rawStart, -1, dim - 1);
                hi = (int)Math.Clamp(rawEnd, -1, dim - 1);
                n = Math.Max(0, (lo - hi - s - 1) / -s);
            }

            begin[axis] = lo;
            step[axis] = s;
            count[axis] = n;
        }

        var srcStrides = x.Strides();
        var strides = new int[rank];
        int baseOffset = 0;
        for (int i = 0; i < rank; i++)
        {
            strides[i] = srcStrides[i] * step[i];
            baseOffset += begin[i] * srcStrides[i];
        }

        // Trailing dimensions taken whole with step 1 still read contiguously.
        int run = 1, expected = 1;
        for (int i = rank - 1; i >= 0; i--)
        {
            if (strides[i] != expected || count[i] != x.Shape[i]) break;
            run *= count[i];
            expected = run;
        }
        if (run == 0) run = 1;

        return GatheredFrom(x, count, strides, run, baseOffset);
    }

    private static long[] ToLongArray(Tensor t)
    {
        var values = new long[t.Count];
        for (int i = 0; i < t.Count; i++) values[i] = t.GetLong(i);
        return values;
    }

    private static Tensor GatheredFrom(Tensor x, int[] shape, int[] strides, int run, int baseOffset)
    {
        int total = Tensor.ElementCount(shape);
        int rank = shape.Length;
        int blocks = run > 0 ? total / run : 0;
        var index = new int[Math.Max(rank, 1)];
        int offset = baseOffset;

        if (x.IsFloat)
        {
            var result = Tensor.AllocateFloat(shape);
            for (int b = 0; b < blocks; b++)
            {
                x.Floats.AsSpan(offset, run).CopyTo(result.Floats.AsSpan(b * run, run));
                Advance(shape, strides, index, rank, run, ref offset);
            }
            return result;
        }
        else
        {
            var result = Tensor.AllocateLong(x.Type, shape);
            for (int b = 0; b < blocks; b++)
            {
                x.Longs.AsSpan(offset, run).CopyTo(result.Longs.AsSpan(b * run, run));
                Advance(shape, strides, index, rank, run, ref offset);
            }
            return result;
        }
    }

    /// <summary>
    /// Gather: index <paramref name="axis"/> of <paramref name="x"/> with an arbitrary-rank
    /// index tensor, splicing the index shape into that axis' place.
    /// </summary>
    public static Tensor Gather(Tensor x, Tensor indices, long axis)
    {
        int a = NormalizeAxis(axis, x.Rank);
        int dim = x.Shape[a];

        int outer = 1;
        for (int i = 0; i < a; i++) outer *= x.Shape[i];
        int inner = 1;
        for (int i = a + 1; i < x.Rank; i++) inner *= x.Shape[i];

        var shape = new List<int>(x.Rank + indices.Rank - 1);
        for (int i = 0; i < a; i++) shape.Add(x.Shape[i]);
        shape.AddRange(indices.Shape);
        for (int i = a + 1; i < x.Rank; i++) shape.Add(x.Shape[i]);

        int k = indices.Count;
        var result = x.IsFloat
            ? Tensor.AllocateFloat(shape.ToArray())
            : Tensor.AllocateLong(x.Type, shape.ToArray());

        for (int o = 0; o < outer; o++)
        {
            for (int j = 0; j < k; j++)
            {
                long raw = indices.GetLong(j);
                int idx = (int)(raw < 0 ? raw + dim : raw);
                if (idx < 0 || idx >= dim) throw new InvalidDataException($"gather: index {raw} out of range for dim {dim}");
                int srcPos = (o * dim + idx) * inner;
                int dstPos = (o * k + j) * inner;
                if (x.IsFloat)
                    x.Floats.AsSpan(srcPos, inner).CopyTo(result.Floats.AsSpan(dstPos, inner));
                else
                    x.Longs.AsSpan(srcPos, inner).CopyTo(result.Longs.AsSpan(dstPos, inner));
            }
        }
        return result;
    }

    /// <summary>
    /// GatherElements: unlike <see cref="Gather"/>, the index tensor has the same rank as the
    /// data and picks one element per output position along <paramref name="axis"/>.
    /// </summary>
    public static Tensor GatherElements(Tensor x, Tensor indices, long axis)
    {
        int a = NormalizeAxis(axis, x.Rank);
        int dim = x.Shape[a];
        var shape = indices.Shape;
        int total = indices.Count;
        var srcStrides = x.Strides();
        var idxStrides = indices.Strides();

        var result = x.IsFloat
            ? Tensor.AllocateFloat(shape)
            : Tensor.AllocateLong(x.Type, shape);

        var index = new int[Math.Max(shape.Length, 1)];
        for (int flat = 0; flat < total; flat++)
        {
            // Decompose the flat output position into per-axis coordinates.
            int remainder = flat;
            for (int d = 0; d < shape.Length; d++)
            {
                index[d] = remainder / idxStrides[d];
                remainder -= index[d] * idxStrides[d];
            }

            long raw = indices.GetLong(flat);
            int pick = (int)(raw < 0 ? raw + dim : raw);
            if (pick < 0 || pick >= dim)
                throw new InvalidDataException($"gather_elements: index {raw} out of range for dim {dim}");

            int srcPos = 0;
            for (int d = 0; d < shape.Length; d++) srcPos += (d == a ? pick : index[d]) * srcStrides[d];

            if (x.IsFloat) result.Floats[flat] = x.Floats[srcPos];
            else result.Longs[flat] = x.Longs[srcPos];
        }
        return result;
    }

    /// <summary>Broadcast to a larger shape by materialising the repeats.</summary>
    public static Tensor Expand(Tensor x, Tensor shapeTensor)
    {
        var target = Broadcast.ResultShape(x.Shape, shapeTensor.ToIntArray());
        var plan = Broadcast.MakePlan(x.Shape, target);
        var result = x.IsFloat ? Tensor.AllocateFloat(target) : Tensor.AllocateLong(x.Type, target);
        int run = plan.BlockLength;

        Broadcast.ForEachBlock(plan, (oa, _, od) =>
        {
            if (x.IsFloat)
            {
                if (x.Count == 1) result.Floats.AsSpan(od, run).Fill(x.Floats[0]);
                else x.Floats.AsSpan(oa, run).CopyTo(result.Floats.AsSpan(od, run));
            }
            else
            {
                if (x.Count == 1) result.Longs.AsSpan(od, run).Fill(x.Longs[0]);
                else x.Longs.AsSpan(oa, run).CopyTo(result.Longs.AsSpan(od, run));
            }
        });
        return result;
    }

    /// <summary>Repeat the tensor <c>repeats[i]</c> times along each axis.</summary>
    public static Tensor Tile(Tensor x, Tensor repeatsTensor)
    {
        var repeats = repeatsTensor.ToIntArray();
        if (repeats.Length != x.Rank) throw new InvalidDataException("tile: repeats length must match rank");

        var shape = new int[x.Rank];
        for (int i = 0; i < x.Rank; i++) shape[i] = x.Shape[i] * repeats[i];

        var result = x.IsFloat ? Tensor.AllocateFloat(shape) : Tensor.AllocateLong(x.Type, shape);
        var srcStrides = x.Strides();
        var dstStrides = result.Strides();
        int total = result.Count;
        var index = new int[Math.Max(x.Rank, 1)];

        for (int flat = 0; flat < total; flat++)
        {
            int remainder = flat, srcPos = 0;
            for (int d = 0; d < x.Rank; d++)
            {
                index[d] = remainder / dstStrides[d];
                remainder -= index[d] * dstStrides[d];
                srcPos += (index[d] % x.Shape[d]) * srcStrides[d];
            }
            if (x.IsFloat) result.Floats[flat] = x.Floats[srcPos];
            else result.Longs[flat] = x.Longs[srcPos];
        }
        return result;
    }

    /// <summary>A tensor of the given shape filled with the node's <c>value</c> attribute.</summary>
    public static Tensor ConstantOfShape(Tensor shapeTensor, Tensor? value)
    {
        var shape = shapeTensor.ToIntArray();
        // Always fill: allocation does not zero, so a zero fill value still has to be written.
        if (value is null || value.IsFloat)
        {
            var result = Tensor.AllocateFloat(shape);
            result.Floats.AsSpan().Fill(value is null ? 0f : value.Floats[0]);
            return result;
        }
        else
        {
            var result = Tensor.AllocateLong(value.Type, shape);
            result.Longs.AsSpan().Fill(value.Longs[0]);
            return result;
        }
    }

    /// <summary>
    /// Split along an axis into the given sizes, or into equal parts when no sizes are given.
    /// </summary>
    public static Tensor[] Split(Tensor x, long axis, int[]? sizes, int outputCount)
    {
        int a = NormalizeAxis(axis, x.Rank);
        int dim = x.Shape[a];

        if (sizes is null)
        {
            if (outputCount <= 0) throw new InvalidDataException("split: no split sizes and no output count");
            // ONNX allows an uneven final chunk when the axis does not divide evenly.
            int chunk = (dim + outputCount - 1) / outputCount;
            sizes = new int[outputCount];
            int left = dim;
            for (int i = 0; i < outputCount; i++)
            {
                sizes[i] = Math.Min(chunk, Math.Max(left, 0));
                left -= sizes[i];
            }
        }

        int outer = 1;
        for (int i = 0; i < a; i++) outer *= x.Shape[i];
        int inner = 1;
        for (int i = a + 1; i < x.Rank; i++) inner *= x.Shape[i];

        var results = new Tensor[sizes.Length];
        int axisOffset = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            var shape = (int[])x.Shape.Clone();
            shape[a] = sizes[i];
            var part = x.IsFloat ? Tensor.AllocateFloat(shape) : Tensor.AllocateLong(x.Type, shape);
            int chunk = sizes[i] * inner;
            for (int o = 0; o < outer; o++)
            {
                int srcPos = o * dim * inner + axisOffset * inner;
                int dstPos = o * chunk;
                if (chunk == 0) continue;
                if (x.IsFloat) x.Floats.AsSpan(srcPos, chunk).CopyTo(part.Floats.AsSpan(dstPos, chunk));
                else x.Longs.AsSpan(srcPos, chunk).CopyTo(part.Longs.AsSpan(dstPos, chunk));
            }
            results[i] = part;
            axisOffset += sizes[i];
        }
        return results;
    }
}
