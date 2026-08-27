using Xberg.Internal.Layout;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Port of the Rust <c>layout::models::pp_doclayout_v3</c> test module.
/// </summary>
/// <remarks>
/// The class mapping is the substance here: PP-DocLayout-V3's 25-class taxonomy folds onto the
/// shared set many-to-one, and a class landing in the wrong bucket is silent — the region is still
/// detected, just described wrongly, which no golden file over this port would catch.
/// </remarks>
public class PpDocLayoutV3Tests
{
    [Fact]
    public void ClassFromIdChartMapsToChart() =>
        Assert.Equal(LayoutClass.Chart, PpDocLayoutV3Model.ClassFromId(3));

    [Fact]
    public void ClassFromIdDisplayFormulaMapsToFormula() =>
        Assert.Equal(LayoutClass.Formula, PpDocLayoutV3Model.ClassFromId(5));

    [Fact]
    public void ClassFromIdFormulaNumberMapsToFormula() =>
        Assert.Equal(LayoutClass.Formula, PpDocLayoutV3Model.ClassFromId(11));

    [Fact]
    public void ClassFromIdInlineFormulaMapsToFormula() =>
        Assert.Equal(LayoutClass.Formula, PpDocLayoutV3Model.ClassFromId(15));

    [Fact]
    public void ClassFromIdDocTitleMapsToTitle() =>
        Assert.Equal(LayoutClass.Title, PpDocLayoutV3Model.ClassFromId(6));

    /// <summary>
    /// <c>figure_title</c> (7) is a figure or table caption, not a document title, and must not
    /// share a bucket with <c>doc_title</c> (6) or <c>paragraph_title</c> (17).
    /// </summary>
    [Fact]
    public void ClassFromIdFigureTitleMapsToCaption() =>
        Assert.Equal(LayoutClass.Caption, PpDocLayoutV3Model.ClassFromId(7));

    [Fact]
    public void ClassFromIdParagraphTitleMapsToTitle() =>
        Assert.Equal(LayoutClass.Title, PpDocLayoutV3Model.ClassFromId(17));

    /// <summary>
    /// <c>footnote</c> (10) and <c>vision_footnote</c> (24) map to their own class rather than
    /// being collapsed into text, because the hint mapping consumes it.
    /// </summary>
    [Fact]
    public void ClassFromIdFootnoteClassesMapToFootnote()
    {
        foreach (long id in new long[] { 10, 24 })
            Assert.Equal(LayoutClass.Footnote, PpDocLayoutV3Model.ClassFromId(id));
    }

    /// <summary>
    /// <c>content</c> (4) is the table-of-contents region, matching RT-DETR's dedicated class.
    /// </summary>
    [Fact]
    public void ClassFromIdContentMapsToDocumentIndex() =>
        Assert.Equal(LayoutClass.DocumentIndex, PpDocLayoutV3Model.ClassFromId(4));

    /// <summary><c>algorithm</c> (1) is a code-like listing, matching RT-DETR's code class.</summary>
    [Fact]
    public void ClassFromIdAlgorithmMapsToCode() =>
        Assert.Equal(LayoutClass.Code, PpDocLayoutV3Model.ClassFromId(1));

    [Fact]
    public void ClassFromIdFooterMapsToPageFooter() =>
        Assert.Equal(LayoutClass.PageFooter, PpDocLayoutV3Model.ClassFromId(8));

    [Fact]
    public void ClassFromIdFooterImageMapsToPageFooter() =>
        Assert.Equal(LayoutClass.PageFooter, PpDocLayoutV3Model.ClassFromId(9));

    [Fact]
    public void ClassFromIdHeaderMapsToPageHeader() =>
        Assert.Equal(LayoutClass.PageHeader, PpDocLayoutV3Model.ClassFromId(12));

    [Fact]
    public void ClassFromIdHeaderImageMapsToPageHeader() =>
        Assert.Equal(LayoutClass.PageHeader, PpDocLayoutV3Model.ClassFromId(13));

    [Fact]
    public void ClassFromIdImageMapsToPicture() =>
        Assert.Equal(LayoutClass.Picture, PpDocLayoutV3Model.ClassFromId(14));

    [Fact]
    public void ClassFromIdSealMapsToPicture() =>
        Assert.Equal(LayoutClass.Picture, PpDocLayoutV3Model.ClassFromId(20));

    [Fact]
    public void ClassFromIdTableMapsToTable() =>
        Assert.Equal(LayoutClass.Table, PpDocLayoutV3Model.ClassFromId(21));

