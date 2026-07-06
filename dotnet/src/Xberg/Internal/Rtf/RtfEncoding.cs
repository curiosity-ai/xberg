// Ported from crates/xberg/src/extractors/rtf/encoding.rs
// Character encoding utilities for RTF parsing: hex byte parsing, Windows-1252
// mapping for the 0x80-0x9F range, and RTF control-word parsing.

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
