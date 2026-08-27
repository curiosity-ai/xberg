using Xberg.Internal.Onnx;
using Xberg.Internal.Onnx.Ops;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for writing an element-wise result over an operand that dies at the node.
/// </summary>
/// <remarks>
/// The element-wise operators are memory-bound and there are four hundred of them in RT-DETR,
/// so the second buffer they each allocate is worth removing. The risk is entirely in the
/// predicate: reusing one buffer too eagerly corrupts a value some later node still reads, and
/// that surfaces as a plausible-looking detection rather than a crash. Every condition below
/// rules out one way of getting it wrong, and the whole-graph check is
/// <c>--check-reuse</c> in <c>tools/Xberg.OnnxParity</c>, which runs RT-DETR both ways and
/// compares the outputs bit for bit.
/// </remarks>
public class OnnxBufferReuseTests
{
    // ------------------------------------------------------------------ Building a graph

    private static OnnxNode Node(string op, string[] inputs, string[] outputs, string? name = null) =>
        new() { OpType = op, Inputs = inputs, Outputs = outputs, Name = name ?? op };

    private static OnnxValueInfo Value(string name) =>
        new() { Name = name, ElementType = ElementType.Float };

    private static OnnxModel Graph(
        OnnxNode[] nodes, string[] inputs, string[] outputs,
        Dictionary<string, Tensor>? initializers = null) =>
        new()
        {
            Nodes = nodes,
            Initializers = initializers ?? [],
            Inputs = inputs.Select(Value).ToArray(),
            Outputs = outputs.Select(Value).ToArray(),
            OpsetVersion = 16,
        };

    private static Tensor Floats(int[] shape, params float[] values) =>
        Tensor.FromFloats(values, shape);

    /// <summary>Run a graph, reporting which nodes wrote over an operand.</summary>
    private static (Dictionary<string, Tensor> Outputs, bool[] Reused) RunTracked(
        OnnxModel model, Dictionary<string, Tensor> feeds, bool reuse = true)
    {
        var session = new OnnxSession(model, optimize: false, reuseBuffers: reuse);
        var profile = new OnnxSession.ExecutionProfile
        {
            NodeMicroseconds = new double[model.Nodes.Length],
            NodeOutputShapes = new string[model.Nodes.Length],
            NodeReusedOperand = new bool[model.Nodes.Length],
        };
        var outputs = session.Run(feeds, capture: null, profile);
        return (outputs, profile.NodeReusedOperand!);
    }

    // ------------------------------------------------------------------ When reuse happens

    /// <summary>
    /// A value produced by one node and consumed by the next is written over.
    /// </summary>
    /// <remarks>
    /// This is the case the whole thing exists for: a chain of element-wise nodes should walk
    /// one buffer rather than renting a new one at every step.
    /// </remarks>
    [Fact]
    public void AValueThatDiesAtTheNodeIsWrittenOver()
    {
        var model = Graph(
            [Node("Relu", ["x"], ["a"]), Node("Sigmoid", ["a"], ["y"])],
            ["x"], ["y"]);

        var (outputs, reused) = RunTracked(model, new() { ["x"] = Floats([4], -1, 0, 1, 2) });

        // The first node reads a caller-owned feed and must not touch it; the second reads a
        // value the first produced, which nothing else will ever see again.
        Assert.False(reused[0]);
        Assert.True(reused[1]);
        Assert.Equal(4, outputs["y"].Count);
    }

