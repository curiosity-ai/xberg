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
}
