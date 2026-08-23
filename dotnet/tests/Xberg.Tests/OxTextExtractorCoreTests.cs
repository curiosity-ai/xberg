// Tests for the ported pdf_oxide extractor lifecycle and operator dispatch
// (pdf_oxide-0.3.77 src/extractors/text.rs: execute_operator, the marked-content handlers,
// calculate_adaptive_tj_threshold and process_xobject).
//
// Operators are fed in one at a time, because the dispatch is what is under test, and the
// spans the showing half emits are what the assertions read: where the dispatch ends a run
// (a font change, a line break, a q/Q block, a marked-content boundary) is visible as a span
// boundary, and where it re-enters a Form XObject is visible as a second span at a second
// position. No font is declared unless a case needs one, which decodes shown bytes as Latin-1
// (§9.6.6) and gives every glyph the 500/1000 em default advance.
using System;
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

public sealed class OxTextExtractorCoreTests
{
    // ---- helpers -----------------------------------------------------------------

    private static OxTextExtractor NewExtractor(OxTextExtractionConfig? config = null)
    {
        var extractor = new OxTextExtractor(config ?? OxTextExtractionConfig.New());

        // Font loading for a form's own /Resources is a seam of its own; these cases exercise
        // the XObject walk, not the font dictionary reader.
        extractor.LoadFontsForResources = (_, _) => { };
        return extractor;
    }

    private static void Show(OxTextExtractor extractor, string text) =>
        extractor.ExecuteOperatorPublic(new OxOperator.Tj(Ascii(text)));

    /// <summary>The text of every span emitted so far, in emission order.</summary>
    private static List<string> SpanTexts(OxTextExtractor extractor) =>
        extractor.Spans.ConvertAll(s => s.Text);

    private static OxOperand.Dict Props(params (string Key, OxOperand Value)[] entries)
    {
        var map = new Dictionary<string, OxOperand>();
        foreach ((string key, OxOperand value) in entries)
        {
            map[key] = value;
        }
        return new OxOperand.Dict(map);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ---- text state operators ----------------------------------------------------

    [Fact]
    public void TextStateOperatorsUpdateTheGraphicsState()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.Tc(1.5f));
        extractor.ExecuteOperatorPublic(new OxOperator.Tw(2.5f));
        extractor.ExecuteOperatorPublic(new OxOperator.Tz(80.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.TL(14.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Ts(3.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Tr(2));

        var state = extractor.StateStack.Current;
        Assert.Equal(1.5f, state.CharSpace);
        Assert.Equal(2.5f, state.WordSpace);
        Assert.Equal(80.0f, state.HorizontalScaling);
        Assert.Equal(14.0f, state.Leading);
        Assert.Equal(3.0f, state.TextRise);
        Assert.Equal(2, state.RenderMode);
    }

    [Fact]
    public void ReselectingTheSameFontKeepsTheRunTogether()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 12.0f));
        Show(extractor, "A");

