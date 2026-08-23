using System;
using System.IO;
using System.Linq;
using Xberg.Core;
using Xunit;

namespace Xberg.Tests;

public class XbergOptionsTests
{
    [Fact]
    public void DefaultsAreTheShippedBehaviour()
    {
        var options = new XbergOptions();
        Assert.True(options.UsePortedPdfSpans);
        Assert.Equal(25, options.PdfBaseSeconds);
        Assert.Equal(50.0, options.PdfMillisecondsPerPage);
        Assert.Equal(3600, options.PdfMaxSecondsPerDocument);
    }

    [Theory]
    [InlineData(0, 25.0)]        // floor: even a zero-page document gets the base
    [InlineData(1, 25.05)]
    [InlineData(1962, 123.1)]    // algebra_topology, which needs ~39 s
    [InlineData(4778, 263.9)]    // the Intel SDM, which needs ~55 s
    public void BudgetScalesWithPageCount(int pageCount, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, new XbergOptions().PdfBudgetSeconds(pageCount), 3);
    }

    [Fact]
    public void BudgetIsCappedHoweverManyPages()
    {
        var options = new XbergOptions();
        Assert.Equal(3600, options.PdfBudgetSeconds(10_000_000));
        // The cap binds from roughly 71,500 pages up; below that the scaling is live.
        Assert.True(options.PdfBudgetSeconds(70_000) < 3600);
    }

    [Fact]
    public void BudgetCoversTheCorpusWorstCasesWithHeadroom()
    {
        // Measured wall clock for a full extraction of each, on a quiet machine.
        var options = new XbergOptions();
        Assert.True(options.PdfBudgetSeconds(4778) > 55.0 * 2, "Intel SDM needs ~55 s");
        Assert.True(options.PdfBudgetSeconds(1962) > 39.0 * 2, "algebra_topology needs ~39 s");
    }

    [Fact]
    public void NegativePageCountFallsBackToTheBase()
    {
        Assert.Equal(25.0, new XbergOptions().PdfBudgetSeconds(-1), 3);
    }

    [Fact]
    public void ExtractionConfigPicksUpTheAmbientDefault()
    {
        var previous = XbergOptions.Default;
        try
        {
            XbergOptions.Default = new XbergOptions { PdfMaxSecondsPerDocument = 300 };
            Assert.Equal(300, new ExtractionConfig().Options.PdfMaxSecondsPerDocument);
        }
        finally
        {
            XbergOptions.Default = previous;
        }
    }

    [Fact]
    public void PerCallOptionsOverrideTheAmbientDefault()
    {
        var previous = XbergOptions.Default;
        try
        {
            XbergOptions.Default = new XbergOptions { PdfMaxSecondsPerDocument = 300 };
            var config = new ExtractionConfig { Options = new XbergOptions { PdfMaxSecondsPerDocument = 5 } };
            Assert.Equal(5, config.Options.PdfMaxSecondsPerDocument);
        }
        finally
        {
            XbergOptions.Default = previous;
        }
    }

    [Fact]
    public void NonPositiveCapDisablesTheGuard()
    {
        // A cap of "0 seconds" would otherwise mean "every document is already too late".
        Assert.Equal(long.MaxValue, new XbergOptions { PdfMaxSecondsPerDocument = 0 }.PdfDeadlineFromNow(10));
        Assert.Equal(long.MaxValue, new XbergOptions { PdfMaxSecondsPerDocument = -1 }.PdfDeadlineFromNow(10));
    }

    [Fact]
    public void DeadlineIsInTheFutureAndGrowsWithPageCount()
    {
        long now = DateTime.UtcNow.Ticks;
        var options = new XbergOptions();
        long small = options.PdfDeadlineFromNow(1);
        long large = options.PdfDeadlineFromNow(5000);

        Assert.True(small > now);
        Assert.True(large > small);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    public void FromEnvironmentReadsFlags(string? value, bool expected)
    {
        WithEnvironment("XBERG_OXIDE_SPANS", value, () =>
            Assert.Equal(expected, XbergOptions.FromEnvironment().UsePortedPdfSpans));
    }

    [Theory]
    [InlineData("300", 300)]
    [InlineData("0", 0)]
    [InlineData(null, 3600)]
    [InlineData("not-a-number", 3600)]   // unparseable leaves the default in place
    public void FromEnvironmentReadsNumbers(string? value, int expected)
    {
        WithEnvironment("XBERG_PDF_MAX_SECONDS", value, () =>
            Assert.Equal(expected, XbergOptions.FromEnvironment().PdfMaxSecondsPerDocument));
    }

    [Theory]
    [InlineData("10", 10.0)]
    [InlineData("0.5", 0.5)]            // fractional ms/page must survive the round trip
    [InlineData(null, 50.0)]
    public void FromEnvironmentReadsThePerPageAllowance(string? value, double expected)
    {
        WithEnvironment("XBERG_PDF_MS_PER_PAGE", value, () =>
            Assert.Equal(expected, XbergOptions.FromEnvironment().PdfMillisecondsPerPage));
    }

    [Fact]
    public void FromEnvironmentReadsTheBase()
    {
        WithEnvironment("XBERG_PDF_BASE_SECONDS", "90", () =>
            Assert.Equal(90, XbergOptions.FromEnvironment().PdfBaseSeconds));
    }

    [Fact]
    public void FromEnvironmentLeavesUnsetKnobsAtTheirDefaults()
    {
        // A harness varying one variable must not silently reset the others.
        WithEnvironment("XBERG_PDF_MAX_SECONDS", "300", () =>
        {
            WithEnvironment("XBERG_OXIDE_SPANS", null, () =>
            {
                var options = XbergOptions.FromEnvironment();
                Assert.Equal(300, options.PdfMaxSecondsPerDocument);
                Assert.True(options.UsePortedPdfSpans);
                Assert.Equal(25, options.PdfBaseSeconds);
                Assert.Equal(50.0, options.PdfMillisecondsPerPage);
            });
        });
    }

    /// <summary>
    /// The whole point of the options class: library code must not consult ambient process
    /// state. Only the opt-in <see cref="XbergOptions.FromEnvironment"/> factory may.
    /// </summary>
    [Fact]
    public void LibraryCodeNeverReadsTheEnvironment()
    {
        string? root = FindLibrarySourceRoot();
        Assert.True(root is not null, "could not locate the Xberg library sources from the test binary");

        var offenders = Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && Path.GetFileName(f) != "Options.cs")
            .Where(f => File.ReadAllText(f).Contains("GetEnvironmentVariable"))
            .Select(f => Path.GetRelativePath(root!, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "library code must take configuration through XbergOptions, not the environment; found: "
            + string.Join(", ", offenders));
    }

    private static string? FindLibrarySourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Xberg");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void WithEnvironment(string name, string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
