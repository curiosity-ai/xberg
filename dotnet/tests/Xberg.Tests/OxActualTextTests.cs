using System.Collections.Generic;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Structure;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Ports pdf_oxide's struct-tree `/ActualText` pipeline: `structure/parser.rs`,
/// `build_actualtext_index` / `walk_actualtext` in `structure/traversal.rs`, and
/// `actualtext_actions_for_page` / `apply_actualtext_to_spans` in `document.rs`.
/// </summary>
public class OxActualTextTests
{
    // ── the text-string decoder (structure/parser.rs:102) ───────────────────────

    [Fact]
    public void AUtf16BigEndianBomDecodesAsUtf16()
    {
        byte[] bytes = { 0xFE, 0xFF, 0x00, (byte)'h', 0x00, (byte)'i' };
        Assert.Equal("hi", OxStructTree.DecodePdfTextString(bytes));
    }

    [Fact]
    public void AUtf16LittleEndianBomDecodesAsUtf16()
    {
        byte[] bytes = { 0xFF, 0xFE, (byte)'h', 0x00, (byte)'i', 0x00 };
        Assert.Equal("hi", OxStructTree.DecodePdfTextString(bytes));
    }

    [Fact]
    public void ATrailingOddByteAfterAUtf16BomIsDropped()
    {
        byte[] bytes = { 0xFE, 0xFF, 0x00, (byte)'h', 0x00 };
        Assert.Equal("h", OxStructTree.DecodePdfTextString(bytes));
    }

    [Fact]
    public void BytesWithoutABomDecodeAsPdfDocEncoding()
    {
        // 0x92 is U+2122 in PDFDocEncoding (§D.2) and invalid UTF-8: the structure parser
        // has no UTF-8 guess, so the byte must take the PDFDocEncoding meaning.
        byte[] bytes = { (byte)'a', 0x92, (byte)'b' };
        Assert.Equal("a™b", OxStructTree.DecodePdfTextString(bytes));
    }

    // ── the §14.9.4 conformance gate (document.rs:10847) ────────────────────────

    [Fact]
    public void AReplacementThatSwallowsLettersWhileCarryingNoneIsDeclined()
    {
        Assert.True(OxActualText.ActualTextIsDestructive(" ", "word"));
        Assert.True(OxActualText.ActualTextIsDestructive("-", "A1"));
    }

    [Fact]
    public void AnAlphanumericReplacementIsAccepted()
    {
        Assert.False(OxActualText.ActualTextIsDestructive("k-", "c"));
        Assert.False(OxActualText.ActualTextIsDestructive("fi", "ﬁ"));
    }

    [Fact]
    public void ANonAlphanumericReplacementOverNonAlphanumericGlyphsIsAccepted()
    {
        // Nothing alphanumeric is lost, so the gate has no reason to fire.
        Assert.False(OxActualText.ActualTextIsDestructive(" ", "  "));
    }

    // ── index construction (structure/traversal.rs:578) ─────────────────────────

    private static OxStructElem Elem(string? actualText, params OxStructChild[] children)
    {
        var e = new OxStructElem { ActualText = actualText };
        e.Children.AddRange(children);
        return e;
    }

    private static OxStructChild Mcr(int mcid, int page) =>
        new OxStructChild.Mcr(mcid, page, OxMcidScope.Page(page));

    private static OxStructTreeRoot TreeOf(params OxStructElem[] roots)
    {
        var t = new OxStructTreeRoot();
        t.RootElements.AddRange(roots);
        return t;
    }

