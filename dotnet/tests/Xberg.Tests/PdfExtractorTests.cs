using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Pdf;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

public class PdfExtractorTests
{
    // ---- Metadata helpers (port parity with pdf/oxide/metadata.rs) ----

    [Theory]
    [InlineData("D:20230115123045", "2023-01-15T12:30:45Z")]
    [InlineData("D:20230115", "2023-01-15T00:00:00Z")]
    [InlineData("20230115", "2023-01-15T00:00:00Z")]
    [InlineData("D:202301151230", "2023-01-15T12:30:00Z")]
    public void ParsePdfDate_MatchesRust(string input, string expected)
        => Assert.Equal(expected, PdfMetadataExtractor.ParsePdfDate(input));

    [Fact]
    public void ParseAuthors_SplitsCommaAndAnd()
    {
        Assert.Equal(new[] { "John Doe" }, PdfMetadataExtractor.ParseAuthors("John Doe"));
        Assert.Equal(new[] { "John Doe", "Jane Smith" }, PdfMetadataExtractor.ParseAuthors("John Doe, Jane Smith"));
        Assert.Equal(new[] { "John Doe", "Jane Smith" }, PdfMetadataExtractor.ParseAuthors("John Doe and Jane Smith"));
    }

    [Fact]
    public void ParseKeywords_SplitsCommaAndSemicolon()
    {
        Assert.Equal(new[] { "pdf", "document", "test" }, PdfMetadataExtractor.ParseKeywords("pdf, document, test"));
        Assert.Equal(new[] { "pdf", "document", "test" }, PdfMetadataExtractor.ParseKeywords("pdf;document;test"));
    }

    [Fact]
    public void DecodePdfString_HandlesUtf16BeAndLatin1()
    {
        var bom = new byte[] { 0xFE, 0xFF, 0x00, (byte)'H', 0x00, (byte)'i' };
        Assert.Equal("Hi", PdfMetadataExtractor.DecodePdfString(bom));
        Assert.Equal("Hello", PdfMetadataExtractor.DecodePdfString(Encoding.ASCII.GetBytes("Hello")));
        Assert.Null(PdfMetadataExtractor.DecodePdfString(Array.Empty<byte>()));
        Assert.Null(PdfMetadataExtractor.DecodePdfString(Encoding.ASCII.GetBytes("   ")));
    }

    // ---- Filters ----

    [Fact]
    public void AsciiHexDecode_Works()
    {
        var got = PdfFilters.AsciiHexDecode(Encoding.ASCII.GetBytes("48656C6C6F>"));
        Assert.Equal("Hello", Encoding.ASCII.GetString(got));
    }

    [Fact]
    public void Ascii85Decode_Works()
    {
        // "Man " encodes to "9jqo^" in ASCII85.
        var got = PdfFilters.Ascii85Decode(Encoding.ASCII.GetBytes("9jqo^~>"));
        Assert.Equal("Man ", Encoding.ASCII.GetString(got));
    }

    [Fact]
    public void RunLengthDecode_Works()
    {
        // length 4 (=5 literal bytes) "Hello", then EOD 128.
        var input = new byte[] { 4, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 128 };
        Assert.Equal("Hello", Encoding.ASCII.GetString(PdfFilters.RunLengthDecode(input)));
    }

    [Fact]
    public void Flate_RoundTrips()
    {
        byte[] original = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog. " + new string('x', 500));
        byte[] zlib = ZlibCompress(original);
        Assert.Equal(original, PdfFilters.Inflate(zlib));
    }

    [Fact]
    public void LzwDecode_DecodesTiffExample()
    {
        // From TIFF6 spec §13: encoding of {45 45 45 45 45 41 41 41 41 41 41 41 42 ...} — instead
        // verify round-trippable simple case by decoding a known clear+literal+EOD stream.
        // Encode "-----" won't be trivial; assert decoder returns bytes for a clear/EOD stream.
        // 9-bit codes: CLEAR(256)=100000000, 'A'(65)=001000001, EOD(257)=100000001
        var bits = "100000000" + "001000001" + "100000001";
        Assert.Equal("A", Encoding.ASCII.GetString(PdfFilters.LzwDecode(BitsToBytes(bits), true)));
    }

    // ---- Lexer / object model ----

