using System.Text;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Content;
using Xunit;

namespace Xberg.Tests;

public class OxContentParserTests
{
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    private static List<OxOperator> Parse(string s) => OxContentParser.ParseContentStream(B(s));

    /// <summary>Operands of an operator are only reachable through the built operator,
    /// so a lone `Other` carries them out for inspection.</summary>
    private static List<OxOperand> Operands(string s)
    {
        var ops = Parse(s);
        return Assert.IsType<OxOperator.Other>(ops.Single()).Operands;
    }

    // ── Operand literals ──────────────────────────────────────────────────

    [Fact]
    public void ParsesIntegerAndRealOperands()
    {
        var operands = Operands("42 -17 3.5 -.25 5. zz");

        Assert.Equal(42L, Assert.IsType<OxOperand.Integer>(operands[0]).Value);
        Assert.Equal(-17L, Assert.IsType<OxOperand.Integer>(operands[1]).Value);
        Assert.Equal(3.5, Assert.IsType<OxOperand.Real>(operands[2]).Value, 6);
        Assert.Equal(-0.25, Assert.IsType<OxOperand.Real>(operands[3]).Value, 6);
        Assert.Equal(5.0, Assert.IsType<OxOperand.Real>(operands[4]).Value, 6);
    }

    [Fact]
    public void ParsesNameOperandAndDecodesHashEscapes()
    {
        var operands = Operands("/F1 /A#20B zz");

        Assert.Equal("F1", Assert.IsType<OxOperand.Name>(operands[0]).Value);
        Assert.Equal("A B", Assert.IsType<OxOperand.Name>(operands[1]).Value);
    }

    [Fact]
    public void ParsesLiteralStringWithEscapes()
    {
        var ops = Parse(@"(a\nb\(c\)d\\e\101 (nested)) Tj");

        var text = Assert.IsType<OxOperator.Tj>(ops.Single()).Text;
        Assert.Equal("a\nb(c)d\\eA (nested)", Encoding.ASCII.GetString(text));
    }

    [Fact]
    public void ParsesLiteralStringLineContinuation()
    {
        var ops = Parse("(ab\\\ncd) Tj");

        Assert.Equal("abcd", Encoding.ASCII.GetString(Assert.IsType<OxOperator.Tj>(ops.Single()).Text));
    }

    [Fact]
    public void ParsesHexStringIncludingOddDigitPadding()
    {
        var ops = Parse("<48656C6C6F> Tj");
        Assert.Equal("Hello", Encoding.ASCII.GetString(Assert.IsType<OxOperator.Tj>(ops.Single()).Text));

        // An odd digit count pads the trailing nibble with 0 (ISO 32000-1 §7.3.4.3).
        var odd = Parse("<414> Tj");
        Assert.Equal(new byte[] { 0x41, 0x40 }, Assert.IsType<OxOperator.Tj>(odd.Single()).Text);
    }

    [Fact]
    public void ParsesArrayOperandIncludingNesting()
    {
        var operands = Operands("[1 (two) /Three [4]] zz");

        var array = Assert.IsType<OxOperand.Array>(operands.Single()).Items;
        Assert.Equal(1L, Assert.IsType<OxOperand.Integer>(array[0]).Value);
        Assert.Equal("two", Encoding.ASCII.GetString(Assert.IsType<OxOperand.Str>(array[1]).Bytes));
        Assert.Equal("Three", Assert.IsType<OxOperand.Name>(array[2]).Value);
        Assert.Single(Assert.IsType<OxOperand.Array>(array[3]).Items);
    }

