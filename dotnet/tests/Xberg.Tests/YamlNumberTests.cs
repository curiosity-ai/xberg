using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// How YAML scalars that look like numbers reach the extracted text.
/// </summary>
public class YamlNumberTests
{
    private static string Plain(string yaml)
    {
        var doc = new StructuredExtractor().Extract(
            Encoding.UTF8.GetBytes(yaml), "application/yaml", new ExtractionConfig());
        return string.Join("\n", doc.Elements.Select(e => e.Text));
    }

    [Fact]
    public void ACountAboveTheSignedRangeStaysAnInteger()
    {
        // A 64-bit hash exceeds long.MaxValue but is still an exact integer. Reading it as a
        // double loses digits and prints it as 1.4550011543526097e19.
        Assert.Contains("binary_hash: 14550011543526094526", Plain("binary_hash: 14550011543526094526\n"));
    }

    [Fact]
    public void AFloatWrittenWithATrailingZeroStaysAFloat()
    {
        // "397.0" says the value is a measurement, not a count; printing it as 397 changes that.
        Assert.Contains("height: 397.0", Plain("height: 397.0\n"));
    }

    [Fact]
    public void AnIntegerStaysAnInteger()
    {
        Assert.Contains("count: 397", Plain("count: 397\n"));
    }

    [Fact]
    public void NumberishTextThatIsNotANumberIsStillText()
    {
        // A bare "-", a version triple and an out-of-range exponent all look number-ish and
        // none of them is a number; each must survive as what it is rather than throwing.
        string plain = Plain("dash: -\nversion: 1.2.3\nhuge: 1e999\n");
        Assert.Contains("dash: -", plain);
        Assert.Contains("version: 1.2.3", plain);
        Assert.Contains("huge: 1e999", plain);
    }
}
