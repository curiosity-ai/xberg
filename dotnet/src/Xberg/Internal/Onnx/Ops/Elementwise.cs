using System.Numerics.Tensors;

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

    /// <summary>
    /// The Gauss error function, evaluated in double precision so the float32 result is
    /// correctly rounded. ONNX Runtime computes <c>Erf</c> in double internally too, which is
    /// why a float-domain rational approximation would show up as a parity failure.
    /// </summary>
    public static Tensor Erf(Tensor x)
    {
        var f = x.AsFloat();
        var result = Tensor.AllocateFloat(f.Shape);
        var src = f.Floats;
        var dst = result.Floats;
        for (int i = 0; i < src.Length; i++) dst[i] = (float)ErfDouble(src[i]);
        return result;
    }

    /// <summary>
    /// Numerical Recipes' incomplete-gamma-free <c>erfc</c> with a Chebyshev fit, accurate to
    /// roughly 1.2e-7 relative — comfortably below float32 resolution across the whole range.
    /// </summary>
    private static double ErfDouble(double x)
    {
        double z = Math.Abs(x);
        double t = 2.0 / (2.0 + z);
        double ty = 4.0 * t - 2.0;
        double[] coefficients =
        [
            -1.3026537197817094, 6.4196979235649026e-1, 1.9476473204185836e-2, -9.561514786808631e-3,
            -9.46595344482036e-4, 3.66839497852761e-4, 4.2523324806907e-5, -2.0278578112534e-5,
            -1.624290004647e-6, 1.303655835580e-6, 1.5626441722e-8, -8.5238095915e-8,
            6.529054439e-9, 5.059343495e-9, -9.91364156e-10, -2.27365122e-10,
            9.6467911e-11, 2.394038e-12, -6.886027e-12, 8.94487e-13,
            3.13092e-13, -1.12708e-13, 3.81e-16, 7.106e-15,
        ];
        double d = 0.0, dd = 0.0;
        for (int j = coefficients.Length - 1; j > 0; j--)
        {
            double tmp = d;
            d = ty * d - dd + coefficients[j];
            dd = tmp;
        }
        double erfc = t * Math.Exp(-z * z + 0.5 * (coefficients[0] + ty * d) - dd);
        return x >= 0.0 ? 1.0 - erfc : erfc - 1.0;
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