    [Fact]
    public void ParsesDictionaryOperandOnBdc()
    {
        var ops = Parse("/Span <</MCID 3 /Lang (en) /Inner <</A 1>> >> BDC");

        var bdc = Assert.IsType<OxOperator.BeginMarkedContentDict>(ops.Single());
        Assert.Equal("Span", bdc.Tag);
        var dict = Assert.IsType<OxOperand.Dict>(bdc.Properties).Entries;
        Assert.Equal(3L, dict["MCID"].AsInteger);
        Assert.Equal("en", Encoding.ASCII.GetString(dict["Lang"].AsString!));
        Assert.True(dict["Inner"] is OxOperand.Dict);
    }

    [Fact]
    public void ParsesBooleanAndNullOperands()
    {
        var operands = Operands("[true false null] zz");

        var array = Assert.IsType<OxOperand.Array>(operands.Single()).Items;
        Assert.True(Assert.IsType<OxOperand.Bool>(array[0]).Value);
        Assert.False(Assert.IsType<OxOperand.Bool>(array[1]).Value);
        Assert.IsType<OxOperand.Null>(array[2]);
    }

    // ── Text operators ────────────────────────────────────────────────────

    [Fact]
    public void ParsesTjArrayMixingStringsAndNumbers()
    {
        var ops = Parse("[(He) -120 (llo) 25 <20> ] TJ");

        var array = Assert.IsType<OxOperator.TJ>(ops.Single()).Array;
        Assert.Equal(5, array.Count);
        Assert.Equal("He", Encoding.ASCII.GetString(Assert.IsType<OxTextElement.Str>(array[0]).Bytes));
        Assert.Equal(-120f, Assert.IsType<OxTextElement.Offset>(array[1]).Value);
        Assert.Equal("llo", Encoding.ASCII.GetString(Assert.IsType<OxTextElement.Str>(array[2]).Bytes));
        Assert.Equal(25f, Assert.IsType<OxTextElement.Offset>(array[3]).Value);
        Assert.Equal(" ", Encoding.ASCII.GetString(Assert.IsType<OxTextElement.Str>(array[4]).Bytes));
    }

    [Fact]
    public void ParsesTextStateAndPositioningOperators()
    {
        var ops = Parse("BT /F1 12 Tf 2 Tc 3 Tw 90 Tz 14 TL 1 Tr 4 Ts 10 20 Td 30 40 TD T* ET");

        Assert.IsType<OxOperator.BeginText>(ops[0]);
        var tf = Assert.IsType<OxOperator.Tf>(ops[1]);
        Assert.Equal("F1", tf.Font);
        Assert.Equal(12f, tf.Size);
        Assert.Equal(2f, Assert.IsType<OxOperator.Tc>(ops[2]).CharSpace);
        Assert.Equal(3f, Assert.IsType<OxOperator.Tw>(ops[3]).WordSpace);
        Assert.Equal(90f, Assert.IsType<OxOperator.Tz>(ops[4]).Scale);
        Assert.Equal(14f, Assert.IsType<OxOperator.TL>(ops[5]).Leading);
        Assert.Equal(1, Assert.IsType<OxOperator.Tr>(ops[6]).Render);
        Assert.Equal(4f, Assert.IsType<OxOperator.Ts>(ops[7]).Rise);
        Assert.Equal((10f, 20f), (Assert.IsType<OxOperator.Td>(ops[8]).Tx, Assert.IsType<OxOperator.Td>(ops[8]).Ty));
        Assert.Equal((30f, 40f), (Assert.IsType<OxOperator.TD>(ops[9]).Tx, Assert.IsType<OxOperator.TD>(ops[9]).Ty));
        Assert.IsType<OxOperator.TStar>(ops[10]);
        Assert.IsType<OxOperator.EndText>(ops[11]);
    }

    [Fact]
    public void ParsesQuoteAndDoubleQuoteOperators()
    {
        var ops = Parse("(one) ' 1 2 (two) \"");

        Assert.Equal("one", Encoding.ASCII.GetString(Assert.IsType<OxOperator.Quote>(ops[0]).Text));
        var dq = Assert.IsType<OxOperator.DoubleQuote>(ops[1]);
        Assert.Equal(1f, dq.WordSpace);
        Assert.Equal(2f, dq.CharSpace);
        Assert.Equal("two", Encoding.ASCII.GetString(dq.Text));
    }

