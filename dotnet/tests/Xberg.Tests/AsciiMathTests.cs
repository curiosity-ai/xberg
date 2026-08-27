// The AsciiMath to LaTeX converter, ported from the `mathemascii` 0.4.0 crate and
// crates/xberg/src/extraction/asciimath.rs. The expectations below are upstream's own, from
// that module's tests.
using Xberg.Internal.MathMarkup;
using Xunit;

namespace Xberg.Tests;

public class AsciiMathTests
{
    private static string? Convert(string source) => AsciiMath.ConvertToLatex(source);

    [Fact]
    public void RootsAndFractionsConvert()
    {
        Assert.Equal("\\sqrt{4}=2", Convert("sqrt(4) = 2"));
        Assert.Equal("\\frac{a}{b}", Convert("a/b"));
    }

    [Fact]
    public void SumsCarryTheirLimits()
    {
        string latex = Convert("sum_(i=1)^n i^3")!;
        Assert.Contains("\\sum", latex);
        Assert.Contains("i=1", latex);
    }

    [Fact]
    public void TheQuadraticFormulaConverts()
    {
        string latex = Convert("x = (-b +- sqrt(b^2-4ac))/(2a)")!;
        Assert.Contains("\\frac", latex);
        Assert.Contains("\\pm", latex);
    }

    [Fact]
    public void EmptyInputYieldsNothing() => Assert.Null(Convert("   "));

    [Fact]
    public void RealSpecificationAsciiMathConverts()
    {
        // Each of these comes from a published document and must convert.
        foreach (string source in new[]
                 {
                     "b_(g0)=1/(mn)sum_(i=0)^(mn-1)g_i",
                     "|barg_(xii)|leC",
                     "|sigma_(xii)|lesigma",
                 })
        {
            string? latex = Convert(source);
            Assert.False(string.IsNullOrWhiteSpace(latex), $"no LaTeX for {source}");
        }
    }

    [Fact]
    public void MultibyteAsciiMathDropsTheEquationRatherThanTheDocument()
    {
        // A published specification writes `≤` in its AsciiMath. The parser slices by byte index
        // while indexing by character, so this input aborts inside it — upstream contains that
        // and loses the equation, not the document.
        Assert.Null(Convert("t(t_i≤t≤t_(i+1))"));
        Assert.NotNull(Convert("a+b"));
    }

    [Fact]
    public void CancelDropsTheEquation()
    {
        // `cancel` renders `<menclose>`, which the crate leaves unimplemented and panics on.
        Assert.Null(Convert("cancel(x)"));
        Assert.NotNull(Convert("a+b"));
    }
}

/// <summary>
/// The Typst-math to LaTeX converter, ported from crates/xberg/src/extraction/typst_math.rs on
/// top of a port of typst-syntax's own math parser. The expectations below are upstream's own,
/// from that module's tests and the Typst extractor's.
/// </summary>
public class TypstMathTests
{
    private static string Convert(string source) => TypstMath.ConvertToLatex(source);

    [Fact]
    public void AttachBecomesSubAndSuperscript()
    {
        Assert.Equal("x^2", Convert("x^2"));
        Assert.Equal("f_n", Convert("f_n"));
        Assert.Equal("f_{n - 1}", Convert("f_(n - 1)"));
        Assert.Equal("x_i^2", Convert("x_i^2"));
    }

    [Fact]
    public void SymbolsBecomeCommands()
    {
        Assert.Equal("\\alpha + \\beta", Convert("alpha + beta"));
        Assert.Equal("\\nabla \\cdot v", Convert("nabla dot v"));
        Assert.Equal("a \\times b", Convert("a times b"));
    }

    [Fact]
    public void FunctionsBecomeCommands()
    {
        Assert.Equal("\\frac{a}{b}", Convert("frac(a, b)"));
        Assert.Equal("\\sqrt{x + 1}", Convert("sqrt(x + 1)"));
        Assert.Equal("\\sqrt[3]{x}", Convert("root(3, x)"));
        Assert.Equal("\\mathbf{D}", Convert("bold(D)"));
    }

    [Fact]
    public void MatricesAndCasesBecomeEnvironments()
    {
        Assert.Equal("\\begin{pmatrix}a & b \\\\ c & d\\end{pmatrix}", Convert("mat(a, b; c, d)"));
        Assert.Equal("\\underbrace{x + y}_{|A|}", Convert("underbrace(x + y, |A|)"));
    }

    [Fact]
    public void AnAlignedBlockIsWrapped()
    {
        // A `\` line break with alignment points only parses inside an `aligned` environment.
        Assert.Equal(
            "\\begin{aligned}\\nabla \\cdot \\mathbf{D} & = \\rho \\\\ \\nabla \\cdot \\mathbf{B} & = 0\\end{aligned}",
            Convert("nabla dot bold(D) &= rho \\\nnabla dot bold(B) &= 0"));
    }

    [Fact]
    public void ALayoutArgumentDrops()
    {
        // `lr` asks Typst to size delimiters the content already carries, and `size:` is layout
        // rather than mathematics.
        Assert.Equal("[\\sum_{k = 0}^n e^{k^2}]", Convert("lr([sum_(k = 0)^n e^(k^2)], size: #50%)"));
    }

    [Fact]
    public void AnUnknownFunctionKeepsItsNameAndArguments()
    {
        Assert.Equal("\\mathrm{curl}(\\mathrm{grad} f)", Convert("curl(grad f)"));
    }

    [Fact]
    public void ADottedNameRendersAsTheParserSplitsIt()
    {
        // `dots.h.c` is in the symbol table, but the lexer only takes `dots` as the identifier
        // and leaves `.h.c` as text — so the table entry never applies and the output keeps the
        // suffix. Measured against the golden for typst/undergradmath.typ, which reads
        // `… - \dots .h.c`.
        Assert.Equal("\\dots .h.c", Convert("dots.h.c"));
    }
}
