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
