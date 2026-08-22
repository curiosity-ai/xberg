// Tests for the text-showing half of the pdf_oxide text extractor port
// (`extractors/text.rs`: flush_tj_buffer .. show_text).
//
// Every case drives the extractor through a real operator sequence and asserts on the
// spans, characters and text matrix that come out, because that is all the rest of the
// pipeline sees. Widths are chosen so the §9.4.4 advance formula lands on round numbers —
// a 500/1000 em glyph at 10pt is 5pt — which keeps each of Tc, Tw and Tz visible in the
// totals instead of buried in a sum of similar-looking terms.
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

public class OxTextShowingTests
{
    // ---- fixtures -----------------------------------------------------------------

    /// <summary>A parsed /ToUnicode CMap over an explicit table.</summary>
    private sealed class StubCMap : IOxCMap
    {
        private readonly Dictionary<uint, string> _map;
        internal StubCMap(Dictionary<uint, string> map) => _map = map;

        public bool IsParsed => true;
        public int Count => _map.Count;
        public string? Lookup(uint code) => _map.TryGetValue(code, out string? s) ? s : null;
        public byte Wmode => 0;
    }

    /// <summary>
    /// A simple font whose every glyph is 500/1000 em and whose bytes map to themselves, so
    /// an advance is exactly `0.5 * Tfs` before Tc/Tw/Tz.
    /// </summary>
    private static OxFontInfo SimpleFont(IOxCMap? toUnicode = null)
    {
        var widths = new float[256];
        for (int i = 0; i < widths.Length; i++) widths[i] = 500.0f;

        var encoding = new Dictionary<byte, char>();
        for (int i = 0x20; i < 0x7F; i++) encoding[(byte)i] = (char)i;

        return new OxFontInfo
        {
            BaseFont = "NotAStandardFont",
            Subtype = "Type1",
            Encoding = OxEncoding.Custom(encoding),
            Widths = widths,
            FirstChar = 0,
            LastChar = 255,
            ToUnicode = toUnicode,
        };
    }

    /// <summary>An Identity-encoded CID font, whose codes are two bytes wide.</summary>
    private static OxFontInfo CidFont(string encodingName = "Identity-H")
    {
        var cidWidths = new Dictionary<ushort, float>();
        for (ushort cid = 0; cid < 128; cid++) cidWidths[cid] = 500.0f;

        return new OxFontInfo
        {
            BaseFont = "NotAStandardFont",
            Subtype = "Type0",
            Encoding = OxEncoding.Standard(encodingName),
            CidWidths = cidWidths,
            ToUnicode = new StubCMap(new Dictionary<uint, string>
            {
                [0x0020] = " ",
                [0x0041] = "A",
                [0x0042] = "B",
            }),
        };
    }

    /// <summary>An extractor with <paramref name="font"/> selected as /F1 at the given size.</summary>
    private static OxTextExtractor Extractor(
        OxFontInfo? font, float fontSize = 10.0f,
        float charSpace = 0.0f, float wordSpace = 0.0f, float horizontalScaling = 100.0f,
        byte wmode = 0)
    {
        var ex = new OxTextExtractor();
        OxGraphicsState state = ex.StateStack.Current;
        state.FontSize = fontSize;
        state.CharSpace = charSpace;
        state.WordSpace = wordSpace;
        state.HorizontalScaling = horizontalScaling;
        state.TextWMode = wmode;
        if (font is not null)
        {
            state.FontName = "F1";
            ex.Fonts["F1"] = font;
            ex.CachedCurrentFont = font;
        }
        return ex;
    }

    private static OxTjBuffer Buffer(OxTextExtractor ex) =>
        OxTextDecoding.NewTjBuffer(ex.StateStack.Current, ex.CurrentMcid, ex.CachedCurrentFont);

    private static byte[] Bytes(string ascii) => Encoding.ASCII.GetBytes(ascii);

