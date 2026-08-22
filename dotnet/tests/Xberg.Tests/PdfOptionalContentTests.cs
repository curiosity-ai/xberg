using System.Text;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Ports `compute_default_off_ocgs` from pdf_oxide's `optional_content.rs`.
/// </summary>
public class PdfOptionalContentTests
{
    private static byte[] BuildDocumentWithOcProperties(string ocProperties, params string[] extraObjects)
    {
        var objs = new List<string>
        {
            $"<</Type/Catalog/Pages 2 0 R{ocProperties}>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>",
        };
        objs.AddRange(extraObjects);

        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xrefPos = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n");
        sb.Append($"0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string Watermark = "<</Type/OCG/Name(Watermark)>>";
    private const string Body = "<</Type/OCG/Name(Body)>>";

    [Fact]
    public void ADocumentWithoutOptionalContentHidesNothing()
    {
        var doc = PdfDocument.Open(BuildDocumentWithOcProperties(""));
        Assert.Empty(PdfOptionalContent.DefaultOffOcgs(doc));
    }

    [Fact]
    public void OnlyTheGroupsNamedInOffAreHiddenUnderTheDefaultBaseState()
    {
        var doc = PdfDocument.Open(BuildDocumentWithOcProperties(
            "/OCProperties<</OCGs[4 0 R 5 0 R]/D<</OFF[4 0 R]>>>>", Watermark, Body));
        Assert.Equal(new[] { "Watermark" }, PdfOptionalContent.DefaultOffOcgs(doc));
    }

    [Fact]
    public void AnOffBaseStateHidesEveryGroupNotNamedInOn()
    {
        var doc = PdfDocument.Open(BuildDocumentWithOcProperties(
            "/OCProperties<</OCGs[4 0 R 5 0 R]/D<</BaseState/OFF/ON[5 0 R]>>>>", Watermark, Body));
        Assert.Equal(new[] { "Watermark" }, PdfOptionalContent.DefaultOffOcgs(doc));
    }
}
