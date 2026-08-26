using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Xberg.Internal.Onnx;

/// <summary>ONNX <c>AttributeProto.AttributeType</c>.</summary>
internal enum AttributeType
{
    Undefined = 0,
    Float = 1,
    Int = 2,
    String = 3,
    Tensor = 4,
    Graph = 5,
    Floats = 6,
    Ints = 7,
    Strings = 8,
    Tensors = 9,
    Graphs = 10,
}

/// <summary>One node attribute. Only the forms the layout graphs actually carry are decoded.</summary>
internal sealed class OnnxAttribute
{
    public string Name = "";
    public AttributeType Type;
    public float Float;
    public long Int;
    public string String = "";
    public float[] Floats = [];
    public long[] Ints = [];
    public Tensor? Tensor;

    /// <summary>
    /// The subgraph a control-flow attribute carries — <c>Loop</c>'s <c>body</c>, <c>If</c>'s
    /// branches.
    /// </summary>
    /// <remarks>
    /// A subgraph is a graph in its own right, with its own nodes, initializers and formal
    /// inputs, but it also reads outer-scope values by name; the session supplies those, so what
    /// is stored here is only what the wire format carries.
    /// </remarks>
    public OnnxSubgraph? Graph;
}

/// <summary>A nested graph, as carried by a control-flow node's attribute.</summary>
internal sealed class OnnxSubgraph
{
    public required OnnxNode[] Nodes { get; init; }
    public required Dictionary<string, Tensor> Initializers { get; init; }
    public required OnnxValueInfo[] Inputs { get; init; }
    public required OnnxValueInfo[] Outputs { get; init; }
}

/// <summary>An activation folded into the producing node, applied as part of its output pass.</summary>
internal enum FusedActivation
{
    None,
    Relu,
    Sigmoid,
    /// <summary>SiLU / swish: <c>x * sigmoid(x)</c>, which these graphs spell as a
    /// <c>Sigmoid</c> and a <c>Mul</c> sharing one producer.</summary>
    SiLU,
}

/// <summary>One graph node: an operator instance with named inputs and outputs.</summary>
internal sealed class OnnxNode
{
    public string Name = "";
    public string OpType = "";
    public string Domain = "";
    public string[] Inputs = [];
    public string[] Outputs = [];
    public OnnxAttribute[] Attributes = [];

    /// <summary>Activation fused into this node by <see cref="GraphOptimizer"/>, if any.</summary>
    public FusedActivation Activation = FusedActivation.None;

    public OnnxNode Clone() => new()
    {
        Name = Name,
        OpType = OpType,
        Domain = Domain,
        Inputs = (string[])Inputs.Clone(),
        Outputs = (string[])Outputs.Clone(),
        Attributes = Attributes,
        Activation = Activation,
    };

    public OnnxAttribute? Attr(string name)
    {
        foreach (var a in Attributes) if (a.Name == name) return a;
        return null;
    }

    public long AttrInt(string name, long fallback) => Attr(name)?.Int ?? fallback;
    public float AttrFloat(string name, float fallback) => Attr(name) is { } a ? a.Float : fallback;
    public string AttrString(string name, string fallback) => Attr(name) is { } a ? a.String : fallback;

    public long[]? AttrInts(string name) => Attr(name)?.Ints;

    public override string ToString() =>
        $"{OpType}({string.Join(", ", Inputs)}) -> {string.Join(", ", Outputs)}";
}

/// <summary>A declared graph input or output, with its element type (shape is not used).</summary>
internal sealed class OnnxValueInfo
{
    public string Name = "";
    public ElementType ElementType;
}

/// <summary>A parsed ONNX model: the graph, its constants, and its declared boundary.</summary>
internal sealed class OnnxModel
{
    public required OnnxNode[] Nodes { get; init; }
    public required Dictionary<string, Tensor> Initializers { get; init; }
    public required OnnxValueInfo[] Inputs { get; init; }
    public required OnnxValueInfo[] Outputs { get; init; }
    public required int OpsetVersion { get; init; }

    /// <summary>Graph inputs that are genuine inputs — some exports also list initializers here.</summary>
    public IEnumerable<OnnxValueInfo> FeedInputs => Inputs.Where(i => !Initializers.ContainsKey(i.Name));

