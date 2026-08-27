using Xberg.Internal.Layout;
using Xberg.Internal.Onnx;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the SLANeXt decode.
/// </summary>
/// <remarks>
/// Upstream carries no tests for this model. The graph execution is verified against ONNX Runtime
/// per node; what these cover is the token walk that turns the model's HTML structure tokens into
/// a grid, which is the part that is ported rather than executed.
/// </remarks>
public class SlanetTests
{
    /// <summary>Build a logits tensor whose argmax at each step is the named token.</summary>
    private static Tensor LogitsFor(params string[] tokens)
    {
        int vocab = SlanetModel.VocabSize;
        var data = new float[tokens.Length * vocab];
        for (int t = 0; t < tokens.Length; t++)
        {
            int index = Array.IndexOf(SlanetModel.TokenDict, tokens[t]);
            Assert.True(index >= 0, $"unknown token '{tokens[t]}'");
            data[t * vocab + index] = 10.0f;
        }
        return Tensor.FromFloats(data, [1, tokens.Length, vocab]);
    }

    /// <summary>Cell polygons, one per sequence position, in normalised coordinates.</summary>
    private static Tensor BoxesFor(int sequence, params (int Position, float[] Polygon)[] entries)
    {
        var data = new float[sequence * 8];
        foreach (var (position, polygon) in entries)
            Array.Copy(polygon, 0, data, position * 8, 8);
        return Tensor.FromFloats(data, [1, sequence, 8]);
    }

    private static float[] Square(float x1, float y1, float x2, float y2) =>
        [x1, y1, x2, y1, x2, y2, x1, y2];

    [Fact]
    public void DecodeWalksASimpleTwoByTwoTable()
    {
        var tokens = new[] { "<tbody>", "<tr>", "<td>", "<td>", "</tr>", "<tr>", "<td>", "<td>", "</tr>", "</tbody>" };
        var result = SlanetModel.Decode(LogitsFor(tokens), BoxesFor(tokens.Length), 100.0f, 100.0f);

        Assert.Equal(2, result.NumRows);
        Assert.Equal(2, result.NumCols);
        Assert.Equal(4, result.Cells.Count);
        Assert.Equal((0, 0), (result.Cells[0].Row, result.Cells[0].Col));
        Assert.Equal((0, 1), (result.Cells[1].Row, result.Cells[1].Col));
        Assert.Equal((1, 0), (result.Cells[2].Row, result.Cells[2].Col));
        Assert.Equal((1, 1), (result.Cells[3].Row, result.Cells[3].Col));
    }

    /// <summary>
    /// An open <c>&lt;td</c> takes attributes, and the column only advances at the matching
    /// <c>&gt;</c> — not at the tag itself, as a closed <c>&lt;td&gt;</c> does.
    /// </summary>
    [Fact]
    public void DecodeAdvancesTheColumnAtTheClosingAngleOfAnOpenCell()
    {
        var tokens = new[] { "<tr>", "<td", " colspan=\"2\"", ">", "<td>", "</tr>" };
        var result = SlanetModel.Decode(LogitsFor(tokens), BoxesFor(tokens.Length), 100.0f, 100.0f);

        Assert.Equal(1, result.NumRows);
        Assert.Equal(2, result.NumCols);
        Assert.Equal(2, result.Cells.Count);
        Assert.Equal(0, result.Cells[0].Col);
        Assert.Equal(1, result.Cells[1].Col);
    }

    /// <summary>The widest row sets the column count, not the last one.</summary>
    [Fact]
    public void DecodeTakesTheWidestRowAsTheColumnCount()
    {
        var tokens = new[] { "<tr>", "<td>", "<td>", "<td>", "</tr>", "<tr>", "<td>", "</tr>" };
        var result = SlanetModel.Decode(LogitsFor(tokens), BoxesFor(tokens.Length), 100.0f, 100.0f);

        Assert.Equal(2, result.NumRows);
        Assert.Equal(3, result.NumCols);
    }

