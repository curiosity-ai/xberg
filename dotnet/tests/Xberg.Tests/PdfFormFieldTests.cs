using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Pdf;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// AcroForm field extraction and the injection of filled values as document elements
/// (crates/xberg/src/extractors/pdf/mod.rs :: inject_unrepresented_form_field_elements).
/// </summary>
public class PdfFormFieldTests
{
    [Fact]
    public void Extract_QualifiesKidNamesThroughTheParentChain()
    {
        var pdf = PdfDocument.Open(BuildFormPdf("/FT/Tx/T(name)/V(Alice)"));
        var fields = PdfFormFields.Extract(pdf);

        // Kids are emitted before the parent that named them.
        Assert.Equal(new[] { "form1.name", "form1" }, fields.Select(f => f.FullName));
        Assert.Equal("name", fields[0].Name);
        Assert.Equal("Alice", fields[0].Value);
        // A grouping node with /T but no /FT is surfaced, valueless.
        Assert.Null(fields[1].Value);
    }

    [Fact]
    public void Extract_DecodesUtf16BeValues()
    {
        var pdf = PdfDocument.Open(BuildFormPdf("/FT/Tx/T(name)/V<FEFF00410042>"));
        Assert.Equal("AB", PdfFormFields.Extract(pdf)[0].Value);
    }

    [Theory]
    [InlineData("/Yes", "true")]
    [InlineData("/On", "true")]
    [InlineData("/Off", "false")]
    [InlineData("/Export1", "Export1")]
    public void Extract_ReadsButtonStatesAsBooleansAndExportValuesAsNames(string value, string expected)
    {
        var pdf = PdfDocument.Open(BuildFormPdf("/FT/Btn/T(box)/V" + value));
        Assert.Equal(expected, PdfFormFields.Extract(pdf)[0].Value);
    }

    [Fact]
    public void Extract_JoinsMultiSelectArrays()
    {
        var pdf = PdfDocument.Open(BuildFormPdf("/FT/Ch/T(pick)/V[(one)(two)]"));
        Assert.Equal("one, two", PdfFormFields.Extract(pdf)[0].Value);
    }

    [Fact]
    public void Extract_SkipsNodesWithNeitherNameNorType()
    {
        var pdf = PdfDocument.Open(BuildFormPdf("/V(orphan)"));
        // Only the named parent survives; the anonymous kid is nothing to report.
        Assert.Equal(new[] { "form1" }, PdfFormFields.Extract(pdf).Select(f => f.FullName));
    }

    [Fact]
    public void Inject_PushesOneParagraphPerValueNoElementCarries()
    {
        var doc = new InternalDocument("pdf");
        doc.PushElement(InternalElement.TextElement(ElementKind.Paragraph, "already says Alice here", 0));

        PdfExtractor.InjectUnrepresentedFormFieldElements(doc, new List<PdfAcroFormField>
        {
            new() { Name = "name", FullName = "form1.name", Value = "Alice" },
            new() { Name = "city", FullName = "form1.city", Value = "Berlin" },
            new() { Name = "empty", FullName = "form1.empty", Value = "" },
            new() { Name = "bare", FullName = "", Value = "Zed" },
        });

        Assert.Equal(
            new[] { "already says Alice here", "form1.city: Berlin", "bare: Zed" },
            doc.Elements.Select(e => e.Text));
    }

    [Fact]
    public void Extract_SurfacesAUtf16ValueThePageTextSpliceMangles()
    {
        var doc = new PdfExtractor().Extract(
            BuildFormPdf("/FT/Tx/T(name)/V<FEFF00410042>"), "application/pdf",
            new ExtractionConfig { OutputFormat = OutputFormat.Plain });

        // The annotation layer reads /V as UTF-8, so the byte-order mark reaches the page
        // text as replacement characters and the decoded value is genuinely unrepresented.
        Assert.Contains("\uFFFD\uFFFD\u0000A\u0000B", doc.Elements[^2].Text, StringComparison.Ordinal);
        Assert.Equal("form1.name: AB", doc.Elements[^1].Text);
    }

    /// <summary>One page, one widget annotation whose field dictionary is <paramref name="kidEntries"/>,
    /// under a /T-only parent field named <c>form1</c>.</summary>
    private static byte[] BuildFormPdf(string kidEntries)
    {
        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R/AcroForm 6 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 4 0 R>>>>"
                + "/Contents 5 0 R/Annots[7 0 R]>>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };
        const string stream = "BT /F1 24 Tf 72 700 Td (Label) Tj ET";
        objs.Add($"<</Length {stream.Length}>>\nstream\n{stream}\nendstream");
        objs.Add("<</Fields[8 0 R]>>");
        objs.Add($"<</Type/Annot/Subtype/Widget/Rect[100 600 300 620]/Parent 8 0 R{kidEntries}>>");
        objs.Add("<</T(form1)/Kids[7 0 R]>>");

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xrefPos = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n").Append($"0 {objs.Count + 1}\n").Append("0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Count + 1}/Root 1 0 R>>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
