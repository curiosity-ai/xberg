using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for <see cref="OdtExtractor"/>'s content walk, ported from `extractors/odt.rs`.
/// </summary>
public class OdtExtractorTests
{
    private const string Mime = "application/vnd.oasis.opendocument.text";

    private const string Namespaces = """
        xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
        xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
        xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
        xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
        xmlns:xlink="http://www.w3.org/1999/xlink"
        """;

    private static byte[] Package(string contentXml)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("content.xml").Open(), Encoding.UTF8);
            w.Write(contentXml);
        }
        return buffer.ToArray();
    }

    private static InternalDocument Extract(string body) =>
        new OdtExtractor().Extract(
            Package($"<office:document-content {Namespaces}><office:body><office:text>{body}</office:text></office:body></office:document-content>"),
            Mime,
            new ExtractionConfig());

    /// <summary>
    /// A note's key carries its class: "fn" for a footnote, "en" for an endnote. Both the
    /// reference and the definition are keyed identically, and that prefix is the only thing
    /// telling the two classes apart — no element kind distinguishes them.
    /// </summary>
    [Theory]
    [InlineData("footnote", "fnftn0")]
    [InlineData("endnote", "enftn0")]
    public void ANotesKeyCarriesItsClass(string noteClass, string expectedKey)
    {
        var doc = Extract(
            $"<text:p>Some text with a note.<text:note text:id=\"ftn0\" text:note-class=\"{noteClass}\">" +
            "<text:note-citation>1</text:note-citation>" +
            "<text:note-body><text:p>Note text</text:p></text:note-body>" +
            "</text:note></text:p>");

        var reference = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.FootnoteRef));
        var definition = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.FootnoteDefinition));
        Assert.Equal(expectedKey, reference.Anchor);
        Assert.Equal(expectedKey, definition.Anchor);
    }

    /// <summary>
    /// A paragraph's inline run is walked recursively, so a span inside a span keeps its tail and
    /// a caption inside a `draw:text-box` is picked up here as well as by the caption pass.
    /// </summary>
    [Fact]
    public void NestedSpansKeepTheirTail()
    {
        var doc = Extract("<text:p>a<text:span>b<text:span>c</text:span>d</text:span>e</text:p>");
        var para = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph));
        Assert.Equal("abcde", para.Text);
    }

    /// <summary>
    /// A pagination field caches whatever the authoring application last displayed there; with no
    /// layout pass there is nothing to resolve it to, and the cached value can be the editor's own
    /// placeholder, so it contributes nothing.
    /// </summary>
    [Fact]
    public void APaginationFieldContributesNoText()
    {
        var doc = Extract("<text:p>Page <text:page-number>7</text:page-number> of " +
                          "<text:page-count>9</text:page-count>.</text:p>");
        var para = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph));
        Assert.Equal("Page  of .", para.Text);
    }

    /// <summary>
    /// A captioned figure nests a frame inside a text box inside an outer frame, and the inner
    /// frame owns the image. Walking every `draw:frame` in the paragraph reaches that image twice
    /// — once as a descendant of the outer frame and once of the inner one — so the image whose
    /// nearest enclosing frame is not the one being processed is left to that frame.
    /// </summary>
    [Fact]
    public void ACaptionedFiguresImageIsEmittedOnce()
    {
        var doc = Extract(
            "<text:p><draw:frame draw:name=\"Rahmen1\"><draw:text-box>" +
            "<text:p><draw:frame draw:name=\"Grafik1\">" +
            "<draw:image xlink:href=\"Pictures/one.jpg\"/></draw:frame>Abbildung 1: Image caption</text:p>" +
            "</draw:text-box></draw:frame></text:p>");

        Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Image);
    }
}
