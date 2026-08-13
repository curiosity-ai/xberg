using System.Runtime.CompilerServices;

namespace Xberg.Internal.Onnx;

/// <summary>ONNX <c>TensorProto.DataType</c> values, limited to those the layout models use.</summary>
internal enum ElementType
{
    Undefined = 0,
    Float = 1,
    UInt8 = 2,
    Int8 = 3,
    Int32 = 6,
    Int64 = 7,
    Bool = 9,
    Float16 = 10,
    Double = 11,
}

/// <summary>
/// A dense row-major tensor: the single value type flowing through the graph.
/// <para>
/// Storage is deliberately two-way rather than generic. Floating-point data lives in a
/// <see cref="float"/> array so every kernel can hand a contiguous span to
/// <c>System.Numerics.Tensors</c> or a <c>Vector&lt;float&gt;</c> loop with no
/// per-element type dispatch; everything integral (<c>int64</c> shapes, <c>int32</c>
/// indices, <c>bool</c> masks, <c>uint8</c> pixels) is widened into a <see cref="long"/>
/// array. Widening costs nothing in practice because the integral tensors in these graphs
/// are shapes and index vectors of a few dozen elements, while it removes the entire
/// combinatorial mess of per-dtype kernels. <see cref="Type"/> still records the original
/// element type, so <c>Cast</c> and output conversion stay exact.
/// </para>
/// </summary>
internal sealed class Tensor
{
    /// <summary>The declared element type. Integral types share <see cref="Longs"/> storage.</summary>
    public ElementType Type { get; }

    /// <summary>Dimensions, outermost first. A zero-length shape is a scalar.</summary>
    public int[] Shape { get; }

    /// <summary>Float payload; non-null exactly when <see cref="IsFloat"/>.</summary>
    public float[] Floats { get; }

    /// <summary>Integral payload widened to 64 bits; non-null exactly when <see cref="IsFloat"/> is false.</summary>
    public long[] Longs { get; }

    public bool IsFloat => Type is ElementType.Float or ElementType.Double or ElementType.Float16;

    /// <summary>Total element count — the product of <see cref="Shape"/>, and 1 for a scalar.</summary>
    public int Count { get; }

    public int Rank => Shape.Length;

    private Tensor(ElementType type, int[] shape, float[]? floats, long[]? longs)
    {
        Type = type;
        Shape = shape;
        Count = ElementCount(shape);
        Floats = floats!;
        Longs = longs!;
    }

    public static Tensor FromFloats(float[] data, params int[] shape)
    {
        int n = ElementCount(shape);
        if (data.Length != n)
            throw new InvalidDataException($"tensor data length {data.Length} does not match shape [{string.Join(",", shape)}] ({n})");
        return new Tensor(ElementType.Float, shape, data, null);
    }

    public static Tensor FromLongs(long[] data, ElementType type, params int[] shape)
    {
        int n = ElementCount(shape);
        if (data.Length != n)
            throw new InvalidDataException($"tensor data length {data.Length} does not match shape [{string.Join(",", shape)}] ({n})");
        return new Tensor(type, shape, null, data);
    }

    /// <summary>Allocate an uninitialised float tensor of the given shape.</summary>
    public static Tensor AllocateFloat(params int[] shape) => FromFloats(new float[ElementCount(shape)], shape);

    /// <summary>Allocate an uninitialised integral tensor of the given shape.</summary>
    public static Tensor AllocateLong(ElementType type, params int[] shape) =>
        FromLongs(new long[ElementCount(shape)], type, shape);

    public static Tensor Scalar(float value) => FromFloats([value]);

    public static Tensor Scalar(long value, ElementType type = ElementType.Int64) => FromLongs([value], type);

    public static int ElementCount(ReadOnlySpan<int> shape)
    {
        int n = 1;
        foreach (int d in shape)
        {
            if (d < 0) throw new InvalidDataException($"negative dimension {d} in shape");
            n = checked(n * d);
        }
        return n;
    }

    /// <summary>The same buffer under a new shape. Reshape is free — storage is row-major.</summary>
    public Tensor Reshaped(params int[] shape)
    {
        if (ElementCount(shape) != Count)
            throw new InvalidDataException($"cannot reshape {Count} elements into [{string.Join(",", shape)}]");
        return IsFloat ? new Tensor(Type, shape, Floats, null) : new Tensor(Type, shape, null, Longs);
    }

    /// <summary>Row-major strides for this shape, in elements.</summary>
    public int[] Strides()
    {
        var strides = new int[Shape.Length];
        int acc = 1;
        for (int i = Shape.Length - 1; i >= 0; i--)
        {
            strides[i] = acc;
            acc *= Shape[i];
        }
        return strides;
    }

    public ReadOnlySpan<float> FloatSpan => Floats;
    public Span<float> FloatSpanMutable => Floats;

    /// <summary>Read element <paramref name="i"/> as a float, whichever storage backs it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetFloat(int i) => IsFloat ? Floats[i] : Longs[i];

    /// <summary>Read element <paramref name="i"/> as a long, whichever storage backs it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetLong(int i) => IsFloat ? (long)Floats[i] : Longs[i];

    /// <summary>The tensor's values as <c>int</c>, for shape and axis operands.</summary>
    public int[] ToIntArray()
    {
        var result = new int[Count];
        for (int i = 0; i < Count; i++) result[i] = checked((int)GetLong(i));
        return result;
    }

    /// <summary>A float view: returns <c>this</c> when already float, otherwise converts.</summary>
    public Tensor AsFloat()
    {
        if (IsFloat) return this;
        var data = new float[Count];
        for (int i = 0; i < Count; i++) data[i] = Longs[i];
        return FromFloats(data, Shape);
    }

    public override string ToString() => $"{Type}[{string.Join(",", Shape)}]";
}
