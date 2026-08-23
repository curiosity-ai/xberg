// Ported from crates/xberg/src/extractors/rtf/encoding.rs
// Character encoding utilities for RTF parsing: hex byte parsing, Windows-1252
// mapping for the 0x80-0x9F range, and RTF control-word parsing.
using System.Text;

namespace Xberg.Internal.Rtf;

internal static class RtfEncoding
{
    /// <summary>Convert a hex digit byte to its numeric value, or null if invalid.</summary>
    public static byte? HexDigitToU8(byte c)
    {
        if (c >= (byte)'0' && c <= (byte)'9') return (byte)(c - (byte)'0');
        if (c >= (byte)'a' && c <= (byte)'f') return (byte)(c - (byte)'a' + 10);
        if (c >= (byte)'A' && c <= (byte)'F') return (byte)(c - (byte)'A' + 10);
        return null;
    }

    /// <summary>Parse a hex-encoded byte from two bytes, or null if either is invalid.</summary>
    public static byte? ParseHexByte(byte h1, byte h2)
    {
        var high = HexDigitToU8(h1);
        var low = HexDigitToU8(h2);
        if (high is null || low is null) return null;
        return (byte)((high.Value << 4) | low.Value);
    }

    /// <summary>
    /// Consume the next `\'hh` escape when one immediately follows (newlines between them are
    /// insignificant), returning its byte; leaves the cursor untouched otherwise.
    /// </summary>
    /// <remarks>
    /// A multi-byte codepage — Shift-JIS, say — spells one character across several escapes, so
    /// they have to reach the decoder together.
    /// </remarks>
    public static byte? ConsumeAdjacentHexEscape(CharCursor chars)
    {
        int mark = chars.Position;
        while (chars.Peek() is '\r' or '\n') chars.Next();
        if (chars.Next() != '\\' || chars.Next() != '\'') { chars.Position = mark; return null; }
        int h1 = chars.Next(), h2 = chars.Next();
        if (h1 < 0 || h2 < 0) { chars.Position = mark; return null; }
        byte? b = ParseHexByte((byte)(h1 & 0xFF), (byte)(h2 & 0xFF));
        if (b is null) chars.Position = mark;
        return b;
    }

    /// <summary>Decode a run of `\'hh` bytes with the active Windows codepage.</summary>
    public static string DecodeAnsiBytes(List<byte> bytes, uint codepage) =>
        Core.Encodings.ForWindowsCodepage(codepage).GetString(bytes.ToArray());

    /// <summary>
    /// Map an RTF `\fcharsetN` value to its Windows codepage. The `\fcharset` numbers are the RTF
    /// 1.9.1 font-charset enumeration, not codepage numbers, so they must be translated rather
    /// than passed through. `\fcharset1` (Default) and `\fcharset2` (Symbol) have no fixed
    /// codepage and report null, leaving the caller on `\ansicpg`.
    /// </summary>
    public static uint? FcharsetToCodepage(byte fcharset) => fcharset switch
    {
        0 => 1252u,
        77 => 10000u, 78 => 10001u, 79 => 10003u, 80 => 10008u, 81 => 10002u,
        83 => 10005u, 84 => 10004u, 85 => 10006u, 86 => 10081u, 87 => 10021u,
        88 => 10029u, 89 => 10007u,
        128 => 932u, 129 => 949u, 130 => 1361u, 134 => 936u, 136 => 950u,
        161 => 1253u, 162 => 1254u, 163 => 1258u, 177 => 1255u, 178 => 1256u,
        186 => 1257u, 204 => 1251u, 222 => 874u, 238 => 1250u, 254 => 437u, 255 => 850u,
        _ => null,
    };