    [Fact]
    public void DoResolvesTheLastOperandWhenStrayOperandsPrecedeIt()
    {
        // ISO 32000-1 §7.8.2: no operands are left over, so the XObject name is
        // the one immediately before the operator.
        var ops = Parse("1 0 0 1 0 0 /Im1 Do");

        Assert.Equal("Im1", Assert.IsType<OxOperator.Do>(ops.Single()).Name);
    }

    [Fact]
    public void ScnKeepsComponentsAndPatternName()
    {
        var ops = Parse("0.1 0.2 /P0 scn 0.5 SCN");

        var fill = Assert.IsType<OxOperator.SetFillColorN>(ops[0]);
        Assert.Equal(new[] { 0.1f, 0.2f }, fill.Components);
        Assert.Equal("P0", fill.Name);

        var stroke = Assert.IsType<OxOperator.SetStrokeColorN>(ops[1]);
        Assert.Equal(new[] { 0.5f }, stroke.Components);
        Assert.Null(stroke.Name);
    }

    [Fact]
    public void UnknownOperatorKeepsNameAndOperands()
    {
        var ops = Parse("1 2 zzz");

        var other = Assert.IsType<OxOperator.Other>(ops.Single());
        Assert.Equal("zzz", other.Name);
        Assert.Equal(2, other.Operands.Count);
    }

    // ── Graphics state ────────────────────────────────────────────────────

    [Fact]
    public void SaveAndRestoreRoundTripsTheWholeState()
    {
        var stack = new OxGraphicsStateStack();
        OxGraphicsState outer = stack.Current;
        outer.Ctm = new OxMatrix(2f, 0f, 0f, 2f, 5f, 6f);
        outer.FontName = "F1";
        outer.FontSize = 11f;
        outer.CharSpace = 1.5f;
        outer.WordSpace = 2.5f;
        outer.HorizontalScaling = 80f;
        outer.Leading = 13f;
        outer.TextRise = 3f;
        outer.RenderMode = 2;
        outer.TextWMode = 1;
        outer.FillColorSpace = "DeviceRGB";
        outer.FillColorRgb = (0.25f, 0.5f, 0.75f);
        outer.FillColorCmyk = (0f, 0f, 0f, 1f);
        outer.FillColorComponents.Add(0.25f);
        outer.DashArray.Add(3f);
        outer.DashPhase = 1f;
        outer.LineWidth = 4f;
        outer.LineCap = 1;
        outer.LineJoin = 2;
        outer.MiterLimit = 7f;
        outer.RenderingIntent = "Perceptual";
        outer.Flatness = 0.5f;
        outer.FillAlpha = 0.4f;
        outer.StrokeAlpha = 0.3f;
        outer.BlendMode = "Multiply";
        outer.FillOverprint = true;
        outer.OverprintMode = 1;
        outer.FillPatternName = "P1";
        outer.FillSpotInks.Add(("Pantone", 0.6f));

        stack.Save();
        Assert.Equal(2, stack.Depth);

        OxGraphicsState inner = stack.Current;
        Assert.Equal("F1", inner.FontName);
        Assert.Equal(new OxMatrix(2f, 0f, 0f, 2f, 5f, 6f), inner.Ctm);
        Assert.Equal(0.25f, inner.FillColorComponents.Single());

        inner.Ctm = OxMatrix.Identity;
        inner.FontName = "F2";
        inner.FontSize = 30f;
        inner.FillColorRgb = (1f, 1f, 1f);
        inner.FillColorCmyk = null;
        inner.FillColorComponents.Add(0.9f);
        inner.DashArray.Clear();
        inner.BlendMode = "Screen";
        inner.FillSpotInks.Clear();

        stack.Restore();
        Assert.Equal(1, stack.Depth);

        OxGraphicsState restored = stack.Current;
        Assert.Same(outer, restored);
        Assert.Equal(new OxMatrix(2f, 0f, 0f, 2f, 5f, 6f), restored.Ctm);
        Assert.Equal("F1", restored.FontName);
        Assert.Equal(11f, restored.FontSize);
        Assert.Equal(1.5f, restored.CharSpace);
        Assert.Equal(80f, restored.HorizontalScaling);
        Assert.Equal(2, restored.RenderMode);
        Assert.Equal(1, restored.TextWMode);
        Assert.Equal("DeviceRGB", restored.FillColorSpace);
        Assert.Equal((0.25f, 0.5f, 0.75f), restored.FillColorRgb);
        Assert.Equal((0f, 0f, 0f, 1f), restored.FillColorCmyk);
        Assert.Equal(new[] { 0.25f }, restored.FillColorComponents);
        Assert.Equal(new[] { 3f }, restored.DashArray);
        Assert.Equal("Multiply", restored.BlendMode);
        Assert.True(restored.FillOverprint);
        Assert.Equal("P1", restored.FillPatternName);
        Assert.Equal(("Pantone", 0.6f), restored.FillSpotInks.Single());
    }

