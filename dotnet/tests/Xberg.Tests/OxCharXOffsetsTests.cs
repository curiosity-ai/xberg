// Cover for the per-glyph x-origin stamp (`document.rs::stamp_char_x_offsets`) and the
// page-rotation reading it gates on (`document.rs::get_page_rotation`).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Layout;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

public class OxCharXOffsetsTests
{
    private static PdfDocument Open(string relative) =>
        PdfDocument.Open(File.ReadAllBytes(Path.Combine("../../../../../../test_documents", relative)));

    /// <summary>Catalog → Pages → one Page carrying the given dictionary entries.</summary>
    private static byte[] OnePagePdf(string pageExtras)
    {
        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 612 792]>>",
            $"<</Type/Page/Parent 2 0 R{pageExtras}>>",
        };

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
        foreach (int off in offsets) sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Count + 1}/Root 1 0 R>>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static OxTextChar Glyph(char c, float x, float y) => new()
    {
        Char = c,
        Bbox = new OxRect(x, y, 5.0f, 10.0f),
        OriginX = x,
        OriginY = y,
        FontSize = 10.0f,
    };

    private static OxTextSpan Span(string text, float x, float y, float width) => new()
    {
        Text = text,
        Bbox = new OxRect(x, y, width, 10.0f),
        FontSize = 10.0f,
    };

    [Fact]
    public void TheAccurateOriginsReplaceTheDriftingPrefixSum()
    {
        // The span's nominal widths say 5pt apiece; the glyphs were actually drawn with a
        // kern that puts the third one 3pt further right. The stamp must report where the
        // glyph is, not where the widths predict.
        var span = Span("abc", 100.0f, 50.0f, 18.0f);
        span.CharWidths.AddRange(new[] { 5.0f, 5.0f, 5.0f });
        var chars = new List<OxTextChar> { Glyph('a', 100.0f, 50.0f), Glyph('b', 105.0f, 50.0f), Glyph('c', 113.0f, 50.0f) };

        OxCharXOffsets.Stamp(PdfDocument.Open(OnePagePdf("")), 0, new List<OxTextSpan> { span }, chars);

        Assert.Equal(new[] { 100.0f, 105.0f, 113.0f }, span.CharXOffsets);
    }

    [Fact]
    public void AGlyphWithNoMatchIsInterpolatedFromThePrecedingAnchor()
    {
        // A word-boundary space the span merger inserted has no glyph of its own; it takes
        // the preceding anchor plus the nominal widths in between, and the glyphs after it
        // pick their own anchors back up.
        var span = Span("a b", 100.0f, 50.0f, 18.0f);
        span.CharWidths.AddRange(new[] { 5.0f, 4.0f, 5.0f });
        var chars = new List<OxTextChar> { Glyph('a', 100.0f, 50.0f), Glyph('b', 112.0f, 50.0f) };

        OxCharXOffsets.Stamp(PdfDocument.Open(OnePagePdf("")), 0, new List<OxTextSpan> { span }, chars);

        Assert.Equal(new[] { 100.0f, 105.0f, 112.0f }, span.CharXOffsets);
    }

    [Fact]
    public void ARunTooFewOfWhoseGlyphsMatchIsLeftToThePrefixSum()
    {
        // Under 60% anchors the char run is not recognisably this span's text.
        var span = Span("abcde", 100.0f, 50.0f, 25.0f);
        var chars = new List<OxTextChar> { Glyph('a', 100.0f, 50.0f), Glyph('z', 105.0f, 50.0f) };

        OxCharXOffsets.Stamp(PdfDocument.Open(OnePagePdf("")), 0, new List<OxTextSpan> { span }, chars);

        Assert.Empty(span.CharXOffsets);
    }

    [Fact]
    public void GlyphsOnAnotherBaselineAreNotBorrowed()
    {
        // Only chars within 0.6 font sizes of the span's baseline belong to it; a line
        // above or below must not supply anchors.
        var span = Span("abc", 100.0f, 50.0f, 15.0f);
        var chars = new List<OxTextChar> { Glyph('a', 100.0f, 80.0f), Glyph('b', 105.0f, 80.0f), Glyph('c', 110.0f, 80.0f) };

        OxCharXOffsets.Stamp(PdfDocument.Open(OnePagePdf("")), 0, new List<OxTextSpan> { span }, chars);

        Assert.Empty(span.CharXOffsets);
    }

    [Fact]
    public void ARotatedRunKeepsThePrefixSumPath()
    {
        // Its glyphs advance vertically in the displayed frame, so a horizontal-x stamp
        // would describe something else entirely.
        var span = Span("abc", 100.0f, 50.0f, 15.0f);
        span.RotationDegrees = 90.0f;
        var chars = new List<OxTextChar> { Glyph('a', 100.0f, 50.0f), Glyph('b', 105.0f, 50.0f), Glyph('c', 110.0f, 50.0f) };

        OxCharXOffsets.Stamp(PdfDocument.Open(OnePagePdf("")), 0, new List<OxTextSpan> { span }, chars);

        Assert.Empty(span.CharXOffsets);
    }

    [Fact]
    public void AHalfTurnedPageIsSkippedWholesale()
    {
        // 180° is the one rotation that leaves every span in the displayed frame, so the
        // raw x-origins do not correspond to what the reader sees.
        var span = Span("abc", 100.0f, 50.0f, 15.0f);
        var chars = new List<OxTextChar> { Glyph('a', 100.0f, 50.0f), Glyph('b', 105.0f, 50.0f), Glyph('c', 110.0f, 50.0f) };

        OxCharXOffsets.Stamp(PdfDocument.Open(OnePagePdf("/Rotate 180")), 0, new List<OxTextSpan> { span }, chars);

        Assert.Empty(span.CharXOffsets);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("/Rotate 90", 90)]
    [InlineData("/Rotate 450", 90)]
    [InlineData("/Rotate -90", 270)]
    // §7.7.3.3 requires a multiple of 90; anything else is invalid and must not be floored.
    [InlineData("/Rotate 135", 0)]
    public void ThePageRotationIsNormalizedToAQuarterTurn(string extras, int expected)
    {
        var doc = PdfDocument.Open(OnePagePdf(extras));
        Assert.Equal(expected, OxCharXOffsets.GetPageRotation(doc, 0));
    }

    [Fact]
    public void EveryGlyphOfARealPagesSpansGetsAnOriginInsideItsBox()
    {
        var page = OxPageExtractor.ExtractPageText(Open("vendored/pdfplumber/pdf/annotations.pdf"), 0);
        var stamped = page.Spans.Where(s => s.CharXOffsets.Count > 0).ToList();

        Assert.NotEmpty(stamped);
        foreach (var span in stamped)
        {
            Assert.Equal(span.Text.EnumerateRunes().Count(), span.CharXOffsets.Count);
            foreach (float x in span.CharXOffsets)
            {
                Assert.InRange(x, span.Bbox.X - 0.5f, span.Bbox.X + span.Bbox.Width + 0.5f);
            }
        }
    }
}
