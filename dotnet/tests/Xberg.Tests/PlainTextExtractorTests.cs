using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// How a plain-text document is split into paragraphs.
/// </summary>
public class PlainTextExtractorTests
{
    private static List<string> Paragraphs(string text) =>
        new PlainTextExtractor()
            .Extract(Encoding.UTF8.GetBytes(text), "text/plain", new ExtractionConfig())
            .Elements.Select(e => e.Text).ToList();

    [Fact]
    public void ABlankLineSeparatesParagraphs()
    {
        Assert.Equal(new[] { "First.", "Second." }, Paragraphs("First.\n\nSecond.\n"));
    }

    [Fact]
    public void ACrlfDocumentSplitsAndKeepsNoCarriageReturns()
    {
        // This extractor builds the document itself, which bypasses the paragraph splitter
        // downstream, so "\r\n\r\n" never matched the blank-line boundary: a CRLF document
        // collapsed into one paragraph and carried its carriage returns into the text.
        Assert.Equal(
            new[] { "First line.\nSame paragraph.", "Second paragraph." },
            Paragraphs("First line.\r\nSame paragraph.\r\n\r\nSecond paragraph.\r\n"));
    }

    [Fact]
    public void ALoneCarriageReturnAlsoEndsALine()
    {
        Assert.Equal(new[] { "One.", "Two." }, Paragraphs("One.\r\rTwo."));
    }

    [Fact]
    public void CountsAreReportedOverTheSourceText()
    {
        var doc = new PlainTextExtractor().Extract(
            Encoding.UTF8.GetBytes("alpha beta\ngamma\n"), "text/plain", new ExtractionConfig());
        var text = (TextMetadata)doc.Metadata.Format!.Payload!;
        Assert.Equal(2u, text.LineCount);
        Assert.Equal(3u, text.WordCount);
    }
}