    public static OnnxModel Load(string path) => Parse(File.ReadAllBytes(path));

    public static OnnxModel Parse(ReadOnlySpan<byte> bytes)
    {
        OnnxNode[]? nodes = null;
        Dictionary<string, Tensor>? initializers = null;
        OnnxValueInfo[]? inputs = null;
        OnnxValueInfo[]? outputs = null;
        int opset = 0;

        var reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out var wire))
        {
            switch (field)
            {
                case 7 when wire == WireType.LengthDelimited: // graph
                    ParseGraph(reader.ReadBytes(), out nodes, out initializers, out inputs, out outputs);
                    break;
                case 8 when wire == WireType.LengthDelimited: // opset_import
                    ParseOpsetId(reader.ReadBytes(), ref opset);
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (nodes is null || initializers is null || inputs is null || outputs is null)
            throw new InvalidDataException("onnx: model has no graph");

        return new OnnxModel
        {
            Nodes = nodes,
            Initializers = initializers,
            Inputs = inputs,
            Outputs = outputs,
            OpsetVersion = opset,
        };
    }

    /// <summary>Record the default-domain opset version; custom domains are not supported.</summary>
    private static void ParseOpsetId(ReadOnlySpan<byte> bytes, ref int opset)
    {
        var r = new ProtoReader(bytes);
        string domain = "";
        long version = 0;
        while (r.TryReadTag(out int f, out var w))
        {
            switch (f)
            {
                case 1 when w == WireType.LengthDelimited: domain = r.ReadString(); break;
                case 2 when w == WireType.Varint: version = r.ReadInt64(); break;
                default: r.SkipField(w); break;
            }
        }
        if (domain.Length == 0 || domain == "ai.onnx") opset = (int)version;
    }

    private static void ParseGraph(
        ReadOnlySpan<byte> bytes,
        out OnnxNode[] nodes,
        out Dictionary<string, Tensor> initializers,
        out OnnxValueInfo[] inputs,
        out OnnxValueInfo[] outputs)
    {
        var nodeList = new List<OnnxNode>();
        var initMap = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        var inputList = new List<OnnxValueInfo>();
        var outputList = new List<OnnxValueInfo>();

        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out var w))
        {
            switch (f)
            {
                case 1 when w == WireType.LengthDelimited: // node
                    nodeList.Add(ParseNode(r.ReadBytes()));
                    break;
                case 5 when w == WireType.LengthDelimited: // initializer
                {
                    var (name, tensor) = ParseTensor(r.ReadBytes());
                    if (name.Length > 0) initMap[name] = tensor;
                    break;
                }
                case 11 when w == WireType.LengthDelimited: // input
                    inputList.Add(ParseValueInfo(r.ReadBytes()));
                    break;
                case 12 when w == WireType.LengthDelimited: // output
                    outputList.Add(ParseValueInfo(r.ReadBytes()));
                    break;
                default:
                    r.SkipField(w);
                    break;
            }
        }

        nodes = nodeList.ToArray();
        initializers = initMap;
        inputs = inputList.ToArray();
        outputs = outputList.ToArray();
    }

    private static OnnxNode ParseNode(ReadOnlySpan<byte> bytes)
    {
        var inputs = new List<string>();
        var outputs = new List<string>();
        var attrs = new List<OnnxAttribute>();
        string name = "", opType = "", domain = "";

        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out var w))
        {
            switch (f)
            {
                case 1 when w == WireType.LengthDelimited: inputs.Add(r.ReadString()); break;
                case 2 when w == WireType.LengthDelimited: outputs.Add(r.ReadString()); break;
                case 3 when w == WireType.LengthDelimited: name = r.ReadString(); break;
                case 4 when w == WireType.LengthDelimited: opType = r.ReadString(); break;
                case 5 when w == WireType.LengthDelimited: attrs.Add(ParseAttribute(r.ReadBytes())); break;
                case 7 when w == WireType.LengthDelimited: domain = r.ReadString(); break;
                default: r.SkipField(w); break;
            }
        }

        return new OnnxNode
        {
            Name = name,
            OpType = opType,
            Domain = domain,
            Inputs = inputs.ToArray(),
            Outputs = outputs.ToArray(),
            Attributes = attrs.ToArray(),
        };
    }

    private static OnnxAttribute ParseAttribute(ReadOnlySpan<byte> bytes)
    {
        var attr = new OnnxAttribute();
        var floats = new List<float>();
        var ints = new List<long>();

        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out var w))
        {
            switch (f)
            {
                case 1 when w == WireType.LengthDelimited: attr.Name = r.ReadString(); break;
                case 2 when w == WireType.Fixed32: attr.Float = r.ReadFloat(); break;
                case 3 when w == WireType.Varint: attr.Int = r.ReadInt64(); break;
                case 4 when w == WireType.LengthDelimited: attr.String = r.ReadString(); break;
                case 5 when w == WireType.LengthDelimited: attr.Tensor = ParseTensor(r.ReadBytes()).Tensor; break;
                case 6 when w == WireType.LengthDelimited: attr.Graph = ParseSubgraph(r.ReadBytes()); break;
                case 7: r.ReadPackedFloat(w, floats); break;
                case 8: r.ReadPackedInt64(w, ints); break;
                case 20 when w == WireType.Varint: attr.Type = (AttributeType)r.ReadInt32(); break;
                default: r.SkipField(w); break;
            }
        }

        attr.Floats = floats.ToArray();
        attr.Ints = ints.ToArray();
        return attr;
    }

    /// <summary>Parse a nested <c>GraphProto</c> carried by a control-flow attribute.</summary>
    private static OnnxSubgraph ParseSubgraph(ReadOnlySpan<byte> bytes)
    {
        ParseGraph(bytes, out var nodes, out var initializers, out var inputs, out var outputs);
        return new OnnxSubgraph
        {
            Nodes = nodes,
            Initializers = initializers,
            Inputs = inputs,
            Outputs = outputs,
        };
    }

    private static OnnxValueInfo ParseValueInfo(ReadOnlySpan<byte> bytes)
    {
        var info = new OnnxValueInfo();
        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out var w))
        {
            switch (f)
            {
                case 1 when w == WireType.LengthDelimited: info.Name = r.ReadString(); break;
                case 2 when w == WireType.LengthDelimited: info.ElementType = ParseTypeProto(r.ReadBytes()); break;
                default: r.SkipField(w); break;
            }
        }
        return info;
    }

    /// <summary>Element type of a <c>TypeProto</c>. Only the tensor case appears in these graphs.</summary>
    private static ElementType ParseTypeProto(ReadOnlySpan<byte> bytes)
    {
        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out var w))
        {
            if (f == 1 && w == WireType.LengthDelimited)
            {
                var inner = new ProtoReader(r.ReadBytes());
                while (inner.TryReadTag(out int g, out var iw))
                {
                    if (g == 1 && iw == WireType.Varint) return (ElementType)inner.ReadInt32();
                    inner.SkipField(iw);
                }
                return ElementType.Undefined;
            }
            r.SkipField(w);
        }
        return ElementType.Undefined;
    }

    /// <summary>
    /// Decode a <c>TensorProto</c>. Values arrive either in <c>raw_data</c> (little-endian
    /// packed bytes, the form every real exporter uses for weights) or in one of the typed
    /// repeated fields; both are handled.
    /// </summary>
    internal static (string Name, Tensor Tensor) ParseTensor(ReadOnlySpan<byte> bytes)
    {
        var dims = new List<long>();
        var floatData = new List<float>();
        var int64Data = new List<long>();
        var int32Data = new List<long>();
        var doubleData = new List<double>();
        ReadOnlySpan<byte> rawData = default;
        bool hasRaw = false;
        string name = "";
        var dataType = ElementType.Undefined;

        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out var w))
        {
            switch (f)
            {
                case 1: r.ReadPackedInt64(w, dims); break;
                case 2 when w == WireType.Varint: dataType = (ElementType)r.ReadInt32(); break;
                case 4: r.ReadPackedFloat(w, floatData); break;
                case 5: r.ReadPackedInt64(w, int32Data); break;
                case 7: r.ReadPackedInt64(w, int64Data); break;
                case 8 when w == WireType.LengthDelimited: name = r.ReadString(); break;
                case 9 when w == WireType.LengthDelimited: rawData = r.ReadBytes(); hasRaw = true; break;
                case 10 when w == WireType.LengthDelimited:
                {
                    var inner = new ProtoReader(r.ReadBytes());
                    while (!inner.Eof) doubleData.Add(inner.ReadDouble());
                    break;
                }
                case 13 when w == WireType.LengthDelimited:
                    // external_data: weights stored beside the model. Not produced for any
                    // model xberg pins, and silently ignoring it would yield zeros.
                    throw new NotSupportedException("onnx: external tensor data is not supported");
                default: r.SkipField(w); break;
            }
        }

        var shape = new int[dims.Count];
        for (int i = 0; i < dims.Count; i++) shape[i] = checked((int)dims[i]);
        int count = Tensor.ElementCount(shape);

        Tensor tensor = dataType switch
        {
            ElementType.Float => Tensor.FromFloats(
                hasRaw ? DecodeRawFloats(rawData, count) : PadFloats(floatData, count), shape),
            ElementType.Double => Tensor.FromFloats(
                hasRaw ? DecodeRawDoubles(rawData, count) : doubleData.Select(d => (float)d).ToArray(), shape),
            ElementType.Float16 => Tensor.FromFloats(
                hasRaw ? DecodeRawHalves(rawData, count) : PadFloats(floatData, count), shape),
            ElementType.Int64 => Tensor.FromLongs(
                hasRaw ? DecodeRawInt64(rawData, count) : PadLongs(int64Data, count), dataType, shape),
            ElementType.Int32 => Tensor.FromLongs(
                hasRaw ? DecodeRawInt32(rawData, count) : PadLongs(int32Data, count), dataType, shape),
            ElementType.Bool or ElementType.UInt8 => Tensor.FromLongs(
                hasRaw ? DecodeRawBytes(rawData, count, signed: false) : PadLongs(int32Data, count), dataType, shape),
            ElementType.Int8 => Tensor.FromLongs(
                hasRaw ? DecodeRawBytes(rawData, count, signed: true) : PadLongs(int32Data, count), dataType, shape),
            _ => throw new NotSupportedException($"onnx: tensor data type {dataType} is not supported"),
        };

        return (name, tensor);
    }

    private static float[] PadFloats(List<float> data, int count)
    {
        if (data.Count == count) return data.ToArray();
        var result = new float[count];
        data.CopyTo(result, 0);
        return result;
    }

    private static long[] PadLongs(List<long> data, int count)
    {
        if (data.Count == count) return data.ToArray();
        var result = new long[count];
        data.CopyTo(result, 0);
        return result;
    }

    private static float[] DecodeRawFloats(ReadOnlySpan<byte> raw, int count)
    {
        var result = new float[count];
        // Little-endian is the wire format and every target Xberg runs on is little-endian,
        // so the whole block reinterprets in one copy.
        MemoryMarshal.Cast<byte, float>(raw[..(count * 4)]).CopyTo(result);
        return result;
    }

    private static float[] DecodeRawDoubles(ReadOnlySpan<byte> raw, int count)
    {
        var result = new float[count];
        var src = MemoryMarshal.Cast<byte, double>(raw[..(count * 8)]);
        for (int i = 0; i < count; i++) result[i] = (float)src[i];
        return result;
    }

    private static float[] DecodeRawHalves(ReadOnlySpan<byte> raw, int count)
    {
        var result = new float[count];
        var src = MemoryMarshal.Cast<byte, Half>(raw[..(count * 2)]);
        for (int i = 0; i < count; i++) result[i] = (float)src[i];
        return result;
    }

    private static long[] DecodeRawInt64(ReadOnlySpan<byte> raw, int count)
    {
        var result = new long[count];
        MemoryMarshal.Cast<byte, long>(raw[..(count * 8)]).CopyTo(result);
        return result;
    }

    private static long[] DecodeRawInt32(ReadOnlySpan<byte> raw, int count)
    {
        var result = new long[count];
        var src = MemoryMarshal.Cast<byte, int>(raw[..(count * 4)]);
        for (int i = 0; i < count; i++) result[i] = src[i];
        return result;
    }

    private static long[] DecodeRawBytes(ReadOnlySpan<byte> raw, int count, bool signed)
    {
        var result = new long[count];
        for (int i = 0; i < count; i++) result[i] = signed ? (sbyte)raw[i] : raw[i];
        return result;
    }
}
