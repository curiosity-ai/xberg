using Xberg.Internal.MathMarkup;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// MathML → LaTeX conversion (`extraction/mathml.rs` + `extraction/math_symbols.rs`).
/// Expectations mirror the Rust unit tests one for one.
/// </summary>
public class MathMlTests
{
    private static string Convert(string inner) =>
        MathMl.ConvertMathmlStrToLatex($"<math xmlns=\"http://www.w3.org/1998/Math/MathML\">{inner}</math>");

    // ── leaves ──────────────────────────────────────────────────────────────

    [Fact]
    public void MiRendersPlainText() => Assert.Equal("x", Convert("<mi>x</mi>"));

    [Fact]
    public void MnRendersNumber() => Assert.Equal("42", Convert("<mn>42</mn>"));

    [Fact]
    public void MoMapsUnicodeOperator() => Assert.Equal("\\times ", Convert("<mo>×</mo>"));

    [Fact]
    public void MsRendersStringLiteral() => Assert.Equal("abc", Convert("<ms>abc</ms>"));

    [Fact]
    public void NumericCharRefWithTrailingCommentIsNotDuplicated()
    {
        // Real-world MathML annotates a numeric character reference with a same-content XML
        // comment: `<mo>&#x222B;<!-- ∫ --></mo>`. The comment must not render a second time.
        Assert.Equal("\\int ", Convert("<mo>&#x222B;<!-- ∫ --></mo>"));
        Assert.Equal("\\infty ", Convert("<mi mathvariant=\"normal\">&#x221E;<!-- ∞ --></mi>"));
    }

    [Fact]
    public void MtextWrapsInTextCommand() => Assert.Equal("\\text{hello world}", Convert("<mtext>hello world</mtext>"));

    [Fact]
    public void MrowConcatenatesChildren() =>
        Assert.Equal("x+y", Convert("<mrow><mi>x</mi><mo>+</mo><mi>y</mi></mrow>"));

    // ── layout schemata ─────────────────────────────────────────────────────

    [Fact]
    public void MfracRendersFraction() => Assert.Equal("\\frac{1}{2}", Convert("<mfrac><mn>1</mn><mn>2</mn></mfrac>"));

    [Fact]
    public void MsupRendersSuperscript() => Assert.Equal("x^{2}", Convert("<msup><mi>x</mi><mn>2</mn></msup>"));

    [Fact]
    public void MsubRendersSubscript() => Assert.Equal("a_{n}", Convert("<msub><mi>a</mi><mi>n</mi></msub>"));

    [Fact]
    public void MsubsupRendersBothScripts() =>
        Assert.Equal("x_{i}^{2}", Convert("<msubsup><mi>x</mi><mi>i</mi><mn>2</mn></msubsup>"));

    [Fact]
    public void MsqrtRendersRoot() => Assert.Equal("\\sqrt{x}", Convert("<msqrt><mi>x</mi></msqrt>"));

    [Fact]
    public void MrootRendersDegree() => Assert.Equal("\\sqrt[3]{x}", Convert("<mroot><mi>x</mi><mn>3</mn></mroot>"));

    [Fact]
    public void MspaceRendersAsSpace() =>
        Assert.Equal("a b", Convert("<mrow><mi>a</mi><mspace/><mi>b</mi></mrow>"));

    [Fact]
    public void MphantomKeepsItsSpace() => Assert.Equal("\\phantom{x}", Convert("<mphantom><mi>x</mi></mphantom>"));

    [Fact]
    public void MtableRendersMatrix()
    {
        string latex = Convert(
            "<mtable><mtr><mtd><mn>1</mn></mtd><mtd><mn>2</mn></mtd></mtr>" +
            "<mtr><mtd><mn>3</mn></mtd><mtd><mn>4</mn></mtd></mtr></mtable>");
        Assert.Equal("\\begin{matrix}1 & 2 \\\\ 3 & 4\\end{matrix}", latex);
    }

    [Fact]
    public void UnknownElementDegradesToTextContent() =>
        Assert.Equal("42", Convert("<mlongdiv><mn>42</mn></mlongdiv>"));

    // ── fences ──────────────────────────────────────────────────────────────

    [Fact]
    public void MfencedDefaultsToParens() => Assert.Equal("\\left(x\\right)", Convert("<mfenced><mi>x</mi></mfenced>"));

