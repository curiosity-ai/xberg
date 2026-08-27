// Derived from `typst-syntax` 0.15.1 (Copyright The Typst Project Developers,
// https://github.com/typst/typst), licensed under the Apache License 2.0. This file is a
// modified translation of that crate's scanner and math-mode lexer into C#; see
// ../../../../THIRD_PARTY_NOTICES.md and ../../../../third_party/LICENSE-Apache-2.0.txt.
//
// Modified: only math mode is ported — markup, code and raw modes, links, numbering and labels
// are omitted, because `parse_math` never enters them. Error nodes carry their text but not the
// crate's messages and hints, which the converter does not read.
//
// Offsets are UTF-8 byte offsets, as in the crate. That is not incidental: `math_text` advances
// by the byte length of one grapheme cluster, and the parser compares a token's start against the
// previous token's end to decide whether two tokens are adjacent.
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Xberg.Internal.MathMarkup;

/// <summary>A cursor over the source, addressed by UTF-8 byte offset.</summary>
internal sealed class TypstScanner
{
    private readonly byte[] _bytes;
    private readonly string _text;
    private int _cursor;

    public TypstScanner(string text)
    {
        _text = text;
        _bytes = Encoding.UTF8.GetBytes(text);
    }

    public string Text => _text;
    public int Cursor => _cursor;
    public int Length => _bytes.Length;
    public bool Done => _cursor >= _bytes.Length;

    public void Jump(int cursor) => _cursor = System.Math.Clamp(cursor, 0, _bytes.Length);

    /// <summary>The source between the given byte offset and the cursor.</summary>
    public string From(int start) => Slice(start, _cursor);

    public string Slice(int start, int end)
    {
        start = System.Math.Clamp(start, 0, _bytes.Length);
        end = System.Math.Clamp(end, start, _bytes.Length);
        return Encoding.UTF8.GetString(_bytes, start, end - start);
    }

    /// <summary>The scalar at the cursor without consuming it, or <c>null</c> at the end.</summary>
    public int? Peek() => PeekAt(_cursor);

    /// <summary>The scalar <paramref name="n"/> positions ahead of the cursor.</summary>
    public int? Scout(int n)
    {
        int at = _cursor;
        for (int i = 0; i < n; i++)
        {
            if (PeekAt(at) is not { } cp) return null;
            at += Utf8Len(cp);
        }
        return PeekAt(at);
    }

    private int? PeekAt(int at)
    {
        if (at >= _bytes.Length) return null;
        // The offset always lands on a character boundary, so decoding the next one to four bytes
        // yields exactly one scalar.
        int len = Utf8SequenceLength(_bytes[at]);
        if (at + len > _bytes.Length) len = 1;
        string s = Encoding.UTF8.GetString(_bytes, at, len);
        return s.Length == 0 ? null : char.ConvertToUtf32(s, 0);
    }

    private static int Utf8SequenceLength(byte b) =>
        b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;

    public static int Utf8Len(int cp) => cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;

    /// <summary>Consume and return the scalar at the cursor.</summary>
    public int? Eat()
    {
        if (Peek() is not { } cp) return null;
        _cursor += Utf8Len(cp);
        return cp;
    }

    public bool At(int cp) => Peek() == cp;

    public bool At(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        return _cursor + n <= _bytes.Length && Slice(_cursor, _cursor + n) == s;
    }

    public bool At(System.Func<int, bool> pred) => Peek() is { } cp && pred(cp);

    public bool AtAny(params int[] cps)
    {
        if (Peek() is not { } cp) return false;
        foreach (int c in cps) if (c == cp) return true;
        return false;
    }

    public bool EatIf(int cp)
    {
        if (!At(cp)) return false;
        _cursor += Utf8Len(cp);
        return true;
    }

    public bool EatIf(string s)
    {
        if (!At(s)) return false;
        _cursor += Encoding.UTF8.GetByteCount(s);
        return true;
    }

    public bool EatIf(System.Func<int, bool> pred)
    {
        if (Peek() is not { } cp || !pred(cp)) return false;
        _cursor += Utf8Len(cp);
        return true;
    }

