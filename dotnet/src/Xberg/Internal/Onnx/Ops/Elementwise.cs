using System.Numerics.Tensors;
using System.Numerics;

namespace Xberg.Internal.Onnx.Ops;

/// <summary>Which arithmetic a binary elementwise node performs.</summary>
internal enum BinaryKind { Add, Sub, Mul, Div, Pow, Min, Max }

/// <summary>
/// Elementwise kernels: the arithmetic and activations that make up roughly half of every
/// node in these graphs.
/// <para>
/// Each binary kernel runs through a <see cref="Broadcast.Plan"/> so the common shapes cost
/// a single vectorised call over the whole array, and the general case still vectorises
/// within each contiguous block. Unary kernels go straight to
/// <c>System.Numerics.Tensors.TensorPrimitives</c>, which picks the widest SIMD path the
/// running CPU offers.
/// </para>
/// </summary>
internal static class Elementwise
{
    public static Tensor Binary(Tensor a, Tensor b, BinaryKind kind)
    {
        // Integral operands stay integral: Shape arithmetic (Mul/Div on dimension vectors)
        // must not round-trip through float, where large dimensions would lose exactness.
        if (!a.IsFloat && !b.IsFloat) return BinaryInteger(a, b, kind);

        var fa = a.AsFloat();
        var fb = b.AsFloat();
        var plan = Broadcast.MakePlan(fa.Shape, fb.Shape);
        var result = Tensor.AllocateFloat(plan.Shape);
        var dataA = fa.Floats;
        var dataB = fb.Floats;
        var dataOut = result.Floats;

        if (plan.IsFlat && fa.Count == plan.Total && fb.Count == plan.Total)
        {
            Apply(kind, dataA, dataB, dataOut);
            return result;
        }

        // A scalar operand has a dedicated primitive overload that avoids walking the plan.
        if (fb.Count == 1 && fa.Count == plan.Total)
        {
            ApplyScalarRight(kind, dataA, dataB[0], dataOut);
            return result;
        }
        if (fa.Count == 1 && fb.Count == plan.Total)
        {
            ApplyScalarLeft(kind, dataA[0], dataB, dataOut);
            return result;
        }

        // A run longer than one element only forms where *both* operands stride in lockstep
        // with the output, so within a block both sides are contiguous; a run of one means
        // both sides are pinned to a single element.
        int run = plan.BlockLength;
        Broadcast.ForEachBlock(plan, (oa, ob, od) =>
        {
            if (run > 1)
                Apply(kind, dataA.AsSpan(oa, run), dataB.AsSpan(ob, run), dataOut.AsSpan(od, run));
            else
                dataOut[od] = Scalar(kind, dataA[oa], dataB[ob]);
        });
        return result;
    }