    [Fact]
    public void MfencedBracketsSeparateElements() =>
        Assert.Equal("\\left[a,b\\right]", Convert("<mfenced open=\"[\" close=\"]\"><mi>a</mi><mi>b</mi></mfenced>"));

    [Fact]
    public void MfencedNormDelimiters() =>
        Assert.Equal("\\left\\|x\\right\\|", Convert("<mfenced open=\"&#x2225;\" close=\"&#x2225;\"><mi>x</mi></mfenced>"));

    [Fact]
    public void MfencedAngleDelimitersDoNotGlue() =>
        Assert.Equal("\\left\\langle A\\right\\rangle ",
            Convert("<mfenced open=\"&#x27E8;\" close=\"&#x27E9;\"><mi>A</mi></mfenced>"));

    [Fact]
    public void MfencedWithOperatorChildrenDropsSeparators() =>
        Assert.Equal("\\left(1-x\\right)", Convert("<mfenced><mn>1</mn><mo>-</mo><mi>x</mi></mfenced>"));

    // ── scripts and accents ─────────────────────────────────────────────────

    [Fact]
    public void MunderRendersUnderset() =>
        Assert.Equal("\\underset{n}{lim}", Convert("<munder><mi>lim</mi><mi>n</mi></munder>"));

    [Fact]
    public void MoverHatAccent() => Assert.Equal("\\hat{x}", Convert("<mover><mi>x</mi><mo>^</mo></mover>"));

    [Fact]
    public void MoverAccentFamily()
    {
        Assert.Equal("\\tilde{x}", Convert("<mover><mi>x</mi><mo>˜</mo></mover>"));
        Assert.Equal("\\dot{q}", Convert("<mover><mi>q</mi><mo>˙</mo></mover>"));
        Assert.Equal("\\bar{y}", Convert("<mover><mi>y</mi><mo>¯</mo></mover>"));
        Assert.Equal("\\vec{v}", Convert("<mover><mi>v</mi><mo>→</mo></mover>"));
        // A multi-glyph base widens to the stretched forms.
        Assert.Equal("\\overline{ab}",
            Convert("<mover><mrow><mi>a</mi><mi>b</mi></mrow><mo>¯</mo></mover>"));
    }

    [Fact]
    public void MunderLowLineIsUnderline() =>
        Assert.Equal("\\underline{m}", Convert("<munder><mi>m</mi><mo>_</mo></munder>"));

    [Fact]
    public void MoverWithContentScriptKeepsOverset() =>
        Assert.Equal("\\overset{n}{x}", Convert("<mover><mi>x</mi><mi>n</mi></mover>"));

    [Fact]
    public void MunderoverStacksBothScripts() =>
        Assert.Equal("\\overset{n}{\\underset{i}{\\sum }}",
            Convert("<munderover><mo>∑</mo><mi>i</mi><mi>n</mi></munderover>"));

    [Fact]
    public void CombiningOverlineFoldsIntoBar()
    {
        Assert.Equal("\\bar{U}", Convert("<mi>U̅</mi>"));
        // A mark split into its own element applies to the previous atom.
        Assert.Equal("\\bar{\\Sigma} ", Convert("<mi>Σ</mi><mo>̅</mo>"));
    }

    // ── escaping ────────────────────────────────────────────────────────────

    [Fact]
    public void LiteralStretchyBraceIsEscaped() => Assert.Equal("\\{x", Convert("<mo>{</mo><mi>x</mi>"));

    [Fact]
    public void LiteralBackslashIsEscaped() =>
        Assert.Equal("A\\backslash B", Convert("<mi>A</mi><mo>\\</mo><mi>B</mi>"));

    [Fact]
    public void MtextGreekMovesOutsideTextGroup() =>
        Assert.Equal("\\text{rate }\\Delta \\text{x}", Convert("<mtext>rate Δx</mtext>"));

    [Fact]
    public void MtextEscapesStructuralChars() =>
        Assert.Equal("\\text{m\\_\\{0\\} 50\\%}", Convert("<mtext>m_{0} 50%</mtext>"));

    // ── argument bracing ────────────────────────────────────────────────────

    [Fact]
    public void BracedBaseWithScriptStillWraps() =>
        Assert.Equal("{{Sb}_{1}}_{2}",
            Convert("<msub><msub><mrow><mi>S</mi><mi>b</mi></mrow><mn>1</mn></msub><mn>2</mn></msub>"));

