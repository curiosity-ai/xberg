// Derived from `typst-syntax` 0.15.1 (Copyright The Typst Project Developers,
// https://github.com/typst/typst), licensed under the Apache License 2.0. This file is a
// modified translation of that crate's parser into C#; see ../../../../THIRD_PARTY_NOTICES.md
// and ../../../../third_party/LICENSE-Apache-2.0.txt.
//
// Modified: `parse_math` and everything it reaches is ported faithfully. Markup mode, the
// newline modes, incremental reparsing and the packrat memoisation are omitted — math mode never
// uses them. Code mode, which math enters at a `#`, is reduced: see TypstParser.CodeExprPrec.
// Error nodes carry their text but not the crate's messages, which the converter does not read.
using System.Collections.Generic;

namespace Xberg.Internal.MathMarkup;

/// <summary>A set of syntax kinds, as a bitset over the enum's values.</summary>
internal readonly struct TypstSet
{
    private readonly ulong _a, _b, _c;

    private TypstSet(ulong a, ulong b, ulong c) { _a = a; _b = b; _c = c; }

    public static TypstSet Of(params TypstKind[] kinds)
    {
        ulong a = 0, b = 0, c = 0;
        foreach (var k in kinds)
        {
            int i = (int)k;
            if (i < 64) a |= 1UL << i;
            else if (i < 128) b |= 1UL << (i - 64);
            else c |= 1UL << (i - 128);
        }
        return new TypstSet(a, b, c);
    }

    public bool Contains(TypstKind kind)
    {
        int i = (int)kind;
        return i < 64 ? (_a & (1UL << i)) != 0
             : i < 128 ? (_b & (1UL << (i - 64))) != 0
             : (_c & (1UL << (i - 128))) != 0;
    }

    public TypstSet Remove(TypstKind kind)
    {
        int i = (int)kind;
        return i < 64 ? new TypstSet(_a & ~(1UL << i), _b, _c)
             : i < 128 ? new TypstSet(_a, _b & ~(1UL << (i - 64)), _c)
             : new TypstSet(_a, _b, _c & ~(1UL << (i - 128)));
    }
}

internal static class TypstSets
{
    public static readonly TypstSet Empty = TypstSet.Of();
    public static readonly TypstSet End = TypstSet.Of(TypstKind.End);

    /// <summary>Kinds that can start a math expression.</summary>
    public static readonly TypstSet MathExpr = TypstSet.Of(
        TypstKind.Hash, TypstKind.MathIdent, TypstKind.MathFieldAccess, TypstKind.Dot,
        TypstKind.Comma, TypstKind.Semicolon,
        // Parens and braces become `MathText` unless they are parsed as a function call.
        TypstKind.LeftBrace, TypstKind.RightBrace, TypstKind.LeftParen, TypstKind.RightParen,
        TypstKind.MathText, TypstKind.MathShorthand, TypstKind.Linebreak,
        TypstKind.MathAlignPoint, TypstKind.MathPrimes, TypstKind.Escape, TypstKind.Str,
        TypstKind.Root,
        // `Bang` becomes `MathText` when parsed.
        TypstKind.Bang);

    public static readonly TypstSet MathDelimStop =
        TypstSet.Of(TypstKind.Dollar, TypstKind.End, TypstKind.RightBrace, TypstKind.RightParen);
    public static readonly TypstSet Closing = TypstSet.Of(TypstKind.RightBrace, TypstKind.RightParen);
    public static readonly TypstSet Opening = TypstSet.Of(TypstKind.LeftBrace, TypstKind.LeftParen);
    public static readonly TypstSet ArgsStop = TypstSet.Of(
        TypstKind.End, TypstKind.Dollar, TypstKind.RightParen);
    public static readonly TypstSet ArgStop = TypstSet.Of(
        TypstKind.End, TypstKind.Dollar, TypstKind.Comma, TypstKind.Semicolon, TypstKind.RightParen);
    public static readonly TypstSet AttachChain = TypstSet.Of(TypstKind.Hat, TypstKind.Underscore);

    /// <summary>Whitespace and comments, which the parser collects but never inspects.</summary>
    public static bool IsTrivia(TypstKind kind) =>
        kind is TypstKind.Shebang or TypstKind.LineComment or TypstKind.BlockComment
            or TypstKind.Space or TypstKind.Parbreak;
}

