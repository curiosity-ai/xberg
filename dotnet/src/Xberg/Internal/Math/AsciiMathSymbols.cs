// Derived from `mathemascii` 0.4.0 and `alemat` 0.8.0 (Copyright Nadir Fejzic), licensed under
// the Apache License 2.0. This file is generated from those crates' sources and is a modified
// work; see ../../../../THIRD_PARTY_NOTICES.md and ../../../../third_party/LICENSE-Apache-2.0.txt.
//
// The symbol tables of `mathemascii` 0.4.0, resolved through `alemat` 0.8.0's own
// `Ident::…`/`Operator::…` constructors. Generated from the two crates' sources rather than
// transcribed, so every character is the one the goldens were produced with.
//
// Each entry is (isIdentifier, text): an identifier renders as `<mi>`, an operator as `<mo>`.

namespace Xberg.Internal.MathMarkup;

internal static class AmSymbols
{
    public static (bool Ident, string Text) Greek(AmGreek value) => value switch
    {
        AmGreek.Alpha => (true, "\U0001d6fc"),
        AmGreek.Beta => (true, "\U0001d6fd"),
        AmGreek.Gamma => (true, "\U0001d6fe"),
        AmGreek.BigGamma => (true, "\u0393"),
        AmGreek.Delta => (true, "\U0001d6ff"),
        AmGreek.Epsilon => (true, "\U0001d700"),
        AmGreek.Varepsilon => (true, "\U0001d716"),
        AmGreek.Zeta => (true, "\U0001d701"),
        AmGreek.Eta => (true, "\U0001d702"),
        AmGreek.Theta => (true, "\U0001d703"),
        AmGreek.Vartheta => (true, "\U0001d717"),
        AmGreek.Iota => (true, "\U0001d704"),
        AmGreek.Kappa => (true, "\U0001d705"),
        AmGreek.Lambda => (true, "\U0001d706"),
        AmGreek.Mu => (true, "\U0001d707"),
        AmGreek.Nu => (true, "\U0001d708"),
        AmGreek.Xi => (true, "\U0001d709"),
        AmGreek.BigXi => (true, "\u039e"),
        AmGreek.Pi => (true, "\U0001d70b"),
        AmGreek.BigPi => (true, "\u03a0"),
        AmGreek.Rho => (true, "\U0001d70c"),
        AmGreek.Sigma => (true, "\U0001d70e"),
        AmGreek.BigSigma => (true, "\u03a3"),
        AmGreek.Tau => (true, "\U0001d70f"),
        AmGreek.Upsilon => (true, "\U0001d710"),
        AmGreek.Phi => (true, "\U0001d711"),
        AmGreek.BigPhi => (true, "\u03a6"),
        AmGreek.Varphi => (true, "\U0001d719"),
        AmGreek.Chi => (true, "\U0001d713"),
        AmGreek.Psi => (true, "\u03c8"),
        AmGreek.BigPsi => (true, "\u03a8"),
        AmGreek.Omega => (true, "\u03c9"),
        AmGreek.BigOmega => (true, "\u03a9"),
        _ => (false, ""),
    };

    public static (bool Ident, string Text) Arrow(AmArrow value) => value switch
    {
        AmArrow.Up => (false, "\u2191"),
        AmArrow.Down => (false, "\u2193"),
        AmArrow.Right => (false, "\u2192"),
        AmArrow.RightTail => (false, "\u21a3"),
        AmArrow.TwoHeadRight => (false, "\u21a0"),
        AmArrow.TwoHeadRightTail => (false, "\u2916"),
        AmArrow.MapsTo => (false, "\u21a6"),
        AmArrow.Left => (false, "\u2190"),
        AmArrow.LeftRight => (false, "\u2194"),
        AmArrow.BigRight => (false, "\u21d2"),
        AmArrow.BigLeft => (false, "\u21d0"),
        AmArrow.BigLeftRight => (false, "\u21d4"),
        _ => (false, ""),
    };

    public static (bool Ident, string Text) Logical(AmLogical value) => value switch
    {
        AmLogical.And => (false, "\u2227"),
        AmLogical.Or => (false, "\u2228"),
        AmLogical.Not => (false, "\u00ac"),
        AmLogical.Implies => (false, "\u21d2"),
        AmLogical.If => (false, "if"),
        AmLogical.IfAndOnlyIf => (false, "\u21d4"),
        AmLogical.ForAll => (false, "\u2200"),
        AmLogical.Exists => (false, "\u2203"),
        AmLogical.Bottom => (false, "\u22a5"),
        AmLogical.Top => (false, "\u22a4"),
        AmLogical.VerticalDash => (false, "\u22a2"),
        AmLogical.Models => (false, "\u22a8"),
        _ => (false, ""),
    };