    /// <summary>An end-of-sequence token stops the walk; nothing after it is read.</summary>
    [Fact]
    public void DecodeStopsAtEndOfSequence()
    {
        var tokens = new[] { "<tr>", "<td>", "eos", "<td>", "<td>" };
        var result = SlanetModel.Decode(LogitsFor(tokens), BoxesFor(tokens.Length), 100.0f, 100.0f);

        Assert.Single(result.Cells);
        Assert.Equal(2, result.StructureTokens.Count);
    }

    /// <summary>
    /// The start token is skipped rather than treated as end-of-sequence, which is why the
    /// end check only applies past the first position.
    /// </summary>
    [Fact]
    public void DecodeSkipsTheStartToken()
    {
        var tokens = new[] { "sos", "<tr>", "<td>", "</tr>" };
        var result = SlanetModel.Decode(LogitsFor(tokens), BoxesFor(tokens.Length), 100.0f, 100.0f);

        Assert.Equal(new[] { "<tr>", "<td>", "</tr>" }, result.StructureTokens);
        Assert.Single(result.Cells);
    }

    /// <summary>
    /// Cell polygons scale by the original image size and clamp to it, so a prediction that
    /// overshoots the image cannot produce a box outside it.
    /// </summary>
    [Fact]
    public void DecodeScalesAndClampsCellPolygons()
    {
        var tokens = new[] { "<tr>", "<td>", "</tr>" };
        var boxes = BoxesFor(tokens.Length, (1, Square(-0.1f, 0.25f, 1.5f, 0.75f)));
        var result = SlanetModel.Decode(LogitsFor(tokens), boxes, 200.0f, 400.0f);

        var cell = Assert.Single(result.Cells);
        Assert.Equal(0.0f, cell.Box[0]);      // clamped from -20
        Assert.Equal(100.0f, cell.Box[1]);    // 0.25 * 400
        Assert.Equal(200.0f, cell.Box[2]);    // clamped from 300
        Assert.Equal(300.0f, cell.Box[3]);    // 0.75 * 400
    }

    [Fact]
    public void DecodeOfAnEmptySequenceIsEmpty()
    {
        var result = SlanetModel.Decode(
            Tensor.FromFloats([], [1, 0, SlanetModel.VocabSize]),
            Tensor.FromFloats([], [1, 0, 8]), 100.0f, 100.0f);

        Assert.Equal(0, result.NumRows);
        Assert.Equal(0, result.NumCols);
        Assert.Empty(result.Cells);
        Assert.Equal(0.0f, result.Confidence);
    }

    /// <summary>
    /// The vocabulary is 50 tokens: a start token, 48 HTML structure tokens, and an end token.
    /// </summary>
    [Fact]
    public void TokenDictionaryHasTheExpectedShape()
    {
        Assert.Equal(SlanetModel.VocabSize, SlanetModel.TokenDict.Length);
        Assert.Equal("sos", SlanetModel.TokenDict[0]);
        Assert.Equal("eos", SlanetModel.TokenDict[^1]);
        Assert.Equal(" colspan=\"2\"", SlanetModel.TokenDict[11]);
        Assert.Equal(" rowspan=\"20\"", SlanetModel.TokenDict[48]);
    }

    /// <summary>
    /// The reported confidence is the softmax probability of the chosen token, not the raw logit.
    /// </summary>
    [Fact]
    public void ArgmaxWithScoreReturnsTheSoftmaxOfTheMaximum()
    {
        // Two equal logits give each a probability of one half.
        var (index, probability) = SlanetModel.ArgmaxWithScore([1.0f, 1.0f], 0, 2);
        Assert.Equal(0, index);
        Assert.Equal(0.5f, probability, 5);

        var (clear, confident) = SlanetModel.ArgmaxWithScore([0.0f, 10.0f, 0.0f], 0, 3);
        Assert.Equal(1, clear);
        Assert.True(confident > 0.99f);
    }
}
