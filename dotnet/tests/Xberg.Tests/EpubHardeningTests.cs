using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// EPUB extraction fixes brought in with the upstream sync
/// (<c>fix(epub): stop dropping chapters, metadata and whole books</c>, upstream #1502/#1486).
/// </summary>
public sealed class EpubHardeningTests
{
    private const string Mime = "application/epub+zip";

    /// <summary>
    /// An EPUB whose OPF is <paramref name="opfBody"/> (metadata + manifest + spine), with each
    /// named chapter file written verbatim.
    /// </summary>
    private static byte[] Package(string opfBody, params (string Name, string Body)[] files)
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
            Add("EPUB/package.opf", opfBody);
            foreach (var (name, body) in files) Add("EPUB/" + name, body);
        }
        return buffer.ToArray();
    }

    private static string Opf(string metadata, string manifest, string spine) =>
        "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">" +
        "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">" +
        metadata + "</metadata><manifest>" + manifest + "</manifest><spine>" + spine + "</spine></package>";

    private static string Chapter(string body) =>
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>C</title></head><body>" + body + "</body></html>";

    private static InternalDocument Extract(byte[] epub) =>
        new EpubExtractor().Extract(epub, Mime, new ExtractionConfig());

    /// <summary>
    /// <c>text/html</c> is not an EPUB core media type, but Internet Archive builds declare every
    /// page with it while the payload is XHTML. An exact match against
    /// <c>application/xhtml+xml</c> alone extracted those books with zero content (upstream #1486).
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    [InlineData("application/xhtml+xml; charset=utf-8")]
    [InlineData("  Application/XHTML+XML  ")]
    public void ASpineItemLabelledWithAnyXhtmlishMediaTypeIsStillRead(string mediaType)
    {
        byte[] epub = Package(
            Opf("<dc:title>T</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                $"<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"{mediaType}\"/>",
                "<itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        Assert.Contains(Extract(epub).Elements, e => e.Text.Contains("Chapter text."));
    }

    /// <summary>An empty media-type attribute is treated like a missing one: the extension
    /// decides.</summary>
    [Fact]
    public void AnEmptyMediaTypeFallsBackToTheFileExtension()
    {
        byte[] epub = Package(
            Opf("<dc:title>T</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                "<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"\"/>",
                "<itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        Assert.Contains(Extract(epub).Elements, e => e.Text.Contains("Chapter text."));
    }

    /// <summary>
    /// Every Dublin Core arm used to overwrite the previous value, so a subtitle replaced the
    /// title, an illustrator replaced the author, and a modification date replaced the publication
    /// date.
    /// </summary>
    [Fact]
    public void EveryCreatorAndSubjectIsKeptAndTheFirstTitleWins()
    {
        byte[] epub = Package(
            Opf("<dc:title>The Book</dc:title><dc:title>A Subtitle</dc:title>" +
                "<dc:creator>Ada Lovelace</dc:creator><dc:creator>Charles Babbage</dc:creator>" +
                "<dc:subject>Computing</dc:subject><dc:subject>History</dc:subject>" +
                "<dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                "<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        var meta = Extract(epub).Metadata;
        Assert.Equal("The Book", meta.Title);
        Assert.Equal(new[] { "Ada Lovelace", "Charles Babbage" }, meta.Authors);
        Assert.Equal(new[] { "Computing", "History" }, meta.Keywords);
    }

    /// <summary>EPUB 3 marks the main title with a refining meta element; it wins over the first
    /// one written.</summary>
    [Fact]
    public void ARefiningMetaNamesTheMainTitle()
    {
        byte[] epub = Package(
            Opf("<dc:title id=\"t2\">The Real Title</dc:title><dc:title id=\"t1\">Series Name</dc:title>" +
                "<meta refines=\"#t2\" property=\"title-type\">main</meta>" +
                "<dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                "<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        Assert.Equal("The Real Title", Extract(epub).Metadata.Title);
    }

    /// <summary>A modification date is used only when no other date exists.</summary>
    [Fact]
    public void APublicationDateBeatsAModificationDate()
    {
        byte[] epub = Package(
            Opf("<dc:title>T</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>" +
                "<dc:date opf:event=\"modification\">2024-06-01</dc:date>" +
                "<dc:date opf:event=\"publication\">1843-10-01</dc:date>",
                "<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        Assert.Equal("1843-10-01", Extract(epub).Metadata.CreatedAt);
    }

    /// <summary>
    /// A bare <c>&lt;title&gt;</c> inside an EPUB 3 <c>&lt;collection&gt;</c> describes the
    /// collection, not the book, so matching on the Dublin Core namespace keeps it from
    /// overwriting the book title.
    /// </summary>
    [Fact]
    public void ACollectionTitleDoesNotOverwriteTheBookTitle()
    {
        byte[] epub = Package(
            Opf("<dc:title>The Book</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>" +
                "<collection role=\"series\"><dc:title>A Series</dc:title></collection>",
                "<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        Assert.Equal("The Book", Extract(epub).Metadata.Title);
    }

    /// <summary>
    /// A page with no text but with image markup is kept, so covers, plates and fixed-layout
    /// pages reach the image pipeline instead of being dropped.
    /// </summary>
    [Fact]
    public void AnImageOnlyPageIsKept()
    {
        byte[] epub = Package(
            Opf("<dc:title>T</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                "<item id=\"c1\" href=\"cover.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"c2\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"c1\"/><itemref idref=\"c2\"/>"),
            ("cover.xhtml", Chapter("<img src=\"cover.jpg\" alt=\"Cover plate\"/>")),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        var doc = Extract(epub);
        Assert.Contains(doc.Elements, e => e.Text.Contains("Cover plate") || e.Kind.Tag == ElementKindTag.Image);
    }

    /// <summary>
    /// One spine item whose href escapes the package root used to fail the whole book. It is
    /// skipped with a warning now, and the remaining chapters still extract.
    /// </summary>
    [Fact]
    public void AnUnsafeHrefSkipsOneItemRatherThanFailingTheBook()
    {
        byte[] epub = Package(
            Opf("<dc:title>T</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                "<item id=\"bad\" href=\"../../outside.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"c1\" href=\"chap1.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"bad\"/><itemref idref=\"c1\"/>"),
            ("chap1.xhtml", Chapter("<p>Chapter text.</p>")));

        var doc = Extract(epub);
        Assert.Contains(doc.Elements, e => e.Text.Contains("Chapter text."));
        Assert.Contains(doc.ProcessingWarnings, w => w.Message.Contains("outside.xhtml"));
    }

    /// <summary>
    /// The navigation heuristic ran on every spine item and dropped any chapter with two links,
    /// two list items and at most one paragraph: bibliographies, endnotes and licence pages. Only
    /// items the package itself names as navigation are checked against it now.
    /// </summary>
    [Fact]
    public void AShortLinkHeavyChapterIsKeptWhenThePackageDoesNotCallItNavigation()
    {
        byte[] epub = Package(
            Opf("<dc:title>T</dc:title><dc:identifier id=\"id\">urn:uuid:1</dc:identifier>",
                "<item id=\"c1\" href=\"biblio.xhtml\" media-type=\"application/xhtml+xml\"/>",
                "<itemref idref=\"c1\"/>"),
            ("biblio.xhtml", Chapter(
                "<p>Further reading.</p><ul>" +
                "<li><a href=\"a.xhtml\">A Treatise</a></li>" +
                "<li><a href=\"b.xhtml\">B Treatise</a></li></ul>")));

        Assert.Contains(Extract(epub).Elements, e => e.Text.Contains("A Treatise"));
    }
}