    /// <summary>Consume while the predicate holds; returns what was consumed.</summary>
    public string EatWhile(System.Func<int, bool> pred)
    {
        int start = _cursor;
        while (Peek() is { } cp && pred(cp)) _cursor += Utf8Len(cp);
        return From(start);
    }

    public string EatWhile(int cp)
    {
        int start = _cursor;
        while (At(cp)) _cursor += Utf8Len(cp);
        return From(start);
    }

    public void EatUntil(System.Func<int, bool> pred)
    {
        while (Peek() is { } cp && !pred(cp)) _cursor += Utf8Len(cp);
    }

    /// <summary>The byte length of the first grapheme cluster at <paramref name="start"/>.</summary>
    public int FirstGraphemeLength(int start)
    {
        string rest = Slice(start, _bytes.Length);
        if (rest.Length == 0) return 0;
        var e = StringInfo.GetTextElementEnumerator(rest);
        return e.MoveNext() ? Encoding.UTF8.GetByteCount((string)e.Current) : 0;
    }

    /// <summary>The byte offset at which the last grapheme cluster of a run begins.</summary>
    public static int LastGraphemeStart(string s)
    {
        int last = 0, at = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            last = at;
            at += Encoding.UTF8.GetByteCount((string)e.Current);
        }
        return last;
    }
}

/// <summary>One token: its kind and the node the lexer built for it.</summary>
internal readonly record struct TypstToken(TypstKind Kind, TypstNode Node);

internal sealed class TypstLexer
{
    private readonly TypstScanner _s;
    private bool _newline;
    private bool _error;

    public TypstLexer(string text) => _s = new TypstScanner(text);

    public int Cursor => _s.Cursor;
    public void Jump(int cursor) => _s.Jump(cursor);

    /// <summary>Whether the last token contained a newline.</summary>
    public bool Newline => _newline;

    private TypstKind Error()
    {
        _error = true;
        return TypstKind.Error;
    }

    public TypstToken Next()
    {
        int start = _s.Cursor;
        _newline = false;
        _error = false;

        TypstKind kind;
        int? first = _s.Eat();
        if (first is null) kind = TypstKind.End;
        else if (IsWhitespace(first.Value)) kind = Whitespace(start, first.Value);
        else if (first.Value == '/' && _s.EatIf('/')) kind = LineComment();
        else if (first.Value == '/' && _s.EatIf('*')) kind = BlockComment();
        else if (first.Value == '*' && _s.EatIf('/')) kind = Error();
        else
        {
            if (CodeMode) kind = Code(start, first.Value);
            else
            {
                var (mathKind, node) = Math(start, first.Value);
                if (node is not null) return new TypstToken(mathKind, node);
                kind = mathKind;
            }
        }

        string text = _s.From(start);
        return new TypstToken(kind, _error ? TypstNode.Error(text) : TypstNode.Leaf(kind, text));
    }

    private TypstKind Whitespace(int start, int c)
    {
        string more = _s.EatWhile(IsWhitespace);
        int newlines = c == ' ' && more.Length == 0 ? 0 : CountNewlines(_s.From(start));
        _newline = newlines > 0;
        // Math mode never produces `Parbreak`; only markup treats a blank line as one.
        return TypstKind.Space;
    }

    private TypstKind LineComment()
    {
        _s.EatUntil(IsNewline);
        return TypstKind.LineComment;
    }

    private TypstKind BlockComment()
    {
        int state = '_';
        int depth = 1;
        while (_s.Eat() is { } c)
        {
            if (state == '*' && c == '/')
            {
                depth--;
                if (depth == 0) break;
                state = '_';
            }
            else if (state == '/' && c == '*') { depth++; state = '_'; }
            else state = c;
        }
        return TypstKind.BlockComment;
    }

    // -- math --------------------------------------------------------------------------------

