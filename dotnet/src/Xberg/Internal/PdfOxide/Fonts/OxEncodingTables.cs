// Port of pdf_oxide 0.3.77 encoding support:
//   fonts/encoding.rs — UnicodeEncoder, math_alphanumeric_base, unicode_to_winansi,
//                       is_winansi_char, encode_bytes_as_literal, encode_bytes_as_hex
//   fonts/font_dict.rs — standard_encoding_lookup, pdfdoc_encoding_lookup,
//                        symbol_encoding_lookup, zapf_dingbats_encoding_lookup,
//                        FontInfo::gid_to_standard_glyph_name,
//                        builtin_encoding_looks_like_cipher
//
// The Standard/WinAnsi/MacRoman/PDFDoc/Symbol/ZapfDingbats tables live in font_dict.rs
// upstream, not encoding.rs; they are collected here because they are pure encoding data.

using System.Globalization;
using System.Text;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>
/// <c>encoding::UnicodeEncoder</c> — converts Unicode text into PDF string syntax.
/// </summary>
internal sealed class OxUnicodeEncoder
{
    private readonly Dictionary<uint, ushort> _glyphCache = new();

    /// <summary>
    /// Encode text as an Identity-H hex string of glyph IDs, e.g. "&lt;00410042&gt;".
    /// Per ISO 32000-1 §9.7.5.2 each 4-hex group is a big-endian u16 glyph ID.
    /// </summary>
    internal string EncodeIdentityH(string text, Func<uint, ushort?> glyphLookup)
    {
        var hex = new StringBuilder(text.Length * 4 + 2);
        hex.Append('<');

        foreach (Rune rune in text.EnumerateRunes())
        {
            uint codepoint = (uint)rune.Value;
            if (!_glyphCache.TryGetValue(codepoint, out ushort glyphId))
            {
                ushort? looked = glyphLookup(codepoint);
                if (looked is null)
                {
                    // .notdef for missing glyphs; not cached, so a later lookup can still win.
                    glyphId = 0;
                }
                else
                {
                    glyphId = looked.Value;
                    _glyphCache[codepoint] = glyphId;
                }
            }
            hex.Append(glyphId.ToString("X4", CultureInfo.InvariantCulture));
        }

        hex.Append('>');
        return hex.ToString();
    }

    internal string EncodeCharIdentityH(ushort glyphId) =>
        "<" + glyphId.ToString("X4", CultureInfo.InvariantCulture) + ">";

    /// <summary>
    /// Encode text as a PDF literal string. Characters outside Latin-1 become '?'.
    /// </summary>
    internal static string EncodeLiteral(string text)
    {
        var result = new StringBuilder(text.Length + 2);
        result.Append('(');

        foreach (Rune rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            switch (cp)
            {
                case '(': result.Append("\\("); break;
                case ')': result.Append("\\)"); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (cp <= 0x7F && cp >= ' ')
                    {
                        result.Append((char)cp);
                    }
                    else if (cp < 256)
                    {
                        result.Append('\\').Append(Convert.ToString(cp, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        result.Append('?');
                    }
                    break;
            }
        }

        result.Append(')');
        return result.ToString();
    }

    /// <summary>Encode text as a UTF-16BE PDF hex string (BOM-prefixed), for metadata.</summary>
    internal static string EncodeUtf16Be(string text)
    {
        var hex = new StringBuilder();
        hex.Append('<');
        hex.Append("FEFF");

        foreach (char unit in text)
        {
            hex.Append(((ushort)unit).ToString("X4", CultureInfo.InvariantCulture));
        }

        hex.Append('>');
        return hex.ToString();
    }

    /// <summary>Literal string when the text is plain ASCII, UTF-16BE hex otherwise.</summary>
    internal static string EncodeText(string text)
    {
        bool plainAscii = true;
        bool latin1 = true;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (!(cp <= 0x7F && cp >= ' ' && cp != '(' && cp != ')' && cp != '\\'))
            {
                plainAscii = false;
            }
            if (cp >= 256)
            {
                latin1 = false;
            }
        }

        if (plainAscii)
        {
            return "(" + text + ")";
        }
        if (latin1)
        {
            return EncodeLiteral(text);
        }
        return EncodeUtf16Be(text);
    }

    internal void ClearCache() => _glyphCache.Clear();

    internal int CacheSize => _glyphCache.Count;
}

internal static class OxEncodingTables
{
    /// <summary>
    /// <c>encoding::math_alphanumeric_base</c> — collapse a Mathematical Alphanumeric Symbol
    /// (U+1D400-U+1D7FF) to its plain Latin/Greek/digit base. None of the 1024 styled forms
    /// have glyphs in the standard 14 PDF fonts, but the block is laid out as fixed-stride
    /// ranges so the mapping is pure arithmetic. Returns null outside the block and for the
    /// reserved holes whose canonical letter lives elsewhere.
    /// </summary>
    internal static uint? MathAlphanumericBase(uint codepoint)
    {
        if (codepoint < 0x1D400 || codepoint > 0x1D7FF)
        {
            return null;
        }

        // Reserved holes: codepoints already encoded elsewhere in Unicode.
        uint canonical = codepoint switch
        {
            0x1D455 => 0x0068, // h (BMP italic h hole)
            0x1D49D => 0x0042, // B
            0x1D4A0 => 0x0045, // E
            0x1D4A1 => 0x0046, // F
            0x1D4A3 => 0x0048, // H
            0x1D4A4 => 0x0049, // I
            0x1D4A7 => 0x004C, // L
            0x1D4A8 => 0x004D, // M
            0x1D4AD => 0x0052, // R
            0x1D4BA => 0x0065, // e
            0x1D4BC => 0x0067, // g
            0x1D4C4 => 0x006F, // o
            0x1D506 => 0x0043, // C
            0x1D50B => 0x0048, // H
            0x1D50C => 0x0049, // I
            0x1D515 => 0x0052, // R
            0x1D51D => 0x005A, // Z
            0x1D53A => 0x0043, // C
            0x1D53F => 0x0048, // H
            0x1D545 => 0x004E, // N
            0x1D547 => 0x0050, // P
            0x1D548 => 0x0051, // Q
            0x1D549 => 0x0052, // R
            0x1D551 => 0x005A, // Z
            _ => 0,
        };
        if (canonical != 0)
        {
            return canonical;
        }

        foreach ((uint start, uint baseCp) in LatinRanges)
        {
            if (codepoint >= start && codepoint < start + 26)
            {
                return baseCp + (codepoint - start);
            }
        }

        // Each Greek block is 58 chars: 25 capitals, nabla at +25, 25 lowercase at +26,
        // partial-differential at +51, then 6 alternate forms. Only the two 25-run letter
        // spans are mapped; the Theta-variant slot lands on unassigned U+03A2 but is rare.
        foreach (uint start in GreekRanges)
        {
            if (codepoint >= start && codepoint < start + 25)
            {
                return 0x0391 + (codepoint - start);
            }
            uint lowerStart = start + 26;
            if (codepoint >= lowerStart && codepoint < lowerStart + 25)
            {
                return 0x03B1 + (codepoint - lowerStart);
            }
        }

        foreach (uint start in DigitStarts)
        {
            if (codepoint >= start && codepoint < start + 10)
            {
                return 0x30 + (codepoint - start);
            }
        }

        return null;
    }

