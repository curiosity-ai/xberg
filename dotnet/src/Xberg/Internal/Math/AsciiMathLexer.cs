// Derived from `mathemascii` 0.4.0 (Copyright Nadir Fejzic, https://github.com/nfejzic/mathemascii),
// licensed under the Apache License 2.0. This file is a modified translation of that crate's
// scanner and lexer into C#; see ../../../../THIRD_PARTY_NOTICES.md and
// ../../../../third_party/LICENSE-Apache-2.0.txt.
//
// Ported from the `mathemascii` 0.4.0 crate, which upstream uses to turn AsciiMath into MathML
// (crates/xberg/src/extraction/asciimath.rs). This file is the crate's scanner and lexer; the
// parser and the MathML writer are in AsciiMath.cs.
//
// The port is deliberately literal, quirks included: the corpus's goldens were produced by this
// exact code, so a "better" tokenizer would disagree with them. Where a behaviour looks wrong
// rather than merely surprising, the comment says so.
using System.Collections.Generic;
using System.Text;

namespace Xberg.Internal.MathMarkup;

/// <summary>Kinds of token the AsciiMath lexer recognises.</summary>
internal enum AmTok
{
    Number, Greek, Arrow, Function, Operator, UnknownOperator, Relation, Logical,
    Grouping, Other, Accent, FontCommand, Variable,
}

internal enum AmGreek
{
    Alpha, Beta, Gamma, BigGamma, Delta, Epsilon, Varepsilon, Zeta, Eta, Theta, Vartheta, Iota,
    Kappa, Lambda, Mu, Nu, Xi, BigXi, Pi, BigPi, Rho, Sigma, BigSigma, Tau, Upsilon, Phi, BigPhi,
    Varphi, Chi, Psi, BigPsi, Omega, BigOmega,
}

internal enum AmArrow
{
    Up, Down, Right, RightTail, TwoHeadRight, TwoHeadRightTail, MapsTo, Left, LeftRight,
    BigRight, BigLeft, BigLeftRight,
}

internal enum AmFunction
{
    Sin, Cos, Tan, Sec, Csc, Cot, ArcSin, ArcCos, ArcTan, SinH, CosH, TanH, SecH, CscH, CotH,
    Exp, Lim, Log, Ln, Det, Dim, Mod, Gcd, Lcm, Lub, Glb, Min, Max, F, G,
}

internal enum AmOperator
{
    Plus, Minus, Prime, Dot, Asterisk, Star, ForwardSlashLiteral, Backslash, Times, Divide,
    LTimes, RTimes, Bowtie, Circle, OPlus, OTimes, ODot, Sum, Prod, Wedge, BigWedge, Cap, BigCap,
    Cup, BigCup,
}

internal enum AmRelation
{
    Eq, NotEq, Define, LessThan, GreaterThan, LessEqualThan, GreaterEqualThan, MuchLessThan,
    MuchGreaterThan, Prec, PrecEq, Succ, SuccEq, In, NotIn, Subset, Superset, SubsetEq, SupersetEq,
    Equivalent, Congruent, Approximate, Prop,
}

internal enum AmLogical
{
    And, Or, Not, Implies, If, IfAndOnlyIf, ForAll, Exists, Bottom, Top, VerticalDash, Models,
}

internal enum AmGrouping
{
    OpenParen, CloseParen, OpenBracket, CloseBracket, OpenBrace, CloseBrace, LeftAngled,
    RightAngled, OpenIgnored, CloseIgnored, Absolute, Floor, Ceiling, NormFn, Norm,
}

internal enum AmOther
{
    Comma, Fraction, ForwardSlash, Power, Subscript, SquareRoot, Root, Integral, OIntegral,
    Partial, Nabla, PlusMinus, EmptySet, Infinity, Aleph, Therefore, Because, LowDots, CenterDots,
    VerticalDots, DiagonalDots, VerticalBar, VerticalBars, VerticalBarsWide, Angle, Frown,
    Triangle, Diamond, Square, LeftFloor, RightFloor, LeftCeiling, RightCeiling, Complex, Natural,
    Rational, Irrational, Integer, Text, Quote,
}

internal enum AmAccent
{
    Hat, Overline, Underline, Vector, Tilde, Dot, DoubleDot, Overset, Underset, Underbrace,
    Overbrace, Color, Cancel,
}

internal enum AmFontCommand { Bold, BlackboardBold, Calligraphic, Typewriter, Gothic, SansSerif }