    /// <summary>
    /// Lex one math token. A node comes back only for an identifier or field access, which the
    /// lexer builds whole rather than leaving to the parser.
    /// </summary>
    private (TypstKind Kind, TypstNode? Node) Math(int start, int c)
    {
        TypstKind? simple = c switch
        {
            '\\' => Backslash(),
            '"' => Str(),

            // The shorthands, longest first, exactly as the crate orders them.
            '-' when _s.EatIf(">>") => TypstKind.MathShorthand,
            '-' when _s.EatIf('>') => TypstKind.MathShorthand,
            '-' when _s.EatIf("->") => TypstKind.MathShorthand,
            ':' when _s.EatIf('=') => TypstKind.MathShorthand,
            ':' when _s.EatIf(":=") => TypstKind.MathShorthand,
            '!' when _s.EatIf('=') => TypstKind.MathShorthand,
            '.' when _s.EatIf("..") => TypstKind.MathShorthand,
            '<' when _s.EatIf("==>") => TypstKind.MathShorthand,
            '<' when _s.EatIf("-->") => TypstKind.MathShorthand,
            '<' when _s.EatIf("--") => TypstKind.MathShorthand,
            '<' when _s.EatIf("-<") => TypstKind.MathShorthand,
            '<' when _s.EatIf("->") => TypstKind.MathShorthand,
            '<' when _s.EatIf("<-") => TypstKind.MathShorthand,
            '<' when _s.EatIf("<<") => TypstKind.MathShorthand,
            '<' when _s.EatIf("=>") => TypstKind.MathShorthand,
            '<' when _s.EatIf("==") => TypstKind.MathShorthand,
            '<' when _s.EatIf("~~") => TypstKind.MathShorthand,
            '<' when _s.EatIf('=') => TypstKind.MathShorthand,
            '<' when _s.EatIf('<') => TypstKind.MathShorthand,
            '<' when _s.EatIf('-') => TypstKind.MathShorthand,
            '<' when _s.EatIf('~') => TypstKind.MathShorthand,
            '>' when _s.EatIf("->") => TypstKind.MathShorthand,
            '>' when _s.EatIf(">>") => TypstKind.MathShorthand,
            '=' when _s.EatIf("=>") => TypstKind.MathShorthand,
            '=' when _s.EatIf('>') => TypstKind.MathShorthand,
            '=' when _s.EatIf(':') => TypstKind.MathShorthand,
            '>' when _s.EatIf('=') => TypstKind.MathShorthand,
            '>' when _s.EatIf('>') => TypstKind.MathShorthand,
            '|' when _s.EatIf("->") => TypstKind.MathShorthand,
            '|' when _s.EatIf("=>") => TypstKind.MathShorthand,
            '|' when _s.EatIf('|') => TypstKind.MathShorthand,
            '~' when _s.EatIf("~>") => TypstKind.MathShorthand,
            '~' when _s.EatIf('>') => TypstKind.MathShorthand,
            '*' or '-' or '~' => TypstKind.MathShorthand,

            '.' => TypstKind.Dot,
            ',' => TypstKind.Comma,
            ';' => TypstKind.Semicolon,
            '#' => TypstKind.Hash,
            '_' => TypstKind.Underscore,
            '$' => TypstKind.Dollar,
            '/' => TypstKind.Slash,
            '^' => TypstKind.Hat,
            '&' => TypstKind.MathAlignPoint,
            '\u221A' or '\u221B' or '\u221C' => TypstKind.Root,
            '!' => TypstKind.Bang,

            '\'' => Primes(),

            // Delimiters are lexed as brace and paren kinds and converted back to `MathText` or
            // `MathShorthand` in the parser.
            '(' => TypstKind.LeftParen,
            ')' => TypstKind.RightParen,
            '[' when _s.EatIf('|') => TypstKind.LeftBrace,
            '|' when _s.EatIf(']') => TypstKind.RightBrace,

            _ => null,
        };
        if (simple is { } kind) return (kind, null);

        if (TypstMathClass.IsOpening(c)) return (TypstKind.LeftBrace, null);
        if (TypstMathClass.IsClosing(c)) return (TypstKind.RightBrace, null);

        if (IsMathIdStart(c) && _s.At(IsMathIdContinue))
        {
            _s.EatWhile(IsMathIdContinue);
            // A run that is a single grapheme cluster is text, not an identifier.
            if (TypstScanner.LastGraphemeStart(_s.From(start)) == 0) return (TypstKind.MathText, null);
            var (identKind, node) = MathIdentOrField(start);
            return (identKind, node);
        }

        return (MathText(start, c), null);
    }

