using System.Numerics.Tensors;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

        // Resolve each batch's operand offsets up front so the batches can be dispatched in
        // any order.
        var offsetsA = new int[batches];
        var offsetsB = new int[batches];
        {
            var index = new int[Math.Max(batchRank, 1)];
            int offsetA = 0, offsetB = 0;
            for (int batch = 0; batch < batches; batch++)
            {
                offsetsA[batch] = offsetA;
                offsetsB[batch] = offsetB;
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
        }

        // Attention layers issue many small batched products — eight heads of a few hundred
        // rows each. Parallelising inside every one of those pays the thread-pool handshake
        // per batch on a few milliseconds of work, which is why the decoder's multiplies
        // measured an order of magnitude below the backbone's. Spreading the batches instead
        // gives one dispatch for the whole node and leaves each product running straight
        // through.
        if (batches > 1)
        {
            Parallel.For(0, batches, batch => MultiplyInto(
                fa.Floats.AsMemory(offsetsA[batch] * sizeA, sizeA),
                fb.Floats.AsMemory(offsetsB[batch] * sizeB, sizeB),
                result.Floats.AsMemory(batch * sizeC, sizeC),
                m, k, n, n, parallel: false));
        }
        else
        {
            MultiplyInto(
                fa.Floats.AsMemory(offsetsA[0] * sizeA, sizeA),
                fb.Floats.AsMemory(offsetsB[0] * sizeB, sizeB),
                result.Floats.AsMemory(0, sizeC),
                m, k, n);
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
        ReadOnlyMemory<float> a, ReadOnlyMemory<float> b, Memory<float> c, int m, int k, int n) =>
        MultiplyInto(a, b, c, m, k, n, n, parallel: true);

    /// <summary>
    /// As <see cref="MultiplyInto(ReadOnlyMemory{float}, ReadOnlyMemory{float}, Memory{float}, int, int, int)"/>,
    /// but writing into a destination whose rows are <paramref name="destinationStride"/>
    /// apart rather than packed.
    /// <para>
    /// Tiled convolution needs this: it computes a slice of output <em>columns</em> at a time
    /// and scatters each into the full-width result, so the destination rows are strided by
    /// the whole spatial extent. <paramref name="parallel"/> is turned off when the caller is
    /// already running one tile per thread, so the two levels do not oversubscribe.
    /// </para>
    /// </summary>
    public static void MultiplyInto(
        ReadOnlyMemory<float> a, ReadOnlyMemory<float> b, Memory<float> c,
        int m, int k, int n, int destinationStride, bool parallel)
    {
        if (m == 0 || n == 0 || k == 0) return;

        var destination = c.Span;
        if (destinationStride == n) destination.Clear();
        else for (int i = 0; i < m; i++) destination.Slice(i * destinationStride, n).Clear();

        long work = (long)m * n * k;
        int width = Vector<float>.Count;

        // Column panels are the unit of parallelism: they partition the destination, so
        // threads never touch the same output, and each operand slab is streamed by exactly
        // one thread. Narrowing them buys parallelism at the cost of re-reading the left
        // operand once per panel, so it is only worth doing when this call is what provides
        // the parallelism — a caller running one tile per thread already has enough.
        int panel = parallel
            ? Math.Min(ColumnPanel, Math.Max(width, RoundUp(
                (n + 2 * Environment.ProcessorCount - 1) / (2 * Environment.ProcessorCount), width)))
            : ColumnPanel;
        int panels = (n + panel - 1) / panel;

        if (parallel && work >= ParallelThreshold && panels > 1)
            Parallel.For(0, panels, index => ComputeColumnPanel(
                a.Span, b.Span, c.Span, m, k, n, destinationStride, index * panel, Math.Min(panel, n - index * panel)));
        else
            for (int index = 0; index < panels; index++)
                ComputeColumnPanel(
                    a.Span, b.Span, c.Span, m, k, n, destinationStride, index * panel, Math.Min(panel, n - index * panel));
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// Compute one full column panel of the destination: every depth panel, and within each,
    /// every row block. Ordering depth outside the rows is what earns the cache reuse — the
    /// operand slab stays resident while all the rows consume it.
    /// </summary>
    private static void ComputeColumnPanel(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
        int m, int k, int n, int ldc, int jc, int columns)
    {
        int blocks = (m + RowBlock - 1) / RowBlock;
        for (int pc = 0; pc < k; pc += DepthPanel)
        {
            int depth = Math.Min(DepthPanel, k - pc);
            for (int block = 0; block < blocks; block++)
                ComputePanel(a, b, c, m, k, n, ldc, block * RowBlock, pc, depth, jc, columns);
        }
    }

    /// <summary>
    /// Accumulate one panel of the product into up to <see cref="RowBlock"/> output rows:
    /// <c>C[i0.., jc..jc+columns] += A[i0.., pc..pc+depth] * B[pc..pc+depth, jc..jc+columns]</c>.
    /// </summary>
    private static void ComputePanel(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
        int m, int k, int n, int ldc, int i0, int pc, int depth, int jc, int columns)
    {
        int rows = Math.Min(RowBlockSize, m - i0);
        int end = jc + columns;

        int full = i0;
        for (; full + 4 <= i0 + rows; full += 4) ComputeFourRows(a, b, c, k, n, ldc, full, pc, depth, jc, end);
        for (int i = full; i < i0 + rows; i++) AccumulateRow(a, b, c, i, k, n, ldc, pc, depth, jc, columns);
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
        int k, int n, int ldc, int i0, int pc, int depth, int jStart, int end)
    {
        int width = Vector<float>.Count;
        int a0 = i0 * k + pc, a1 = a0 + k, a2 = a1 + k, a3 = a2 + k;
        int c0 = i0 * ldc, c1 = c0 + ldc, c2 = c1 + ldc, c3 = c2 + ldc;
        int j = jStart;

        // Take the widest strips first where the hardware has 512-bit vectors, then let the
        // portable loops below finish whatever is left.
        if (Use512BitVectors) j = ComputeFourRows512(a, b, c, n, a0, a1, a2, a3, c0, c1, c2, c3, pc, depth, j, end);

        // Reference-based addressing rather than span slicing. Every `Slice` in the inner
        // loop is a bounds check and a span construction, and at eight loads per iteration
        // that overhead — not the arithmetic — was what held the kernel to a third of the
        // machine's measured fused-multiply-add ceiling. The offsets are all derived from
        // panel bounds the caller has already clamped.
        ref float aRef = ref MemoryMarshal.GetReference(a);
        ref float bRef = ref MemoryMarshal.GetReference(b);
        ref float cRef = ref MemoryMarshal.GetReference(c);

        for (; j + 2 * width <= end; j += 2 * width)
        {
            // Accumulators start from C, not zero: a panel adds its share of the reduction to
            // whatever earlier depth panels already contributed.
            var acc00 = Vector.LoadUnsafe(ref cRef, (nuint)(c0 + j));
            var acc01 = Vector.LoadUnsafe(ref cRef, (nuint)(c0 + j + width));
            var acc10 = Vector.LoadUnsafe(ref cRef, (nuint)(c1 + j));
            var acc11 = Vector.LoadUnsafe(ref cRef, (nuint)(c1 + j + width));
            var acc20 = Vector.LoadUnsafe(ref cRef, (nuint)(c2 + j));
            var acc21 = Vector.LoadUnsafe(ref cRef, (nuint)(c2 + j + width));
            var acc30 = Vector.LoadUnsafe(ref cRef, (nuint)(c3 + j));
            var acc31 = Vector.LoadUnsafe(ref cRef, (nuint)(c3 + j + width));

            int bRow = pc * n + j;
            for (int p = 0; p < depth; p++, bRow += n)
            {
                var b0 = Vector.LoadUnsafe(ref bRef, (nuint)bRow);
                var b1 = Vector.LoadUnsafe(ref bRef, (nuint)(bRow + width));

                var s0 = new Vector<float>(Unsafe.Add(ref aRef, a0 + p));
                var s1 = new Vector<float>(Unsafe.Add(ref aRef, a1 + p));
                var s2 = new Vector<float>(Unsafe.Add(ref aRef, a2 + p));
                var s3 = new Vector<float>(Unsafe.Add(ref aRef, a3 + p));

                // Explicitly fused, not `acc += s * b`. The JIT will not contract a multiply
                // and an add into an FMA on its own — doing so would change the rounding —
                // and measured on this hardware the fused form is several times the
                // throughput of the separate pair. It is also what ONNX Runtime's kernels
                // emit, so the single-rounding result is the closer match, not the looser one.
                acc00 = Vector.FusedMultiplyAdd(s0, b0, acc00);
                acc01 = Vector.FusedMultiplyAdd(s0, b1, acc01);
                acc10 = Vector.FusedMultiplyAdd(s1, b0, acc10);
                acc11 = Vector.FusedMultiplyAdd(s1, b1, acc11);
                acc20 = Vector.FusedMultiplyAdd(s2, b0, acc20);
                acc21 = Vector.FusedMultiplyAdd(s2, b1, acc21);
                acc30 = Vector.FusedMultiplyAdd(s3, b0, acc30);
                acc31 = Vector.FusedMultiplyAdd(s3, b1, acc31);
            }

            acc00.StoreUnsafe(ref cRef, (nuint)(c0 + j));
            acc01.StoreUnsafe(ref cRef, (nuint)(c0 + j + width));
            acc10.StoreUnsafe(ref cRef, (nuint)(c1 + j));
            acc11.StoreUnsafe(ref cRef, (nuint)(c1 + j + width));
            acc20.StoreUnsafe(ref cRef, (nuint)(c2 + j));
            acc21.StoreUnsafe(ref cRef, (nuint)(c2 + j + width));
            acc30.StoreUnsafe(ref cRef, (nuint)(c3 + j));
            acc31.StoreUnsafe(ref cRef, (nuint)(c3 + j + width));
        }

        // One vector-wide strip, then whatever scalar tail is left.
        for (; j + width <= end; j += width)
        {
            var acc0 = Vector.LoadUnsafe(ref cRef, (nuint)(c0 + j));
            var acc1 = Vector.LoadUnsafe(ref cRef, (nuint)(c1 + j));
            var acc2 = Vector.LoadUnsafe(ref cRef, (nuint)(c2 + j));
            var acc3 = Vector.LoadUnsafe(ref cRef, (nuint)(c3 + j));
            int bRow = pc * n + j;
            for (int p = 0; p < depth; p++, bRow += n)
            {
                var bv = Vector.LoadUnsafe(ref bRef, (nuint)bRow);
                acc0 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + p)), bv, acc0);
                acc1 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a1 + p)), bv, acc1);
                acc2 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a2 + p)), bv, acc2);
                acc3 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a3 + p)), bv, acc3);
            }
            acc0.StoreUnsafe(ref cRef, (nuint)(c0 + j));
            acc1.StoreUnsafe(ref cRef, (nuint)(c1 + j));
            acc2.StoreUnsafe(ref cRef, (nuint)(c2 + j));
            acc3.StoreUnsafe(ref cRef, (nuint)(c3 + j));
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

    /// <summary>
    /// Whether to run the 512-bit kernel.
    /// <para>
    /// Gated on the instruction set rather than on <c>Vector512.IsHardwareAccelerated</c>,
    /// which reports <c>false</c> here: .NET keeps the portable <c>Vector&lt;T&gt;</c> at 256
    /// bits by default on these parts, but explicit <see cref="Vector512{T}"/> code still
    /// compiles down to real AVX-512. Measured on this hardware the difference is not
    /// marginal — a pure fused-multiply-add loop runs at 577 GFLOP/s against 161 — so the
    /// flag would cost most of the machine's capability if it were believed.
    /// </para>
    /// </summary>
    private static bool Use512BitVectors { get; } = Avx512F.IsSupported;

    /// <summary>
    /// Output rows the panel loop advances by.
    /// <para>
    /// Four, even where AVX-512's thirty-two registers would nominally hold more. Widening to
    /// eight rows — sixteen accumulators, two operand vectors and eight broadcasts — was
    /// measured and made the multiply markedly <em>slower</em> (116 down to 74 GFLOP/s), so
    /// the register file is evidently not the binding constraint at that width and the extra
    /// live values cost more than the halved operand re-reads save.
    /// </para>
    /// </summary>
    private static int RowBlockSize { get; } = RowBlock;

    /// <summary>
    /// The 512-bit register-blocked kernel: four output rows across two 16-float column
    /// strips. Identical in structure to the portable version, but AVX-512's 32 registers
    /// leave the eight accumulators, two operand vectors and four broadcasts comfortably in
    /// place where the 16-register 256-bit target has no room to spare.
    /// </summary>
    /// <returns>The first column not covered, for the portable loops to continue from.</returns>
    private static int ComputeFourRows512(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c, int n,
        int a0, int a1, int a2, int a3, int c0, int c1, int c2, int c3,
        int pc, int depth, int j, int end)
    {
        const int Width = 16;
        ref float aRef = ref MemoryMarshal.GetReference(a);
        ref float bRef = ref MemoryMarshal.GetReference(b);
        ref float cRef = ref MemoryMarshal.GetReference(c);

        for (; j + 2 * Width <= end; j += 2 * Width)
        {
            var acc00 = Vector512.LoadUnsafe(ref cRef, (nuint)(c0 + j));
            var acc01 = Vector512.LoadUnsafe(ref cRef, (nuint)(c0 + j + Width));
            var acc10 = Vector512.LoadUnsafe(ref cRef, (nuint)(c1 + j));
            var acc11 = Vector512.LoadUnsafe(ref cRef, (nuint)(c1 + j + Width));
            var acc20 = Vector512.LoadUnsafe(ref cRef, (nuint)(c2 + j));
            var acc21 = Vector512.LoadUnsafe(ref cRef, (nuint)(c2 + j + Width));
            var acc30 = Vector512.LoadUnsafe(ref cRef, (nuint)(c3 + j));
            var acc31 = Vector512.LoadUnsafe(ref cRef, (nuint)(c3 + j + Width));

            int bRow = pc * n + j;
            for (int p = 0; p < depth; p++, bRow += n)
            {
                var b0 = Vector512.LoadUnsafe(ref bRef, (nuint)bRow);
                var b1 = Vector512.LoadUnsafe(ref bRef, (nuint)(bRow + Width));

                var s0 = Vector512.Create(Unsafe.Add(ref aRef, a0 + p));
                var s1 = Vector512.Create(Unsafe.Add(ref aRef, a1 + p));
                var s2 = Vector512.Create(Unsafe.Add(ref aRef, a2 + p));
                var s3 = Vector512.Create(Unsafe.Add(ref aRef, a3 + p));

                acc00 = Vector512.FusedMultiplyAdd(s0, b0, acc00);
                acc01 = Vector512.FusedMultiplyAdd(s0, b1, acc01);
                acc10 = Vector512.FusedMultiplyAdd(s1, b0, acc10);
                acc11 = Vector512.FusedMultiplyAdd(s1, b1, acc11);
                acc20 = Vector512.FusedMultiplyAdd(s2, b0, acc20);
                acc21 = Vector512.FusedMultiplyAdd(s2, b1, acc21);
                acc30 = Vector512.FusedMultiplyAdd(s3, b0, acc30);
                acc31 = Vector512.FusedMultiplyAdd(s3, b1, acc31);
            }

            acc00.StoreUnsafe(ref cRef, (nuint)(c0 + j));
            acc01.StoreUnsafe(ref cRef, (nuint)(c0 + j + Width));
            acc10.StoreUnsafe(ref cRef, (nuint)(c1 + j));
            acc11.StoreUnsafe(ref cRef, (nuint)(c1 + j + Width));
            acc20.StoreUnsafe(ref cRef, (nuint)(c2 + j));
            acc21.StoreUnsafe(ref cRef, (nuint)(c2 + j + Width));
            acc30.StoreUnsafe(ref cRef, (nuint)(c3 + j));
            acc31.StoreUnsafe(ref cRef, (nuint)(c3 + j + Width));
        }

        for (; j + Width <= end; j += Width)
        {
            var acc0 = Vector512.LoadUnsafe(ref cRef, (nuint)(c0 + j));
            var acc1 = Vector512.LoadUnsafe(ref cRef, (nuint)(c1 + j));
            var acc2 = Vector512.LoadUnsafe(ref cRef, (nuint)(c2 + j));
            var acc3 = Vector512.LoadUnsafe(ref cRef, (nuint)(c3 + j));
            int bRow = pc * n + j;
            for (int p = 0; p < depth; p++, bRow += n)
            {
                var bv = Vector512.LoadUnsafe(ref bRef, (nuint)bRow);
                acc0 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + p)), bv, acc0);
                acc1 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a1 + p)), bv, acc1);
                acc2 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a2 + p)), bv, acc2);
                acc3 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a3 + p)), bv, acc3);
            }
            acc0.StoreUnsafe(ref cRef, (nuint)(c0 + j));
            acc1.StoreUnsafe(ref cRef, (nuint)(c1 + j));
            acc2.StoreUnsafe(ref cRef, (nuint)(c2 + j));
            acc3.StoreUnsafe(ref cRef, (nuint)(c3 + j));
        }
        return j;
    }

    /// <summary>Single-row fallback for the rows left over when <c>m</c> is not a multiple of
    /// <see cref="RowBlock"/>.</summary>
    private static void AccumulateRow(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
        int i, int k, int n, int ldc, int pc, int depth, int jc, int columns)
    {
        var row = c.Slice(i * ldc + jc, columns);
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
