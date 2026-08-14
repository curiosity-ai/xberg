using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// The single-precision matrix multiply, structured after ONNX Runtime's MLAS.
/// <para>
/// The shape of this file is not invention. Profiling put convolution at well over half of
/// inference, convolution lowers onto this multiply, and the multiply was running at roughly
/// a fifth of the machine's measured ceiling — so MLAS's own SGEMM was read to find what it
/// does differently. Three things, all reproduced here.
/// </para>
/// <para>
/// <strong>The right-hand operand is packed.</strong> Before any arithmetic, a panel of
/// <c>B</c> is copied into a buffer where each group of sixteen columns is made physically
/// contiguous — <c>[columnBlock][k][16]</c>. The kernel then walks it strictly forwards
/// instead of jumping a full row stride per step, which is what turns its operand reads into
/// a stream the prefetcher can follow. The copy is paid once per panel and amortised over
/// every row block that consumes it.
/// </para>
/// <para>
/// <strong>The register block is six rows by two vectors.</strong> Twelve accumulators, two
/// operand vectors, and a <em>single</em> broadcast register reused across the rows — fifteen
/// live values, which is what fits AVX2's sixteen registers and what MLAS itself uses. An
/// earlier attempt at eight rows measured markedly slower, and reading MLAS explains why the
/// ceiling is where it is rather than higher.
/// </para>
/// <para>
/// <strong>The depth loop accumulates in place.</strong> The first depth panel writes the
/// destination and later ones add to it, so nothing pre-clears <c>C</c> and each output
/// element is touched once per panel rather than twice.
/// </para>
/// </summary>
internal static class GemmKernel
{
    /// <summary>Columns per packed block. Sixteen matches the 512-bit vector width and is the
    /// unit MLAS packs in; the 256-bit kernel simply consumes each block as two vectors.</summary>
    private const int BlockWidth = 16;

    /// <summary>Output rows per register block.</summary>
    private const int RowBlock = 6;

    /// <summary>
    /// Panel extents, matching MLAS's <c>MLAS_SGEMM_STRIDEN</c> and <c>STRIDEK</c>. A
    /// 128x128 float panel is 64 KB, which stays resident while every row block reads it.
    /// </summary>
    private const int StrideN = 128;

    /// <inheritdoc cref="StrideN"/>
    private const int StrideK = 128;

    private static bool Use512 { get; } = Avx512F.IsSupported;

    /// <summary>Scratch for the packed panel, one per thread and reused across calls.</summary>
    [ThreadStatic]
    private static float[]? _packed;

    /// <summary>
    /// <c>C[m,n] = A[m,k] * B[k,n]</c>, where <c>C</c>'s rows are
    /// <paramref name="ldc"/> apart.
    /// </summary>
    /// <param name="parallel">
    /// Spread column panels across threads. Turned off by callers that are already running
    /// one unit of work per thread, so the two levels do not oversubscribe.
    /// </param>
    public static void Multiply(
        ReadOnlyMemory<float> a, ReadOnlyMemory<float> b, Memory<float> c,
        int m, int k, int n, int ldc, bool parallel, long parallelThreshold)
    {
        var source = b;
        int ldb = n;
        Multiply(a, (destination, pc, countK, jc, countN) =>
            PackB(destination, source.Span, ldb, pc, countK, jc, countN),
            c, m, k, n, ldc, parallel, parallelThreshold);
    }

