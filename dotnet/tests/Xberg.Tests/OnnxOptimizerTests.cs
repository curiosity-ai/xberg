using Xberg.Internal.Onnx;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the two transformations that make the runtime fast without changing what it
/// computes: graph rewriting and buffer reuse. Both are the kind of optimisation whose
/// failure mode is silent — a fused graph that quietly computes something else, or a recycled
/// buffer handed to two live tensors — so each is checked against the behaviour it replaces
/// rather than only for the absence of a crash.
/// </summary>
public class OnnxOptimizerTests
{
    // ---- graph rewriting ---------------------------------------------------------------

    [Fact]
    public void Optimizer_FoldsPerChannelScaleAndShiftIntoTheConvolution()
    {
        // Conv -> Mul(per-channel) -> Add(per-channel) collapses to a single Conv.
        var model = OnnxModel.Parse(ConvChainModel.Build(
            scale: [2f, 3f], shift: [1f, -1f], activation: null));
        var optimized = GraphOptimizer.Optimize(model);

        Assert.Equal(3, model.Nodes.Length);
        Assert.Single(optimized.Nodes);
        Assert.Equal("Conv", optimized.Nodes[0].OpType);
        // The Conv now owns the graph output the Add used to produce.
        Assert.Equal("y", optimized.Nodes[0].Outputs[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Relu")]
    [InlineData("Sigmoid")]
    public void Optimizer_PreservesResultsExactlyForEveryFoldedShape(string? activation)
    {
        var bytes = ConvChainModel.Build(scale: [2f, 3f], shift: [1f, -1f], activation);
        var model = OnnxModel.Parse(bytes);

        var input = Tensor.FromFloats([1, 2, 3, 4, -5, 6, -7, 8], 1, 2, 2, 2);
        var feeds = new Dictionary<string, Tensor> { ["x"] = input };

        var plain = new OnnxSession(model, optimize: false).Run(feeds)["y"];
        var fused = new OnnxSession(model, optimize: true).Run(feeds)["y"];

        Assert.Equal(plain.Shape, fused.Shape);
        for (int i = 0; i < plain.Count; i++)
            Assert.Equal(plain.Floats[i], fused.Floats[i], 4);
    }

    [Fact]
    public void Optimizer_FusesTheSigmoidAndMulPairThatSpellsSiLU()
    {
        var model = OnnxModel.Parse(SiLUModel.Build());
        var optimized = GraphOptimizer.Optimize(model);

        Assert.Equal(3, model.Nodes.Length);   // Conv, Sigmoid, Mul
        Assert.Single(optimized.Nodes);
        Assert.Equal(FusedActivation.SiLU, optimized.Nodes[0].Activation);

        var feeds = new Dictionary<string, Tensor>
        {
            ["x"] = Tensor.FromFloats([1, 2, 3, 4, -5, 6, -7, 8], 1, 2, 2, 2),
        };
        var plain = new OnnxSession(model, optimize: false).Run(feeds)["y"];
        var fused = new OnnxSession(model, optimize: true).Run(feeds)["y"];
        for (int i = 0; i < plain.Count; i++) Assert.Equal(plain.Floats[i], fused.Floats[i], 4);
    }

    [Fact]
    public void Optimizer_RefusesToFoldAConstantThatBroadcastsOnTheWrongAxis()
    {
        // A bare [C] constant has the same element count as [1,C,1,1] but broadcasts along
        // width, not channels. Folding it would silently compute something else.
        var model = OnnxModel.Parse(ConvChainModel.Build(
            scale: [2f, 3f], shift: [1f, -1f], activation: null, channelShapedConstants: false));
        var optimized = GraphOptimizer.Optimize(model);

        Assert.Equal(model.Nodes.Length, optimized.Nodes.Length);
    }

    [Fact]
    public void Optimizer_LeavesAValueAloneWhenSomethingElseStillReadsIt()
    {
        // The Conv output feeds both the norm chain and a second consumer, so folding the
        // chain away would destroy a value that is still needed.
        var model = OnnxModel.Parse(ConvChainModel.Build(
            scale: [2f, 3f], shift: [1f, -1f], activation: null, extraConsumer: true));
        var optimized = GraphOptimizer.Optimize(model);

        Assert.Contains(optimized.Nodes, n => n.OpType == "Mul");
    }

    [Fact]
    public void Optimizer_DoesNotDisturbTheOriginalModel()
    {
        var model = OnnxModel.Parse(ConvChainModel.Build([2f, 3f], [1f, -1f], activation: "Relu"));
        int before = model.Nodes.Length;
        string firstOutput = model.Nodes[0].Outputs[0];

        GraphOptimizer.Optimize(model);

        Assert.Equal(before, model.Nodes.Length);
        Assert.Equal(firstOutput, model.Nodes[0].Outputs[0]);
        Assert.Equal(FusedActivation.None, model.Nodes[0].Activation);
    }

    // ---- buffer reuse ------------------------------------------------------------------

    [Fact]
    public void Pool_ReusesBuffersOfTheSameLengthOnceReleased()
    {
        var pool = new TensorPool();
        using (pool.Activate())
        {
            var first = Tensor.AllocateFloat(8192);
            first.Buffer!.AddReference();
            first.Buffer.Release();

            var second = Tensor.AllocateFloat(8192);
            Assert.Same(first.Floats, second.Floats);
        }
        Assert.Equal(1, pool.Reused);
    }

    [Fact]
    public void Pool_NeverHandsOutABufferThatIsStillReferenced()
    {
        var pool = new TensorPool();
        using (pool.Activate())
        {
            var tensor = Tensor.AllocateFloat(8192);
            // Two holders — a reshape view alongside the original, as Reshape produces.
            tensor.Buffer!.AddReference();
            var view = tensor.Reshaped(64, 128);
            view.Buffer!.AddReference();

            tensor.Buffer.Release();   // the original name dies

            var other = Tensor.AllocateFloat(8192);
            Assert.NotSame(view.Floats, other.Floats);
        }
    }

    [Fact]
    public void Pool_ReturnsExactlySizedBuffersOnly()
    {
        // Kernels hand whole arrays to vectorised primitives, so an oversized buffer would
        // silently extend the operation past the tensor.
        var pool = new TensorPool();
        using (pool.Activate())
        {
            var big = Tensor.AllocateFloat(16384);
            big.Buffer!.AddReference();
            big.Buffer.Release();

            var small = Tensor.AllocateFloat(8192);
            Assert.Equal(8192, small.Floats.Length);
            Assert.NotSame(big.Floats, small.Floats);
        }
    }

    [Fact]
    public void Pool_SurvivesADoubleRelease()
    {
        var pool = new TensorPool();
        using (pool.Activate())
        {
            var tensor = Tensor.AllocateFloat(8192);
            tensor.Buffer!.AddReference();
            tensor.Buffer.Release();
            tensor.Buffer.Release();   // a second release must not queue the array twice

            var a = Tensor.AllocateFloat(8192);
            var b = Tensor.AllocateFloat(8192);
            Assert.NotSame(a.Floats, b.Floats);
        }
    }

    [Fact]
    public void Session_ProducesTheSameResultsAcrossRepeatedRuns()
    {
        // The pool is reused between runs, so a lifetime bug shows up as the second run
        // disagreeing with the first.
        var session = new OnnxSession(OnnxModel.Parse(TinyOnnxModel.Build()));
        var feeds = new Dictionary<string, Tensor> { ["x"] = Tensor.FromFloats([1, -2, 3, -4], 4) };

        var first = (float[])session.Run(feeds)["y"].Floats.Clone();
        for (int i = 0; i < 5; i++)
        {
            var again = session.Run(feeds)["y"];
            Assert.Equal(first, again.Floats);
        }
    }
}
