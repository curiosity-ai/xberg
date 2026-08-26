using System.Security.Cryptography;
using Xberg.Core;
using Xberg.Internal.Layout;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Model download, caching and atomic publication, ported from Rust
/// <c>layout/model_manager.rs</c>.
/// </summary>
/// <remarks>
/// Nothing here reaches the network: the paths that matter — digest verification, publication,
/// rollback, cache layout — are all local, and the download itself was checked against the real
/// Hugging Face repositories separately.
/// </remarks>
public class LayoutModelManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xberg-lmm-" + Guid.NewGuid().ToString("N"));

    public LayoutModelManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string contents)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Sha256Of(string contents) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));

    /// <summary>
    /// Every model is pinned to a repository revision and a digest. A missing pin would mean a
    /// silently different model after an upstream push.
    /// </summary>
    [Fact]
    public void EveryModelIsPinnedToARevisionAndADigest()
    {
        Assert.NotEmpty(LayoutModelManager.Models);
        foreach (var model in LayoutModelManager.Models)
        {
            Assert.Equal(40, model.HfRevision.Length);          // a full git SHA-1
            Assert.Equal(64, model.Sha256.Length);              // a full SHA-256
            Assert.True(model.SizeBytes > 0);
            Assert.NotEmpty(model.RemoteFilename);
            Assert.NotEmpty(model.LocalFilename);
        }
    }

    /// <summary>A revision is only meaningful within its own repository, so the pair is the unit.</summary>
    [Fact]
    public void PinnedRepoRevisionsAreDeduplicatedPairs()
    {
        var pairs = LayoutModelManager.PinnedRepoRevisions();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        Assert.Contains(("xberg-io/layout-models", "c6bf493e2f7b0b9a29a5870da9880c14e20ff0a3"), pairs);
        Assert.Contains(("xberg-io/paddleocr-onnx-models", "bfaf0b492cfc1dee0c73245fc5860bfdcf2c3443"), pairs);
    }

    /// <summary>
    /// The cache path is Hugging Face's own layout: the repository with its slash doubled into
    /// dashes, then the revision snapshot, then the file's path within the repository. Getting
    /// this wrong means a model already on disk is downloaded again.
    /// </summary>
    [Fact]
    public void TheCachePathIsTheHuggingFaceLayout()
    {
        var model = LayoutModelManager.Models.Single(m => m.ModelType == "table_classifier");
        string path = LayoutModelManager.HfCacheRelativePath(model).Replace('\\', '/');
        Assert.Equal(
            "models--xberg-io--paddleocr-onnx-models/snapshots/"
            + "bfaf0b492cfc1dee0c73245fc5860bfdcf2c3443/v2/classifiers/PP-LCNet_x1_0_table_cls.onnx",
            path);
    }

    [Fact]
    public void AFileVerifiesAgainstItsOwnDigestAndNoOther()
    {
        string path = WriteFile("a.bin", "hello");
        Assert.True(LayoutModelManager.VerifyFile(path, Sha256Of("hello")));
        Assert.False(LayoutModelManager.VerifyFile(path, Sha256Of("goodbye")));
    }

    [Fact]
    public void AMissingFileDoesNotVerify()
    {
        Assert.False(LayoutModelManager.VerifyFile(
            Path.Combine(_dir, "absent.bin"), new string('0', 64)));
    }

    [Fact]
    public void PublishingMovesTheStagedFileIntoPlace()
    {
        string staged = WriteFile("staged.bin", "payload");
        string destination = Path.Combine(_dir, "out", "model.onnx");

        LayoutModelManager.AtomicPublish(
            staged, destination, Path.Combine(_dir, "out"), Sha256Of("payload"), "test");

        Assert.True(File.Exists(destination));
        Assert.Equal("payload", File.ReadAllText(destination));
        Assert.False(File.Exists(staged));
    }

    /// <summary>
    /// A publish that produces a file failing its digest must leave the previous one in place —
    /// a working cache beats an empty one.
    /// </summary>
    [Fact]
    public void AFailedPublishRollsBackToWhatWasThere()
    {
        string destination = Path.Combine(_dir, "out", "model.onnx");
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
        File.WriteAllText(destination, "original");

        string staged = WriteFile("bad.bin", "corrupt");

        Assert.Throws<InvalidOperationException>(() => LayoutModelManager.AtomicPublish(
            staged, destination, Path.Combine(_dir, "out"), Sha256Of("expected something else"), "test"));

        Assert.True(File.Exists(destination));
        Assert.Equal("original", File.ReadAllText(destination));
    }

    [Fact]
    public void PublishingLeavesNoBackupBehindOnSuccess()
    {
        string destination = Path.Combine(_dir, "out", "model.onnx");
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
        File.WriteAllText(destination, "old");

        string staged = WriteFile("new.bin", "new");
        LayoutModelManager.AtomicPublish(
            staged, destination, Path.Combine(_dir, "out"), Sha256Of("new"), "test");

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(destination + ".backup"));
    }

    /// <summary>
    /// A staged cache reports a model as cached only when the file on disk actually verifies —
    /// a truncated download that was interrupted mid-write looks fine to a size check alone.
    /// </summary>
    [Fact]
    public void ACachedModelIsOnlyCachedIfItVerifies()
    {
        var manager = new LayoutModelManager(_dir);
        Assert.False(manager.IsModelCached("table_classifier"));

        var model = LayoutModelManager.Models.Single(m => m.ModelType == "table_classifier");
        string dir = Path.Combine(_dir, "table_classifier");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, model.LocalFilename), "not the model");

        Assert.False(manager.IsModelCached("table_classifier"));
    }

    [Fact]
    public void AnUnknownModelTypeIsRefused()
    {
        var manager = new LayoutModelManager(_dir);
        Assert.Throws<InvalidOperationException>(() => manager.IsModelCached("no-such-model"));
    }

    /// <summary>
    /// A directory passed explicitly is used as a staging cache; passing none means the standard
    /// Hugging Face cache, with no second copy of every model.
    /// </summary>
    [Fact]
    public void AnExplicitDirectoryIsUsedAsGiven()
    {
        Assert.Equal(_dir, new LayoutModelManager(_dir).CacheDir);
        Assert.Equal(LayoutModelManager.DefaultHfCacheDir(),
                     new LayoutModelManager(null, new XbergOptions()).CacheDir);
    }

    /// <summary>
    /// The cache root is configuration, not ambient state: the manager reads no environment of
    /// its own, so a caller who wants a different root supplies it through the options.
    /// </summary>
    [Fact]
    public void TheCacheRootComesFromTheOptions()
    {
        var options = new XbergOptions { ModelCacheDirectory = _dir };
        Assert.Equal(_dir, new LayoutModelManager(null, options).CacheDir);

        // An explicit directory still wins — it selects the staged layout, not just a root.
        string other = Path.Combine(_dir, "explicit");
        Assert.Equal(other, new LayoutModelManager(other, options).CacheDir);
    }

    /// <summary>
    /// With downloads disabled, a model that is not already cached fails rather than reaching
    /// the network. The empty cache directory is what makes this test offline-safe.
    /// </summary>
    [Fact]
    public async Task DisablingDownloadsRefusesAnUncachedModel()
    {
        var manager = new LayoutModelManager(_dir, new XbergOptions { ModelDownloadsDisabled = true });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureModelAsync("table_classifier"));
        Assert.Contains("downloads are disabled", error.Message);
    }
}
