using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Ooxml;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// <see cref="SecurityLimits.MaxPages"/>, the per-document page ceiling upstream added in
/// <c>feat(security): add a per-document page limit</c> (GH#1451) and then extended past the PDF
/// path in <c>feat(security): enforce max_pages beyond the PDF path</c>.
/// </summary>
/// <remarks>
/// Cost follows page count, not byte count: a scanned page can compress to a few kilobytes, so a
/// document well under the byte limits can still hold thousands of pages of per-page work. The
/// check rejects rather than truncates, matching every other primary-document limit, and
/// <c>&gt;</c> not <c>&gt;=</c> is deliberate — a document exactly at the ceiling is within it.
/// </remarks>
public sealed class PageLimitTests
{
    private static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }

    private static byte[] PptxWithSlides(int count)
    {
        var parts = new List<(string, string)>
        {
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(Enumerable.Range(1, count).Select(i =>
                    $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" " +
                    $"Target=\"slides/slide{i}.xml\"/>")) +
                "</Relationships>"),
        };
        for (int i = 1; i <= count; i++)
            parts.Add(($"ppt/slides/slide{i}.xml",
                "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                $"<p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Slide {i}</a:t></a:r></a:p>" +
                "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));
        return Zip(parts.ToArray());
    }

    private static byte[] OdpWithSlides(int count) => Zip(
        ("content.xml",
            "<office:document-content " +
            "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
            "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\">" +
            "<office:body><office:presentation>" +
            string.Concat(Enumerable.Range(1, count).Select(i => $"<draw:page draw:name=\"page{i}\"/>")) +
            "</office:presentation></office:body></office:document-content>"));

    private const string PptxMime =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    private const string OdpMime = "application/vnd.oasis.opendocument.presentation";

    [Fact]
    public void ThePageLimitIsUnlimitedByDefault()
    {
        // A default that rejects real documents would be worse than the risk it mitigates: the
        // ceiling only applies once a caller opts in.
        Assert.Null(new SecurityLimits().MaxPages);
        DocumentLimits.EnforcePageCount(1_000_000, new SecurityLimits());
    }

    [Fact]
    public void ADocumentExactlyAtTheCeilingIsWithinIt()
    {
        DocumentLimits.EnforcePageCount(5, new SecurityLimits { MaxPages = 5 });
        var ex = Assert.Throws<SecurityException>(
            () => DocumentLimits.EnforcePageCount(6, new SecurityLimits { MaxPages = 5 }));
        Assert.Equal(SecurityViolation.TooManyPages, ex.Violation);
    }

    [Fact]
    public void APresentationOverTheSlideCeilingIsRejected()
    {
        var config = new ExtractionConfig { SecurityLimits = new SecurityLimits { MaxPages = 2 } };
        var ex = Assert.Throws<SecurityException>(
            () => new PptxExtractor().Extract(PptxWithSlides(3), PptxMime, config));
        Assert.Equal(SecurityViolation.TooManyPages, ex.Violation);
    }

    [Fact]
    public void APresentationWithinTheSlideCeilingStillExtracts()
    {
        var config = new ExtractionConfig { SecurityLimits = new SecurityLimits { MaxPages = 3 } };
        var doc = new PptxExtractor().Extract(PptxWithSlides(3), PptxMime, config);
        Assert.NotEmpty(doc.Elements);
    }

    [Fact]
    public void AnOdpOverTheSlideCeilingIsRejected()
    {
        var config = new ExtractionConfig { SecurityLimits = new SecurityLimits { MaxPages = 2 } };
        var ex = Assert.Throws<SecurityException>(
            () => new OdpExtractor().Extract(OdpWithSlides(3), OdpMime, config));
        Assert.Equal(SecurityViolation.TooManyPages, ex.Violation);
    }

    [Fact]
    public void AnOdpWithinTheSlideCeilingStillExtracts()
    {
        var config = new ExtractionConfig { SecurityLimits = new SecurityLimits { MaxPages = 3 } };
        var doc = new OdpExtractor().Extract(OdpWithSlides(3), OdpMime, config);
        Assert.Equal(3, doc.Elements.Count(e => e.Kind.Tag == Xberg.Types.ElementKindTag.Slide));
    }

    /// <summary>
    /// Upstream <c>fix(pptx,rst): clamp a declared list level</c>. <c>&lt;a:pPr lvl="…"&gt;</c> is
    /// document-controlled text, and the level drives a per-level two-space indent loop:
    /// <c>lvl="4294967294"</c> is several GB of indentation. PowerPoint's own list UI stops at 9
    /// levels, so the cap is 8 — the same ceiling DOCX enforces for <c>w:ilvl</c>.
    /// </summary>
    [Fact]
    public void AnOutOfRangePptxListLevelClampsToEightLevelsOfIndent()
    {
        // A preceding paragraph makes the indent observable: a whole-document trim would otherwise
        // remove exactly the indentation under test.
        byte[] pptx = Zip(
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" " +
                "Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml",
                "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:cSld><p:spTree><p:sp><p:txBody>" +
                "<a:p><a:r><a:t>Intro</a:t></a:r></a:p>" +
                "<a:p><a:pPr lvl=\"4294967294\"><a:buChar char=\"\u2022\"/></a:pPr><a:r><a:t>Bomb</a:t></a:r></a:p>" +
                "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));

        // Asserted on the reader's content string, where the indentation still exists: the
        // extractor's block parser folds it back into a list item's depth.
        string content = PptxReader.Extract(pptx, plain: false, injectPlaceholders: true).Content;

        // Matching the whole line, not just a prefix: a deeper indent satisfies a `Contains` on
        // sixteen spaces too, which would make the bound vacuous.
        Assert.Contains(content.Split('\n'), line => line == new string(' ', 16) + "- Bomb");
    }
}
