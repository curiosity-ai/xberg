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
        Assert.Equal(25, options.PdfMaxSecondsPerDocument);
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
    public void NonPositiveDeadlineDisablesTheGuard()
    {
        // A guard of "0 seconds" would otherwise mean "every document is already too late".
        Assert.Equal(long.MaxValue, new XbergOptions { PdfMaxSecondsPerDocument = 0 }.PdfDeadlineFromNow());
        Assert.Equal(long.MaxValue, new XbergOptions { PdfMaxSecondsPerDocument = -1 }.PdfDeadlineFromNow());
    }

    [Fact]
    public void PositiveDeadlineIsInTheFutureAndScalesWithTheSetting()
    {
        long now = DateTime.UtcNow.Ticks;
        long shortDeadline = new XbergOptions { PdfMaxSecondsPerDocument = 1 }.PdfDeadlineFromNow();
        long longDeadline = new XbergOptions { PdfMaxSecondsPerDocument = 300 }.PdfDeadlineFromNow();

        Assert.True(shortDeadline > now);
        Assert.True(longDeadline > shortDeadline);
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
    [InlineData(null, 25)]
    [InlineData("not-a-number", 25)]   // unparseable leaves the default in place
    public void FromEnvironmentReadsNumbers(string? value, int expected)
    {
        WithEnvironment("XBERG_PDF_MAX_SECONDS", value, () =>
            Assert.Equal(expected, XbergOptions.FromEnvironment().PdfMaxSecondsPerDocument));
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