internal sealed class TypstParser
{
    // Picked by gut feeling, as in the crate.
    private const int MaxDepth = 256;
    private const int MathFuncPrec = 2;
    private const int MathRootPrec = 2;

    private readonly TypstLexer _lexer;
    private readonly List<TypstNode> _nodes = new();
    private Tok _token;
    private int _depth;

    /// <summary>The current token, held as one item of lookahead ahead of <c>_nodes</c>.</summary>
    private struct Tok
    {
        public TypstKind Kind;
        public TypstNode Node;
        public int NTrivia;
        public int Start;
        public int PrevEnd;
    }

    private TypstParser(string text)
    {
        _lexer = new TypstLexer(text);
        _token = Lex(_nodes, _lexer);
    }

    /// <summary>Parse a string as top-level math.</summary>
    public static TypstNode ParseMath(string text)
    {
        var p = new TypstParser(text);
        p.MathExprs(TypstSets.End);
        return TypstNode.Inner(TypstKind.Math, p._nodes);
    }

    // -- token access ------------------------------------------------------------------------

    private TypstKind Current => _token.Kind;
    private bool At(TypstKind kind) => _token.Kind == kind;
    private bool AtSet(TypstSet set) => set.Contains(_token.Kind);
    private bool End => At(TypstKind.End);
    private bool HadTrivia => _token.NTrivia > 0;
    private bool DirectlyAt(TypstKind kind) => _token.Kind == kind && !HadTrivia;
    private int CurrentStart => _token.Start;
    private string CurrentText => _token.Node.LeafText;

    private int Marker() => _nodes.Count;

    /// <summary>The position of the first trivia before this token, or the token itself.</summary>
    private int BeforeTrivia() => _nodes.Count - _token.NTrivia;

    // -- consuming ---------------------------------------------------------------------------

    /// <summary>Save the current token and pull the next one from the lexer.</summary>
    private void Eat()
    {
        _nodes.Add(_token.Node);
        _token = Lex(_nodes, _lexer);
    }

    private bool EatIf(TypstKind kind)
    {
        bool at = At(kind);
        if (at) Eat();
        return at;
    }

    private void Assert(TypstKind kind)
    {
        System.Diagnostics.Debug.Assert(_token.Kind == kind);
        Eat();
    }

    /// <summary>Re-label the current token, then eat it.</summary>
    private void ConvertAndEat(TypstKind kind)
    {
        _token.Node.ConvertToKind(kind);
        Eat();
    }

    /// <summary>Wrap the nodes from a marker up to (but excluding) the current token.</summary>
    private void Wrap(int from, TypstKind kind)
    {
        int to = BeforeTrivia();
        from = System.Math.Min(from, to);
        var children = _nodes.GetRange(from, to - from);
        _nodes.RemoveRange(from, to - from);
        _nodes.Insert(from, TypstNode.Inner(kind, children));
    }

    /// <summary>Pull the next non-trivia token, pushing the trivia onto the node list.</summary>
    private static Tok Lex(List<TypstNode> nodes, TypstLexer lexer)
    {
        int prevEnd = lexer.Cursor;
        int start = prevEnd;
        var t = lexer.Next();
        int nTrivia = 0;

        while (TypstSets.IsTrivia(t.Kind))
        {
            nTrivia++;
            nodes.Add(t.Node);
            start = lexer.Cursor;
            t = lexer.Next();
        }

        // The newline modes only ever apply in markup and code blocks, which `parse_math` does
        // not reach, so no token is ever turned into a temporary `End` here.
        return new Tok { Kind = t.Kind, Node = t.Node, NTrivia = nTrivia, Start = start, PrevEnd = prevEnd };
    }

    // -- errors ------------------------------------------------------------------------------

    /// <summary>Note that something was expected here, as a zero-length error node.</summary>
    private void Expected()
    {
        if (_token.Kind == TypstKind.Error) { TrimErrors(); Eat(); }
        else if (!AfterError()) _nodes.Insert(BeforeTrivia(), TypstNode.Error(""));
    }

    private bool AfterError()
    {
        int m = BeforeTrivia();
        return m > 0 && _nodes[m - 1].Kind == TypstKind.Error;
    }