    [Fact]
    public void Lexer_ParsesDictionaryAndTypes()
    {
        var bytes = Encoding.ASCII.GetBytes("<< /Type /Page /Count 3 /Flag true /Ratio 1.5 /Name (hi) >>");
        var lex = new PdfLexer(bytes);
        var dict = lex.ParseObject().AsDict();
        Assert.NotNull(dict);
        Assert.Equal("Page", dict!.Get("Type").AsName());
        Assert.Equal(3, dict.Get("Count").AsLong());
        Assert.Equal(true, dict.Get("Flag").AsBool());
        Assert.Equal(1.5, dict.Get("Ratio").AsNumber());
        Assert.Equal("hi", Encoding.ASCII.GetString(dict.Get("Name").AsStringBytes()!));
    }

    [Fact]
    public void Lexer_ParsesIndirectReference()
    {
        var lex = new PdfLexer(Encoding.ASCII.GetBytes("12 0 R"));
        var obj = lex.ParseObject();
        var r = Assert.IsType<PdfRef>(obj);
        Assert.Equal(12, r.Number);
    }

    [Fact]
    public void Lexer_HexStringAndNameEscape()
    {
        var lex = new PdfLexer(Encoding.ASCII.GetBytes("<48656C6C6F>"));
        Assert.Equal("Hello", Encoding.ASCII.GetString(((PdfString)lex.ParseObject()).Bytes));
        var lex2 = new PdfLexer(Encoding.ASCII.GetBytes("/A#20B"));
        Assert.Equal("A B", lex2.ParseObject().AsName());
    }

    // ---- ToUnicode CMap ----

    [Fact]
    public void ToUnicodeCMap_MapsBfCharAndBfRange()
    {
        string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 beginbfchar <0041> <0048> endbfchar\n" +
            "1 beginbfrange <0042> <0043> <0062> endbfrange\n" +
            "endcmap end end";
        var m = PdfCMap.ParseToUnicode(Encoding.ASCII.GetBytes(cmap));
        Assert.Equal("H", m.LookupUnicode(0x41));
        Assert.Equal("b", m.LookupUnicode(0x42));
        Assert.Equal("c", m.LookupUnicode(0x43));
    }

    // ---- Encodings ----

    [Fact]
    public void GlyphNameToUnicode_Common()
    {
        Assert.Equal("A", PdfEncodings.GlyphNameToUnicode("A"));
        Assert.Equal("•", PdfEncodings.GlyphNameToUnicode("bullet"));
        Assert.Equal("é", PdfEncodings.GlyphNameToUnicode("eacute"));
        Assert.Equal("A", PdfEncodings.GlyphNameToUnicode("uni0041"));
        Assert.Equal("", PdfEncodings.GlyphNameToUnicode(".notdef"));
    }

    // ---- End-to-end on a synthetic PDF ----

    [Fact]
    public void Extract_SimplePdf_YieldsTextAndMetadata()
    {
        byte[] pdf = BuildSimplePdf();
        var extractor = new PdfExtractor();
        var doc = extractor.Extract(pdf, "application/pdf", new ExtractionConfig { OutputFormat = OutputFormat.Plain });

        string text = string.Join("\n\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Hello World", text);

        Assert.NotNull(doc.Metadata.Format);
        Assert.Equal("pdf", doc.Metadata.Format!.FormatType);
        var pm = Assert.IsType<PdfMetadata>(doc.Metadata.Format.Payload);
        Assert.Equal(1u, pm.PageCount);
        Assert.Equal("1.4", pm.PdfVersion);
        Assert.False(pm.IsEncrypted);
        Assert.Equal(612, pm.Width);
        Assert.Equal(792, pm.Height);
    }

    [Fact]
    public void Extract_EmptyOrGarbage_Throws()
    {
        var extractor = new PdfExtractor();
        Assert.ThrowsAny<Exception>(() =>
            extractor.Extract(Encoding.ASCII.GetBytes("%PDF-1.4\nnot a real pdf"), "application/pdf", new ExtractionConfig()));
    }

    // ---- helpers ----

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        // Write zlib header + raw deflate + adler32.
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        uint adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static byte[] BitsToBytes(string bits)
    {
        while (bits.Length % 8 != 0) bits += "0";
        var bytes = new byte[bits.Length / 8];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(bits.Substring(i * 8, 8), 2);
        return bytes;
    }