    [Fact]
    public void RestoreOnTheBottomStateIsANoOp()
    {
        var stack = new OxGraphicsStateStack();
        stack.Restore();
        stack.Restore();

        Assert.Equal(1, stack.Depth);
    }

    [Fact]
    public void CmConcatenatesNewMatrixBeforeTheExistingCtm()
    {
        var ops = Parse("2 0 0 2 0 0 cm 1 0 0 1 10 20 cm");

        OxMatrix ctm = OxMatrix.Identity;
        foreach (OxOperator op in ops)
        {
            var cm = Assert.IsType<OxOperator.Cm>(op);
            ctm = new OxMatrix(cm.A, cm.B, cm.C, cm.D, cm.E, cm.F).Multiply(ctm);
        }

        // The scale applied first still scales the later translation.
        Assert.Equal(new OxMatrix(2f, 0f, 0f, 2f, 20f, 40f), ctm);
    }

    [Fact]
    public void AdvanceTextMatrixSwapsAxisOnVerticalWritingMode()
    {
        var state = new OxGraphicsState { TextMatrix = new OxMatrix(2f, 3f, 4f, 5f, 0f, 0f) };

        (float de, float df) = state.AdvanceTextMatrix(10f);
        Assert.Equal((20f, 30f), (de, df));

        state.TextWMode = 1;
        (de, df) = state.AdvanceTextMatrix(10f);
        Assert.Equal((40f, 50f), (de, df));
        Assert.Equal(60f, state.TextMatrix.E);
        Assert.Equal(80f, state.TextMatrix.F);
    }

    [Fact]
    public void DashPatternClassification()
    {
        var state = new OxGraphicsState();
        Assert.False(state.IsDashed());
        Assert.False(state.IsDotted());

        state.DashArray.AddRange(new[] { 1f, 1.5f });
        Assert.True(state.IsDashed());
        Assert.True(state.IsDotted());

        state.DashArray.Clear();
        state.DashArray.AddRange(new[] { 10f, 10f });
        Assert.True(state.IsDashed());
        Assert.False(state.IsDotted());
    }

    // ── Inline images ─────────────────────────────────────────────────────

    [Fact]
    public void InlineImageDataMayContainTheBytesEi()
    {
        // The embedded "EI" is not preceded by whitespace, so only the trailing
        // whitespace-bounded EI ends the image (ISO 32000-1 §8.9.7).
        var ops = OxContentParser.ParseContentStream(B("q BI /W 4 /H 1 /BPC 8 ID AEIB EI Q"));

        var image = ops.OfType<OxOperator.InlineImage>().Single();
        Assert.Equal(4L, image.Dict["W"].AsInteger);
        Assert.Equal(1L, image.Dict["H"].AsInteger);
        Assert.Equal(8L, image.Dict["BPC"].AsInteger);
        Assert.Equal("AEIB", Encoding.ASCII.GetString(image.Data));
        Assert.Contains(ops, o => o is OxOperator.RestoreState);
    }