    [Fact]
    public void ScriptedBaseWrapsBeforeOuterScript() =>
        Assert.Equal("{\\lambda ^{1}}^{2}",
            Convert("<msup><msup><mi>λ</mi><mn>1</mn></msup><mn>2</mn></msup>"));

    [Fact]
    public void EmptyScriptBaseRendersAsEmptyGroup() =>
        Assert.Equal("T^{\\nu }{}_{\\nu }",
            Convert("<msup><mi>T</mi><mi>ν</mi></msup><msub><mrow/><mi>ν</mi></msub>"));

    // ── annotations ─────────────────────────────────────────────────────────

    [Fact]
    public void TexAnnotationWinsOverPresentationTree()
    {
        string latex = Convert(
            "<semantics><mrow><mi>E</mi><mo>=</mo><mi>m</mi><msup><mi>c</mi><mn>2</mn></msup></mrow>" +
            "<annotation encoding=\"application/x-tex\">E = mc^2</annotation></semantics>");
        Assert.Equal("E = mc^2", latex);
    }

    [Fact]
    public void DisplayStyleWrapperComesOff()
    {
        string latex = Convert(
            "<semantics><mrow><mi>x</mi></mrow>" +
            "<annotation encoding=\"application/x-tex\">{\\displaystyle x^{2}+1}</annotation></semantics>");
        Assert.Equal("x^{2}+1", latex);
    }

    [Fact]
    public void PartialBraceGroupKeepsTheWrapper() =>
        Assert.Equal("{\\displaystyle a} + b", MathMl.StripStyleWrapper("{\\displaystyle a} + b"));

    [Fact]
    public void NonTexAnnotationStillRendersPresentationBranch()
    {
        string latex = Convert(
            "<semantics><mrow><mi>E</mi><mo>=</mo><mi>m</mi></mrow>" +
            "<annotation encoding=\"StarMath 5.0\">E = m</annotation></semantics>");
        Assert.Equal("E=m", latex);
    }

    [Fact]
    public void EmptyTexAnnotationFallsBack()
    {
        string latex = Convert(
            "<semantics><mrow><mi>a</mi><mo>+</mo><mi>b</mi></mrow>" +
            "<annotation encoding=\"application/x-tex\">   </annotation></semantics>");
        Assert.Equal("a+b", latex);
    }

    // ── content MathML ──────────────────────────────────────────────────────

    [Fact]
    public void ContentMathmlApplyConvertsByOperator()
    {
        Assert.Equal("a+b", Convert("<apply><plus/><ci>a</ci><ci>b</ci></apply>"));
        Assert.Equal("x^{2}", Convert("<apply><power/><ci>x</ci><cn>2</cn></apply>"));
        Assert.Equal("\\sqrt[3]{x}", Convert("<apply><root/><degree><cn>3</cn></degree><ci>x</ci></apply>"));
    }

    [Fact]
    public void ContentMathmlFunctionsAndRelations() =>
        Assert.Equal("y=\\sin\\left(x\\right)",
            Convert("<apply><eq/><ci>y</ci><apply><sin/><ci>x</ci></apply></apply>"));

    [Fact]
    public void ContentMathmlSumCarriesLimits()
    {
        string latex = Convert(
            "<apply><sum/><bvar><ci>i</ci></bvar><lowlimit><cn>1</cn></lowlimit>" +
            "<uplimit><ci>n</ci></uplimit><ci>i</ci></apply>");
        Assert.Equal("\\sum_{i=1}^{n} i", latex);
    }

    [Fact]
    public void ContentMathmlMatrixAndPiecewise()
    {
        string matrix = Convert(
            "<matrix><matrixrow><cn>1</cn><cn>0</cn></matrixrow>" +
            "<matrixrow><cn>0</cn><cn>1</cn></matrixrow></matrix>");
        Assert.Equal("\\begin{pmatrix}1 & 0 \\\\ 0 & 1\\end{pmatrix}", matrix);

        string cases = Convert(
            "<piecewise><piece><cn>0</cn><apply><lt/><ci>x</ci><cn>0</cn></apply></piece>" +
            "<otherwise><ci>x</ci></otherwise></piecewise>");
        Assert.StartsWith("\\begin{cases}", cases);
        Assert.Contains("\\text{otherwise}", cases);
    }

    [Fact]
    public void UnknownContentOperatorDegrades() =>
        Assert.Equal("\\operatorname{wibble}\\left(a\\right)", Convert("<apply><wibble/><ci>a</ci></apply>"));

