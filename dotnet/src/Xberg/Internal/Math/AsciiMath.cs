// Derived from `mathemascii` 0.4.0 and `alemat` 0.8.0 (Copyright Nadir Fejzic,
// https://github.com/nfejzic/mathemascii, https://github.com/nfejzic/alemat), both licensed under
// the Apache License 2.0. This file is a modified translation of those crates into C#; see
// ../../../../THIRD_PARTY_NOTICES.md and ../../../../third_party/LICENSE-Apache-2.0.txt.
//
// Ported from the `mathemascii` 0.4.0 crate and the slice of `alemat` 0.8.0 it renders through,
// which together are how upstream turns AsciiMath into MathML
// (crates/xberg/src/extraction/asciimath.rs). The MathML then goes through this port's shared
// MathML-to-LaTeX converter, so AsciiMath inherits every fix that converter carries.
//
// The writer is `alemat`'s `BufMathMlWriter`, the one `render_mathml` uses. It writes an
// attribute separator per attribute, so a tag with none has no space — except `munder`,
// `mover` and `munderover`, which write the separator before the loop and so always carry a
// trailing space. A table's `columnlines` list likewise ends with a trailing space. Both are
// reproduced: the goldens were produced by that writer.
//
// It also escapes nothing, so a `<` in the source reaches the output as a raw `<`. That is
// reproduced rather than fixed: upstream's MathML converter then fails to parse it and drops the
// equation, which is what the goldens record.
using System.Collections.Generic;
using System.Text;

namespace Xberg.Internal.MathMarkup;

// ── the MathML element tree ──────────────────────────────────────────────────────

internal abstract class AmEl
{
    public abstract void Write(StringBuilder sb);

    public static void WriteAll(StringBuilder sb, List<AmEl> els)
    {
        foreach (var el in els) el.Write(sb);
    }

    /// <summary>Wrap in a row where the builder does: more than one element, never exactly one.</summary>
    public static List<AmEl> RowIfMany(List<AmEl> els) =>
        els.Count > 1 ? new List<AmEl> { new AmRow(els) } : els;
}

internal sealed class AmLeaf(string tag, string text) : AmEl
{
    public string Tag => tag;
    public string Text => text;
    public override void Write(StringBuilder sb) =>
        sb.Append('<').Append(tag).Append('>').Append(text).Append("</").Append(tag).Append('>');
}

internal static class AmMake
{
    public static AmEl Ident(string text) => new AmLeaf("mi", text);
    public static AmEl Op(string text) => new AmLeaf("mo", text);
    public static AmEl Num(string text) => new AmLeaf("mn", text);
    public static AmEl Text(string text) => new AmLeaf("mtext", text);

    /// <summary>An <c>&lt;mi&gt;</c> or <c>&lt;mo&gt;</c> as the symbol table asks for.</summary>
    public static AmEl Symbol((bool Ident, string Text) sym) =>
        sym.Ident ? Ident(sym.Text) : Op(sym.Text);

    public static List<AmEl> One(AmEl el) => new() { el };
}

internal sealed class AmRow(List<AmEl> children) : AmEl
{
    public override void Write(StringBuilder sb)
    {
        sb.Append("<mrow>");
        WriteAll(sb, children);
        sb.Append("</mrow>");
    }
}

internal sealed class AmPhantom(List<AmEl> children) : AmEl
{
    public override void Write(StringBuilder sb)
    {
        sb.Append("<mphantom>");
        WriteAll(sb, children);
        sb.Append("</mphantom>");
    }
}

/// <summary><c>mstyle</c>; <paramref name="attr"/> is the rendered attribute, always present.</summary>
internal sealed class AmStyle(List<AmEl> children, string attr) : AmEl
{
    public override void Write(StringBuilder sb)
    {
        sb.Append("<mstyle ").Append(attr).Append('>');
        WriteAll(sb, children);
        sb.Append("</mstyle>");
    }
}

internal sealed class AmFrac(List<AmEl> num, List<AmEl> denom) : AmEl
{
    private readonly List<AmEl> _num = RowIfMany(num);
    private readonly List<AmEl> _denom = RowIfMany(denom);

    public override void Write(StringBuilder sb)
    {
        sb.Append("<mfrac>");
        WriteAll(sb, _num);
        WriteAll(sb, _denom);
        sb.Append("</mfrac>");
    }
}

internal sealed class AmRadical : AmEl
{
    private readonly List<AmEl> _index;
    private readonly List<AmEl> _content;

    public AmRadical(List<AmEl> index, List<AmEl> content)
    {
        _index = RowIfMany(index);
        _content = content;
        // `mroot` takes exactly two children, so only a real root wraps its body; `msqrt` takes
        // any number and leaves it alone.
        if (!IsSquare) _content = RowIfMany(_content);
    }

    /// <summary>An index of exactly the number 2 means <c>msqrt</c>, which drops the index.</summary>
    private bool IsSquare =>
        _index.Count == 1 && _index[0] is AmLeaf { Tag: "mn" } n
        && float.TryParse(n.Text, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out float v) && v == 2f;

