using System;
using System.IO;
using Xberg.Internal.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace Xberg.Tests;

public class TempProbe
{
    private readonly ITestOutputHelper _out;
    public TempProbe(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void Probe()
    {
        var path = "../../../../../../test_documents/vendored/pdfplumber/pdf/annotations-rotated-180.pdf";
        var pdf = PdfDocument.Open(File.ReadAllBytes(path));
        var ex = new PdfContentExtractor(pdf, DateTime.UtcNow.AddSeconds(30).Ticks);
        var spans = ex.Extract(pdf.GetPageContent(0), pdf.Resolve(pdf.Pages[0].Get("Resources")).AsDict());
        foreach (var s in spans) _out.WriteLine($"span '{s.Text}' bold={s.IsBold} fs={s.FontSize} font={s.FontName}");
        foreach (var e in PdfBookmarks.ExtractOutlineEntries(pdf))
            _out.WriteLine($"outline '{e.Title}' depth={e.Depth} page={e.PageNumber}");
    }
}