    private static Tensor BinaryInteger(Tensor a, Tensor b, BinaryKind kind)
    {
        var plan = Broadcast.MakePlan(a.Shape, b.Shape);
        var result = Tensor.AllocateLong(a.Type == ElementType.Bool ? b.Type : a.Type, plan.Shape);
        var dst = result.Longs;
        var la = a.Longs;
        var lb = b.Longs;
        int rank = plan.Shape.Length;
        var index = new int[Math.Max(rank, 1)];
        int offsetA = 0, offsetB = 0;

        for (int i = 0; i < plan.Total; i++)
        {
            dst[i] = ScalarInteger(kind, la[offsetA], lb[offsetB]);
            for (int d = rank - 1; d >= 0; d--)
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
        return result;
    }

    private static void Apply(BinaryKind kind, ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> dst)
    {
        switch (kind)
        {
            case BinaryKind.Add: TensorPrimitives.Add(x, y, dst); break;
            case BinaryKind.Sub: TensorPrimitives.Subtract(x, y, dst); break;
            case BinaryKind.Mul: TensorPrimitives.Multiply(x, y, dst); break;
            case BinaryKind.Div: TensorPrimitives.Divide(x, y, dst); break;
            case BinaryKind.Pow: TensorPrimitives.Pow(x, y, dst); break;
            case BinaryKind.Min: TensorPrimitives.Min(x, y, dst); break;
            case BinaryKind.Max: TensorPrimitives.Max(x, y, dst); break;
            default: throw new NotSupportedException($"binary kind {kind}");
        }
    }

    private static void ApplyScalarRight(BinaryKind kind, ReadOnlySpan<float> x, float y, Span<float> dst)
    {
        switch (kind)
        {
            case BinaryKind.Add: TensorPrimitives.Add(x, y, dst); break;
            case BinaryKind.Sub: TensorPrimitives.Subtract(x, y, dst); break;
            case BinaryKind.Mul: TensorPrimitives.Multiply(x, y, dst); break;
            case BinaryKind.Div: TensorPrimitives.Divide(x, y, dst); break;
            case BinaryKind.Pow: TensorPrimitives.Pow(x, y, dst); break;
            case BinaryKind.Min: TensorPrimitives.Min(x, y, dst); break;
            case BinaryKind.Max: TensorPrimitives.Max(x, y, dst); break;
            default: throw new NotSupportedException($"binary kind {kind}");
        }
    }

    private static void ApplyScalarLeft(BinaryKind kind, float x, ReadOnlySpan<float> y, Span<float> dst)
    {
        switch (kind)
        {
            // Commutative cases reuse the right-scalar primitives.
            case BinaryKind.Add: TensorPrimitives.Add(y, x, dst); break;
            case BinaryKind.Mul: TensorPrimitives.Multiply(y, x, dst); break;
            case BinaryKind.Min: TensorPrimitives.Min(y, x, dst); break;
            case BinaryKind.Max: TensorPrimitives.Max(y, x, dst); break;
            case BinaryKind.Sub: TensorPrimitives.Subtract(x, y, dst); break;
            case BinaryKind.Div: TensorPrimitives.Divide(x, y, dst); break;
            case BinaryKind.Pow:
                for (int i = 0; i < y.Length; i++) dst[i] = MathF.Pow(x, y[i]);
                break;
            default: throw new NotSupportedException($"binary kind {kind}");
        }
    }

    private static float Scalar(BinaryKind kind, float x, float y) => kind switch
    {
        BinaryKind.Add => x + y,
        BinaryKind.Sub => x - y,
        BinaryKind.Mul => x * y,
        BinaryKind.Div => x / y,
        BinaryKind.Pow => MathF.Pow(x, y),
        BinaryKind.Min => MathF.Min(x, y),
        BinaryKind.Max => MathF.Max(x, y),
        _ => throw new NotSupportedException($"binary kind {kind}"),
    };

    private static long ScalarInteger(BinaryKind kind, long x, long y) => kind switch
    {
        BinaryKind.Add => x + y,
        BinaryKind.Sub => x - y,
        BinaryKind.Mul => x * y,
        // ONNX integer Div truncates toward zero, which is what C# `/` already does.
        BinaryKind.Div => y == 0 ? 0 : x / y,
        BinaryKind.Pow => (long)Math.Pow(x, y),
        BinaryKind.Min => Math.Min(x, y),
        BinaryKind.Max => Math.Max(x, y),
        _ => throw new NotSupportedException($"binary kind {kind}"),
    };

    public static Tensor Relu(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Max(f.Floats, 0f, result.Floats);
        return result;
    }

    public static Tensor Sigmoid(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Sigmoid(f.Floats, result.Floats);
        return result;
    }

    public static Tensor Sqrt(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Sqrt(f.Floats, result.Floats);
        return result;
    }

    public static Tensor Exp(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Exp(f.Floats, result.Floats);
        return result;
    }

    public static Tensor Log(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Log(f.Floats, result.Floats);
        return result;
    }

    public static Tensor Abs(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Abs(f.Floats, result.Floats);
        return result;
    }

    public static Tensor Tanh(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        TensorPrimitives.Tanh(f.Floats, result.Floats);
        return result;
    }

    public static Tensor Neg(Tensor x)
    {
        if (!x.IsFloat)
        {
            var negInt = Tensor.AllocateLong(x.Type, x.Shape);
            for (int i = 0; i < x.Count; i++) negInt.Longs[i] = -x.Longs[i];
            return negInt;
        }
        var result = Tensor.AllocateFloat(x.Shape);
        TensorPrimitives.Negate(x.Floats, result.Floats);
        return result;
    }

    public static Tensor Clip(Tensor x, float min, float max)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        var dst = result.Floats.AsSpan();
        TensorPrimitives.Max(f.Floats, min, dst);
        TensorPrimitives.Min(dst, max, dst);
        return result;
    }