    private TypstKind Primes()
    {
        _s.EatWhile('\'');
        return TypstKind.MathPrimes;
    }

    /// <summary>Read a single <c>MathIdent</c> or a whole <c>MathFieldAccess</c>.</summary>
    private (TypstKind Kind, TypstNode Node) MathIdentOrField(int start)
    {
        var kind = TypstKind.MathIdent;
        var node = TypstNode.Leaf(kind, _s.From(start));
        while (MaybeDotIdent() is { } ident)
        {
            kind = TypstKind.MathFieldAccess;
            node = TypstNode.Inner(kind, new List<TypstNode>
            {
                node,
                TypstNode.Leaf(TypstKind.Dot, "."),
                TypstNode.Leaf(TypstKind.MathIdent, ident),
            });
        }
        return (kind, node);
    }

    /// <summary>At a dot followed by a math identifier, consume and return that identifier.</summary>
    private string? MaybeDotIdent()
    {
        if (_s.Scout(1) is { } next && IsMathIdStart(next) && _s.EatIf('.'))
        {
            int identStart = _s.Cursor;
            _s.Eat();
            _s.EatWhile(IsMathIdContinue);
            return _s.From(identStart);
        }
        return null;
    }

    /// <summary>An atom: a number keeps its digits and decimals, anything else one grapheme.</summary>
    private TypstKind MathText(int start, int c)
    {
        if (IsNumeric(c))
        {
            _s.EatWhile(IsNumeric);
            // A decimal point only counts when digits follow it.
            int save = _s.Cursor;
            if (!(_s.EatIf('.') && _s.EatWhile(IsNumeric).Length != 0)) _s.Jump(save);
        }
        else
        {
            _s.Jump(start + _s.FirstGraphemeLength(start));
        }
        return TypstKind.MathText;
    }

    /// <summary>A named argument in a math call: <c>thickness: #12pt</c>.</summary>
    public TypstNode? MaybeMathNamedArg(int start)
    {
        int cursor = _s.Cursor;
        _s.Jump(start);
        if (_s.EatIf(IsIdStart))
        {
            _s.EatWhile(IsIdContinue);
            // The colon must follow directly, and must not open the `:=` or `::=` shorthand.
            if (_s.At(':') && !_s.At(":=") && !_s.At("::="))
            {
                string text = _s.From(start);
                return text != "_" ? TypstNode.Leaf(TypstKind.Ident, text) : TypstNode.Error(text);
            }
        }
        _s.Jump(cursor);
        return null;
    }

    /// <summary>A spread argument in a math call: <c>..args</c>.</summary>
    public TypstNode? MaybeMathSpreadArg(int start)
    {
        int cursor = _s.Cursor;
        _s.Jump(start);
        if (_s.EatIf(".."))
        {
            // Not a spread when followed by space, end, a dot (that is the `...` shorthand), or a
            // character that ends the argument -- those spread nothing.
            if (!SpaceOrEnd() && !_s.AtAny('.', ',', ';', ')', '$'))
                return TypstNode.Leaf(TypstKind.Dots, _s.From(start));
        }
        _s.Jump(cursor);
        return null;
    }

    private bool SpaceOrEnd() => _s.Done || _s.At(IsWhitespace) || _s.At("//") || _s.At("/*");

    private TypstKind Backslash()
    {
        if (_s.EatIf("u{"))
        {
            string hex = _s.EatWhile(IsAsciiAlphanumeric);
            if (!_s.EatIf('}')) return Error();
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
                || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                return Error();
            return TypstKind.Escape;
        }
        if (_s.Done || _s.At(IsWhitespace)) return TypstKind.Linebreak;
        _s.Eat();
        return TypstKind.Escape;
    }

    private TypstKind Str()
    {
        bool escaped = false;
        _s.EatUntil(c =>
        {
            bool stop = c == '"' && !escaped;
            escaped = c == '\\' && !escaped;
            return stop;
        });
        return _s.EatIf('"') ? TypstKind.Str : Error();
    }

    // -- character classes -------------------------------------------------------------------
    // Outside markup any whitespace separates tokens, so math mode has only this one class.