    [Fact]
    public void ContentAnnotationIsUsedWhenPresentationIsEmpty()
    {
        string latex = Convert(
            "<semantics><mrow/><annotation-xml encoding=\"MathML-Content\">" +
            "<apply><plus/><ci>a</ci><ci>b</ci></apply></annotation-xml></semantics>");
        Assert.Equal("a+b", latex);
    }

    [Fact]
    public void PresentationBranchStillWinsOverContentAnnotation()
    {
        string latex = Convert(
            "<semantics><mrow><mi>E</mi><mo>=</mo><mi>m</mi></mrow>" +
            "<annotation-xml encoding=\"MathML-Content\"><apply><plus/><ci>q</ci><ci>r</ci></apply>" +
            "</annotation-xml></semantics>");
        Assert.Equal("E=m", latex);
    }

    // ── whole-document shapes ───────────────────────────────────────────────

    [Fact]
    public void PrivateUseCharactersAreDropped()
    {
        // OpenOffice writes its stretchy fences as private use codepoints, which no renderer can
        // display.
        string latex = MathMl.ConvertMathmlStrToLatex(
            "<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mrow><mi>F</mi><mo></mo>" +
            "<mn>1</mn><mo></mo><mfenced open=\"\" close=\"\"><mn>2</mn></mfenced></mrow></math>");

        Assert.DoesNotContain('', latex);
        Assert.DoesNotContain('', latex);
        Assert.Contains("F", latex);
        Assert.Contains("1", latex);
    }

    [Fact]
    public void PrefixedMathmlWithTheOpenOfficeDoctypeConverts()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE math:math PUBLIC \"-//OpenOffice.org//DTD Modified W3C MathML 1.01//EN\" \"math.dtd\">\n" +
            "<math:math xmlns:math=\"http://www.w3.org/1998/Math/MathML\">\n" +
            " <math:semantics>\n  <math:mrow>\n   <math:mn>1</math:mn>\n" +
            "   <math:mo math:stretchy=\"false\">+</math:mo>\n   <math:mn>2</math:mn>\n" +
            "  </math:mrow>\n </math:semantics>\n</math:math>";

        string latex = MathMl.ConvertMathmlStrToLatex(xml);
        Assert.Contains("1", latex);
        Assert.Contains("2", latex);
    }

    [Fact]
    public void NestedQuadraticFormula()
    {
        string latex = Convert(
            "<mi>x</mi><mo>=</mo><mfrac><mrow><mo>-</mo><mi>b</mi><mo>±</mo>" +
            "<msqrt><msup><mi>b</mi><mn>2</mn></msup><mo>-</mo><mn>4</mn><mi>a</mi><mi>c</mi></msqrt>" +
            "</mrow><mrow><mn>2</mn><mi>a</mi></mrow></mfrac>");
        Assert.Equal("x=\\frac{-b\\pm \\sqrt{b^{2}-4ac}}{2a}", latex);
    }

    [Fact]
    public void EmbeddedFormulaObjectShape()
    {
        // The shape an ODT embedded formula object has: a StarMath annotation beside the
        // presentation tree.
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><semantics>" +
            "<mrow><mrow><mi>E</mi><mo stretchy=\"false\">=</mo><mrow><mi>m</mi>" +
            "<mo stretchy=\"false\">⋅</mo><msup><mi>c</mi><mn>2</mn></msup></mrow></mrow></mrow>" +
            "<annotation encoding=\"StarMath 5.0\">E = m cdot c^2</annotation></semantics></math>";
        Assert.Equal("E=m\\cdot c^{2}", MathMl.ConvertMathmlStrToLatex(xml));
    }

    [Fact]
    public void UnparseableFragmentConvertsToEmpty() =>
        Assert.Equal("", MathMl.ConvertMathmlStrToLatex("<math><mi>x</mi>"));

    [Fact]
    public void StripMathDelimitersUnwrapsOnlyBalancedSpans()
    {
        Assert.Equal("x^2", MathMl.StripMathDelimiters("$$x^2$$"));
        Assert.Equal("x^2", MathMl.StripMathDelimiters("  \\[x^2\\]  "));
        // A `\$` inside the span is a literal dollar sign, so it does not end the span.
        Assert.Equal("a\\$b", MathMl.StripMathDelimiters("$a\\$b$"));
        Assert.Equal("$a$ + $b$", MathMl.StripMathDelimiters("$a$ + $b$"));
    }
}
