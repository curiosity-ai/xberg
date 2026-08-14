namespace Xberg.Internal.Onnx;

/// <summary>
/// Numpy-style broadcasting shared by every binary elementwise kernel.
/// <para>
/// The interesting part is the split into <em>blocks</em>. A naive broadcast loop
/// recomputes a multi-dimensional index per element and defeats vectorisation entirely.
/// In practice the operands in these graphs broadcast on the <em>outer</em> dims — a
/// per-channel bias added to <c>[N,C,H,W]</c>, a scalar scale, a <c>[1,1,D]</c> positional
/// term — leaving a run of trailing dimensions where both sides advance in lockstep.
/// Finding that run lets the kernel emit whole vectorised spans and pay index arithmetic
/// only once per block.
/// </para>
/// </summary>
internal static class Broadcast
{
    /// <summary>The broadcast result shape, or throws when the shapes are incompatible.</summary>
    public static int[] ResultShape(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        int rank = Math.Max(a.Length, b.Length);
        var shape = new int[rank];
        for (int i = 0; i < rank; i++)
        {
            int da = Dim(a, rank, i);
            int db = Dim(b, rank, i);
            if (da != db && da != 1 && db != 1)
                throw new InvalidDataException(
                    $"cannot broadcast [{string.Join(",", a.ToArray())}] with [{string.Join(",", b.ToArray())}]");
            shape[i] = Math.Max(da, db);
        }
        return shape;
    }

    /// <summary>Dimension <paramref name="i"/> of <paramref name="shape"/> right-aligned into
    /// <paramref name="rank"/>; missing leading dimensions read as 1.</summary>
    private static int Dim(ReadOnlySpan<int> shape, int rank, int i)
    {
        int offset = rank - shape.Length;
        return i < offset ? 1 : shape[i - offset];
    }

    /// <summary>
    /// Strides for <paramref name="shape"/> when read through a broadcast to a tensor of
    /// rank <paramref name="rank"/>: a broadcast dimension gets stride 0, so incrementing
    /// that index re-reads the same element.
    /// </summary>
    public static int[] StridesFor(ReadOnlySpan<int> shape, int rank)
    {
        var strides = new int[rank];
        int acc = 1;
        for (int i = shape.Length - 1; i >= 0; i--)
        {
            strides[i + rank - shape.Length] = shape[i] == 1 ? 0 : acc;
            acc *= shape[i];
        }
        return strides;
    }

    /// <summary>
    /// How the iteration space splits: the last <c>InnerDims</c> dimensions form a
    /// contiguous, unbroadcast block of <c>BlockLength</c> elements that a kernel can hand
    /// to a vectorised primitive in one call.
    /// </summary>
    public readonly record struct Plan(int[] Shape, int[] StrideA, int[] StrideB, int InnerDims, int BlockLength)
    {
        public int Total => Tensor.ElementCount(Shape);
        public int BlockCount => BlockLength > 0 ? Total / BlockLength : 0;
        /// <summary>True when both operands already match the output exactly, element for
        /// element — the case worth a straight whole-array vector call.</summary>
        public bool IsFlat => BlockLength == Total;
    }

    public static Plan MakePlan(ReadOnlySpan<int> shapeA, ReadOnlySpan<int> shapeB)
    {
        int[] shape = ResultShape(shapeA, shapeB);
        int rank = shape.Length;
        int[] strideA = StridesFor(shapeA, rank);
        int[] strideB = StridesFor(shapeB, rank);

        // Walk in from the right while both operands stay contiguous with the output.
        int block = 1, innerDims = 0;
        for (int i = rank - 1; i >= 0; i--)
        {
            if (strideA[i] != block || strideB[i] != block) break;
            block *= shape[i];
            innerDims++;
        }
        // A rank-0 or fully broadcast operand leaves no run; one element per block still works.
        if (innerDims == 0) block = 1;

        return new Plan(shape, strideA, strideB, innerDims, block);
    }

    /// <summary>
    /// Invoke <paramref name="block"/> once per contiguous block with the flat offsets into
    /// operand A, operand B and the output. Output offsets advance linearly because the
    /// destination is never broadcast.
    /// </summary>
    public static void ForEachBlock(in Plan plan, Action<int, int, int> block)
    {
        int outerRank = plan.Shape.Length - plan.InnerDims;
        int blocks = plan.BlockCount;
        var index = new int[Math.Max(outerRank, 1)];
        int offsetA = 0, offsetB = 0;

        for (int b = 0; b < blocks; b++)
        {
            block(offsetA, offsetB, b * plan.BlockLength);

            // Odometer over the outer dimensions only; the inner run is handled by the block.
            for (int d = outerRank - 1; d >= 0; d--)
            {
                index[d]++;
                offsetA += plan.StrideA[d];
                offsetB += plan.StrideB[d];
                if (index[d] < plan.Shape[d]) break;
                offsetA -= plan.StrideA[d] * index[d];
                offsetB -= plan.StrideB[d] * index[d];
                index[d] = 0;
            }
        }
    }
}
