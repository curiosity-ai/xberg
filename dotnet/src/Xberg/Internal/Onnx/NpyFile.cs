using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Xberg.Internal.Onnx;

/// <summary>
/// Reader and writer for numpy's <c>.npy</c> container.
/// <para>
/// The parity harness needs to exchange raw tensors with the Python reference pipeline, and
/// <c>.npy</c> is the format that makes both ends trivial: numpy writes it natively, and the
/// layout is a short ASCII header followed by the same little-endian row-major bytes this
/// runtime already stores. Only the subset numpy actually emits for these dumps is handled —
/// version 1.0 or 2.0, C order, no pickled objects.
/// </para>
/// </summary>
internal static class NpyFile
{
    /// <summary>numpy's file signature. Spelled as raw bytes because 0x93 is not an ASCII
    /// character: a <c>u8</c> string literal would encode it as the two-byte UTF-8 sequence.</summary>
    private static ReadOnlySpan<byte> Magic => [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

    /// <summary>Load a <c>.npy</c> file into a <see cref="Tensor"/>.</summary>
    public static Tensor Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 10 || !bytes.AsSpan(0, 6).SequenceEqual(Magic))
            throw new InvalidDataException($"{path}: not a .npy file");

        int major = bytes[6];
        int headerLength;
        int dataStart;
        if (major == 1)
        {
            headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
            dataStart = 10 + headerLength;
        }
        else
        {
            headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            dataStart = 12 + headerLength;
        }

        string header = Encoding.ASCII.GetString(bytes, dataStart - headerLength, headerLength);
        var (descr, fortranOrder, shape) = ParseHeader(header);
        if (fortranOrder) throw new NotSupportedException($"{path}: Fortran-ordered arrays are not supported");

        var data = bytes.AsSpan(dataStart);
        int count = Tensor.ElementCount(shape);

        return descr switch
        {
            "<f4" or "=f4" or "|f4" => Tensor.FromFloats(ReadFloats(data, count), shape),
            "<f8" or "=f8" => Tensor.FromFloats(ReadDoublesAsFloats(data, count), shape),
            "<i8" or "=i8" => Tensor.FromLongs(ReadLongs(data, count), ElementType.Int64, shape),
            "<i4" or "=i4" => Tensor.FromLongs(ReadInt32s(data, count), ElementType.Int32, shape),
            "|b1" or "<b1" => Tensor.FromLongs(ReadBytes(data, count), ElementType.Bool, shape),
            "|u1" or "<u1" => Tensor.FromLongs(ReadBytes(data, count), ElementType.UInt8, shape),
            _ => throw new NotSupportedException($"{path}: unsupported dtype '{descr}'"),
        };
    }

    /// <summary>Write a tensor as a version 1.0 <c>.npy</c> file.</summary>
    public static void Save(string path, Tensor tensor)
    {
        string descr = tensor.IsFloat ? "<f4" : "<i8";
        string shape = tensor.Rank == 0
            ? "()"
            : "(" + string.Join(", ", tensor.Shape) + (tensor.Rank == 1 ? ",)" : ")");
        string dict = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': {shape}, }}";

        // numpy pads the header with spaces so the data begins on a 64-byte boundary.
        int unpadded = 10 + dict.Length + 1;
        int padding = (64 - unpadded % 64) % 64;
        string header = dict + new string(' ', padding) + "\n";

        using var stream = File.Create(path);
        stream.Write(Magic);
        stream.WriteByte(1);
        stream.WriteByte(0);
        Span<byte> lengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)header.Length);
        stream.Write(lengthBytes);
        stream.Write(Encoding.ASCII.GetBytes(header));
        stream.Write(tensor.IsFloat
            ? MemoryMarshal.AsBytes<float>(tensor.Floats)
            : MemoryMarshal.AsBytes<long>(tensor.Longs));
    }

    /// <summary>
    /// Pull dtype, ordering and shape out of the header's Python dict literal. A real parser
    /// is unnecessary: numpy writes this dict in a fixed, canonical form.
    /// </summary>
    private static (string Descr, bool FortranOrder, int[] Shape) ParseHeader(string header)
    {
        string descr = Between(header, "'descr':", ",").Trim().Trim('\'');
        bool fortran = Between(header, "'fortran_order':", ",").Trim() == "True";

        int open = header.IndexOf('(');
        int close = header.IndexOf(')', open + 1);
        string shapeText = open >= 0 && close > open ? header[(open + 1)..close] : "";
        var shape = shapeText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();
        return (descr, fortran, shape);
    }

    private static string Between(string text, string after, string before)
    {
        int start = text.IndexOf(after, StringComparison.Ordinal);
        if (start < 0) return "";
        start += after.Length;
        int end = text.IndexOf(before, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static float[] ReadFloats(ReadOnlySpan<byte> data, int count)
    {
        var result = new float[count];
        MemoryMarshal.Cast<byte, float>(data[..(count * 4)]).CopyTo(result);
        return result;
    }

    private static float[] ReadDoublesAsFloats(ReadOnlySpan<byte> data, int count)
    {
        var result = new float[count];
        var src = MemoryMarshal.Cast<byte, double>(data[..(count * 8)]);
        for (int i = 0; i < count; i++) result[i] = (float)src[i];
        return result;
    }

    private static long[] ReadLongs(ReadOnlySpan<byte> data, int count)
    {
        var result = new long[count];
        MemoryMarshal.Cast<byte, long>(data[..(count * 8)]).CopyTo(result);
        return result;
    }

    private static long[] ReadInt32s(ReadOnlySpan<byte> data, int count)
    {
        var result = new long[count];
        var src = MemoryMarshal.Cast<byte, int>(data[..(count * 4)]);
        for (int i = 0; i < count; i++) result[i] = src[i];
        return result;
    }

    private static long[] ReadBytes(ReadOnlySpan<byte> data, int count)
    {
        var result = new long[count];
        for (int i = 0; i < count; i++) result[i] = data[i];
        return result;
    }
}
