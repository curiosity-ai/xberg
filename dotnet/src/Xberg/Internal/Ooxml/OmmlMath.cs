using System.Text;
using System.Xml.Linq;

namespace Xberg.Internal.Ooxml;

/// <summary>
/// OMML (Office Math Markup Language) → LaTeX converter. Faithful port of
/// <c>extraction/docx/math.rs</c>. Operates on the already-parsed <see cref="XElement"/> subtree
/// (the C# DOCX reader is DOM-based, so there is no streaming/security-budget layer here).
/// </summary>
internal static class OmmlMath
{
    // ── MathNode tree ──────────────────────────────────────────────────────
    private enum FracType { Bar, NoBar, Linear, Skewed }

    private abstract record MathNode;
    private sealed record Run(string Text) : MathNode;
    private sealed record SSup(List<MathNode> Base, List<MathNode> Sup) : MathNode;
    private sealed record SSub(List<MathNode> Base, List<MathNode> Sub) : MathNode;
    private sealed record SSubSup(List<MathNode> Base, List<MathNode> Sub, List<MathNode> Sup) : MathNode;
    private sealed record Frac(List<MathNode> Num, List<MathNode> Den, FracType Type) : MathNode;
    private sealed record Rad(List<MathNode> Deg, List<MathNode> Body, bool DegHide) : MathNode;
    private sealed record Nary(string Chr, List<MathNode> Sub, List<MathNode> Sup, List<MathNode> Body, bool SubHide, bool SupHide) : MathNode;
    private sealed record Delim(string BeginChr, string EndChr, string SepChr, List<List<MathNode>> Elements) : MathNode;
    private sealed record Func(List<MathNode> Name, List<MathNode> Body) : MathNode;
    private sealed record Acc(string Chr, List<MathNode> Body) : MathNode;
    private sealed record EqArr(List<List<MathNode>> Rows) : MathNode;
    private sealed record LimLow(List<MathNode> Body, List<MathNode> Lim) : MathNode;
    private sealed record LimUpp(List<MathNode> Body, List<MathNode> Lim) : MathNode;
    private sealed record Bar(List<MathNode> Body, bool Top) : MathNode;
    private sealed record BorderBox(List<MathNode> Body) : MathNode;
    private sealed record Matrix(List<List<List<MathNode>>> Rows) : MathNode;
    private sealed record Group(List<MathNode> Children) : MathNode;
    private sealed record SPre(List<MathNode> Base, List<MathNode> Sub, List<MathNode> Sup) : MathNode;

    // ── Public entry points ─────────────────────────────────────────────────

    /// <summary>Convert an <c>m:oMath</c> element to LaTeX (inline math).</summary>
    public static string ConvertOMath(XElement oMath) => RenderNodes(CollectChildren(oMath));

    /// <summary>Convert an <c>m:oMathPara</c> element to LaTeX (display math).</summary>
    public static string ConvertOMathPara(XElement oMathPara)
    {
        var children = CollectChildren(oMathPara);
        var parts = new List<string>();
        foreach (var child in children)
            if (child is Group g)
            {
                string rendered = RenderNodes(g.Children);
                if (rendered.Length != 0) parts.Add(rendered);
            }
        return parts.Count == 0 ? RenderNodes(children) : string.Join(" \\\\ ", parts);
    }