    /// <summary>Eat the current token and mark it as not belonging here.</summary>
    private void Unexpected()
    {
        TrimErrors();
        int offset = _nodes.Count;
        Eat();
        _nodes[offset].ConvertToError();
    }

    /// <summary>Drop trailing zero-length error nodes.</summary>
    private void TrimErrors()
    {
        int end = BeforeTrivia();
        int start = end;
        while (start > 0 && _nodes[start - 1].Kind == TypstKind.Error
               && _nodes[start - 1].LeafText.Length == 0)
            start--;
        _nodes.RemoveRange(start, end - start);
    }

    private void ExpectClosingDelimiter(int open, TypstKind kind)
    {
        if (!EatIf(kind)) _nodes[open].ConvertToError();
    }

    /// <summary>Guard against runaway nesting, as the crate does.</summary>
    private bool TooDeep()
    {
        if (_depth < MaxDepth) return false;
        // The crate makes a balanced recovery here; math in the corpus never nests that far, so
        // this port simply consumes one token to guarantee forward progress.
        if (!End) Unexpected();
        return true;
    }

    // -- math --------------------------------------------------------------------------------

    /// <summary>Parse math expressions until a stop condition is met; returns how many.</summary>
    private int MathExprs(TypstSet stopSet)
    {
        if (TooDeep()) return 1;
        int count = 0;
        while (!AtSet(stopSet))
        {
            if (AtSet(TypstSets.MathExpr)) MathExpr();
            else Unexpected();
            count++;
        }
        return count;
    }

    private void MathExpr() => MathExprPrec(0, TypstSets.Empty);

    /// <summary>
    /// Parse a math expression of at least the given precedence, chaining with another operator
    /// by returning early when it binds less tightly.
    /// </summary>
    private void MathExprPrec(int minPrec, TypstSet stopSet)
    {
        if (TooDeep()) return;
        _depth++;
        try
        {
            int m = Marker();
            bool continuable = false;

            switch (Current)
            {
                case TypstKind.Hash:
                    EmbeddedCodeExpr();
                    break;

                // The lexer builds a whole `MathFieldAccess` node when it needs to.
                case TypstKind.MathIdent:
                case TypstKind.MathFieldAccess:
                    continuable = true;
                    Eat();
                    // An identifier or field access directly followed by `(` is a call.
                    if (MathFuncPrec >= minPrec && DirectlyAt(TypstKind.LeftParen))
                    {
                        MathArgs();
                        Wrap(m, TypstKind.MathCall);
                        continuable = false;
                    }
                    break;

                case TypstKind.LeftBrace:
                case TypstKind.LeftParen:
                    MathDelimited();
                    break;

                case TypstKind.RightBrace when CurrentText == "|]":
                    ConvertAndEat(TypstKind.MathShorthand);
                    break;

                case TypstKind.Dot:
                case TypstKind.Bang:
                case TypstKind.Comma:
                case TypstKind.Semicolon:
                case TypstKind.RightBrace:
                case TypstKind.RightParen:
                    ConvertAndEat(TypstKind.MathText);
                    break;

                case TypstKind.MathText:
                    continuable = IsMathAlphabetic(CurrentText);
                    Eat();
                    break;

                case TypstKind.Linebreak:
                case TypstKind.MathAlignPoint:
                case TypstKind.MathShorthand:
                    Eat();
                    break;

                case TypstKind.MathPrimes:
                case TypstKind.Escape:
                case TypstKind.Str:
                    continuable = true;
                    Eat();
                    break;

                case TypstKind.Root:
                {
                    Eat();
                    int m2 = Marker();
                    MathExprPrec(MathRootPrec, TypstSets.Empty);
                    MathUnparen(m2);
                    Wrap(m, TypstKind.MathRoot);
                    break;
                }

                default:
                    Expected();
                    break;
            }

            // An implicit function call: a continuable token followed directly by delimiters
            // groups as one, with the precedence of a normal function. `a(b)/c` parses as
            // `(a(b))/c` when `a` is continuable.
            if (continuable && MathFuncPrec >= minPrec && !HadTrivia && AtSet(TypstSets.Opening))
            {
                MathDelimited();
                Wrap(m, TypstKind.Math);
            }

            // Infix and postfix operators. A parsed operator looks like
            // `MathAttach[ MathText("x"), Hat("^"), MathText("2") ]`.
            while (!AtSet(stopSet))
            {
                var opKind = Current;
                bool hadTrivia = HadTrivia;
                if (MathOp(opKind, hadTrivia) is not { } op) break;
                var (wrapper, assoc, prec) = op;
                if (prec < minPrec) break;

                // `^` chains with `_`, `_` chains with `^`, and a prime chains with either —
                // though a prime cannot interrupt a chain, see below.
                var chainSet = wrapper == TypstKind.MathAttach
                    ? TypstSets.AttachChain.Remove(opKind)
                    : TypstSets.Empty;

                if (opKind == TypstKind.Bang) ConvertAndEat(TypstKind.MathText);
                else Eat();

                // Slash is the only operator that removes parens from its left operand.
                if (wrapper == TypstKind.MathFrac) MathUnparen(m);

                if (assoc is { } a)
                {
                    int rhsPrec = a == Assoc.Left ? prec + 1 : prec;
                    int mRhs = Marker();
                    MathExprPrec(rhsPrec, chainSet);
                    MathUnparen(mRhs);
                }

                // Do not interrupt a chain when first parsing a prime: for `a^b'_c^d` the
                // grouping is `(a^(b')_c)^d`, not `a^(b'_c^d)`.
                if (!(opKind == TypstKind.MathPrimes && AtSet(stopSet)))
                {
                    while (AtSet(chainSet))
                    {
                        chainSet = chainSet.Remove(Current);
                        Eat();
                        int mChainRhs = Marker();
                        MathExprPrec(prec, chainSet);
                        MathUnparen(mChainRhs);
                    }
                }

                Wrap(m, wrapper);
            }
        }
        finally { _depth--; }
    }