    [Fact]
    public void InlineImageAcceptsNullAsTheWhitespaceBeforeEi()
    {
        // PDF Table 1 lists NUL as whitespace, and text after the image must
        // still be extracted.
        var stream = B("q BI /W 2 /H 2 ID AB").Concat(B("\0EI Q BT (Hi) Tj ET")).ToArray();
        var ops = OxContentParser.ParseContentStream(stream);

        Assert.Contains(ops, o => o is OxOperator.InlineImage);
        Assert.Contains(ops, o => o is OxOperator.RestoreState);
        Assert.Contains(ops, o => o is OxOperator.Tj);
    }

    [Fact]
    public void TextOnlyParseSkipsInlineImagesButKeepsFollowingText()
    {
        var ops = OxContentParser.ParseContentStreamTextOnly(B("BI /W 2 /H 2 ID AB EI BT (Hi) Tj ET"));

        Assert.DoesNotContain(ops, o => o is OxOperator.InlineImage);
        Assert.Equal("Hi", Encoding.ASCII.GetString(ops.OfType<OxOperator.Tj>().Single().Text));
    }

    // ── Malformed input ───────────────────────────────────────────────────

    [Fact]
    public void TruncatedStreamEndingMidOperatorDegradesInsteadOfThrowing()
    {
        // The unterminated literal string can never complete, so the trailing
        // bytes are dropped and everything before them survives.
        var ops = Parse("BT /F1 12 Tf 100 700 Td (Hel");

        Assert.IsType<OxOperator.BeginText>(ops[0]);
        Assert.Equal("F1", Assert.IsType<OxOperator.Tf>(ops[1]).Font);
        Assert.Equal(100f, Assert.IsType<OxOperator.Td>(ops[2]).Tx);
        Assert.DoesNotContain(ops, o => o is OxOperator.Tj);
    }

    [Fact]
    public void TrailingOperandsWithoutAnOperatorAreDropped()
    {
        var ops = Parse("BT ET 100 200");

        Assert.Equal(2, ops.Count);
        Assert.IsType<OxOperator.EndText>(ops[1]);
    }

    [Fact]
    public void GarbageBytesAreSkippedAndParsingResumes()
    {
        var ops = Parse("BT \x01\x02\x03 (Hi) Tj ET");

        Assert.Equal("Hi", Encoding.ASCII.GetString(ops.OfType<OxOperator.Tj>().Single().Text));
        Assert.Contains(ops, o => o is OxOperator.EndText);
    }

    [Fact]
    public void UnbalancedTextMarkersDoNotDerailTheParser()
    {
        var ops = Parse("BT (a) Tj BT (b) Tj ET");

        Assert.Equal(2, ops.OfType<OxOperator.Tj>().Count());
    }

    [Fact]
    public void EmptyAndWhitespaceOnlyStreamsYieldNoOperators()
    {
        Assert.Empty(Parse(string.Empty));
        Assert.Empty(Parse("   \r\n\t  "));

        // A comment is only skipped while looking for an operand, so a
        // comment-only stream degrades into unknown operators rather than
        // failing — same as upstream.
        Assert.All(Parse("% just a comment\n% and another\n"), o => Assert.IsType<OxOperator.Other>(o));
    }

    [Fact]
    public void TruncatedInlineImageDoesNotThrow()
    {
        var ops = Parse("q BI /W 2 /H 2 ID AB");

        Assert.Contains(ops, o => o is OxOperator.SaveState);
        Assert.DoesNotContain(ops, o => o is OxOperator.InlineImage);
    }

