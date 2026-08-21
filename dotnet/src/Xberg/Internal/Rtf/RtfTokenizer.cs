// Ported from crates/xberg/src/extractors/rtf/encoding.rs and parser.rs.
// Provides a Peekable<Chars>-equivalent cursor over Unicode scalar values plus
// UTF-8 length helpers, since the Rust code operates on byte offsets into a UTF-8
// String while iterating over `char` (Unicode scalar values).

using System.Text;

namespace Xberg.Internal.Rtf;

/// <summary>
/// Mirrors Rust's <c>std::iter::Peekable&lt;std::str::Chars&gt;</c>: iterates over
/// Unicode scalar values (codepoints), with one-element lookahead. Codepoints are
/// returned as <c>int</c>; -1 signals end-of-input.
/// </summary>
internal sealed class CharCursor
{
    private readonly int[] _cp;
    private int _i;

    public CharCursor(string s)
    {
        var list = new List<int>(s.Length);
        foreach (var r in s.EnumerateRunes())
            list.Add(r.Value);
        _cp = list.ToArray();
        _i = 0;
    }

    /// <summary>Peek the next codepoint without consuming, or -1 if none.</summary>
    public int Peek() => _i < _cp.Length ? _cp[_i] : -1;

    /// <summary>Consume and return the next codepoint, or -1 if none.</summary>
    public int Next() => _i < _cp.Length ? _cp[_i++] : -1;

    public bool HasNext => _i < _cp.Length;

    /// <summary>
    /// The cursor's position, so a caller can look ahead and rewind (Rust clones the iterator).
    /// </summary>
    public int Position
    {
        get => _i;
        set => _i = value;
    }
}

/// <summary>UTF-8 helpers mirroring Rust `char::len_utf8` / `char::from_u32`.</summary>
internal static class RtfChars
{
    /// <summary>UTF-8 byte length of a Unicode scalar value (Rust `char::len_utf8`).</summary>
    public static int Utf8Len(int cp)
    {
        if (cp < 0x80) return 1;
        if (cp < 0x800) return 2;
        if (cp < 0x10000) return 3;
        return 4;
    }

    /// <summary>Mirror of Rust `char::from_u32`: valid scalar values only (excludes surrogates).</summary>
    public static bool IsValidScalar(long cp) =>
        cp >= 0 && cp <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF);

    /// <summary>Append a Unicode scalar value to a StringBuilder.</summary>
    public static void AppendCp(StringBuilder sb, int cp)
    {
        if (cp <= 0xFFFF)
        {
            sb.Append((char)cp);
        }
        else
        {
            int v = cp - 0x10000;
            sb.Append((char)(0xD800 + (v >> 10)));
            sb.Append((char)(0xDC00 + (v & 0x3FF)));
        }
    }

    public static bool IsAlphabetic(int cp) => cp >= 0 && cp <= 0xFFFF && char.IsLetter((char)cp);
    public static bool IsAsciiDigit(int cp) => cp >= '0' && cp <= '9';
    public static bool IsAsciiAlphabetic(int cp) =>
        (cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z');
    public static bool IsAsciiHexDigit(int cp) =>
        IsAsciiDigit(cp) || (cp >= 'a' && cp <= 'f') || (cp >= 'A' && cp <= 'F');
}

/// <summary>
/// A byte-offset-tracking text accumulator. Mirrors Rust's <c>result: String</c>, where
/// <c>result.len()</c> is the UTF-8 byte length. Formatting spans use these byte offsets.
/// </summary>
internal sealed class Utf8Buf
{
    public readonly StringBuilder Sb = new();
    private int _len;

    /// <summary>UTF-8 byte length so far (Rust `result.len()`).</summary>
    public int Len => _len;

    public bool IsEmpty => Sb.Length == 0;

    public bool EndsWith(char c) => Sb.Length > 0 && Sb[Sb.Length - 1] == c;

    public void PushCp(int cp)
    {
        RtfChars.AppendCp(Sb, cp);
        _len += RtfChars.Utf8Len(cp);
    }

    public void PushChar(char c)
    {
        Sb.Append(c);
        _len += RtfChars.Utf8Len(c);
    }

    public void PushStr(string s)
    {
        Sb.Append(s);
        _len += Encoding.UTF8.GetByteCount(s);
    }

    public override string ToString() => Sb.ToString();
}
