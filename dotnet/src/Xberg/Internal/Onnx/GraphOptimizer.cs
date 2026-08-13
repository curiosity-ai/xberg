namespace Xberg.Internal.Onnx;

/// <summary>
/// Rewrites a parsed graph into an equivalent, cheaper one before execution.
/// <para>
/// The rewrites here were chosen from measurement, not from a list of textbook passes.
/// Profiling RT-DETR per node showed <c>Add</c> and <c>Mul</c> together taking 36% of
/// runtime, and the node names explained why: the export decomposes every batch
/// normalisation into a per-channel <c>Mul</c> followed by a per-channel <c>Add</c>, each a
/// full streaming pass over an activation that can be tens of megabytes. Both are affine and
/// constant, so they fold into the convolution's own weights and bias and disappear entirely.
/// Activations then fold into the same output pass, removing another two passes per block.
/// </para>
/// <para>
/// One pass deliberately <em>not</em> implemented: constant folding of the shape arithmetic.
/// It is the obvious thing to reach for — 1683 of the 2676 nodes are scalar bookkeeping — but
/// measurement put all of them together at 2.3 ms out of 8081 ms. Folding them would be
/// visible in the node count and invisible in the clock.
/// </para>
/// </summary>
internal static class GraphOptimizer
{
    /// <summary>
    /// Returns an optimised copy of <paramref name="model"/>. The original is left untouched,
    /// so a caller that needs verbatim per-node behaviour — the parity harness — can keep it.
    /// </summary>
    public static OnnxModel Optimize(OnnxModel model)
    {
        var nodes = model.Nodes.Select(n => n.Clone()).ToList();
        var initializers = new Dictionary<string, Tensor>(model.Initializers, StringComparer.Ordinal);
        var protectedValues = new HashSet<string>(model.Outputs.Select(o => o.Name), StringComparer.Ordinal);

        var removed = new bool[nodes.Count];
        var consumers = BuildConsumerMap(nodes);

        for (int i = 0; i < nodes.Count; i++)
        {
            if (removed[i] || nodes[i].OpType != "Conv") continue;
            FoldAffineIntoConv(nodes, removed, consumers, initializers, protectedValues, i);
            FuseActivationIntoConv(nodes, removed, consumers, protectedValues, i);
        }

        var kept = new List<OnnxNode>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) if (!removed[i]) kept.Add(nodes[i]);