    // ── Text-only and streaming variants ──────────────────────────────────

    [Fact]
    public void TextOnlyParseSkipsPathsButKeepsTextAndCtm()
    {
        var ops = OxContentParser.ParseContentStreamTextOnly(
            B("1 0 0 1 50 60 cm 10 10 m 20 20 l S BT /F1 12 Tf (Hi) Tj ET"));

        Assert.Contains(ops, o => o is OxOperator.Cm);
        Assert.DoesNotContain(ops, o => o is OxOperator.MoveTo);
        Assert.DoesNotContain(ops, o => o is OxOperator.Stroke);
        Assert.Equal("Hi", Encoding.ASCII.GetString(ops.OfType<OxOperator.Tj>().Single().Text));
    }

    [Fact]
    public void TextOnlyParseKeepsColourSetBeforeTheTextObject()
    {
        // Colour set outside a q/Q scope persists into the text object, so it
        // must survive the graphics scan.
        var ops = OxContentParser.ParseContentStreamTextOnly(B("1 0 0 rg BT (Hi) Tj ET"));

        var rgb = ops.OfType<OxOperator.SetFillRgb>().Single();
        Assert.Equal((1f, 0f, 0f), (rgb.R, rgb.G, rgb.B));
    }

    [Fact]
    public void FullAndTextOnlyParsesAgreeOnTextOperators()
    {
        byte[] stream = B("q 1 0 0 1 10 20 cm 0 0 100 50 re f BT /F1 12 Tf [(A) -50 (B)] TJ ET Q");

        string[] Text(IEnumerable<OxOperator> ops) => ops
            .Where(o => o is OxOperator.Tj or OxOperator.TJ or OxOperator.Tf or OxOperator.BeginText or OxOperator.EndText)
            .Select(o => o.ToString()!)
            .ToArray();

        Assert.Equal(
            Text(OxContentParser.ParseContentStream(stream)),
            Text(OxContentParser.ParseContentStreamTextOnly(stream)));
    }

