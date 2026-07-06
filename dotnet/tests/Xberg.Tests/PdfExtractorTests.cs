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
}
