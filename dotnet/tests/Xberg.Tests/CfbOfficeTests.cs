using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Cfb;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the legacy OLE/CFB office extractors (Doc, Ppt, Xls, Msg, Hwp) and the shared
/// <see cref="CompoundFile"/> reader. Fixture-backed tests locate files under a
/// <c>test_documents</c> tree and are skipped gracefully when it is not present, mirroring the
/// Rust <c>#[cfg(test)]</c> "return if file missing" pattern.
/// </summary>
public sealed class CfbOfficeTests
{
    private static string? FindTestDocuments()
    {
        foreach (var candidate in new[]
        {
            "/workspace/test_documents",
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents"),
        })
        {
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static byte[]? Read(string relative)
    {
        var root = FindTestDocuments();
        if (root is null) return null;
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // ── CompoundFile reader ─────────────────────────────────────────────────────

    [Fact]
    public void CompoundFileOpensDocAndReadsWordDocumentStream()
    {
        byte[]? bytes = Read("vendored/unstructured/doc/simple.doc");
        if (bytes is null) return;

        var comp = CompoundFile.Open(bytes);
        Assert.True(comp.Exists("/WordDocument"));
        byte[]? wd = comp.TryReadStream("/WordDocument");
        Assert.NotNull(wd);
        Assert.True(wd!.Length >= 12);
        // Word magic 0xA5EC at the start of the FIB.
        Assert.Equal(0xEC, wd[0]);
        Assert.Equal(0xA5, wd[1]);
    }

    [Fact]
    public void CompoundFileRejectsNonCfb()
    {
        Assert.ThrowsAny<Exception>(() => CompoundFile.Open(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    // ── Doc ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DocExtractsTextAndAuthor()
    {
        byte[]? bytes = Read("vendored/unstructured/doc/fake.doc");
        if (bytes is null) return;

        var doc = new DocExtractor().Extract(bytes, "application/msword", new ExtractionConfig());
        Assert.Equal("application/msword", doc.MimeType);
        Assert.NotEmpty(doc.Elements);
        Assert.Equal(new List<string> { "Mr. Miagi" }, doc.Metadata.Authors);
        string text = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Lorem ipsum", text);
    }

    // ── Ppt ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PptExtractsSlidesAndText()
    {
        byte[]? bytes = Read("ppt/simple.ppt");
        if (bytes is null) return;

        var doc = new PptExtractor().Extract(bytes, "application/vnd.ms-powerpoint", new ExtractionConfig());
        Assert.Equal(PageUnitType.Slide, doc.Metadata.Pages!.UnitType);
        Assert.True(doc.Metadata.Pages.TotalCount > 0);
        string text = string.Join(" ", doc.Elements.Select(e => e.Text));
        Assert.Contains("Title Slide", text);
    }

    // ── Xls (BIFF) ───────────────────────────────────────────────────────────────

    [Fact]
    public void XlsExtractsSheetsAndCells()
    {
        byte[]? bytes = Read("xls/test_excel.xls");
        if (bytes is null) return;

        var doc = new XlsExtractor().Extract(bytes, "application/vnd.ms-excel", new ExtractionConfig());
        Assert.Single(doc.Tables);
        var cells = doc.Tables[0].Cells;
        Assert.Equal("Item", cells[0][0]);
        Assert.Contains("192000", cells[1]);
        var excel = Assert.IsType<ExcelMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(new List<string> { "Sheet1" }, excel.SheetNames);
    }

    [Fact]
    public void XlsHandlesMultipleSheets()
    {
        byte[]? bytes = Read("xls/tests_example.xls");
        if (bytes is null) return;

        var doc = new XlsExtractor().Extract(bytes, "application/vnd.ms-excel", new ExtractionConfig());
        var excel = Assert.IsType<ExcelMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(3u, excel.SheetCount);
    }

    // ── Msg ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MsgExtractsHeadersAndBody()
    {
        byte[]? bytes = Read("email/simple_msg.msg");
        if (bytes is null) return;

        var doc = new MsgExtractor().Extract(bytes, "application/vnd.ms-outlook", new ExtractionConfig());
        Assert.Equal("This is the subject", doc.Metadata.Subject);
        var email = Assert.IsType<EmailMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("peterpan@neverland.com", email.FromEmail);
        Assert.Contains("crocodile@neverland.com", email.ToEmails);
    }

    [Fact]
    public void MsgAdvertisesOutlookMimeOnly()
    {
        var mimes = new MsgExtractor().SupportedMimeTypes.ToList();
        Assert.Equal(new List<string> { "application/vnd.ms-outlook" }, mimes);
    }

    // ── Hwp (Rust parity: both fixtures fail with "no BodyText sections") ─────────

    [Fact]
    public void HwpThrowsNoBodyTextLikeRust()
    {
        byte[]? bytes = Read("hwp/styled_document.hwp");
        if (bytes is null) return;

        // Mirrors the Rust extractor, which never matches its BodyText streams and errors.
        Assert.ThrowsAny<Exception>(() => new HwpExtractor().Extract(bytes, "application/x-hwp", new ExtractionConfig()));
    }

    // ── Registry dispatch: XLS wins over XLSX for application/vnd.ms-excel ────────

    [Fact]
    public void RegistryRoutesMsExcelToXlsExtractor()
    {
        var registry = Registry.RegisterDefaults();
        var extractor = registry.ForMime("application/vnd.ms-excel");
        Assert.IsType<XlsExtractor>(extractor);
    }
}