    [Fact]
    public void EveryMarkedContentReferenceUnderAScopeIsCovered()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem("replacement", Mcr(0, 0), Mcr(1, 0))));

        Assert.Contains((OxMcidScope.Page(0), 0), idx.CoveredMcids);
        Assert.Contains((OxMcidScope.Page(0), 1), idx.CoveredMcids);
        Assert.Equal("replacement", idx.McidToActualText[(OxMcidScope.Page(0), 0)]);
        Assert.Equal("replacement", idx.McidToActualText[(OxMcidScope.Page(0), 1)]);
    }

    [Fact]
    public void ADescendantScopeOverridesItsAncestorForItsOwnSubtree()
    {
        var idx = OxActualText.BuildIndex(TreeOf(
            Elem("outer", Mcr(0, 0), new OxStructChild.Elem(Elem("inner", Mcr(1, 0))))));

        Assert.Equal("outer", idx.McidToActualText[(OxMcidScope.Page(0), 0)]);
        Assert.Equal("inner", idx.McidToActualText[(OxMcidScope.Page(0), 1)]);
    }

    [Fact]
    public void AScopeWithNoMarkedContentAnywhereBelowItIsDropped()
    {
        // Nothing to attach the replacement to, so it must not leak into a sibling.
        var idx = OxActualText.BuildIndex(TreeOf(Elem("orphan")));
        Assert.True(idx.IsEmpty);
    }

    [Fact]
    public void APageScopedSubtreeEmitsOnItsFirstPageAndSuppressesTheRest()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem("spans two pages", Mcr(0, 3), Mcr(1, 4))));

        Assert.Equal("spans two pages", idx.McidToActualText[(OxMcidScope.Page(3), 0)]);
        Assert.DoesNotContain((OxMcidScope.Page(4), 1), (IReadOnlyCollection<(OxMcidScope, int)>)idx.McidToActualText.Keys);
        Assert.Contains((OxMcidScope.Page(4), 1), idx.SuppressOnly);
    }

    [Fact]
    public void AFormScopedReferenceEmitsAtItsOwnAnchor()
    {
        // Each Form XObject owns its MCID namespace (§14.7.4.3), so the emit-once-per-page
        // rule does not apply to it.
        var form = new OxStructChild.Mcr(0, 0, OxMcidScope.Form(7, 0));
        var idx = OxActualText.BuildIndex(TreeOf(Elem("in a form", Mcr(0, 0), form)));

        Assert.Equal("in a form", idx.McidToActualText[(OxMcidScope.Page(0), 0)]);
        Assert.Equal("in a form", idx.McidToActualText[(OxMcidScope.Form(7, 0), 0)]);
        Assert.Empty(idx.SuppressOnly);
    }

    // ── per-page actions (document.rs:10852) ────────────────────────────────────

    private static Dictionary<(OxMcidScope, int), StringBuilder> Glyphs(
        params ((OxMcidScope Scope, int Mcid) Key, string Text)[] entries)
    {
        var map = new Dictionary<(OxMcidScope, int), StringBuilder>();
        foreach (var (key, text) in entries) map[key] = new StringBuilder(text);
        return map;
    }

    [Fact]
    public void ARunOfEqualReplacementsEmitsOnceAndSuppressesTheRest()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem("Chapter", Mcr(0, 0), Mcr(1, 0), Mcr(2, 0))));
        var order = new[]
        {
            (OxMcidScope.Page(0), 0), (OxMcidScope.Page(0), 1), (OxMcidScope.Page(0), 2),
        };
        var visible = new HashSet<(OxMcidScope, int)>(order);

        var actions = OxActualText.ActionsForPage(
            idx, order, visible, new HashSet<int>(),
            Glyphs(((OxMcidScope.Page(0), 0), "Chap"), ((OxMcidScope.Page(0), 1), "ter")));

        Assert.Equal("Chapter", actions[(OxMcidScope.Page(0), 0)].Replacement);
        Assert.Null(actions[(OxMcidScope.Page(0), 1)].Replacement);
        Assert.Null(actions[(OxMcidScope.Page(0), 2)].Replacement);
    }

    [Fact]
    public void TheRunEmitsAtItsFirstVisibleKey()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem("Chapter", Mcr(0, 0), Mcr(1, 0))));
        var order = new[] { (OxMcidScope.Page(0), 0), (OxMcidScope.Page(0), 1) };
        // MCID 0 drew nothing that survived the upstream filters.
        var visible = new HashSet<(OxMcidScope, int)> { (OxMcidScope.Page(0), 1) };

        var actions = OxActualText.ActionsForPage(
            idx, order, visible, new HashSet<int>(), Glyphs(((OxMcidScope.Page(0), 1), "Chapter")));

        Assert.Null(actions[(OxMcidScope.Page(0), 0)].Replacement);
        Assert.Equal("Chapter", actions[(OxMcidScope.Page(0), 1)].Replacement);
    }

    [Fact]
    public void AnInStreamReplacementKeepsItsMcidOutOfTheStructTreeScope()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem("outer", Mcr(0, 0), Mcr(1, 0))));
        var order = new[] { (OxMcidScope.Page(0), 0), (OxMcidScope.Page(0), 1) };
        var visible = new HashSet<(OxMcidScope, int)>(order);

        var actions = OxActualText.ActionsForPage(
            idx, order, visible, new HashSet<int> { 0 },
            Glyphs(((OxMcidScope.Page(0), 0), "a"), ((OxMcidScope.Page(0), 1), "b")));

        Assert.False(actions.ContainsKey((OxMcidScope.Page(0), 0)));
        Assert.Equal("outer", actions[(OxMcidScope.Page(0), 1)].Replacement);
    }

    [Fact]
    public void ADestructiveReplacementLeavesTheRunUntouched()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem(" ", Mcr(0, 0))));
        var order = new[] { (OxMcidScope.Page(0), 0) };
        var visible = new HashSet<(OxMcidScope, int)>(order);

        var actions = OxActualText.ActionsForPage(
            idx, order, visible, new HashSet<int>(), Glyphs(((OxMcidScope.Page(0), 0), "word")));

        Assert.Empty(actions);
    }

    [Fact]
    public void AKeyThatNoScopeCoversGetsNoAction()
    {
        var idx = OxActualText.BuildIndex(TreeOf(Elem("covered", Mcr(0, 0))));
        var order = new[] { (OxMcidScope.Page(0), 0), (OxMcidScope.Page(0), 9) };
        var visible = new HashSet<(OxMcidScope, int)>(order);

        var actions = OxActualText.ActionsForPage(
            idx, order, visible, new HashSet<int>(), Glyphs(((OxMcidScope.Page(0), 0), "x")));

        Assert.False(actions.ContainsKey((OxMcidScope.Page(0), 9)));
    }

    // ── the parser, over a real document ────────────────────────────────────────

    private static byte[] BuildTaggedDocument(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xrefPos = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n");
        sb.Append($"0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void TheParserReadsActualTextAndResolvesPgToAPageIndex()
    {
        var doc = PdfDocument.Open(BuildTaggedDocument(
            "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 4 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>",
            "<</Type/StructTreeRoot/K 5 0 R>>",
            "<</Type/StructElem/S/P/Pg 3 0 R/ActualText(fi)/K[0 1]>>"));

        var idx = OxActualText.Index(doc);

        Assert.NotNull(idx);
        Assert.Equal("fi", idx!.McidToActualText[(OxMcidScope.Page(0), 0)]);
        Assert.Equal("fi", idx.McidToActualText[(OxMcidScope.Page(0), 1)]);
        Assert.Equal(
            new[] { (OxMcidScope.Page(0), 0), (OxMcidScope.Page(0), 1) },
            OxActualText.McidOrderForPage(doc, 0));
    }

    [Fact]
    public void AnUntaggedDocumentHasNoIndex()
    {
        var doc = PdfDocument.Open(BuildTaggedDocument(
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>"));

        Assert.Null(OxActualText.Index(doc));
    }

    [Fact]
    public void ATaggedDocumentWithoutAnyActualTextHasNoIndex()
    {
        var doc = PdfDocument.Open(BuildTaggedDocument(
            "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 4 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>",
            "<</Type/StructTreeRoot/K 5 0 R>>",
            "<</Type/StructElem/S/P/Pg 3 0 R/K[0]>>"));

        Assert.Null(OxActualText.Index(doc));
    }

    [Fact]
    public void AMarkedContentReferenceDictionaryCarriesItsOwnPage()
    {
        var doc = PdfDocument.Open(BuildTaggedDocument(
            "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 5 0 R>>",
            "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>",
            "<</Type/StructTreeRoot/K 6 0 R>>",
            "<</Type/StructElem/S/P/Pg 3 0 R/ActualText(x)/K[<</Type/MCR/Pg 4 0 R/MCID 7>>]>>"));

        var idx = OxActualText.Index(doc);

        Assert.NotNull(idx);
        Assert.Equal("x", idx!.McidToActualText[(OxMcidScope.Page(1), 7)]);
        Assert.Empty(OxActualText.McidOrderForPage(doc, 0));
        Assert.Equal(new[] { (OxMcidScope.Page(1), 7) }, OxActualText.McidOrderForPage(doc, 1));
    }

    [Fact]
    public void SuppressedSpansAreDroppedAndTheReplacementLandsOnTheFirstOne()
    {
        var doc = PdfDocument.Open(BuildTaggedDocument(
            "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 4 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>",
            "<</Type/StructTreeRoot/K 5 0 R>>",
            "<</Type/StructElem/S/P/Pg 3 0 R/ActualText(office)/K[0 1]>>"));

        var spans = new List<OxTextSpan>
        {
            new() { Text = "of", Mcid = 0, McidScope = OxMcidScope.Page(0) },
            new() { Text = "ﬁce", Mcid = 1, McidScope = OxMcidScope.Page(0) },
            new() { Text = " tail", Mcid = 2, McidScope = OxMcidScope.Page(0) },
        };

        OxActualText.ApplyToSpans(doc, 0, spans, new HashSet<int>());

        Assert.Equal(new[] { "office", " tail" }, spans.ConvertAll(s => s.Text));
    }
}