    private enum Assoc { Left, Right }

    /// <summary>The wrapper kind, associativity and precedence of an infix or postfix operator.</summary>
    private static (TypstKind Wrapper, Assoc? Assoc, int Prec)? MathOp(TypstKind kind, bool hadTrivia) => kind switch
    {
        TypstKind.Slash => (TypstKind.MathFrac, Assoc.Left, 1),
        TypstKind.Underscore => (TypstKind.MathAttach, Assoc.Right, 2),
        TypstKind.Hat => (TypstKind.MathAttach, Assoc.Right, 2),
        TypstKind.MathPrimes when !hadTrivia => (TypstKind.MathAttach, ((Assoc?)null), 2),
        TypstKind.Bang when !hadTrivia => (TypstKind.Math, ((Assoc?)null), 3),
        _ => null,
    };

    /// <summary>
    /// Whether text counts as alphabetic in math, which makes it group with parens as an
    /// implicit function call.
    /// </summary>
    private static bool IsMathAlphabetic(string text)
    {
        if (text.Length == 0) return false;
        int lastStart = char.IsLowSurrogate(text[^1]) && text.Length >= 2 ? text.Length - 2 : text.Length - 1;
        if (lastStart == 0)
        {
            // Just a single character.
            int cp = char.ConvertToUtf32(text, 0);
            return (cp <= 0xFFFF && char.IsLetter((char)cp)) || TypstMathClass.IsAlphabetic(cp);
        }
        // Multiple characters.
        foreach (char c in text) if (!char.IsLetter(c)) return false;
        return true;
    }

    /// <summary>Parse matched delimiters in math: <c>[x + y]</c>.</summary>
    private void MathDelimited()
    {
        int m = Marker();
        // The lexer gives brace and paren kinds; converting them back is this function's job.
        if (CurrentText == "[|") ConvertAndEat(TypstKind.MathShorthand);
        else ConvertAndEat(TypstKind.MathText);

        int mBody = Marker();
        MathExprs(TypstSets.MathDelimStop);
        if (AtSet(TypstSets.Closing))
        {
            Wrap(mBody, TypstKind.Math);
            if (CurrentText == "|]") ConvertAndEat(TypstKind.MathShorthand);
            else ConvertAndEat(TypstKind.MathText);
            Wrap(m, TypstKind.MathDelimited);
        }
        else
        {
            // With no closing delimiter this is just a math sequence.
            Wrap(m, TypstKind.Math);
        }
    }