    /// <summary>Two operands of the same shape: the dying one takes the result.</summary>
    [Fact]
    public void AnAdditionWritesOverItsDyingOperand()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),
                Node("Relu", ["x"], ["b"]),
                Node("Add", ["a", "b"], ["y"]),
            ],
            ["x"], ["y"]);

        var (outputs, reused) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) });

        Assert.True(reused[2]);
        Assert.Equal([2f, 4f, 6f], outputs["y"].Floats);
    }

    /// <summary>
    /// A broadcast addition writes over the operand that already has the output's shape.
    /// </summary>
    /// <remarks>
    /// Adding a per-channel bias is the commonest shape in these graphs, and the large operand
    /// is exactly the one worth not copying. The small one is read once per row and must not be
    /// touched.
    /// </remarks>
    [Fact]
    public void ABroadcastAdditionWritesOverTheFullSizeOperand()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),
                Node("Add", ["a", "bias"], ["y"]),
            ],
            ["x"], ["y"],
            new Dictionary<string, Tensor> { ["bias"] = Floats([2, 1], 10, 20) });

        var (outputs, reused) = RunTracked(
            model, new() { ["x"] = Floats([2, 3], 1, 2, 3, 4, 5, 6) });

        Assert.True(reused[1]);
        Assert.Equal([11f, 12f, 13f, 24f, 25f, 26f], outputs["y"].Floats);
    }

    // ------------------------------------------------------------------ When it must not

    /// <summary>
    /// A value a later node still reads is left alone.
    /// </summary>
    /// <remarks>
    /// The residual shape: one value feeds both a branch and the addition that rejoins it.
    /// Overwriting it at the first consumer would hand the second the wrong numbers, and the
    /// graph would still run.
    /// </remarks>
    [Fact]
    public void AValueWithAnotherReaderIsNotWrittenOver()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),
                Node("Sigmoid", ["a"], ["b"]),      // 'a' is read again below, so it must survive
                Node("Add", ["a", "b"], ["y"]),
            ],
            ["x"], ["y"]);

        var feed = Floats([3], 1, 2, 3);
        var (outputs, reused) = RunTracked(model, new() { ["x"] = feed });

        Assert.False(reused[1]);

        // Reuse must not change the answer: the same graph with reuse off is the reference.
        var (plain, _) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) }, reuse: false);
        Assert.Equal(plain["y"].Floats, outputs["y"].Floats);
    }

    /// <summary>
    /// Where one operand dies and another does not, the dying one is chosen.
    /// </summary>
    /// <remarks>
    /// A node can have a dead operand and a live one at once, and picking by "this node has
    /// something to spare" rather than by which operand that is would overwrite the live one.
    /// The counts alone cannot tell them apart: both are held by exactly one name.
    /// </remarks>
    [Fact]
    public void TheDyingOperandIsChosenAndNotTheLiveOne()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),         // read by both additions below
                Node("Sigmoid", ["x"], ["c"]),      // read once, so it dies at the first
                Node("Add", ["a", "c"], ["t"]),
                Node("Add", ["a", "t"], ["y"]),
            ],
            ["x"], ["y"]);

        var (outputs, reused) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) });

        Assert.True(reused[2]);

        var (plain, _) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) }, reuse: false);
        Assert.Equal(plain["y"].Floats, outputs["y"].Floats);
    }

    /// <summary>
    /// An initializer is a constant shared by every run and is never written over.
    /// </summary>
    /// <remarks>
    /// Corrupting one would leave the first inference correct and every later one wrong, which
    /// is the hardest possible version of this bug to find.
    /// </remarks>
    [Fact]
    public void AnInitializerIsNotWrittenOver()
    {
        var constant = Floats([3], 1, 2, 3);
        var model = Graph(
            [Node("Relu", ["k"], ["y"])],
            [], ["y"],
            new Dictionary<string, Tensor> { ["k"] = constant });

        var (_, reused) = RunTracked(model, []);

        Assert.False(reused[0]);
        Assert.Equal([1f, 2f, 3f], constant.Floats);
    }

    /// <summary>The caller's own input buffer is not written over either.</summary>
    [Fact]
    public void AFeedIsNotWrittenOver()
    {
        var model = Graph([Node("Relu", ["x"], ["y"])], ["x"], ["y"]);
        var feed = Floats([4], -1, -2, 3, 4);

        var (_, reused) = RunTracked(model, new() { ["x"] = feed });

        Assert.False(reused[0]);
        Assert.Equal([-1f, -2f, 3f, 4f], feed.Floats);
    }

    /// <summary>
    /// A value bound under a second name is not written over.
    /// </summary>
    /// <remarks>
    /// <c>Identity</c> and <c>Reshape</c> hand back the same storage under a new name, so a
    /// value being dead says nothing about whether the memory behind it is.
    /// </remarks>
    [Fact]
    public void AnAliasedValueIsNotWrittenOver()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),
                Node("Identity", ["a"], ["alias"]),  // 'a' and 'alias' now share one array
                Node("Sigmoid", ["a"], ["b"]),
                Node("Add", ["b", "alias"], ["y"]),
            ],
            ["x"], ["y"]);

        var (outputs, reused) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) });

        Assert.False(reused[2]);

        var (plain, _) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) }, reuse: false);
        Assert.Equal(plain["y"].Floats, outputs["y"].Floats);
    }

    /// <summary>
    /// An operand a node names twice is read twice and is not written over.
    /// </summary>
    /// <remarks>
    /// It is held once, so a reference count alone would call it free.
    /// </remarks>
    [Fact]
    public void AnOperandNamedTwiceIsNotWrittenOver()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),
                Node("Add", ["a", "a"], ["y"]),
            ],
            ["x"], ["y"]);

        var (outputs, reused) = RunTracked(model, new() { ["x"] = Floats([3], 1, 2, 3) });

        Assert.False(reused[1]);
        Assert.Equal([2f, 4f, 6f], outputs["y"].Floats);
    }

    /// <summary>A graph output is what the caller is about to be handed, so it is left alone.</summary>
    [Fact]
    public void AGraphOutputIsNotWrittenOver()
    {
        var model = Graph(
            [
                Node("Relu", ["x"], ["a"]),
                Node("Sigmoid", ["a"], ["y"]),
            ],
            ["x"], ["a", "y"]);        // 'a' is a declared output as well as an intermediate

        var (outputs, reused) = RunTracked(model, new() { ["x"] = Floats([3], -1, 0, 1) });

        Assert.False(reused[1]);
        Assert.Equal([0f, 0f, 1f], outputs["a"].Floats);
    }

    // ------------------------------------------------------------------ The operator itself

    /// <summary>A destination of the wrong size is refused rather than truncating the result.</summary>
    [Fact]
    public void ADestinationOfTheWrongSizeIsIgnored()
    {
        var source = Floats([4], 1, 2, 3, 4);
        var tooSmall = Floats([2], 0, 0);

        var result = Elementwise.Relu(source, into: tooSmall);

        Assert.Equal(4, result.Count);
        Assert.NotSame(tooSmall.Buffer, result.Buffer);
        Assert.Equal([0f, 0f], tooSmall.Floats);
    }

    /// <summary>
    /// A broadcast operand is never the destination, whatever the session offers.
    /// </summary>
    /// <remarks>
    /// It is re-read across blocks, so writing to it would feed later blocks the results of
    /// earlier ones — and the count check alone would not catch it, because the broadcast
    /// operand can happen to hold as many elements as one row of the output.
    /// </remarks>
    [Fact]
    public void ABroadcastOperandIsNeverTheDestination()
    {
        var big = Floats([3, 2], 1, 2, 3, 4, 5, 6);
        var small = Floats([1, 2], 10, 20);

        var result = Elementwise.Binary(big, small, BinaryKind.Add, into: small);

        Assert.NotSame(small.Buffer, result.Buffer);
        Assert.Equal([10f, 20f], small.Floats);
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], result.Floats);
    }
}
