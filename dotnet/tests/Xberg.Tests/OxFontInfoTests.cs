using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Fonts;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Covers the ported pdf_oxide `fonts/font_dict.rs` loader: the width arrays, the Type0
/// /W forms, /Differences over a base encoding and /CIDToGIDMap in both of its shapes.
/// The sibling seams (glyph names, encoding tables) are stubbed here, so these tests pin
/// the font-dictionary behaviour rather than the tables.
/// </summary>
public sealed class OxFontInfoTests : IDisposable
{
    public OxFontInfoTests()
    {
        OxFontSeams.GlyphNames = new StubGlyphNames();
        OxFontSeams.EncodingTables = new StubEncodingTables();
    }

    public void Dispose()
    {
        OxFontSeams.GlyphNames = null;
        OxFontSeams.EncodingTables = null;
    }

    // ---- simple fonts: /Widths, /FirstChar and the fallback ----

    [Fact]
    public void SimpleFont_WidthsIndexedFromFirstChar()
    {
        var b = new PdfBuilder();
        int font = b.AddObject("<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont"
            + "/FirstChar 65/LastChar 67/Widths[500 600 700]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc);
        Assert.NotNull(f);
        Assert.Equal(500f, f!.GetGlyphWidth(65));
        Assert.Equal(600f, f.GetGlyphWidth(66));
        Assert.Equal(700f, f.GetGlyphWidth(67));
        Assert.True(f.HasExplicitWidths());
    }

    [Fact]
    public void SimpleFont_CodeOutsideWidthsUsesFlagsDefault()
    {
        // No /Flags at all: pdf_oxide's middle-ground 550 rather than a /MissingWidth read —
        // the Rust never looks at /MissingWidth, so a code outside every range lands here.
        var b = new PdfBuilder();
        int font = b.AddObject("<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont"
            + "/FirstChar 65/LastChar 66/Widths[500 600]/MissingWidth 999>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal(550f, f.GetGlyphWidth(64));
        Assert.Equal(550f, f.GetGlyphWidth(200));
    }

    [Fact]
    public void SimpleFont_FixedPitchDescriptorRaisesTheDefaultWidth()
    {
        var b = new PdfBuilder();
        int desc = b.AddObject("<</Type/FontDescriptor/FontName/NotAStandardFont/Flags 1/StemV 120>>");
        int font = b.AddObject($"<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont"
            + $"/FirstChar 65/LastChar 65/Widths[500]/FontDescriptor {desc} 0 R>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal(600f, f.GetGlyphWidth(32)); // monospace default
        Assert.Equal(500f, f.GetGlyphWidth(65));
        // StemV > 110 is the last weight tier in the cascade.
        Assert.Equal(OxFontWeight.Bold, f.GetFontWeight());
    }

    [Fact]
    public void Standard14_WithoutWidthsUsesBuiltInMetrics()
    {
        var b = new PdfBuilder();
        int font = b.AddObject("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal(278f, f.GetGlyphWidth(32));  // space
        Assert.Equal(667f, f.GetGlyphWidth(65));  // 'A'
        Assert.Equal(0.718f, f.Ascent, 5);
        Assert.Equal(-0.207f, f.Descent, 5);
        Assert.False(f.HasExplicitWidths());
    }

    [Fact]
    public void ByteToWidthTable_MatchesPerGlyphLookup()
    {
        var b = new PdfBuilder();
        int font = b.AddObject("<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont"
            + "/FirstChar 65/LastChar 66/Widths[500 600]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        float[] table = f.GetByteToWidthTable();
        Assert.Equal(500f, table[65]);
        Assert.Equal(600f, table[66]);
        Assert.Equal(f.GetGlyphWidth(200), table[200]);
    }

    // ---- Type0: /W in both forms, /DW ----

    [Fact]
    public void Type0_WArrayHandlesBothForms()
    {
        var b = new PdfBuilder();
        int cid = b.AddObject("<</Type/Font/Subtype/CIDFontType2/BaseFont/Sub+CIDFont"
            + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
            + "/DW 850/W[1[500 600 700] 100 200 300]>>");
        int font = b.AddObject($"<</Type/Font/Subtype/Type0/BaseFont/Sub+CIDFont"
            + $"/Encoding/Identity-H/DescendantFonts[{cid} 0 R]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        // c [w1 w2 w3]
        Assert.Equal(500f, f.GetGlyphWidth(1));
        Assert.Equal(600f, f.GetGlyphWidth(2));
        Assert.Equal(700f, f.GetGlyphWidth(3));
        // c_first c_last w
        Assert.Equal(300f, f.GetGlyphWidth(100));
        Assert.Equal(300f, f.GetGlyphWidth(150));
        Assert.Equal(300f, f.GetGlyphWidth(200));
        // Covered by neither: an explicit /DW answers.
        Assert.Equal(850f, f.GetGlyphWidth(5000));
        Assert.True(f.HasExplicitDw);
    }

    [Fact]
    public void Type0_WithoutDwFallsThroughToTheSimpleFontDefault()
    {
        // Table 117 makes a missing /DW mean 1000; pdf_oxide deliberately deviates, because
        // returning 1000 for every uncovered CID over-estimates non-fullwidth CID fonts and
        // disables the gap-correction heuristic downstream.
        var b = new PdfBuilder();
        int cid = b.AddObject("<</Type/Font/Subtype/CIDFontType2/BaseFont/Sub+CIDFont"
            + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
            + "/W[1[500]]>>");
        int font = b.AddObject($"<</Type/Font/Subtype/Type0/BaseFont/Sub+CIDFont"
            + $"/Encoding/Identity-H/DescendantFonts[{cid} 0 R]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.False(f.HasExplicitDw);
        Assert.Equal(500f, f.GetGlyphWidth(1));
        Assert.Equal(550f, f.GetGlyphWidth(9999));
        // Identity-encoded Type0 fonts never trust code 0x20 as the space advance.
        Assert.Equal(250f, f.GetSpaceGlyphWidth());
    }

    [Fact]
    public void Type0_VerticalMetricsComeFromW2AndDw2()
    {
        var b = new PdfBuilder();
        int cid = b.AddObject("<</Type/Font/Subtype/CIDFontType2/BaseFont/Sub+CIDFont"
            + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
            + "/DW2[900 -1100]/W2[1[-880 400 800] 10 12 -900 450 850]>>");
        int font = b.AddObject($"<</Type/Font/Subtype/Type0/BaseFont/Sub+CIDFont"
            + $"/Encoding/Identity-V/DescendantFonts[{cid} 0 R]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal(1, f.Wmode); // Identity-V

        var explicitCid = f.GetVerticalMetrics(1);
        Assert.Equal(-880f, explicitCid.W1y);
        Assert.Equal(400f, explicitCid.Vx);
        Assert.Equal(800f, explicitCid.Vy);

        var ranged = f.GetVerticalMetrics(11);
        Assert.Equal(-900f, ranged.W1y);
        Assert.Equal(450f, ranged.Vx);

        // Uncovered CIDs take /DW2, whose v_x is always the spec's 500.
        var dflt = f.GetVerticalMetrics(9999);
        Assert.Equal(-1100f, dflt.W1y);
        Assert.Equal(500f, dflt.Vx);
        Assert.Equal(900f, dflt.Vy);
    }

    // ---- /CIDToGIDMap ----

    [Fact]
    public void CidToGidMap_IdentityNameMapsCidToItself()
    {
        var b = new PdfBuilder();
        int cid = b.AddObject("<</Type/Font/Subtype/CIDFontType2/BaseFont/Sub+CIDFont"
            + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
            + "/CIDToGIDMap/Identity>>");
        int font = b.AddObject($"<</Type/Font/Subtype/Type0/BaseFont/Sub+CIDFont"
            + $"/Encoding/Identity-H/DescendantFonts[{cid} 0 R]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.NotNull(f.CidToGidMap);
        Assert.Equal(7, f.CidToGidMap!.GetGid(7));
        Assert.Equal("Identity", f.CidSystemInfo!.Ordering);
        Assert.Equal("CIDFontType2", f.CidFontType);
    }

    [Fact]
    public void CidToGidMap_StreamIsReadAsBigEndianUint16()
    {
        // CID 0 → GID 0, CID 1 → GID 5, CID 2 → GID 0x0102.
        byte[] mapData = { 0x00, 0x00, 0x00, 0x05, 0x01, 0x02 };
        var b = new PdfBuilder();
        int map = b.AddStream("", mapData);
        int cid = b.AddObject("<</Type/Font/Subtype/CIDFontType2/BaseFont/Sub+CIDFont"
            + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
            + $"/CIDToGIDMap {map} 0 R>>");
        int font = b.AddObject($"<</Type/Font/Subtype/Type0/BaseFont/Sub+CIDFont"
            + $"/Encoding/Identity-H/DescendantFonts[{cid} 0 R]>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal(5, f.CidToGidMap!.GetGid(1));
        Assert.Equal(0x0102, f.CidToGidMap.GetGid(2));
        // Past the end of the stream the mapping falls back to identity rather than .notdef.
        Assert.Equal(9, f.CidToGidMap.GetGid(9));
    }

    // ---- /Encoding and /Differences ----

    [Fact]
    public void Differences_OverrideTheNamedBaseEncoding()
    {
        var b = new PdfBuilder();
        int enc = b.AddObject("<</Type/Encoding/BaseEncoding/WinAnsiEncoding/Differences[65/period 200/bullet]>>");
        int font = b.AddObject($"<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont/Encoding {enc} 0 R>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.True(f.HasCustomEncoding());
        Assert.Equal(".", f.CharToUnicode(65));            // overridden by /Differences
        Assert.Equal("B", f.CharToUnicode(66));            // still the base encoding
        Assert.Equal("•", f.CharToUnicode(200));      // a code the base leaves alone
        Assert.Equal("period", f.DiffGlyphNames[65]);      // the raw name is retained
        Assert.Equal('.', f.GetEncodedChar(65));
    }

    [Fact]
    public void Differences_CompoundGlyphNameLandsInTheMultiCharMap()
    {
        var b = new PdfBuilder();
        int enc = b.AddObject("<</Type/Encoding/BaseEncoding/WinAnsiEncoding/Differences[70/f_f 200/f_f]>>");
        int font = b.AddObject($"<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont/Encoding {enc} 0 R>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal("ff", f.MultiCharMap[70]);
        // The single-char map wins where the base encoding covers the code, so the compound
        // expansion only surfaces for codes the base leaves unmapped.
        Assert.Equal("F", f.CharToUnicode(70));
        Assert.Equal("ff", f.CharToUnicode(200));
    }

    [Fact]
    public void NamedEncoding_IsKeptAsStandard()
    {
        var b = new PdfBuilder();
        int font = b.AddObject("<</Type/Font/Subtype/TrueType/BaseFont/NotAStandardFont/Encoding/WinAnsiEncoding>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.False(f.HasCustomEncoding());
        Assert.Equal("A", f.CharToUnicode(65));
    }

    // ---- degradation ----

    [Fact]
    public void MalformedDictionaryDegradesToDefaults()
    {
        var b = new PdfBuilder();
        // Type0 with no /DescendantFonts, a /Widths that is not an array and a bad /Encoding.
        int font = b.AddObject("<</Type/Font/Subtype/Type0/BaseFont/Broken/Widths 7/Encoding 42>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc);
        Assert.NotNull(f);
        Assert.Equal("Broken", f!.BaseFont);
        Assert.Equal("Type0", f.Subtype);
        // The descendant parse failed, so CIDToGIDMap falls back to Identity.
        Assert.NotNull(f.CidToGidMap);
        Assert.Equal(3, f.CidToGidMap!.GetGid(3));
        Assert.Equal(550f, f.GetGlyphWidth(65));
        Assert.Equal(OxMappingProvenance.Fallback, f.BestMappingProvenance());
    }

    [Fact]
    public void NonDictionaryObjectIsRejected()
    {
        Assert.Null(OxFontInfo.FromDict(new PdfNumber(7, true), null));
    }

    // ---- name-derived weight / style ----

    [Theory]
    [InlineData("ABCDEF+Arial-BoldMT", OxFontWeight.Bold)]
    [InlineData("Arial-SemiBold", OxFontWeight.SemiBold)]
    [InlineData("Roboto-Black", OxFontWeight.Black)]
    [InlineData("Roboto-Thin", OxFontWeight.Thin)]
    [InlineData("Arial", OxFontWeight.Normal)]
    public void FontWeightFallsBackToTheNameHeuristics(string baseFont, OxFontWeight expected)
    {
        var b = new PdfBuilder();
        int font = b.AddObject($"<</Type/Font/Subtype/TrueType/BaseFont/{baseFont}>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.Equal(expected, f.GetFontWeight());
        Assert.Equal(expected >= OxFontWeight.SemiBold, f.IsBold());
    }

    [Fact]
    public void ItalicAndSymbolicAreReadFromNameAndFlags()
    {
        var b = new PdfBuilder();
        int font = b.AddObject("<</Type/Font/Subtype/Type1/BaseFont/Helvetica-Oblique>>");
        var (doc, dict) = b.Open(font);

        var f = OxFontInfo.FromDict(dict, doc)!;
        Assert.True(f.IsItalic());
        Assert.False(f.IsSymbolic());

        var b2 = new PdfBuilder();
        int desc = b2.AddObject("<</Type/FontDescriptor/FontName/Wingdings/Flags 4>>");
        int font2 = b2.AddObject($"<</Type/Font/Subtype/TrueType/BaseFont/Wingdings/FontDescriptor {desc} 0 R>>");
        var (doc2, dict2) = b2.Open(font2);

        var f2 = OxFontInfo.FromDict(dict2, doc2)!;
        Assert.True(f2.IsSymbolic());
    }

    // ---- free functions ----

    [Fact]
    public void WmodeIsDerivedFromThePredefinedCMapName()
    {
        Assert.Equal(1, OxFontTables.WmodeFromPredefinedCMapName("Identity-V"));
        Assert.Equal(1, OxFontTables.WmodeFromPredefinedCMapName("UniJIS-UTF16-V"));
        Assert.Equal(1, OxFontTables.WmodeFromPredefinedCMapName("V"));
        Assert.Equal(0, OxFontTables.WmodeFromPredefinedCMapName("Identity-H"));
        Assert.Equal(0, OxFontTables.WmodeFromPredefinedCMapName("90ms-RKSJ-H"));
    }

    [Fact]
    public void PdfDocEncodingCoversAsciiTheSpecialBlockAndLatin1()
    {
        Assert.Equal('A', OxFontTables.PdfDocEncodingLookup(0x41));
        Assert.Equal('•', OxFontTables.PdfDocEncodingLookup(0x80)); // bullet
        Assert.Equal('−', OxFontTables.PdfDocEncodingLookup(0x8A)); // minus, not hyphen
        Assert.Null(OxFontTables.PdfDocEncodingLookup(0x9F));
        Assert.Equal('é', OxFontTables.PdfDocEncodingLookup(0xE9));
    }

    [Fact]
    public void GidToStandardGlyphNameCoversTheAsciiRange()
    {
        Assert.Equal("space", OxFontInfo.GidToStandardGlyphName(0x20));
        Assert.Equal("A", OxFontInfo.GidToStandardGlyphName(0x41));
        Assert.Null(OxFontInfo.GidToStandardGlyphName(0xFFFF));
    }

    [Fact]
    public void SymbolAndZapfEncodingsResolveTheirBuiltInGlyphs()
    {
        Assert.Equal('α', OxFontTables.SymbolEncodingLookup(0x61)); // alpha
        Assert.Equal('∫', OxFontTables.SymbolEncodingLookup(0xF2)); // integral
        Assert.Equal('①', OxFontTables.ZapfDingbatsEncodingLookup(0xAC)); // circled one
    }

    // ---- stubs for the sibling seams ----

    private sealed class StubGlyphNames : IOxGlyphNames
    {
        public char? GlyphNameToUnicode(string glyphName) => glyphName switch
        {
            "period" => '.',
            "comma" => ',',
            "bullet" => '•',
            _ => glyphName.Length == 1 ? glyphName[0] : null,
        };

        public string? GlyphNameToUnicodeString(string glyphName) => glyphName switch
        {
            "f_f" => "ff",
            "f_f_i" => "ffi",
            _ => null,
        };

        public string? MapGlyphNameToUnicodeString(string glyphName) => GlyphNameToUnicode(glyphName)?.ToString();

        public char? AdobeGlyphListLookup(string glyphName) => GlyphNameToUnicode(glyphName);
    }

    /// <summary>ASCII-only stand-in for the standard encoding tables.</summary>
    private sealed class StubEncodingTables : IOxEncodingTables
    {
        public string? StandardEncodingLookup(string encoding, byte code) =>
            code >= 32 && code <= 126 ? ((char)code).ToString() : null;
    }

    // ---- minimal PDF writer, so indirect references resolve through PdfDocument ----

    private sealed class PdfBuilder
    {
        private readonly List<byte[]> _objects = new();

        public int AddObject(string body)
        {
            _objects.Add(Encoding.ASCII.GetBytes(body));
            return _objects.Count;
        }

        public int AddStream(string dictEntries, byte[] data)
        {
            var head = Encoding.ASCII.GetBytes($"<<{dictEntries}/Length {data.Length}>>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            var buf = new byte[head.Length + data.Length + tail.Length];
            Buffer.BlockCopy(head, 0, buf, 0, head.Length);
            Buffer.BlockCopy(data, 0, buf, head.Length, data.Length);
            Buffer.BlockCopy(tail, 0, buf, head.Length + data.Length, tail.Length);
            _objects.Add(buf);
            return _objects.Count;
        }

        /// <summary>Serialize, open the document and hand back the font dictionary object.</summary>
        public (PdfDocument Doc, PdfObject Dict) Open(int fontObjectNumber)
        {
            int catalog = AddObject("<</Type/Catalog>>");
            var bytes = Build(catalog);
            var doc = PdfDocument.Open(bytes);
            var obj = doc.LoadObject(fontObjectNumber, 0);
            Assert.NotNull(obj);
            return (doc, obj!);
        }

        private byte[] Build(int rootObjectNumber)
        {
            var outBytes = new List<byte>();
            void Append(string s) => outBytes.AddRange(Encoding.ASCII.GetBytes(s));

            Append("%PDF-1.7\n");
            var offsets = new List<int>();
            for (int i = 0; i < _objects.Count; i++)
            {
                offsets.Add(outBytes.Count);
                Append($"{i + 1} 0 obj\n");
                outBytes.AddRange(_objects[i]);
                Append("\nendobj\n");
            }
            int xrefPos = outBytes.Count;
            Append("xref\n");
            Append($"0 {_objects.Count + 1}\n");
            Append("0000000000 65535 f \n");
            foreach (int off in offsets) Append(off.ToString("D10") + " 00000 n \n");
            Append($"trailer\n<</Size {_objects.Count + 1}/Root {rootObjectNumber} 0 R>>\n");
            Append($"startxref\n{xrefPos}\n%%EOF");
            return outBytes.ToArray();
        }
    }
}
