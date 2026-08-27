using Xberg.Extractors;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Character-level normalization of the legacy <c>.doc</c> text stream, ported from the Rust
/// <c>extraction::doc</c> tests. Covers upstream <c>fix(doc): drop field instructions instead of
/// emitting them as text</c> (GH#1460) and <c>fix(doc): keep the non-breaking hyphen instead of
/// deleting it</c>.
/// </summary>
public sealed class DocFieldCodeTests
{
    [Fact]
    public void OnlyTheFieldResultSurvivesTheInstruction()
    {
        // The instruction between BEGIN and SEPARATOR is markup; only the result survives.
        Assert.Equal("AresultB", DocExtractor.NormalizeDocText("A\u0013FIELD\u0014result\u0015B"));
    }

    [Fact]
    public void HyperlinkInstructionIsDroppedAndItsResultKept()
    {
        const string text = "See \u0013 HYPERLINK \"http://example.com/spec\" \\o \"Spec\" \u0014the specification\u0015 for details.";
        Assert.Equal("See the specification for details.", DocExtractor.NormalizeDocText(text));
    }

    [Fact]
    public void NestedPagerefFieldsInsideATocFieldAreStripped()
    {
        // A TOC field whose result contains PAGEREF fields, exactly as Word writes it.
        const string text = "\u0013 TOC \\o \"1-3\" \\h \\z \\u \u0014" +
                            "\u0013 PAGEREF _Toc101 \\h \u00141\u0015\tIntroduction\n" +
                            "\u0013 PAGEREF _Toc102 \\h \u00142\u0015\tMethods\n" +
                            "\u0015" +
                            "Body text.";
        Assert.Equal("1\tIntroduction\n2\tMethods\nBody text.", DocExtractor.NormalizeDocText(text));
    }

    [Fact]
    public void TextAfterAnUnterminatedFieldBeginIsKept()
    {
        // BEGIN with no END at all: treated as inert so the document tail is never lost.
        Assert.Equal("Intro.\nPAGEREF _Toc1 \\h", DocExtractor.NormalizeDocText("Intro.\n\u0013PAGEREF _Toc1 \\h \u0014"));
    }

    [Fact]
    public void AStrayFieldEndWithoutABeginIsIgnored()
    {
        Assert.Equal("BeforeAfter", DocExtractor.NormalizeDocText("Before\u0015After"));
        // Unbalanced END markers must not underflow the field stack.
        Assert.Equal("7Tail", DocExtractor.NormalizeDocText("\u0015\u0013 SEQ Figure \\* ARABIC \u00147\u0015\u0015Tail"));
    }

    [Fact]
    public void ATerminatedFieldWithoutASeparatorContributesNoText()
    {
        // BEGIN..END with no SEPARATOR: the field has no result, so there is nothing to emit.
        Assert.Equal("AB", DocExtractor.NormalizeDocText("A\u0013 SEQ Figure \\* MERGEFORMAT \u0015B"));
    }

    [Fact]
    public void TheNonBreakingHyphenIsVisibleTextAndSurvives()
    {
        // 0x1E is a hyphen the reader SEES; dropping it welds the compound together.
        Assert.Equal(
            "Section twenty‑one of the sub‑section",
            DocExtractor.NormalizeDocText("Section twenty\u001Eone of the sub\u001Esection"));
    }

    [Fact]
    public void TheOptionalHyphenStaysDiscardedWhileTheNonBreakingOneSurvives()
    {
        // The two are one byte apart and must stay on opposite sides of the line: 0x1E is always
        // rendered, 0x1F only when the line breaks there.
        Assert.Equal(
            "self‑contained extraordinary",
            DocExtractor.NormalizeDocText("self\u001Econtained extra\u001Fordinary"));
    }

    [Fact]
    public void TheHyphenMappingAppliesToTextKeptFromAFieldResult()
    {
        // Field-code stripping runs before character mapping; a cross-reference result such as a
        // clause number must keep its hyphen.
        Assert.Equal(
            "See clause 3‑4.",
            DocExtractor.NormalizeDocText("See \u0013 REF _Ref1 \\h \u0014clause 3\u001E4\u0015."));
    }
}
