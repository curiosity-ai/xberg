using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The AsciiDoc extractor, ported from Rust <c>extractors/asciidoc.rs</c>.
/// </summary>
public class AsciiDocExtractorTests
{
    private static InternalDocument Parse(string source) =>
        new AsciiDocExtractor().Extract(Encoding.UTF8.GetBytes(source), "text/asciidoc", new ExtractionConfig());

    private static List<string> TextsOf(InternalDocument doc, ElementKindTag tag) =>
        doc.Elements.Where(e => e.Kind.Tag == tag).Select(e => e.Text).ToList();

    [Fact]
    public void TheHeaderYieldsTitleAuthorsAndAttributes()
    {
        var doc = Parse("""
            = Marine Estimation
            Ada Lovelace <ada@example.com>; Alan Turing
            :revnumber: 2.1
            :stem:

            Body text.
            """);

        Assert.Equal("Marine Estimation", doc.Metadata.Title);
        Assert.Equal(new[] { "Ada Lovelace <ada@example.com>", "Alan Turing" }, doc.Metadata.Authors);
        Assert.Equal("2.1", doc.Metadata.Additional["asciidoc_revnumber"].GetString());
    }

    [Fact]
    public void AnAttributeReferenceIsSubstitutedIntoBodyText()
    {
        var doc = Parse("""
            = Doc
            :product: Widget

            Install {product} first.
            """);
        Assert.Contains("Install Widget first.", TextsOf(doc, ElementKindTag.Paragraph));
    }

    [Fact]
    public void AnUndefinedAttributeReferenceStaysVerbatim()
    {
        // AsciiDoc treats an unresolved reference as literal text; dropping it loses content.
        var doc = Parse("= Doc\n\nSee {nosuchthing} here.\n");
        Assert.Contains("See {nosuchthing} here.", TextsOf(doc, ElementKindTag.Paragraph));
    }

