namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// Multi-dimensional gather and scatter kernels: ONNX <c>GatherND</c>, <c>ScatterND</c> and
/// <c>EyeLike</c>.
/// </summary>
/// <remarks>
/// These appear where a graph indexes with a computed coordinate tensor rather than a slice —
/// in a detection export, that is the head selecting top-k proposals by index.
/// </remarks>
internal static class Indexing
{
    /// <summary>Row-major strides for a shape.</summary>
    private static int[] Strides(ReadOnlySpan<int> shape)
    {
        var strides = new int[shape.Length];
        int acc = 1;
        for (int i = shape.Length - 1; i >= 0; i--) { strides[i] = acc; acc *= shape[i]; }
        return strides;
    }

    private static Tensor Allocate(Tensor like, int[] shape) =>
        like.IsFloat ? Tensor.AllocateFloat(shape) : Tensor.AllocateLong(like.Type, shape);

    private static void Copy(Tensor source, int from, Tensor destination, int to, int count)
    {
        if (source.IsFloat) Array.Copy(source.Floats, from, destination.Floats, to, count);
        else Array.Copy(source.Longs, from, destination.Longs, to, count);
    }

    /// <summary>
    /// ONNX <c>GatherND</c>: index <paramref name="data"/> with coordinate tuples from
    /// <paramref name="indices"/>, whose last dimension holds one coordinate each.
    /// </summary>
    /// <remarks>
    /// The leading <paramref name="batchDims"/> dimensions are matched positionally between the
    /// two rather than indexed, so batch element <c>b</c> of the indices only ever reads batch
    /// element <c>b</c> of the data. A negative coordinate counts from the end of its dimension,
    /// as everywhere else in ONNX.
    /// </remarks>
    public static Tensor GatherND(Tensor data, Tensor indices, int batchDims)
    {
        int r = data.Shape.Length;
        int q = indices.Shape.Length;
        int k = indices.Shape[q - 1];
        if (k + batchDims > r)
            throw new InvalidDataException(
                $"GatherND: index tuples of length {k} with {batchDims} batch dims exceed data rank {r}");

        int batchCount = 1;
        for (int i = 0; i < batchDims; i++) batchCount *= data.Shape[i];

        // Each gathered coordinate addresses one contiguous slice of this length.
        int sliceLength = 1;
        for (int i = batchDims + k; i < r; i++) sliceLength *= data.Shape[i];

        // Index tuples per batch element.
        int tuplesPerBatch = 1;
        for (int i = batchDims; i < q - 1; i++) tuplesPerBatch *= indices.Shape[i];

        var shape = new List<int>();
        for (int i = 0; i < q - 1; i++) shape.Add(indices.Shape[i]);
        for (int i = batchDims + k; i < r; i++) shape.Add(data.Shape[i]);
        var result = Allocate(data, shape.Count == 0 ? [1] : shape.ToArray());

        var dataStrides = Strides(data.Shape);
        int dataBatchStride = batchCount == 0 ? 0 : data.Count / Math.Max(batchCount, 1);

        int outPosition = 0;
        int tuple = 0;
        for (int b = 0; b < Math.Max(batchCount, 1); b++)
        {
            for (int t = 0; t < tuplesPerBatch; t++, tuple++)
            {
                int offset = b * dataBatchStride;
                for (int c = 0; c < k; c++)
                {
                    int dimension = data.Shape[batchDims + c];
                    long coordinate = indices.GetLong(tuple * k + c);
                    if (coordinate < 0) coordinate += dimension;
                    if (coordinate < 0 || coordinate >= dimension)
                        throw new InvalidDataException(
                            $"GatherND: index {coordinate} out of range for dimension {dimension}");
                    offset += (int)coordinate * dataStrides[batchDims + c];
                }
                Copy(data, offset, result, outPosition, sliceLength);
                outPosition += sliceLength;
            }
        }

        return result;
    }

    /// <summary>
    /// ONNX <c>ScatterND</c>: a copy of <paramref name="data"/> with the slices named by
    /// <paramref name="indices"/> replaced by <paramref name="updates"/>.
    /// </summary>
    /// <remarks>
    /// Duplicate indices are left to write in order, last one winning — the ONNX spec calls the
    /// result undefined in that case rather than an error, so this must not throw.
    /// </remarks>
    public static Tensor ScatterND(Tensor data, Tensor indices, Tensor updates)
    {
        var result = Allocate(data, data.Shape);
        Copy(data, 0, result, 0, data.Count);

        int q = indices.Shape.Length;
        int k = indices.Shape[q - 1];
        int tuples = 1;
        for (int i = 0; i < q - 1; i++) tuples *= indices.Shape[i];

        int sliceLength = 1;
        for (int i = k; i < data.Shape.Length; i++) sliceLength *= data.Shape[i];

        var dataStrides = Strides(data.Shape);
        for (int t = 0; t < tuples; t++)
        {
            int offset = 0;
            for (int c = 0; c < k; c++)
            {
                int dimension = data.Shape[c];
                long coordinate = indices.GetLong(t * k + c);
                if (coordinate < 0) coordinate += dimension;
                offset += (int)coordinate * dataStrides[c];
            }
            Copy(updates, t * sliceLength, result, offset, sliceLength);
        }

        return result;
    }

