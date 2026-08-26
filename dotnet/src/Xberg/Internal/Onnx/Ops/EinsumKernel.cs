namespace Xberg.Internal.Onnx.Ops;

/// <summary>
/// ONNX <c>Einsum</c> for one or two operands.
/// </summary>
/// <remarks>
/// <para>
/// A direct evaluation of the equation's own definition: every label in the equation names an
/// axis, the labels in the output term index the result, and the labels that appear only on the
/// input side are summed over. That is slower than routing each equation to a specialised kernel,
/// but exported graphs use einsum for one or two contractions on small tensors — this model has a
/// single such node — where clarity is worth more than the constant factor.
/// </para>
/// <para>
/// Ellipsis (<c>...</c>) is not supported: no equation in the models this port runs uses one, and
/// guessing at broadcast semantics would be worse than saying so.
/// </para>
/// </remarks>
internal static class EinsumKernel
{
    public static Tensor Apply(string equation, IReadOnlyList<Tensor> inputs)
    {
        if (inputs.Count is not (1 or 2))
            throw new NotSupportedException($"onnx: Einsum with {inputs.Count} operands is not implemented");
        if (equation.Contains("..."))
            throw new NotSupportedException("onnx: Einsum with an ellipsis is not implemented");

        equation = equation.Replace(" ", "");
        string inputPart, outputPart;
        int arrow = equation.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            inputPart = equation[..arrow];
            outputPart = equation[(arrow + 2)..];
        }
        else
        {
            inputPart = equation;
            outputPart = ImplicitOutput(inputPart);
        }

        var terms = inputPart.Split(',');
        if (terms.Length != inputs.Count)
            throw new InvalidDataException(
                $"Einsum equation '{equation}' names {terms.Length} operands but {inputs.Count} were given");

        // Every distinct label gets an extent, taken from whichever operand carries it. A label
        // appearing twice with different extents is a malformed equation, not a broadcast.
        var extent = new Dictionary<char, int>();
        for (int t = 0; t < terms.Length; t++)
        {
            var shape = inputs[t].Shape;
            if (terms[t].Length != shape.Length)
                throw new InvalidDataException(
                    $"Einsum term '{terms[t]}' has {terms[t].Length} labels but operand {t} has rank {shape.Length}");
            for (int i = 0; i < terms[t].Length; i++)
            {
                char label = terms[t][i];
                if (extent.TryGetValue(label, out int known))
                {
                    if (known != shape[i])
                        throw new InvalidDataException(
                            $"Einsum label '{label}' has extent {known} and {shape[i]}");
                }
                else extent[label] = shape[i];
            }
        }

        var outputLabels = outputPart.ToCharArray();
        var summedLabels = extent.Keys.Where(label => !outputPart.Contains(label)).OrderBy(label => label).ToArray();

        var outputShape = outputLabels.Length == 0 ? new[] { 1 } : outputLabels.Select(l => extent[l]).ToArray();
        var result = Tensor.AllocateFloat(outputShape);

        var strides = new int[terms.Length][];
        for (int t = 0; t < terms.Length; t++) strides[t] = RowMajorStrides(inputs[t].Shape);

        var position = new Dictionary<char, int>();
        int outputTotal = Tensor.ElementCount(outputShape);
        int summedTotal = summedLabels.Aggregate(1, (acc, label) => acc * extent[label]);

        var outputIndex = new int[outputLabels.Length];
        for (int flat = 0; flat < outputTotal; flat++)
        {
            for (int i = 0; i < outputLabels.Length; i++) position[outputLabels[i]] = outputIndex[i];

            float sum = 0f;
            var summedIndex = new int[summedLabels.Length];
            for (int s = 0; s < summedTotal; s++)
            {
                for (int i = 0; i < summedLabels.Length; i++) position[summedLabels[i]] = summedIndex[i];

                float product = 1f;
                for (int t = 0; t < terms.Length; t++)
                {
                    int offset = 0;
                    for (int i = 0; i < terms[t].Length; i++) offset += position[terms[t][i]] * strides[t][i];
                    product *= inputs[t].GetFloat(offset);
                }
                sum += product;

                for (int d = summedLabels.Length - 1; d >= 0; d--)
                {
                    if (++summedIndex[d] < extent[summedLabels[d]]) break;
                    summedIndex[d] = 0;
                }
            }

            result.Floats[flat] = sum;

            for (int d = outputLabels.Length - 1; d >= 0; d--)
            {
                if (++outputIndex[d] < extent[outputLabels[d]]) break;
                outputIndex[d] = 0;
            }
        }

        return result;
    }

    /// <summary>
    /// The output term an equation without <c>-&gt;</c> implies: every label appearing exactly
    /// once across the inputs, in alphabetical order.
    /// </summary>
    private static string ImplicitOutput(string inputPart)
    {
        var counts = new Dictionary<char, int>();
        foreach (char label in inputPart)
        {
            if (label == ',') continue;
            counts[label] = counts.TryGetValue(label, out int n) ? n + 1 : 1;
        }
        return new string(counts.Where(pair => pair.Value == 1).Select(pair => pair.Key).OrderBy(l => l).ToArray());
    }

    private static int[] RowMajorStrides(ReadOnlySpan<int> shape)
    {
        var strides = new int[shape.Length];
        int acc = 1;
        for (int i = shape.Length - 1; i >= 0; i--) { strides[i] = acc; acc *= shape[i]; }
        return strides;
    }
}
