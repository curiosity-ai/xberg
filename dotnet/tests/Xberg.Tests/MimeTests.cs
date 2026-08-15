using System.Text;
using Xberg.Core;
using Xunit;

namespace Xberg.Tests;

public class MimeTests
{
    [Theory]
    [InlineData("file.txt", "text/plain")]
    [InlineData("a/b/doc.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("index.HTML", "text/html")]
    [InlineData("data.csv", "text/csv")]
    [InlineData("config.json", "application/json")]
    public void DetectFromExtension(string path, string expected)
    {
        Assert.Equal(expected, Mime.DetectMimeType(path, checkExists: false));
    }

    [Fact]
    public void UnknownExtensionReturnsNull()
    {
        Assert.Null(Mime.DetectMimeType("mystery.zzz", checkExists: false));
    }

    [Fact]
    public void DetectPdfFromBytes()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n...");
        Assert.Equal("application/pdf", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void DetectPngFromMagic()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal("image/png", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void DetectJsonFromText()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"key\": \"value\"}");
        Assert.Equal("application/json", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void DetectPlainTextFromText()
    {
        var bytes = Encoding.UTF8.GetBytes("just some words here");
        Assert.Equal("text/plain", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void GetExtensionsForMimeIncludesKnown()
    {
        var exts = Mime.GetExtensionsForMime("image/jpeg");
        Assert.Contains("jpg", exts);
        Assert.Contains("jpeg", exts);
    }

    // ── extension vs content ───────────────────────────────────────────────────

    /// <summary>
    /// An extension is a claim; a recognisable signature is evidence. The corpus carries markup
    /// and DocTags streams saved as `.txt`, which routed to the plain-text extractor and came out
    /// as their own raw source.
    /// </summary>
    [Fact]
    public void ContentOverrulesTheExtensionWhenTheyDisagree()
    {
        var markup = Encoding.UTF8.GetBytes("<doctag><text>Body.</text></doctag>");
        Assert.Equal("application/xml", Mime.ResolveWithContent("text/plain", markup));

        var pdf = Encoding.UTF8.GetBytes("%PDF-1.7\nrest");
        Assert.Equal("application/pdf", Mime.ResolveWithContent("text/plain", pdf));
    }

    [Fact]
    public void AgreementLeavesTheExtensionAlone()
    {
        var text = Encoding.UTF8.GetBytes("just some words here");
        Assert.Equal("text/plain", Mime.ResolveWithContent("text/plain", text));
    }

    /// <summary>
    /// Plain text is not a signature — it is the absence of one, and must never displace a more
    /// specific extension.
    /// </summary>
    [Fact]
    public void PlainTextContentNeverDisplacesAMoreSpecificExtension()
    {
        var prose = Encoding.UTF8.GetBytes("# Heading\n\nSome prose.\n");
        Assert.Equal("text/markdown", Mime.ResolveWithContent("text/markdown", prose));
    }

    /// <summary>
    /// A generic signature is strictly less informative than the extension rather than in
    /// conflict with it: XML cannot tell FictionBook from DocBook, and JSON cannot tell a
    /// notebook from line-delimited JSON.
    /// </summary>
    [Fact]
    public void GenericSignaturesDoNotDisplaceASpecificVocabulary()
    {
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><FictionBook/>");
        Assert.Equal("application/x-fictionbook+xml", Mime.ResolveWithContent("application/x-fictionbook+xml", xml));

        var json = Encoding.UTF8.GetBytes("{\"cells\": []}");
        Assert.Equal("application/x-ipynb+json", Mime.ResolveWithContent("application/x-ipynb+json", json));
    }

    /// <summary>With no extension to go on, content is all there is.</summary>
    [Fact]
    public void WithoutAnExtensionContentDecidesAlone()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal("image/png", Mime.ResolveWithContent(null, png));
    }

    /// <summary>An empty file has nothing to contradict the extension with.</summary>
    [Fact]
    public void EmptyContentLeavesTheExtensionAlone()
    {
        Assert.Equal("text/markdown", Mime.ResolveWithContent("text/markdown", ReadOnlySpan<byte>.Empty));
    }
}
