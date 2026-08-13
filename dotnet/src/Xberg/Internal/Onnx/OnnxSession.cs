using Xberg.Internal.Onnx.Ops;

namespace Xberg.Internal.Onnx;

/// <summary>
/// Executes a parsed <see cref="OnnxModel"/>.
/// <para>
/// ONNX stores nodes in topological order, so execution is a single forward pass with a
/// name-to-tensor environment. The one thing the pass does beyond dispatch is <em>free
/// intermediates as soon as their last consumer has run</em>: RT-DETR's live values total
/// well over a gigabyte if every activation is retained to the end, and keeping only the
/// live set turns that into a working set the collector can actually recycle.
/// </para>
/// <para>
/// This is deliberately not a general ONNX runtime. It covers the operators the layout
/// models use, at the opset they were exported with, and throws on anything else rather
/// than approximating it — a wrong-but-plausible kernel is far more expensive to find than
/// a missing one.
/// </para>
/// </summary>
internal sealed class OnnxSession
{
    private readonly OnnxModel _model;
    /// <summary>For each node index, the values whose last use is that node.</summary>
    private readonly List<string>[] _lastUse;

    public OnnxSession(OnnxModel model)
    {
        _model = model;
        _lastUse = ComputeLastUse(model);
    }

    public static OnnxSession Load(string path) => new(OnnxModel.Load(path));

    public OnnxModel Model => _model;

    /// <summary>Names of the values the caller must supply.</summary>
    public IEnumerable<string> InputNames => _model.FeedInputs.Select(i => i.Name);

    /// <summary>Names the graph declares as outputs, in declaration order.</summary>
    public IReadOnlyList<string> OutputNames => _model.Outputs.Select(o => o.Name).ToArray();

    /// <summary>
    /// Run the graph and return its declared outputs.
    /// </summary>
    public Dictionary<string, Tensor> Run(IReadOnlyDictionary<string, Tensor> feeds)
        => Run(feeds, capture: null);

    /// <summary>
    /// Run the graph, optionally capturing every intermediate value.
    /// <para>
    /// <paramref name="capture"/> is what the parity harness uses: with it set, no value is
    /// released early and the caller sees the same per-node tensors the Python reference
    /// dumps, so a divergence is attributed to the first node that produced it.
    /// </para>
    /// </summary>
    public Dictionary<string, Tensor> Run(
        IReadOnlyDictionary<string, Tensor> feeds, Dictionary<string, Tensor>? capture) =>
        Run(feeds, capture, profile: null);

    /// <summary>
    /// Run the graph, optionally capturing intermediates and/or accumulating per-operator
    /// timings into <paramref name="profile"/> (microseconds, keyed by op type).
    /// </summary>
    public Dictionary<string, Tensor> Run(
        IReadOnlyDictionary<string, Tensor> feeds,
        Dictionary<string, Tensor>? capture,
        Dictionary<string, double>? profile)
    {
        var env = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (var (name, tensor) in _model.Initializers) env[name] = tensor;
        foreach (var (name, tensor) in feeds) env[name] = tensor;

        foreach (var input in _model.FeedInputs)
        {
            if (!env.ContainsKey(input.Name))
                throw new InvalidOperationException($"onnx: missing input '{input.Name}'");
        }

        var declaredOutputs = new HashSet<string>(OutputNames, StringComparer.Ordinal);

        for (int i = 0; i < _model.Nodes.Length; i++)
        {
            var node = _model.Nodes[i];
            Tensor?[] outputs;
            long startTicks = profile is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                outputs = Execute(node, env);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"onnx: node #{i} {node.OpType} ('{node.Name}') failed: {ex.Message}", ex);
            }

            if (profile is not null)
            {
                double microseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
                                      * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
                profile[node.OpType] = profile.GetValueOrDefault(node.OpType) + microseconds;
            }

            for (int o = 0; o < node.Outputs.Length && o < outputs.Length; o++)
            {
                if (node.Outputs[o].Length == 0 || outputs[o] is null) continue;
                env[node.Outputs[o]] = outputs[o]!;
                capture?[node.Outputs[o]] = outputs[o]!;
            }