    // Bold / Italic / Bold-Italic / Script / Bold-Script / Fraktur / Double-Struck /
    // Bold-Fraktur / Sans / Sans-Bold / Sans-Italic / Sans-Bold-Italic / Mono Latin
    // (each style is 52 chars: A-Z then a-z).
    private static readonly (uint Start, uint Base)[] LatinRanges =
    {
        (0x1D400, 0x41), // A-Z bold
        (0x1D41A, 0x61), // a-z bold
        (0x1D434, 0x41), // A-Z italic
        (0x1D44E, 0x61), // a-z italic
        (0x1D468, 0x41), // bold italic
        (0x1D482, 0x61),
        (0x1D49C, 0x41), // script
        (0x1D4B6, 0x61),
        (0x1D4D0, 0x41), // bold script
        (0x1D4EA, 0x61),
        (0x1D504, 0x41), // fraktur
        (0x1D51E, 0x61),
        (0x1D538, 0x41), // double-struck
        (0x1D552, 0x61),
        (0x1D56C, 0x41), // bold fraktur
        (0x1D586, 0x61),
        (0x1D5A0, 0x41), // sans-serif
        (0x1D5BA, 0x61),
        (0x1D5D4, 0x41), // sans-serif bold
        (0x1D5EE, 0x61),
        (0x1D608, 0x41), // sans-serif italic
        (0x1D622, 0x61),
        (0x1D63C, 0x41), // sans-serif bold italic
        (0x1D656, 0x61),
        (0x1D670, 0x41), // monospace
        (0x1D68A, 0x61),
    };

    private static readonly uint[] GreekRanges = { 0x1D6A8, 0x1D6E2, 0x1D71C, 0x1D756, 0x1D790 };

    private static readonly uint[] DigitStarts = { 0x1D7CE, 0x1D7D8, 0x1D7E2, 0x1D7EC, 0x1D7F6 };

    /// <summary>
    /// <c>encoding::unicode_to_winansi</c> — Unicode to WinAnsi (Windows-1252) byte.
    /// Only 0x80-0x9F differs from Latin-1.
    /// </summary>
    internal static byte? UnicodeToWinAnsi(uint codepoint)
    {
        if (codepoint < 0x80 || (codepoint >= 0xA0 && codepoint <= 0xFF))
        {
            return (byte)codepoint;
        }

        return codepoint switch
        {
            0x20AC => (byte)0x80, // Euro sign
            0x201A => (byte)0x82, // Single low-9 quotation mark
            0x0192 => (byte)0x83, // Latin small letter f with hook
            0x201E => (byte)0x84, // Double low-9 quotation mark
            0x2026 => (byte)0x85, // Horizontal ellipsis
            0x2020 => (byte)0x86, // Dagger
            0x2021 => (byte)0x87, // Double dagger
            0x02C6 => (byte)0x88, // Modifier letter circumflex accent
            0x2030 => (byte)0x89, // Per mille sign
            0x0160 => (byte)0x8A, // Latin capital letter S with caron
            0x2039 => (byte)0x8B, // Single left-pointing angle quotation mark
            0x0152 => (byte)0x8C, // Latin capital ligature OE
            0x017D => (byte)0x8E, // Latin capital letter Z with caron
            0x2018 => (byte)0x91, // Left single quotation mark
            0x2019 => (byte)0x92, // Right single quotation mark
            0x201C => (byte)0x93, // Left double quotation mark
            0x201D => (byte)0x94, // Right double quotation mark
            0x2022 => (byte)0x95, // Bullet
            0x2013 => (byte)0x96, // En dash
            0x2014 => (byte)0x97, // Em dash
            0x02DC => (byte)0x98, // Small tilde
            0x2122 => (byte)0x99, // Trade mark sign
            0x0161 => (byte)0x9A, // Latin small letter s with caron
            0x203A => (byte)0x9B, // Single right-pointing angle quotation mark
            0x0153 => (byte)0x9C, // Latin small ligature oe
            0x017E => (byte)0x9E, // Latin small letter z with caron
            0x0178 => (byte)0x9F, // Latin capital letter Y with diaeresis
            _ => null,
        };
    }

    internal static bool IsWinAnsiChar(Rune ch) => UnicodeToWinAnsi((uint)ch.Value) is not null;

    /// <summary>Encode bytes as a PDF literal string with proper escaping.</summary>
    internal static string EncodeBytesAsLiteral(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder(bytes.Length * 2 + 2);
        result.Append('(');
        foreach (byte b in bytes)
        {
            result.Append(EscapeByteForLiteral(b));
        }
        result.Append(')');
        return result.ToString();
    }

    private static string EscapeByteForLiteral(byte b) => b switch
    {
        (byte)'(' => "\\(",
        (byte)')' => "\\)",
        (byte)'\\' => "\\\\",
        0x0A => "\\n",
        0x0D => "\\r",
        0x09 => "\\t",
        0x08 => "\\b",
        0x0C => "\\f",
        >= 0x20 and < 0x7F => ((char)b).ToString(),
        _ => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'),
    };

