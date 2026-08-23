using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.IWork;
using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The Apple iWork extractors, ported from Rust <c>extractors/iwork/{mod,pages,keynote,numbers}.rs</c>.
/// </summary>
public class IWorkExtractorTests
{
    private const string PagesMime = "application/x-iwork-pages-sffpages";
    private const string KeynoteMime = "application/x-iwork-keynote-sffkey";
    private const string NumbersMime = "application/x-iwork-numbers-sffnumbers";

    /// <summary>An uncompressed (chunk type 0x01) IWA stream wrapping one length-delimited text field.</summary>
    private static byte[] IwaTextFrame(string text)
    {
        var payload = new List<byte> { 0x1A, (byte)Encoding.UTF8.GetByteCount(text) };
        payload.AddRange(Encoding.UTF8.GetBytes(text));
        var frame = new List<byte> { 1, (byte)(payload.Count & 0xff), (byte)((payload.Count >> 8) & 0xff), (byte)((payload.Count >> 16) & 0xff) };
        frame.AddRange(payload);
        return frame.ToArray();
    }

    private static byte[] Package(params (string Name, byte[] Data)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }
        return buffer.ToArray();
    }

    private static ZipArchive Open(byte[] package) =>
        new(new MemoryStream(package, writable: false), ZipArchiveMode.Read);

    // ── IWA container ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DecodesUncompressedIwaChunks()
    {
        Assert.Equal(new byte[] { 0x1A, 0x01, 0x41 }, IwaContainer.DecodeIwaStream(IwaTextFrame("A")));
    }

    [Fact]
    public void RejectsUnknownIwaChunkType()
    {
        Assert.Throws<IwaFormatException>(() => IwaContainer.DecodeIwaStream(new byte[] { 2, 0, 0, 0 }));
    }

    [Fact]
    public void RejectsTrailingIwaFramingBytes()
    {
        Assert.Throws<IwaFormatException>(() => IwaContainer.DecodeIwaStream(new byte[] { 0, 0, 0 }));
    }

    [Fact]
    public void RejectsIwaChunkRunningPastTheEnd()
    {
        Assert.Throws<IwaFormatException>(() => IwaContainer.DecodeIwaStream(new byte[] { 1, 8, 0, 0, 1, 2 }));
    }

    /// <summary>
    /// A Snappy block whose copy tag has an offset smaller than its length encodes a repeated
    /// run, so the decoder must expand it byte by byte rather than block-copying.
    /// </summary>
    [Fact]
    public void DecodesSnappyOverlappingCopy()
    {
        // Preamble 10; literal "ab"; copy length 8 at offset 2.
        byte[] block = { 10, 0x04, (byte)'a', (byte)'b', 0x01 | (4 << 2), 2 };
        Assert.Equal("ababababab", Encoding.ASCII.GetString(Snappy.Decompress(block)));
    }

    [Fact]
    public void DecodesSnappyLongLiteral()
    {
        var text = Encoding.ASCII.GetBytes(new string('x', 100));
        var block = new List<byte> { 100, (60 << 2), 99 };
        block.AddRange(text);
        Assert.Equal(text, Snappy.Decompress(block.ToArray()));
    }

    [Fact]
    public void ExtractsTextFromProtoBasic()
    {
        Assert.Contains(
            IwaContainer.ExtractTextFromProto(IwaContainer.DecodeIwaStream(IwaTextFrame("Hello World from iWork"))),
            s => s.Contains("Hello World"));
    }

    [Fact]
    public void ExtractsTextFromNestedProtoMessages()
    {
        var inner = new List<byte> { 0x1A, 14 };
        inner.AddRange(Encoding.UTF8.GetBytes("Nested Content"));
        var outer = new List<byte> { 0x12, (byte)inner.Count };
        outer.AddRange(inner);

        Assert.Contains(IwaContainer.ExtractTextFromProto(outer.ToArray()), s => s.Contains("Nested Content"));
    }

    /// <summary>A single alphanumeric character is real content — a numeric answer, a unit label.</summary>
    [Fact]
    public void KeepsShortAlphanumericStrings()
    {
        Assert.Equal(new[] { "5" }, IwaContainer.ExtractTextFromProto(new byte[] { 0x1A, 1, (byte)'5' }));
    }

    [Fact]
    public void DropsPurelyNonAlphanumericStrings()
    {
        Assert.Empty(IwaContainer.ExtractTextFromProto(new byte[] { 0x1A, 3, (byte)'-', (byte)'-', (byte)'-' }));
    }

    [Fact]
    public void SkipsBinaryFieldsThatAreNotUtf8()
    {
        var binary = Enumerable.Range(0, 20).Select(i => (byte)(i * 7 + 3)).ToArray();
        var proto = new List<byte> { 0x1A, (byte)binary.Length };
        proto.AddRange(binary);

        Assert.DoesNotContain(IwaContainer.ExtractTextFromProto(proto.ToArray()), s => s.All(char.IsLetter));
    }

    /// <summary>
    /// Adjacent repeats are a wire-format artifact of reading a payload as text and then
    /// rescanning it; a non-adjacent repeat is legitimate content.
    /// </summary>
    [Fact]
    public void DedupCollapsesOnlyAdjacentRepeats()
    {
        Assert.Equal(
            new[] { "Confidential", "Body text", "Confidential" },
            IwaContainer.DedupText(new[] { "Confidential", "Confidential", "Body text", "Confidential" }));
    }

    [Fact]
    public void CollectsOnlyIwaPaths()
    {
        using var archive = Open(Package(
            ("Index/Document.iwa", IwaTextFrame("Body")),
            ("metadata.xml", Encoding.UTF8.GetBytes("<xml/>"))));

        Assert.Equal(new[] { "Index/Document.iwa" }, IwaContainer.CollectIwaPaths(archive));
    }

    [Fact]
    public void ReadsPlistAndDocumentIdentifierMetadata()
    {
        string plist = "<plist>\n<key>author</key>\n<string>Ada</string>\n<key>keywords</key>\n<string>a, b</string>\n</plist>";
        using var archive = Open(Package(
            ("Metadata/Properties.plist", Encoding.UTF8.GetBytes(plist)),
            ("Metadata/DocumentIdentifier", Encoding.UTF8.GetBytes(" ABC-123 \n"))));

        var metadata = IwaContainer.ExtractMetadataFromZip(archive);

        Assert.Equal("ABC-123", metadata.Title);
        Assert.Equal(new[] { "Ada" }, metadata.Authors);
        Assert.Equal(new[] { "a", "b" }, metadata.Keywords);
    }

    // ── Pages ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PagesRendersALoneShortLineAsAHeading()
    {
        var doc = new PagesExtractor().Extract(
            Package(("Index/Document.iwa", IwaTextFrame("Hello World from Pages"))), PagesMime, new ExtractionConfig());

        var element = Assert.Single(doc.Elements);
        Assert.Equal(ElementKindTag.Heading, element.Kind.Tag);
        Assert.Equal("Hello World from Pages", element.Text);
    }

    /// <summary>A body of more than one line leads with a title, not a heading.</summary>
    [Fact]
    public void PagesPromotesTheFirstOfSeveralLinesToTitle()
    {
        var body = IwaTextFrame("Quarterly Report").Concat(IwaTextFrame("the numbers are in.")).ToArray();
        var doc = new PagesExtractor().Extract(
            Package(("Index/Document.iwa", body)), PagesMime, new ExtractionConfig());

        Assert.Equal(ElementKindTag.Title, doc.Elements[0].Kind.Tag);
        Assert.Equal("Quarterly Report", doc.Elements[0].Text);
        Assert.Equal(ElementKindTag.Paragraph, doc.Elements[1].Kind.Tag);
    }

    /// <summary>Text outside the document archives lands under its own "Annotations" heading.</summary>
    [Fact]
    public void PagesSeparatesAnnotationTextFromTheBody()
    {
        var doc = new PagesExtractor().Extract(
            Package(
                ("Index/Document.iwa", IwaTextFrame("Body text")),
                ("Index/AnnotationAuthorStorage.iwa", IwaTextFrame("Reviewer note"))),
            PagesMime, new ExtractionConfig());

        Assert.Collection(
            doc.Elements.Select(e => e.Text),
            text => Assert.Equal("Body text", text),
            text => Assert.Equal("Annotations", text),
            text => Assert.Equal("Reviewer note", text));
    }

    /// <summary>A member that fails to decompress must be named in a warning, not vanish.</summary>
    [Fact]
    public void PagesWarnsWhenADocumentMemberFailsToParse()
    {
        var doc = new PagesExtractor().Extract(
            Package(
                ("Index/Document-1.iwa", IwaTextFrame("Body text")),
                ("Index/Document-2.iwa", new byte[] { 1, 0, 0 })),
            PagesMime, new ExtractionConfig());

        Assert.Equal("Body text", Assert.Single(doc.Elements).Text);
        var warning = Assert.Single(doc.ProcessingWarnings);
        Assert.Equal("iwork", warning.Source);
        Assert.Contains("Index/Document-2.iwa", warning.Message);
    }

    // ── Keynote ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeynoteBuildsOneSlidePerArchive()
    {
        var slide = IwaTextFrame("Title").Concat(IwaTextFrame("Body")).ToArray();
        var doc = new KeynoteExtractor().Extract(
            Package(("Index/Slide-1.iwa", slide)), KeynoteMime, new ExtractionConfig());

        Assert.Equal(ElementKindTag.Slide, doc.Elements[0].Kind.Tag);
        Assert.Equal("Title", doc.Elements[0].Text);
        Assert.Equal(ElementKindTag.Paragraph, doc.Elements[1].Kind.Tag);
        Assert.Equal("Body", doc.Elements[1].Text);
    }

    /// <summary>The same footer on two slides belongs on both, not only the first.</summary>
    [Fact]
    public void KeynoteKeepsTextRepeatedAcrossSlides()
    {
        var doc = new KeynoteExtractor().Extract(
            Package(
                ("Index/Slide-1.iwa", IwaTextFrame("Confidential")),
                ("Index/Slide-2.iwa", IwaTextFrame("Confidential"))),
            KeynoteMime, new ExtractionConfig());

        Assert.Equal(2, doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.Slide));
        Assert.All(doc.Elements, e => Assert.Equal("Confidential", e.Text));
    }

    /// <summary>A master slide is not a slide; its text is additional content.</summary>
    [Fact]
    public void KeynoteTreatsMasterSlidesAsAdditionalContent()
    {
        var doc = new KeynoteExtractor().Extract(
            Package(
                ("Index/Slide-1.iwa", IwaTextFrame("Agenda")),
                ("Index/MasterSlide-1.iwa", IwaTextFrame("Company template"))),
            KeynoteMime, new ExtractionConfig());

        Assert.Collection(
            doc.Elements.Select(e => e.Text),
            text => Assert.Equal("Agenda", text),
            text => Assert.Equal("Additional Content", text),
            text => Assert.Equal("Company template", text));
    }

    [Fact]
    public void KeynoteWarnsWhenASlideMemberFailsToParse()
    {
        var doc = new KeynoteExtractor().Extract(
            Package(
                ("Index/Slide-1.iwa", IwaTextFrame("Body")),
                ("Index/Slide-2.iwa", new byte[] { 1, 0, 0 })),
            KeynoteMime, new ExtractionConfig());

        Assert.Equal("Body", Assert.Single(doc.Elements).Text);
        Assert.Contains("Index/Slide-2.iwa", Assert.Single(doc.ProcessingWarnings).Message);
    }

    // ── Numbers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The schema-aware walk over a real Numbers package, checked against the Rust ground
    /// truth for the same file. The fixture never reaches this extractor through normal
    /// routing (see <see cref="NumbersPackageRoutesByItsFirstArchiveMember"/>), so the MIME
    /// type is supplied directly.
    /// </summary>
    [Fact]
    public void NumbersReproducesTheStructuredTableGroundTruth()
    {
        string? fixturePath = FindFixture("iwork/test.numbers");
        string? groundTruthPath = FindFixture("ground_truth/iwork/numbers_tables.md");
        if (fixturePath is null || groundTruthPath is null) return; // fixture tree not present

        var doc = new NumbersExtractor().Extract(
            File.ReadAllBytes(fixturePath), NumbersMime, new ExtractionConfig());

        Assert.Equal(
            Canonical(File.ReadAllText(groundTruthPath)),
            Canonical(MarkdownRenderer.Render(doc)));
    }

    /// <summary>
    /// A Numbers package names <c>Index/Document.iwa</c> before <c>Index/CalculationEngine.iwa</c>,
    /// and content sniffing reads only a 4 KiB header, so a real spreadsheet is identified as a
    /// Pages document and extracted as one. Upstream behaves the same way; the golden for the
    /// .numbers fixture is Pages output.
    /// </summary>
    [Fact]
    public void NumbersPackageRoutesByItsFirstArchiveMember()
    {
        string? fixturePath = FindFixture("iwork/test.numbers");
        if (fixturePath is null) return; // fixture tree not present

        Assert.Equal(
            PagesMime,
            Mime.ResolveWithContent(NumbersMime, File.ReadAllBytes(fixturePath)));
    }

    /// <summary>The flat fallback groups table archives apart from the rest of the package.</summary>
    [Fact]
    public void NumbersFallsBackToFlatTextWhenNoTableSchemaIsFound()
    {
        var doc = new NumbersExtractor().Extract(
            Package(
                ("Index/Tables/DataList.iwa", IwaTextFrame("Revenue")),
                ("Index/Document.iwa", IwaTextFrame("Untitled"))),
            NumbersMime, new ExtractionConfig());

        Assert.Collection(
            doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading).Select(e => e.Text),
            text => Assert.Equal("Sheet Data", text),
            text => Assert.Equal("Document Info", text));
        Assert.Equal(new[] { "Revenue" }, doc.Tables[0].Cells[0]);
        Assert.Equal(new[] { "Untitled" }, doc.Tables[1].Cells[0]);
    }

    [Fact]
    public void NumbersWarnsAboutNonTableDrawables()
    {
        var warnings = new List<ProcessingWarning>();

        NumbersParser.PushNonTableDrawableWarning(warnings, "Sheet1", new uint[] { 42, 7 });

        var warning = Assert.Single(warnings);
        Assert.Contains("Sheet1", warning.Message);
        Assert.Contains("2 non-table drawable", warning.Message);
        Assert.Contains("42, 7", warning.Message);
    }

    [Fact]
    public void NumbersDoesNotWarnWithoutNonTableDrawables()
    {
        var warnings = new List<ProcessingWarning>();

        NumbersParser.PushNonTableDrawableWarning(warnings, "Sheet1", Array.Empty<uint>());

        Assert.Empty(warnings);
    }

    /// <summary>
    /// A legacy formula or comment is a flag bit with a payload this parser has no schema for,
    /// so its presence is reported rather than guessed at. Repeats collapse to one line.
    /// </summary>
    [Fact]
    public void NumbersWarnsOncePerLegacyFormulaAndComment()
    {
        var warnings = new List<ProcessingWarning>();

        NumbersParser.PushLegacyFormulaCommentWarning(warnings, "Sheet1", hasFormula: true, hasComment: true);
        NumbersParser.PushLegacyFormulaCommentWarning(warnings, "Sheet1", hasFormula: true, hasComment: true);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("legacy-format formula"));
        Assert.Contains(warnings, w => w.Message.Contains("legacy-format comment"));
    }

    /// <summary>Rust's <c>f64</c> Display never switches to exponent notation.</summary>
    [Theory]
    [InlineData(42.0, "42")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(1e21, "1000000000000000000000")]
    [InlineData(1.5e-7, "0.00000015")]
    public void NumbersFormatsScalarsWithoutExponents(double value, string expected)
    {
        Assert.Equal(expected, NumbersParser.FormatScalar(value));
    }

    [Fact]
    public void NumbersFormatsDatesFromTheIworkEpoch()
    {
        Assert.Equal("2001-01-01T00:00:00Z", NumbersParser.FormatIworkDate(0));
        Assert.Equal("2000-12-31T23:59:59Z", NumbersParser.FormatIworkDate(-1));
    }

    /// <summary>
    /// The Rust integration test's <c>canonical_markdown</c>: column padding and separator-rule
    /// width are presentation, so both sides are reduced to one space around each cell before
    /// comparing.
    /// </summary>
    private static string Canonical(string markdown) =>
        string.Join(
            "\n",
            markdown.Replace("\r\n", "\n").Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line =>
                {
                    if (!line.StartsWith('|') || !line.EndsWith('|')) return line;
                    var cells = line.Trim('|').Split('|')
                        .Select(cell => cell.Trim())
                        .Select(cell => cell.Length > 0 && cell.All(c => c == '-') ? "---" : cell);
                    return $"| {string.Join(" | ", cells)} |";
                }));

    private static string? FindFixture(string relative) =>
        new[]
        {
            Path.Combine("/workspace/test_documents", relative),
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents", relative),
        }.FirstOrDefault(File.Exists);
}