    /// <summary>
    /// Per-font Windows codepages from the RTF font table (`parse_font_charset_table`).
    /// </summary>
    /// <remarks>
    /// A font's codepage comes from `\fcharsetN`, or from a literal `\cpgN` when the entry has no
    /// `\fcharset` at all. Per RTF 1.9.1 a `\cpgN` alongside a `\fcharsetN` is ignored — even when
    /// that fcharset has no fixed codepage — so such a font falls through to `\ansicpg` rather
    /// than to its own `\cpg`.
    /// </remarks>
    public static Dictionary<ushort, uint> ParseFontCharsetTable(string content)
    {
        var map = new Dictionary<ushort, uint>();
        int start = content.IndexOf("{\\*\\fonttbl", StringComparison.Ordinal);
        if (start < 0) start = content.IndexOf("{\\fonttbl", StringComparison.Ordinal);
        if (start < 0) return map;

        var tableContent = new StringBuilder();
        int depth = 0;
        foreach (char ch in content.AsSpan(start))
        {
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) break; }
            if (depth > 0) tableContent.Append(ch);
        }

        var chars = new CharCursor(tableContent.ToString());
        int entryDepth = 0;
        ushort? fontId = null;
        byte? fcharset = null;
        uint? cpg = null;

        while (chars.HasNext)
        {
            int ci = chars.Next();
            if (ci == '{')
            {
                entryDepth++;
                if (entryDepth == 2) { fontId = null; fcharset = null; cpg = null; }
            }
            else if (ci == '}')
            {
                entryDepth--;
                if (entryDepth == 1 && fontId is { } id)
                {
                    uint? codepage = fcharset is { } fc ? FcharsetToCodepage(fc) : cpg;
                    if (codepage is { } cp) map[id] = cp;
                }
            }
            else if (ci == '\\')
            {
                if (entryDepth < 2) continue;
                var (word, param) = ParseRtfControlWord(chars);
                switch (word)
                {
                    case "f": if (param is int f) fontId = (ushort)Math.Max(0, f); break;
                    case "fcharset": if (param is int fs) fcharset = (byte)Math.Max(0, fs); break;
                    case "cpg": if (param is int c && c > 0) cpg = (uint)c; break;
                }
            }
        }

        return map;
    }

    /// <summary>Decode a byte using Windows-1252 for the 0x80-0x9F range.</summary>
    public static char DecodeWindows1252(byte b) => b switch
    {
        0x80 => '€',
        0x81 => '?',
        0x82 => '‚',
        0x83 => 'ƒ',
        0x84 => '„',
        0x85 => '…',
        0x86 => '†',
        0x87 => '‡',
        0x88 => 'ˆ',
        0x89 => '‰',
        0x8A => 'Š',
        0x8B => '‹',
        0x8C => 'Œ',
        0x8D => '?',
        0x8E => 'Ž',
        0x8F => '?',
        0x90 => '?',
        0x91 => '‘',
        0x92 => '’',
        0x93 => '“',
        0x94 => '”',
        0x95 => '•',
        0x96 => '–',
        0x97 => '—',
        0x98 => '˜',
        0x99 => '™',
        0x9A => 'š',
        0x9B => '›',
        0x9C => 'œ',
        0x9D => '?',
        0x9E => 'ž',
        0x9F => 'Ÿ',
        _ => (char)b,
    };

    /// <summary>
    /// Parse an RTF control word and its optional numeric parameter from the cursor.
    /// Consumes a single trailing space delimiter, per the RTF spec.
    /// </summary>
    public static (string Word, int? Param) ParseRtfControlWord(CharCursor chars)
    {
        var word = new System.Text.StringBuilder();
        var numStr = new System.Text.StringBuilder();
        bool isNegative = false;

        // Alphabetic control word.
        while (true)
        {
            int c = chars.Peek();
            if (c >= 0 && RtfChars.IsAlphabetic(c))
            {
                word.Append((char)c);
                chars.Next();
            }
            else
            {
                break;
            }
        }

        // Optional negative sign.
        if (chars.Peek() == '-')
        {
            isNegative = true;
            chars.Next();
        }

        // Numeric parameter.
        while (true)
        {
            int c = chars.Peek();
            if (RtfChars.IsAsciiDigit(c))
            {
                numStr.Append((char)c);
                chars.Next();
            }
            else
            {
                break;
            }
        }

        int? numValue;
        if (numStr.Length > 0)
        {
            int val = int.TryParse(numStr.ToString(), out var parsed) ? parsed : 0;
            numValue = isNegative ? -val : val;
        }
        else
        {
            numValue = null;
        }

        // Consume a single trailing space delimiter.
        if (chars.Peek() == ' ')
            chars.Next();

        return (word.ToString(), numValue);
    }
}