    /// <summary>
    /// Advances are f32 sums over a 1/1000-em font matrix, so they land a few ULPs off the
    /// exact decimal the formula predicts; the port keeps f32 deliberately.
    /// </summary>
    private static void AssertWidths(IReadOnlyList<float> expected, IReadOnlyList<float> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i], 4);
    }

    private static OxTextElement Str(string ascii) => new OxTextElement.Str(Bytes(ascii));

    private static OxTextElement Off(float value) => new OxTextElement.Offset(value);

    // ---- advance arithmetic (append_advance_buffer, text.rs:8471) -------------------

    [Fact]
    public void AdvanceAddsCharSpacingToEveryGlyphAndWordSpacingOnlyToTheSpace()
    {
        // §9.4.4: tx = (w0 * Tfs + Tc + Tw) * Th. At 10pt a 500-unit glyph is 5pt, so with
        // Tc = 2 each character advances 7pt and the space adds Tw on top.
        var ex = Extractor(SimpleFont(), charSpace: 2.0f, wordSpace: 5.0f);
        OxTjBuffer buffer = Buffer(ex);

        ex.AppendAdvanceBuffer(buffer, Bytes("A A"));

        AssertWidths(new[] { 7.0f, 12.0f, 7.0f }, buffer.CharWidths);
        Assert.Equal(26.0f, buffer.AccumulatedWidth, 4);
        Assert.Equal(26.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    [Fact]
    public void HorizontalScalingScalesTheGlyphAndBothSpacings()
    {
        // Th multiplies the whole horizontal displacement, Tc and Tw included.
        var ex = Extractor(SimpleFont(), charSpace: 2.0f, wordSpace: 5.0f, horizontalScaling: 50.0f);
        OxTjBuffer buffer = Buffer(ex);

        ex.AppendAdvanceBuffer(buffer, Bytes("A A"));

        AssertWidths(new[] { 3.5f, 6.0f, 3.5f }, buffer.CharWidths);
        Assert.Equal(13.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    [Fact]
    public void NoFontFallsBackToAHalfEmAdvance()
    {
        // §9.6.6 leaves no metrics at all, so the advance is the 500-unit default width.
        var ex = Extractor(null, charSpace: 1.0f, wordSpace: 4.0f);
        OxTjBuffer buffer = Buffer(ex);

        ex.AppendAdvanceBuffer(buffer, Bytes("A "));

        AssertWidths(new[] { 6.0f, 10.0f }, buffer.CharWidths);
        Assert.Equal(16.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    [Fact]
    public void WordSpacingSkipsATwoByteCodeThirtyTwo()
    {
        // §9.3.3: Tw applies to the single-byte code 32 only. A 2-byte CID 0x0020 in an
        // Identity-H font taking Tw would over-advance the run.
        var ex = Extractor(CidFont(), wordSpace: 5.0f);
        OxTjBuffer buffer = Buffer(ex);

        ex.AppendAdvanceBuffer(buffer, new byte[] { 0x00, 0x20, 0x00, 0x41 });

        AssertWidths(new[] { 5.0f, 5.0f }, buffer.CharWidths);
    }

    [Fact]
    public void TheTjSpanBufferPathTakesWordSpacingOnACidSpaceAnyway()
    {
        // append_and_advance gates word spacing on the code alone, where the TJ-array twin
        // also requires a single-byte code. The divergence is upstream's and is load-bearing
        // for the `'`/`"`/Tj path's geometry, so it is pinned here rather than unified.
        var ex = Extractor(CidFont(), wordSpace: 5.0f);
        ex.TjSpanBuffer = Buffer(ex);

        ex.AppendAndAdvance(new byte[] { 0x00, 0x20, 0x00, 0x41 });

        AssertWidths(new[] { 10.0f, 5.0f }, ex.TjSpanBuffer!.CharWidths);
    }

    [Fact]
    public void VerticalModeUsesTheVerticalDisplacementAndIgnoresTz()
    {
        // §9.4.4: ty = w1y * Tfs + Tc + Tw, with no Th. The /DW2 default w1y is -1000, so a
        // 10pt line steps -10pt down the page before Tc.
        var ex = Extractor(CidFont("Identity-V"), charSpace: 2.0f, horizontalScaling: 50.0f, wmode: 1);
        OxTjBuffer buffer = Buffer(ex);

        ex.AppendAdvanceBuffer(buffer, new byte[] { 0x00, 0x41 });

        AssertWidths(new[] { -8.0f }, buffer.CharWidths);
        // The displacement routes into the y column, leaving x untouched.
        Assert.Equal(0.0f, ex.StateStack.Current.TextMatrix.E, 4);
        Assert.Equal(-8.0f, ex.StateStack.Current.TextMatrix.F, 4);
    }

    [Fact]
    public void AMultiCharacterMappingSplitsTheGlyphsAdvanceEvenly()
    {
        // One character code, two output characters: the span merger checks
        // char_widths.Count against the character count, so the entries have to stay in
        // lockstep with the text even when a code expands.
        var font = SimpleFont(new StubCMap(new Dictionary<uint, string> { [0x41] = "fi" }));
        var ex = Extractor(font);
        OxTjBuffer buffer = Buffer(ex);

        ex.AppendAdvanceBuffer(buffer, Bytes("A"));

        Assert.Equal("fi", buffer.Unicode.ToString());
        AssertWidths(new[] { 2.5f, 2.5f }, buffer.CharWidths);
        Assert.Equal(5.0f, buffer.AccumulatedWidth, 4);
    }

    [Fact]
    public void AdvancePositionForStringMovesTheMatrixWithoutDecoding()
    {
        var ex = Extractor(SimpleFont(), charSpace: 2.0f);

        float width = ex.AdvancePositionForString(Bytes("AB"));

        Assert.Equal(14.0f, width, 4);
        Assert.Equal(14.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    // ---- TJ offsets (advance_position_for_offset / fold_offset_into_buffer) --------

    [Fact]
    public void ATjOffsetMovesTheMatrixBackwardsScaledByTz()
    {
        // tx = -offset / 1000 * Tfs * Th: a -500 offset at 10pt and Tz 50 is a 2.5pt gap.
        var ex = Extractor(SimpleFont(), horizontalScaling: 50.0f);

        ex.AdvancePositionForOffset(-500.0f);

        Assert.Equal(2.5f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    [Fact]
    public void ATjOffsetInVerticalModeIgnoresTz()
    {
        var ex = Extractor(CidFont("Identity-V"), horizontalScaling: 50.0f, wmode: 1);

        ex.AdvancePositionForOffset(-500.0f);

        Assert.Equal(5.0f, ex.StateStack.Current.TextMatrix.F, 4);
    }

    [Fact]
    public void ASubThresholdOffsetIsFoldedOntoThePrecedingGlyphsAdvance()
    {
        // The offset adjusts the spacing *after* the last glyph, so folding it there keeps
        // sum(char_widths) equal to the accumulated width and to the matrix advance.
        var ex = Extractor(SimpleFont());
        OxTjBuffer buffer = Buffer(ex);
        ex.AppendAdvanceBuffer(buffer, Bytes("AB"));

        ex.FoldOffsetIntoBuffer(buffer, -50.0f);

        AssertWidths(new[] { 5.0f, 5.5f }, buffer.CharWidths);
        Assert.Equal(10.5f, buffer.AccumulatedWidth, 4);
    }

    [Fact]
    public void AnOffsetBeforeAnyGlyphIsNotFolded()
    {
        // With nothing recorded yet the matrix move alone already positions the next
        // buffer, so there is no advance to correct.
        var ex = Extractor(SimpleFont());
        OxTjBuffer buffer = Buffer(ex);

        ex.FoldOffsetIntoBuffer(buffer, -50.0f);

        Assert.Empty(buffer.CharWidths);
        Assert.Equal(0.0f, buffer.AccumulatedWidth, 4);
    }

    // ---- TJ array processing (process_tj_array_tiebreaker, text.rs:7559) -----------

    [Fact]
    public void SmallOffsetsKeepOneSpanWhoseWidthTracksTheMatrix()
    {
        // §9.4.4 NOTE 6: keep shown strings as long as possible. A kerning-sized offset
        // must not end the run, and the span's width must still match where the matrix
        // ended up, offsets included.
        var ex = Extractor(SimpleFont());

        ex.ProcessTjArray(new List<OxTextElement> { Str("AB"), Off(-50.0f), Str("CD") });

        OxTextSpan span = Assert.Single(ex.Spans);
        Assert.Equal("ABCD", span.Text);
        Assert.Equal(20.5f, span.Bbox.Width, 4);
        Assert.Equal(20.5f, ex.StateStack.Current.TextMatrix.E, 4);
        Assert.Equal(4, span.CharWidths.Count);
    }

    [Fact]
    public void ALargeNegativeOffsetEndsTheRunAndEmitsASpaceSpan()
    {
        // Past the threshold the offset is a word gap: the run flushes, a synthetic space
        // span records the gap, and the next run starts at the post-offset position.
        var ex = Extractor(SimpleFont());

        ex.ProcessTjArray(new List<OxTextElement> { Str("AB"), Off(-500.0f), Str("CD") });

        Assert.Equal(3, ex.Spans.Count);
        Assert.Equal("AB", ex.Spans[0].Text);
        Assert.Equal(" ", ex.Spans[1].Text);
        Assert.Equal("CD", ex.Spans[2].Text);
        Assert.True(ex.Spans[1].OffsetSemantic);

        // The space span sits at the end of "AB", and the next run starts a further 5pt on
        // — the offset itself, with nothing added for the synthetic space.
        Assert.Equal(10.0f, ex.Spans[1].Bbox.X, 4);
        Assert.Equal(15.0f, ex.Spans[2].Bbox.X, 4);
        Assert.Equal(25.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    [Fact]
    public void TheSpaceSpanIsAQuarterEmPlusWordSpacingScaledByTz()
    {
        var ex = Extractor(SimpleFont(), wordSpace: 3.0f, horizontalScaling: 50.0f);

        ex.InsertSpaceAsSpan();

        OxTextSpan space = Assert.Single(ex.Spans);
        Assert.Equal(2.75f, space.Bbox.Width, 4);
        // One synthetic character, one width entry, so the merger's lockstep holds.
        AssertWidths(new[] { 2.75f }, space.CharWidths);
        // The caller drives the matrix by the real offset; this must not move it.
        Assert.Equal(0.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    [Fact]
    public void AVerticalSpaceSpanIsTallRatherThanWide()
    {
        // Column detection reads width against height to decide a span's orientation.
        var ex = Extractor(CidFont("Identity-V"), wmode: 1);

        ex.InsertSpaceAsSpan();

        OxTextSpan space = Assert.Single(ex.Spans);
        Assert.Equal(10.0f, space.Bbox.Width, 4);
        Assert.Equal(2.5f, space.Bbox.Height, 4);
    }

    [Theory]
    [InlineData("AB ", "CD")]
    [InlineData("AB", " CD")]
    public void NoSpaceSpanIsAddedWhenEitherSideAlreadyHasWhitespace(string before, string after)
    {
        // "word " + " next" would otherwise extract with two spaces.
        var ex = Extractor(SimpleFont());

        ex.ProcessTjArray(new List<OxTextElement> { Str(before), Off(-500.0f), Str(after) });

        Assert.Equal(2, ex.Spans.Count);
        Assert.DoesNotContain(ex.Spans, s => s.OffsetSemantic);
    }

    [Fact]
    public void TjOffsetsAreRecordedForTheJustificationStatistics()
    {
        var ex = Extractor(SimpleFont());

        ex.ProcessTjArray(new List<OxTextElement> { Str("A"), Off(-50.0f), Str("B"), Off(-30.0f), Str("C") });

        Assert.Equal(new List<float> { -50.0f, -30.0f }, ex.TjOffsetHistory);
        Assert.Equal(2, ex.TjStatsLen);
        Assert.Equal(-80.0, ex.TjSum, 4);
    }

    [Fact]
    public void AnAdaptiveThresholdIsDerivedFromTheFontsSpaceGlyph()
    {
        // With the adaptive threshold on, the gap that ends a run is a fraction of the
        // font's own space advance rather than the fixed -120: 500 units * 10pt * 0.1 / 1000
        // is -0.5, so even a tiny offset becomes a word gap.
        var ex = Extractor(SimpleFont());
        ex.Config = OxTextExtractionConfig.WithWordMarginRatio(0.1f);

        ex.ProcessTjArray(new List<OxTextElement> { Str("AB"), Off(-10.0f), Str("CD") });

        Assert.Equal(3, ex.Spans.Count);
        Assert.Equal(" ", ex.Spans[1].Text);
    }

    // ---- flush boundaries (flush_tj_buffer / flush_tj_span_buffer) ------------------

    [Fact]
    public void AnEmptyBufferFlushesToNothing()
    {
        var ex = Extractor(SimpleFont());

        ex.FlushTjBuffer(Buffer(ex));

        Assert.Empty(ex.Spans);
    }

    [Fact]
    public void FlushCarriesTextSpaceWidthsIntoUserSpace()
    {
        // The buffer accumulates in text space; the CTM's horizontal scale, captured when
        // the buffer was created, is what makes the span's geometry user space.
        var ex = Extractor(SimpleFont());
        ex.StateStack.Current.Ctm = OxMatrix.Scaling(2.0f, 2.0f);
        OxTjBuffer buffer = Buffer(ex);
        ex.AppendAdvanceBuffer(buffer, Bytes("AB"));

        ex.FlushTjBuffer(buffer);

        OxTextSpan span = Assert.Single(ex.Spans);
        Assert.Equal(20.0f, span.Bbox.Width, 4);
        AssertWidths(new[] { 10.0f, 10.0f }, span.CharWidths);
        Assert.Equal(20.0f, span.FontSize, 4);
    }

    [Fact]
    public void FlushRecordsTheTextStateTheRunWasDrawnUnder()
    {
        var ex = Extractor(SimpleFont(), charSpace: 2.0f, wordSpace: 3.0f, horizontalScaling: 90.0f);
        OxTjBuffer buffer = Buffer(ex);
        ex.AppendAdvanceBuffer(buffer, Bytes("AB"));

        ex.FlushTjBuffer(buffer);

        OxTextSpan span = Assert.Single(ex.Spans);
        Assert.Equal(2.0f, span.CharSpacing, 4);
        Assert.Equal(3.0f, span.WordSpacing, 4);
        Assert.Equal(90.0f, span.HorizontalScaling, 4);
        Assert.Equal("F1", span.FontName);
        Assert.Equal(0, span.Sequence);
    }

    [Fact]
    public void TheTjSpanBufferFlushesOnceAndRecordsTheSpecDefaultTextState()
    {
        // A Tj run may have crossed several Tc/Tw/Tz settings, so the span carries the
        // §9.3.1 defaults instead of claiming one of them applied throughout.
        var ex = Extractor(SimpleFont(), charSpace: 2.0f, wordSpace: 3.0f, horizontalScaling: 90.0f);
        ex.TjSpanBuffer = Buffer(ex);
        ex.AppendAndAdvance(Bytes("AB"));

        ex.FlushTjSpanBuffer();
        ex.FlushTjSpanBuffer();

        OxTextSpan span = Assert.Single(ex.Spans);
        Assert.Equal("AB", span.Text);
        Assert.Equal(0.0f, span.CharSpacing, 4);
        Assert.Equal(0.0f, span.WordSpacing, 4);
        Assert.Equal(100.0f, span.HorizontalScaling, 4);
        Assert.Null(ex.TjSpanBuffer);
    }

    [Fact]
    public void EachFlushedSpanTakesTheNextSequenceNumber()
    {
        // Reading order falls back to draw order, so the counter has to advance across
        // every kind of span the showing path emits, synthetic spaces included.
        var ex = Extractor(SimpleFont());

        ex.ProcessTjArray(new List<OxTextElement> { Str("AB"), Off(-500.0f), Str("CD") });

        Assert.Equal(new List<int> { 0, 1, 2 }, ex.Spans.ConvertAll(s => s.Sequence));
    }

    [Fact]
    public void SuppressedContentIsMeasuredButNotEmitted()
    {
        // An excluded optional-content layer still has to advance the matrix, or every
        // later run on the line would be mis-positioned.
        var ex = Extractor(SimpleFont());
        ex.InsideExcludedLayer = true;

        ex.ProcessTjArray(new List<OxTextElement> { Str("AB"), Off(-500.0f), Str("CD") });

        Assert.Empty(ex.Spans);
        Assert.Equal(25.0f, ex.StateStack.Current.TextMatrix.E, 4);
    }

    // ---- ligatures (is_ligature_code / apply_ligature_decisions) --------------------

    [Theory]
    [InlineData(0xFAFFu, false)]
    [InlineData(0xFB00u, true)]
    [InlineData(0xFB04u, true)]
    [InlineData(0xFB05u, false)]
    public void OnlyTheFiveStandardLatinLigaturesCount(uint code, bool expected) =>
        Assert.Equal(expected, OxTextExtractor.IsLigatureCode(code));

    private static void SeedLigature(OxTextExtractor ex, float nextX, int? nextTjOffset = null)
    {
        ex.TjCharacterArray.Add(new CharacterInfo
        {
            Code = 0xFB01, // fi
            Width = 600.0f,
            XPosition = 0.0f,
            FontSize = 10.0f,
            IsLigature = true,
        });
        ex.TjCharacterArray.Add(new CharacterInfo
        {
            Code = 'x',
            Width = 500.0f,
            XPosition = nextX,
            FontSize = 10.0f,
            TjOffset = nextTjOffset,
        });
    }

    [Fact]
    public void ALigatureAtAWordGapIsExpandedIntoItsComponents()
    {
        // A gap of more than half the font size after the ligature means a word ended on
        // it, so the components have to become separate characters for the boundary to
        // land between words rather than inside a glyph.
        var ex = Extractor(SimpleFont());
        SeedLigature(ex, nextX: 610.0f);

        ex.ApplyLigatureDecisions();

        Assert.Equal(3, ex.TjCharacterArray.Count);
        Assert.Equal('f', ex.TjCharacterArray[0].Code);
        Assert.Equal('i', ex.TjCharacterArray[1].Code);
        // The advance is shared equally, so the components span exactly the ligature.
        Assert.Equal(300.0f, ex.TjCharacterArray[0].Width, 4);
        Assert.Equal(0.0f, ex.TjCharacterArray[0].XPosition, 4);
        Assert.Equal(300.0f, ex.TjCharacterArray[1].XPosition, 4);
        Assert.False(ex.TjCharacterArray[0].IsLigature);
        Assert.Equal(new Rune('ﬁ'), ex.TjCharacterArray[0].OriginalLigature);
    }

    [Fact]
    public void ALigatureInsideAWordIsLeftWhole()
    {
        var ex = Extractor(SimpleFont());
        SeedLigature(ex, nextX: 600.0f);

        ex.ApplyLigatureDecisions();

        Assert.Equal(2, ex.TjCharacterArray.Count);
        Assert.Equal(0xFB01, ex.TjCharacterArray[0].Code);
        Assert.True(ex.TjCharacterArray[0].IsLigature);
    }

    [Fact]
    public void AnExplicitTjGapAfterALigatureAlsoSplitsIt()
    {
        // The producer said "word boundary here" with a TJ offset; the geometry alone
        // would not have shown it, because the glyphs still sit next to each other.
        var ex = Extractor(SimpleFont());
        SeedLigature(ex, nextX: 600.0f, nextTjOffset: -150);

        ex.ApplyLigatureDecisions();

        Assert.Equal(3, ex.TjCharacterArray.Count);
    }

    [Fact]
    public void ALigatureAtTheEndOfTheRunIsLeftWhole()
    {
        // With no following character there is no boundary to split at.
        var ex = Extractor(SimpleFont());
        ex.TjCharacterArray.Add(new CharacterInfo
        {
            Code = 0xFB01,
            Width = 600.0f,
            XPosition = 0.0f,
            FontSize = 10.0f,
            IsLigature = true,
        });

        ex.ApplyLigatureDecisions();

        Assert.Single(ex.TjCharacterArray);
        Assert.Equal(0xFB01, ex.TjCharacterArray[0].Code);
    }

    [Fact]
    public void TheTjWalkFlagsLigatureCodesItSees()
    {
        // The character array feeding boundary detection is normalized through the
        // encoding, so a custom-encoded byte still shows up as the ligature it draws.
        var font = SimpleFont();
        font.Encoding = OxEncoding.Custom(new Dictionary<byte, char> { [0x01] = 'ﬁ', [0x41] = 'A' });
        var ex = Extractor(font);

        ex.ProcessTjArray(new List<OxTextElement> { new OxTextElement.Str(new byte[] { 0x01, 0x41 }) });

        Assert.Equal(2, ex.TjCharacterArray.Count);
        Assert.True(ex.TjCharacterArray[0].IsLigature);
        Assert.False(ex.TjCharacterArray[1].IsLigature);
    }

    // ---- show_text (text.rs:8975) --------------------------------------------------

    [Fact]
    public void ShowTextEmitsOneCharacterPerGlyphAtItsDrawnPosition()
    {
        var ex = Extractor(SimpleFont(), charSpace: 2.0f);

        ex.ShowText(Bytes("AB"));

        Assert.Equal(2, ex.CharCount());
        Assert.Equal('A', ex.Chars[0].Char);
        Assert.Equal(0.0f, ex.Chars[0].OriginX, 4);
        // The second glyph is drawn one full advance on — the glyph plus Tc.
        Assert.Equal(7.0f, ex.Chars[1].OriginX, 4);
        // The bbox is the glyph, the rendered advance is the glyph plus spacing.
        Assert.Equal(5.0f, ex.Chars[0].AdvanceWidth, 4);
        Assert.Equal(7.0f, ex.Chars[0].RenderedAdvance, 4);
        Assert.Equal(10.0f, ex.Chars[0].FontSize, 4);
    }

    [Fact]
    public void ShowTextSpreadsALigatureMappingAcrossItsCharacters()
    {
        // One code, two characters: each gets half the glyph box so the pair still covers
        // exactly the space the single glyph occupied.
        var font = SimpleFont(new StubCMap(new Dictionary<uint, string> { [0x41] = "fi" }));
        var ex = Extractor(font);

        ex.ShowText(Bytes("A"));

        Assert.Equal(2, ex.CharCount());
        Assert.Equal('f', ex.Chars[0].Char);
        Assert.Equal('i', ex.Chars[1].Char);
        Assert.Equal(2.5f, ex.Chars[0].AdvanceWidth, 4);
        Assert.Equal(2.5f, ex.Chars[1].OriginX, 4);
    }

    [Fact]
    public void ShowTextScalesGeometryByTheCtm()
    {
        var ex = Extractor(SimpleFont());
        ex.StateStack.Current.Ctm = OxMatrix.Scaling(2.0f, 2.0f);

        ex.ShowText(Bytes("AB"));

        Assert.Equal(10.0f, ex.Chars[0].AdvanceWidth, 4);
        Assert.Equal(10.0f, ex.Chars[1].OriginX, 4);
        Assert.Equal(20.0f, ex.Chars[0].FontSize, 4);
    }

    [Fact]
    public void ClearDiscardsTheExtractedCharacters()
    {
        var ex = Extractor(SimpleFont());
        ex.ShowText(Bytes("AB"));

        ex.Clear();

        Assert.Equal(0, ex.CharCount());
    }
}
