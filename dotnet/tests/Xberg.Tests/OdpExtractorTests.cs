using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The OpenDocument Presentation extractor, ported from Rust <c>extractors/odp.rs</c>.
/// </summary>
public class OdpExtractorTests
{
    private const string Mime = "application/vnd.oasis.opendocument.presentation";

    private static byte[] Package(string contentXml, string? stylesXml = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string body)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(body);
            }
            Add("content.xml", contentXml);
            if (stylesXml is not null) Add("styles.xml", stylesXml);
        }
        return buffer.ToArray();
    }

    private const string Namespaces = """
        xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
        xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
        xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
        xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0"
        xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
        """;

    private static InternalDocument Extract(byte[] bytes) =>
        new OdpExtractor().Extract(bytes, Mime, new ExtractionConfig());

    [Fact]
    public void EachDrawPageBecomesASlide()
    {
        var doc = Extract(Package($"""
            <office:document-content {Namespaces}><office:body><office:presentation>
              <draw:page draw:name="Intro">
                <draw:frame><draw:text-box><text:p>First slide body</text:p></draw:text-box></draw:frame>
              </draw:page>
              <draw:page draw:name="Detail">
                <draw:frame><draw:text-box><text:p>Second slide body</text:p></draw:text-box></draw:frame>
              </draw:page>
            </office:presentation></office:body></office:document-content>
            """));

        var slides = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Slide).ToList();
        Assert.Equal(2, slides.Count);
        string plain = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("First slide body", plain);
        Assert.Contains("Second slide body", plain);
    }

    [Fact]
    public void TextOnAShapeIsSlideContent()
    {
        // Text does not only live in a frame's text box — a custom shape, a rectangle or a
        // connector carries its own paragraphs, and a group nests them arbitrarily deep. Only
        // frames and groups are entered from the page itself, so every shape here sits in one.
        var doc = Extract(Package($"""
            <office:document-content {Namespaces}><office:body><office:presentation>
              <draw:page>
                <draw:g><draw:frame><draw:custom-shape><text:p>Shape text</text:p></draw:custom-shape></draw:frame></draw:g>
                <draw:frame><draw:rect><text:p>Rectangle text</text:p></draw:rect></draw:frame>
              </draw:page>
            </office:presentation></office:body></office:document-content>
            """));

        string plain = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Shape text", plain);
        Assert.Contains("Rectangle text", plain);
    }

    [Fact]
    public void SpeakerNotesStaySeparateFromSlideText()
    {
        // Notes are a sibling of the drawing frames. What the presenter read is not what the
        // audience saw, so it must not merge into the slide's own paragraphs.
        var doc = Extract(Package($"""
            <office:document-content {Namespaces}><office:body><office:presentation>
              <draw:page>
                <draw:frame><draw:text-box><text:p>On the slide</text:p></draw:text-box></draw:frame>
                <presentation:notes>
                  <draw:frame><draw:text-box><text:p>Remember the anecdote</text:p></draw:text-box></draw:frame>
                </presentation:notes>
              </draw:page>
            </office:presentation></office:body></office:document-content>
            """));

        var raw = doc.Elements.Where(e => e.Text.Contains("anecdote")).ToList();
        Assert.Single(raw);
        var slideParagraph = doc.Elements.First(e => e.Text.Contains("On the slide"));
        Assert.DoesNotContain("anecdote", slideParagraph.Text);
    }

    [Fact]
    public void AFieldsCachedDisplayTextIsNotContent()
    {
        // A slide master's page-number placeholder stores "<number>" as its last-rendered value.
        // Nobody wrote that, and emitting it puts a literal "<number>" in every presentation.
        var doc = Extract(Package(
            $"""
            <office:document-content {Namespaces}><office:body><office:presentation>
              <draw:page><draw:frame><draw:text-box><text:p>Real text</text:p></draw:text-box></draw:frame></draw:page>
            </office:presentation></office:body></office:document-content>
            """,
            $"""
            <office:document-styles {Namespaces}><office:master-styles>
              <style:master-page style:name="Default">
                <draw:frame><draw:text-box><text:p><text:page-number>&lt;number&gt;</text:page-number></text:p></draw:text-box></draw:frame>
              </style:master-page>
            </office:master-styles></office:document-styles>
            """));

        string plain = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Real text", plain);
        Assert.DoesNotContain("<number>", plain);
    }

    [Fact]
    public void AListIsNumberedWhenItsOwnStyleNumbersIt()
    {
        var doc = Extract(Package($"""
            <office:document-content {Namespaces}>
              <office:automatic-styles>
                <text:list-style style:name="L1"><text:list-level-style-number text:level="1"/></text:list-style>
                <text:list-style style:name="L2"><text:list-level-style-bullet text:level="1"/></text:list-style>
              </office:automatic-styles>
              <office:body><office:presentation><draw:page><draw:frame><draw:text-box>
                <text:list text:style-name="L1"><text:list-item><text:p>Numbered</text:p></text:list-item></text:list>
                <text:list text:style-name="L2"><text:list-item><text:p>Bulleted</text:p></text:list-item></text:list>
              </draw:text-box></draw:frame></draw:page></office:presentation></office:body>
            </office:document-content>
            """));

        var lists = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.ListStart).ToList();
        Assert.Equal(2, lists.Count);
        Assert.True(lists[0].Kind.Ordered);
        Assert.False(lists[1].Kind.Ordered);
    }
}