    // ── Tree builder ─────────────────────────────────────────────────────────
    private static List<MathNode> CollectChildren(XElement parent)
    {
        var nodes = new List<MathNode>();
        foreach (var e in parent.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "r": nodes.Add(CollectRun(e)); break;
                case "sSup": nodes.Add(new SSup(Child(e, "e"), Child(e, "sup"))); break;
                case "sSub": nodes.Add(new SSub(Child(e, "e"), Child(e, "sub"))); break;
                case "sSubSup": nodes.Add(new SSubSup(Child(e, "e"), Child(e, "sub"), Child(e, "sup"))); break;
                case "f": nodes.Add(CollectFrac(e)); break;
                case "rad": nodes.Add(CollectRad(e)); break;
                case "nary": nodes.Add(CollectNary(e)); break;
                case "d": nodes.Add(CollectDelim(e)); break;
                case "func": nodes.Add(new Func(Child(e, "fName"), Child(e, "e"))); break;
                case "acc": nodes.Add(CollectAcc(e)); break;
                case "eqArr": nodes.Add(new EqArr(ChildrenList(e, "e"))); break;
                case "limLow": nodes.Add(new LimLow(Child(e, "e"), Child(e, "lim"))); break;
                case "limUpp": nodes.Add(new LimUpp(Child(e, "e"), Child(e, "lim"))); break;
                case "bar": nodes.Add(CollectBar(e)); break;
                case "borderBox": nodes.Add(new BorderBox(Child(e, "e"))); break;
                case "m": nodes.Add(CollectMatrix(e)); break;
                case "box":
                case "phant": nodes.Add(new Group(CollectElementBody(e))); break;
                case "sPre": nodes.Add(new SPre(Child(e, "e"), Child(e, "sub"), Child(e, "sup"))); break;
                case "oMath": nodes.Add(new Group(CollectChildren(e))); break;
                // Unknown element: skipped entirely (matches Rust `skip_to_end`).
            }
        }
        return nodes;
    }

    private static List<MathNode> Child(XElement parent, string localName)
    {
        var el = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        return el is null ? new List<MathNode>() : CollectChildren(el);
    }

    private static List<List<MathNode>> ChildrenList(XElement parent, string localName)
    {
        var list = new List<List<MathNode>>();
        foreach (var el in parent.Elements().Where(e => e.Name.LocalName == localName))
            list.Add(CollectChildren(el));
        return list;
    }

    private static MathNode CollectRun(XElement r)
    {
        var text = new StringBuilder();
        foreach (var t in r.Elements().Where(e => e.Name.LocalName == "t"))
            text.Append(DocxReader.DropXmlEntities(t.Value));
        return new Run(text.ToString());
    }

    private static MathNode CollectFrac(XElement f)
    {
        var type = FracType.Bar;
        var fPr = f.Elements().FirstOrDefault(e => e.Name.LocalName == "fPr");
        if (fPr is not null)
        {
            var val = MVal(fPr.Elements().FirstOrDefault(e => e.Name.LocalName == "type"));
            type = val switch { "noBar" => FracType.NoBar, "lin" => FracType.Linear, "skw" => FracType.Skewed, _ => FracType.Bar };
        }
        return new Frac(Child(f, "num"), Child(f, "den"), type);
    }

    private static MathNode CollectRad(XElement rad)
    {
        bool degHide = true;
        var radPr = rad.Elements().FirstOrDefault(e => e.Name.LocalName == "radPr");
        if (radPr is not null)
        {
            var dh = radPr.Elements().FirstOrDefault(e => e.Name.LocalName == "degHide");
            if (dh is not null) degHide = MVal(dh) != "0";
        }
        return new Rad(Child(rad, "deg"), Child(rad, "e"), degHide);
    }

    private static MathNode CollectNary(XElement nary)
    {
        string chr = "∫"; // integral default
        bool subHide = false, supHide = false;
        var naryPr = nary.Elements().FirstOrDefault(e => e.Name.LocalName == "naryPr");
        if (naryPr is not null)
            foreach (var p in naryPr.Elements())
                switch (p.Name.LocalName)
                {
                    case "chr": { var v = MVal(p); if (v is not null) chr = v; break; }
                    case "subHide": subHide = MVal(p) != "0"; break;
                    case "supHide": supHide = MVal(p) != "0"; break;
                }
        return new Nary(chr, Child(nary, "sub"), Child(nary, "sup"), Child(nary, "e"), subHide, supHide);
    }

    private static MathNode CollectDelim(XElement d)
    {
        string beginChr = "(", endChr = ")", sepChr = "|";
        var dPr = d.Elements().FirstOrDefault(e => e.Name.LocalName == "dPr");
        if (dPr is not null)
            foreach (var p in dPr.Elements())
                switch (p.Name.LocalName)
                {
                    case "begChr": { var v = MVal(p); if (v is not null) beginChr = v; break; }
                    case "endChr": { var v = MVal(p); if (v is not null) endChr = v; break; }
                    case "sepChr": { var v = MVal(p); if (v is not null) sepChr = v; break; }
                }
        return new Delim(beginChr, endChr, sepChr, ChildrenList(d, "e"));
    }

    private static MathNode CollectAcc(XElement acc)
    {
        string chr = "̂"; // combining circumflex (hat) default
        var accPr = acc.Elements().FirstOrDefault(e => e.Name.LocalName == "accPr");
        if (accPr is not null)
        {
            var v = MVal(accPr.Elements().FirstOrDefault(e => e.Name.LocalName == "chr"));
            if (v is not null) chr = v;
        }
        return new Acc(chr, Child(acc, "e"));
    }

    private static MathNode CollectBar(XElement bar)
    {
        bool top = true;
        var barPr = bar.Elements().FirstOrDefault(e => e.Name.LocalName == "barPr");
        if (barPr is not null)
        {
            var v = MVal(barPr.Elements().FirstOrDefault(e => e.Name.LocalName == "pos"));
            if (v is not null) top = v != "bot";
        }
        return new Bar(Child(bar, "e"), top);
    }

    private static MathNode CollectMatrix(XElement m)
    {
        var rows = new List<List<List<MathNode>>>();
        foreach (var mr in m.Elements().Where(e => e.Name.LocalName == "mr"))
            rows.Add(ChildrenList(mr, "e"));
        return new Matrix(rows);
    }

    private static List<MathNode> CollectElementBody(XElement el)
    {
        var children = new List<MathNode>();
        foreach (var e in el.Elements())
            if (e.Name.LocalName == "e") children.AddRange(CollectChildren(e));
        return children;
    }

    private static string? MVal(XElement? e) =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value;

    // ── LaTeX renderer ─────────────────────────────────────────────────────
    private static string RenderNodes(List<MathNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes) RenderNode(n, sb);
        return sb.ToString();
    }

    private static void RenderNode(MathNode node, StringBuilder o)
    {
        switch (node)
        {
            case Run run: RenderRunText(run.Text, o); break;
            case SSup s:
                RenderGroup(s.Base, o); o.Append("^{"); o.Append(RenderNodes(s.Sup)); o.Append('}');
                break;
            case SSub s:
                RenderGroup(s.Base, o); o.Append("_{"); o.Append(RenderNodes(s.Sub)); o.Append('}');
                break;
            case SSubSup s:
                RenderGroup(s.Base, o); o.Append("_{"); o.Append(RenderNodes(s.Sub));
                o.Append("}^{"); o.Append(RenderNodes(s.Sup)); o.Append('}');
                break;
            case Frac f:
                switch (f.Type)
                {
                    case FracType.Bar:
                        o.Append("\\frac{"); o.Append(RenderNodes(f.Num)); o.Append("}{"); o.Append(RenderNodes(f.Den)); o.Append('}');
                        break;
                    case FracType.NoBar:
                        o.Append("\\binom{"); o.Append(RenderNodes(f.Num)); o.Append("}{"); o.Append(RenderNodes(f.Den)); o.Append('}');
                        break;
                    default: // Linear | Skewed
                        string numS = RenderNodes(f.Num), denS = RenderNodes(f.Den);
                        if (Utf8Len(numS) > 1) { o.Append('{'); o.Append(numS); o.Append('}'); } else o.Append(numS);
                        o.Append('/');
                        if (Utf8Len(denS) > 1) { o.Append('{'); o.Append(denS); o.Append('}'); } else o.Append(denS);
                        break;
                }
                break;
            case Rad r:
                o.Append("\\sqrt");
                if (!r.DegHide && r.Deg.Count != 0)
                {
                    string degS = RenderNodes(r.Deg);
                    if (degS.Length != 0) { o.Append('['); o.Append(degS); o.Append(']'); }
                }
                o.Append('{'); o.Append(RenderNodes(r.Body)); o.Append('}');
                break;
            case Nary n:
                o.Append(NaryChrToLatex(n.Chr));
                if (!n.SubHide && n.Sub.Count != 0) { o.Append("_{"); o.Append(RenderNodes(n.Sub)); o.Append('}'); }
                if (!n.SupHide && n.Sup.Count != 0) { o.Append("^{"); o.Append(RenderNodes(n.Sup)); o.Append('}'); }
                if (n.Body.Count != 0) { o.Append('{'); o.Append(RenderNodes(n.Body)); o.Append('}'); }
                break;
            case Delim d:
                o.Append("\\left"); o.Append(DelimChrToLatex(d.BeginChr));
                for (int i = 0; i < d.Elements.Count; i++)
                {
                    if (i > 0) o.Append(DelimSepToLatex(d.SepChr));
                    o.Append(RenderNodes(d.Elements[i]));
                }
                o.Append("\\right"); o.Append(DelimChrToLatex(d.EndChr));
                break;
            case Func fn:
                string funcName = RenderNodes(fn.Name);
                string latexFunc = funcName.Trim() switch
                {
                    "sin" => "\\sin", "cos" => "\\cos", "tan" => "\\tan", "cot" => "\\cot",
                    "sec" => "\\sec", "csc" => "\\csc", "log" => "\\log", "ln" => "\\ln",
                    "exp" => "\\exp", "lim" => "\\lim", "max" => "\\max", "min" => "\\min",
                    "sup" => "\\sup", "inf" => "\\inf", "det" => "\\det", "gcd" => "\\gcd",
                    "deg" => "\\deg", "dim" => "\\dim", "hom" => "\\hom", "ker" => "\\ker",
                    "arg" => "\\arg", "sinh" => "\\sinh", "cosh" => "\\cosh", "tanh" => "\\tanh",
                    _ => "",
                };
                if (latexFunc.Length != 0) o.Append(latexFunc);
                else { o.Append("\\mathrm{"); o.Append(funcName); o.Append('}'); }
                o.Append('{'); o.Append(RenderNodes(fn.Body)); o.Append('}');
                break;
            case Acc a:
                o.Append(AccentChrToLatex(a.Chr)); o.Append('{'); o.Append(RenderNodes(a.Body)); o.Append('}');
                break;
            case EqArr eq:
                o.Append("\\begin{aligned}");
                for (int i = 0; i < eq.Rows.Count; i++)
                {
                    if (i > 0) o.Append(" \\\\ ");
                    o.Append(RenderNodes(eq.Rows[i]));
                }
                o.Append("\\end{aligned}");
                break;
            case LimLow l:
                o.Append("\\underset{"); o.Append(RenderNodes(l.Lim)); o.Append("}{"); o.Append(RenderNodes(l.Body)); o.Append('}');
                break;
            case LimUpp l:
                o.Append("\\overset{"); o.Append(RenderNodes(l.Lim)); o.Append("}{"); o.Append(RenderNodes(l.Body)); o.Append('}');
                break;
            case Bar b:
                o.Append(b.Top ? "\\overline{" : "\\underline{"); o.Append(RenderNodes(b.Body)); o.Append('}');
                break;
            case BorderBox bb:
                o.Append("\\boxed{"); o.Append(RenderNodes(bb.Body)); o.Append('}');
                break;
            case Matrix mx:
                o.Append("\\begin{matrix}");
                for (int i = 0; i < mx.Rows.Count; i++)
                {
                    if (i > 0) o.Append(" \\\\ ");
                    var row = mx.Rows[i];
                    for (int j = 0; j < row.Count; j++)
                    {
                        if (j > 0) o.Append(" & ");
                        o.Append(RenderNodes(row[j]));
                    }
                }
                o.Append("\\end{matrix}");
                break;
            case Group g: o.Append(RenderNodes(g.Children)); break;
            case SPre sp:
                o.Append("{}_{"); o.Append(RenderNodes(sp.Sub)); o.Append("}^{"); o.Append(RenderNodes(sp.Sup)); o.Append('}');
                RenderGroup(sp.Base, o);
                break;
        }
    }

    private static void RenderGroup(List<MathNode> nodes, StringBuilder o)
    {
        string rendered = RenderNodes(nodes);
        bool needsBraces = RuneCount(rendered) > 1 && !rendered.StartsWith('\\') && !rendered.StartsWith('{');
        if (needsBraces) { o.Append('{'); o.Append(rendered); o.Append('}'); }
        else o.Append(rendered);
    }

    private static void RenderRunText(string text, StringBuilder o)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            string? latex = rune.IsBmp ? UnicodeToLatex((char)rune.Value) : null;
            if (latex is not null) o.Append(latex);
            else o.Append(rune.ToString());
        }
    }

    private static int RuneCount(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    // ── Character mapping tables ─────────────────────────────────────────────
    private static string? UnicodeToLatex(char ch) => ch switch
    {
        // Greek lowercase
        'α' => "\\alpha ", 'β' => "\\beta ", 'γ' => "\\gamma ", 'δ' => "\\delta ",
        'ε' => "\\epsilon ", 'ζ' => "\\zeta ", 'η' => "\\eta ", 'θ' => "\\theta ",
        'ι' => "\\iota ", 'κ' => "\\kappa ", 'λ' => "\\lambda ", 'μ' => "\\mu ",
        'ν' => "\\nu ", 'ξ' => "\\xi ", 'ο' => "o", 'π' => "\\pi ",
        'ρ' => "\\rho ", 'ς' => "\\varsigma ", 'σ' => "\\sigma ", 'τ' => "\\tau ",
        'υ' => "\\upsilon ", 'φ' => "\\phi ", 'χ' => "\\chi ", 'ψ' => "\\psi ",
        'ω' => "\\omega ",
        // Greek uppercase
        'Γ' => "\\Gamma ", 'Δ' => "\\Delta ", 'Θ' => "\\Theta ", 'Λ' => "\\Lambda ",
        'Ξ' => "\\Xi ", 'Π' => "\\Pi ", 'Σ' => "\\Sigma ", 'Υ' => "\\Upsilon ",
        'Φ' => "\\Phi ", 'Ψ' => "\\Psi ", 'Ω' => "\\Omega ",
        // Operators
        '±' => "\\pm ", '∓' => "\\mp ", '×' => "\\times ", '÷' => "\\div ",
        '⋅' => "\\cdot ", '∗' => "\\ast ", '∘' => "\\circ ", '∙' => "\\bullet ",
        // Relations
        '≤' => "\\leq ", '≥' => "\\geq ", '≠' => "\\neq ", '≈' => "\\approx ",
        '≡' => "\\equiv ", '≺' => "\\prec ", '≻' => "\\succ ", '⊆' => "\\subseteq ",
        '⊇' => "\\supseteq ", '⊂' => "\\subset ", '⊃' => "\\supset ", '∈' => "\\in ",
        '∉' => "\\notin ", '∋' => "\\ni ",
        // Arrows
        '←' => "\\leftarrow ", '→' => "\\rightarrow ", '↑' => "\\uparrow ", '↓' => "\\downarrow ",
        '↔' => "\\leftrightarrow ", '⇐' => "\\Leftarrow ", '⇒' => "\\Rightarrow ",
        '⇔' => "\\Leftrightarrow ", '↦' => "\\mapsto ",
        // Special symbols
        '∞' => "\\infty ", '∂' => "\\partial ", '∇' => "\\nabla ", '∀' => "\\forall ",
        '∃' => "\\exists ", '∅' => "\\emptyset ", '∧' => "\\wedge ", '∨' => "\\vee ",
        '¬' => "\\neg ", '∩' => "\\cap ", '∪' => "\\cup ", '…' => "\\ldots ",
        '⋯' => "\\cdots ", '⋮' => "\\vdots ", '⋱' => "\\ddots ", '′' => "'",
        '″' => "''", 'ℏ' => "\\hbar ", 'ℓ' => "\\ell ", 'ℜ' => "\\Re ",
        'ℑ' => "\\Im ", '℘' => "\\wp ", 'ℵ' => "\\aleph ",
        // N-ary operators (when used as text)
        '∑' => "\\sum ", '∏' => "\\prod ", '∫' => "\\int ", '∬' => "\\iint ",
        '∭' => "\\iiint ", '∮' => "\\oint ", '∐' => "\\coprod ", '⋀' => "\\bigwedge ",
        '⋁' => "\\bigvee ", '⋂' => "\\bigcap ", '⋃' => "\\bigcup ",
        _ => null,
    };

    private static string NaryChrToLatex(string chr)
    {
        if (chr.Length > 0)
            switch (chr[0])
            {
                case '∑': return "\\sum";
                case '∏': return "\\prod";
                case '∐': return "\\coprod";
                case '∫': return "\\int";
                case '∬': return "\\iint";
                case '∭': return "\\iiint";
                case '∮': return "\\oint";
                case '⋀': return "\\bigwedge";
                case '⋁': return "\\bigvee";
                case '⋂': return "\\bigcap";
                case '⋃': return "\\bigcup";
            }
        return chr;
    }

    private static string DelimChrToLatex(string chr) => chr switch
    {
        "(" or ")" or "[" or "]" => chr,
        "{" => "\\{",
        "}" => "\\}",
        "|" => "|",
        "‖" => "\\|",
        "〈" or "⟨" => "\\langle",
        "〉" or "⟩" => "\\rangle",
        "⌊" => "\\lfloor",
        "⌋" => "\\rfloor",
        "⌈" => "\\lceil",
        "⌉" => "\\rceil",
        "" => ".",
        _ => chr,
    };

    private static string DelimSepToLatex(string sep) => sep == "|" ? " \\mid " : sep;

    private static string AccentChrToLatex(string chr)
    {
        if (chr.Length > 0)
            switch (chr[0])
            {
                case '̂': case '^': return "\\hat";
                case '̃': case '~': return "\\tilde";
                case '̄': case '̅': return "\\bar";
                case '⃗': case '→': return "\\vec";
                case '̇': return "\\dot";
                case '̈': return "\\ddot";
                case '̌': return "\\check";
                case '̆': return "\\breve";
                case '́': return "\\acute";
                case '̀': return "\\grave";
            }
        return "\\hat";
    }
}