    /// <summary>
    /// As <see cref="Multiply(ReadOnlyMemory{float}, ReadOnlyMemory{float}, Memory{float}, int, int, int, int, bool, long)"/>,
    /// but with the caller supplying the right-hand operand a panel at a time.
    /// <para>
    /// This exists for convolution. Materialising the unrolled receptive fields into a buffer
    /// and then packing that buffer moves the expanded data — nine times the input for a 3x3
    /// layer — through memory twice. A caller that can produce the packed panel directly
    /// writes it once, and the intermediate disappears entirely.
    /// </para>
    /// </summary>
    public static void Multiply(
        ReadOnlyMemory<float> a, PanelPacker packB, Memory<float> c,
        int m, int k, int n, int ldc, bool parallel, long parallelThreshold)
    {
        if (m == 0 || n == 0 || k == 0) return;

        // MLAS reshapes the panel when one dimension is small: a shallow reduction wants
        // wider column panels, a narrow output wants deeper ones, so the panel stays a
        // similar size either way.
        int strideN = StrideN, strideK = StrideK;
        if (n >= k)
        {
            while (strideK / 2 >= k && strideK > 16) { strideN *= 2; strideK /= 2; }
        }
        else
        {
            while (strideN > BlockWidth && strideN / 2 >= n) { strideK *= 2; strideN /= 2; }
        }

        int panels = (n + strideN - 1) / strideN;
        long work = (long)m * n * k;

        if (parallel && work >= parallelThreshold && panels > 1)
        {
            Parallel.For(0, panels, index => ColumnPanel(
                a.Span, packB, c.Span, m, k, ldc,
                index * strideN, Math.Min(strideN, n - index * strideN), strideK));
        }
        else
        {
            for (int index = 0; index < panels; index++)
                ColumnPanel(
                    a.Span, packB, c.Span, m, k, ldc,
                    index * strideN, Math.Min(strideN, n - index * strideN), strideK);
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the packed form of
    /// <c>B[pc..pc+countK, jc..jc+countN]</c>, laid out as <c>[columnBlock][k][16]</c> with the
    /// trailing block zero-filled.
    /// </summary>
    internal delegate void PanelPacker(float[] destination, int pc, int countK, int jc, int countN);

    /// <summary>
    /// Compute one column panel of the destination in full: pack each depth slice of the
    /// operand once, then run every row block against it.
    /// </summary>
    private static void ColumnPanel(
        ReadOnlySpan<float> a, PanelPacker packB, Span<float> c,
        int m, int k, int ldc, int jc, int countN, int strideK)
    {
        int blocks = (countN + BlockWidth - 1) / BlockWidth;
        var packed = RentPacked(blocks * strideK * BlockWidth);

        for (int pc = 0; pc < k; pc += strideK)
        {
            int countK = Math.Min(strideK, k - pc);
            packB(packed, pc, countK, jc, countN);

            // The first depth slice establishes the destination; later ones add to it.
            bool first = pc == 0;
            for (int i0 = 0; i0 < m; i0 += RowBlock)
            {
                int rows = Math.Min(RowBlock, m - i0);
                RowBlockKernel(a, packed, c, k, ldc, i0, rows, pc, countK, jc, countN, first);
            }
        }
    }

    private static float[] RentPacked(int elements)
    {
        var buffer = _packed;
        if (buffer is null || buffer.Length < elements) _packed = buffer = new float[elements];
        return buffer;
    }

    /// <summary>
    /// Copy <c>B[pc..pc+countK, jc..jc+countN]</c> into the packed layout
    /// <c>[block][k][16]</c>, zero-filling the final block when the panel is not a whole
    /// number of blocks wide — which lets the kernel run one uniform shape and ignore edges.
    /// </summary>
    internal static void PackB(
        float[] packed, ReadOnlySpan<float> b, int ldb, int pc, int countK, int jc, int countN)
    {
        int blocks = (countN + BlockWidth - 1) / BlockWidth;
        for (int block = 0; block < blocks; block++)
        {
            int column = jc + block * BlockWidth;
            int width = Math.Min(BlockWidth, jc + countN - column);
            int destination = block * countK * BlockWidth;

            if (width == BlockWidth)
            {
                for (int kk = 0; kk < countK; kk++, destination += BlockWidth)
                    b.Slice((pc + kk) * ldb + column, BlockWidth)
                     .CopyTo(packed.AsSpan(destination, BlockWidth));
            }
            else
            {
                for (int kk = 0; kk < countK; kk++, destination += BlockWidth)
                {
                    var target = packed.AsSpan(destination, BlockWidth);
                    b.Slice((pc + kk) * ldb + column, width).CopyTo(target);
                    target[width..].Clear();
                }
            }
        }
    }

    /// <summary>Dispatch one row block to the widest kernel that fits it.</summary>
    private static void RowBlockKernel(
        ReadOnlySpan<float> a, float[] packed, Span<float> c,
        int k, int ldc, int i0, int rows, int pc, int countK, int jc, int countN, bool first)
    {
        if (rows == RowBlock)
        {
            if (Use512) SixRows512(a, packed, c, k, ldc, i0, pc, countK, jc, countN, first);
            else SixRowsVector(a, packed, c, k, ldc, i0, pc, countK, jc, countN, first);
            return;
        }
        for (int i = i0; i < i0 + rows; i++)
            SingleRow(a, packed, c, k, ldc, i, pc, countK, jc, countN, first);
    }

    /// <summary>Flat offset of a packed block's first element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlockOffset(int block, int countK) => block * countK * BlockWidth;

    /// <summary>
    /// Six output rows against two 16-float packed blocks, in 512-bit vectors.
    /// </summary>
    private static void SixRows512(
        ReadOnlySpan<float> a, float[] packed, Span<float> c,
        int k, int ldc, int i0, int pc, int countK, int jc, int countN, bool first)
    {
        ref float aRef = ref MemoryMarshal.GetReference(a);
        ref float pRef = ref MemoryMarshal.GetArrayDataReference(packed);
        ref float cRef = ref MemoryMarshal.GetReference(c);

        int a0 = i0 * k + pc;
        int c0 = i0 * ldc + jc;
        int blocks = (countN + BlockWidth - 1) / BlockWidth;
        int block = 0;

        for (; block + 2 <= blocks; block += 2)
        {
            int p0 = BlockOffset(block, countK);
            int p1 = BlockOffset(block + 1, countK);
            int column = block * BlockWidth;

            Vector512<float> r00 = Vector512<float>.Zero, r01 = Vector512<float>.Zero;
            Vector512<float> r10 = Vector512<float>.Zero, r11 = Vector512<float>.Zero;
            Vector512<float> r20 = Vector512<float>.Zero, r21 = Vector512<float>.Zero;
            Vector512<float> r30 = Vector512<float>.Zero, r31 = Vector512<float>.Zero;
            Vector512<float> r40 = Vector512<float>.Zero, r41 = Vector512<float>.Zero;
            Vector512<float> r50 = Vector512<float>.Zero, r51 = Vector512<float>.Zero;

            for (int kk = 0; kk < countK; kk++)
            {
                var b0 = Vector512.LoadUnsafe(ref pRef, (nuint)(p0 + kk * BlockWidth));
                var b1 = Vector512.LoadUnsafe(ref pRef, (nuint)(p1 + kk * BlockWidth));

                // One broadcast register, reused down the rows — the shape that keeps twelve
                // accumulators plus two operands inside the register file.
                var s = Vector512.Create(Unsafe.Add(ref aRef, a0 + kk));
                r00 = Vector512.FusedMultiplyAdd(s, b0, r00);
                r01 = Vector512.FusedMultiplyAdd(s, b1, r01);
                s = Vector512.Create(Unsafe.Add(ref aRef, a0 + k + kk));
                r10 = Vector512.FusedMultiplyAdd(s, b0, r10);
                r11 = Vector512.FusedMultiplyAdd(s, b1, r11);
                s = Vector512.Create(Unsafe.Add(ref aRef, a0 + 2 * k + kk));
                r20 = Vector512.FusedMultiplyAdd(s, b0, r20);
                r21 = Vector512.FusedMultiplyAdd(s, b1, r21);
                s = Vector512.Create(Unsafe.Add(ref aRef, a0 + 3 * k + kk));
                r30 = Vector512.FusedMultiplyAdd(s, b0, r30);
                r31 = Vector512.FusedMultiplyAdd(s, b1, r31);
                s = Vector512.Create(Unsafe.Add(ref aRef, a0 + 4 * k + kk));
                r40 = Vector512.FusedMultiplyAdd(s, b0, r40);
                r41 = Vector512.FusedMultiplyAdd(s, b1, r41);
                s = Vector512.Create(Unsafe.Add(ref aRef, a0 + 5 * k + kk));
                r50 = Vector512.FusedMultiplyAdd(s, b0, r50);
                r51 = Vector512.FusedMultiplyAdd(s, b1, r51);
            }

            Store512(ref cRef, c0 + 0 * ldc + column, countN - column, r00, r01, first);
            Store512(ref cRef, c0 + 1 * ldc + column, countN - column, r10, r11, first);
            Store512(ref cRef, c0 + 2 * ldc + column, countN - column, r20, r21, first);
            Store512(ref cRef, c0 + 3 * ldc + column, countN - column, r30, r31, first);
            Store512(ref cRef, c0 + 4 * ldc + column, countN - column, r40, r41, first);
            Store512(ref cRef, c0 + 5 * ldc + column, countN - column, r50, r51, first);
        }

        for (; block < blocks; block++)
        {
            int p0 = BlockOffset(block, countK);
            int column = block * BlockWidth;

            Vector512<float> r0 = Vector512<float>.Zero, r1 = Vector512<float>.Zero;
            Vector512<float> r2 = Vector512<float>.Zero, r3 = Vector512<float>.Zero;
            Vector512<float> r4 = Vector512<float>.Zero, r5 = Vector512<float>.Zero;

            for (int kk = 0; kk < countK; kk++)
            {
                var b0 = Vector512.LoadUnsafe(ref pRef, (nuint)(p0 + kk * BlockWidth));
                r0 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + kk)), b0, r0);
                r1 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + k + kk)), b0, r1);
                r2 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + 2 * k + kk)), b0, r2);
                r3 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + 3 * k + kk)), b0, r3);
                r4 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + 4 * k + kk)), b0, r4);
                r5 = Vector512.FusedMultiplyAdd(Vector512.Create(Unsafe.Add(ref aRef, a0 + 5 * k + kk)), b0, r5);
            }

            StoreOne512(ref cRef, c0 + 0 * ldc + column, countN - column, r0, first);
            StoreOne512(ref cRef, c0 + 1 * ldc + column, countN - column, r1, first);
            StoreOne512(ref cRef, c0 + 2 * ldc + column, countN - column, r2, first);
            StoreOne512(ref cRef, c0 + 3 * ldc + column, countN - column, r3, first);
            StoreOne512(ref cRef, c0 + 4 * ldc + column, countN - column, r4, first);
            StoreOne512(ref cRef, c0 + 5 * ldc + column, countN - column, r5, first);
        }
    }

    /// <summary>
    /// Write two accumulators back, adding to what is there unless this is the first depth
    /// panel. The trailing block may extend past the panel's real width, since packing
    /// zero-fills it; only the live columns are stored.
    /// </summary>
    private static void Store512(
        ref float cRef, int offset, int remaining, Vector512<float> v0, Vector512<float> v1, bool first)
    {
        StoreOne512(ref cRef, offset, remaining, v0, first);
        StoreOne512(ref cRef, offset + BlockWidth, remaining - BlockWidth, v1, first);
    }

    private static void StoreOne512(ref float cRef, int offset, int remaining, Vector512<float> v, bool first)
    {
        if (remaining <= 0) return;
        if (remaining >= BlockWidth)
        {
            if (!first) v += Vector512.LoadUnsafe(ref cRef, (nuint)offset);
            v.StoreUnsafe(ref cRef, (nuint)offset);
            return;
        }
        for (int i = 0; i < remaining; i++)
        {
            ref float target = ref Unsafe.Add(ref cRef, offset + i);
            target = first ? v[i] : target + v[i];
        }
    }

    /// <summary>
    /// Six output rows against one 16-float packed block, using the portable vector width.
    /// On a 256-bit target the block is consumed as two vectors, giving the same twelve
    /// accumulators.
    /// </summary>
    private static void SixRowsVector(
        ReadOnlySpan<float> a, float[] packed, Span<float> c,
        int k, int ldc, int i0, int pc, int countK, int jc, int countN, bool first)
    {
        int width = Vector<float>.Count;
        int halves = BlockWidth / width;          // 2 on AVX2, 1 where the vector is already 16 wide
        ref float aRef = ref MemoryMarshal.GetReference(a);
        ref float pRef = ref MemoryMarshal.GetArrayDataReference(packed);
        ref float cRef = ref MemoryMarshal.GetReference(c);

        int a0 = i0 * k + pc;
        int c0 = i0 * ldc + jc;
        int blocks = (countN + BlockWidth - 1) / BlockWidth;

        for (int block = 0; block < blocks; block++)
        {
            int p0 = BlockOffset(block, countK);
            int column = block * BlockWidth;

            for (int half = 0; half < halves; half++)
            {
                int lane = half * width;
                Vector<float> r0 = default, r1 = default, r2 = default;
                Vector<float> r3 = default, r4 = default, r5 = default;

                for (int kk = 0; kk < countK; kk++)
                {
                    var bv = Vector.LoadUnsafe(ref pRef, (nuint)(p0 + kk * BlockWidth + lane));
                    r0 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + kk)), bv, r0);
                    r1 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + k + kk)), bv, r1);
                    r2 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + 2 * k + kk)), bv, r2);
                    r3 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + 3 * k + kk)), bv, r3);
                    r4 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + 4 * k + kk)), bv, r4);
                    r5 = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + 5 * k + kk)), bv, r5);
                }

                int at = column + lane;
                StoreVector(ref cRef, c0 + 0 * ldc + at, countN - at, r0, first);
                StoreVector(ref cRef, c0 + 1 * ldc + at, countN - at, r1, first);
                StoreVector(ref cRef, c0 + 2 * ldc + at, countN - at, r2, first);
                StoreVector(ref cRef, c0 + 3 * ldc + at, countN - at, r3, first);
                StoreVector(ref cRef, c0 + 4 * ldc + at, countN - at, r4, first);
                StoreVector(ref cRef, c0 + 5 * ldc + at, countN - at, r5, first);
            }
        }
    }

    private static void StoreVector(ref float cRef, int offset, int remaining, Vector<float> v, bool first)
    {
        if (remaining <= 0) return;
        int width = Vector<float>.Count;
        if (remaining >= width)
        {
            if (!first) v += Vector.LoadUnsafe(ref cRef, (nuint)offset);
            v.StoreUnsafe(ref cRef, (nuint)offset);
            return;
        }
        for (int i = 0; i < remaining; i++)
        {
            ref float target = ref Unsafe.Add(ref cRef, offset + i);
            target = first ? v[i] : target + v[i];
        }
    }

    /// <summary>One output row, for the rows left over when the block does not divide evenly.</summary>
    private static void SingleRow(
        ReadOnlySpan<float> a, float[] packed, Span<float> c,
        int k, int ldc, int i, int pc, int countK, int jc, int countN, bool first)
    {
        ref float aRef = ref MemoryMarshal.GetReference(a);
        ref float pRef = ref MemoryMarshal.GetArrayDataReference(packed);
        ref float cRef = ref MemoryMarshal.GetReference(c);

        int width = Vector<float>.Count;
        int a0 = i * k + pc;
        int c0 = i * ldc + jc;
        int blocks = (countN + BlockWidth - 1) / BlockWidth;

        for (int block = 0; block < blocks; block++)
        {
            int p0 = BlockOffset(block, countK);
            int column = block * BlockWidth;

            for (int lane = 0; lane < BlockWidth; lane += width)
            {
                Vector<float> acc = default;
                for (int kk = 0; kk < countK; kk++)
                {
                    var bv = Vector.LoadUnsafe(ref pRef, (nuint)(p0 + kk * BlockWidth + lane));
                    acc = Vector.FusedMultiplyAdd(new Vector<float>(Unsafe.Add(ref aRef, a0 + kk)), bv, acc);
                }
                int at = column + lane;
                StoreVector(ref cRef, c0 + at, countN - at, acc, first);
            }
        }
    }
}
