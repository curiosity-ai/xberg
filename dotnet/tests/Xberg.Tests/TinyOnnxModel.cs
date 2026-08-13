using System.Runtime.InteropServices;
using System.Text;

namespace Xberg.Tests;

/// <summary>
/// Builds a minimal ONNX model as raw protobuf bytes.
/// <para>
/// The point is to test the parser against a file it did not write. Round-tripping through
/// Xberg's own serialiser would prove nothing — there isn't one — so this hand-encodes the
/// wire format straight from the ONNX schema's field numbers, the same way a real exporter
/// would, giving the reader genuine third-party input to parse.
/// </para>
/// <para>The graph is <c>y = Relu(x * w + b)</c> over a four-element vector, with <c>w</c>
/// and <c>b</c> as initializers.</para>
/// </summary>
internal static class TinyOnnxModel
{
    public static byte[] Build()
    {
        var graph = new List<byte>();

        // GraphProto.node (field 1) — order matters: ONNX requires topological order.
        Append(graph, 1, Node(["x", "w"], ["xw"], "Mul", "mul"));
        Append(graph, 1, Node(["xw", "b"], ["preact"], "Add", "add"));
        Append(graph, 1, Node(["preact"], ["y"], "Relu", "relu"));

        // GraphProto.name (field 2)
        AppendString(graph, 2, "tiny");

        // GraphProto.initializer (field 5)
        Append(graph, 5, FloatTensor("w", [2f, 2f, 2f, 2f], [4]));
        Append(graph, 5, FloatTensor("b", [-1f, -1f, -1f, -1f], [4]));

        // GraphProto.input (field 11) / output (field 12)
        Append(graph, 11, ValueInfo("x", elementType: 1, [4]));
        Append(graph, 12, ValueInfo("y", elementType: 1, [4]));

        var model = new List<byte>();
        AppendVarintField(model, 1, 8);              // ModelProto.ir_version
        AppendString(model, 2, "xberg-tests");       // ModelProto.producer_name
        Append(model, 7, graph.ToArray());           // ModelProto.graph
        Append(model, 8, OpsetId("", 16));           // ModelProto.opset_import
        return model.ToArray();
    }

    private static byte[] Node(string[] inputs, string[] outputs, string opType, string name)
    {
        var node = new List<byte>();
        foreach (string input in inputs) AppendString(node, 1, input);
        foreach (string output in outputs) AppendString(node, 2, output);
        AppendString(node, 3, name);
        AppendString(node, 4, opType);
        return node.ToArray();
    }

    private static byte[] FloatTensor(string name, float[] values, long[] dims)
    {
        var tensor = new List<byte>();
        foreach (long dim in dims) AppendVarintField(tensor, 1, (ulong)dim);  // dims
        AppendVarintField(tensor, 2, 1);                                      // data_type = FLOAT
        AppendString(tensor, 8, name);                                        // name
        Append(tensor, 9, MemoryMarshal.AsBytes<float>(values).ToArray());    // raw_data
        return tensor.ToArray();
    }

    private static byte[] ValueInfo(string name, int elementType, long[] dims)
    {
        var shape = new List<byte>();
        foreach (long dim in dims)
        {
            var dimension = new List<byte>();
            AppendVarintField(dimension, 1, (ulong)dim);        // Dimension.dim_value
            Append(shape, 1, dimension.ToArray());              // TensorShapeProto.dim
        }

        var tensorType = new List<byte>();
        AppendVarintField(tensorType, 1, (ulong)elementType);   // Tensor.elem_type
        Append(tensorType, 2, shape.ToArray());                 // Tensor.shape

        var typeProto = new List<byte>();
        Append(typeProto, 1, tensorType.ToArray());             // TypeProto.tensor_type

        var info = new List<byte>();
        AppendString(info, 1, name);
        Append(info, 2, typeProto.ToArray());
        return info.ToArray();
    }

    private static byte[] OpsetId(string domain, long version)
    {
        var opset = new List<byte>();
        if (domain.Length > 0) AppendString(opset, 1, domain);
        AppendVarintField(opset, 2, (ulong)version);
        return opset.ToArray();
    }

    /// <summary>Write a length-delimited field: tag, then length, then payload.</summary>
    private static void Append(List<byte> into, int fieldNumber, byte[] payload)
    {
        WriteVarint(into, ((ulong)fieldNumber << 3) | 2);
        WriteVarint(into, (ulong)payload.Length);
        into.AddRange(payload);
    }

    private static void AppendString(List<byte> into, int fieldNumber, string value) =>
        Append(into, fieldNumber, Encoding.UTF8.GetBytes(value));

    private static void AppendVarintField(List<byte> into, int fieldNumber, ulong value)
    {
        WriteVarint(into, (ulong)fieldNumber << 3);
        WriteVarint(into, value);
    }

    private static void WriteVarint(List<byte> into, ulong value)
    {
        while (value >= 0x80)
        {
            into.Add((byte)(value | 0x80));
            value >>= 7;
        }
        into.Add((byte)value);
    }
}
