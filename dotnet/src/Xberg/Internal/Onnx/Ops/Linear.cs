using System.Numerics.Tensors;
using System.Numerics;

namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// Matrix multiplication — the arithmetic core of both halves of these graphs, since
/// convolution is lowered onto it too.
/// <para>
/// The kernel is register-blocked: four output rows are computed together across two
/// vector-wide column strips, with the reduction innermost so eight accumulators stay in
/// SIMD registers for the whole length of <c>k</c> and the destination is written exactly
/// once. Each step loads two vectors from the right-hand operand and spends them on eight
/// multiply-adds.
/// </para>
/// <para>
/// The ordering is chosen for memory traffic rather than instruction count. Computing one
/// row at a time re-reads the entire right-hand operand once per output row; for a deep
/// convolution lowered to a 2304x6400 product that is 59 MB re-read 32 times, and the
/// multiply becomes bandwidth-bound long before it becomes arithmetic-bound. The dot-product
/// ordering is worse still — it strides down columns and misses cache on every access.
/// </para>
/// </summary>
internal static class Linear
{
    /// <summary>Multiply-accumulate operations above which the work is worth spreading across cores.</summary>
    private const long ParallelThreshold = 1L << 18;

    /// <summary>
    /// Output rows computed together by the register-blocked kernel.
    /// <para>
    /// Four rows against two vector-wide column strips keeps eight accumulators live, which
    /// with two operand vectors and four broadcast scalars fits the sixteen architectural
    /// SIMD registers of every target. It is also what bounds memory traffic: the naive
    /// one-row-at-a-time form re-reads the entire right-hand operand once per output row,
    /// and for a deep convolution lowered to a 2304x6400 product that is 59 MB re-read 32
    /// times. Four rows per pass cuts that fourfold, and the two-vector strip means each
    /// 64-byte cache line fetched from it is consumed whole rather than half.
    /// </para>
    /// </summary>
    private const int RowBlock = 4;

    /// <summary>
    /// Columns and depth per cache panel.
    /// <para>
    /// Register blocking alone is not enough: with the row loop outermost, every row block
    /// streams the entire right-hand operand from memory, and for a 256x256x25600 layer that
    /// is 26 MB pulled 64 times — about 1.7 GB, which at realistic bandwidth accounts for
    /// the whole measured runtime. Iterating panels on the outside instead keeps a
    /// <c>DepthPanel x ColumnPanel</c> slab of that operand resident in L2 while every row
    /// block consumes it, so it crosses the memory bus once. At 128x256 floats the slab is
    /// 128 KB, which stays comfortable when four cores share the level.
    /// </para>
    /// </summary>
    private const int ColumnPanel = 256;

    /// <inheritdoc cref="ColumnPanel"/>
    private const int DepthPanel = 128;

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
        if (m == 0 || n == 0 || k == 0) return;

        long work = (long)m * n * k;
        int width = Vector<float>.Count;

        // Column panels are the unit of parallelism: they partition the destination, so
        // threads never touch the same output, and each operand slab is streamed by exactly
        // one thread. Narrow the panel when the matrix is small so there is still enough of
        // it to go around, keeping the width a whole number of vectors.
        int panel = Math.Min(ColumnPanel, Math.Max(width, RoundUp(
            (n + 2 * Environment.ProcessorCount - 1) / (2 * Environment.ProcessorCount), width)));
        int panels = (n + panel - 1) / panel;

        if (work >= ParallelThreshold && panels > 1)
            Parallel.For(0, panels, index => ComputeColumnPanel(a.Span, b.Span, c.Span, m, k, n, index * panel, Math.Min(panel, n - index * panel)));
        else
            for (int index = 0; index < panels; index++)
                ComputeColumnPanel(a.Span, b.Span, c.Span, m, k, n, index * panel, Math.Min(panel, n - index * panel));
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// Compute one full column panel of the destination: every depth panel, and within each,
    /// every row block. Ordering depth outside the rows is what earns the cache reuse — the
    /// operand slab stays resident while all the rows consume it.
    /// </summary>
    private static void ComputeColumnPanel(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c, int m, int k, int n, int jc, int columns)
    {
        int blocks = (m + RowBlock - 1) / RowBlock;
        for (int pc = 0; pc < k; pc += DepthPanel)
        {
            int depth = Math.Min(DepthPanel, k - pc);
            for (int block = 0; block < blocks; block++)
                ComputePanel(a, b, c, m, k, n, block * RowBlock, pc, depth, jc, columns);
        }
    }

    /// <summary>
    /// Accumulate one panel of the product into up to <see cref="RowBlock"/> output rows:
    /// <c>C[i0.., jc..jc+columns] += A[i0.., pc..pc+depth] * B[pc..pc+depth, jc..jc+columns]</c>.
    /// </summary>
    private static void ComputePanel(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
        int m, int k, int n, int i0, int pc, int depth, int jc, int columns)
    {
        int rows = Math.Min(RowBlock, m - i0);
        if (rows == RowBlock) ComputeFourRows(a, b, c, k, n, i0, pc, depth, jc, columns);
        else for (int i = i0; i < i0 + rows; i++) AccumulateRow(a, b, c, i, k, n, pc, depth, jc, columns);
    }

