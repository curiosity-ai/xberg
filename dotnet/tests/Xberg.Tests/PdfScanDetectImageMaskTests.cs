using System.Text;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// A stencil mask carries no <c>/ColorSpace</c>, and the reference image extractor rejects any
/// image dictionary without one, so masks are invisible to scan detection: they add neither
/// raster coverage nor a codec vote.
/// </summary>
public class PdfScanDetectImageMaskTests
{
    /// <summary>One page, 100x100, painting the named XObjects over the whole page.</summary>
    private static byte[] BuildPagePaintingImages(IEnumerable<(string Name, string Dict)> images)
    {
        var imageList = images.ToList();
        string content = string.Concat(imageList.Select(i => $"q 100 0 0 100 0 0 cm /{i.Name} Do Q "));
        var resourceNames = string.Concat(
            imageList.Select((img, idx) => $"/{img.Name} {5 + idx} 0 R"));

        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Resources<</XObject<<{resourceNames}>>>>/Contents 4 0 R>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
        };
        foreach (var (_, dict) in imageList)
            objs.Add($"{dict}\nstream\n\nendstream");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xrefPos = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n");
        sb.Append($"0 {objs.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Count + 1}/Root 1 0 R>>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string JpegImage =
        "<</Type/XObject/Subtype/Image/Width 8/Height 8/BitsPerComponent 8"
        + "/ColorSpace/DeviceRGB/Filter/DCTDecode/Length 0>>";

    private const string CcittStencil =
        "<</Type/XObject/Subtype/Image/Width 8/Height 8/BitsPerComponent 1"
        + "/ImageMask true/Filter/CCITTFaxDecode/DecodeParms<</K -1/Columns 8>>/Length 0>>";

    [Fact]
    public void CcittStencilOverAPhotoDoesNotEarnTheBilevelBonus()
    {
        var doc = PdfDocument.Open(BuildPagePaintingImages(new[]
        {
            ("Im0", JpegImage),
            ("Im1", CcittStencil),
        }));

        // Full-page raster with no text layer, graded as a photo: 0.50 + 0.35.
        Assert.True(Math.Abs(PdfScanDetect.Detect(doc).Confidence - 0.85) < 1e-5);
    }

    [Fact]
    public void CcittImageWithAColorSpaceStillEarnsTheBilevelBonus()
    {
        const string ccittGray =
            "<</Type/XObject/Subtype/Image/Width 8/Height 8/BitsPerComponent 1"
            + "/ColorSpace/DeviceGray/Filter/CCITTFaxDecode/DecodeParms<</K -1/Columns 8>>/Length 0>>";
        var doc = PdfDocument.Open(BuildPagePaintingImages(new[] { ("Im0", ccittGray) }));

        Assert.True(Math.Abs(PdfScanDetect.Detect(doc).Confidence - 0.95) < 1e-5);
    }

    [Fact]
    public void APageOfStencilsAloneHasNoRasterCoverage()
    {
        var doc = PdfDocument.Open(BuildPagePaintingImages(new[] { ("Im0", CcittStencil) }));

        Assert.Equal(0.0, PdfScanDetect.Detect(doc).Confidence);
    }
}