    public override void Write(StringBuilder sb)
    {
        if (IsSquare)
        {
            sb.Append("<msqrt>");
            WriteAll(sb, _content);
            sb.Append("</msqrt>");
        }
        else
        {
            sb.Append("<mroot>");
            WriteAll(sb, _content);
            WriteAll(sb, _index);
            sb.Append("</mroot>");
        }
    }
}

internal sealed class AmSubSup(List<AmEl> baseEl, List<AmEl>? sub, List<AmEl>? sup) : AmEl
{
    private readonly List<AmEl> _base = RowIfMany(baseEl);
    private readonly List<AmEl>? _sub = sub is null ? null : RowIfMany(sub);
    private readonly List<AmEl>? _sup = sup is null ? null : RowIfMany(sup);

    public override void Write(StringBuilder sb)
    {
        string tag = (_sub, _sup) switch
        {
            (null, not null) => "msup",
            (not null, null) => "msub",
            _ => "msubsup",
        };
        sb.Append('<').Append(tag).Append('>');
        WriteAll(sb, _base);
        if (_sub is not null) WriteAll(sb, _sub);
        if (_sup is not null) WriteAll(sb, _sup);
        sb.Append("</").Append(tag).Append('>');
    }
}

internal sealed class AmUnderOver(List<AmEl> expr, List<AmEl>? under, List<AmEl>? over) : AmEl
{
    private readonly List<AmEl> _expr = RowIfMany(expr);
    private readonly List<AmEl>? _under = under is null ? null : RowIfMany(under);
    private readonly List<AmEl>? _over = over is null ? null : RowIfMany(over);

    public override void Write(StringBuilder sb)
    {
        string tag = (_under, _over) switch
        {
            (null, not null) => "mover",
            (not null, null) => "munder",
            _ => "munderover",
        };
        // The writer emits the attribute separator before the (here always empty) attribute
        // list, so the tag keeps a trailing space.
        sb.Append('<').Append(tag).Append(" >");
        WriteAll(sb, _expr);
        if (_under is not null) WriteAll(sb, _under);
        if (_over is not null) WriteAll(sb, _over);
        sb.Append("</").Append(tag).Append('>');
    }
}

internal sealed class AmTable(List<List<List<AmEl>>> rows, List<string> columnLines) : AmEl
{
    public override void Write(StringBuilder sb)
    {
        sb.Append("<mtable columnlines=\"");
        foreach (string line in columnLines) sb.Append(line).Append(' ');
        sb.Append("\">");
        foreach (var row in rows)
        {
            sb.Append("<mtr>");
            foreach (var cell in row)
            {
                sb.Append("<mtd>");
                WriteAll(sb, cell);
                sb.Append("</mtd>");
            }
            sb.Append("</mtr>");
        }
        sb.Append("</mtable>");
    }
}

// ── the abstract syntax tree ────────────────────────────────────────────────────────────────

internal enum AmVarKind { Function, Number, Greek, Variable, Arrow, Relation, Logical, Operator, UnknownOperator, Other, Text }

internal sealed class AmVar
{
    public AmVarKind Kind;
    public int Value;        // the payload enum for Kind, where it has one
    public string Text = ""; // the source text, for the kinds that carry it

    public bool IsComma => Kind == AmVarKind.Other && (AmOther)Value == AmOther.Comma;
}

internal enum AmUnaryKind
{
    Hat, Overline, Underline, Vector, Tilde, Dot, DoubleDot, Underbrace, Overbrace, Cancel,
    SquareRoot, Absolute, Floor, Ceiling, Norm,
    Bold, BlackboardBold, Calligraphic, Typewriter, Gothic, SansSerif,
}

internal enum AmBinaryKind { Fraction, Root, Overset, Underset, Color }

internal abstract class AmSimpleExpr;

internal sealed class AmVarExpr(AmVar var) : AmSimpleExpr { public AmVar Var = var; }

internal sealed class AmGroupingExpr : AmSimpleExpr
{
    public AmGrouping Left, Right;
    public List<AmExpression> Exprs = new();
}

internal sealed class AmUnaryExpr : AmSimpleExpr
{
    public AmUnaryKind Kind;
    public AmSimpleExpr Expr = null!;
}

internal sealed class AmBinaryExpr : AmSimpleExpr
{
    public AmBinaryKind Kind;
    public AmSimpleExpr Expr1 = null!, Expr2 = null!;
}

internal sealed class AmInterm(AmExpression inner) : AmSimpleExpr { public AmExpression Inner = inner; }

internal sealed class AmExpression
{
    public AmSimpleExpr Interm = null!;
    public AmSimpleExpr? Subscript;
    public AmSimpleExpr? Supscript;

    public bool IsScripted => Subscript is not null || Supscript is not null;
    public bool IsComma => Interm is AmVarExpr v && v.Var.IsComma;
}

// ── the parser ──────────────────────────────────────────────────────────────────────────────

internal sealed class AmParser(List<AmToken> tokens)
{
    private int _i;

