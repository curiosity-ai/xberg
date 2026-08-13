using System.Numerics.Tensors;

namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// Matrix multiplication — the arithmetic core of the transformer half of these graphs.
/// <para>
/// The inner kernel is written in <em>axpy</em> form: for each output row, accumulate
/// <c>C[i,:] += A[i,k] * B[k,:]</c> across k. That ordering walks both <c>B</c> and <c>C</c>
/// forwards through memory in row-major order and turns the innermost work into a single
/// fused multiply-add over a contiguous span, which
/// <c>TensorPrimitives.MultiplyAdd</c> maps onto the widest SIMD width available. The more
/// obvious dot-product ordering would instead stride down <c>B</c>'s columns and miss the
/// cache on every access.
/// </para>
/// </summary>
internal static class Linear
{
    /// <summary>Output elements above which the row loop is worth handing to the thread pool.</summary>
    private const long ParallelThreshold = 1L << 18;

    /// <summary>
    /// ONNX MatMul, including the 1-D promotions and leading-dimension broadcasting.
    /// </summary>
    public static Tensor MatMul(Tensor a, Tensor b)
    {
        var fa = a.AsFloat();
        var fb = b.AsFloat();

        // A 1-D operand is promoted to a matrix for the multiply, then the added dimension
        // is removed again — exactly the numpy rule ONNX inherits.
        bool promotedA = fa.Rank == 1;
        bool promotedB = fb.Rank == 1;
        if (promotedA) fa = fa.Reshaped(1, fa.Shape[0]);
        if (promotedB) fb = fb.Reshaped(fb.Shape[0], 1);

        int m = fa.Shape[^2], k = fa.Shape[^1];
        int k2 = fb.Shape[^2], n = fb.Shape[^1];
        if (k != k2)
            throw new InvalidDataException($"matmul: inner dimensions differ ({k} vs {k2})");

        // Broadcast the batch dimensions that precede the two matrix axes.
        var batchA = fa.Shape[..^2];
        var batchB = fb.Shape[..^2];
        var batchShape = Broadcast.ResultShape(batchA, batchB);
        int batchRank = batchShape.Length;
        int batches = Tensor.ElementCount(batchShape);
        var strideA = Broadcast.StridesFor(batchA, batchRank);
        var strideB = Broadcast.StridesFor(batchB, batchRank);

        var outShape = new int[batchRank + 2];
        batchShape.CopyTo(outShape, 0);
        outShape[batchRank] = m;
        outShape[batchRank + 1] = n;
        var result = Tensor.AllocateFloat(outShape);

        int sizeA = m * k, sizeB = k * n, sizeC = m * n;
        var index = new int[Math.Max(batchRank, 1)];
        int offsetA = 0, offsetB = 0;

        for (int batch = 0; batch < batches; batch++)
        {
            MultiplyInto(
                fa.Floats.AsMemory(offsetA * sizeA, sizeA),
                fb.Floats.AsMemory(offsetB * sizeB, sizeB),
                result.Floats.AsMemory(batch * sizeC, sizeC),
                m, k, n);

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

        // Undo the 1-D promotions on the result.
        if (promotedA && promotedB) return result.Reshaped();
        if (promotedA)
        {
            var shape = outShape.Where((_, i) => i != batchRank).ToArray();
            return result.Reshaped(shape);
        }
        if (promotedB) return result.Reshaped(outShape[..^1]);
        return result;
    }

    /// <summary>
    /// One <c>[m,k] x [k,n] -> [m,n]</c> product, accumulating into a zeroed destination.
    /// </summary>
    public static void MultiplyInto(
        ReadOnlyMemory<float> a, ReadOnlyMemory<float> b, Memory<float> c, int m, int k, int n)
    {
        c.Span.Clear();
        long work = (long)m * n * k;

        if (work >= ParallelThreshold && m > 1)
        {
            Parallel.For(0, m, i => AccumulateRow(a.Span, b.Span, c.Span, i, k, n));
            return;
        }
        for (int i = 0; i < m; i++) AccumulateRow(a.Span, b.Span, c.Span, i, k, n);
    }

    private static void AccumulateRow(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c, int i, int k, int n)
    {
        var row = c.Slice(i * n, n);
        int aBase = i * k;
        for (int p = 0; p < k; p++)
        {
            float scale = a[aBase + p];
            // Skipping zeros is a real win on attention masks and padded projections, where
            // whole rows of the operand are exactly zero.
            if (scale == 0f) continue;
            TensorPrimitives.MultiplyAdd(b.Slice(p * n, n), scale, row, row);
        }
    }

    /// <summary>
    /// Gemm: <c>alpha * A' * B' + beta * C</c>, with optional transposes and a broadcast bias.
    /// </summary>
    public static Tensor Gemm(Tensor a, Tensor b, Tensor? c, float alpha, float beta, bool transA, bool transB)
    {
        var fa = a.AsFloat();
        var fb = b.AsFloat();
        if (fa.Rank != 2 || fb.Rank != 2) throw new InvalidDataException("gemm: A and B must be rank 2");

        if (transA) fa = Shapes.Transpose(fa, [1, 0]);
        if (transB) fb = Shapes.Transpose(fb, [1, 0]);

        int m = fa.Shape[0], k = fa.Shape[1], n = fb.Shape[1];
        if (fb.Shape[0] != k) throw new InvalidDataException($"gemm: inner dimensions differ ({k} vs {fb.Shape[0]})");

        var result = Tensor.AllocateFloat(m, n);
        MultiplyInto(fa.Floats, fb.Floats, result.Floats, m, k, n);

        if (alpha != 1f) TensorPrimitives.Multiply(result.Floats, alpha, result.Floats);

        if (c is not null && beta != 0f)
        {
            var bias = c.AsFloat();
            var scaled = beta == 1f ? bias : Elementwise.Binary(bias, Tensor.Scalar(beta), BinaryKind.Mul);
            return Elementwise.Binary(result, scaled, BinaryKind.Add);
        }
        return result;
    }
}