    /// <summary>
    /// Remove one pair of parentheses from an already-parsed expression, by re-labelling the
    /// nodes so they are no longer expressions.
    /// </summary>
    private void MathUnparen(int m)
    {
        if (m >= _nodes.Count) return;
        var node = _nodes[m];
        if (node.Kind != TypstKind.MathDelimited) return;

        var children = node.Children;
        if (children.Count < 2) return;
        var first = children[0];
        var last = children[^1];
        if (first.LeafText != "(" || last.LeafText != ")") return;

        first.ConvertToKind(TypstKind.LeftParen);
        last.ConvertToKind(TypstKind.RightParen);
        // Only convert when these really were plain parentheses.
        node.ConvertToKind(TypstKind.Math);
    }

    /// <summary>Parse an argument list in math: <c>(a, b; c, d; size: #50%)</c>.</summary>
    private void MathArgs()
    {
        int m = Marker();
        Assert(TypstKind.LeftParen);

        var seen = new HashSet<string>();
        while (!AtSet(TypstSets.ArgsStop))
        {
            MathArg(seen);
            if (AtSet(TypstSets.ArgsStop)) { }
            else if (Current is TypstKind.Semicolon or TypstKind.Comma) Eat();
            else Expected();
        }

        ExpectClosingDelimiter(m, TypstKind.RightParen);
        Wrap(m, TypstKind.MathArgs);
    }

    /// <summary>Parse one argument of a math argument list.</summary>
    private void MathArg(HashSet<string> seen)
    {
        int m = Marker();
        int start = CurrentStart;
        TypstKind? argKind = null;

        if (_lexer.MaybeMathSpreadArg(start) is { } spread)
        {
            argKind = TypstKind.Spread;
            _token.Node = spread;
            Eat();
        }
        else if (_lexer.MaybeMathNamedArg(start) is { } named)
        {
            argKind = TypstKind.Named;
            _token.Node = named;
            string text = CurrentText;
            Eat();
            ConvertAndEat(TypstKind.Colon);
            if (!seen.Add(text)) _nodes[m].ConvertToError();
        }

        int mArg = Marker();
        int count = MathExprs(TypstSets.ArgStop);

        // A named argument requires a value.
        if (count == 0 && argKind == TypstKind.Named) Expected();

        // Wrap the arguments so adjacent math content joins, and so zero arguments still make an
        // empty `Math` node. One argument is left unwrapped, because wrapping would change its
        // type from potentially non-content to content.
        if (count != 1) Wrap(mArg, TypstKind.Math);

        if (argKind is { } kind) Wrap(m, kind);
    }

    // -- code --------------------------------------------------------------------------------
    // Math enters code mode at a `#`. This is the one place where the port is a reduction rather
    // than a translation: the crate's code grammar also covers control flow, imports, patterns
    // and destructuring, and reaching all of it would mean porting the other two-thirds of the
    // parser. What is ported is the shape a `#` takes inside math — a literal, a name, a field
    // access, a call with named and spread arguments, a parenthesised or bracketed group, a let
    // binding, and a closure — which is what the corpus contains. A keyword this port does not
    // model is eaten so the parser makes progress, and its body reads as ordinary expressions.

    private static readonly TypstSet UnaryOp =
        TypstSet.Of(TypstKind.Plus, TypstKind.Minus, TypstKind.Not);

    private static readonly TypstSet BinaryOp = TypstSet.Of(
        TypstKind.Plus, TypstKind.Minus, TypstKind.Star, TypstKind.Slash, TypstKind.And,
        TypstKind.Or, TypstKind.EqEq, TypstKind.ExclEq, TypstKind.Lt, TypstKind.LtEq,
        TypstKind.Gt, TypstKind.GtEq, TypstKind.Eq, TypstKind.In, TypstKind.PlusEq,
        TypstKind.HyphEq, TypstKind.StarEq, TypstKind.SlashEq);