        return new OnnxModel
        {
            Nodes = kept.ToArray(),
            Initializers = initializers,
            Inputs = model.Inputs,
            Outputs = model.Outputs,
            OpsetVersion = model.OpsetVersion,
        };
    }

    private static Dictionary<string, List<int>> BuildConsumerMap(List<OnnxNode> nodes)
    {
        var consumers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
        {
            foreach (string input in nodes[i].Inputs)
            {
                if (input.Length == 0) continue;
                if (!consumers.TryGetValue(input, out var list)) consumers[input] = list = [];
                list.Add(i);
            }
        }
        return consumers;
    }

    /// <summary>Live consumers of a value, skipping nodes already folded away.</summary>
    private static List<int> LiveConsumers(Dictionary<string, List<int>> consumers, bool[] removed, string value)
    {
        if (!consumers.TryGetValue(value, out var list)) return [];
        var live = new List<int>(list.Count);
        foreach (int index in list) if (!removed[index]) live.Add(index);
        return live;
    }

    /// <summary>
    /// Absorb a chain of per-channel <c>Mul</c> and <c>Add</c> nodes into a convolution.
    /// <para>
    /// The chain composes to a single affine map <c>y -> y * scale + shift</c> per output
    /// channel, whatever order the operations appear in: a later <c>Mul</c> scales the shift
    /// accumulated so far, a later <c>Add</c> just accumulates. Applying that to the
    /// convolution is exact — scaling output channel <c>o</c>'s filter by <c>scale[o]</c> and
    /// its bias by the same, then adding <c>shift[o]</c>, produces the identical result
    /// because convolution is linear in its weights.
    /// </para>
    /// </summary>
    private static void FoldAffineIntoConv(
        List<OnnxNode> nodes,
        bool[] removed,
        Dictionary<string, List<int>> consumers,
        Dictionary<string, Tensor> initializers,
        HashSet<string> protectedValues,
        int convIndex)
    {
        var conv = nodes[convIndex];
        if (!initializers.TryGetValue(conv.Inputs[1], out var weight) || weight.Rank != 4) return;
        int filters = weight.Shape[0];

        var scale = new float[filters];
        var shift = new float[filters];
        Array.Fill(scale, 1f);

        string current = conv.Outputs[0];
        bool folded = false;

        while (true)
        {
            // A value the caller can observe, or one with more than one consumer, cannot be
            // rewritten away — something else still needs it exactly as it is.
            if (protectedValues.Contains(current)) break;
            var live = LiveConsumers(consumers, removed, current);
            if (live.Count != 1) break;

            var next = nodes[live[0]];
            if (next.OpType is not ("Mul" or "Add")) break;
            if (next.Inputs.Length != 2) break;

            // The constant may be on either side; the activation is whichever input is `current`.
            string constantName = next.Inputs[0] == current ? next.Inputs[1] : next.Inputs[0];
            if (next.Inputs[0] != current && next.Inputs[1] != current) break;
            if (!initializers.TryGetValue(constantName, out var constant)) break;
            if (!TryReadPerChannel(constant, filters, out var values)) break;

            if (next.OpType == "Mul")
            {
                for (int c = 0; c < filters; c++)
                {
                    scale[c] *= values[c];
                    shift[c] *= values[c];
                }
            }
            else
            {
                for (int c = 0; c < filters; c++) shift[c] += values[c];
            }

            removed[live[0]] = true;
            current = next.Outputs[0];
            folded = true;
        }

        if (!folded) return;

        // Rewrite weights and bias under fresh names: an initializer can be shared by more
        // than one node, and folding in place would corrupt the other user.
        int perFilter = weight.Count / filters;
        var newWeights = new float[weight.Count];
        for (int o = 0; o < filters; o++)
        {
            float s = scale[o];
            var source = weight.Floats.AsSpan(o * perFilter, perFilter);
            var destination = newWeights.AsSpan(o * perFilter, perFilter);
            for (int j = 0; j < perFilter; j++) destination[j] = source[j] * s;
        }

        var newBias = new float[filters];
        if (conv.Inputs.Length > 2 && conv.Inputs[2].Length > 0 &&
            initializers.TryGetValue(conv.Inputs[2], out var bias))
        {
            for (int o = 0; o < filters; o++) newBias[o] = bias.GetFloat(o) * scale[o] + shift[o];
        }
        else
        {
            shift.CopyTo(newBias, 0);
        }

        string weightName = conv.Inputs[1] + "__folded";
        string biasName = (conv.Inputs.Length > 2 && conv.Inputs[2].Length > 0 ? conv.Inputs[2] : conv.Name) + "__folded_bias";
        initializers[weightName] = Tensor.FromFloats(newWeights, weight.Shape);
        initializers[biasName] = Tensor.FromFloats(newBias, filters);

        conv.Inputs = [conv.Inputs[0], weightName, biasName];
        conv.Outputs = [current];
    }

    /// <summary>
    /// Fold the activation that follows a convolution into the convolution itself, so it is
    /// applied while the output is still hot rather than in a separate streaming pass.
    /// </summary>
    private static void FuseActivationIntoConv(
        List<OnnxNode> nodes,
        bool[] removed,
        Dictionary<string, List<int>> consumers,
        HashSet<string> protectedValues,
        int convIndex)
    {
        var conv = nodes[convIndex];
        if (conv.Activation != FusedActivation.None) return;

        string output = conv.Outputs[0];
        if (protectedValues.Contains(output)) return;
        var live = LiveConsumers(consumers, removed, output);

        if (live.Count == 1)
        {
            var next = nodes[live[0]];
            var activation = next.OpType switch
            {
                "Relu" => FusedActivation.Relu,
                "Sigmoid" => FusedActivation.Sigmoid,
                _ => FusedActivation.None,
            };
            if (activation == FusedActivation.None) return;
            conv.Activation = activation;
            conv.Outputs = [next.Outputs[0]];
            removed[live[0]] = true;
            return;
        }

        // SiLU: the producer feeds both a Sigmoid and the Mul that consumes that Sigmoid.
        if (live.Count != 2) return;
        int sigmoidIndex = live.FindIndex(index => nodes[index].OpType == "Sigmoid");
        int mulIndex = live.FindIndex(index => nodes[index].OpType == "Mul");
        if (sigmoidIndex < 0 || mulIndex < 0) return;

        var sigmoid = nodes[live[sigmoidIndex]];
        var mul = nodes[live[mulIndex]];
        if (protectedValues.Contains(sigmoid.Outputs[0])) return;

        // The Mul must consume exactly the producer and that Sigmoid, and nothing else may
        // read the Sigmoid — otherwise the intermediate is still needed.
        var sigmoidConsumers = LiveConsumers(consumers, removed, sigmoid.Outputs[0]);
        if (sigmoidConsumers.Count != 1 || sigmoidConsumers[0] != live[mulIndex]) return;
        if (mul.Inputs.Length != 2) return;
        if (!(mul.Inputs.Contains(output) && mul.Inputs.Contains(sigmoid.Outputs[0]))) return;

        conv.Activation = FusedActivation.SiLU;
        conv.Outputs = [mul.Outputs[0]];
        removed[live[sigmoidIndex]] = true;
        removed[live[mulIndex]] = true;
    }

    /// <summary>
    /// Read a constant as one value per output channel, or fail.
    /// <para>
    /// The shape check is the safety property of the whole pass. A <c>[1,C,1,1]</c> or
    /// <c>[C,1,1]</c> constant broadcasts along channels, which is what folding assumes. A
    /// bare <c>[C]</c> looks identical in element count but broadcasts along the <em>last</em>
    /// axis — width, not channels — so accepting it would silently compute something else
    /// entirely.
    /// </para>
    /// </summary>
    private static bool TryReadPerChannel(Tensor constant, int filters, out float[] values)
    {
        values = [];
        if (!constant.IsFloat) return false;

        if (constant.Count == 1)
        {
            values = new float[filters];
            Array.Fill(values, constant.Floats[0]);
            return true;
        }

        if (constant.Count != filters) return false;
        bool channelShaped = constant.Rank switch
        {
            4 => constant.Shape is [1, var c4, 1, 1] && c4 == filters,
            3 => constant.Shape is [var c3, 1, 1] && c3 == filters,
            _ => false,
        };
        if (!channelShaped) return false;

        values = constant.Floats;
        return true;
    }
}
