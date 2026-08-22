using System.IO;
using System.Linq;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// End-to-end cover for the page-level driver: a real PDF in, ordered spans out. This is the
/// first point at which the whole ported pipeline — fonts, content parsing, operator
/// dispatch, text showing, span merging, reading order — runs as one piece.
/// </summary>
public class OxPageExtractorTests
{
    private static PdfDocument Open(string relative) =>
        PdfDocument.Open(File.ReadAllBytes(Path.Combine("../../../../../../test_documents", relative)));

    [Fact]
    public void ASimplePageYieldsItsTextAsOrderedSpans()
    {
        var doc = Open("vendored/pdfplumber/pdf/annotations.pdf");
        var page = OxPageExtractor.ExtractPageText(doc, 0);

        Assert.NotEmpty(page.Spans);
        string text = string.Concat(page.Spans.Select(s => s.Text));
        Assert.Contains("Dummy PDF file", text);
    }

    [Fact]
    public void ThePagesMediaBoxIsReported()
    {
        var doc = Open("vendored/pdfplumber/pdf/annotations.pdf");
        var page = OxPageExtractor.ExtractPageText(doc, 0);

        Assert.Equal(595, page.PageWidth, 0);
        Assert.Equal(842, page.PageHeight, 0);
    }

    [Fact]
    public void SpansCarryTheirFontAndGeometry()
    {
        var doc = Open("vendored/pdfplumber/pdf/annotations.pdf");
        var span = OxPageExtractor.ExtractPageText(doc, 0).Spans.First(s => s.Text.Trim().Length > 0);

        Assert.True(span.FontSize > 0, "a shown run must carry the size it was set at");
        Assert.True(span.Bbox.Width > 0, "a shown run must cover ground");
        Assert.False(string.IsNullOrEmpty(span.FontName));
    }

    [Fact]
    public void APageIndexPastTheEndYieldsNothingRatherThanThrowing()
    {
        var doc = Open("vendored/pdfplumber/pdf/annotations.pdf");
        Assert.Empty(OxPageExtractor.ExtractPageText(doc, 99).Spans);
    }
}
