// Ported from crates/xberg/src/extraction/typst_math.rs.
//
// `Formula.Latex` holds LaTeX, so Typst math notation cannot go into it verbatim: no renderer
// accepts `f_n = cases(a &"if" n = 0)`. Typst's own parser reads the math (see TypstParser), and
// the walk below maps its tree to LaTeX.
//
// A construct with no LaTeX equivalent degrades rather than breaking the output: an unknown
// symbol becomes upright text, an unknown function keeps its name and arguments, and a
// layout-only argument (`size: #50%`) drops. The result always parses.
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Xberg.Internal.MathMarkup;

internal static class TypstMath
{
    /// <summary>Convert one Typst math expression to LaTeX.</summary>
    public static string ConvertToLatex(string source)
    {
        var root = TypstParser.ParseMath(source);
        // Upstream keeps this in `extraction::latex_shape`, shared with the RST extractor; in
        // this port it already lives on that extractor, so it is called there rather than moved.
        return Xberg.Extractors.RstExtractor.WrapAlignedMath(CollapseSpaces(Render(root)).Trim());
    }

    /// <summary>Typst symbol names whose LaTeX command differs, or is worth pinning.</summary>
    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["dot"] = "\\cdot",
        ["dot.c"] = "\\cdot",
        ["dot.op"] = "\\cdot",
        ["times"] = "\\times",
        ["div"] = "\\div",
        ["plus.minus"] = "\\pm",
        ["minus.plus"] = "\\mp",
        ["star"] = "\\star",
        ["ast"] = "\\ast",
        ["circle"] = "\\circ",
        ["infinity"] = "\\infty",
        ["oo"] = "\\infty",
        ["partial"] = "\\partial",
        ["nabla"] = "\\nabla",
        ["diff"] = "\\partial",
        ["dots"] = "\\dots",
        ["dots.h"] = "\\dots",
        ["dots.h.c"] = "\\cdots",
        ["dots.v"] = "\\vdots",
        ["dots.down"] = "\\ddots",
        ["arrow.r"] = "\\rightarrow",
        ["arrow.l"] = "\\leftarrow",
        ["arrow.t"] = "\\uparrow",
        ["arrow.b"] = "\\downarrow",
        ["arrow.r.double"] = "\\Rightarrow",
        ["arrow.l.double"] = "\\Leftarrow",
        ["arrow.l.r.double"] = "\\Leftrightarrow",
        ["arrow.r.long"] = "\\longrightarrow",
        ["in"] = "\\in",
        ["in.not"] = "\\notin",
        ["subset"] = "\\subset",
        ["subset.eq"] = "\\subseteq",
        ["supset"] = "\\supset",
        ["supset.eq"] = "\\supseteq",
        ["union"] = "\\cup",
        ["sect"] = "\\cap",
        ["union.big"] = "\\bigcup",
        ["sect.big"] = "\\bigcap",
        ["emptyset"] = "\\emptyset",
        ["forall"] = "\\forall",
        ["exists"] = "\\exists",
        ["not"] = "\\neg",
        ["and"] = "\\land",
        ["or"] = "\\lor",
        ["eq"] = "=",
        ["eq.not"] = "\\ne",
        ["lt"] = "<",
        ["lt.eq"] = "\\le",
        ["gt"] = ">",
        ["gt.eq"] = "\\ge",
        ["approx"] = "\\approx",
        ["equiv"] = "\\equiv",
        ["prop"] = "\\propto",
        ["tilde.op"] = "\\sim",
        ["integral"] = "\\int",
        ["integral.double"] = "\\iint",
        ["integral.cont"] = "\\oint",
        ["sum"] = "\\sum",
        ["product"] = "\\prod",
        ["limit"] = "\\lim",
        ["floor.l"] = "\\lfloor",
        ["floor.r"] = "\\rfloor",
        ["ceil.l"] = "\\lceil",
        ["ceil.r"] = "\\rceil",
        ["angle.l"] = "\\langle",
        ["angle.r"] = "\\rangle",
        ["bar.v"] = "|",
        ["bar.v.double"] = "\\|",
        ["planck.reduce"] = "\\hbar",
        ["ell"] = "\\ell",
        ["aleph"] = "\\aleph",
        ["degree"] = "^\\circ",
        ["prime"] = "'",
    };

    /// <summary>Greek letter names, which Typst and LaTeX spell the same way.</summary>
    private static readonly HashSet<string> Greek = new()
    {
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
        "lambda", "mu", "nu", "xi", "omicron", "pi", "rho", "sigma", "tau", "upsilon", "phi",
        "chi", "psi", "omega", "varepsilon", "vartheta", "varphi", "varrho", "varsigma", "Gamma",
        "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon", "Phi", "Psi", "Omega",
    };

    /// <summary>Function names that wrap their single argument in a LaTeX command.</summary>
    private static readonly Dictionary<string, string> Wrappers = new()
    {
        ["bold"] = "\\mathbf",
        ["upright"] = "\\mathrm",
        ["italic"] = "\\mathit",
        ["sans"] = "\\mathsf",
        ["mono"] = "\\mathtt",
        ["cal"] = "\\mathcal",
        ["frak"] = "\\mathfrak",
        ["bb"] = "\\mathbb",
        ["hat"] = "\\hat",
        ["tilde"] = "\\tilde",
        ["bar"] = "\\bar",
        ["dot"] = "\\dot",
        ["ddot"] = "\\ddot",
        ["breve"] = "\\breve",
        ["check"] = "\\check",
        ["grave"] = "\\grave",
        ["acute"] = "\\acute",
        ["arrow"] = "\\vec",
        ["vec"] = "\\vec",
        ["overline"] = "\\overline",
        ["underline"] = "\\underline",
    };

    /// <summary>Operator names that LaTeX spells with a leading backslash.</summary>
    private static readonly HashSet<string> Operators = new()
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh",
        "tanh", "log", "ln", "exp", "det", "dim", "ker", "deg", "gcd", "max", "min", "sup", "inf",
        "lim", "mod",
    };

    /// <summary>Render one node and its children.</summary>
    private static string Render(TypstNode node)
    {
        switch (node.Kind)
        {
            case TypstKind.Math: return RenderChildren(node);
            case TypstKind.MathText:
            case TypstKind.Text: return EscapeText(node.LeafText);
            case TypstKind.MathIdent:
            case TypstKind.Ident: return Symbol(node.LeafText);
            case TypstKind.MathShorthand: return Shorthand(node.LeafText);
            case TypstKind.MathAlignPoint: return " & ";
            case TypstKind.Linebreak: return " \\\\ ";
            case TypstKind.Str: return TextCommand(node.LeafText.Trim('"'));
            case TypstKind.MathAttach: return RenderAttach(node);
            case TypstKind.MathFrac: return RenderFrac(node);
            case TypstKind.MathRoot: return RenderRoot(node);
            case TypstKind.MathPrimes: return node.LeafText;
            case TypstKind.MathDelimited: return RenderChildren(node);
            case TypstKind.MathCall:
            case TypstKind.FuncCall: return RenderCall(node);
            case TypstKind.Space:
            case TypstKind.Parbreak: return " ";
            case TypstKind.LeftParen: return "(";
            case TypstKind.RightParen: return ")";
            case TypstKind.Comma: return ", ";
            case TypstKind.Escape: return node.LeafText.TrimStart('\\');
            // A layout argument, a comment, or a construct with no math meaning.
            case TypstKind.Hash:
            case TypstKind.LineComment:
            case TypstKind.BlockComment:
            case TypstKind.Error: return "";
            default:
                return node.Children.Count > 0 ? RenderChildren(node) : EscapeText(node.LeafText);
        }
    }

    private static string RenderChildren(TypstNode node)
    {
        var sb = new StringBuilder();
        foreach (var child in node.Children) sb.Append(Render(child));
        return sb.ToString();
    }

    /// <summary>
    /// Render a node as a LaTeX group, dropping a delimiting pair of parentheses. Typst writes
    /// <c>f_(n - 1)</c>, where the parentheses group the subscript rather than appearing in it.
    /// </summary>
    private static string Group(TypstNode node)
    {
        string inner = GroupInner(node);
        return CountRunes(inner) == 1 ? inner : "{" + inner + "}";
    }

    /// <summary>
    /// Render a node as a braced LaTeX argument. A single character may follow <c>_</c> or
    /// <c>^</c> bare, but never a command: <c>\frac</c> with bare arguments reads as the
    /// undefined control sequence <c>\fracab</c>.
    /// </summary>
    private static string Braced(TypstNode node) => "{" + GroupInner(node) + "}";

    private static string GroupInner(TypstNode node)
    {
        string rendered = CollapseSpaces(Render(node)).Trim();
        if (rendered.StartsWith("(", System.StringComparison.Ordinal)
            && rendered.EndsWith(")", System.StringComparison.Ordinal))
            return rendered[1..^1].Trim();
        return rendered;
    }

    private static string RenderAttach(TypstNode node)
    {
        var baseText = new StringBuilder();
        var outText = new StringBuilder();
        char? pending = null;
        foreach (var child in node.Children)
        {
            switch (child.Kind)
            {
                case TypstKind.Underscore: pending = '_'; break;
                case TypstKind.Hat: pending = '^'; break;
                case TypstKind.Space: break;
                default:
                    if (pending is { } marker)
                    {
                        pending = null;
                        outText.Append(marker).Append(Group(child));
                    }
                    else baseText.Append(Render(child));
                    break;
            }
        }
        string b = baseText.ToString().Trim();
        // A script with no base of its own still needs one, or two scripts in a row read as a
        // double subscript.
        if (b.Length == 0 && outText.Length > 0) return "{}" + outText;
        return b + outText;
    }

    private static string RenderFrac(TypstNode node)
    {
        var parts = node.Children
            .Where(c => c.Kind is not (TypstKind.Slash or TypstKind.Space)).ToList();
        return parts.Count == 2
            ? $"\\frac{Braced(parts[0])}{Braced(parts[1])}"
            : RenderChildren(node);
    }

    private static string RenderRoot(TypstNode node)
    {
        var parts = node.Children
            .Where(c => c.Kind is not (TypstKind.Root or TypstKind.Space)).ToList();
        if (parts.Count == 1) return $"\\sqrt{Braced(parts[0])}";
        if (parts.Count == 2)
            return $"\\sqrt[{CollapseSpaces(Render(parts[0])).Trim()}]{Braced(parts[1])}";
        return RenderChildren(node);
    }

    /// <summary>
    /// Split a call's arguments into rows and cells. A comma separates cells and a semicolon
    /// separates rows, which is how Typst writes <c>mat(1, 0; 0, 1)</c>. A named argument
    /// (<c>size: #50%</c>) is layout, not math, so it drops.
    /// </summary>
    private static List<List<string>> CallRows(TypstNode args)
    {
        var rows = new List<List<string>> { new() };
        var cell = new StringBuilder();
        foreach (var child in args.Children)
        {
            switch (child.Kind)
            {
                case TypstKind.LeftParen:
                case TypstKind.RightParen:
                    break;
                case TypstKind.Comma:
                    rows[^1].Add(cell.ToString().Trim());
                    cell.Clear();
                    break;
                case TypstKind.Semicolon:
                    rows[^1].Add(cell.ToString().Trim());
                    cell.Clear();
                    rows.Add(new List<string>());
                    break;
                case TypstKind.Named:
                case TypstKind.Spread:
                    break;
                default:
                    cell.Append(Render(child));
                    break;
            }
        }
        string trailing = cell.ToString().Trim();
        if (trailing.Length > 0) rows[^1].Add(trailing);
        foreach (var row in rows) row.RemoveAll(c => c.Length == 0);
        rows.RemoveAll(row => row.Count == 0);
        return rows;
    }

    private static string Environment(string name, List<List<string>> rows) =>
        $"\\begin{{{name}}}{string.Join(" \\\\ ", rows.Select(r => string.Join(" & ", r)))}\\end{{{name}}}";

    private static string RenderCall(TypstNode node)
    {
        var children = node.Children;
        if (children.Count == 0) return RenderChildren(node);
        string name = children[0].LeafText;
        var args = children.FirstOrDefault(c => c.Kind is TypstKind.MathArgs or TypstKind.Args);
        if (args is null) return Symbol(name);

        var rows = CallRows(args);
        var flat = rows.SelectMany(r => r).ToList();

        switch (name)
        {
            case "frac" when flat.Count == 2: return $"\\frac{{{flat[0]}}}{{{flat[1]}}}";
            case "sqrt" when flat.Count == 1: return $"\\sqrt{{{flat[0]}}}";
            case "root" when flat.Count == 2: return $"\\sqrt[{flat[0]}]{{{flat[1]}}}";
            // A comma separates the branches of `cases`, so each argument is a row rather than
            // a cell.
            case "cases": return Environment("cases", flat.Select(c => new List<string> { c }).ToList());
            case "mat": return Environment("pmatrix", rows);
            case "vec" when flat.Count > 0:
                return Environment("pmatrix", flat.Select(c => new List<string> { c }).ToList());
            case "abs" when flat.Count == 1: return $"\\left|{flat[0]}\\right|";
            case "norm" when flat.Count == 1: return $"\\left\\|{flat[0]}\\right\\|";
            // `lr` asks Typst to size delimiters that the content already carries.
            case "lr": return string.Join(" ", flat);
            case "underbrace" when flat.Count == 2: return $"\\underbrace{{{flat[0]}}}_{{{flat[1]}}}";
            case "underbrace" when flat.Count == 1: return $"\\underbrace{{{flat[0]}}}";
            case "overbrace" when flat.Count == 2: return $"\\overbrace{{{flat[0]}}}^{{{flat[1]}}}";
            case "overbrace" when flat.Count == 1: return $"\\overbrace{{{flat[0]}}}";
            case "text" when flat.Count == 1: return TextCommand(flat[0]);
            case "op" when flat.Count == 1: return $"\\operatorname{{{flat[0]}}}";
            default:
                if (Wrappers.TryGetValue(name, out string? command) && flat.Count == 1)
                    return $"{command}{{{flat[0]}}}";
                // An unknown function keeps its name and arguments, which parses and shows the
                // reader what the source held.
                return $"\\mathrm{{{EscapeText(name)}}}({string.Join(", ", flat)})";
        }
    }

    /// <summary>Map a Typst symbol name to LaTeX.</summary>
    private static string Symbol(string name)
    {
        if (Symbols.TryGetValue(name, out string? latex)) return latex + " ";
        if (Greek.Contains(name)) return "\\" + name + " ";
        if (Operators.Contains(name)) return "\\" + name + " ";
        // A dotted name whose full form is unknown falls back to its base: `arrow.r.long.bar`
        // still reads as an arrow.
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            string head = name[..dot];
            if (Symbols.ContainsKey(head) || Greek.Contains(head)) return Symbol(head);
        }
        if (CountRunes(name) == 1) return EscapeText(name);
        return $"\\mathrm{{{EscapeText(name)}}}";
    }

    /// <summary>Map a Typst shorthand to its LaTeX command.</summary>
    private static string Shorthand(string text)
    {
        string? latex = text switch
        {
            "->" => "\\to",
            "<-" => "\\leftarrow",
            "<->" => "\\leftrightarrow",
            "=>" => "\\Rightarrow",
            "<=>" => "\\Leftrightarrow",
            "|->" => "\\mapsto",
            ">=" => "\\ge",
            "<=" => "\\le",
            "!=" => "\\ne",
            "==" => "\\equiv",
            "..." => "\\dots",
            "[|" => "\\llbracket",
            "|]" => "\\rrbracket",
            "-" => "-",
            _ => null,
        };
        return latex is null ? EscapeText(text) : latex + " ";
    }

    /// <summary>Wrap prose in <c>\text{}</c>, escaping what LaTeX would otherwise read as markup.</summary>
    private static string TextCommand(string text)
    {
        var inner = new StringBuilder(text.Length);
        foreach (char ch in text)
            inner.Append(ch switch
            {
                '\\' => "\\textbackslash{}",
                '{' => "\\{",
                '}' => "\\}",
                '#' => "\\#",
                '%' => "\\%",
                '$' => "\\$",
                '&' => "\\&",
                '_' => "\\_",
                '^' => "\\textasciicircum{}",
                '~' => "\\textasciitilde{}",
                _ => ch.ToString(),
            });
        return "\\text{" + inner + "}";
    }

    /// <summary>Escape the characters that would change the structure of the LaTeX.</summary>
    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            sb.Append(ch switch
            {
                '\\' => "\\backslash ",
                '{' => "\\{",
                '}' => "\\}",
                '#' => "\\#",
                '%' => "\\%",
                '$' => "\\$",
                '&' => "\\&",
                _ => ch.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>Collapse runs of whitespace, which the walk emits freely around commands.</summary>
    private static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool lastSpace = false;
        foreach (char ch in text)
        {
            bool isSpace = char.IsWhiteSpace(ch);
            if (isSpace) { if (!lastSpace) sb.Append(' '); }
            else sb.Append(ch);
            lastSpace = isSpace;
        }
        return sb.ToString();
    }

    /// <summary>Count Unicode scalars, as Rust's <c>chars().count()</c> does.</summary>
    private static int CountRunes(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }
}
