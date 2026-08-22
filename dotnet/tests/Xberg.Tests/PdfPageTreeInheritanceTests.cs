using System.Text;
using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Inheritable page attributes (ISO 32000-1 Table 30) across a nested page tree.
/// </summary>
public class PdfPageTreeInheritanceTests
{
    /// <summary>Catalog → outer Pages → inner Pages → one Page, each node's dictionary given.</summary>
    private static byte[] BuildNestedPageTree(string outerPages, string innerPages, string page)
    {
        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            outerPages,
            innerPages,
            page,
        };

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

    [Fact]
    public void TheOutermostAncestorsMediaBoxWins()
    {
        var doc = PdfDocument.Open(BuildNestedPageTree(
            "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            "<</Type/Pages/Kids[4 0 R]/Count 1/Parent 2 0 R/MediaBox[0 0 612 792]>>",
            "<</Type/Page/Parent 3 0 R>>"));

        var (llx, lly, urx, ury) = doc.GetPageMediaBox(0);
        Assert.Equal((0, 0, 595, 842), (llx, lly, urx, ury));
    }

    [Fact]
    public void ThePagesOwnMediaBoxStillOutranksEveryAncestor()
    {
        var doc = PdfDocument.Open(BuildNestedPageTree(
            "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            "<</Type/Pages/Kids[4 0 R]/Count 1/Parent 2 0 R>>",
            "<</Type/Page/Parent 3 0 R/MediaBox[0 0 200 400]>>"));

        var (llx, lly, urx, ury) = doc.GetPageMediaBox(0);
        Assert.Equal((0, 0, 200, 400), (llx, lly, urx, ury));
    }
}