/// <summary>One token: its kind, the payload enum value for that kind, and its source text.</summary>
internal readonly record struct AmToken(AmTok Kind, int Value, string Content, int Start, int End)
{
    public AmGreek Greek => (AmGreek)Value;
    public AmArrow Arrow => (AmArrow)Value;
    public AmFunction Function => (AmFunction)Value;
    public AmOperator Operator => (AmOperator)Value;
    public AmRelation Relation => (AmRelation)Value;
    public AmLogical Logical => (AmLogical)Value;
    public AmGrouping Grouping => (AmGrouping)Value;
    public AmOther Other => (AmOther)Value;
    public AmAccent Accent => (AmAccent)Value;
    public AmFontCommand FontCommand => (AmFontCommand)Value;

    /// <summary>An opening bracket — every grouping symbol that is not one of the closing ones.</summary>
    public bool IsGroupingOpen =>
        Kind == AmTok.Grouping && Grouping is not (AmGrouping.CloseParen or AmGrouping.CloseBracket
            or AmGrouping.CloseBrace or AmGrouping.RightAngled or AmGrouping.CloseIgnored);
}

/// <summary>
/// Thrown where the Rust crate panics. `Symbol::as_str` slices the source by *character* index
/// while indexing a `str` by *byte*, so any multi-byte character in the input makes the two
/// disagree and the slice lands mid-character. Upstream contains the panic and drops the equation
/// (crates/xberg/src/extraction/asciimath.rs); this port raises the same condition explicitly so
/// the caller can do the same.
/// </summary>
internal sealed class AsciiMathPanic : System.Exception
{
    public AsciiMathPanic(string message) : base(message) { }
}

/// <summary>One keyword table: every literal that maps to a kind, plus the crate's prefix rules.</summary>
internal sealed class AmKeywords
{
    private readonly Dictionary<string, int> _map = new(System.StringComparer.Ordinal);
    private readonly HashSet<string> _firstSymbols = new(System.StringComparer.Ordinal);
    private readonly Dictionary<int, int> _prefixOf = new();

    public int MinLen { get; private set; } = int.MaxValue;
    public int MaxLen { get; private set; }
    public AmTok Kind { get; }

    /// <param name="rows">Each row is one enum value and the literals that produce it.</param>
    /// <param name="prefixOf">
    /// The crate's `prefixes:` clause. A kind listed here is a prefix of a longer keyword, so the
    /// scan keeps going past it until it has read that many characters. The value is the length of
    /// the longest keyword the entry is a prefix of.
    /// </param>
    public AmKeywords(AmTok kind, (int Value, string[] Literals)[] rows, (int Value, int Len)[]? prefixOf = null)
    {
        Kind = kind;
        foreach (var (value, literals) in rows)
            foreach (string literal in literals)
            {
                _map[literal] = value;
                if (literal.Length < MinLen) MinLen = literal.Length;
                if (literal.Length > MaxLen) MaxLen = literal.Length;
                // `starts_with` compares the first *character* of each literal, so the set holds
                // strings rather than chars.
                _firstSymbols.Add(literal[..1]);
            }
        if (prefixOf is not null)
            foreach (var (value, len) in prefixOf) _prefixOf[value] = len;
    }

    public bool StartsWith(string symbol) => _firstSymbols.Contains(symbol);
    public bool TryGet(string key, out int value) => _map.TryGetValue(key, out value);
    public int? PrefixOf(int value) => _prefixOf.TryGetValue(value, out int len) ? len : null;
}

