// Port of pdf_oxide 0.3.77 `fonts/type1_encoding.rs` — parse_type1_encoding.
//
// Type 1 font programs carry their built-in encoding in the clear-text (ASCII) section as
//     /Encoding 256 array
//     0 1 255 {1 index exch /.notdef put} for
//     dup 11 /ff put
//     …
//     readonly def
// Per PDF spec §9.6.6.2, when no /BaseEncoding is given the implicit base encoding for an
// embedded font is the font program's built-in encoding, so this table is authoritative.

using System.Text;

namespace Xberg.Internal.PdfOxide.Fonts;

internal static class OxType1Encoding
{
    /// <summary>
    /// Scan the clear-text section of a Type 1 font program for
    /// <c>dup CODE /GLYPHNAME put</c> entries, returning code → Unicode.
    /// Returns null when the font declares the predefined StandardEncoding or nothing parses.
    /// </summary>
    internal static Dictionary<byte, Rune>? ParseType1Encoding(ReadOnlySpan<byte> fontData)
    {
        // The clear text section is typically well under 10 KB; cap the scan so a large
        // binary (eexec-encrypted) tail is never walked.
        int searchLimit = Math.Min(fontData.Length, 65536);
        ReadOnlySpan<byte> searchData = fontData[..searchLimit];

        int encodingPos = FindBytes(searchData, "/Encoding"u8);
        if (encodingPos < 0)
        {
            return null;
        }

        // "/Encoding StandardEncoding def" means there is no custom table — return null so
        // the caller's default StandardEncoding handling takes over.
        ReadOnlySpan<byte> afterEncoding = searchData[(encodingPos + 9)..];
        ReadOnlySpan<byte> trimmed = SkipWhitespace(afterEncoding);
        if (trimmed.StartsWith("StandardEncoding"u8))
        {
            return null;
        }

        var encodingMap = new Dictionary<byte, Rune>();
        int pos = encodingPos;

        while (pos < searchLimit)
        {
            ReadOnlySpan<byte> remaining = searchData[pos..];
            int dupOffset = FindBytes(remaining, "dup"u8);
            if (dupOffset < 0)
            {
                break;
            }
            pos += dupOffset + 3;

            ReadOnlySpan<byte> beforeDup = searchData[(pos - 3)..Math.Min(pos, searchLimit)];
            if (beforeDup.SequenceEqual("def"u8))
            {
                break;
            }

            remaining = searchData[pos..Math.Min(searchLimit, searchData.Length)];
            if (TryParseDupEntry(remaining, out byte code, out string glyphName, out int consumed))
            {
                Rune? unicodeChar = OxGlyphNames.GlyphNameToUnicode(glyphName);
                if (unicodeChar is not null)
                {
                    encodingMap[code] = unicodeChar.Value;
                }
                pos += consumed;
            }

            // "readonly" / "def" closes the encoding array.
            remaining = searchData[pos..Math.Min(searchLimit, searchData.Length)];
            trimmed = SkipWhitespace(remaining);
            if (trimmed.StartsWith("readonly"u8) || trimmed.StartsWith("def"u8))
            {
                break;
            }
        }

        return encodingMap.Count == 0 ? null : encodingMap;
    }

    /// <summary>Parse a single "CODE /GLYPHNAME put" entry following "dup".</summary>
    private static bool TryParseDupEntry(
        ReadOnlySpan<byte> data,
        out byte code,
        out string glyphName,
        out int consumed)
    {
        code = 0;
        glyphName = "";
        consumed = 0;

        int pos = 0;
        while (pos < data.Length && IsWhitespace(data[pos]))
        {
            pos++;
        }

        int codeStart = pos;
        while (pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9')
        {
            pos++;
        }
        if (pos == codeStart)
        {
            return false;
        }
        if (!ushort.TryParse(Encoding.ASCII.GetString(data[codeStart..pos]), out ushort codeValue))
        {
            return false;
        }
        if (codeValue > 255)
        {
            return false;
        }

        while (pos < data.Length && IsWhitespace(data[pos]))
        {
            pos++;
        }

        if (pos >= data.Length || data[pos] != (byte)'/')
        {
            return false;
        }
        pos++;

        int nameStart = pos;
        while (pos < data.Length && IsGlyphNameChar(data[pos]))
        {
            pos++;
        }
        if (pos == nameStart)
        {
            return false;
        }
        string name = Encoding.ASCII.GetString(data[nameStart..pos]);

        while (pos < data.Length && IsWhitespace(data[pos]))
        {
            pos++;
        }
        if (!data[pos..].StartsWith("put"u8))
        {
            return false;
        }
        pos += 3;

        code = (byte)codeValue;
        glyphName = name;
        consumed = pos;
        return true;
    }

    private static int FindBytes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle) => data.IndexOf(needle);

    private static ReadOnlySpan<byte> SkipWhitespace(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        while (pos < data.Length && IsWhitespace(data[pos]))
        {
            pos++;
        }
        return data[pos..];
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private static bool IsGlyphNameChar(byte b) =>
        char.IsAsciiLetterOrDigit((char)b) || b == (byte)'.' || b == (byte)'_';
}