    private static bool IsWhitespace(int c) => c <= 0xFFFF && char.IsWhiteSpace((char)c);

    /// <summary>Line feed, vertical tab, form feed, carriage return, and the three separators.</summary>
    public static bool IsNewline(int c) =>
        c is '\n' or '\u000B' or '\u000C' or '\r' or '\u0085' or '\u2028' or '\u2029';

    private static bool IsNumeric(int c) => c <= 0xFFFF && char.IsNumber((char)c);

    private static bool IsAsciiAlphanumeric(int c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>`XID_Start`, approximated as a letter or a letter number.</summary>
    private static bool IsXidStart(int c)
    {
        if (c > 0xFFFF) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory((char)c);
        return cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    }

    /// <summary>`XID_Continue`: a start character, a digit, a mark, or a connector.</summary>
    private static bool IsXidContinue(int c)
    {
        if (IsXidStart(c)) return true;
        if (c > 0xFFFF) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory((char)c);
        return cat is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;
    }

    public static bool IsIdStart(int c) => IsXidStart(c) || c == '_';
    public static bool IsIdContinue(int c) => IsXidContinue(c) || c == '_' || c == '-';
    private static bool IsMathIdStart(int c) => IsXidStart(c);
    private static bool IsMathIdContinue(int c) => IsXidContinue(c) && c != '_';

    private static int CountNewlines(string text)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsNewline(text[i])) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            n++;
        }
        return n;
    }

    // -- code ---------------------------------------------------------------------------------
    // Math enters code mode at a `#`. Only the lexer's code arm is ported; the parser above it
    // reads the reduced grammar described in TypstParser.

    /// <summary>Whether the lexer is reading code rather than math.</summary>
    public bool CodeMode { get; set; }

    private TypstKind Code(int start, int c)
    {
        // A number may lead with its decimal point, so that is checked before the bare `.` arm
        // below would claim it.
        if (c >= '0' && c <= '9') return Number(start, c);
        if (c == '.' && _s.At(x => x >= '0' && x <= '9')) return Number(start, c);

        switch (c)
        {
            case '"': return Str();
            case '<' when _s.At(IsIdContinue): return Label();
            case '=' when _s.EatIf('='): return TypstKind.EqEq;
            case '!' when _s.EatIf('='): return TypstKind.ExclEq;
            case '<' when _s.EatIf('='): return TypstKind.LtEq;
            case '>' when _s.EatIf('='): return TypstKind.GtEq;
            case '+' when _s.EatIf('='): return TypstKind.PlusEq;
            case '-' when _s.EatIf('='): return TypstKind.HyphEq;
            case '−' when _s.EatIf('='): return TypstKind.HyphEq;
            case '*' when _s.EatIf('='): return TypstKind.StarEq;
            case '/' when _s.EatIf('='): return TypstKind.SlashEq;
            case '.' when _s.EatIf('.'): return TypstKind.Dots;
            case '=' when _s.EatIf('>'): return TypstKind.Arrow;

            case '{': return TypstKind.LeftBrace;
            case '}': return TypstKind.RightBrace;
            case '[': return TypstKind.LeftBracket;
            case ']': return TypstKind.RightBracket;
            case '(': return TypstKind.LeftParen;
            case ')': return TypstKind.RightParen;
            case '$': return TypstKind.Dollar;
            case ',': return TypstKind.Comma;
            case ';': return TypstKind.Semicolon;
            case ':': return TypstKind.Colon;
            case '.': return TypstKind.Dot;
            case '+': return TypstKind.Plus;
            case '-': case '−': return TypstKind.Minus;
            case '*': return TypstKind.Star;
            case '/': return TypstKind.Slash;
            case '=': return TypstKind.Eq;
            case '<': return TypstKind.Lt;
            case '>': return TypstKind.Gt;
        }

        if (IsIdStart(c)) return Ident(start);
        return Error();
    }

    /// <summary>A label literal: <c>&lt;eq:euler&gt;</c>.</summary>
    private TypstKind Label()
    {
        string content = _s.EatWhile(c => IsIdContinue(c) || c == ':' || c == '.');
        if (content.Length == 0 || !_s.EatIf('>')) return Error();
        return TypstKind.Label;
    }

    private TypstKind Ident(int start)
    {
        _s.EatWhile(IsIdContinue);
        string ident = _s.From(start);
        string prev = _s.Slice(0, start);
        // A name after `.` or `@` is a field, not a keyword — unless the dots were a spread.
        bool afterAccess = (prev.EndsWith(".", System.StringComparison.Ordinal)
                            || prev.EndsWith("@", System.StringComparison.Ordinal))
                           && !prev.EndsWith("..", System.StringComparison.Ordinal);
        if (!afterAccess && Keyword(ident) is { } kw) return kw;
        return ident == "_" ? TypstKind.Underscore : TypstKind.Ident;
    }

    private static TypstKind? Keyword(string ident) => ident switch
    {
        "none" => TypstKind.None,
        "auto" => TypstKind.Auto,
        "true" or "false" => TypstKind.Bool,
        "not" => TypstKind.Not,
        "and" => TypstKind.And,
        "or" => TypstKind.Or,
        "let" => TypstKind.Let,
        "set" => TypstKind.Set,
        "show" => TypstKind.Show,
        "context" => TypstKind.Context,
        "if" => TypstKind.If,
        "else" => TypstKind.Else,
        "for" => TypstKind.For,
        "in" => TypstKind.In,
        "while" => TypstKind.While,
        "break" => TypstKind.Break,
        "continue" => TypstKind.Continue,
        "return" => TypstKind.Return,
        "import" => TypstKind.Import,
        "include" => TypstKind.Include,
        "as" => TypstKind.As,
        _ => null,
    };

    /// <summary>A number literal, with its base, decimals, exponent, and unit suffix.</summary>
    private TypstKind Number(int start, int firstC)
    {
        int numBase = 10;
        if (firstC == '0' && _s.EatIf('b')) numBase = 2;
        else if (firstC == '0' && _s.EatIf('o')) numBase = 8;
        else if (firstC == '0' && _s.EatIf('x')) numBase = 16;

        if (numBase == 16) _s.EatWhile(IsAsciiAlphanumeric);
        else _s.EatWhile(IsAsciiDigit);

        bool isFloat = false;
        if (numBase == 10)
        {
            if (firstC == '.')
            {
                isFloat = true;   // the digits after the dot were eaten above
            }
            // A `..` spread or a `.name` method call is not a decimal separator.
            else if (!_s.At("..") && !(_s.Scout(1) is { } nx && IsIdStart(nx)) && _s.EatIf('.'))
            {
                isFloat = true;
                _s.EatWhile(IsAsciiDigit);
            }

            if (!_s.At("em") && (_s.EatIf('e') || _s.EatIf('E')))
            {
                isFloat = true;
                if (!_s.EatIf('+')) _s.EatIf('-');
                _s.EatWhile(IsAsciiDigit);
            }
        }

        string number = _s.From(start);
        string suffix = _s.EatWhile(c => IsAsciiAlphanumeric(c) || c == '%');

        // A decimal integer too large for `i64` is read as a float.
        if (numBase == 10 && !isFloat && !long.TryParse(number, out _)
            && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            isFloat = true;

        bool suffixOk = suffix.Length == 0
            || suffix is "pt" or "mm" or "cm" or "in" or "deg" or "rad" or "em" or "fr" or "%";
        bool numberOk = numBase == 10
            ? !(isFloat && !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            : ParsesInBase(number.Length > 2 ? number[2..] : "", numBase) && suffix.Length == 0;

        if (!numberOk || !suffixOk) return Error();
        if (suffix.Length > 0) return TypstKind.Numeric;
        return isFloat ? TypstKind.Float : TypstKind.Int;
    }

    private static bool ParsesInBase(string digits, int numBase)
    {
        if (digits.Length == 0) return false;
        long value = 0;
        foreach (char ch in digits)
        {
            int d = ch >= '0' && ch <= '9' ? ch - '0'
                  : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10
                  : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10
                  : -1;
            if (d < 0 || d >= numBase) return false;
            value = value * numBase + d;
            if (value < 0) return false;
        }
        return true;
    }

    private static bool IsAsciiDigit(int c) => c >= '0' && c <= '9';

}