    public static (bool Ident, string Text) Operator(AmOperator value) => value switch
    {
        AmOperator.Plus => (false, "+"),
        AmOperator.Minus => (false, "-"),
        AmOperator.Prime => (false, "'"),
        AmOperator.Dot => (false, "\u22c5"),
        AmOperator.Asterisk => (false, "\u2217"),
        AmOperator.Star => (false, "\u22c6"),
        AmOperator.ForwardSlashLiteral => (false, "/"),
        AmOperator.Backslash => (false, "\u2216"),
        AmOperator.Times => (false, "\u00d7"),
        AmOperator.Divide => (false, "\u00f7"),
        AmOperator.LTimes => (false, "\u22c9"),
        AmOperator.RTimes => (false, "\u22ca"),
        AmOperator.Bowtie => (false, "\u22c8"),
        AmOperator.Circle => (false, "\u2218"),
        AmOperator.OPlus => (false, "\u2295"),
        AmOperator.OTimes => (false, "\u2297"),
        AmOperator.ODot => (false, "\u2299"),
        AmOperator.Sum => (false, "\u2211"),
        AmOperator.Prod => (false, "\u220f"),
        AmOperator.Wedge => (false, "\u2227"),
        AmOperator.BigWedge => (false, "\u22c0"),
        AmOperator.Cap => (false, "\u2229"),
        AmOperator.BigCap => (false, "\u22c2"),
        AmOperator.Cup => (false, "\u222a"),
        AmOperator.BigCup => (false, "\u22c3"),
        _ => (false, ""),
    };

    public static (bool Ident, string Text) Relation(AmRelation value) => value switch
    {
        AmRelation.Eq => (false, "="),
        AmRelation.NotEq => (false, "\u2260"),
        AmRelation.Define => (false, "\u2254"),
        AmRelation.LessThan => (false, "<"),
        AmRelation.GreaterThan => (false, ">"),
        AmRelation.LessEqualThan => (false, "\u2264"),
        AmRelation.GreaterEqualThan => (false, "\u2265"),
        AmRelation.MuchLessThan => (false, "m<"),
        AmRelation.MuchGreaterThan => (false, "m>"),
        AmRelation.Prec => (false, "\u227a"),
        AmRelation.PrecEq => (false, "\u227c"),
        AmRelation.Succ => (false, "\u227b"),
        AmRelation.SuccEq => (false, "\u227d"),
        AmRelation.In => (false, "\u2208"),
        AmRelation.NotIn => (false, "\u2209"),
        AmRelation.Subset => (false, "\u2282"),
        AmRelation.Superset => (false, "\u2283"),
        AmRelation.SubsetEq => (false, "\u2286"),
        AmRelation.SupersetEq => (false, "\u2287"),
        AmRelation.Equivalent => (false, "\u2261"),
        AmRelation.Congruent => (false, "\u2245"),
        AmRelation.Approximate => (false, "\u2248"),
        AmRelation.Prop => (false, "\u221d"),
        _ => (false, ""),
    };

    /// <summary>
    /// The <c>Other</c> symbols that map to a single element. <c>VerticalBars</c> and
    /// <c>VerticalBarsWide</c> render as a row of three operators instead and are handled by the
    /// caller; everything not listed falls back to the keyword's own first spelling.
    /// </summary>
    public static (bool Ident, string Text)? Other(AmOther value) => value switch
    {
        AmOther.Comma => (false, ","),
        AmOther.ForwardSlash => (false, "/"),
        AmOther.Integral => (false, "\u222b"),
        AmOther.OIntegral => (false, "\u222e"),
        AmOther.Partial => (false, "\u2202"),
        AmOther.Nabla => (false, "\u2207"),
        AmOther.PlusMinus => (false, "\u00b1"),
        AmOther.EmptySet => (true, "\u2205"),
        AmOther.Infinity => (true, "\u221e"),
        AmOther.Aleph => (true, "\u2135"),
        AmOther.Therefore => (false, "\u2234"),
        AmOther.Because => (false, "\u2235"),
        AmOther.LowDots => (false, "..."),
        AmOther.CenterDots => (false, "\u22ef"),
        AmOther.VerticalDots => (false, "\u22ee"),
        AmOther.DiagonalDots => (false, "\u22f1"),
        AmOther.VerticalBar => (false, "|"),
        AmOther.Angle => (false, "\u2220"),
        AmOther.Frown => (false, "\u2322"),
        AmOther.Triangle => (false, "\u25b3"),
        AmOther.Diamond => (false, "\u25c7"),
        AmOther.Square => (false, "\u25a1"),
        AmOther.LeftFloor => (false, "\u230a"),
        AmOther.RightFloor => (false, "\u230b"),
        AmOther.LeftCeiling => (false, "\u2308"),
        AmOther.RightCeiling => (false, "\u2309"),
        AmOther.Complex => (true, "\u2102"),
        AmOther.Natural => (true, "\u2115"),
        AmOther.Rational => (true, "\u211a"),
        AmOther.Irrational => (true, "\u211d"),
        AmOther.Integer => (true, "\u2124"),
        _ => null,
    };
}