internal static class AmTables
{
    private static (int, string[])[] R(params (System.Enum Value, string[] Literals)[] rows)
    {
        var result = new (int, string[])[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            result[i] = (System.Convert.ToInt32(rows[i].Value), rows[i].Literals);
        return result;
    }

    private static (int, int)[] P(params (System.Enum Value, string Longer)[] entries)
    {
        var result = new (int, int)[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            result[i] = (System.Convert.ToInt32(entries[i].Value), entries[i].Longer.Length);
        return result;
    }

    public static readonly AmKeywords Greeks = new(AmTok.Greek, R(
        (AmGreek.Alpha, new[] { "alpha" }), (AmGreek.Beta, new[] { "beta" }),
        (AmGreek.Gamma, new[] { "gamma" }), (AmGreek.BigGamma, new[] { "Gamma" }),
        (AmGreek.Delta, new[] { "delta" }), (AmGreek.Epsilon, new[] { "epsilon" }),
        (AmGreek.Varepsilon, new[] { "varepsilon" }), (AmGreek.Zeta, new[] { "zeta" }),
        (AmGreek.Eta, new[] { "eta" }), (AmGreek.Theta, new[] { "theta" }),
        (AmGreek.Vartheta, new[] { "vartheta" }), (AmGreek.Iota, new[] { "iota" }),
        (AmGreek.Kappa, new[] { "kappa" }), (AmGreek.Lambda, new[] { "lambda" }),
        (AmGreek.Mu, new[] { "mu" }), (AmGreek.Nu, new[] { "nu" }),
        (AmGreek.Xi, new[] { "xi" }), (AmGreek.BigXi, new[] { "Xi" }),
        (AmGreek.Pi, new[] { "pi" }), (AmGreek.BigPi, new[] { "Pi" }),
        (AmGreek.Rho, new[] { "rho" }), (AmGreek.Sigma, new[] { "sigma" }),
        (AmGreek.BigSigma, new[] { "Sigma" }), (AmGreek.Tau, new[] { "tau" }),
        (AmGreek.Upsilon, new[] { "upsilon" }), (AmGreek.Phi, new[] { "phi" }),
        (AmGreek.BigPhi, new[] { "Phi" }), (AmGreek.Varphi, new[] { "varphi" }),
        (AmGreek.Chi, new[] { "chi" }), (AmGreek.Psi, new[] { "psi" }),
        (AmGreek.BigPsi, new[] { "Psi" }), (AmGreek.Omega, new[] { "omega" }),
        (AmGreek.BigOmega, new[] { "Omega" })));

    public static readonly AmKeywords Arrows = new(AmTok.Arrow, R(
        (AmArrow.Up, new[] { "uarr", "uparrow" }),
        (AmArrow.Down, new[] { "darr", "downarrow" }),
        (AmArrow.Right, new[] { "->", "to", "rarr", "rightarrow" }),
        (AmArrow.RightTail, new[] { ">->", "rightarrowtail" }),
        (AmArrow.TwoHeadRight, new[] { "->>", "twoheadrightarrow" }),
        (AmArrow.TwoHeadRightTail, new[] { ">->>", "twoheadrightarrowtail" }),
        (AmArrow.MapsTo, new[] { "|->", "mapsto" }),
        (AmArrow.Left, new[] { "larr", "leftarrow" }),
        (AmArrow.LeftRight, new[] { "harr", "leftrightarrow" }),
        (AmArrow.BigRight, new[] { "rArr", "Rightarrow" }),
        (AmArrow.BigLeft, new[] { "lArr", "Leftarrow" }),
        (AmArrow.BigLeftRight, new[] { "hArr", "Leftrightarrow" })),
        P((AmArrow.RightTail, ">->>"), (AmArrow.TwoHeadRight, "twoheadrightarrowtail"),
           (AmArrow.Right, "->>")));

    public static readonly AmKeywords Functions = new(AmTok.Function, R(
        (AmFunction.Sin, new[] { "sin" }), (AmFunction.Cos, new[] { "cos" }),
        (AmFunction.Tan, new[] { "tan" }), (AmFunction.Sec, new[] { "sec" }),
        (AmFunction.Csc, new[] { "csc" }), (AmFunction.Cot, new[] { "cot" }),
        (AmFunction.ArcSin, new[] { "arcsin" }), (AmFunction.ArcCos, new[] { "arccos" }),
        (AmFunction.ArcTan, new[] { "arctan" }), (AmFunction.SinH, new[] { "sinh" }),
        (AmFunction.CosH, new[] { "cosh" }), (AmFunction.TanH, new[] { "tanh" }),
        (AmFunction.SecH, new[] { "sech" }), (AmFunction.CscH, new[] { "csch" }),
        (AmFunction.CotH, new[] { "coth" }), (AmFunction.Exp, new[] { "exp" }),
        (AmFunction.Lim, new[] { "lim" }), (AmFunction.Log, new[] { "log" }),
        (AmFunction.Ln, new[] { "ln" }), (AmFunction.Det, new[] { "det" }),
        (AmFunction.Dim, new[] { "dim" }), (AmFunction.Mod, new[] { "mod" }),
        (AmFunction.Gcd, new[] { "gcd" }), (AmFunction.Lcm, new[] { "lcm" }),
        (AmFunction.Lub, new[] { "lub" }), (AmFunction.Glb, new[] { "glb" }),
        (AmFunction.Min, new[] { "min" }), (AmFunction.Max, new[] { "max" }),
        (AmFunction.F, new[] { "f" }), (AmFunction.G, new[] { "g" })),
        P((AmFunction.Sin, "sinh"), (AmFunction.Cos, "cosh"), (AmFunction.Tan, "tanh"),
           (AmFunction.Sec, "sech"), (AmFunction.Csc, "csch"), (AmFunction.Cot, "coth"),
           (AmFunction.G, "gcd")));

    public static readonly AmKeywords Operators = new(AmTok.Operator, R(
        (AmOperator.Plus, new[] { "+" }), (AmOperator.Minus, new[] { "-" }),
        (AmOperator.Prime, new[] { "'" }), (AmOperator.Dot, new[] { "*", "cdot" }),
        (AmOperator.Asterisk, new[] { "**", "ast" }), (AmOperator.Star, new[] { "***", "star" }),
        (AmOperator.ForwardSlashLiteral, new[] { "//" }),
        (AmOperator.Backslash, new[] { "\\\\", "backslash", "setminus" }),
        (AmOperator.Times, new[] { "xx", "times" }), (AmOperator.Divide, new[] { "-:", "div" }),
        (AmOperator.LTimes, new[] { "|><", "ltimes" }), (AmOperator.RTimes, new[] { "><|", "rtimes" }),
        (AmOperator.Bowtie, new[] { "|><|", "bowtie" }), (AmOperator.Circle, new[] { "@", "circ" }),
        (AmOperator.OPlus, new[] { "o+", "oplus" }), (AmOperator.OTimes, new[] { "ox", "otimes" }),
        (AmOperator.ODot, new[] { "o.", "odot" }), (AmOperator.Sum, new[] { "sum" }),
        (AmOperator.Prod, new[] { "prod" }), (AmOperator.Wedge, new[] { "^^", "wedge" }),
        (AmOperator.BigWedge, new[] { "^^^", "bigwedge" }), (AmOperator.Cap, new[] { "nn", "cap" }),
        (AmOperator.BigCap, new[] { "nnn", "bigcap" }), (AmOperator.Cup, new[] { "uu", "cup" }),
        (AmOperator.BigCup, new[] { "uuu", "bigcup" })),
        P((AmOperator.Minus, "-:"), (AmOperator.Dot, "**"), (AmOperator.Asterisk, "***"),
           (AmOperator.LTimes, "|><|"), (AmOperator.Wedge, "^^^"), (AmOperator.Cap, "nnn"),
           (AmOperator.Cup, "uuu")));

    public static readonly AmKeywords Relations = new(AmTok.Relation, R(
        (AmRelation.Eq, new[] { "=" }), (AmRelation.NotEq, new[] { "!=", "ne" }),
        (AmRelation.Define, new[] { ":=" }), (AmRelation.LessThan, new[] { "<", "lt" }),
        (AmRelation.GreaterThan, new[] { ">", "gt" }),
        (AmRelation.LessEqualThan, new[] { "<=", "le" }),
        (AmRelation.GreaterEqualThan, new[] { ">=", "ge" }),
        (AmRelation.MuchLessThan, new[] { "mlt", "ll" }),
        (AmRelation.MuchGreaterThan, new[] { "mgt", "gg" }),
        (AmRelation.Prec, new[] { "-<", "prec" }), (AmRelation.PrecEq, new[] { "-<=", "preceq" }),
        (AmRelation.Succ, new[] { ">-", "succ" }), (AmRelation.SuccEq, new[] { ">-=", "succeq" }),
        (AmRelation.In, new[] { "in" }), (AmRelation.NotIn, new[] { "!in", "notin" }),
        (AmRelation.Subset, new[] { "sub", "subset" }),
        (AmRelation.Superset, new[] { "sup", "supset" }),
        (AmRelation.SubsetEq, new[] { "sube", "subseteq" }),
        (AmRelation.SupersetEq, new[] { "supe", "supseteq" }),
        (AmRelation.Equivalent, new[] { "_=", "equiv" }),
        (AmRelation.Congruent, new[] { "~=", "cong" }),
        (AmRelation.Approximate, new[] { "~~", "approx" }),
        (AmRelation.Prop, new[] { "prop", "propto" })),
        P((AmRelation.LessThan, "<="), (AmRelation.GreaterThan, ">="), (AmRelation.Prec, "preceq"),
           (AmRelation.Succ, "succeq"), (AmRelation.Subset, "subseteq"),
           (AmRelation.Superset, "supseteq")));

    public static readonly AmKeywords Logicals = new(AmTok.Logical, R(
        (AmLogical.And, new[] { "and" }), (AmLogical.Or, new[] { "or" }),
        (AmLogical.Not, new[] { "not", "neg" }), (AmLogical.Implies, new[] { "=>", "implies" }),
        (AmLogical.If, new[] { "if" }), (AmLogical.IfAndOnlyIf, new[] { "<=>", "iff" }),
        (AmLogical.ForAll, new[] { "AA", "forall" }), (AmLogical.Exists, new[] { "EE", "exists" }),
        (AmLogical.Bottom, new[] { "_|_", "bot" }), (AmLogical.Top, new[] { "TT", "top" }),
        (AmLogical.VerticalDash, new[] { "|--", "vdash" }),
        (AmLogical.Models, new[] { "|==", "models" })),
        P((AmLogical.If, "iff")));

    public static readonly AmKeywords Groupings = new(AmTok.Grouping, R(
        (AmGrouping.OpenParen, new[] { "(" }), (AmGrouping.CloseParen, new[] { ")" }),
        (AmGrouping.OpenBracket, new[] { "[" }), (AmGrouping.CloseBracket, new[] { "]" }),
        (AmGrouping.OpenBrace, new[] { "{" }), (AmGrouping.CloseBrace, new[] { "}" }),
        (AmGrouping.LeftAngled, new[] { "(:", "langle", "<<" }),
        (AmGrouping.RightAngled, new[] { ":)", "rangle", ">>" }),
        (AmGrouping.OpenIgnored, new[] { "{:" }), (AmGrouping.CloseIgnored, new[] { ":}" }),
        (AmGrouping.Absolute, new[] { "abs" }), (AmGrouping.Floor, new[] { "floor" }),
        (AmGrouping.Ceiling, new[] { "ceil" }), (AmGrouping.NormFn, new[] { "norm" }),
        (AmGrouping.Norm, new[] { "||" })),
        P((AmGrouping.OpenParen, "(:"), (AmGrouping.OpenBrace, "{:")));

    public static readonly AmKeywords Others = new(AmTok.Other, R(
        (AmOther.Comma, new[] { "," }), (AmOther.Fraction, new[] { "frac" }),
        (AmOther.ForwardSlash, new[] { "/" }), (AmOther.Power, new[] { "^" }),
        (AmOther.Subscript, new[] { "_" }), (AmOther.SquareRoot, new[] { "sqrt" }),
        (AmOther.Root, new[] { "root" }), (AmOther.Integral, new[] { "int" }),
        (AmOther.OIntegral, new[] { "oint" }), (AmOther.Partial, new[] { "del", "partial" }),
        (AmOther.Nabla, new[] { "grad", "nabla" }), (AmOther.PlusMinus, new[] { "+-", "pm" }),
        (AmOther.EmptySet, new[] { "O/", "emptyset" }), (AmOther.Infinity, new[] { "oo", "infty" }),
        (AmOther.Aleph, new[] { "aleph" }), (AmOther.Therefore, new[] { ":.", "therefore" }),
        (AmOther.Because, new[] { ":'", "because" }), (AmOther.LowDots, new[] { "...", "ldots" }),
        (AmOther.CenterDots, new[] { "cdots" }), (AmOther.VerticalDots, new[] { "vdots" }),
        (AmOther.DiagonalDots, new[] { "ddots" }), (AmOther.VerticalBar, new[] { "|" }),
        (AmOther.VerticalBars, new[] { "|\\|" }), (AmOther.VerticalBarsWide, new[] { "|quad|" }),
        (AmOther.Angle, new[] { "/_" }), (AmOther.Frown, new[] { "frown" }),
        (AmOther.Triangle, new[] { "/_\\", "triangle" }), (AmOther.Diamond, new[] { "diamond" }),
        (AmOther.Square, new[] { "square" }), (AmOther.LeftFloor, new[] { "|__", "lfloor" }),
        (AmOther.RightFloor, new[] { "__|", "rfloor" }),
        (AmOther.LeftCeiling, new[] { "|~", "lceiling" }),
        (AmOther.RightCeiling, new[] { "~|", "rceiling" }), (AmOther.Complex, new[] { "CC" }),
        (AmOther.Natural, new[] { "NN" }), (AmOther.Rational, new[] { "QQ" }),
        (AmOther.Irrational, new[] { "RR" }), (AmOther.Integer, new[] { "ZZ" }),
        (AmOther.Text, new[] { "text" }), (AmOther.Quote, new[] { "\"" })),
        P((AmOther.VerticalBar, "|\\|"), (AmOther.ForwardSlash, "/_"),
           (AmOther.Subscript, "__|"), (AmOther.Angle, "/_\\")));

    public static readonly AmKeywords Accents = new(AmTok.Accent, R(
        (AmAccent.Hat, new[] { "hat" }), (AmAccent.Overline, new[] { "bar", "overline" }),
        (AmAccent.Underline, new[] { "ul", "underline" }), (AmAccent.Vector, new[] { "vec" }),
        (AmAccent.Tilde, new[] { "tilde" }), (AmAccent.Dot, new[] { "dot" }),
        (AmAccent.DoubleDot, new[] { "ddot" }), (AmAccent.Overset, new[] { "overset" }),
        (AmAccent.Underset, new[] { "underset" }),
        (AmAccent.Underbrace, new[] { "ubrace", "underbrace" }),
        (AmAccent.Overbrace, new[] { "obrace", "overbrace" }), (AmAccent.Color, new[] { "color" }),
        (AmAccent.Cancel, new[] { "cancel" })));

    public static readonly AmKeywords FontCommands = new(AmTok.FontCommand, R(
        (AmFontCommand.Bold, new[] { "bb", "mathbf" }),
        (AmFontCommand.BlackboardBold, new[] { "bbb", "mathbb" }),
        (AmFontCommand.Calligraphic, new[] { "cc", "mathcal" }),
        (AmFontCommand.Typewriter, new[] { "tt", "mathtt" }),
        (AmFontCommand.Gothic, new[] { "fr", "mathfrak" }),
        (AmFontCommand.SansSerif, new[] { "sf", "mathsf" })),
        P((AmFontCommand.Bold, "bbb")));

    /// <summary>The first literal each kind was declared with — the crate's <c>AsRef&lt;str&gt;</c>.</summary>
    public static string OtherAsStr(AmOther other) => other switch
    {
        AmOther.Comma => ",", AmOther.Fraction => "frac", AmOther.ForwardSlash => "/",
        AmOther.Power => "^", AmOther.Subscript => "_", AmOther.SquareRoot => "sqrt",
        AmOther.Root => "root", AmOther.Integral => "int", AmOther.OIntegral => "oint",
        AmOther.Partial => "del", AmOther.Nabla => "grad", AmOther.PlusMinus => "+-",
        AmOther.EmptySet => "O/", AmOther.Infinity => "oo", AmOther.Aleph => "aleph",
        AmOther.Therefore => ":.", AmOther.Because => ":'", AmOther.LowDots => "...",
        AmOther.CenterDots => "cdots", AmOther.VerticalDots => "vdots",
        AmOther.DiagonalDots => "ddots", AmOther.VerticalBar => "|",
        AmOther.VerticalBars => "|\\|", AmOther.VerticalBarsWide => "|quad|",
        AmOther.Angle => "/_", AmOther.Frown => "frown", AmOther.Triangle => "/_\\",
        AmOther.Diamond => "diamond", AmOther.Square => "square", AmOther.LeftFloor => "|__",
        AmOther.RightFloor => "__|", AmOther.LeftCeiling => "|~", AmOther.RightCeiling => "~|",
        AmOther.Complex => "CC", AmOther.Natural => "NN", AmOther.Rational => "QQ",
        AmOther.Irrational => "RR", AmOther.Integer => "ZZ", AmOther.Text => "text",
        _ => "\"",
    };

    public static string FunctionAsStr(AmFunction f) => f switch
    {
        AmFunction.Sin => "sin", AmFunction.Cos => "cos", AmFunction.Tan => "tan",
        AmFunction.Sec => "sec", AmFunction.Csc => "csc", AmFunction.Cot => "cot",
        AmFunction.ArcSin => "arcsin", AmFunction.ArcCos => "arccos", AmFunction.ArcTan => "arctan",
        AmFunction.SinH => "sinh", AmFunction.CosH => "cosh", AmFunction.TanH => "tanh",
        AmFunction.SecH => "sech", AmFunction.CscH => "csch", AmFunction.CotH => "coth",
        AmFunction.Exp => "exp", AmFunction.Lim => "lim", AmFunction.Log => "log",
        AmFunction.Ln => "ln", AmFunction.Det => "det", AmFunction.Dim => "dim",
        AmFunction.Mod => "mod", AmFunction.Gcd => "gcd", AmFunction.Lcm => "lcm",
        AmFunction.Lub => "lub", AmFunction.Glb => "glb", AmFunction.Min => "min",
        AmFunction.Max => "max", AmFunction.F => "f", _ => "g",
    };
}

/// <summary>
/// The crate's scanner and lexer. The source is split into one-character symbols indexed from
/// zero; a token's text is recovered by slicing the source between two symbols' indices.
/// </summary>
internal sealed class AmLexer
{
    private readonly string[] _syms;   // one entry per character of the source
    private readonly byte[] _bytes;    // the source as UTF-8, which is what the crate slices
    private int _curr;

    public AmLexer(string source)
    {
        // `split("")` splits per Unicode scalar, so a surrogate pair is one symbol.
        var syms = new List<string>();
        for (int i = 0; i < source.Length;)
        {
            int len = char.IsHighSurrogate(source[i]) && i + 1 < source.Length ? 2 : 1;
            syms.Add(source.Substring(i, len));
            i += len;
        }
        _syms = syms.ToArray();
        _bytes = Encoding.UTF8.GetBytes(source);
    }

    private static bool IsDigit(string s) => s.Length == 1 && s[0] >= '0' && s[0] <= '9';
    private static bool IsDot(string s) => s == ".";
    private static bool IsWhitespace(string s)
    {
        foreach (char c in s) if (!char.IsWhiteSpace(c)) return false;
        return s.Length > 0;
    }
    private static bool IsLetter(string s)
    {
        foreach (char c in s) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
        return s.Length > 0;
    }

    /// <summary>
    /// The crate's <c>Symbol::as_str</c>: <c>&amp;src[first.offs..=last.offs]</c>. Both offsets are
    /// character positions but the slice is by byte, so the two agree only while every preceding
    /// character is one byte long. Where they do not, Rust panics.
    /// </summary>
    private string SliceSymbols(int start, int endExclusive)
    {
        if (start >= endExclusive) return "";
        int from = start, to = endExclusive - 1 + 1;   // `..=last.offs` → one past the last index
        if (from > _bytes.Length || to > _bytes.Length) throw new AsciiMathPanic("slice out of range");
        if (!IsCharBoundary(from) || !IsCharBoundary(to)) throw new AsciiMathPanic("slice not on a char boundary");
        return Encoding.UTF8.GetString(_bytes, from, to - from);
    }

    private bool IsCharBoundary(int index) =>
        index == 0 || index == _bytes.Length || (_bytes[index] & 0xC0) != 0x80;

    private void SkipWhitespace()
    {
        while (_curr < _syms.Length && IsWhitespace(_syms[_curr])) _curr++;
    }

    /// <summary>A number: <c>42</c>, <c>42.24</c>, <c>.3</c>. A lone dot is not one.</summary>
    private (AmToken Token, int Cursor)? LexNumber()
    {
        bool dotSeen = false;
        int start = _curr, curr = _curr;
        while (curr < _syms.Length)
        {
            string sym = _syms[curr];
            if (!IsDigit(sym) && !IsDot(sym)) break;
            if (IsDot(sym))
            {
                if (dotSeen) break;
                dotSeen = true;
            }
            curr++;
        }
        if (start == curr) return null;
        string content = SliceSymbols(start, curr);
        if (content.Length == 0 || content == ".") return null;
        return (new AmToken(AmTok.Number, 0, content, start, curr), curr);
    }

    /// <summary>
    /// Read the longest keyword from <paramref name="table"/> at the cursor, at least
    /// <paramref name="minLen"/> characters long. A keyword that is a prefix of a longer one keeps
    /// the scan going, so <c>gamma</c> wins over the function <c>g</c>.
    /// </summary>
    private (AmToken Token, int Cursor)? LexKeyword(AmKeywords table, int minLen)
    {
        if (_curr < _syms.Length && !table.StartsWith(_syms[_curr])) return null;

        minLen = System.Math.Max(table.MinLen, minLen);
        int start = _curr;
        int curr = System.Math.Max(_curr + minLen - 1, 0);
        int foundAt = start;
        AmToken? keyword = null;

        while (curr < _syms.Length)
        {
            int sliceLen = curr - start + 1;
            if (sliceLen <= 0) break;
            if (IsWhitespace(_syms[curr])) break;   // a token cannot contain whitespace
            curr++;
            if (sliceLen > table.MaxLen) break;
            string sliceStr = SliceSymbols(start, start + sliceLen);
            if (sliceStr.Length == 0) return null;
            if (table.TryGet(sliceStr, out int kind))
            {
                keyword = new AmToken(table.Kind, kind, sliceStr, start, curr);
                foundAt = curr;
                int? wordLen = table.PrefixOf(kind);
                if (wordLen is null) break;
                if (sliceLen > wordLen.Value) break;
            }
        }
        return keyword is null ? null : (keyword.Value, foundAt);
    }

    /// <summary><c>text(…)</c> and <c>"…"</c> keep their content verbatim as a text token.</summary>
    private (AmToken Token, int Cursor)? LexOther(int minLen)
    {
        var lexed = LexKeyword(AmTables.Others, minLen);
        if (lexed is null) return null;
        var (token, cursor) = lexed.Value;
        if (token.Other is not (AmOther.Text or AmOther.Quote)) return (token, cursor);

        var content = LexTextContent(cursor);
        if (content is null) return null;
        var (text, newCursor) = content.Value;
        return (new AmToken(AmTok.Other, (int)AmOther.Text, text, _curr, newCursor), newCursor);
    }

    private (string Content, int Cursor)? LexTextContent(int cursor)
    {
        if (cursor >= _syms.Length) return null;
        string closing = _syms[cursor] == "(" ? ")" : "\"";
        int startIdx = closing == ")" ? cursor + 1 : cursor;
        if (startIdx > _syms.Length) return null;

        int found = -1;
        for (int i = startIdx; i < _syms.Length; i++)
            if (_syms[i] == closing) { found = i; break; }
        if (found < 0) return null;

        string content = SliceSymbols(startIdx, found);
        if (content.Length == 0) return null;   // `Symbol::as_str` of an empty slice is None
        return (content, found + 1);
    }

    /// <summary>
    /// The fallback: one letter is a variable, anything else an operator. A leading <c>d</c>
    /// followed by <c>x</c>, <c>y</c>, <c>z</c> or <c>t</c> is read as a derivative and takes two
    /// characters.
    /// </summary>
    private (AmToken Token, int Cursor)? LexVariable()
    {
        int cursor = _curr;
        AmTok kind = AmTok.Variable;
        if (cursor >= _syms.Length) return null;
        string sym = _syms[cursor];
        cursor++;

        if (!IsLetter(sym)) kind = AmTok.UnknownOperator;
        else if (sym == "d" && cursor < _syms.Length && _syms[cursor] is "x" or "y" or "z" or "t")
            cursor++;

        string content = SliceSymbols(_curr, cursor);
        if (content.Length == 0) return null;
        return (new AmToken(kind, 0, content, _curr, cursor), cursor);
    }

    /// <summary>
    /// Read the next token. Each keyword table is tried in turn and the longest match wins, which
    /// is why the minimum length passed to each one grows as matches are found.
    /// </summary>
    public AmToken? Next()
    {
        SkipWhitespace();
        if (_curr >= _syms.Length) return null;

        if (LexNumber() is { } number)   // a number is never a prefix of anything
        {
            _curr = number.Cursor;
            return number.Token;
        }

        int curr = _curr;
        AmToken? token = null;
        var tables = new (AmKeywords? Table, bool IsOther)[]
        {
            (AmTables.Greeks, false), (AmTables.Arrows, false), (AmTables.Functions, false),
            (AmTables.Operators, false), (AmTables.Relations, false), (AmTables.Logicals, false),
            (AmTables.Groupings, false), (null, true), (AmTables.Accents, false),
            (AmTables.FontCommands, false),
        };

        foreach (var (table, isOther) in tables)
        {
            int minLen = curr - _curr + 1;
            var found = isOther ? LexOther(minLen) : LexKeyword(table!, minLen);
            if (found is null) continue;
            token = found.Value.Token;
            curr = found.Value.Cursor;
            // A token cannot contain whitespace, so one that ends at a space is already maximal.
            if (curr < _syms.Length && IsWhitespace(_syms[curr]))
            {
                _curr = curr;
                return token;
            }
        }

        if (token is not null)
        {
            _curr = curr;
            return token;
        }

        var variable = LexVariable();
        if (variable is null) return null;
        _curr = variable.Value.Cursor;
        return variable.Value.Token;
    }

    /// <summary>Read every token in the source.</summary>
    public List<AmToken> Tokenize()
    {
        var tokens = new List<AmToken>();
        while (Next() is { } token) tokens.Add(token);
        return tokens;
    }
}