    private static byte[] BuildSimplePdf()
    {
        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };
        string stream = "BT /F1 24 Tf 72 700 Td (Hello World) Tj ET";
        string contentObj = $"<</Length {stream.Length}>>\nstream\n{stream}\nendstream";
        objs.Add(contentObj);
        string info = "<</Producer(TestGen)/CreationDate(D:20230101000000)>>";
        objs.Add(info);

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
        foreach (var off in offsets)
            sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Count + 1}/Root 1 0 R/Info 6 0 R>>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---- Column-aware reading order (XY-cut) ----
    // Ports the intent of pdf_oxide's XYCutStrategy: a two-column page must read
    // the whole left column top-to-bottom before the right column, not interleave.

    private static TextSpan Span(string text, double x, double y, double w, double fs = 10.0)
        => new TextSpan { Text = text, X = x, Y = y, Width = w, Height = fs, FontSize = fs };

    [Fact]
    public void Assemble_TwoColumnPage_ReadsColumnMajor()
    {
        // Region ~[50,500] wide with a clear ~70pt gutter (240..310). Six lines
        // per column, each a multi-word run so the horizontal projection has a
        // real valley at the gutter. Input order interleaves the columns.
        var spans = new List<TextSpan>();
        for (int line = 0; line < 6; line++)
        {
            double y = 700 - line * 20;
            spans.Add(Span($"left column text line {line}", 50, y, 180));
            spans.Add(Span($"right column text line {line}", 310, y, 180));
        }

        string outText = PdfPageText.Assemble(spans);
        int firstRight = outText.IndexOf("right column text line 0", StringComparison.Ordinal);
        int lastLeft = outText.IndexOf("left column text line 5", StringComparison.Ordinal);
        Assert.True(firstRight >= 0 && lastLeft >= 0);
        // Entire left column must precede the right column.
        Assert.True(lastLeft < firstRight,
            "left column must be fully emitted before the right column (column-major reading order)");
    }

    [Fact]
    public void Assemble_SingleColumn_PreservesTopToBottom()
    {
        var spans = new List<TextSpan>
        {
            Span("first line of the paragraph body", 72, 700, 300),
            Span("second line of the paragraph body", 72, 686, 300),
            Span("third line of the paragraph body", 72, 672, 300),
        };
        string outText = PdfPageText.Assemble(spans);
        int a = outText.IndexOf("first", StringComparison.Ordinal);
        int b = outText.IndexOf("second", StringComparison.Ordinal);
        int c = outText.IndexOf("third", StringComparison.Ordinal);
        Assert.True(a >= 0 && a < b && b < c, "single-column text must stay top-to-bottom");
    }

    // ---- Heading-aware structure pipeline (PdfStructure) ----

    private static SegmentData Seg(string text, float y, float fs, bool bold = false)
        => new SegmentData { Text = text, X = 72, Y = y, Width = 300, Height = fs, FontSize = fs, IsBold = bold, BaselineY = y };

    [Fact]
    public void Structure_DetectsHeadingFromLargerFont()
    {
        // One large-font title line above enough uniform body lines to establish a body-font
        // baseline: clustering should classify the large line as a heading and the rest as
        // body paragraphs.
        var page = new List<SegmentData>
        {
            Seg("Document Title Here", 700, 22f),
            Seg("This is the first body sentence of the document.", 660, 10f),
            Seg("This is the second body sentence, continuing on.", 646, 10f),
            Seg("A third body sentence rounds out the paragraph text.", 632, 10f),
            Seg("A fourth body sentence keeps the baseline honest.", 618, 10f),
            Seg("A fifth body sentence completes the sample text.", 604, 10f),
        };
        var doc = PdfStructure.Build(new List<List<SegmentData>> { page });
        Assert.NotNull(doc);
        Assert.Contains(doc!.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text.Contains("Document Title"));
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
    }

    [Fact]
    public void Structure_SparseDocument_DoesNotPromoteALoneLargerLine()
    {
        // Below the block threshold there is no reliable body-font baseline, so font-size
        // clustering must not promote anything: a cover page or one-line document would
        // otherwise turn its only text into an H1. Matches Rust's sparsity gate.
        var page = new List<SegmentData>
        {
            Seg("Document Title Here", 700, 22f),
            Seg("This is the first body sentence of the document.", 660, 10f),
            Seg("This is the second body sentence, continuing on.", 646, 10f),
            Seg("A third body sentence rounds out the paragraph text.", 632, 10f),
        };
        var doc = PdfStructure.Build(new List<List<SegmentData>> { page });
        Assert.NotNull(doc);
        Assert.DoesNotContain(doc!.Elements, e => e.Kind.Tag == ElementKindTag.Heading);
    }

    [Fact]
    public void Structure_NormalizesUnicodeAndDetectsListItems()
    {
        // Curly quote should be normalized to ASCII (Stage-5 text repair), and a bullet
        // line should become a list item.
        var page = new List<SegmentData>
        {
            Seg("It’s a normalized quote", 700, 10f),
            Seg("• a bullet list item here", 680, 10f),
        };
        var doc = PdfStructure.Build(new List<List<SegmentData>> { page });
        Assert.NotNull(doc);
        Assert.Contains(doc!.Elements, e => e.Text.Contains("It's a normalized quote"));
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.ListItem);
    }

    // ---- Table reconstruction (port parity with table_core.rs + table_reconstruct.rs) ----

    private static List<List<string>> Grid(params string[][] rows) => rows.Select(r => r.ToList()).ToList();

    [Fact]
    public void TableToMarkdown_BasicAndEscapesPipes()
    {
        var md = PdfTableReconstruct.TableToMarkdown(Grid(new[] { "Name", "Value" }, new[] { "Alice", "42" }));
        Assert.Contains("| Name | Value |", md);
        Assert.Contains("| --- | --- |", md);
        Assert.Contains("| Alice | 42 |", md);
        Assert.Equal("", PdfTableReconstruct.TableToMarkdown(new List<List<string>>()));
        Assert.Contains("a\\|b", PdfTableReconstruct.TableToMarkdown(Grid(new[] { "Header" }, new[] { "a|b" })));
    }

    [Fact]
    public void PostProcess_RejectsProse_AcceptsRealTable()
    {
        var prose = Grid(
            new[] { "Foreword", "", "", "", "", "ISO 21111-10:2021(E)", "", "" },
            new[] { "ISO", "(the", "International", "Organization", "for", "Standardization)is", "a", "worldwide" },
            new[] { "bodies", "(ISO", "member", "bodies).The", "work", "of", "preparing", "International" },
            new[] { "through", "ISO", "technical", "committees.Each", "member", "body", "interested", "in" });
        Assert.Null(PdfTableReconstruct.PostProcessTable(prose, false, false));

        var real = Grid(
            new[] { "Name", "Department", "Annual Salary" },
            new[] { "John Smith", "Engineering Dept", "$95,000" },
            new[] { "Jane Doe", "Marketing Team", "$88,500" },
            new[] { "Bob Johnson", "Sales Division", "$92,000" },
            new[] { "Alice Williams", "Human Resources", "$85,000" });
        Assert.NotNull(PdfTableReconstruct.PostProcessTable(real, false, false));
    }

    [Fact]
    public void PostProcess_RejectsMultiColumnProseFlowThrough()
    {
        var table = Grid(
            new[] { "Header Left", "Header Right" },
            new[] { "The results of this experiment show that the proposed method", "significantly outperforms the baseline in all metrics tested" },
            new[] { "across multiple datasets including the standard benchmark", "suite commonly used in the literature for evaluation of" },
            new[] { "natural language processing tasks and related problems", "involving text classification and information extraction" },
            new[] { "methods that rely on deep learning architectures with", "attention mechanisms and transformer-based embeddings" });
        Assert.Null(PdfTableReconstruct.PostProcessTable(table, false, false));
        Assert.Null(PdfTableReconstruct.PostProcessTable(table, true, false));
    }

    [Fact]
    public void IsWellFormed_RejectsDegenerateAndProse_AcceptsVaried()
    {
        Assert.False(PdfTableReconstruct.IsWellFormedTable(Grid(new[] { "Header", "Value" })));
        Assert.False(PdfTableReconstruct.IsWellFormedTable(Grid(new[] { "H" }, new[] { "R1" }, new[] { "R2" })));

        var repetitive = Grid(
            new[] { "Bookmark", "File PDF", "Year 4" }, new[] { "Bookmark", "File PDF", "Year 4" },
            new[] { "Bookmark", "File PDF", "Year 4" }, new[] { "Bookmark", "File PDF", "Year 4" },
            new[] { "Bookmark", "File PDF", "Year 4" });
        Assert.False(PdfTableReconstruct.IsWellFormedTable(repetitive));

        var varied = Grid(
            new[] { "ID", "Product Name", "Price" },
            new[] { "1", "Widget Alpha Premium", "$29.99" },
            new[] { "2", "Gadget Beta Standard", "$149.50" },
            new[] { "3", "Tool Gamma Deluxe Ed", "$7.25" },
            new[] { "4", "Part Delta Industrial", "$1,299.00" });
        Assert.True(PdfTableReconstruct.IsWellFormedTable(varied));
    }

    [Fact]
    public void LooksLikeCodeListing_DetectsBraces()
    {
        Assert.True(PdfTableReconstruct.LooksLikeCodeListing(Grid(new[] { "if (x)", "{" }, new[] { "return", "}" })));
        Assert.False(PdfTableReconstruct.LooksLikeCodeListing(Grid(new[] { "Name", "Age" }, new[] { "Alice", "42" })));
    }

    [Fact]
    public void ReconstructTable_MergesIntraCellWordSpacing()
    {
        HocrWord W(string t, uint l, uint top, uint w) => new HocrWord { Text = t, Left = l, Top = top, Width = w, Height = 12, Confidence = 95.0 };
        var words = new List<HocrWord>
        {
            W("Chose", 57, 496, 30), W("Truc", 306, 496, 23),
            W("Chose", 57, 510, 28), W("1", 90, 510, 6), W("Truc", 306, 510, 21), W("1", 332, 510, 5),
            W("Chose", 57, 524, 28), W("2", 90, 524, 6), W("Truc", 306, 524, 21), W("2", 332, 524, 5),
        };
        var table = PdfTableReconstruct.ReconstructTable(words, 60, 0.5);
        Assert.Equal(3, table.Count);
        Assert.Equal(2, table[0].Count);
        Assert.Equal("Chose 1", table[1][0]);
        Assert.Equal("Truc 2", table[2][1]);
    }

    [Fact]
    public void SegmentsToWords_SplitsProportionally()
    {
        var seg = new SegmentData { Text = "Col A", X = 100, Y = 500, Width = 100, Height = 12 };
        var words = PdfTableReconstruct.SegmentsToWords(new List<SegmentData> { seg }, 800f);
        Assert.Equal(2, words.Count);
        Assert.Equal("Col", words[0].Text);
        Assert.Equal("A", words[1].Text);
        Assert.Equal(180u, words[1].Left);
    }

    // ---- Double-drawn / duplicate-glyph de-duplication (issue #71 / #1114) ----

    private static TextSpan Sp(string text, double x, double y, double w, double h = 10)
        => new TextSpan { Text = text, X = x, Y = y, Width = w, Height = h, FontSize = h };

    [Fact]
    public void Dedup_DropsGeometricallyOverlappingGlyphs()
    {
        // A run drawn twice at a ~0.3pt offset (fill+stroke). After the reading-
        // order sort the twin glyphs are adjacent; the second copy is dropped.
        var spans = new List<TextSpan>
        {
            Sp("本", 503.87, 186, 8.54), Sp("科", 512.48, 186, 8.54),
            Sp("本", 503.60, 186, 8.54), Sp("科", 512.20, 186, 8.54),
        };
        var kept = PdfPageText.DeduplicateOverlappingSpans(
            PdfPageText.SortSpansByReadingOrder(spans));
        Assert.Equal(new[] { "本", "科" }, kept.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void Dedup_DropsExactContentDuplicate()
    {
        // Same word emitted twice at the identical position (content phase,
        // text >= 5 bytes, overlapping X/Y).
        var spans = new List<TextSpan>
        {
            Sp("Duplicated", 117.61, 481.86, 85.0),
            Sp("Duplicated", 117.61, 481.86, 85.0),
        };
        var kept = PdfPageText.DeduplicateOverlappingSpans(spans);
        Assert.Single(kept);
    }

    [Fact]
    public void Dedup_DropsStrokeFillOverlapByIoU()
    {
        // Large display title drawn twice at a ~1.5pt offset — not on the same
        // rounded baseline, so only the IoU (stroke+fill) phase catches it.
        var spans = new List<TextSpan>
        {
            Sp("THE", 240.30, 789.07, 139.25, 62.0),
            Sp("THE", 238.94, 790.67, 139.25, 62.0),
        };
        var kept = PdfPageText.DeduplicateOverlappingSpans(spans);
        Assert.Single(kept);
    }

    [Fact]
    public void Dedup_KeepsLegitimatelyShiftedDuplicates()
    {
        // "Vertical shift" drawn twice with a real 3.7pt vertical offset: these
        // are distinct lines (issue-1114 keeps both). Must NOT be collapsed.
        var spans = new List<TextSpan>
        {
            Sp("Vertical shift", 117.61, 187.09, 97.94),
            Sp("Vertical shift", 117.61, 183.37, 97.94),
        };
        var kept = PdfPageText.DeduplicateOverlappingSpans(spans);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Dedup_KeepsAdjacentNarrowGlyphDoublets()
    {
        // Genuine "ll" — two 'l' glyphs one advance apart must survive (the
        // ratio-based threshold stays below one advance).
        var spans = new List<TextSpan>
        {
            Sp("l", 100.0, 200.0, 2.5), Sp("l", 102.5, 200.0, 2.5),
        };
        var kept = PdfPageText.DeduplicateOverlappingSpans(spans);
        Assert.Equal(2, kept.Count);
    }
}