    [Fact]
    public void ClassFromIdTextClassesMapToText()
    {
        foreach (long id in new long[] { 0, 2, 16, 18, 19, 22, 23 })
            Assert.Equal(LayoutClass.Text, PpDocLayoutV3Model.ClassFromId(id));
    }

    [Fact]
    public void ClassFromIdOutOfRangeReturnsNone()
    {
        Assert.Null(PpDocLayoutV3Model.ClassFromId(25));
        Assert.Null(PpDocLayoutV3Model.ClassFromId(-1));
        Assert.Null(PpDocLayoutV3Model.ClassFromId(100));
    }

    [Fact]
    public void DefaultThresholdIsHalf() => Assert.Equal(0.5f, PpDocLayoutV3Model.DefaultThreshold);

    [Fact]
    public void InputSizeIs800() => Assert.Equal(800, PpDocLayoutV3Model.InputSize);

    private static List<LayoutDetection> ParseOneRow(float[] row, float threshold, int width, int height) =>
        PpDocLayoutV3Model.ParseDetections(row, 0, row.Length, 1, threshold, width, height);

    [Fact]
    public void ParseDetectionsFiltersLowConfidence() =>
        Assert.Empty(ParseOneRow(new[] { 3.0f, 0.3f, 10.0f, 20.0f, 100.0f, 200.0f, 0.0f }, 0.5f, 640, 480));

    [Fact]
    public void ParseDetectionsAcceptsAboveThreshold()
    {
        var detections = ParseOneRow(new[] { 21.0f, 0.8f, 10.0f, 20.0f, 100.0f, 200.0f, 0.0f }, 0.5f, 640, 480);
        Assert.Single(detections);
        Assert.Equal(LayoutClass.Table, detections[0].ClassName);
        Assert.True(MathF.Abs(detections[0].Confidence - 0.8f) < 1e-5f);
    }

    [Fact]
    public void ParseDetectionsClampsCoordinatesToImageBounds()
    {
        var detections = ParseOneRow(new[] { 22.0f, 0.9f, -5.0f, -10.0f, 700.0f, 500.0f, 0.0f }, 0.5f, 640, 480);
        Assert.Single(detections);
        Assert.Equal(0.0f, detections[0].Box.X1);
        Assert.Equal(0.0f, detections[0].Box.Y1);
        Assert.Equal(640.0f, detections[0].Box.X2);
        Assert.Equal(480.0f, detections[0].Box.Y2);
    }

    [Fact]
    public void ParseDetectionsSkipsUnknownClassId() =>
        Assert.Empty(ParseOneRow(new[] { 25.0f, 0.9f, 10.0f, 20.0f, 100.0f, 200.0f, 0.0f }, 0.5f, 640, 480));

    /// <summary>
    /// An empty batch must not reach the graph at all — a zero-length batch dimension is not a
    /// shape the export accepts.
    /// </summary>
    [Fact]
    public void EmptyBatchShortCircuitsToEmptyResult()
    {
        var shortCircuit = PpDocLayoutV3Model.EmptyBatchShortCircuit(0);
        Assert.NotNull(shortCircuit);
        Assert.Empty(shortCircuit);
    }

    [Fact]
    public void NonEmptyBatchDoesNotShortCircuit() =>
        Assert.Null(PpDocLayoutV3Model.EmptyBatchShortCircuit(1));

    /// <summary>
    /// <c>im_shape</c> is always the resized tensor size, never the original page size.
    /// </summary>
    /// <remarks>
    /// PaddleDetection divides output coordinates by <c>scale_factor</c> to restore original pixel
    /// space, so passing the original size here makes every box overflow the page.
    /// </remarks>
    [Fact]
    public void PreprocessSingleImShapeIsResizedDimensionsNotOriginal()
    {
        var (imShape, _) = PpDocLayoutV3Model.ScalarInputs(1275, 1650);
        Assert.Equal(PpDocLayoutV3Model.InputSize, imShape.Height);
        Assert.Equal(PpDocLayoutV3Model.InputSize, imShape.Width);
    }

    [Fact]
    public void PreprocessSingleScaleFactorIsResizedOverOriginal()
    {
        var (_, scale) = PpDocLayoutV3Model.ScalarInputs(1275, 1650);
        Assert.True(MathF.Abs(scale.Height - PpDocLayoutV3Model.InputSize / 1650.0f) < 1e-5f);
        Assert.True(MathF.Abs(scale.Width - PpDocLayoutV3Model.InputSize / 1275.0f) < 1e-5f);
    }
}
