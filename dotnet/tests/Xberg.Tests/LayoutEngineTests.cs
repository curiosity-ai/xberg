using Xberg.Internal.Layout;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the layout engine facade.
/// </summary>
/// <remarks>
/// The models it dispatches to are checked against the real implementations by their own
/// probes; what is left here is the dispatch itself and the configuration that decides it.
/// </remarks>
public class LayoutEngineTests
{
    /// <summary>
    /// Two configurations differing anywhere are different engines.
    /// </summary>
    /// <remarks>
    /// A cached engine holds a loaded model, and whether it can be reused is exactly whether
    /// its configuration matches. A comparison blind to one field hands back an engine running
    /// the wrong model, or at the wrong threshold, with nothing to say so.
    /// </remarks>
    [Fact]
    public void EveryConfigurationFieldSeparatesTwoEngines()
    {
        var baseline = new LayoutEngineConfig();

        Assert.Equal(baseline, new LayoutEngineConfig());
        Assert.NotEqual(baseline, baseline with { Backend = LayoutBackend.PpDocLayoutV3 });
        Assert.NotEqual(baseline, baseline with { ModelPath = "model.onnx" });
        Assert.NotEqual(baseline, baseline with { Variant = CustomModelVariant.Yolox });
        Assert.NotEqual(baseline, baseline with { YoloxInputWidth = 640 });
        Assert.NotEqual(baseline, baseline with { YoloxInputHeight = 640 });
        Assert.NotEqual(baseline, baseline with { ConfidenceThreshold = 0.75f });
        Assert.NotEqual(baseline, baseline with { ApplyHeuristics = false });
        Assert.NotEqual(baseline, baseline with { CacheDirectory = "elsewhere" });
    }

    /// <summary>The defaults are the detector that is pinned and downloadable.</summary>
    [Fact]
    public void TheDefaultBackendIsTheOneWithAPinnedModel()
    {
        var config = new LayoutEngineConfig();
        Assert.Equal(LayoutBackend.RtDetr, config.Backend);
        Assert.True(config.ApplyHeuristics);
        Assert.Null(config.ConfidenceThreshold);
    }

    /// <summary>
    /// A custom backend with no file to load is refused rather than reaching for a download.
    /// </summary>
    /// <remarks>
    /// No YOLO model is pinned anywhere, so a caller who names one has to supply it. Falling
    /// back to a download would fail later and further away.
    /// </remarks>
    [Fact]
    public async Task ACustomBackendNeedsItsPath()
    {
        var config = new LayoutEngineConfig
        {
            Backend = LayoutBackend.Custom,
            Variant = CustomModelVariant.YoloDocLayNet,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LayoutEngine.CreateAsync(config));
        Assert.Contains("path", error.Message);
    }
}