    /// <summary>
    /// The register-blocked kernel: four output rows across two vector-wide column strips.
    /// <para>
    /// The column strip is the outer loop and the reduction is innermost, so all eight
    /// accumulators stay in registers for the whole length of <c>k</c> and the destination is
    /// written exactly once. Each iteration loads two vectors from the right-hand operand and
    /// spends them on eight fused multiply-adds.
    /// </para>
    /// </summary>
    private static void ComputeFourRows(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
        int k, int n, int i0, int pc, int depth, int jc, int columns)
    {
        int width = Vector<float>.Count;
        int a0 = i0 * k + pc, a1 = a0 + k, a2 = a1 + k, a3 = a2 + k;
        int c0 = i0 * n, c1 = c0 + n, c2 = c1 + n, c3 = c2 + n;
        int end = jc + columns;
        int j = jc;

        for (; j + 2 * width <= end; j += 2 * width)
        {
            // Accumulators start from C, not zero: a panel adds its share of the reduction to
            // whatever earlier depth panels already contributed.
            var acc00 = new Vector<float>(c.Slice(c0 + j, width));
            var acc01 = new Vector<float>(c.Slice(c0 + j + width, width));
            var acc10 = new Vector<float>(c.Slice(c1 + j, width));
            var acc11 = new Vector<float>(c.Slice(c1 + j + width, width));
            var acc20 = new Vector<float>(c.Slice(c2 + j, width));
            var acc21 = new Vector<float>(c.Slice(c2 + j + width, width));
            var acc30 = new Vector<float>(c.Slice(c3 + j, width));
            var acc31 = new Vector<float>(c.Slice(c3 + j + width, width));

            for (int p = 0; p < depth; p++)
            {
                int bRow = (pc + p) * n + j;
                var b0 = new Vector<float>(b.Slice(bRow, width));
                var b1 = new Vector<float>(b.Slice(bRow + width, width));

                var s0 = new Vector<float>(a[a0 + p]);
                var s1 = new Vector<float>(a[a1 + p]);
                var s2 = new Vector<float>(a[a2 + p]);
                var s3 = new Vector<float>(a[a3 + p]);

                acc00 += s0 * b0; acc01 += s0 * b1;
                acc10 += s1 * b0; acc11 += s1 * b1;
                acc20 += s2 * b0; acc21 += s2 * b1;
                acc30 += s3 * b0; acc31 += s3 * b1;
            }

            acc00.CopyTo(c.Slice(c0 + j, width)); acc01.CopyTo(c.Slice(c0 + j + width, width));
            acc10.CopyTo(c.Slice(c1 + j, width)); acc11.CopyTo(c.Slice(c1 + j + width, width));
            acc20.CopyTo(c.Slice(c2 + j, width)); acc21.CopyTo(c.Slice(c2 + j + width, width));
            acc30.CopyTo(c.Slice(c3 + j, width)); acc31.CopyTo(c.Slice(c3 + j + width, width));
        }

        // One vector-wide strip, then whatever scalar tail is left.
        for (; j + width <= end; j += width)
        {
            var acc0 = new Vector<float>(c.Slice(c0 + j, width));
            var acc1 = new Vector<float>(c.Slice(c1 + j, width));
            var acc2 = new Vector<float>(c.Slice(c2 + j, width));
            var acc3 = new Vector<float>(c.Slice(c3 + j, width));
            for (int p = 0; p < depth; p++)
            {
                var bv = new Vector<float>(b.Slice((pc + p) * n + j, width));
                acc0 += new Vector<float>(a[a0 + p]) * bv;
                acc1 += new Vector<float>(a[a1 + p]) * bv;
                acc2 += new Vector<float>(a[a2 + p]) * bv;
                acc3 += new Vector<float>(a[a3 + p]) * bv;
            }
            acc0.CopyTo(c.Slice(c0 + j, width));
            acc1.CopyTo(c.Slice(c1 + j, width));
            acc2.CopyTo(c.Slice(c2 + j, width));
            acc3.CopyTo(c.Slice(c3 + j, width));
        }

        for (; j < end; j++)
        {
            float sum0 = c[c0 + j], sum1 = c[c1 + j], sum2 = c[c2 + j], sum3 = c[c3 + j];
            for (int p = 0; p < depth; p++)
            {
                float bv = b[(pc + p) * n + j];
                sum0 += a[a0 + p] * bv;
                sum1 += a[a1 + p] * bv;
                sum2 += a[a2 + p] * bv;
                sum3 += a[a3 + p] * bv;
            }
            c[c0 + j] = sum0;
            c[c1 + j] = sum1;
            c[c2 + j] = sum2;
            c[c3 + j] = sum3;
        }
    }

    /// <summary>Single-row fallback for the rows left over when <c>m</c> is not a multiple of
    /// <see cref="RowBlock"/>.</summary>
    private static void AccumulateRow(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
        int i, int k, int n, int pc, int depth, int jc, int columns)
    {
        var row = c.Slice(i * n + jc, columns);
        int aBase = i * k + pc;
        for (int p = 0; p < depth; p++)
        {
            float scale = a[aBase + p];
            // Skipping zeros is a real win on attention masks and padded projections, where
            // whole rows of the operand are exactly zero.
            if (scale == 0f) continue;
            TensorPrimitives.MultiplyAdd(b.Slice((pc + p) * n + jc, columns), scale, row, row);
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