    /// <summary>
    /// ONNX <c>EyeLike</c>: a 2-D tensor shaped like <paramref name="like"/> with ones on the
    /// <paramref name="k"/>th diagonal.
    /// </summary>
    /// <remarks>
    /// Only the input's <em>shape</em> is used; its values are ignored entirely.
    /// </remarks>
    public static Tensor EyeLike(Tensor like, int k, ElementType? dtype)
    {
        if (like.Shape.Length != 2)
            throw new InvalidDataException($"EyeLike expects a 2-D input, got rank {like.Shape.Length}");

        var type = dtype ?? like.Type;
        int rows = like.Shape[0], columns = like.Shape[1];
        bool isFloat = type is ElementType.Float or ElementType.Double or ElementType.Float16;
        var result = isFloat
            ? Tensor.AllocateFloat(rows, columns)
            : Tensor.AllocateLong(type, rows, columns);

        for (int row = 0; row < rows; row++)
        {
            int column = row + k;
            if (column < 0 || column >= columns) continue;
            if (isFloat) result.Floats[row * columns + column] = 1f;
            else result.Longs[row * columns + column] = 1;
        }
        return result;
    }

    /// <summary>
    /// ONNX <c>OneHot</c>: expand each index in <paramref name="indices"/> into a vector along a
    /// new axis, carrying the on-value at that position and the off-value elsewhere.
    /// </summary>
    /// <remarks>
    /// <paramref name="values"/> is a two-element tensor holding <c>[off, on]</c> in that order.
    /// A negative index counts from the end of the new axis, and an index outside the axis
    /// produces an all-off vector rather than an error — that is what the spec requires, and it
    /// is what lets a padded sequence position encode as nothing.
    /// </remarks>
    public static Tensor OneHot(Tensor indices, Tensor depth, Tensor values, int axis)
    {
        int size = (int)depth.GetLong(0);
        if (size <= 0) throw new InvalidDataException($"OneHot: depth {size} is not positive");

        int rank = indices.Shape.Length + 1;
        if (axis < 0) axis += rank;
        if (axis < 0 || axis >= rank)
            throw new InvalidDataException($"OneHot: axis {axis} is out of range for rank {rank}");

        var shape = new int[rank];
        for (int i = 0, j = 0; i < rank; i++) shape[i] = i == axis ? size : indices.Shape[j++];

        bool isFloat = values.IsFloat;
        var result = isFloat ? Tensor.AllocateFloat(shape) : Tensor.AllocateLong(values.Type, shape);

        // Everything ahead of the new axis, and everything behind it, stay contiguous around it.
        int inner = 1;
        for (int d = axis + 1; d < rank; d++) inner *= shape[d];
        int outer = indices.Count / Math.Max(inner, 1);

        if (isFloat) Array.Fill(result.Floats, values.GetFloat(0));
        else Array.Fill(result.Longs, values.GetLong(0));

        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                long index = indices.GetLong(o * inner + i);
                if (index < 0) index += size;
                if (index < 0 || index >= size) continue;
                int offset = (o * size + (int)index) * inner + i;
                if (isFloat) result.Floats[offset] = values.GetFloat(1);
                else result.Longs[offset] = values.GetLong(1);
            }
        }

        return result;
    }

    /// <summary>
    /// ONNX <c>ScatterElements</c>: a copy of <paramref name="data"/> with individual elements
    /// replaced, each index naming a position along <paramref name="axis"/> only.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ScatterND"/>, an index here replaces a single element rather than a
    /// slice: the other coordinates come from the index tensor's own position.
    /// </remarks>
    public static Tensor ScatterElements(Tensor data, Tensor indices, Tensor updates, int axis)
    {
        int rank = data.Shape.Length;
        if (axis < 0) axis += rank;
        if (axis < 0 || axis >= rank)
            throw new InvalidDataException($"ScatterElements: axis {axis} is out of range for rank {rank}");

        var result = Allocate(data, data.Shape);
        Copy(data, 0, result, 0, data.Count);

        var dataStrides = Strides(data.Shape);
        var indexStrides = Strides(indices.Shape);
        var position = new int[rank];

        for (int flat = 0; flat < indices.Count; flat++)
        {
            for (int d = 0; d < rank; d++) position[d] = flat / indexStrides[d] % indices.Shape[d];

            long along = indices.GetLong(flat);
            if (along < 0) along += data.Shape[axis];
            if (along < 0 || along >= data.Shape[axis])
                throw new InvalidDataException(
                    $"ScatterElements: index {along} out of range for dimension {data.Shape[axis]}");

            int offset = 0;
            for (int d = 0; d < rank; d++) offset += (d == axis ? (int)along : position[d]) * dataStrides[d];
            Copy(updates, flat, result, offset, 1);
        }

        return result;
    }
}
