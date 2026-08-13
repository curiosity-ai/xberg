using System.Runtime.InteropServices;
using System.Text;

namespace Xberg.Tests;

/// <summary>
/// Hand-encoded ONNX graphs shaped like the patterns <c>GraphOptimizer</c> rewrites, so the
/// rewrites can be tested without a 169 MB download.
/// </summary>
internal static class ConvChainModel
{
    /// <summary>
    /// <c>Conv -> Mul(scale) -> Add(shift) [-> activation]</c> over a 1x1 convolution on a
    /// two-channel 2x2 input — the shape an exported batch normalisation takes.
    /// </summary>
    /// <param name="channelShapedConstants">
    /// When true the constants are <c>[1,C,1,1]</c> and fold; when false they are a bare
    /// <c>[C]</c>, which broadcasts along width instead and must be refused.
    /// </param>
    /// <param name="extraConsumer">Add a second reader of the Conv output, which blocks folding.</param>
    public static byte[] Build(
        float[] scale, float[] shift, string? activation,
        bool channelShapedConstants = true, bool extraConsumer = false)
    {
        int channels = scale.Length;
        long[] constantShape = channelShapedConstants ? [1, channels, 1, 1] : [channels];

        var graph = new List<byte>();

        // Identity weights: one output channel per input channel, so the convolution itself
        // is a no-op and the test isolates the folding arithmetic.
        var weights = new float[channels * channels];
        for (int o = 0; o < channels; o++) weights[o * channels + o] = 1f;

        ProtoWriter.Append(graph, 1, Node(["x", "w"], ["conv"], "Conv", "conv"));
        ProtoWriter.Append(graph, 1, Node(["conv", "scale"], ["scaled"], "Mul", "norm_mul"));

        string last = "scaled";
        if (activation is null)
        {
            ProtoWriter.Append(graph, 1, Node([last, "shift"], ["y"], "Add", "norm_add"));
        }
        else
        {
            ProtoWriter.Append(graph, 1, Node([last, "shift"], ["preact"], "Add", "norm_add"));
            ProtoWriter.Append(graph, 1, Node(["preact"], ["y"], activation, "act"));
        }

        if (extraConsumer)
        {
            // A second reader of the Conv output, and a graph output for it so it stays live.
            ProtoWriter.Append(graph, 1, Node(["conv"], ["side"], "Relu", "side"));
        }

        ProtoWriter.AppendString(graph, 2, "conv-chain");
        ProtoWriter.Append(graph, 5, FloatTensor("w", weights, [channels, channels, 1, 1]));
        ProtoWriter.Append(graph, 5, FloatTensor("scale", scale, constantShape));
        ProtoWriter.Append(graph, 5, FloatTensor("shift", shift, constantShape));
        ProtoWriter.Append(graph, 11, ValueInfo("x", [1, channels, 2, 2]));
        ProtoWriter.Append(graph, 12, ValueInfo("y", [1, channels, 2, 2]));
        if (extraConsumer) ProtoWriter.Append(graph, 12, ValueInfo("side", [1, channels, 2, 2]));

        return ProtoWriter.WrapModel(graph);
    }

    internal static byte[] Node(string[] inputs, string[] outputs, string opType, string name)
    {
        var node = new List<byte>();
        foreach (string input in inputs) ProtoWriter.AppendString(node, 1, input);
        foreach (string output in outputs) ProtoWriter.AppendString(node, 2, output);
        ProtoWriter.AppendString(node, 3, name);
        ProtoWriter.AppendString(node, 4, opType);
        return node.ToArray();
    }

    internal static byte[] FloatTensor(string name, float[] values, long[] dims)
    {
        var tensor = new List<byte>();
        foreach (long dim in dims) ProtoWriter.AppendVarintField(tensor, 1, (ulong)dim);
        ProtoWriter.AppendVarintField(tensor, 2, 1);   // FLOAT
        ProtoWriter.AppendString(tensor, 8, name);
        ProtoWriter.Append(tensor, 9, MemoryMarshal.AsBytes<float>(values).ToArray());
        return tensor.ToArray();
    }

    internal static byte[] ValueInfo(string name, long[] dims)
    {
        var shape = new List<byte>();
        foreach (long dim in dims)
        {
            var dimension = new List<byte>();
            ProtoWriter.AppendVarintField(dimension, 1, (ulong)dim);
            ProtoWriter.Append(shape, 1, dimension.ToArray());
        }

        var tensorType = new List<byte>();
        ProtoWriter.AppendVarintField(tensorType, 1, 1);   // FLOAT
        ProtoWriter.Append(tensorType, 2, shape.ToArray());

        var typeProto = new List<byte>();
        ProtoWriter.Append(typeProto, 1, tensorType.ToArray());

        var info = new List<byte>();
        ProtoWriter.AppendString(info, 1, name);
        ProtoWriter.Append(info, 2, typeProto.ToArray());
        return info.ToArray();
    }
}

/// <summary><c>Conv -> (Sigmoid, Mul)</c>, the shape these exports use for SiLU.</summary>
internal static class SiLUModel
{
    public static byte[] Build()
    {
        const int channels = 2;
        var weights = new float[channels * channels];
        for (int o = 0; o < channels; o++) weights[o * channels + o] = 1f;

        var graph = new List<byte>();
        ProtoWriter.Append(graph, 1, ConvChainModel.Node(["x", "w"], ["conv"], "Conv", "conv"));
        ProtoWriter.Append(graph, 1, ConvChainModel.Node(["conv"], ["sig"], "Sigmoid", "sig"));
        ProtoWriter.Append(graph, 1, ConvChainModel.Node(["conv", "sig"], ["y"], "Mul", "silu"));
        ProtoWriter.AppendString(graph, 2, "silu");
        ProtoWriter.Append(graph, 5, ConvChainModel.FloatTensor("w", weights, [channels, channels, 1, 1]));
        ProtoWriter.Append(graph, 11, ConvChainModel.ValueInfo("x", [1, channels, 2, 2]));
        ProtoWriter.Append(graph, 12, ConvChainModel.ValueInfo("y", [1, channels, 2, 2]));
        return ProtoWriter.WrapModel(graph);
    }
}

/// <summary>Shared protobuf wire-format writing for the hand-built test models.</summary>
internal static class ProtoWriter
{
    public static byte[] WrapModel(List<byte> graph)
    {
        var model = new List<byte>();
        AppendVarintField(model, 1, 8);            // ir_version
        AppendString(model, 2, "xberg-tests");     // producer_name
        Append(model, 7, graph.ToArray());         // graph
        var opset = new List<byte>();
        AppendVarintField(opset, 2, 16);
        Append(model, 8, opset.ToArray());
        return model.ToArray();
    }

    public static void Append(List<byte> into, int fieldNumber, byte[] payload)
    {
        WriteVarint(into, ((ulong)fieldNumber << 3) | 2);
        WriteVarint(into, (ulong)payload.Length);
        into.AddRange(payload);
    }

    public static void AppendString(List<byte> into, int fieldNumber, string value) =>
        Append(into, fieldNumber, Encoding.UTF8.GetBytes(value));

    public static void AppendVarintField(List<byte> into, int fieldNumber, ulong value)
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