    [Fact]
    public void SectionMarkersBecomeHeadingsAtTheirOwnLevel()
    {
        var doc = Parse("== Top\n\ntext\n\n==== Deep\n\nmore\n");
        var headings = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading).ToList();
        Assert.Equal(2, headings.Count);
        Assert.Equal(2, headings[0].Kind.Level);
        Assert.Equal(4, headings[1].Kind.Level);
    }

    [Fact]
    public void ASourceBlockKeepsItsLanguageAndItsLinesVerbatim()
    {
        var doc = Parse("""
            [source,rust]
            ----
            fn main() {
                println!("hi");
            }
            ----
            """);
        var code = doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Code);
        Assert.Equal("rust", code.Attributes!["language"]);
        Assert.Contains("println!(\"hi\");", code.Text);
        // Indentation is content in a listing block.
        Assert.Contains("    println!", code.Text);
    }

    [Fact]
    public void BothAdmonitionFormsProduceAnAdmonition()
    {
        var inline = Parse("NOTE: Mind the gap.\n");
        var block = Parse("[WARNING]\n====\nDo not do that.\n====\n");

        Assert.Equal("Mind the gap.", TextsOf(inline, ElementKindTag.Admonition).Single());
        Assert.Equal("Do not do that.", TextsOf(block, ElementKindTag.Admonition).Single());
    }

    [Fact]
    public void ATableBecomesStructuredCellsRatherThanPipeNoise()
    {
        var doc = Parse("""
            |===
            | Name | Value

            | alpha | 1
            | beta | 2
            |===
            """);
        var table = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Name", "Value" }, table.Cells[0]);
        Assert.Equal(new[] { "alpha", "1" }, table.Cells[1]);
        Assert.Equal(new[] { "beta", "2" }, table.Cells[2]);
    }

    [Fact]
    public void ACellsTextMayRunOverSeveralLines()
    {
        var doc = Parse("""
            |===
            | Term | Meaning

            | drift
            | Slow accumulation
            of error.
            |===
            """);
        var table = Assert.Single(doc.Tables);
        Assert.Equal("Slow accumulation of error.", table.Cells[1][1]);
    }

    [Fact]
    public void ListsCarryTheirNestingAndTheirOrderedness()
    {
        var doc = Parse("""
            * one
            ** nested
            * two

            . first
            . second
            """);
        var starts = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.ListStart).ToList();
        Assert.Equal(3, starts.Count);
        Assert.False(starts[0].Kind.Ordered);
        Assert.False(starts[1].Kind.Ordered);
        Assert.True(starts[2].Kind.Ordered);
    }

    [Fact]
    public void ConstrainedSpansAnnotateButMidwordMarkersDoNot()
    {
        var doc = Parse("A *bold* word and mid*word*markers.\n");
        var paragraph = doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Paragraph);

        // The constrained span's markers are stripped; the midword ones stay literal, which is
        // what makes them literal rather than emphasis.
        Assert.Equal("A bold word and mid*word*markers.", paragraph.Text);
        var bold = Assert.Single(paragraph.Annotations);
        Assert.Equal(AnnotationKind.Tag.Bold, bold.Kind.Which);
        Assert.Equal("bold", Encoding.UTF8.GetString(
            Encoding.UTF8.GetBytes(paragraph.Text)[(int)bold.Start..(int)bold.End]));
    }

    [Fact]
    public void LinkMacrosAndBareUrlsBecomeLinkAnnotations()
    {
        var doc = Parse("See link:https://example.org/docs[the docs] and https://example.com/plain.\n");
        var paragraph = doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Paragraph);

        Assert.Equal("See the docs and https://example.com/plain.", paragraph.Text);
        var urls = paragraph.Annotations.Select(a => a.Kind.Url).ToList();
        Assert.Equal(new[] { "https://example.org/docs", "https://example.com/plain" }, urls);
    }

    [Fact]
    public void AnUnterminatedBlockIsClosedAtEndOfInputAndReported()
    {
        // Malformed input must degrade rather than fail: the body is still content.
        var doc = Parse("----\nunclosed listing\n");
        Assert.Contains("unclosed listing", doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Code).Text);
        Assert.Contains(doc.ProcessingWarnings, w => w.Message.Contains("unterminated"));
    }

    [Fact]
    public void AnInlineMathMacroReachesTheTextAsDelimitedLatex()
    {
        // Inline math stays in the sentence, as it does for markdown, but reaches the text as
        // delimited LaTeX rather than as the raw macro.
        Assert.Equal(new[] { "The value $E = mc^2$ holds." },
            TextsOf(Parse("The value latexmath:[E = mc^2] holds.\n"), ElementKindTag.Paragraph));
        Assert.Empty(TextsOf(Parse("The value latexmath:[E = mc^2] holds.\n"), ElementKindTag.Formula));
    }

    [Fact]
    public void AnInlineStemMacroIsConvertedFromAsciiMath()
    {
        Assert.Equal(new[] { "Take $\\sqrt{4}$ as given." },
            TextsOf(Parse("Take stem:[sqrt(4)] as given.\n"), ElementKindTag.Paragraph));
        Assert.Equal(new[] { "The tuples $B_{\\text{FROM}}^{\\text{out}}$ are output." },
            TextsOf(Parse("The tuples stem:[B_\"FROM\"^\"out\"] are output.\n"), ElementKindTag.Paragraph));
    }

    [Fact]
    public void AMathMacroMayHoldBracketsOfItsOwn()
    {
        Assert.Equal(new[] { "Given $a[i] + b$ throughout." },
            TextsOf(Parse("Given latexmath:[a[i] + b] throughout.\n"), ElementKindTag.Paragraph));
    }

    [Fact]
    public void ALatexMathBlockBecomesAFormula()
    {
        Assert.Equal(new[] { "\\int_0^1 x\\,dx = \\frac{1}{2}" },
            TextsOf(Parse("[latexmath]\n++++\n\\int_0^1 x\\,dx = \\frac{1}{2}\n++++\n"),
                    ElementKindTag.Formula));
    }

    [Fact]
    public void AStemBlockIsAsciiMathUnlessTheDocumentSaysOtherwise()
    {
        // `stem` follows the document's `:stem:` attribute, which AsciiDoc defines as AsciiMath
        // unless the document names `latexmath`. An `[asciimath]` block names its own notation
        // whatever that attribute says.
        Assert.Equal(new[] { "\\sqrt{4}=2" },
            TextsOf(Parse("[stem]\n++++\nsqrt(4) = 2\n++++\n"), ElementKindTag.Formula));
        Assert.Equal(new[] { "\\sqrt{4}=2" },
            TextsOf(Parse("= Doc\n:stem: latexmath\n\n[asciimath]\n++++\nsqrt(4) = 2\n++++\n"),
                    ElementKindTag.Formula));
        Assert.Equal(new[] { "\\alpha + \\beta" },
            TextsOf(Parse("= Doc\n:stem: latexmath\n\n[stem]\n++++\n\\alpha + \\beta\n++++\n"),
                    ElementKindTag.Formula));
    }

    [Fact]
    public void APassthroughBlockWithNoMathAttributeIsNotMath()
    {
        Assert.Empty(TextsOf(Parse("++++\n<hr/>\n++++\n"), ElementKindTag.Formula));
    }
}