    [Fact]
    public void ParseAndExecuteTextOnlyStreamsTheSameOperators()
    {
        byte[] stream = B("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var streamed = new List<OxOperator>();
        OxContentParser.ParseAndExecuteTextOnly(stream, op =>
        {
            streamed.Add(op);
            return true;
        });

        Assert.Equal(
            OxContentParser.ParseContentStreamTextOnly(stream).Select(o => o.ToString()),
            streamed.Select(o => o.ToString()));
    }

    [Fact]
    public void ParseAndExecuteTextOnlyStopsWhenTheHandlerReturnsFalse()
    {
        int seen = 0;
        OxContentParser.ParseAndExecuteTextOnly(B("BT (a) Tj (b) Tj (c) Tj ET"), _ =>
        {
            seen++;
            return seen < 2;
        });

        Assert.Equal(2, seen);
    }

    [Fact]
    public void LargeStreamsGoThroughThePrescanAndStillYieldTheirText()
    {
        // Above 256 KB the streaming parser switches to region prescanning, and
        // the enclosing cm must be re-injected ahead of each region.
        var sb = new StringBuilder("q 2 0 0 2 30 40 cm\n");
        for (int i = 0; i < 30000; i++)
        {
            sb.Append("10 20 m 30 40 l S\n");
        }

        sb.Append("BT /F1 12 Tf 100 700 Td (Deep) Tj ET\nQ\n");
        byte[] stream = B(sb.ToString());
        Assert.True(stream.Length > 256 * 1024);

        var streamed = new List<OxOperator>();
        OxContentParser.ParseAndExecuteTextOnly(stream, op =>
        {
            streamed.Add(op);
            return true;
        });

        Assert.Equal("Deep", Encoding.ASCII.GetString(streamed.OfType<OxOperator.Tj>().Single().Text));
        var cm = streamed.OfType<OxOperator.Cm>().First();
        Assert.Equal((2f, 2f, 30f, 40f), (cm.A, cm.D, cm.E, cm.F));
        Assert.DoesNotContain(streamed, o => o is OxOperator.MoveTo);
    }

    [Fact]
    public void ImagesOnlyParseKeepsXObjectsAndSkipsText()
    {
        var ops = OxContentParser.ParseContentStreamImagesOnly(
            B("q 1 0 0 1 0 0 cm /Im0 Do Q BT (Hi) Tj ET"));

        Assert.Equal("Im0", ops.OfType<OxOperator.Do>().Single().Name);
        Assert.DoesNotContain(ops, o => o is OxOperator.Tj);
    }

    [Fact]
    public void PathsOnlyParseKeepsGeometryAndSkipsText()
    {
        var ops = OxContentParser.ParseContentStreamPathsOnly(
            B("10 20 m 30 40 l 0 0 100 50 re f BT (Hi) Tj ET 1 0 0 RG S"));

        Assert.Equal((10f, 20f), (ops.OfType<OxOperator.MoveTo>().Single().X, ops.OfType<OxOperator.MoveTo>().Single().Y));
        Assert.Single(ops.OfType<OxOperator.LineTo>());
        Assert.Single(ops.OfType<OxOperator.Rectangle>());
        Assert.Single(ops.OfType<OxOperator.SetStrokeRgb>());
        Assert.DoesNotContain(ops, o => o is OxOperator.Tj);
    }

    // ── Limits ────────────────────────────────────────────────────────────

    [Fact]
    public void OperatorCapTruncatesTheStream()
    {
        int? previous = OxContentParser.SetMaxOpsPerStream(10);
        try
        {
            var ops = Parse(string.Concat(Enumerable.Repeat("q Q ", 50)));
            Assert.Equal(10, ops.Count);
        }
        finally
        {
            OxContentParser.SetMaxOpsPerStream(previous);
        }
    }

    [Fact]
    public void ConsecutiveErrorBailoutStopsOnLongJunkRuns()
    {
        // Past MaxConsecutiveErrors skipped bytes the remainder is treated as
        // junk, so the trailing text is not reached.
        var ops = Parse(new string('\x01', OxContentParser.MaxConsecutiveErrors + 100) + " BT (Hi) Tj ET");

        Assert.Empty(ops);
    }

    // ── Operator metadata ─────────────────────────────────────────────────

    [Fact]
    public void IsColorSettingCoversEveryColourOperator()
    {
        Assert.True(new OxOperator.SetFillRgb(0f, 0f, 0f).IsColorSetting());
        Assert.True(new OxOperator.SetStrokeCmyk(0f, 0f, 0f, 0f).IsColorSetting());
        Assert.True(new OxOperator.SetFillColorSpace("DeviceRGB").IsColorSetting());
        Assert.True(new OxOperator.SetStrokeColorN(new List<float>(), null).IsColorSetting());
        Assert.False(OxOperator.SaveState.Instance.IsColorSetting());
        Assert.False(new OxOperator.Tj(Array.Empty<byte>()).IsColorSetting());
    }

    [Fact]
    public void ValidateOperandsChecksTableA1Counts()
    {
        var two = new List<OxOperand> { new OxOperand.Integer(1), new OxOperand.Integer(2) };
        var one = new List<OxOperand> { new OxOperand.Integer(1) };

        Assert.Null(OxOperator.ValidateOperandsForRawOperator("Td", two));
        Assert.Null(OxOperator.ValidateOperandsForRawOperator("q", new List<OxOperand>()));
        Assert.Contains("got 1", OxOperator.ValidateOperandsForRawOperator("Td", one));
        Assert.Contains("moveto", OxOperator.ValidateOperandsForRawOperator("m", one));

        // Unknown operators are deliberately unvalidated.
        Assert.Null(OxOperator.ValidateOperandsForRawOperator("zzz", two));
    }
}
