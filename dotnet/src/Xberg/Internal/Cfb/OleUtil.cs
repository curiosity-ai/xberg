using System.Text;

namespace Xberg.Internal.Cfb;

/// <summary>
/// Shared helpers for the legacy OLE binary formats: CP1252→char mapping and the OLE
/// SummaryInformation / DocumentSummaryInformation property-set reader. Ports the identical
/// functions duplicated across the Rust <c>extraction/doc/mod.rs</c> and <c>extraction/ppt/mod.rs</c>.
/// </summary>
internal static class OleUtil
{
    /// <summary>Convert a CP1252 byte to its Unicode char (identity outside 0x80–0x9F).</summary>
    public static char Cp1252ToChar(byte b) => b switch
    {
        0x80 => '€', 0x82 => '‚', 0x83 => 'ƒ', 0x84 => '„',
        0x85 => '…', 0x86 => '†', 0x87 => '‡', 0x88 => 'ˆ',
        0x89 => '‰', 0x8A => 'Š', 0x8B => '‹', 0x8C => 'Œ',
        0x8E => 'Ž', 0x91 => '‘', 0x92 => '’', 0x93 => '“',
        0x94 => '”', 0x95 => '•', 0x96 => '–', 0x97 => '—',
        0x98 => '˜', 0x99 => '™', 0x9A => 'š', 0x9B => '›',
        0x9C => 'œ', 0x9E => 'ž', 0x9F => 'Ÿ',
        _ => (char)b,
    };

    /// <summary>Parsed subset of the SummaryInformation property set.</summary>
    public sealed class OleMetadata
    {
        public string? Title;
        public string? Subject;
        public string? Author;
        public string? LastAuthor;
        public string? RevisionNumber;
    }

    /// <summary>Parse the SummaryInformation property set into <see cref="OleMetadata"/>
    /// (property IDs 2/3/4/8/9 → title/subject/author/lastAuthor/revision).</summary>
    public static void ParseSummaryInfo(byte[] data, OleMetadata meta)
    {
        if (data.Length < 48) return;
        int setOffset = (int)U32(data, 44);
        ParsePropertySet(data, setOffset, meta);
    }

    private static void ParsePropertySet(byte[] data, int setOffset, OleMetadata meta)
    {
        if (setOffset < 0 || setOffset + 8 > data.Length) return;
        int numProps = (int)U32(data, setOffset + 4);
        int propsStart = setOffset + 8;
        for (int i = 0; i < numProps; i++)
        {
            int entryOffset = propsStart + i * 8;
            if (entryOffset + 8 > data.Length) break;
            uint propId = U32(data, entryOffset);
            int propOffset = (int)U32(data, entryOffset + 4);
            int absOffset = setOffset + propOffset;
            if (absOffset + 8 > data.Length) continue;
            string? value = ReadPropertyValue(data, absOffset);
            if (value is null) continue;
            switch (propId)
            {
                case 2: meta.Title = value; break;
                case 3: meta.Subject = value; break;
                case 4: meta.Author = value; break;
                case 8: meta.LastAuthor = value; break;
                case 9: meta.RevisionNumber = value; break;
            }
        }
    }

    private static string? ReadPropertyValue(byte[] data, int offset)
    {
        if (offset + 8 > data.Length) return null;
        uint vt = U32(data, offset);
        switch (vt)
        {
            case 30: // VT_LPSTR — code-page string
            {
                int len = (int)U32(data, offset + 4);
                if (len <= 0 || offset + 8 + len > data.Length) return null;
                int end = offset + 8;
                while (end < offset + 8 + len && data[end] != 0) end++;
                var sb = new StringBuilder(end - (offset + 8));
                for (int i = offset + 8; i < end; i++) sb.Append((char)data[i]);
                // from_utf8_lossy over the raw bytes: decode as UTF-8.
                var bytes = new byte[end - (offset + 8)];
                Array.Copy(data, offset + 8, bytes, 0, bytes.Length);
                return Utf8Lossy(bytes);
            }
            case 31: // VT_LPWSTR — UTF-16LE string
            {
                int len = (int)U32(data, offset + 4);
                if (len <= 0 || offset + 8 + len * 2 > data.Length) return null;
                var sb = new StringBuilder(len);
                for (int i = 0; i < len; i++)
                {
                    ushort cu = (ushort)(data[offset + 8 + i * 2] | (data[offset + 9 + i * 2] << 8));
                    if (cu == 0) break;
                    sb.Append((char)cu);
                }
                return sb.ToString();
            }
            default: return null;
        }
    }

    private static string Utf8Lossy(byte[] bytes) =>
        new UTF8Encoding(false, false).GetString(bytes);

    internal static uint U32(byte[] b, int o) =>
        o + 3 < b.Length ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) : 0u;
    internal static int U16(byte[] b, int o) => o + 1 < b.Length ? b[o] | (b[o + 1] << 8) : 0;
}
