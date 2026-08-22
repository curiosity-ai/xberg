using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for <see cref="EpubExtractor"/>'s spine walk, ported from `extractors/epub/`.
/// </summary>
public class EpubExtractorTests
{
    private const string Mime = "application/epub+zip";

    /// <summary>A one-chapter EPUB whose spine holds <paramref name="chapterBody"/>.</summary>
    private static byte[] Package(string chapterBody)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string body)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(body);
            }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml",
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles>" +
                "<rootfile full-path=\"EPUB/package.opf\" media-type=\"application/oebps-package+xml\"/>" +
                "</rootfiles></container>");
            Add("EPUB/package.opf",
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>T</dc:title>" +
                "<dc:identifier id=\"id\">urn:uuid:1</dc:identifier></metadata>" +
                "<manifest><item id=\"c1\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>" +
                "<spine><itemref idref=\"c1\"/></spine></package>");
            Add("EPUB/chap1.xhtml",
                "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>C</title></head><body>" +
                chapterBody + "</body></html>");
        }
        return buffer.ToArray();
    }

    private static InternalDocument Extract(string chapterBody, OutputFormat fmt) =>
        new EpubExtractor().Extract(Package(chapterBody), Mime, new ExtractionConfig { OutputFormat = fmt });

    /// <summary>
    /// Audio and video elements are delivery controls rather than book text: without stripping
    /// them the conversion emits their source URLs and the serialized fallback markup alongside
    /// the prose.
    /// </summary>
    [Fact]
    public void EmbeddedMediaIsStrippedBeforeConversion()
    {
        var doc = Extract(
            "<p>Before.</p><video id=\"v\" controls=\"controls\">" +
            "<source src=\"../video/x.mp4\" type=\"video/mp4\"/>" +
            "<div><p>Your Reading System does not support (this) video.</p></div>" +
            "</video><p>After.</p>",
            OutputFormat.Markdown);

        string text = doc.PreRenderedContent ?? string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Before.", text);
        Assert.Contains("After.", text);
        Assert.DoesNotContain("x.mp4", text);
        Assert.DoesNotContain("Reading System", text);
    }

    /// <summary>
    /// The blockquote container is recorded even though its contents are not inside it: the
    /// structure walker's nodes are flat, so the quote opens and closes at once and the quoted
    /// paragraph follows it as a sibling.
    /// </summary>
    [Fact]
    public void ABlockquoteIsRecordedAsAnEmptyContainer()
    {
        var doc = Extract("<h1>Chapter</h1><blockquote><p>Quoted.</p></blockquote>", OutputFormat.Html);

        int start = doc.Elements.FindIndex(e => e.Kind.Tag == ElementKindTag.QuoteStart);
        int end = doc.Elements.FindIndex(e => e.Kind.Tag == ElementKindTag.QuoteEnd);
        int quoted = doc.Elements.FindIndex(e => e.Text == "Quoted.");
        Assert.True(start >= 0 && end == start + 1 && quoted > end);
    }

    private const string SwitchMarkup =
        "<epub:switch xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
        "<epub:case required-namespace=\"http://www.w3.org/1998/Math/MathML\">" +
        "<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mi>x</mi></math>" +
        "</epub:case>" +
        "<epub:default><p>Fallback.</p></epub:default>" +
        "</epub:switch>";

    /// <summary>
    /// The plain path draws no MathML, so an <c>epub:case</c> asking for that namespace loses to
    /// <c>epub:default</c> — and only the winning branch reaches the document.
    /// </summary>
    [Fact]
    public void ASwitchCaseThePlainRendererCannotDrawSelectsTheDefault()
    {
        var doc = Extract("<p>Before.</p>" + SwitchMarkup, OutputFormat.Plain);

        string text = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Fallback.", text);
        Assert.DoesNotContain("x", text.Replace("Before.", "").Replace("Fallback.", ""));
    }

    /// <summary>The markup renderers do draw MathML, so the case wins and the default is cut.</summary>
    [Fact]
    public void ASwitchCaseTheMarkupRendererCanDrawWinsOverTheDefault()
    {
        var doc = Extract("<p>Before.</p>" + SwitchMarkup, OutputFormat.Markdown);

        string text = doc.PreRenderedContent ?? string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Before.", text);
        Assert.DoesNotContain("Fallback.", text);
    }

    /// <summary>
    /// A case whose required namespace no renderer claims is dropped whichever way the document
    /// is rendered — a switch never emits two branches.
    /// </summary>
    [Fact]
    public void AnUnknownRequiredNamespaceNeverReachesTheDocument()
    {
        var doc = Extract(
            "<epub:switch xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
            "<epub:case required-namespace=\"http://example.invalid/ns\"><p>Unsupported.</p></epub:case>" +
            "<epub:default><p>Supported.</p></epub:default>" +
            "</epub:switch>",
            OutputFormat.Plain);

        string text = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Supported.", text);
        Assert.DoesNotContain("Unsupported.", text);
    }
}