    /// <summary>Kinds that can start a code expression this port models.</summary>
    private static readonly TypstSet CodeExprStart = TypstSet.Of(
        TypstKind.Ident, TypstKind.LeftBrace, TypstKind.LeftBracket, TypstKind.LeftParen,
        TypstKind.Dollar, TypstKind.Let, TypstKind.Set, TypstKind.Show, TypstKind.Context,
        TypstKind.If, TypstKind.While, TypstKind.For, TypstKind.Import, TypstKind.Include,
        TypstKind.Break, TypstKind.Continue, TypstKind.Return, TypstKind.None, TypstKind.Auto,
        TypstKind.Int, TypstKind.Float, TypstKind.Bool, TypstKind.Numeric, TypstKind.Str,
        TypstKind.Label, TypstKind.Raw, TypstKind.Plus, TypstKind.Minus, TypstKind.Not,
        TypstKind.Underscore);

    private static (Assoc Assoc, int Prec)? BinOp(TypstKind kind) => kind switch
    {
        TypstKind.Star or TypstKind.Slash => (Assoc.Left, 6),
        TypstKind.Plus or TypstKind.Minus => (Assoc.Left, 5),
        TypstKind.EqEq or TypstKind.ExclEq or TypstKind.Lt or TypstKind.LtEq
            or TypstKind.Gt or TypstKind.GtEq or TypstKind.In => (Assoc.Left, 4),
        TypstKind.And => (Assoc.Left, 3),
        TypstKind.Or => (Assoc.Left, 2),
        TypstKind.Eq or TypstKind.PlusEq or TypstKind.HyphEq or TypstKind.StarEq
            or TypstKind.SlashEq => (Assoc.Right, 1),
        _ => null,
    };

    private static int UnOpPrec(TypstKind kind) => kind == TypstKind.Not ? 4 : 7;

    /// <summary>
    /// Parse a code expression introduced by <c>#</c>. Entering the mode changes only the tokens
    /// that follow — the <c>#</c> itself was already read as math — and leaving it re-reads the
    /// lookahead token, which code mode had claimed, back as math.
    /// </summary>
    private void EmbeddedCodeExpr()
    {
        _lexer.CodeMode = true;
        try
        {
            Assert(TypstKind.Hash);
            if (HadTrivia || End) { Expected(); return; }
            CodeExprPrec(atomic: true, minPrec: 0);
            // A 2-D math argument relies on this being a directly adjacent `;`.
            if (DirectlyAt(TypstKind.Semicolon)) Eat();
        }
        finally
        {
            _lexer.CodeMode = false;
            // Rewind past the lookahead token and the trivia before it, and read them again as
            // math: code mode may have split them differently.
            _lexer.Jump(_token.PrevEnd);
            _nodes.RemoveRange(_nodes.Count - _token.NTrivia, _token.NTrivia);
            _token = Lex(_nodes, _lexer);
        }
    }

    private void CodeExprPrec(bool atomic, int minPrec)
    {
        if (TooDeep()) return;
        _depth++;
        try
        {
            int m = Marker();
            if (!atomic && AtSet(UnaryOp))
            {
                var op = Current;
                Eat();
                CodeExprPrec(atomic, UnOpPrec(op));
                Wrap(m, TypstKind.Unary);
            }
            else
            {
                CodePrimary(atomic);
            }

            while (true)
            {
                if (DirectlyAt(TypstKind.LeftParen) || DirectlyAt(TypstKind.LeftBracket))
                {
                    CodeArgs();
                    Wrap(m, TypstKind.FuncCall);
                    continue;
                }

                bool atFieldOrMethod = DirectlyAt(TypstKind.Dot);
                if (atomic && !atFieldOrMethod) break;

                if (EatIf(TypstKind.Dot))
                {
                    if (!EatIf(TypstKind.Ident)) { Expected(); break; }
                    Wrap(m, TypstKind.FieldAccess);
                    continue;
                }

                if (BinOp(Current) is not { } binop) break;
                var (assoc, prec) = binop;
                if (prec < minPrec) break;
                if (assoc == Assoc.Left) prec += 1;

                Eat();
                CodeExprPrec(false, prec);
                Wrap(m, TypstKind.Binary);
            }
        }
        finally { _depth--; }
    }