    /// <summary>HardSigmoid: <c>clip(alpha * x + beta, 0, 1)</c>.</summary>
    public static Tensor HardSigmoid(Tensor x, float alpha, float beta)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        var dst = result.Floats.AsSpan();
        TensorPrimitives.Multiply(f.Floats, alpha, dst);
        TensorPrimitives.Add(dst, beta, dst);
        TensorPrimitives.Max(dst, 0f, dst);
        TensorPrimitives.Min(dst, 1f, dst);
        return result;
    }

    /// <summary>HardSwish: <c>x * clip(x / 6 + 0.5, 0, 1)</c>, the ONNX definition.</summary>
    public static Tensor HardSwish(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        var dst = result.Floats.AsSpan();
        TensorPrimitives.Multiply(f.Floats, 1f / 6f, dst);
        TensorPrimitives.Add(dst, 0.5f, dst);
        TensorPrimitives.Max(dst, 0f, dst);
        TensorPrimitives.Min(dst, 1f, dst);
        TensorPrimitives.Multiply(dst, f.Floats, dst);
        return result;
    }

    // Abramowitz and Stegun 7.1.26: erf(|x|) = 1 - poly(t) * exp(-x^2), t = 1/(1 + p|x|).
    // Maximum absolute error 1.5e-7, below float32 resolution over the whole range and three
    // orders of magnitude inside the parity tolerance.
    private const float ErfP = 0.3275911f;
    private const float ErfA1 = 0.254829592f;
    private const float ErfA2 = -0.284496736f;
    private const float ErfA3 = 1.421413741f;
    private const float ErfA4 = -1.453152027f;
    private const float ErfA5 = 1.061405429f;

    /// <summary>
    /// The Gauss error function, vectorised.
    /// <para>
    /// The obvious implementation — a high-order Chebyshev fit evaluated in double precision,
    /// one element at a time — is exact to the last bit and unusably slow: a single GELU node
    /// over 400k activations measured 50 ms, about 1% of the whole model. The rational
    /// approximation below runs entirely in float vectors and is accurate to 1.5e-7, which
    /// this graph cannot distinguish.
    /// </para>
    /// </summary>
    public static Tensor Erf(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        var src = f.Floats.AsSpan();
        var dst = result.Floats.AsSpan();

        // exp() comes from the vectorised primitive, so it runs over a chunk at a time; the
        // rest of the expression is evaluated in registers around it.
        const int ChunkSize = 2048;
        Span<float> scratch = stackalloc float[ChunkSize];

        for (int offset = 0; offset < src.Length; offset += ChunkSize)
        {
            int length = Math.Min(ChunkSize, src.Length - offset);
            var input = src.Slice(offset, length);
            var output = dst.Slice(offset, length);
            var buffer = scratch[..length];

            // exp(-x*x)
            TensorPrimitives.Multiply(input, input, buffer);
            TensorPrimitives.Negate(buffer, buffer);
            TensorPrimitives.Exp(buffer, buffer);

            int width = Vector<float>.Count;
            int i = 0;
            var one = Vector<float>.One;
            for (; i + width <= length; i += width)
            {
                var v = new Vector<float>(input.Slice(i, width));
                var magnitude = Vector.Abs(v);
                var t = one / (one + new Vector<float>(ErfP) * magnitude);

                var poly = new Vector<float>(ErfA5);
                poly = Vector.FusedMultiplyAdd(poly, t, new Vector<float>(ErfA4));
                poly = Vector.FusedMultiplyAdd(poly, t, new Vector<float>(ErfA3));
                poly = Vector.FusedMultiplyAdd(poly, t, new Vector<float>(ErfA2));
                poly = Vector.FusedMultiplyAdd(poly, t, new Vector<float>(ErfA1));
                poly *= t;

                var magnitudeResult = one - poly * new Vector<float>(buffer.Slice(i, width));
                // erf is odd, so the sign of the argument carries straight through.
                var signed = Vector.ConditionalSelect(
                    Vector.LessThan(v, Vector<float>.Zero), -magnitudeResult, magnitudeResult);
                signed.CopyTo(output.Slice(i, width));
            }

            for (; i < length; i++)
            {
                float v = input[i];
                float magnitude = MathF.Abs(v);
                float t = 1f / (1f + ErfP * magnitude);
                float poly = ((((ErfA5 * t + ErfA4) * t + ErfA3) * t + ErfA2) * t + ErfA1) * t;
                float value = 1f - poly * buffer[i];
                output[i] = v < 0f ? -value : value;
            }
        }
        return result;
    }

    /// <summary>Convert between element types, preserving ONNX's truncate-toward-zero
    /// semantics for float-to-integer casts.</summary>
    public static Tensor Cast(Tensor x, ElementType to)
    {
        bool toFloat = to is ElementType.Float or ElementType.Double or ElementType.Float16;
        if (toFloat)
        {
            if (x.IsFloat) return x.Shape.Length == 0 ? x : Tensor.FromFloats(x.Floats, x.Shape);
            var data = new float[x.Count];
            for (int i = 0; i < x.Count; i++) data[i] = x.Longs[i];
            return Tensor.FromFloats(data, x.Shape);
        }

        var longs = new long[x.Count];
        if (x.IsFloat)
        {
            for (int i = 0; i < x.Count; i++)
            {
                float v = x.Floats[i];
                longs[i] = to == ElementType.Bool
                    ? (v != 0f ? 1 : 0)
                    : (float.IsNaN(v) ? 0 : (long)MathF.Truncate(v));
            }
        }
        else
        {
            for (int i = 0; i < x.Count; i++)
                longs[i] = to == ElementType.Bool ? (x.Longs[i] != 0 ? 1 : 0) : x.Longs[i];
        }
        return Tensor.FromLongs(longs, to, x.Shape);
    }
}
