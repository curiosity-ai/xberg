using Xberg.Internal.Layout;
using Xberg.Internal.Onnx;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the YOLO-family layout wrapper's reading of a model's output.
/// </summary>
/// <remarks>
/// The end-to-end check that this agrees with the Rust <c>YoloModel</c> runs through
/// <c>tools/yolo-probe</c>: no YOLO model is pinned in the model manager, so both sides run
/// synthetic ONNX files whose single output is a fixed tensor, and what is compared is the
/// decode rather than the arithmetic of a convolution. Across the three variants that is 5,636
/// detections agreeing on class, confidence and every coordinate. These pin the format rules
/// that such a comparison would only fail vaguely on.
/// </remarks>
public class YoloModelTests
{
    // ------------------------------------------------------------------ The YOLOX grid

    /// <summary>
    /// The grid holds one anchor per cell of every stride level.
    /// </summary>
    /// <remarks>
    /// A count that disagrees with the model's output means the two are describing different
    /// things, which is why the reader refuses rather than decoding the rows it happens to have.
    /// </remarks>
    [Fact]
    public void TheGridHasOneAnchorPerCellPerStride()
    {
        var grid = YoloModel.BuildYoloxGrid(768, 1024);
        int expected = 96 * 128 + 48 * 64 + 24 * 32;     // strides 8, 16 and 32 over 768x1024
        Assert.Equal(expected, grid.Count);
    }

    /// <summary>
    /// The grid is walked finest stride first, and row-major within each level.
    /// </summary>
    /// <remarks>
    /// The order is what ties row <c>i</c> of the output to a place on the page. Walk it the
    /// other way and every box lands somewhere else, with nothing in the numbers to say so.
    /// </remarks>
    [Fact]
    public void TheGridIsWalkedFinestStrideFirstAndRowMajor()
    {
        var grid = YoloModel.BuildYoloxGrid(768, 1024);

        Assert.Equal((0f, 0f, 8f), grid[0]);
        Assert.Equal((1f, 0f, 8f), grid[1]);
        Assert.Equal((0f, 1f, 8f), grid[96]);            // 768 / 8 columns per row

        int finest = 96 * 128;
        Assert.Equal((0f, 0f, 16f), grid[finest]);

        int throughSixteen = finest + 48 * 64;
        Assert.Equal((0f, 0f, 32f), grid[throughSixteen]);
        Assert.Equal(32f, grid[^1].Stride);
    }

    /// <summary>A square input is not assumed: the two dimensions divide independently.</summary>
    [Fact]
    public void TheGridFollowsBothInputDimensions()
    {
        var grid = YoloModel.BuildYoloxGrid(640, 320);
        Assert.Equal(80 * 40 + 40 * 20 + 20 * 10, grid.Count);
        Assert.Equal((79f, 0f, 8f), grid[79]);
        Assert.Equal((0f, 1f, 8f), grid[80]);
    }

    // ------------------------------------------------------------------ Output shape

    /// <summary>
    /// A rank-2 output is a rank-3 one with the batch dimension dropped.
    /// </summary>
    /// <remarks>
    /// Exports disagree on whether tracing keeps the leading 1, and reading a rank-2 output as
    /// though it were rank 3 takes the detection count for a column count.
    /// </remarks>
    [Fact]
    public void BothOutputRanksDescribeTheSameThing()
    {
        Assert.Equal((40, 6), YoloModel.OutputLayout(Tensor.AllocateFloat(1, 40, 6)));
        Assert.Equal((40, 6), YoloModel.OutputLayout(Tensor.AllocateFloat(40, 6)));
    }

    [Fact]
    public void AnOutputThatIsNotAGridIsRefused()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => YoloModel.OutputLayout(Tensor.AllocateFloat(40)));
        Assert.Contains("rank", error.Message);
    }

    // ------------------------------------------------------------------ Class scores

    [Fact]
    public void TheHighestScoringClassWins()
    {
        var scores = Tensor.AllocateFloat(1, 5);
        scores.Floats[0] = 0.1f;
        scores.Floats[1] = 0.7f;
        scores.Floats[2] = 0.3f;
        scores.Floats[3] = 0.9f;
        scores.Floats[4] = 0.2f;

        var (score, classId) = YoloModel.BestClass(scores, 0, 5);
        Assert.Equal(0.9f, score);
        Assert.Equal(3, classId);
    }

    /// <summary>
    /// A tie goes to the earlier class, and a row of zeroes scores zero.
    /// </summary>
    /// <remarks>
    /// The strict comparison that keeps the first of a tie is also what leaves an all-zero row
    /// at class 0 with score 0, which the threshold then discards — reading it as a confident
    /// detection of class 0 would fill the page with boxes.
    /// </remarks>
    [Fact]
    public void ATieKeepsTheEarlierClass()
    {
        var scores = Tensor.AllocateFloat(1, 3);
        scores.Floats[0] = 0.5f;
        scores.Floats[1] = 0.5f;
        scores.Floats[2] = 0.4f;

        Assert.Equal((0.5f, 0L), YoloModel.BestClass(scores, 0, 3));
        Assert.Equal((0f, 0L), YoloModel.BestClass(Tensor.AllocateFloat(1, 3), 0, 3));
    }

    /// <summary>Scores are read from where the geometry ends, not from the start of the row.</summary>
    [Fact]
    public void ScoresAreReadFromTheGivenOffset()
    {
        var row = Tensor.AllocateFloat(1, 8);
        row.Floats[0] = 9f;                              // geometry, which must not be scored
        row.Floats[5] = 0.2f;
        row.Floats[6] = 0.8f;
        row.Floats[7] = 0.1f;

        Assert.Equal((0.8f, 1L), YoloModel.BestClass(row, 5, 3));
    }
}