    private void CodePrimary(bool atomic)
    {
        int m = Marker();
        switch (Current)
        {
            case TypstKind.Ident:
                Eat();
                if (!atomic && At(TypstKind.Arrow))
                {
                    Wrap(m, TypstKind.Params);
                    Assert(TypstKind.Arrow);
                    CodeExprPrec(false, 0);
                    Wrap(m, TypstKind.Closure);
                }
                break;

            case TypstKind.None:
            case TypstKind.Auto:
            case TypstKind.Int:
            case TypstKind.Float:
            case TypstKind.Bool:
            case TypstKind.Numeric:
            case TypstKind.Str:
            case TypstKind.Label:
            case TypstKind.Raw:
            case TypstKind.Underscore:
                Eat();
                break;

            case TypstKind.LeftParen: ParenGroup(); break;
            case TypstKind.LeftBrace: DelimitedCode(TypstKind.LeftBrace, TypstKind.RightBrace, TypstKind.CodeBlock); break;
            case TypstKind.LeftBracket: DelimitedCode(TypstKind.LeftBracket, TypstKind.RightBracket, TypstKind.ContentBlock); break;

            case TypstKind.Let: LetBinding(); break;

            case TypstKind.Set: SetRule(); break;
            case TypstKind.Show: ShowRule(); break;

            // A keyword this port does not model. Eating it keeps the parser moving, and what
            // follows still parses as expressions.
            case TypstKind.Context:
            case TypstKind.If:
            case TypstKind.While:
            case TypstKind.For:
            case TypstKind.Import:
            case TypstKind.Include:
            case TypstKind.Break:
            case TypstKind.Continue:
            case TypstKind.Return:
                Eat();
                if (AtSet(CodeExprStart)) CodeExprPrec(false, 0);
                break;

            default:
                if (atomic) Unexpected(); else Expected();
                break;
        }
    }

    /// <summary>A set rule: <c>set math.equation(numbering: "(1)")</c>.</summary>
    private void SetRule()
    {
        int m = Marker();
        Assert(TypstKind.Set);
        int m2 = Marker();
        if (!EatIf(TypstKind.Ident)) Expected();
        while (EatIf(TypstKind.Dot))
        {
            if (!EatIf(TypstKind.Ident)) Expected();
            Wrap(m2, TypstKind.FieldAccess);
        }
        CodeArgs();
        if (EatIf(TypstKind.If)) CodeExprPrec(false, 0);
        Wrap(m, TypstKind.SetRule);
    }

    /// <summary>A show rule: <c>show heading: it =&gt; …</c>.</summary>
    private void ShowRule()
    {
        int m = Marker();
        Assert(TypstKind.Show);
        int m2 = BeforeTrivia();
        if (!At(TypstKind.Colon)) CodeExprPrec(false, 0);
        if (EatIf(TypstKind.Colon)) CodeExprPrec(false, 0);
        else _nodes.Insert(m2, TypstNode.Error(""));
        Wrap(m, TypstKind.ShowRule);
    }

    /// <summary>A let binding: <c>let x = 1</c>, or <c>let f(x) = …</c>.</summary>
    private void LetBinding()
    {
        int m = Marker();
        Assert(TypstKind.Let);
        int mPattern = Marker();
        if (At(TypstKind.Ident))
        {
            Eat();
            // `let f(x) = …` binds a closure, whose parameters are the parenthesised list.
            if (DirectlyAt(TypstKind.LeftParen))
            {
                Params();
                Wrap(mPattern, TypstKind.Closure);
            }
        }
        else if (At(TypstKind.LeftParen)) ParenGroup();
        else if (At(TypstKind.Underscore)) Eat();
        else Expected();

        if (EatIf(TypstKind.Eq)) CodeExprPrec(false, 0);
        else Expected();
        Wrap(m, TypstKind.LetBinding);
    }

    /// <summary>
    /// A parenthesised expression, an array, a dictionary, or a closure's parameter list — which
    /// of those it is depends on what follows, so it is parsed as items and labelled after.
    /// </summary>
    private void ParenGroup()
    {
        int m = Marker();
        int count = ParenItems(out bool sawColon, out bool sawComma);

        if (At(TypstKind.Arrow))
        {
            Wrap(m, TypstKind.Params);
            Assert(TypstKind.Arrow);
            CodeExprPrec(false, 0);
            Wrap(m, TypstKind.Closure);
            return;
        }

        Wrap(m, sawColon ? TypstKind.Dict : count == 1 && !sawComma ? TypstKind.Parenthesized : TypstKind.Array);
    }