    private AmToken? Peek() => _i < tokens.Count ? tokens[_i] : null;
    private AmToken? Next() => _i < tokens.Count ? tokens[_i++] : null;

    /// <summary>Map a token to the unary operator it introduces, if it is one.</summary>
    public static AmUnaryKind? UnaryKindOf(AmToken token) => token.Kind switch
    {
        AmTok.Accent => token.Accent switch
        {
            AmAccent.Hat => AmUnaryKind.Hat,
            AmAccent.Overline => AmUnaryKind.Overline,
            AmAccent.Underline => AmUnaryKind.Underline,
            AmAccent.Vector => AmUnaryKind.Vector,
            AmAccent.Tilde => AmUnaryKind.Tilde,
            AmAccent.Dot => AmUnaryKind.Dot,
            AmAccent.DoubleDot => AmUnaryKind.DoubleDot,
            AmAccent.Underbrace => AmUnaryKind.Underbrace,
            AmAccent.Overbrace => AmUnaryKind.Overbrace,
            AmAccent.Cancel => AmUnaryKind.Cancel,
            _ => null,
        },
        AmTok.FontCommand => token.FontCommand switch
        {
            AmFontCommand.Bold => AmUnaryKind.Bold,
            AmFontCommand.BlackboardBold => AmUnaryKind.BlackboardBold,
            AmFontCommand.Calligraphic => AmUnaryKind.Calligraphic,
            AmFontCommand.Typewriter => AmUnaryKind.Typewriter,
            AmFontCommand.Gothic => AmUnaryKind.Gothic,
            _ => AmUnaryKind.SansSerif,
        },
        AmTok.Other => token.Other == AmOther.SquareRoot ? AmUnaryKind.SquareRoot : null,
        AmTok.Grouping => token.Grouping switch
        {
            AmGrouping.Absolute => AmUnaryKind.Absolute,
            AmGrouping.Floor => AmUnaryKind.Floor,
            AmGrouping.Ceiling => AmUnaryKind.Ceiling,
            AmGrouping.NormFn => AmUnaryKind.Norm,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Map a token to the binary operator it introduces, if it is one.</summary>
    public static AmBinaryKind? BinaryKindOf(AmToken token) => token.Kind switch
    {
        AmTok.Accent => token.Accent switch
        {
            AmAccent.Overset => AmBinaryKind.Overset,
            AmAccent.Underset => AmBinaryKind.Underset,
            AmAccent.Color => AmBinaryKind.Color,
            _ => null,
        },
        AmTok.Other => token.Other switch
        {
            AmOther.Fraction => AmBinaryKind.Fraction,
            AmOther.Root => AmBinaryKind.Root,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Two grouping symbols that close each other.</summary>
    private static bool Matches(AmGrouping open, AmGrouping close) => (open, close) switch
    {
        (AmGrouping.OpenParen, AmGrouping.CloseParen) => true,
        (AmGrouping.OpenParen, AmGrouping.CloseIgnored) => true,
        (AmGrouping.CloseParen, AmGrouping.OpenParen) => true,
        (AmGrouping.CloseParen, AmGrouping.OpenIgnored) => true,
        (AmGrouping.OpenBracket, AmGrouping.CloseBracket) => true,
        (AmGrouping.OpenBracket, AmGrouping.CloseIgnored) => true,
        (AmGrouping.CloseBracket, AmGrouping.OpenBracket) => true,
        (AmGrouping.CloseBracket, AmGrouping.OpenIgnored) => true,
        (AmGrouping.OpenBrace, AmGrouping.CloseBrace) => true,
        (AmGrouping.OpenBrace, AmGrouping.CloseIgnored) => true,
        (AmGrouping.CloseBrace, AmGrouping.OpenBrace) => true,
        (AmGrouping.CloseBrace, AmGrouping.OpenIgnored) => true,
        (AmGrouping.LeftAngled, AmGrouping.RightAngled) => true,
        (AmGrouping.LeftAngled, AmGrouping.CloseIgnored) => true,
        (AmGrouping.RightAngled, AmGrouping.LeftAngled) => true,
        (AmGrouping.RightAngled, AmGrouping.OpenIgnored) => true,
        (AmGrouping.OpenIgnored, AmGrouping.CloseParen) => true,
        (AmGrouping.OpenIgnored, AmGrouping.CloseBracket) => true,
        (AmGrouping.OpenIgnored, AmGrouping.CloseBrace) => true,
        (AmGrouping.OpenIgnored, AmGrouping.RightAngled) => true,
        (AmGrouping.OpenIgnored, AmGrouping.CloseIgnored) => true,
        (AmGrouping.CloseIgnored, AmGrouping.OpenParen) => true,
        (AmGrouping.CloseIgnored, AmGrouping.OpenBracket) => true,
        (AmGrouping.CloseIgnored, AmGrouping.OpenBrace) => true,
        (AmGrouping.CloseIgnored, AmGrouping.LeftAngled) => true,
        (AmGrouping.CloseIgnored, AmGrouping.OpenIgnored) => true,
        (AmGrouping.Absolute, AmGrouping.Absolute) => true,
        (AmGrouping.Floor, AmGrouping.Floor) => true,
        (AmGrouping.Ceiling, AmGrouping.Ceiling) => true,
        (AmGrouping.Norm, AmGrouping.Norm) => true,
        _ => false,
    };

    private static AmVar VarOf(AmToken token) => token.Kind switch
    {
        AmTok.Function => new AmVar { Kind = AmVarKind.Function, Value = token.Value },
        AmTok.Number => new AmVar { Kind = AmVarKind.Number, Text = token.Content },
        AmTok.Greek => new AmVar { Kind = AmVarKind.Greek, Value = token.Value },
        AmTok.Variable => new AmVar { Kind = AmVarKind.Variable, Text = token.Content },
        AmTok.Arrow => new AmVar { Kind = AmVarKind.Arrow, Value = token.Value },
        AmTok.Relation => new AmVar { Kind = AmVarKind.Relation, Value = token.Value },
        AmTok.Logical => new AmVar { Kind = AmVarKind.Logical, Value = token.Value },
        AmTok.Operator => new AmVar { Kind = AmVarKind.Operator, Value = token.Value },
        AmTok.UnknownOperator => new AmVar { Kind = AmVarKind.UnknownOperator, Text = token.Content },
        AmTok.Other when (AmOther)token.Value == AmOther.Text =>
            new AmVar { Kind = AmVarKind.Text, Text = token.Content },
        AmTok.Other => new AmVar { Kind = AmVarKind.Other, Value = token.Value },
        _ => new AmVar { Kind = AmVarKind.UnknownOperator, Text = token.Content },
    };

    /// <summary>
    /// Read a bracketed group as literal text — how <c>color(red)(x)</c> gets its colour name.
    /// </summary>
    private AmSimpleExpr? ParseGroupingAsStr()
    {
        var token = Next();
        if (token is null || token.Value.Kind != AmTok.Grouping) return null;
        AmGrouping opening = token.Value.Grouping;

        var content = new StringBuilder();
        while (true)
        {
            var inner = Next();
            if (inner is null) return null;
            if (inner.Value.Kind == AmTok.Grouping && Matches(opening, inner.Value.Grouping)) break;
            content.Append(inner.Value.Content);
        }
        return new AmVarExpr(new AmVar { Kind = AmVarKind.Text, Text = content.ToString() });
    }

    private AmSimpleExpr? ParseSimpleExpr()
    {
        var peeked = Peek();
        if (peeked is null) return null;
        var token = peeked.Value;

        // A bracket that is not also a unary or binary operator opens a group.
        if (token.IsGroupingOpen && UnaryKindOf(token) is null && BinaryKindOf(token) is null)
        {
            AmGrouping grouping = token.Grouping;
            Next();

            var exprs = new List<AmExpression>();
            AmGrouping right;
            while (true)
            {
                var expr = ParseExpr();
                if (expr is null) { right = AmGrouping.CloseIgnored; break; }
                exprs.Add(expr);

                // At the end of input the closer is the ignored bracket; a token that is not a
                // bracket at all is part of the group, so the scan continues.
                var ahead = Peek();
                if (ahead is not null && ahead.Value.Kind != AmTok.Grouping) continue;
                AmGrouping candidate = ahead?.Grouping ?? AmGrouping.CloseIgnored;
                if (Matches(grouping, candidate))
                {
                    if (ahead is not null) Next();
                    right = candidate;
                    break;
                }
            }
            return new AmGroupingExpr { Left = grouping, Right = right, Exprs = exprs };
        }

        if (UnaryKindOf(token) is { } unaryKind) return ParseUnary(unaryKind);
        if (BinaryKindOf(token) is { } binaryKind) return ParseBinary(binaryKind);

        var varToken = Next();
        return varToken is null ? null : new AmVarExpr(VarOf(varToken.Value));
    }

    private static AmSimpleExpr EmptyOperator() =>
        new AmVarExpr(new AmVar { Kind = AmVarKind.UnknownOperator, Text = "" });

    private AmSimpleExpr ParseUnary(AmUnaryKind kind)
    {
        Next();   // skip the operator
        return new AmUnaryExpr { Kind = kind, Expr = ParseSimpleExpr() ?? EmptyOperator() };
    }

    private AmSimpleExpr ParseBinary(AmBinaryKind kind)
    {
        Next();   // skip the operator
        AmSimpleExpr expr1 = kind == AmBinaryKind.Color
            ? ParseGroupingAsStr() ?? new AmVarExpr(new AmVar { Kind = AmVarKind.Text, Text = "black" })
            : ParseSimpleExpr() ?? EmptyOperator();
        return new AmBinaryExpr { Kind = kind, Expr1 = expr1, Expr2 = ParseSimpleExpr() ?? EmptyOperator() };
    }

    private AmExpression? ParseIntermExpr()
    {
        var simple = ParseSimpleExpr();
        if (simple is null) return null;

        AmSimpleExpr? subscript = null;
        if (Peek() is { Kind: AmTok.Other } sub && sub.Other == AmOther.Subscript)
        {
            Next();
            subscript = ParseSimpleExpr();
        }

        AmSimpleExpr? supscript = null;
        if (Peek() is { Kind: AmTok.Other } sup && sup.Other == AmOther.Power)
        {
            Next();
            supscript = ParseSimpleExpr();
        }

        return new AmExpression { Interm = simple, Subscript = subscript, Supscript = supscript };
    }

    public AmExpression? ParseExpr()
    {
        var interm = ParseIntermExpr();
        if (interm is null) return null;

        if (Peek() is { Kind: AmTok.Other } slash && slash.Other == AmOther.ForwardSlash)
        {
            Next();
            var denominator = ParseIntermExpr() ?? new AmExpression
            {
                Interm = new AmVarExpr(new AmVar { Kind = AmVarKind.Text, Text = "" }),
            };

            // A scripted operand is treated as if it were parenthesised, so `a_b/c_d` is
            // `frac{a_b}{c_d}` rather than `a_(b/c)_d`.
            AmSimpleExpr num = interm.IsScripted ? new AmInterm(interm) : interm.Interm;
            AmSimpleExpr den = denominator.IsScripted ? new AmInterm(denominator) : denominator.Interm;

            return new AmExpression
            {
                Interm = new AmBinaryExpr { Kind = AmBinaryKind.Fraction, Expr1 = num, Expr2 = den },
            };
        }
        return interm;
    }

    public List<AmExpression> ParseAll()
    {
        var exprs = new List<AmExpression>();
        while (ParseExpr() is { } expr) exprs.Add(expr);
        return exprs;
    }
}

// ── the AST to MathML conversion ────────────────────────────────────────────────────────────

internal static class AmRender
{
    /// <summary>Wrap a run of elements in a row when there is more than one.</summary>
    private static List<AmEl> Row(List<AmEl> els) => AmEl.RowIfMany(els);

    private static List<AmEl> VarElements(AmVar v)
    {
        switch (v.Kind)
        {
            case AmVarKind.Function: return AmMake.One(AmMake.Ident(AmTables.FunctionAsStr((AmFunction)v.Value)));
            case AmVarKind.Greek: return AmMake.One(AmMake.Symbol(AmSymbols.Greek((AmGreek)v.Value)));
            case AmVarKind.Variable: return AmMake.One(AmMake.Ident(v.Text));
            case AmVarKind.Relation: return AmMake.One(AmMake.Symbol(AmSymbols.Relation((AmRelation)v.Value)));
            case AmVarKind.Logical: return AmMake.One(AmMake.Symbol(AmSymbols.Logical((AmLogical)v.Value)));
            case AmVarKind.Operator: return AmMake.One(AmMake.Symbol(AmSymbols.Operator((AmOperator)v.Value)));
            case AmVarKind.Arrow: return AmMake.One(AmMake.Symbol(AmSymbols.Arrow((AmArrow)v.Value)));
            case AmVarKind.Text: return AmMake.One(AmMake.Text(v.Text));
            case AmVarKind.Number: return AmMake.One(AmMake.Num(v.Text));
            case AmVarKind.UnknownOperator: return AmMake.One(AmMake.Op(v.Text));
            default: return OtherElements((AmOther)v.Value);
        }
    }

    /// <summary>
    /// The two doubled-bar symbols render as three operators in a row; every other listed symbol
    /// is one element, and an unlisted one falls back to the keyword's own first spelling.
    /// </summary>
    private static List<AmEl> OtherElements(AmOther other)
    {
        if (other is AmOther.VerticalBars or AmOther.VerticalBarsWide)
        {
            string gap = other == AmOther.VerticalBars ? " " : "  ";
            return AmMake.One(new AmRow(new List<AmEl>
            {
                AmMake.Op("|"), AmMake.Op(gap), AmMake.Op("|"),
            }));
        }
        var sym = AmSymbols.Other(other);
        return AmMake.One(sym is null ? AmMake.Op(AmTables.OtherAsStr(other)) : AmMake.Symbol(sym.Value));
    }

    /// <summary>A grouping symbol on the side it appears on: floor and ceiling differ by side.</summary>
    private static AmEl GroupingElement(AmGrouping grp, bool isOpening) => grp switch
    {
        AmGrouping.OpenParen => AmMake.Op("("),
        AmGrouping.CloseParen => AmMake.Op(")"),
        AmGrouping.OpenBracket => AmMake.Op("["),
        AmGrouping.CloseBracket => AmMake.Op("]"),
        AmGrouping.OpenBrace => AmMake.Op("{"),
        AmGrouping.CloseBrace => AmMake.Op("}"),
        AmGrouping.LeftAngled => AmMake.Op("⟨"),
        AmGrouping.RightAngled => AmMake.Op("⟩"),
        AmGrouping.OpenIgnored => new AmPhantom(AmMake.One(AmMake.Op("{"))),
        AmGrouping.CloseIgnored => new AmPhantom(AmMake.One(AmMake.Op("}"))),
        AmGrouping.Absolute => AmMake.Op("|"),
        AmGrouping.Floor => AmMake.Op(isOpening ? "⌊" : "⌋"),
        AmGrouping.Ceiling => AmMake.Op(isOpening ? "⌈" : "⌉"),
        _ => AmMake.Op("∥"),
    };

    /// <summary>The group's own expressions, without its brackets.</summary>
    private static List<AmEl> UngroupElements(AmGroupingExpr grp)
    {
        var els = new List<AmEl>();
        foreach (var e in grp.Exprs) els.AddRange(ExprElements(e));
        return els;
    }

    /// <summary>The group with its brackets, but not wrapped in a row.</summary>
    private static List<AmEl> GroupElements(AmGroupingExpr grp)
    {
        var els = new List<AmEl> { GroupingElement(grp.Left, true) };
        els.AddRange(UngroupElements(grp));
        els.Add(GroupingElement(grp.Right, false));
        return els;
    }

    /// <summary>
    /// Parentheses, brackets, braces and the ignored bracket are "simple": a unary or binary
    /// operator drops them, since the operator supplies its own delimiters.
    /// </summary>
    private static bool IsSimpleGrp(AmGroupingExpr grp) =>
        grp.Left is AmGrouping.OpenParen or AmGrouping.OpenBracket or AmGrouping.OpenBrace or AmGrouping.OpenIgnored
        && grp.Right is AmGrouping.CloseParen or AmGrouping.CloseBracket or AmGrouping.CloseBrace or AmGrouping.CloseIgnored;

    /// <summary>The operand of a unary or binary operator, with a simple bracket pair removed.</summary>
    private static List<AmEl> OperandElements(AmSimpleExpr expr) => expr switch
    {
        AmGroupingExpr grp when IsSimpleGrp(grp) => UngroupElements(grp),
        AmGroupingExpr grp => GroupElements(grp),
        _ => SimpleElements(expr),
    };

    private static List<AmEl> SimpleElements(AmSimpleExpr expr)
    {
        switch (expr)
        {
            case AmVarExpr v: return VarElements(v.Var);
            case AmGroupingExpr grp: return AmMake.One(new AmRow(GroupElements(grp)));
            case AmUnaryExpr un: return UnaryElements(un);
            case AmBinaryExpr bin: return BinaryElements(bin);
            case AmInterm inner: return ExprElements(inner.Inner);
            default: return new List<AmEl>();
        }
    }

    private static List<AmEl> UnaryElements(AmUnaryExpr un)
    {
        var inner = OperandElements(un.Expr);

        List<AmEl> Accent(List<AmEl>? under, List<AmEl>? over) =>
            AmMake.One(new AmUnderOver(inner, under, over));
        List<AmEl> Fenced(string left, string right)
        {
            var els = new List<AmEl> { AmMake.Op(left) };
            els.AddRange(inner);
            els.Add(AmMake.Op(right));
            return els;
        }
        List<AmEl> Styled(string variant) =>
            AmMake.One(new AmStyle(inner, $"mathvariant=\"{variant}\""));

        switch (un.Kind)
        {
            case AmUnaryKind.Hat: return Accent(null, AmMake.One(AmMake.Op("^")));
            case AmUnaryKind.Overline: return Accent(null, AmMake.One(AmMake.Op("¯")));
            case AmUnaryKind.Underline: return Accent(AmMake.One(AmMake.Op("¯")), null);
            case AmUnaryKind.Vector: return Accent(null, AmMake.One(AmMake.Op("→")));
            case AmUnaryKind.Tilde: return Accent(null, AmMake.One(AmMake.Op("~")));
            case AmUnaryKind.Dot: return Accent(null, AmMake.One(AmMake.Op("⋅")));
            case AmUnaryKind.DoubleDot: return Accent(null, AmMake.One(AmMake.Op("¨")));
            case AmUnaryKind.Underbrace: return Accent(AmMake.One(AmMake.Op("⏟")), null);
            case AmUnaryKind.Overbrace: return Accent(null, AmMake.One(AmMake.Op("⏞")));
            // `<menclose>` is non-standard, so the crate leaves this arm unimplemented and
            // panics. Upstream contains the panic and drops the equation.
            case AmUnaryKind.Cancel:
                throw new AsciiMathPanic("cancel renders <menclose>, which the crate leaves unimplemented");
            case AmUnaryKind.SquareRoot:
                return AmMake.One(new AmRadical(AmMake.One(AmMake.Num("2")), inner));
            case AmUnaryKind.Absolute: return Fenced("|", "|");
            case AmUnaryKind.Floor: return Fenced("⌊", "⌋");
            case AmUnaryKind.Ceiling: return Fenced("⌈", "⌉");
            case AmUnaryKind.Norm: return AmMake.One(new AmRow(Fenced("∥", "∥")));
            case AmUnaryKind.Bold: return Styled("bold");
            case AmUnaryKind.BlackboardBold: return Styled("double-struck");
            case AmUnaryKind.Calligraphic: return Styled("script");
            case AmUnaryKind.Typewriter: return Styled("monospace");
            case AmUnaryKind.Gothic: return Styled("fraktur");
            default: return Styled("sans-serif");
        }
    }

    private static List<AmEl> BinaryElements(AmBinaryExpr bin)
    {
        switch (bin.Kind)
        {
            case AmBinaryKind.Fraction:
                return AmMake.One(new AmFrac(OperandElements(bin.Expr1), OperandElements(bin.Expr2)));
            case AmBinaryKind.Root:
                return AmMake.One(new AmRadical(OperandElements(bin.Expr1), OperandElements(bin.Expr2)));
            case AmBinaryKind.Overset:
                return AmMake.One(new AmUnderOver(OperandElements(bin.Expr2), null, OperandElements(bin.Expr1)));
            case AmBinaryKind.Underset:
                return AmMake.One(new AmUnderOver(OperandElements(bin.Expr2), OperandElements(bin.Expr1), null));
            default:
            {
                // The parser guarantees a text variable here; the crate panics otherwise.
                if (bin.Expr1 is not AmVarExpr { Var.Kind: AmVarKind.Text } colour)
                    throw new AsciiMathPanic("color expects a colour name");
                return AmMake.One(new AmStyle(OperandElements(bin.Expr2),
                                              $"mathcolor=\"{colour.Var.Text}\""));
            }
        }
    }

    /// <summary>
    /// Operators whose scripts sit above and below rather than beside — the big operators and
    /// <c>lim</c>, plus the two brace accents.
    /// </summary>
    private static bool IsUnderOver(AmSimpleExpr expr) => expr switch
    {
        AmVarExpr { Var.Kind: AmVarKind.Operator } v =>
            (AmOperator)v.Var.Value is AmOperator.Sum or AmOperator.Prod or AmOperator.BigCap
                or AmOperator.BigCup or AmOperator.BigWedge,
        AmVarExpr { Var.Kind: AmVarKind.Function } v => (AmFunction)v.Var.Value == AmFunction.Lim,
        AmUnaryExpr un => un.Kind is AmUnaryKind.Underbrace or AmUnaryKind.Overbrace,
        _ => false,
    };

    /// <summary>A script that is a bracketed group loses its brackets.</summary>
    private static List<AmEl> ScriptElements(AmSimpleExpr expr) =>
        expr is AmGroupingExpr grp ? UngroupElements(grp) : SimpleElements(expr);

    public static List<AmEl> ExprElements(AmExpression expr)
    {
        if (IsMatrix(expr)) return MatrixElements(expr);

        bool isUnderOver = IsUnderOver(expr.Interm);
        if (!expr.IsScripted) return SimpleElements(expr.Interm);

        var inner = SimpleElements(expr.Interm);
        var sub = expr.Subscript is null ? null : ScriptElements(expr.Subscript);
        var sup = expr.Supscript is null ? null : ScriptElements(expr.Supscript);

        return AmMake.One(isUnderOver
            ? new AmUnderOver(inner, sub, sup)
            : new AmSubSup(inner, sub, sup));
    }

    // ── matrices ────────────────────────────────────────────────────────────────────────────
    // A bracketed group whose every element is itself a bracketed group of equal length is a
    // matrix. A column holding nothing but `|` is a rule between columns rather than a column.

    /// <summary>Split a group's expressions on commas, dropping the commas and empty runs.</summary>
    private static List<List<AmExpression>> GroupByCommas(List<AmExpression> exprs)
    {
        var groups = new List<List<AmExpression>>();
        var current = new List<AmExpression>();
        foreach (var e in exprs)
        {
            if (e.IsComma)
            {
                if (current.Count > 0) { groups.Add(current); current = new List<AmExpression>(); }
                continue;
            }
            current.Add(e);
        }
        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private static bool IsVerticalBar(AmExpression e) =>
        e.Interm is AmVarExpr { Var.Kind: AmVarKind.Other } v && (AmOther)v.Var.Value == AmOther.VerticalBar;

    /// <summary>Brackets that can hold a matrix — not braces, and not any of the fences.</summary>
    private static bool IsMatrixGrp(AmGroupingExpr grp) =>
        !(grp.Left == AmGrouping.OpenBrace && grp.Right == AmGrouping.CloseBrace)
        && grp.Left is not (AmGrouping.Absolute or AmGrouping.Floor or AmGrouping.Ceiling
            or AmGrouping.NormFn or AmGrouping.Norm)
        && grp.Right is not (AmGrouping.Absolute or AmGrouping.Floor or AmGrouping.Ceiling
            or AmGrouping.NormFn or AmGrouping.Norm);

    private static bool IsMatrix(AmExpression expr)
    {
        if (expr.Interm is not AmGroupingExpr grp || !IsMatrixGrp(grp)) return false;

        int len = 0;
        foreach (var e in grp.Exprs)
        {
            if (e.IsComma) continue;
            if (e.Interm is not AmGroupingExpr row) return false;
            if (len == 0) len = GroupByCommas(row.Exprs).Count;
            else if (len != row.Exprs.FindAll(x => x.IsComma).Count + 1) return false;
        }
        // Every row must hold something, and all rows must hold the same number of cells.
        return len != 0;
    }

    private static List<AmEl> MatrixElements(AmExpression expr)
    {
        var grp = (AmGroupingExpr)expr.Interm;
        if (grp.Exprs.Count == 0) throw new AsciiMathPanic("matrix with no rows");
        if (grp.Exprs[0].Interm is not AmGroupingExpr firstRow)
            throw new AsciiMathPanic("first matrix row is not a group");

        // Every column starts out ruled; a column that turns out to hold cells rather than bars
        // has its rule cleared as the rows are read.
        int columns = GroupByCommas(firstRow.Exprs).Count;
        var columnLines = new List<string>();
        for (int i = 0; i < columns; i++) columnLines.Add("solid");

        var rows = new List<List<List<AmEl>>>();
        int maxLen = 0;
        bool lastWasLine = true;

        foreach (var rowExpr in grp.Exprs)
        {
            if (rowExpr.IsComma) continue;
            if (rowExpr.Interm is not AmGroupingExpr row) throw new AsciiMathPanic("matrix row is not a group");

            var cells = new List<List<AmEl>>();
            int inserted = 0;
            bool prevLine = false;
            lastWasLine &= row.Exprs.Count > 0 && IsVerticalBar(row.Exprs[^1]);

            var groups = GroupByCommas(row.Exprs);
            for (int curr = 0; curr < groups.Count; curr++)
            {
                var e = groups[curr];
                bool isLine = e.Count == 1 && IsVerticalBar(e[0]);
                if (inserted != curr && !isLine)
                {
                    if (!prevLine)
                    {
                        if (inserted >= columnLines.Count) throw new AsciiMathPanic("column index out of range");
                        columnLines[inserted] = "none";
                    }
                    prevLine = false;
                }
                else if (isLine)
                {
                    if (inserted != curr) prevLine = true;
                    continue;
                }

                List<AmEl> cell;
                if (e.Count >= 2)
                {
                    var joined = new List<AmEl>();
                    foreach (var one in e) joined.AddRange(ExprElements(one));
                    cell = AmMake.One(new AmRow(joined));
                }
                else if (e.Count == 1) cell = ExprElements(e[0]);
                else cell = AmMake.One(new AmPhantom(new List<AmEl>()));

                cells.Add(cell);
                inserted = cells.Count - 1;
            }

            if (cells.Count > maxLen) maxLen = cells.Count;
            rows.Add(cells);
        }

        if (columnLines.Count > maxLen) columnLines.RemoveRange(maxLen, columnLines.Count - maxLen);
        if (!lastWasLine)
        {
            if (maxLen == 0 || maxLen > columnLines.Count) throw new AsciiMathPanic("column index out of range");
            columnLines[maxLen - 1] = "none";
        }

        return AmMake.One(new AmRow(new List<AmEl>
        {
            GroupingElement(grp.Left, true),
            new AmTable(rows, columnLines),
            GroupingElement(grp.Right, false),
        }));
    }

    /// <summary>Render a parsed expression list as one <c>&lt;math&gt;</c> element.</summary>
    public static string RenderMathml(List<AmExpression> exprs)
    {
        var sb = new StringBuilder();
        sb.Append("<math>");
        foreach (var expr in exprs) AmEl.WriteAll(sb, ExprElements(expr));
        sb.Append("</math>");
        return sb.ToString();
    }
}

/// <summary>AsciiMath to LaTeX. Ported from crates/xberg/src/extraction/asciimath.rs.</summary>
internal static class AsciiMath
{
    /// <summary>
    /// Convert one AsciiMath expression to LaTeX, or return <c>null</c> when it yields no maths
    /// so the caller can keep the source text rather than emit an empty formula.
    /// </summary>
    /// <remarks>
    /// The conversion goes through MathML rather than a second hand-written mapping: the
    /// AsciiMath parser renders MathML and the shared MathML converter already turns that into
    /// LaTeX, so AsciiMath inherits the accent, fence and escaping fixes that converter carries.
    /// </remarks>
    public static string? ConvertToLatex(string source)
    {
        string trimmed = source.Trim();
        if (trimmed.Length == 0) return null;

        string mathml;
        try
        {
            mathml = AmRender.RenderMathml(new AmParser(new AmLexer(trimmed).Tokenize()).ParseAll());
        }
        catch (AsciiMathPanic)
        {
            // The parser scans by byte index and splits a multi-byte character, so a real
            // specification that writes `≤` aborts inside it. Upstream contains that panic and
            // loses the equation rather than the document; this port raises the same condition
            // explicitly and drops it here.
            return null;
        }

        string latex = MathMl.ConvertMathmlStrToLatex(mathml).Trim();
        return latex.Length == 0 ? null : latex;
    }
}
