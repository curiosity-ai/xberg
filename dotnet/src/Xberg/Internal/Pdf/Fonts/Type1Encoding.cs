namespace Xberg.Internal.Pdf.Fonts;

/// <summary>
/// Reads the built-in encoding out of an embedded Type 1 font program (<c>/FontFile</c>).
/// </summary>
/// <remarks>
/// A Type 1 program states its own encoding in the clear-text section that precedes the
/// encrypted <c>eexec</c> block, as a PostScript array built one slot at a time:
/// <code>
/// /Encoding 256 array
/// 0 1 255 {1 index exch /.notdef put} for
/// dup 11 /ff put
/// dup 12 /fi put
/// readonly def
/// </code>
/// ISO 32000-1 §9.6.6.1 makes this the implicit base encoding when the font dictionary names
/// none, which is how a TeX font gets its ligatures into codes 11-15 — positions no named
/// encoding assigns. Without it those codes fall through to whatever the default encoding says
/// and a word like "efficient" loses its middle.
/// </remarks>
internal static class Type1Encoding
{
    /// <summary>
    /// The code → text map a Type 1 program declares, or <c>null</c> when it declares none (an
    /// explicit <c>/Encoding StandardEncoding def</c> counts as none — the caller's default for
    /// that name is already right).
    /// </summary>
    public static Dictionary<int, string>? Parse(byte[] fontData)
    {
        // The clear-text header is small; everything past it is encrypted and cannot contain
        // readable `dup … put` entries, so a bounded scan cannot miss the array.
        int limit = Math.Min(fontData.Length, 65536);
        int encPos = IndexOf(fontData, "/Encoding"u8, 0, limit);
        if (encPos < 0) return null;

        int after = SkipWhitespace(fontData, encPos + 9, limit);
        if (StartsWith(fontData, after, limit, "StandardEncoding"u8)) return null;

        var map = new Dictionary<int, string>();
        int pos = encPos;
        while (pos < limit)
        {
            int dup = IndexOf(fontData, "dup"u8, pos, limit);
            if (dup < 0) break;
            pos = dup + 3;
            if (dup >= 3 && StartsWith(fontData, dup - 3, limit, "def"u8)) break;

            if (TryParseDupEntry(fontData, pos, limit, out int code, out string glyph, out int next))
            {
                string text = PdfEncodings.GlyphNameToUnicode(glyph);
                if (text.Length != 0) map[code] = text;
                pos = next;
            }

            int t = SkipWhitespace(fontData, pos, limit);
            if (StartsWith(fontData, t, limit, "readonly"u8) || StartsWith(fontData, t, limit, "def"u8)) break;
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>Parse one <c>CODE /GLYPHNAME put</c> entry, positioned just after its `dup`.</summary>
    private static bool TryParseDupEntry(
        byte[] data, int pos, int limit, out int code, out string glyph, out int next)
    {
        code = 0; glyph = ""; next = pos;

        pos = SkipWhitespace(data, pos, limit);
        int digits = pos;
        while (pos < limit && data[pos] >= (byte)'0' && data[pos] <= (byte)'9') pos++;
        if (pos == digits) return false;
        if (!int.TryParse(System.Text.Encoding.ASCII.GetString(data, digits, pos - digits), out int parsed)) return false;
        if (parsed > 255) return false;

        pos = SkipWhitespace(data, pos, limit);
        if (pos >= limit || data[pos] != (byte)'/') return false;
        pos++;

        int nameStart = pos;
        while (pos < limit && IsGlyphNameChar(data[pos])) pos++;
        if (pos == nameStart) return false;
        string name = System.Text.Encoding.ASCII.GetString(data, nameStart, pos - nameStart);

        pos = SkipWhitespace(data, pos, limit);
        if (!StartsWith(data, pos, limit, "put"u8)) return false;

        code = parsed;
        glyph = name;
        next = pos + 3;
        return true;
    }

    private static bool IsGlyphNameChar(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z') || b == (byte)'.' || b == (byte)'_';

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private static int SkipWhitespace(byte[] data, int pos, int limit)
    {
        while (pos < limit && IsWhitespace(data[pos])) pos++;
        return pos;
    }

    private static bool StartsWith(byte[] data, int pos, int limit, ReadOnlySpan<byte> needle)
    {
        if (pos < 0 || pos + needle.Length > limit) return false;
        return data.AsSpan(pos, needle.Length).SequenceEqual(needle);
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> needle, int from, int limit)
    {
        for (int i = Math.Max(0, from); i + needle.Length <= limit; i++)
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