    /// <summary>A closure's parameter list, when a name is already known to precede it.</summary>
    private void Params()
    {
        int m = Marker();
        ParenItems(out _, out _);
        Wrap(m, TypstKind.Params);
    }

    /// <summary>
    /// Read a parenthesised, comma-separated list of code expressions. Returns how many items it
    /// held, and reports whether any of them was named and whether a comma was present.
    /// </summary>
    private int ParenItems(out bool sawColon, out bool sawComma)
    {
        sawColon = false;
        sawComma = false;
        int open = Marker();
        Assert(TypstKind.LeftParen);
        int count = 0;
        var seen = new HashSet<string>();

        while (!AtSet(Terminator))
        {
            if (!AtSet(CodeExprStart) && !At(TypstKind.Dots)) { Unexpected(); continue; }
            int before = _nodes.Count;
            CodeArg(seen);
            if (_nodes.Count > before && _nodes[^1].Kind == TypstKind.Named) sawColon = true;
            count++;
            if (!AtSet(Terminator))
            {
                if (At(TypstKind.Comma)) { sawComma = true; Eat(); }
                else Expected();
            }
        }

        ExpectClosingDelimiter(open, TypstKind.RightParen);
        return count;
    }

    /// <summary>Kinds that end a bracketed list.</summary>
    private static readonly TypstSet Terminator = TypstSet.Of(
        TypstKind.End, TypstKind.Semicolon, TypstKind.RightBrace, TypstKind.RightParen,
        TypstKind.RightBracket);

    /// <summary>An argument list on a call: <c>(a, b: 1, ..rest)</c> plus trailing content blocks.</summary>
    private void CodeArgs()
    {
        if (!DirectlyAt(TypstKind.LeftParen) && !DirectlyAt(TypstKind.LeftBracket)) Expected();

        int m = Marker();
        if (At(TypstKind.LeftParen))
        {
            int m2 = Marker();
            Assert(TypstKind.LeftParen);
            var seen = new HashSet<string>();
            while (!AtSet(Terminator))
            {
                if (!AtSet(CodeExprStart)) { Unexpected(); continue; }
                CodeArg(seen);
                if (!AtSet(Terminator) && !EatIf(TypstKind.Comma)) Expected();
            }
            ExpectClosingDelimiter(m2, TypstKind.RightParen);
        }

        // A call may take trailing content blocks: `#figure[…][…]`.
        while (DirectlyAt(TypstKind.LeftBracket))
            DelimitedCode(TypstKind.LeftBracket, TypstKind.RightBracket, TypstKind.ContentBlock);

        Wrap(m, TypstKind.Args);
    }

    /// <summary>One argument: a spread, a named pair, or a bare expression.</summary>
    private void CodeArg(HashSet<string> seen)
    {
        int m = Marker();

        if (EatIf(TypstKind.Dots))
        {
            CodeExprPrec(false, 0);
            Wrap(m, TypstKind.Spread);
            return;
        }

        // An argument name is only known to be one once a colon follows it, so the name is first
        // parsed as an ordinary expression.
        bool wasAtExpr = AtSet(CodeExprStart);
        string text = CurrentText;
        CodeExprPrec(false, 0);

        if (EatIf(TypstKind.Colon))
        {
            if (wasAtExpr && m < _nodes.Count)
            {
                if (_nodes[m].Kind != TypstKind.Ident) _nodes[m].ConvertToError();
                else if (!seen.Add(text)) _nodes[m].ConvertToError();
            }
            CodeExprPrec(false, 0);
            Wrap(m, TypstKind.Named);
        }
    }

    /// <summary>
    /// Consume a balanced brace or bracket group. Its contents are code or markup that the
    /// converter renders by concatenating leaves, so the tokens are kept but not structured.
    /// </summary>
    private void DelimitedCode(TypstKind open, TypstKind close, TypstKind wrapper)
    {
        int m = Marker();
        Assert(open);
        int depth = 1;
        while (!End)
        {
            if (At(open)) depth++;
            else if (At(close)) { depth--; if (depth == 0) break; }
            Eat();
        }
        ExpectClosingDelimiter(m, close);
        Wrap(m, wrapper);
    }
}