        // Re-selecting the font already in force is common after q/Q, and must not split the
        // run: the buffer decodes with the font it was created under, and that has not moved.
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 12.0f));
        Show(extractor, "B");
        extractor.FlushPublic();

        Assert.Equal(new[] { "AB" }, SpanTexts(extractor));
    }

    [Fact]
    public void ChangingTheFontSizeEndsTheRun()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 12.0f));
        Show(extractor, "A");
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 14.0f));
        Show(extractor, "B");
        extractor.FlushPublic();

        Assert.Equal(new[] { "A", "B" }, SpanTexts(extractor));
        Assert.Equal(14.0f, extractor.StateStack.Current.FontSize);
        Assert.Equal("F1", extractor.StateStack.Current.FontName);
    }

    [Fact]
    public void TfCachesTheFontAndItsWritingMode()
    {
        var extractor = NewExtractor();
        extractor.AddFont("F1", new OxFontInfo { Wmode = 1 });

        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 10.0f));

        Assert.NotNull(extractor.CachedCurrentFont);
        Assert.Equal(1, extractor.StateStack.Current.TextWMode);
    }

    // ---- text positioning operators ---------------------------------------------

    [Fact]
    public void TdPremultipliesTheTranslationIntoTheTextLineMatrix()
    {
        var extractor = NewExtractor();

        // A scaled Tm means the Td translation is scaled too — the premultiply is what makes
        // that so (§9.4.2 Table 108).
        extractor.ExecuteOperatorPublic(new OxOperator.Tm(2.0f, 0.0f, 0.0f, 2.0f, 10.0f, 20.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Td(3.0f, 4.0f));

        var tm = extractor.StateStack.Current.TextMatrix;
        Assert.Equal(16.0f, tm.E);
        Assert.Equal(28.0f, tm.F);
        Assert.Equal(tm, extractor.StateStack.Current.TextLineMatrix);
    }

    [Fact]
    public void TdSetsLeadingAndTStarConsumesIt()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.TD(0.0f, -15.0f));
        Assert.Equal(15.0f, extractor.StateStack.Current.Leading);

        float afterTd = extractor.StateStack.Current.TextMatrix.F;
        extractor.ExecuteOperatorPublic(OxOperator.TStar.Instance);
        Assert.Equal(afterTd - 15.0f, extractor.StateStack.Current.TextMatrix.F);
    }

    [Fact]
    public void ALinePositioningOperatorEndsTheRun()
    {
        var extractor = NewExtractor();

        Show(extractor, "A");
        extractor.ExecuteOperatorPublic(new OxOperator.Td(0.0f, -14.0f));
        Show(extractor, "B");
        extractor.FlushPublic();

        Assert.Equal(new[] { "A", "B" }, SpanTexts(extractor));
    }

    [Fact]
    public void BeginTextResetsBothTextMatrices()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.Tm(1.0f, 0.0f, 0.0f, 1.0f, 50.0f, 60.0f));
        extractor.ExecuteOperatorPublic(OxOperator.BeginText.Instance);

        Assert.True(extractor.StateStack.Current.TextMatrix.IsIdentity);
        Assert.True(extractor.StateStack.Current.TextLineMatrix.IsIdentity);
    }

    [Fact]
    public void EndTextEmitsThePendingRun()
    {
        var extractor = NewExtractor();

        Show(extractor, "A");
        Assert.Empty(extractor.Spans);

        extractor.ExecuteOperatorPublic(OxOperator.EndText.Instance);
        Assert.Equal(new[] { "A" }, SpanTexts(extractor));
    }

    [Fact]
    public void CmConcatenatesTheNewMatrixBeforeTheCurrentOne()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.Cm(2.0f, 0.0f, 0.0f, 2.0f, 0.0f, 0.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Cm(1.0f, 0.0f, 0.0f, 1.0f, 10.0f, 0.0f));

        // §8.3.4 concatenates as M × CTM, so the translation is scaled by the earlier matrix.
        var ctm = extractor.StateStack.Current.Ctm;
        Assert.Equal(2.0f, ctm.A);
        Assert.Equal(20.0f, ctm.E);
    }

    [Fact]
    public void CmInsideATextObjectEmitsTheRunAtThePositionItWasCapturedUnder()
    {
        var extractor = NewExtractor();

        Show(extractor, "A");
        extractor.ExecuteOperatorPublic(new OxOperator.Cm(1.0f, 0.0f, 0.0f, 1.0f, 300.0f, 0.0f));
        Show(extractor, "B");
        extractor.FlushPublic();

        // Without the flush the second glyph's position would come from the new CTM while the
        // run still reported the old one, dropping the cluster off the page. The second run
        // starts at the new CTM plus the advance the first glyph already made (500/1000 em
        // at 12 pt).
        Assert.Equal(new[] { "A", "B" }, SpanTexts(extractor));
        Assert.Equal(0.0f, extractor.Spans[0].Bbox.X);
        Assert.Equal(306.0f, extractor.Spans[1].Bbox.X, 3);
    }

    [Fact]
    public void SaveAndRestoreRoundTripTheStateAndResyncTheCachedFont()
    {
        var extractor = NewExtractor();
        extractor.AddFont("F1", new OxFontInfo());
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 9.0f));
        Show(extractor, "A");

        extractor.ExecuteOperatorPublic(OxOperator.SaveState.Instance);
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F2", 30.0f));
        Assert.Null(extractor.CachedCurrentFont);

        extractor.ExecuteOperatorPublic(OxOperator.RestoreState.Instance);

        // Each q/Q block emits its own cluster, because Q can restore a CTM the open run's
        // captured position no longer matches.
        Assert.Equal(new[] { "A" }, SpanTexts(extractor));
        Assert.Equal(9.0f, extractor.StateStack.Current.FontSize);
        Assert.NotNull(extractor.CachedCurrentFont);
    }

    [Fact]
    public void ATmOnTheSameLineContinuesTheOpenRun()
    {
        var extractor = NewExtractor();
        extractor.ExecuteOperatorPublic(new OxOperator.Tm(1.0f, 0.0f, 0.0f, 1.0f, 100.0f, 700.0f));
        Show(extractor, "A");

        // Per-glyph Tm+Tj is how many producers position text; a baseline nudge far below the
        // line height is the same visual line and must not split the run.
        extractor.ExecuteOperatorPublic(new OxOperator.Tm(1.0f, 0.0f, 0.0f, 1.0f, 110.0f, 700.3f));
        Show(extractor, "B");
        extractor.FlushPublic();

        Assert.Equal(new[] { "AB" }, SpanTexts(extractor));
    }

    [Fact]
    public void ATmOnANewLineEndsTheRun()
    {
        var extractor = NewExtractor();
        extractor.ExecuteOperatorPublic(new OxOperator.Tm(1.0f, 0.0f, 0.0f, 1.0f, 100.0f, 700.0f));
        Show(extractor, "A");

        // A drop on the order of the font size is a real line break.
        extractor.ExecuteOperatorPublic(new OxOperator.Tm(1.0f, 0.0f, 0.0f, 1.0f, 100.0f, 686.0f));
        Show(extractor, "B");
        extractor.FlushPublic();

        Assert.Equal(new[] { "A", "B" }, SpanTexts(extractor));
        Assert.Equal(700.0f, extractor.Spans[0].Bbox.Y);
        Assert.Equal(686.0f, extractor.Spans[1].Bbox.Y);
    }

    // ---- marked content ----------------------------------------------------------

    [Fact]
    public void AMarkedContentBoundaryEndsTheRunSoOneSpanCannotStraddleTwoElements()
    {
        var extractor = NewExtractor();

        Show(extractor, "A");
        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContent("Artifact"));
        Assert.True(extractor.InsideArtifact);

        Show(extractor, "B");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        Assert.Equal(new[] { "A", "B" }, SpanTexts(extractor));
        Assert.False(extractor.InsideArtifact);
    }

    [Fact]
    public void AnArtifactBdcStampsItsClassificationOnTheSpansInsideIt()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict(
            "Artifact",
            Props(("Type", new OxOperand.Name("Pagination")), ("Subtype", new OxOperand.Name("Header")))));
        Assert.True(extractor.InsideArtifact);

        Show(extractor, "page 3");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        // Artifacts are not suppressed here — they travel on span metadata and are filtered
        // downstream, because many producers mark real page content as an artifact.
        var span = Assert.Single(extractor.Spans);
        Assert.Equal("page 3", span.Text);
        Assert.Equal(OxArtifactType.Pagination, span.ArtifactType);
    }

    [Fact]
    public void ASubtypeWithoutATypeStillClassifiesAPaginationArtifact()
    {
        var parsed = OxTextExtractor.ParseArtifactType(
            new PdfDict { Map = { ["Subtype"] = new PdfName("Watermark") } });

        Assert.NotNull(parsed);
        Assert.Equal(OxArtifactType.Pagination, parsed!.Value.Type);
        Assert.Equal(OxPaginationSubtype.Watermark, parsed.Value.Subtype);
    }

    [Fact]
    public void AnInnerEmcRestoresTheEnclosingMcidRatherThanBlankingIt()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict(
            "P", Props(("MCID", new OxOperand.Integer(4)))));
        Assert.Equal(4, extractor.CurrentMcid);

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict(
            "Span", Props(("MCID", new OxOperand.Integer(9)))));
        Assert.Equal(9, extractor.CurrentMcid);
        Show(extractor, "inner");

        // Marked content nests, so text after the inner EMC still belongs to MCID 4.
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);
        Assert.Equal(4, extractor.CurrentMcid);
        Show(extractor, "outer");

        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);
        Assert.Null(extractor.CurrentMcid);

        Assert.Equal(new[] { "inner", "outer" }, SpanTexts(extractor));
        Assert.Equal(9, extractor.Spans[0].Mcid);
        Assert.Equal(4, extractor.Spans[1].Mcid);
    }

    [Fact]
    public void AnInlineActualTextReplacesTheSequenceExactlyOnce()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict(
            "Span",
            Props(
                ("MCID", new OxOperand.Integer(2)),
                ("ActualText", new OxOperand.Str(Ascii("ffi"))))));

        // Two showing operators inside one sequence, whose glyph codes carry no useful
        // mapping of their own.
        Show(extractor, "\x01");
        Show(extractor, "\x02");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        // §14.9.4: the replacement stands for the whole sequence, so it appears once and the
        // raw glyph codes never appear at all.
        var span = Assert.Single(extractor.Spans);
        Assert.Equal("ffi", span.Text);
        Assert.Equal(2, span.Mcid);

        // The MCID is recorded so a struct-tree /ActualText cannot override it later.
        Assert.Contains(2, extractor.TakeMcActualTextMcids());
    }

    [Fact]
    public void AnActualTextRunStillAdvancesSoLaterTextLandsCorrectly()
    {
        var extractor = NewExtractor();
        extractor.ExecuteOperatorPublic(new OxOperator.Tm(1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 500.0f));

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict(
            "Span", Props(("ActualText", new OxOperand.Str(Ascii("fi"))))));
        Show(extractor, "\x01\x02");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        Show(extractor, "X");
        extractor.FlushPublic();

        // The suppressed glyphs still moved the text matrix: two default advances of
        // 500/1000 em at 12 pt.
        Assert.Equal(new[] { "fi", "X" }, SpanTexts(extractor));
        Assert.Equal(12.0f, extractor.Spans[1].Bbox.X, 3);
    }

    [Fact]
    public void AnExcludedOcgLayerSuppressesTheTextInsideIt()
    {
        var builder = new PdfBuilder();
        int ocg = builder.AddObject("<</Type/OCG/Name(Layer A)>>");
        var doc = builder.Open();

        var extractor = NewExtractor();
        extractor.SetDocument(doc);
        extractor.SetResources(new PdfDict
        {
            Map =
            {
                ["Properties"] = new PdfDict { Map = { ["MC0"] = new PdfRef(ocg, 0) } },
            },
        });
        extractor.SetExcludedLayers(new[] { "Layer A" });

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict("OC", new OxOperand.Name("MC0")));
        Assert.True(extractor.IsContentSuppressed());
        Show(extractor, "hidden");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        Assert.False(extractor.IsContentSuppressed());
        Show(extractor, "visible");
        extractor.FlushPublic();

        Assert.Equal(new[] { "visible" }, SpanTexts(extractor));
    }

    [Fact]
    public void AnOcmdWhoseOnlyGroupIsExcludedIsHiddenUnderTheDefaultPolicy()
    {
        var builder = new PdfBuilder();
        int ocg = builder.AddObject("<</Type/OCG/Name(Layer A)>>");
        int ocmd = builder.AddObject($"<</Type/OCMD/OCGs[{ocg} 0 R]>>");
        var doc = builder.Open();

        var extractor = NewExtractor();
        extractor.SetDocument(doc);
        extractor.SetResources(new PdfDict
        {
            Map =
            {
                ["Properties"] = new PdfDict { Map = { ["MC0"] = new PdfRef(ocmd, 0) } },
            },
        });
        extractor.SetExcludedLayers(new[] { "Layer A" });

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict("OC", new OxOperand.Name("MC0")));

        // /P defaults to AnyOn, and the one group it names is off.
        Assert.True(extractor.IsContentSuppressed());
    }

    [Fact]
    public void ALayerThatWasNotExcludedLeavesContentAlone()
    {
        var builder = new PdfBuilder();
        int ocg = builder.AddObject("<</Type/OCG/Name(Layer B)>>");
        var doc = builder.Open();

        var extractor = NewExtractor();
        extractor.SetDocument(doc);
        extractor.SetResources(new PdfDict
        {
            Map =
            {
                ["Properties"] = new PdfDict { Map = { ["MC0"] = new PdfRef(ocg, 0) } },
            },
        });
        extractor.SetExcludedLayers(new[] { "Layer A" });

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContentDict("OC", new OxOperand.Name("MC0")));
        Assert.False(extractor.IsContentSuppressed());

        Show(extractor, "kept");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);
        Assert.Equal(new[] { "kept" }, SpanTexts(extractor));
    }

    [Fact]
    public void ReversedCharsIsRememberedForTheWholePage()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContent("ReversedChars"));
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        // The flag outlives the scope: the merger must not add geometric word spaces anywhere
        // on a page whose RTL glyphs were drawn individually (§14.8.2.3.3).
        Assert.True(extractor.SawReversedChars);
    }

    // ---- the adaptive TJ threshold ----------------------------------------------

    [Fact]
    public void TheStaticThresholdIsUsedWhileAdaptiveThresholdsAreOff()
    {
        var extractor = NewExtractor(OxTextExtractionConfig.WithSpaceThreshold(-90.0f));

        Assert.Equal(-90.0f, extractor.CalculateAdaptiveTjThreshold());
    }

    [Fact]
    public void TheAdaptiveThresholdScalesWithTheFontsSpaceAdvance()
    {
        var extractor = NewExtractor(
            OxTextExtractionConfig.New().SetAdaptiveTjThreshold(true).SetWordMarginRatio(0.1f));
        extractor.AddFont("F1", SpaceWidthFont(500.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 10.0f));

        // -(space × size × ratio) / 1000, the font units being 1/1000 em.
        Assert.Equal(-0.5f, extractor.CalculateAdaptiveTjThreshold(), 4);
    }

    [Fact]
    public void AnUndeclaredFontFallsBackToTheTimesRomanSpaceWidth()
    {
        var extractor = NewExtractor(
            OxTextExtractionConfig.New().SetAdaptiveTjThreshold(true).SetWordMarginRatio(0.1f));
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("Missing", 10.0f));

        Assert.Equal(-0.25f, extractor.CalculateAdaptiveTjThreshold(), 4);
    }

    [Fact]
    public void AJustifiedOffsetDistributionTriplesTheMargin()
    {
        var extractor = NewExtractor(
            OxTextExtractionConfig.New().SetAdaptiveTjThreshold(true).SetWordMarginRatio(0.1f));
        extractor.AddFont("F1", SpaceWidthFont(500.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Tf("F1", 10.0f));

        // Justified text spreads its offsets to fill the measure, which is exactly the high
        // coefficient of variation the detector keys on.
        extractor.TjOffsetHistory.AddRange(new[] { -10.0f, -400.0f, -20.0f, -900.0f, -30.0f });

        Assert.True(extractor.AnalyzeTjDistribution().IsJustified);
        Assert.Equal(-1.5f, extractor.CalculateAdaptiveTjThreshold(), 4);
    }

    [Fact]
    public void AnEvenOffsetDistributionIsNotJustified()
    {
        var extractor = NewExtractor();
        extractor.TjOffsetHistory.AddRange(new[] { -200.0f, -205.0f, -198.0f, -202.0f });

        (bool isJustified, float cv) = extractor.AnalyzeTjDistribution();
        Assert.False(isJustified);
        Assert.True(cv < 0.5f);
    }

    private static OxFontInfo SpaceWidthFont(float spaceWidth) => new()
    {
        Subtype = "Type1",
        FirstChar = 32,
        LastChar = 32,
        Widths = new[] { spaceWidth },
    };

    // ---- Form XObjects -----------------------------------------------------------

    /// <summary>A form that paints one glyph and nothing else.</summary>
    private const string SimpleFormContent = "BT /F1 12 Tf (A) Tj ET";

    /// <summary>The same, followed by a self-invocation under an unchanged CTM.</summary>
    private const string SelfInvokingFormContent = "BT /F1 12 Tf (A) Tj ET /X1 Do";

    /// <summary>The same, but each self-invocation translates the CTM first.</summary>
    private const string TranslatingSelfInvokingFormContent = "BT /F1 12 Tf (A) Tj ET 1 0 0 1 5 0 cm /X1 Do";

    private static OxTextExtractor FormExtractor(string formContent, bool selfReferencing)
    {
        var builder = new PdfBuilder();
        int font = builder.AddObject("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        string resources = selfReferencing
            ? $"/Resources<</Font<</F1 {font} 0 R>>/XObject<</X1 {font + 1} 0 R>>>>"
            : $"/Resources<</Font<</F1 {font} 0 R>>>>";
        int form = builder.AddStream(
            $"/Type/XObject/Subtype/Form/BBox[0 0 1000 1000]{resources}",
            Encoding.ASCII.GetBytes(formContent));
        var doc = builder.Open();

        var extractor = NewExtractor();
        extractor.SetDocument(doc);
        extractor.SetResources(new PdfDict
        {
            Map =
            {
                ["XObject"] = new PdfDict { Map = { ["X1"] = new PdfRef(form, 0) } },
            },
        });
        return extractor;
    }

    [Fact]
    public void AFormPaintedOnceIsWalkedOnce()
    {
        var extractor = FormExtractor(SimpleFormContent, selfReferencing: false);

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        Assert.Equal(new[] { "A" }, SpanTexts(extractor));
    }

    [Fact]
    public void TheSameFormUnderTheSameCtmIsNotWalkedTwice()
    {
        var extractor = FormExtractor(SimpleFormContent, selfReferencing: false);

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));
        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        Assert.Single(extractor.Spans);
    }

    [Fact]
    public void TheSameFormUnderADifferentCtmIsWalkedAgainAtTheNewPosition()
    {
        var extractor = FormExtractor(SimpleFormContent, selfReferencing: false);

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));
        extractor.ExecuteOperatorPublic(new OxOperator.Cm(1.0f, 0.0f, 0.0f, 1.0f, 100.0f, 40.0f));
        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        // One header form stamped at two positions is two pieces of text, not one.
        Assert.Equal(new[] { "A", "A" }, SpanTexts(extractor));
        Assert.Equal(0.0f, extractor.Spans[0].Bbox.X);
        Assert.Equal(0.0f, extractor.Spans[0].Bbox.Y);
        Assert.Equal(100.0f, extractor.Spans[1].Bbox.X);
        Assert.Equal(40.0f, extractor.Spans[1].Bbox.Y);
    }

    [Fact]
    public void AFormThatInvokesItselfUnderAnUnchangedCtmDoesNotRecurse()
    {
        var extractor = FormExtractor(SelfInvokingFormContent, selfReferencing: true);

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        Assert.Single(extractor.Spans);
    }

    [Fact]
    public void AFormThatInvokesItselfUnderAMovingCtmStopsAtTheDepthLimit()
    {
        var extractor = FormExtractor(TranslatingSelfInvokingFormContent, selfReferencing: true);

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        // Each nesting level changes the CTM, so the visited set never matches; the depth
        // limiter is the backstop that ends it, and each level lands 5 points further right.
        Assert.Equal(10, extractor.Spans.Count);
        for (int i = 0; i < extractor.Spans.Count; i++)
        {
            Assert.Equal(5.0f * i, extractor.Spans[i].Bbox.X);
        }
    }

    [Fact]
    public void AFormWhoseResourcesCannotReachTextIsSkippedWithoutDecoding()
    {
        var builder = new PdfBuilder();
        int form = builder.AddStream(
            "/Type/XObject/Subtype/Form/BBox[0 0 100 100]/Resources<</ProcSet[/PDF]>>",
            Encoding.ASCII.GetBytes(SimpleFormContent));
        var doc = builder.Open();

        var extractor = NewExtractor();
        extractor.SetDocument(doc);
        extractor.SetResources(new PdfDict
        {
            Map = { ["XObject"] = new PdfDict { Map = { ["X1"] = new PdfRef(form, 0) } } },
        });

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        // Neither /Font nor /XObject: the form can draw no text and reach no nested form.
        Assert.Empty(extractor.Spans);
    }

    [Fact]
    public void AnImageXObjectIsNotWalked()
    {
        var builder = new PdfBuilder();
        int image = builder.AddStream(
            "/Type/XObject/Subtype/Image/Width 1/Height 1",
            Encoding.ASCII.GetBytes(SimpleFormContent));
        var doc = builder.Open();

        var extractor = NewExtractor();
        extractor.SetDocument(doc);
        extractor.SetResources(new PdfDict
        {
            Map = { ["XObject"] = new PdfDict { Map = { ["X1"] = new PdfRef(image, 0) } } },
        });

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        Assert.Empty(extractor.Spans);
    }

    [Fact]
    public void AFormPushesItsOwnMcidScopeSoTwoFormsDoNotCollide()
    {
        var extractor = FormExtractor(
            "BT /F1 12 Tf ET /P <</MCID 0>> BDC (A) Tj EMC", selfReferencing: false);
        extractor.SetPageIndex(3);

        extractor.ExecuteOperatorPublic(new OxOperator.Do("X1"));

        // §14.7.4.3: an MCID inside a form belongs to the form's namespace, not the page's.
        var span = Assert.Single(extractor.Spans);
        Assert.Equal(0, span.Mcid);
        Assert.Equal(OxMcidScope.Kind.Form, span.McidScope!.Value.ScopeKind);

        // ... and the page scope is back on top once the form has been walked.
        Assert.Equal(OxMcidScope.Page(3), extractor.CurrentMcidScope());
    }

    [Fact]
    public void APageLevelRunIsStampedWithThePageScope()
    {
        var extractor = NewExtractor();
        extractor.SetPageIndex(7);

        Show(extractor, "A");
        extractor.FlushPublic();

        Assert.Equal(OxMcidScope.Page(7), Assert.Single(extractor.Spans).McidScope);
    }

    // ---- the /PlacedPDF pre-scan -------------------------------------------------

    [Fact]
    public void APageWithoutThePlacedPdfTagIsNotScanned()
    {
        Assert.False(OxTextExtractor.PlacedPdfTextDominates(Ascii("BT (hello) Tj ET")));
    }

    [Fact]
    public void ASmallPlacedRegionIsTreatedAsADecorativeFigure()
    {
        string stream = "/PlacedPDF BMC BT (" + new string('x', 200) + ") Tj ET EMC";

        // Below the 800-character floor a placed region is a logo or a caption, not a body.
        Assert.False(OxTextExtractor.PlacedPdfTextDominates(Ascii(stream)));
    }

    [Fact]
    public void APlacedRegionThatDominatesThePageIsKept()
    {
        string body = new('x', 2000);
        string stream = $"/PlacedPDF BMC BT ({body}) Tj ET EMC BT (running header) Tj ET";

        Assert.True(OxTextExtractor.PlacedPdfTextDominates(Ascii(stream)));
    }

    [Fact]
    public void APlacedRegionThatRepeatsTheSurroundingTextStaysSuppressed()
    {
        var words = new StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            words.Append("alpha beta gamma delta ");
        }
        string stream = $"/PlacedPDF BMC BT ({words}) Tj ET EMC BT ({words}) Tj ET";

        // A placed region whose words all appear outside it is a duplicate overlay.
        Assert.False(OxTextExtractor.PlacedPdfTextDominates(Ascii(stream)));
    }

    [Fact]
    public void APlacedPdfRegionSuppressesItsTextUnlessThePageSaysOtherwise()
    {
        var extractor = NewExtractor();

        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContent("PlacedPDF"));
        Show(extractor, "galley");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);
        Assert.Empty(extractor.Spans);

        // The per-page pre-scan flips this on when the placed region IS the page's body.
        extractor.PlacedPdfKeep = true;
        extractor.ExecuteOperatorPublic(new OxOperator.BeginMarkedContent("PlacedPDF"));
        Show(extractor, "article body");
        extractor.ExecuteOperatorPublic(OxOperator.EndMarkedContent.Instance);

        Assert.Equal(new[] { "article body" }, SpanTexts(extractor));
    }

    [Fact]
    public void TextDuplicationIgnoresPunctuationAndSingleCharacters()
    {
        // Single characters and punctuation carry no evidence either way.
        Assert.Equal(0.0, OxTextExtractor.TextDuplicationFraction(Ascii("a - b ."), Ascii("alpha")));

        Assert.Equal(0.5, OxTextExtractor.TextDuplicationFraction(Ascii("Alpha beta"), Ascii("ALPHA gamma")));
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

        public PdfDocument Open()
        {
            int catalog = AddObject("<</Type/Catalog>>");
            return PdfDocument.Open(Build(catalog));
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
            foreach (int off in offsets)
            {
                Append(off.ToString("D10") + " 00000 n \n");
            }
            Append($"trailer\n<</Size {_objects.Count + 1}/Root {rootObjectNumber} 0 R>>\n");
            Append($"startxref\n{xrefPos}\n%%EOF");
            return outBytes.ToArray();
        }
    }
}
