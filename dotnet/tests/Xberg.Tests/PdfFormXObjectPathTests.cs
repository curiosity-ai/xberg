using System.Text;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the path isolation pdf_oxide applies at both Form XObject boundaries
/// (`process_form_xobject_paths`, document.rs:18025 and :18204): whatever path
/// construction is pending is discarded on the way in and on the way out.
/// </summary>
/// <remarks>
/// The extractor answers only `S`, `s`, `f`/`F`/`f*`, `b` and `n`; `B`, `B*` and `b*`
/// deliberately neither paint nor clear, so subpaths painted with them stay in the
/// buffer. Without the boundary discard they leak out of the form and are emitted as
/// part of whatever the page strokes next — one primitive spanning the whole form,
/// which bridges unrelated ruling-line clusters and widens the table bounding boxes
/// built from them.
/// </remarks>
public class PdfFormXObjectPathTests
{
    /// <summary>A one-page document whose page content is <paramref name="pageContent"/> and
    /// whose `/Fx` Form XObject content is <paramref name="formContent"/>.</summary>
    private static byte[] BuildDocument(string pageContent, string formContent)
    {
        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]"
                + "/Resources<</XObject<</Fx 5 0 R>>>>/Contents 4 0 R>>",
            $"<</Length {Encoding.ASCII.GetByteCount(pageContent)}>>\nstream\n{pageContent}\nendstream",
            $"<</Type/XObject/Subtype/Form/BBox[0 0 612 792]"
                + $"/Length {Encoding.ASCII.GetByteCount(formContent)}>>\nstream\n{formContent}\nendstream",
        };

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

    private static List<PdfPath> PagePaths(string pageContent, string formContent)
    {
        var doc = PdfDocument.Open(BuildDocument(pageContent, formContent));
        var extractor = new PdfContentExtractor(doc, long.MaxValue);
        var resources = doc.Resolve(doc.Pages[0].Get("Resources")).AsDict();
        extractor.Extract(doc.GetPageContent(0), resources);
        return extractor.Paths;
    }

    /// <summary>A 200x200 box painted with `B*`, which the extractor never answers.</summary>
    private const string UnpaintedBoxForm = "10 10 200 200 re\nB*\n";

    [Fact]
    public void APendingFormSubpathDoesNotLeakIntoTheNextStrokedPagePath()
    {
        // The page strokes one short rule after the form. Only that rule may be emitted,
        // and only with its own geometry.
        var paths = PagePaths("q\n/Fx Do\nQ\n2 w\n400 100 m 480 100 l S\n", UnpaintedBoxForm);

        var path = Assert.Single(paths);
        Assert.Equal(2, path.Operations.Count);
        Assert.Equal(400, path.Bbox.X, 3);
        Assert.Equal(80, path.Bbox.Width, 3);
        Assert.Equal(0, path.Bbox.Height, 3);
    }

    [Fact]
    public void APendingPageSubpathDoesNotLeakIntoTheForm()
    {
        // The page leaves a `B*` box pending, then invokes the form, which strokes a
        // rule of its own. The pending page box must not join it.
        var paths = PagePaths("10 600 200 100 re\nB*\n/Fx Do\n", "2 w\n400 100 m 480 100 l S\n");

        var path = Assert.Single(paths);
        Assert.Equal(2, path.Operations.Count);
        Assert.Equal(400, path.Bbox.X, 3);
        Assert.Equal(80, path.Bbox.Width, 3);
    }

    [Fact]
    public void AFormPathThatIsActuallyPaintedIsStillCollected()
    {
        // The discard is only for construction left pending at the boundary: a form
        // subpath the form itself strokes is emitted normally, in page space.
        var paths = PagePaths("q\n/Fx Do\nQ\n", "2 w\n100 700 m 300 700 l S\n");

        var path = Assert.Single(paths);
        Assert.Equal(100, path.Bbox.X, 3);
        Assert.Equal(200, path.Bbox.Width, 3);
    }
}