            if (capture is null)
            {
                foreach (string dead in _lastUse[i])
                    if (!declaredOutputs.Contains(dead)) env.Remove(dead);
            }
        }

        var result = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (string name in OutputNames)
        {
            if (env.TryGetValue(name, out var tensor)) result[name] = tensor;
            else throw new InvalidOperationException($"onnx: graph output '{name}' was never produced");
        }
        return result;
    }

    /// <summary>
    /// For each node, the values that are consumed for the last time by it. Graph outputs and
    /// initializers are excluded — the former are the result, the latter are shared constants.
    /// </summary>
    private static List<string>[] ComputeLastUse(OnnxModel model)
    {
        var lastUse = new List<string>[model.Nodes.Length];
        for (int i = 0; i < lastUse.Length; i++) lastUse[i] = [];

        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < model.Nodes.Length; i++)
            foreach (string input in model.Nodes[i].Inputs)
                if (input.Length > 0) lastIndex[input] = i;

        foreach (var (name, index) in lastIndex)
            if (!model.Initializers.ContainsKey(name)) lastUse[index].Add(name);
        return lastUse;
    }

    /// <summary>Fetch an operand, or null for an omitted optional input.</summary>
    private static Tensor? Optional(OnnxNode node, IReadOnlyDictionary<string, Tensor> env, int index)
    {
        if (index >= node.Inputs.Length) return null;
        string name = node.Inputs[index];
        if (name.Length == 0) return null;
        return env.TryGetValue(name, out var tensor)
            ? tensor
            : throw new InvalidOperationException($"onnx: value '{name}' is not available");
    }

    private static Tensor Required(OnnxNode node, IReadOnlyDictionary<string, Tensor> env, int index) =>
        Optional(node, env, index) ?? throw new InvalidOperationException(
            $"onnx: {node.OpType} requires input #{index}");

    /// <summary>
    /// Read an operand that may be given either as an input (newer opsets) or as an
    /// attribute (older ones). Several ops moved axes and sizes across that boundary, and the
    /// pinned models straddle the change.
    /// </summary>
    private static long[] IntsFromInputOrAttribute(
        OnnxNode node, IReadOnlyDictionary<string, Tensor> env, int inputIndex, string attributeName)
    {
        if (Optional(node, env, inputIndex) is { } tensor)
        {
            var values = new long[tensor.Count];
            for (int i = 0; i < tensor.Count; i++) values[i] = tensor.GetLong(i);
            return values;
        }
        return node.AttrInts(attributeName) ?? [];
    }

    /// <summary>
    /// Run a single node against an environment the caller supplies.
    /// <para>
    /// This is how a kernel is tested in isolation: feed the node the reference's own
    /// recorded inputs and compare only its output. Whole-graph comparison cannot separate a
    /// wrong kernel from a correct one amplifying drift that arrived from upstream; this can.
    /// </para>
    /// </summary>
    public Tensor?[] ExecuteNode(OnnxNode node, Dictionary<string, Tensor> env) => Execute(node, env);

    private Tensor?[] Execute(OnnxNode node, Dictionary<string, Tensor> env)
    {
        switch (node.OpType)
        {
            case "Constant":
                return [ConstantValue(node)];

            case "Identity":
                return [Required(node, env, 0)];

            case "Add": return [Elementwise.Binary(Required(node, env, 0), Required(node, env, 1), BinaryKind.Add)];
            case "Sub": return [Elementwise.Binary(Required(node, env, 0), Required(node, env, 1), BinaryKind.Sub)];
            case "Mul": return [Elementwise.Binary(Required(node, env, 0), Required(node, env, 1), BinaryKind.Mul)];
            case "Div": return [Elementwise.Binary(Required(node, env, 0), Required(node, env, 1), BinaryKind.Div)];
            case "Pow": return [Elementwise.Binary(Required(node, env, 0), Required(node, env, 1), BinaryKind.Pow)];

            case "Min":
            case "Max":
            {
                var kind = node.OpType == "Min" ? BinaryKind.Min : BinaryKind.Max;
                var accumulator = Required(node, env, 0);
                for (int i = 1; i < node.Inputs.Length; i++)
                    accumulator = Elementwise.Binary(accumulator, Required(node, env, i), kind);
                return [accumulator];
            }

            case "Relu": return [Elementwise.Relu(Required(node, env, 0))];
            case "Sigmoid": return [Elementwise.Sigmoid(Required(node, env, 0))];
            case "Sqrt": return [Elementwise.Sqrt(Required(node, env, 0))];
            case "Exp": return [Elementwise.Exp(Required(node, env, 0))];
            case "Log": return [Elementwise.Log(Required(node, env, 0))];
            case "Abs": return [Elementwise.Abs(Required(node, env, 0))];
            case "Tanh": return [Elementwise.Tanh(Required(node, env, 0))];
            case "Neg": return [Elementwise.Neg(Required(node, env, 0))];
            case "Erf": return [Elementwise.Erf(Required(node, env, 0))];
            case "HardSwish": return [Elementwise.HardSwish(Required(node, env, 0))];

            case "HardSigmoid":
                return [Elementwise.HardSigmoid(
                    Required(node, env, 0), node.AttrFloat("alpha", 0.2f), node.AttrFloat("beta", 0.5f))];

            case "Clip":
            {
                // Opset 11 moved min and max from attributes to optional inputs; an omitted
                // bound means "unbounded on that side", not zero.
                var value = Required(node, env, 0);
                float min = Optional(node, env, 1) is { Count: > 0 } lo ? lo.GetFloat(0) : node.AttrFloat("min", float.NegativeInfinity);
                float max = Optional(node, env, 2) is { Count: > 0 } hi ? hi.GetFloat(0) : node.AttrFloat("max", float.PositiveInfinity);
                return [Elementwise.Clip(value, min, max)];
            }

            case "Cast":
                return [Elementwise.Cast(Required(node, env, 0), (ElementType)node.AttrInt("to", (long)ElementType.Float))];

            case "Shape":
            {
                var attr = node.Attr("end");
                return [Shapes.Shape(Required(node, env, 0), node.AttrInt("start", 0), attr is null ? null : attr.Int)];
            }

            case "Reshape":
                return [Shapes.Reshape(Required(node, env, 0), Required(node, env, 1), node.AttrInt("allowzero", 0) != 0)];

            case "Unsqueeze":
                return [Shapes.Unsqueeze(Required(node, env, 0), IntsFromInputOrAttribute(node, env, 1, "axes"))];

            case "Squeeze":
                return [Shapes.Squeeze(Required(node, env, 0), IntsFromInputOrAttribute(node, env, 1, "axes"))];

            case "Flatten":
                return [Shapes.Flatten(Required(node, env, 0), node.AttrInt("axis", 1))];

            case "Transpose":
                return [Shapes.Transpose(Required(node, env, 0), node.AttrInts("perm") ?? [])];

            case "Concat":
            {
                var inputs = new List<Tensor>(node.Inputs.Length);
                for (int i = 0; i < node.Inputs.Length; i++) inputs.Add(Required(node, env, i));
                return [Shapes.Concat(inputs, node.AttrInt("axis", 0))];
            }

            case "Slice":
                return [Shapes.Slice(
                    Required(node, env, 0), Required(node, env, 1), Required(node, env, 2),
                    Optional(node, env, 3), Optional(node, env, 4))];

            case "Gather":
                return [Shapes.Gather(Required(node, env, 0), Required(node, env, 1), node.AttrInt("axis", 0))];

            case "GatherElements":
                return [Shapes.GatherElements(Required(node, env, 0), Required(node, env, 1), node.AttrInt("axis", 0))];

            case "Expand":
                return [Shapes.Expand(Required(node, env, 0), Required(node, env, 1))];

            case "Tile":
                return [Shapes.Tile(Required(node, env, 0), Required(node, env, 1))];

            case "ConstantOfShape":
                return [Shapes.ConstantOfShape(Required(node, env, 0), node.Attr("value")?.Tensor)];

            case "Split":
            {
                var sizesTensor = Optional(node, env, 1);
                int[]? sizes = sizesTensor?.ToIntArray() ?? node.AttrInts("split")?.Select(v => (int)v).ToArray();
                int count = (int)node.AttrInt("num_outputs", node.Outputs.Length);
                return Shapes.Split(Required(node, env, 0), node.AttrInt("axis", 0), sizes, count);
            }

            case "ReduceSum":
                return [Reductions.Reduce(
                    Required(node, env, 0), IntsFromInputOrAttribute(node, env, 1, "axes"),
                    node.AttrInt("keepdims", 1) != 0, node.AttrInt("noop_with_empty_axes", 0) != 0, ReduceKind.Sum)];

            case "ReduceMean":
                return [Reductions.Reduce(
                    Required(node, env, 0), IntsFromInputOrAttribute(node, env, 1, "axes"),
                    node.AttrInt("keepdims", 1) != 0, node.AttrInt("noop_with_empty_axes", 0) != 0, ReduceKind.Mean)];

            case "ReduceMax":
                return [Reductions.Reduce(
                    Required(node, env, 0), IntsFromInputOrAttribute(node, env, 1, "axes"),
                    node.AttrInt("keepdims", 1) != 0, node.AttrInt("noop_with_empty_axes", 0) != 0, ReduceKind.Max)];

            case "ReduceMin":
                return [Reductions.Reduce(
                    Required(node, env, 0), IntsFromInputOrAttribute(node, env, 1, "axes"),
                    node.AttrInt("keepdims", 1) != 0, node.AttrInt("noop_with_empty_axes", 0) != 0, ReduceKind.Min)];

            case "ArgMax":
                return [Reductions.ArgMax(
                    Required(node, env, 0), node.AttrInt("axis", 0),
                    node.AttrInt("keepdims", 1) != 0, node.AttrInt("select_last_index", 0) != 0)];

            case "Softmax":
                return [Reductions.Softmax(Required(node, env, 0), node.AttrInt("axis", -1))];

            case "TopK":
            {
                var k = Required(node, env, 1);
                var (values, indices) = Reductions.TopK(
                    Required(node, env, 0), (int)k.GetLong(0), node.AttrInt("axis", -1),
                    node.AttrInt("largest", 1) != 0, node.AttrInt("sorted", 1) != 0);
                return [values, indices];
            }

            case "MatMul":
                return [Linear.MatMul(Required(node, env, 0), Required(node, env, 1))];

            case "Gemm":
                return [Linear.Gemm(
                    Required(node, env, 0), Required(node, env, 1), Optional(node, env, 2),
                    node.AttrFloat("alpha", 1f), node.AttrFloat("beta", 1f),
                    node.AttrInt("transA", 0) != 0, node.AttrInt("transB", 0) != 0)];

            case "Conv":
                return [Convolution.Conv(
                    Required(node, env, 0), Required(node, env, 1), Optional(node, env, 2),
                    node.AttrInts("strides"), node.AttrInts("pads"), node.AttrInts("dilations"),
                    node.AttrInt("group", 1), node.AttrString("auto_pad", "NOTSET"))];

            case "BatchNormalization":
                return [Convolution.BatchNormalization(
                    Required(node, env, 0), Required(node, env, 1), Required(node, env, 2),
                    Required(node, env, 3), Required(node, env, 4), node.AttrFloat("epsilon", 1e-5f))];

            case "LayerNormalization":
                return [Convolution.LayerNormalization(
                    Required(node, env, 0), Required(node, env, 1), Optional(node, env, 2),
                    node.AttrInt("axis", -1), node.AttrFloat("epsilon", 1e-5f))];

            case "GlobalAveragePool":
                return [Pooling.GlobalAveragePool(Required(node, env, 0))];

            case "MaxPool":
                return [Pooling.MaxPool(
                    Required(node, env, 0), node.AttrInts("kernel_shape"), node.AttrInts("strides"),
                    node.AttrInts("pads"), node.AttrInts("dilations"),
                    node.AttrString("auto_pad", "NOTSET"), node.AttrInt("ceil_mode", 0) != 0)];

            case "AveragePool":
                return [Pooling.AveragePool(
                    Required(node, env, 0), node.AttrInts("kernel_shape"), node.AttrInts("strides"),
                    node.AttrInts("pads"), node.AttrString("auto_pad", "NOTSET"),
                    node.AttrInt("ceil_mode", 0) != 0, node.AttrInt("count_include_pad", 0) != 0)];

            case "Resize":
                // Input 1 is `roi`, used only by tf_crop_and_resize, which these models do not use.
                return [Sampling.Resize(
                    Required(node, env, 0), Optional(node, env, 2), Optional(node, env, 3),
                    node.AttrString("mode", "nearest"),
                    node.AttrString("coordinate_transformation_mode", "half_pixel"),
                    node.AttrString("nearest_mode", "round_prefer_floor"))];

            case "GridSample":
                return [Sampling.GridSample(
                    Required(node, env, 0), Required(node, env, 1),
                    node.AttrString("mode", "bilinear"), node.AttrString("padding_mode", "zeros"),
                    node.AttrInt("align_corners", 0) != 0)];

            default:
                throw new NotSupportedException($"onnx: operator '{node.OpType}' is not implemented");
        }
    }

    /// <summary>
    /// A Constant node's value. The tensor form covers everything these graphs emit; the
    /// scalar and list attribute forms are accepted because exporters use them freely.
    /// </summary>
    private static Tensor ConstantValue(OnnxNode node)
    {
        if (node.Attr("value")?.Tensor is { } tensor) return tensor;
        if (node.Attr("value_float") is { } f) return Tensor.Scalar(f.Float);
        if (node.Attr("value_int") is { } i) return Tensor.Scalar(i.Int);
        if (node.Attr("value_floats") is { } fs) return Tensor.FromFloats(fs.Floats, fs.Floats.Length);
        if (node.Attr("value_ints") is { } ints) return Tensor.FromLongs(ints.Ints, ElementType.Int64, ints.Ints.Length);
        throw new InvalidDataException($"onnx: Constant node '{node.Name}' has no recognised value attribute");
    }
}