    /// <summary>Encode bytes as a PDF hex string.</summary>
    internal static string EncodeBytesAsHex(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder(bytes.Length * 2 + 2);
        result.Append('<');
        foreach (byte b in bytes)
        {
            result.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        result.Append('>');
        return result.ToString();
    }

    /// <summary>
    /// <c>font_dict::pdfdoc_encoding_lookup</c> — PDFDocEncoding, a superset of ISO Latin-1
    /// used for PDF text strings and metadata. 0x00-0x7F is ASCII, 0x80-0x9F is special,
    /// 0xA0-0xFF is Latin-1. ISO 32000-1:2008 Annex D.2 Table D.2.
    /// </summary>
    internal static char? PdfDocEncodingLookup(byte code) => code switch
    {
        // ASCII range (0-127)
        <= 0x7F => (char)code,

        // PDFDocEncoding special range (128-159)
        0x80 => '\u2022', // bullet
        0x81 => '\u2020', // dagger
        0x82 => '\u2021', // daggerdbl
        0x83 => '\u2026', // ellipsis
        0x84 => '\u2014', // emdash
        0x85 => '\u2013', // endash
        0x86 => '\u0192', // florin
        0x87 => '\u2044', // fraction
        0x88 => '\u2039', // guilsinglleft
        0x89 => '\u203A', // guilsinglright
        0x8A => '\u2212', // minus (different from hyphen!)
        0x8B => '\u2030', // perthousand
        0x8C => '\u201E', // quotedblbase
        0x8D => '\u0022', // quotedblleft
        0x8E => '\u0022', // quotedblright
        0x8F => '\u2018', // quoteleft (left single quotation mark)
        0x90 => '\u2019', // quoteright (right single quotation mark)
        0x91 => '\u201A', // quotesinglbase
        0x92 => '\u2122', // trademark
        0x93 => '\uFB01', // fi ligature
        0x94 => '\uFB02', // fl ligature
        0x95 => '\u0141', // Lslash
        0x96 => '\u0152', // OE
        0x97 => '\u0160', // Scaron
        0x98 => '\u0178', // Ydieresis
        0x99 => '\u017D', // Zcaron
        0x9A => '\u0131', // dotlessi
        0x9B => '\u0142', // lslash
        0x9C => '\u0153', // oe
        0x9D => '\u0161', // scaron
        0x9E => '\u017E', // zcaron
        0x9F => null, // undefined

        // ISO Latin-1 range (160-255) - direct mapping
        >= 0xA0 => (char)code,
    };

    /// <summary>
    /// <c>font_dict::symbol_encoding_lookup</c> — the Adobe Symbol font encoding
    /// (ISO 32000-1:2008 Annex D.5). Symbol fonts carry no meaningful /Encoding, so this
    /// table is what recovers Greek letters and math operators from their byte codes.
    /// </summary>
    internal static char? SymbolEncodingLookup(byte code) => code switch
    {
        // Greek lowercase letters
        0x61 => '\u03B1', // alpha
        0x62 => '\u03B2', // beta
        0x63 => '\u03C7', // chi
        0x64 => '\u03B4', // delta
        0x65 => '\u03B5', // epsilon
        0x66 => '\u03C6', // phi
        0x67 => '\u03B3', // gamma
        0x68 => '\u03B7', // eta
        0x69 => '\u03B9', // iota
        0x6A => '\u03D5', // phi1 (variant)
        0x6B => '\u03BA', // kappa
        0x6C => '\u03BB', // lambda
        0x6D => '\u03BC', // mu
        0x6E => '\u03BD', // nu
        0x6F => '\u03BF', // omicron
        0x70 => '\u03C0', // pi
        0x71 => '\u03B8', // theta
        0x72 => '\u03C1', // rho ← THE IMPORTANT ONE for Pearson's ρ!
        0x73 => '\u03C3', // sigma
        0x74 => '\u03C4', // tau
        0x75 => '\u03C5', // upsilon
        0x76 => '\u03D6', // omega1 (variant pi)
        0x77 => '\u03C9', // omega
        0x78 => '\u03BE', // xi
        0x79 => '\u03C8', // psi
        0x7A => '\u03B6', // zeta

        // Greek uppercase letters
        0x41 => '\u0391', // Alpha
        0x42 => '\u0392', // Beta
        0x43 => '\u03A7', // Chi
        0x44 => '\u0394', // Delta
        0x45 => '\u0395', // Epsilon
        0x46 => '\u03A6', // Phi
        0x47 => '\u0393', // Gamma
        0x48 => '\u0397', // Eta
        0x49 => '\u0399', // Iota
        0x4B => '\u039A', // Kappa
        0x4C => '\u039B', // Lambda
        0x4D => '\u039C', // Mu
        0x4E => '\u039D', // Nu
        0x4F => '\u039F', // Omicron
        0x50 => '\u03A0', // Pi
        0x51 => '\u0398', // Theta
        0x52 => '\u03A1', // Rho
        0x53 => '\u03A3', // Sigma
        0x54 => '\u03A4', // Tau
        0x55 => '\u03A5', // Upsilon
        0x57 => '\u03A9', // Omega
        0x58 => '\u039E', // Xi
        0x59 => '\u03A8', // Psi
        0x5A => '\u0396', // Zeta

        // Mathematical operators
        0xB1 => '\u00B1', // plusminus
        0xB4 => '\u00F7', // divide
        0xB5 => '\u221E', // infinity
        0xB6 => '\u2202', // partialdiff
        0xB7 => '\u2022', // bullet
        0xB9 => '\u2260', // notequal
        0xBA => '\u2261', // equivalence
        0xBB => '\u2248', // approxequal
        0xBC => '\u2026', // ellipsis
        0xBE => '\u22A5', // perpendicular
        0xBF => '\u2299', // circleplus

        0xD0 => '\u00B0', // degree
        0xD1 => '\u2207', // gradient (nabla)
        0xD2 => '\u00AC', // logicalnot
        0xD3 => '\u2227', // logicaland
        0xD4 => '\u2228', // logicalor
        0xD5 => '\u220F', // product ← Product symbol!
        0xD6 => '\u221A', // radical ← Square root!
        0xD7 => '\u22C5', // dotmath
        0xD8 => '\u2295', // circleplus
        0xD9 => '\u2297', // circletimes

        0xDA => '\u2208', // element
        0xDB => '\u2209', // notelement
        0xDC => '\u2220', // angle
        0xDD => '\u2207', // gradient
        0xDE => '\u00AE', // registered
        0xDF => '\u00A9', // copyright
        0xE0 => '\u2122', // trademark

        0xE1 => '\u2211', // summation ← Summation symbol!
        0xE2 => '\u2282', // propersubset
        0xE3 => '\u2283', // propersuperset
        0xE4 => '\u2286', // reflexsubset
        0xE5 => '\u2287', // reflexsuperset
        0xE6 => '\u222A', // union
        0xE7 => '\u2229', // intersection
        0xE8 => '\u2200', // universal
        0xE9 => '\u2203', // existential
        0xEA => '\u00AC', // logicalnot

        0xF1 => '\u3008', // angleleft
        0xF2 => '\u222B', // integral ← Integral symbol!
        0xF3 => '\u2320', // integraltp
        0xF4 => '\u2321', // integralbt
        0xF5 => '\u2293', // square intersection
        0xF6 => '\u2294', // square union
        0xF7 => '\u3009', // angleright

        // Basic punctuation and symbols (overlap with ASCII)
        0x20 => '\u0020', // space
        0x21 => '\u0021', // exclam
        0x22 => '\u2200', // universal (sometimes mapped here)
        0x23 => '\u0023', // numbersign
        0x24 => '\u2203', // existential (sometimes mapped here)
        0x25 => '\u0025', // percent
        0x26 => '\u0026', // ampersand
        0x27 => '\u220B', // suchthat
        0x28 => '\u0028', // parenleft
        0x29 => '\u0029', // parenright
        0x2A => '\u2217', // asteriskmath
        0x2B => '\u002B', // plus
        0x2C => '\u002C', // comma
        0x2D => '\u2212', // minus
        0x2E => '\u002E', // period
        0x2F => '\u002F', // slash

        // Digits 0-9 (0x30-0x39) map to themselves

        0x3A => '\u003A', // colon
        0x3B => '\u003B', // semicolon
        0x3C => '\u003C', // less
        0x3D => '\u003D', // equal
        0x3E => '\u003E', // greater
        0x3F => '\u003F', // question

        0x40 => '\u2245', // congruent

        // Brackets and arrows
        0x5B => '\u005B', // bracketleft
        0x5C => '\u2234', // therefore
        0x5D => '\u005D', // bracketright
        0x5E => '\u22A5', // perpendicular
        0x5F => '\u005F', // underscore

        0x7B => '\u007B', // braceleft
        0x7C => '\u007C', // bar
        0x7D => '\u007D', // braceright
        0x7E => '\u223C', // similar

        // Math operators previously missing from the Adobe Symbol set (Annex D.5).
        0xA3 => '\u2264', // ≤ lessequal    (octal 243)
        0xA5 => '\u221E', // ∞ infinity     (octal 245)
        0xB3 => '\u2265', // ≥ greaterequal (octal 263)
        // Digits 0-9 (0x30-0x39) map to themselves
        >= 0x30 and <= 0x39 => (char)code,
        _ => null,
    };

    /// <summary>
    /// <c>font_dict::zapf_dingbats_encoding_lookup</c> — the Adobe ZapfDingbats encoding
    /// (ISO 32000-1:2008 Annex D.6): ornaments, arrows and circled digits.
    /// </summary>
    internal static char? ZapfDingbatsEncodingLookup(byte code) => code switch
    {
        0x20 => '\u0020', // space
        0x21 => '\u2701', // scissors
        0x22 => '\u2702', // scissors (filled)
        0x23 => '\u2703', // scissors (outline)
        0x24 => '\u2704', // scissors (small)
        0x25 => '\u260E', // telephone
        0x26 => '\u2706', // telephone (filled)
        0x27 => '\u2707', // tape drive
        0x28 => '\u2708', // airplane
        0x29 => '\u2709', // envelope
        0x2A => '\u261B', // hand pointing right
        0x2B => '\u261E', // hand pointing right (filled)
        0x2C => '\u270C', // victory hand
        0x2D => '\u270D', // writing hand
        0x2E => '\u270E', // pencil
        0x2F => '\u270F', // pencil (filled)

        0x30 => '\u2710', // pen nib
        0x31 => '\u2711', // pen nib (filled)
        0x32 => '\u2712', // pen nib (outline)
        0x33 => '\u2713', // checkmark
        0x34 => '\u2714', // checkmark (bold)
        0x35 => '\u2715', // multiplication X
        0x36 => '\u2716', // multiplication X (heavy)
        0x37 => '\u2717', // ballot X
        0x38 => '\u2718', // ballot X (heavy)
        0x39 => '\u2719', // outlined Greek cross
        0x3A => '\u271A', // heavy Greek cross
        0x3B => '\u271B', // open center cross
        0x3C => '\u271C', // heavy open center cross
        0x3D => '\u271D', // Latin cross
        0x3E => '\u271E', // Latin cross (shadowed)
        0x3F => '\u271F', // Latin cross (outline)

        // Common symbols
        0x40 => '\u2720', // Maltese cross
        0x41 => '\u2721', // Star of David
        0x42 => '\u2722', // four teardrop-spoked asterisk
        0x43 => '\u2723', // four balloon-spoked asterisk
        0x44 => '\u2724', // heavy four balloon-spoked asterisk
        0x45 => '\u2725', // four club-spoked asterisk
        0x46 => '\u2726', // black four pointed star
        0x47 => '\u2727', // white four pointed star
        0x48 => '\u2605', // black star
        0x49 => '\u2729', // outlined black star
        0x4A => '\u272A', // circled white star
        0x4B => '\u272B', // circled black star
        0x4C => '\u272C', // shadowed white star
        0x4D => '\u272D', // heavy asterisk
        0x4E => '\u272E', // eight spoke asterisk
        0x4F => '\u272F', // eight pointed black star

        // More ornaments
        0x50 => '\u2730', // eight pointed pinwheel star
        0x51 => '\u2731', // heavy eight pointed pinwheel star
        0x52 => '\u2732', // eight pointed star
        0x53 => '\u2733', // eight pointed star (outlined)
        0x54 => '\u2734', // eight pointed star (heavy)
        0x55 => '\u2735', // six pointed black star
        0x56 => '\u2736', // six pointed star
        0x57 => '\u2737', // eight pointed star (black)
        0x58 => '\u2738', // heavy eight pointed star
        0x59 => '\u2739', // twelve pointed black star
        0x5A => '\u273A', // sixteen pointed star
        0x5B => '\u273B', // teardrop-spoked asterisk
        0x5C => '\u273C', // open center teardrop-spoked asterisk
        0x5D => '\u273D', // heavy teardrop-spoked asterisk
        0x5E => '\u273E', // six petalled black and white florette
        0x5F => '\u273F', // black florette

        // Geometric shapes
        0x60 => '\u2740', // white florette
        0x61 => '\u2741', // eight petalled outlined black florette
        0x62 => '\u2742', // circled open center eight pointed star
        0x63 => '\u2743', // heavy teardrop-spoked pinwheel asterisk
        0x64 => '\u2744', // snowflake
        0x65 => '\u2745', // tight trifoliate snowflake
        0x66 => '\u2746', // heavy chevron snowflake
        0x67 => '\u2747', // sparkle
        0x68 => '\u2748', // heavy sparkle
        0x69 => '\u2749', // balloon-spoked asterisk
        0x6A => '\u274A', // eight teardrop-spoked propeller asterisk
        0x6B => '\u274B', // heavy eight teardrop-spoked propeller asterisk

        // Arrows
        0x6C => '\u25CF', // black circle
        0x6D => '\u25CB', // white circle
        0x6E => '\u274D', // shadowed white circle
        0x6F => '\u25A0', // black square
        0x70 => '\u25A1', // white square
        0x71 => '\u25A2', // white square with rounded corners
        0x72 => '\u25A3', // white square containing black small square
        0x73 => '\u25A4', // square with horizontal fill
        0x74 => '\u25A5', // square with vertical fill
        0x75 => '\u25A6', // square with orthogonal crosshatch fill
        0x76 => '\u25A7', // square with upper left to lower right fill
        0x77 => '\u25A8', // square with upper right to lower left fill
        0x78 => '\u25A9', // square with diagonal crosshatch fill
        0x79 => '\u25AA', // black small square
        0x7A => '\u25AB', // white small square

        // Circled digits (Annex D.6, octal 254–323), previously dropped. Codes
        // are the spec's octal CODE in hex; each range is contiguous in Unicode.

        // Arrows (Annex D.6, octal 324–376): four singletons, then two runs.
        0xD4 => '\u2794', // ➔ a160  heavy wide-headed rightwards arrow
        0xD5 => '\u2192', // → a161  rightwards arrow
        0xD6 => '\u2194', // ↔ a163  left right arrow
        0xD7 => '\u2195', // ↕ a164  up down arrow
        // Circled digits (Annex D.6, octal 254-323). Codes are the spec's octal CODE in hex;
        // each range is contiguous in Unicode.
        >= 0xAC and <= 0xB5 => (char)(0x2460 + (code - 0xAC)), // (1)..(10)  a120 a129
        >= 0xB6 and <= 0xBF => (char)(0x2776 + (code - 0xB6)), // dingbat negative circled  a130 a139
        >= 0xC0 and <= 0xC9 => (char)(0x2780 + (code - 0xC0)), // sans-serif circled  a140 a149
        >= 0xCA and <= 0xD3 => (char)(0x278A + (code - 0xCA)), // sans-serif negative circled  a150 a159
        // Arrows (Annex D.6, octal 324-376): four singletons above, then two runs.
        >= 0xD8 and <= 0xEF => (char)(0x2798 + (code - 0xD8)), // a196..a182
        >= 0xF1 and <= 0xFE => (char)(0x27B1 + (code - 0xF1)), // a201..a191
        _ => null,
    };

    /// <summary>
    /// <c>font_dict::standard_encoding_lookup</c> — resolve a byte in a named PDF encoding.
    /// Unknown encoding names fall back to identity for printable ASCII.
    /// </summary>
    internal static string? StandardEncodingLookup(string encoding, byte code)
    {
        switch (encoding)
        {
            case "PDFDocEncoding":
                return PdfDocEncodingLookup(code)?.ToString();

            case "WinAnsiEncoding":
            {
                if (code >= 32 && code <= 126)
                {
                    return ((char)code).ToString();
                }
                char? win = code switch
                {
                    // WinAnsiEncoding extended range (128-255)
                    // Based on Windows-1252 encoding
                    0x80 => '\u20AC', // Euro sign
                    0x82 => '\u201A', // Single low-9 quotation mark
                    0x83 => '\u0192', // Latin small letter f with hook
                    0x84 => '\u201E', // Double low-9 quotation mark
                    0x85 => '\u2026', // Horizontal ellipsis
                    0x86 => '\u2020', // Dagger
                    0x87 => '\u2021', // Double dagger
                    0x88 => '\u02C6', // Modifier letter circumflex accent
                    0x89 => '\u2030', // Per mille sign
                    0x8A => '\u0160', // Latin capital letter S with caron
                    0x8B => '\u2039', // Single left-pointing angle quotation mark
                    0x8C => '\u0152', // Latin capital ligature OE
                    0x8E => '\u017D', // Latin capital letter Z with caron
                    0x91 => '\u2018', // Left single quotation mark
                    0x92 => '\u2019', // Right single quotation mark
                    0x93 => '\u201C', // Left double quotation mark
                    0x94 => '\u201D', // Right double quotation mark
                    0x95 => '\u2022', // Bullet
                    0x96 => '\u2013', // En dash
                    0x97 => '\u2014', // Em dash
                    0x98 => '\u02DC', // Small tilde
                    0x99 => '\u2122', // Trade mark sign
                    0x9A => '\u0161', // Latin small letter s with caron
                    0x9B => '\u203A', // Single right-pointing angle quotation mark
                    0x9C => '\u0153', // Latin small ligature oe
                    0x9E => '\u017E', // Latin small letter z with caron
                    0x9F => '\u0178', // Latin capital letter Y with diaeresis
                    // 0xA0-0xFF: Direct mapping to Unicode (ISO-8859-1)
                    // 0xA0-0xFF: direct mapping to Unicode (ISO-8859-1)
                    >= 0xA0 => (char)code,
                    _ => null,
                };
                return win?.ToString();
            }

            case "StandardEncoding":
            {
                // StandardEncoding diverges sharply from ISO-8859-1 above 0x7F: an
                // ISO-8859-1 fallback there yields wrong characters for ligatures, smart
                // quotes and accents. Annex D Table D.1.
                if (code >= 32 && code <= 126)
                {
                    // 0x27 is quoteright (U+2019); every other printable ASCII code is identity.
                    char ch = code == 0x27 ? '\u2019' : (char)code;
                    return ch.ToString();
                }
                char? std = code switch
                {
                    0xA1 => '\u00A1', // exclamdown
                    0xA2 => '\u00A2', // cent
                    0xA3 => '\u00A3', // sterling
                    0xA4 => '\u2044', // fraction (NOT currency ¤)
                    0xA5 => '\u00A5', // yen
                    0xA6 => '\u0192', // florin (NOT broken bar)
                    0xA7 => '\u00A7', // section
                    0xA8 => '\u00A4', // currency (NOT dieresis)
                    0xA9 => '\u0027', // quotesingle (NOT copyright)
                    0xAA => '\u201C', // quotedblleft (NOT ordfeminine)
                    0xAB => '\u00AB', // guillemotleft
                    0xAC => '\u2039', // guilsinglleft (NOT not-sign)
                    0xAD => '\u203A', // guilsinglright (NOT soft-hyphen)
                    0xAE => '\uFB01', // fi ligature (NOT registered)
                    0xAF => '\uFB02', // fl ligature (NOT macron)
                    // 0xB0-0xBF
                    0xB1 => '\u2013', // endash (NOT plus-minus)
                    0xB2 => '\u2020', // dagger (NOT superscript 2)
                    0xB3 => '\u2021', // daggerdbl (NOT superscript 3)
                    0xB4 => '\u00B7', // periodcentered (NOT acute accent)
                    0xB6 => '\u00B6', // paragraph
                    0xB7 => '\u2022', // bullet (NOT middle dot)
                    0xB8 => '\u201A', // quotesinglbase (NOT cedilla)
                    0xB9 => '\u201E', // quotedblbase (NOT superscript 1)
                    0xBA => '\u201D', // quotedblright (NOT ordmasculine)
                    0xBB => '\u00BB', // guillemotright
                    0xBC => '\u2026', // ellipsis (NOT one quarter)
                    0xBD => '\u2030', // perthousand (NOT one half)
                    0xBF => '\u00BF', // questiondown
                    // 0xC0-0xCF — accent marks and modifiers
                    0xC1 => '\u0060', // grave (NOT A-grave)
                    0xC2 => '\u00B4', // acute (NOT A-circumflex)
                    0xC3 => '\u02C6', // circumflex (NOT A-tilde)
                    0xC4 => '\u02DC', // tilde (NOT A-dieresis)
                    0xC5 => '\u00AF', // macron (NOT A-ring)
                    0xC6 => '\u02D8', // breve (NOT AE)
                    0xC7 => '\u02D9', // dotaccent (NOT C-cedilla)
                    0xC8 => '\u00A8', // dieresis (NOT E-grave)
                    0xCA => '\u02DA', // ring (NOT E-circumflex)
                    0xCB => '\u00B8', // cedilla (NOT E-dieresis)
                    0xCD => '\u02DD', // hungarumlaut (NOT I-acute)
                    0xCE => '\u02DB', // ogonek (NOT I-circumflex)
                    0xCF => '\u02C7', // caron (NOT I-dieresis)
                    // 0xD0 — em dash
                    0xD0 => '\u2014', // emdash (NOT Eth)
                    // 0xE0-0xEF — uppercase special chars
                    0xE1 => '\u00C6', // AE (NOT a-acute)
                    0xE3 => '\u00AA', // ordfeminine (NOT a-tilde)
                    0xE8 => '\u0141', // Lslash (NOT e-grave)
                    0xE9 => '\u00D8', // Oslash (NOT e-acute)
                    0xEA => '\u0152', // OE (NOT e-circumflex)
                    0xEB => '\u00BA', // ordmasculine (NOT e-dieresis)
                    // 0xF0-0xFF — lowercase special chars
                    0xF1 => '\u00E6', // ae (NOT n-tilde)
                    0xF5 => '\u0131', // dotlessi (NOT o-tilde)
                    0xF8 => '\u0142', // lslash (NOT o-stroke)
                    0xF9 => '\u00F8', // oslash (NOT u-grave)
                    0xFA => '\u0153', // oe (NOT u-acute)
                    0xFB => '\u00DF', // germandbls (NOT u-circumflex)
                    _ => null,
                };
                return std?.ToString();
            }

            case "MacRomanEncoding":
            {
                if (code >= 32 && code <= 126)
                {
                    return ((char)code).ToString();
                }
                char? mac = code switch
                {
                    // Complete Mac OS Roman encoding per PDF Spec ISO 32000-1:2008, Annex D, Table D.2
                    // 0x80-0x9F: Accented letters
                    0x80 => '\u00C4', // Adieresis
                    0x81 => '\u00C5', // Aring
                    0x82 => '\u00C7', // Ccedilla
                    0x83 => '\u00C9', // Eacute
                    0x84 => '\u00D1', // Ntilde
                    0x85 => '\u00D6', // Odieresis
                    0x86 => '\u00DC', // Udieresis
                    0x87 => '\u00E1', // aacute
                    0x88 => '\u00E0', // agrave
                    0x89 => '\u00E2', // acircumflex
                    0x8A => '\u00E4', // adieresis
                    0x8B => '\u00E3', // atilde
                    0x8C => '\u00E5', // aring
                    0x8D => '\u00E7', // ccedilla
                    0x8E => '\u00E9', // eacute
                    0x8F => '\u00E8', // egrave
                    0x90 => '\u00EA', // ecircumflex
                    0x91 => '\u00EB', // edieresis
                    0x92 => '\u00ED', // iacute
                    0x93 => '\u00EC', // igrave
                    0x94 => '\u00EE', // icircumflex
                    0x95 => '\u00EF', // idieresis
                    0x96 => '\u00F1', // ntilde
                    0x97 => '\u00F3', // oacute
                    0x98 => '\u00F2', // ograve
                    0x99 => '\u00F4', // ocircumflex
                    0x9A => '\u00F6', // odieresis
                    0x9B => '\u00F5', // otilde
                    0x9C => '\u00FA', // uacute
                    0x9D => '\u00F9', // ugrave
                    0x9E => '\u00FB', // ucircumflex
                    0x9F => '\u00FC', // udieresis
                    // 0xA0-0xBF: Symbols and punctuation (NOT Latin-1!)
                    0xA0 => '\u2020', // dagger (NOT NBSP)
                    0xA1 => '\u00B0', // degree (NOT inverted exclamation)
                    0xA2 => '\u00A2', // cent
                    0xA3 => '\u00A3', // sterling
                    0xA4 => '\u00A7', // section (NOT currency sign)
                    0xA5 => '\u2022', // bullet (NOT yen)
                    0xA6 => '\u00B6', // paragraph (NOT broken bar)
                    0xA7 => '\u00DF', // germandbls (NOT section)
                    0xA8 => '\u00AE', // registered (NOT dieresis)
                    0xA9 => '\u00A9', // copyright
                    0xAA => '\u2122', // trademark (NOT ordfeminine)
                    0xAB => '\u00B4', // acute (NOT guillemotleft)
                    0xAC => '\u00A8', // dieresis (NOT logical not)
                    0xAD => '\u2260', // notequal (NOT soft hyphen)
                    0xAE => '\u00C6', // AE (NOT registered)
                    0xAF => '\u00D8', // Oslash (NOT macron)
                    0xB0 => '\u221E', // infinity (NOT degree)
                    0xB1 => '\u00B1', // plusminus
                    0xB2 => '\u2264', // lessequal (NOT superscript 2)
                    0xB3 => '\u2265', // greaterequal (NOT superscript 3)
                    0xB4 => '\u00A5', // yen (NOT acute)
                    0xB5 => '\u00B5', // mu
                    0xB6 => '\u2202', // partialdiff (NOT paragraph)
                    0xB7 => '\u2211', // summation (NOT middle dot)
                    0xB8 => '\u220F', // product (NOT cedilla)
                    0xB9 => '\u03C0', // pi (NOT superscript 1)
                    0xBA => '\u222B', // integral (NOT ordmasculine)
                    0xBB => '\u00AA', // ordfeminine (NOT guillemotright)
                    0xBC => '\u00BA', // ordmasculine (NOT one quarter)
                    0xBD => '\u2126', // Omega (NOT one half)
                    0xBE => '\u00E6', // ae (NOT three quarters)
                    0xBF => '\u00F8', // oslash (NOT inverted question)
                    // 0xC0-0xCF: More symbols and accented capitals
                    0xC0 => '\u00BF', // questiondown
                    0xC1 => '\u00A1', // exclamdown
                    0xC2 => '\u00AC', // logicalnot
                    0xC3 => '\u221A', // radical
                    0xC4 => '\u0192', // florin
                    0xC5 => '\u2248', // approxequal
                    0xC6 => '\u2206', // Delta
                    0xC7 => '\u00AB', // guillemotleft
                    0xC8 => '\u00BB', // guillemotright
                    0xC9 => '\u2026', // ellipsis
                    0xCA => '\u00A0', // nonbreakingspace
                    0xCB => '\u00C0', // Agrave
                    0xCC => '\u00C3', // Atilde
                    0xCD => '\u00D5', // Otilde
                    0xCE => '\u0152', // OE
                    0xCF => '\u0153', // oe
                    // 0xD0-0xDF: Dashes, quotes, ligatures
                    0xD0 => '\u2013', // endash
                    0xD1 => '\u2014', // emdash
                    0xD2 => '\u201C', // quotedblleft
                    0xD3 => '\u201D', // quotedblright
                    0xD4 => '\u2018', // quoteleft
                    0xD5 => '\u2019', // quoteright
                    0xD6 => '\u00F7', // divide
                    0xD7 => '\u25CA', // lozenge
                    0xD8 => '\u00FF', // ydieresis
                    0xD9 => '\u0178', // Ydieresis
                    0xDA => '\u2044', // fraction
                    0xDB => '\u20AC', // Euro
                    0xDC => '\u2039', // guilsinglleft
                    0xDD => '\u203A', // guilsinglright
                    0xDE => '\uFB01', // fi ligature
                    0xDF => '\uFB02', // fl ligature
                    // 0xE0-0xEF: More symbols and accented capitals
                    0xE0 => '\u2021', // daggerdbl
                    0xE1 => '\u00B7', // periodcentered
                    0xE2 => '\u201A', // quotesinglbase
                    0xE3 => '\u201E', // quotedblbase
                    0xE4 => '\u2030', // perthousand
                    0xE5 => '\u00C2', // Acircumflex
                    0xE6 => '\u00CA', // Ecircumflex
                    0xE7 => '\u00C1', // Aacute
                    0xE8 => '\u00CB', // Edieresis
                    0xE9 => '\u00C8', // Egrave
                    0xEA => '\u00CD', // Iacute
                    0xEB => '\u00CE', // Icircumflex
                    0xEC => '\u00CF', // Idieresis
                    0xED => '\u00CC', // Igrave
                    0xEE => '\u00D3', // Oacute
                    0xEF => '\u00D4', // Ocircumflex
                    // 0xF0-0xFF: More accented and special chars
                    0xF0 => '\uF8FF', // Apple logo (private use area)
                    0xF1 => '\u00D2', // Ograve
                    0xF2 => '\u00DA', // Uacute
                    0xF3 => '\u00DB', // Ucircumflex
                    0xF4 => '\u00D9', // Ugrave
                    0xF5 => '\u0131', // dotlessi
                    0xF6 => '\u02C6', // circumflex
                    0xF7 => '\u02DC', // tilde
                    0xF8 => '\u00AF', // macron
                    0xF9 => '\u02D8', // breve
                    0xFA => '\u02D9', // dotaccent
                    0xFB => '\u02DA', // ring
                    0xFC => '\u00B8', // cedilla
                    0xFD => '\u02DD', // hungarumlaut
                    0xFE => '\u02DB', // ogonek
                    0xFF => '\u02C7', // caron
                    _ => null,
                };
                return mac?.ToString();
            }

            default:
                // Unknown encoding: identity for printable ASCII only.
                return code <= 0x7F && code >= 32 ? ((char)code).ToString() : null;
        }
    }

    /// <summary>
    /// <c>FontInfo::gid_to_standard_glyph_name</c> — map a glyph/byte code to a standard
    /// PostScript glyph name across ASCII, the Windows-1252 extensions and Latin-1
    /// Supplement, so the result can be looked up in the Adobe Glyph List.
    /// </summary>
    internal static string? GidToStandardGlyphName(ushort gid) => gid switch
    {
        // Control characters and whitespace (32-33)
        0x20 => "space",
        0x21 => "exclam",
        0x22 => "quotedbl",
        0x23 => "numbersign",
        0x24 => "dollar",
        0x25 => "percent",
        0x26 => "ampersand",
        0x27 => "quoteright",
        0x28 => "parenleft",
        0x29 => "parenright",
        0x2A => "asterisk",
        0x2B => "plus",
        0x2C => "comma",
        0x2D => "hyphen",
        0x2E => "period",
        0x2F => "slash",
        // Digits (48-57)
        0x30 => "zero",
        0x31 => "one",
        0x32 => "two",
        0x33 => "three",
        0x34 => "four",
        0x35 => "five",
        0x36 => "six",
        0x37 => "seven",
        0x38 => "eight",
        0x39 => "nine",
        // Punctuation (58-64)
        0x3A => "colon",
        0x3B => "semicolon",
        0x3C => "less",
        0x3D => "equal",
        0x3E => "greater",
        0x3F => "question",
        0x40 => "at",
        // Uppercase letters (65-90)
        0x41 => "A",
        0x42 => "B",
        0x43 => "C",
        0x44 => "D",
        0x45 => "E",
        0x46 => "F",
        0x47 => "G",
        0x48 => "H",
        0x49 => "I",
        0x4A => "J",
        0x4B => "K",
        0x4C => "L",
        0x4D => "M",
        0x4E => "N",
        0x4F => "O",
        0x50 => "P",
        0x51 => "Q",
        0x52 => "R",
        0x53 => "S",
        0x54 => "T",
        0x55 => "U",
        0x56 => "V",
        0x57 => "W",
        0x58 => "X",
        0x59 => "Y",
        0x5A => "Z",
        // Brackets (91-96)
        0x5B => "bracketleft",
        0x5C => "backslash",
        0x5D => "bracketright",
        0x5E => "asciicircum",
        0x5F => "underscore",
        0x60 => "quoteleft",
        // Lowercase letters (97-122)
        0x61 => "a",
        0x62 => "b",
        0x63 => "c",
        0x64 => "d",
        0x65 => "e",
        0x66 => "f",
        0x67 => "g",
        0x68 => "h",
        0x69 => "i",
        0x6A => "j",
        0x6B => "k",
        0x6C => "l",
        0x6D => "m",
        0x6E => "n",
        0x6F => "o",
        0x70 => "p",
        0x71 => "q",
        0x72 => "r",
        0x73 => "s",
        0x74 => "t",
        0x75 => "u",
        0x76 => "v",
        0x77 => "w",
        0x78 => "x",
        0x79 => "y",
        0x7A => "z",
        // Braces (123-126)
        0x7B => "braceleft",
        0x7C => "bar",
        0x7D => "braceright",
        0x7E => "asciitilde",

        // ==================================================================================
        // Extended Latin / Windows-1252 range (0x80-0xFF)
        // ==================================================================================
        // These mappings cover the extended ASCII characters commonly found in Western
        // European PDFs. When a Type0 font with Identity CMap encounters these GIDs,
        // we map them to their standard PostScript glyph names for AGL lookup.
        //
        // Per PDF Spec ISO 32000-1:2008 Section 9.10.2, when ToUnicode CMap is unavailable,
        // readers may use glyph name lookup as a fallback mechanism.

        // 0x80-0x8F: Windows-1252 extended control characters and symbols
        0x80 => "euro", // U+20AC (Euro sign)
        // 0x81: undefined in Windows-1252
        0x82 => "quotesinglbase", // U+201A (Single low quotation mark)
        0x83 => "florin", // U+0192 (Latin small letter f with hook)
        0x84 => "quotedblbase", // U+201E (Double low quotation mark)
        0x85 => "ellipsis", // U+2026 (Horizontal ellipsis)
        0x86 => "dagger", // U+2020 (Dagger)
        0x87 => "daggerdbl", // U+2021 (Double dagger)
        0x88 => "circumflex", // U+02C6 (Modifier letter circumflex accent)
        0x89 => "perthousand", // U+2030 (Per mille sign)
        0x8A => "Scaron", // U+0160 (Latin capital letter S with caron)
        0x8B => "guilsinglleft", // U+2039 (Single left-pointing angle quotation mark)
        0x8C => "OE", // U+0152 (Latin capital ligature OE)
        // 0x8D: undefined in Windows-1252
        0x8E => "Zcaron", // U+017D (Latin capital letter Z with caron)
        // 0x8F: undefined in Windows-1252

        // 0x90-0x9F: Windows-1252 smart quotes, dashes, and accents
        // 0x90: undefined in Windows-1252
        0x91 => "quoteleft", // U+2018 (Left single quotation mark)
        0x92 => "quoteright", // U+2019 (Right single quotation mark)
        0x93 => "quotedblleft", // U+201C (Left double quotation mark)
        0x94 => "quotedblright", // U+201D (Right double quotation mark)
        0x95 => "bullet", // U+2022 (Bullet)
        0x96 => "endash", // U+2013 (En dash)
        0x97 => "emdash", // U+2014 (Em dash)
        0x98 => "tilde", // U+02DC (Small tilde)
        0x99 => "trademark", // U+2122 (Trade mark sign)
        0x9A => "scaron", // U+0161 (Latin small letter s with caron)
        0x9B => "guilsinglright", // U+203A (Single right-pointing angle quotation mark)
        0x9C => "oe", // U+0153 (Latin small ligature oe)
        // 0x9D: undefined in Windows-1252
        0x9E => "zcaron", // U+017E (Latin small letter z with caron)
        0x9F => "Ydieresis", // U+0178 (Latin capital letter Y with diaeresis)

        // 0xA0-0xFF: Latin-1 Supplement (ISO 8859-1)
        // Most of these are direct character mappings (À-ÿ)
        // Implement programmatically for common characters and fallback to glyph name generation
        0xA0 => "space", // U+00A0 (No-break space)
        0xA1 => "exclamdown", // U+00A1 (Inverted exclamation mark)
        0xA2 => "cent", // U+00A2 (Cent sign)
        0xA3 => "sterling", // U+00A3 (Pound sign)
        0xA4 => "currency", // U+00A4 (Currency sign)
        0xA5 => "yen", // U+00A5 (Yen sign)
        0xA6 => "brokenbar", // U+00A6 (Broken bar)
        0xA7 => "section", // U+00A7 (Section sign)
        0xA8 => "dieresis", // U+00A8 (Diaeresis)
        0xA9 => "copyright", // U+00A9 (Copyright sign)
        0xAA => "ordfeminine", // U+00AA (Feminine ordinal indicator)
        0xAB => "guillemotleft", // U+00AB (Left-pointing double angle quotation mark)
        0xAC => "logicalnot", // U+00AC (Not sign)
        0xAD => "uni00AD", // U+00AD (Soft hyphen)
        0xAE => "registered", // U+00AE (Registered sign)
        0xAF => "macron", // U+00AF (Macron)
        0xB0 => "degree", // U+00B0 (Degree sign)
        0xB1 => "plusminus", // U+00B1 (Plus-minus sign)
        0xB2 => "twosuperior", // U+00B2 (Superscript two)
        0xB3 => "threesuperior", // U+00B3 (Superscript three)
        0xB4 => "acute", // U+00B4 (Acute accent)
        0xB5 => "mu", // U+00B5 (Micro sign)
        0xB6 => "paragraph", // U+00B6 (Pilcrow)
        0xB7 => "middot", // U+00B7 (Middle dot)
        0xB8 => "cedilla", // U+00B8 (Cedilla)
        0xB9 => "onesuperior", // U+00B9 (Superscript one)
        0xBA => "ordmasculine", // U+00BA (Masculine ordinal indicator)
        0xBB => "guillemotright", // U+00BB (Right-pointing double angle quotation mark)
        0xBC => "onequarter", // U+00BC (Vulgar fraction one quarter)
        0xBD => "onehalf", // U+00BD (Vulgar fraction one half)
        0xBE => "threequarters", // U+00BE (Vulgar fraction three quarters)
        0xBF => "questiondown", // U+00BF (Inverted question mark)

        // 0xC0-0xFE: Latin-1 Supplement letters (À-þ)
        // These map directly to their Unicode equivalents via standard PostScript names
        // Format: glyph name is the Unicode character itself (e.g., "Agrave" for U+00C0)
        0xC0 => "Agrave", // U+00C0 (Latin capital letter A with grave)
        0xC1 => "Aacute", // U+00C1 (Latin capital letter A with acute)
        0xC2 => "Acircumflex", // U+00C2 (Latin capital letter A with circumflex)
        0xC3 => "Atilde", // U+00C3 (Latin capital letter A with tilde)
        0xC4 => "Adieresis", // U+00C4 (Latin capital letter A with diaeresis)
        0xC5 => "Aring", // U+00C5 (Latin capital letter A with ring above)
        0xC6 => "AE", // U+00C6 (Latin capital letter AE)
        0xC7 => "Ccedilla", // U+00C7 (Latin capital letter C with cedilla)
        0xC8 => "Egrave", // U+00C8 (Latin capital letter E with grave)
        0xC9 => "Eacute", // U+00C9 (Latin capital letter E with acute)
        0xCA => "Ecircumflex", // U+00CA (Latin capital letter E with circumflex)
        0xCB => "Edieresis", // U+00CB (Latin capital letter E with diaeresis)
        0xCC => "Igrave", // U+00CC (Latin capital letter I with grave)
        0xCD => "Iacute", // U+00CD (Latin capital letter I with acute)
        0xCE => "Icircumflex", // U+00CE (Latin capital letter I with circumflex)
        0xCF => "Idieresis", // U+00CF (Latin capital letter I with diaeresis)
        0xD0 => "Eth", // U+00D0 (Latin capital letter Eth)
        0xD1 => "Ntilde", // U+00D1 (Latin capital letter N with tilde)
        0xD2 => "Ograve", // U+00D2 (Latin capital letter O with grave)
        0xD3 => "Oacute", // U+00D3 (Latin capital letter O with acute)
        0xD4 => "Ocircumflex", // U+00D4 (Latin capital letter O with circumflex)
        0xD5 => "Otilde", // U+00D5 (Latin capital letter O with tilde)
        0xD6 => "Odieresis", // U+00D6 (Latin capital letter O with diaeresis)
        0xD7 => "multiply", // U+00D7 (Multiplication sign)
        0xD8 => "Oslash", // U+00D8 (Latin capital letter O with stroke)
        0xD9 => "Ugrave", // U+00D9 (Latin capital letter U with grave)
        0xDA => "Uacute", // U+00DA (Latin capital letter U with acute)
        0xDB => "Ucircumflex", // U+00DB (Latin capital letter U with circumflex)
        0xDC => "Udieresis", // U+00DC (Latin capital letter U with diaeresis)
        0xDD => "Yacute", // U+00DD (Latin capital letter Y with acute)
        0xDE => "Thorn", // U+00DE (Latin capital letter Thorn)
        0xDF => "germandbls", // U+00DF (Latin small letter sharp s)
        0xE0 => "agrave", // U+00E0 (Latin small letter a with grave)
        0xE1 => "aacute", // U+00E1 (Latin small letter a with acute)
        0xE2 => "acircumflex", // U+00E2 (Latin small letter a with circumflex)
        0xE3 => "atilde", // U+00E3 (Latin small letter a with tilde)
        0xE4 => "adieresis", // U+00E4 (Latin small letter a with diaeresis)
        0xE5 => "aring", // U+00E5 (Latin small letter a with ring above)
        0xE6 => "ae", // U+00E6 (Latin small letter ae)
        0xE7 => "ccedilla", // U+00E7 (Latin small letter c with cedilla)
        0xE8 => "egrave", // U+00E8 (Latin small letter e with grave)
        0xE9 => "eacute", // U+00E9 (Latin small letter e with acute)
        0xEA => "ecircumflex", // U+00EA (Latin small letter e with circumflex)
        0xEB => "edieresis", // U+00EB (Latin small letter e with diaeresis)
        0xEC => "igrave", // U+00EC (Latin small letter i with grave)
        0xED => "iacute", // U+00ED (Latin small letter i with acute)
        0xEE => "icircumflex", // U+00EE (Latin small letter i with circumflex)
        0xEF => "idieresis", // U+00EF (Latin small letter i with diaeresis)
        0xF0 => "eth", // U+00F0 (Latin small letter eth)
        0xF1 => "ntilde", // U+00F1 (Latin small letter n with tilde)
        0xF2 => "ograve", // U+00F2 (Latin small letter o with grave)
        0xF3 => "oacute", // U+00F3 (Latin small letter o with acute)
        0xF4 => "ocircumflex", // U+00F4 (Latin small letter o with circumflex)
        0xF5 => "otilde", // U+00F5 (Latin small letter o with tilde)
        0xF6 => "odieresis", // U+00F6 (Latin small letter o with diaeresis)
        0xF7 => "divide", // U+00F7 (Division sign)
        0xF8 => "oslash", // U+00F8 (Latin small letter o with stroke)
        0xF9 => "ugrave", // U+00F9 (Latin small letter u with grave)
        0xFA => "uacute", // U+00FA (Latin small letter u with acute)
        0xFB => "ucircumflex", // U+00FB (Latin small letter u with circumflex)
        0xFC => "udieresis", // U+00FC (Latin small letter u with diaeresis)
        0xFD => "yacute", // U+00FD (Latin small letter y with acute)
        0xFE => "thorn", // U+00FE (Latin small letter thorn)
        0xFF => "ydieresis", // U+00FF (Latin small letter y with diaeresis)
        // All other GIDs not in the supported ranges
        _ => null,
    };

    /// <summary>
    /// <c>font_dict::builtin_encoding_looks_like_cipher</c> — decide whether an embedded font
    /// program's built-in /Encoding is a re-indexed subset cipher rather than a real text
    /// encoding to overlay on the producer-declared named base.
    ///
    /// A real encoding (a few non-standard slots over a named base, e.g. space at 0xCA) agrees
    /// with the named base on most shared codes; a subset cipher — the font's own arbitrary
    /// glyph ordering — agrees on almost none, and overlaying it rewrites every mapped code
    /// into mojibake. Empty overlap is NOT a cipher: no evidence either way, keep the overlay.
    /// </summary>
    internal static bool BuiltinEncodingLooksLikeCipher(IReadOnlyDictionary<byte, Rune> progEnc, string stdName)
    {
        uint agree = 0;
        uint overlap = 0;
        foreach (KeyValuePair<byte, Rune> kv in progEnc)
        {
            string? us = StandardEncodingLookup(stdName, kv.Key);
            if (us is null || us.Length == 0)
            {
                continue;
            }
            var e = us.EnumerateRunes();
            if (!e.MoveNext())
            {
                continue;
            }
            overlap += 1;
            if (e.Current == kv.Value)
            {
                agree += 1;
            }
        }
        return overlap > 0 && ((float)agree / overlap) < 0.5f;
    }
}
