using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The hostile-input limits, ported from Rust <c>extractors/security.rs</c>.
/// </summary>
public class SecurityBudgetTests
{
    private static SecurityLimits Tight(Action<SecurityLimits> tweak)
    {
        var l = new SecurityLimits();
        tweak(l);
        return l;
    }

    [Fact]
    public void TheDepthCapAcceptsItsOwnValueAndRefusesTheNextLevel()
    {
        var budget = new SecurityBudget(Tight(l => { l.MaxXmlDepth = 3; l.MaxNestingDepth = 3; }));
        budget.Enter();
        budget.Enter();
        budget.Enter();
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.Enter());
    }

    [Fact]
    public void TheTighterOfTheTwoNestingKnobsWins()
    {
        // Both bound the same parse, so taking the looser would discard a caller's attempt to
        // clamp nesting through whichever knob they reached for.
        var budget = new SecurityBudget(Tight(l => { l.MaxXmlDepth = 2; l.MaxNestingDepth = 1000; }));
        budget.Enter();
        budget.Enter();
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.Enter());
    }

    [Fact]
    public void AnUnbalancedCloseCannotDriveTheDepthCounterNegative()
    {
        // A malformed document closes more than it opens; if the counter went negative, the
        // nesting that followed would be free.
        var budget = new SecurityBudget(Tight(l => { l.MaxXmlDepth = 1; l.MaxNestingDepth = 1; }));
        for (int i = 0; i < 5; i++) budget.Leave();
        budget.Enter();
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.Enter());
    }

    [Fact]
    public void TheEntityCapCountsUtf8BytesNotCharacters()
    {
        // Upstream measures `str::len()`. Two UTF-16 code units of `é` are two UTF-8 bytes, but
        // an emoji is four — counting characters would let a string through that upstream refuses.
        var budget = new SecurityBudget(Tight(l => l.MaxEntityLength = 4));
        budget.CheckEntity("ab");
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.CheckEntity("🙂🙂"));
    }

    [Fact]
    public void TheGrowthCounterSaturatesRatherThanWrapping()
    {
        // The lengths being added come from attacker-controlled headers and can sit near the top
        // of the range; an unchecked add would wrap the total back down to something small.
        var budget = new SecurityBudget(Tight(l => l.MaxContentSize = 1000));
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.AccountText(long.MaxValue));
        // Still refusing after the overflow, rather than having reset to a low total.
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.AccountText(1));
    }

    [Fact]
    public void TheIterationCapStopsAnUnboundedParserLoop()
    {
        var budget = new SecurityBudget(Tight(l => l.MaxIterations = 2));
        budget.Step();
        budget.Step();
        Assert.Throws<Xberg.Core.SecurityException>(() => budget.Step());
    }

    [Fact]
    public void TheReportingVariantsStopWithoutThrowing()
    {
        // An EPUB that runs out of budget mid-spine keeps the chapters it already read.
        var budget = new SecurityBudget(Tight(l => { l.MaxIterations = 1; l.MaxContentSize = 1; }));
        Assert.True(budget.TryStep());
        Assert.False(budget.TryStep());
        Assert.False(budget.TryAccountText(1000));
    }

    /// <summary>
    /// A zip whose declared sizes exceed the compression-ratio cap is refused from the central
    /// directory alone, before a single entry is decompressed.
    /// </summary>
    [Fact]
    public void AZipBombIsRefusedOnItsDeclaredSizes()
    {
        byte[] zip = ZipOf(("bomb.txt", new string('A', 200_000)));
        var config = new ExtractionConfig { SecurityLimits = Tight(l => l.MaxCompressionRatio = 2) };

        var result = new Extractor().Extract(
            ExtractInput.FromBytes(zip, "application/zip", "bomb.zip"), config);

        var error = Assert.Single(result.Errors);
        // Upstream reports the archive extractor's refusal as a validation failure, not a
        // security one — the distinction is upstream's, and the error item carries it.
        Assert.Equal("validation", error.ErrorType);
        Assert.Equal(1002u, error.Code);
        Assert.Contains("ZIP bomb", error.Message);
    }

    [Fact]
    public void AnArchiveWithinItsLimitsIsStillExtracted()
    {
        byte[] zip = ZipOf(("readme.txt", "hello world"));
        var result = new Extractor().Extract(
            ExtractInput.FromBytes(zip, "application/zip", "ok.zip"), new ExtractionConfig());

        Assert.Empty(result.Errors);
        Assert.Single(result.Results);
    }

    /// <summary>
    /// A JSON document nested past the depth cap is refused, and the error item carries the
    /// <c>security</c> / 1006 pair upstream's <c>XbergError::Security</c> maps to.
    /// </summary>
    [Fact]
    public void DeeplyNestedJsonIsRefusedAsASecurityError()
    {
        // 40 deep, not 400: System.Text.Json refuses past its own 64-level reader depth first,
        // and that refusal is a parse failure rather than the limit under test here.
        string json = new string('[', 40) + "1" + new string(']', 40);
        var config = new ExtractionConfig { SecurityLimits = Tight(l => l.MaxNestingDepth = 10) };

        var result = new Extractor().Extract(
            ExtractInput.FromBytes(Encoding.UTF8.GetBytes(json), "application/json", "deep.json"), config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("security", error.ErrorType);
        Assert.Equal(1006u, error.Code);
    }

    [Fact]
    public void ACsvClaimingMoreCellsThanTheCapIsRefused()
    {
        var text = new StringBuilder();
        for (int row = 0; row < 40; row++) text.Append(string.Join(',', Enumerable.Repeat("x", 40))).Append('\n');
        var config = new ExtractionConfig { SecurityLimits = Tight(l => l.MaxTableCells = 100) };

        var result = new Extractor().Extract(
            ExtractInput.FromBytes(Encoding.UTF8.GetBytes(text.ToString()), "text/csv", "wide.csv"), config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("security", error.ErrorType);
        Assert.Contains("table cells", error.Message);
    }

    [Fact]
    public void AParentDirectoryComponentIsTraversalButAVersionRangeIsNot()
    {
        Assert.True(PathSafety.HasPathTraversal("word/../../etc/passwd"));
        Assert.False(PathSafety.HasPathTraversal("word/images/photo.png"));
        // Split into components rather than searched as a string, so a list-numbering prefix
        // is not mistaken for a traversal.
        Assert.False(PathSafety.HasPathTraversal("chapter 1..2"));
    }

    [Fact]
    public void DefaultLimitsLeaveOrdinaryDocumentsAlone()
    {
        // The whole point of the defaults: nothing in the corpus comes near them.
        var doc = new Extractor().Extract(
            ExtractInput.FromBytes(Encoding.UTF8.GetBytes("# Title\n\nSome prose.\n"), "text/markdown", "a.md"),
            new ExtractionConfig());
        Assert.Empty(doc.Errors);
    }

    private static byte[] ZipOf(params (string Name, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }

    /// <summary>
    /// The refusal message is upstream's, word for word. It is not cosmetic: the corpus's
    /// DocTags groundtruth files nest `&lt;loc_*&gt;` and `&lt;page_*&gt;` without ever closing
    /// them, and upstream's golden for `redp5110_sampled.doctags.txt` records exactly
    /// "Security violation: Nesting too deep: 1025 levels (max: 1024)". Before the limits were
    /// threaded through, the port extracted those files where upstream refuses them.
    /// </summary>
    [Fact]
    public void TheDepthRefusalReadsTheSameAsUpstreams()
    {
        var budget = new SecurityBudget(new SecurityLimits());
        var error = Assert.Throws<Xberg.Core.SecurityException>(() =>
        {
            for (int i = 0; i < 1025; i++) budget.Enter();
        });
        Assert.Equal("Nesting too deep: 1025 levels (max: 1024)", error.Message);
        Assert.Equal(SecurityViolation.NestingTooDeep, error.Violation);
    }

    /// <summary>
    /// An XML document that never closes its tags walks past the default nesting cap and is
    /// refused, rather than being walked to whatever depth it asks for.
    /// </summary>
    [Fact]
    public void XmlThatNeverClosesItsTagsIsRefusedAtTheDepthCap()
    {
        string xml = string.Concat(Enumerable.Range(0, 1200).Select(i => $"<t{i}>"));
        var result = new Extractor().Extract(
            ExtractInput.FromBytes(Encoding.UTF8.GetBytes(xml), "application/xml", "deep.xml"),
            new ExtractionConfig());

        var error = Assert.Single(result.Errors);
        Assert.Equal("security", error.ErrorType);
        Assert.Contains("Nesting too deep", error.Message);
    }
}
