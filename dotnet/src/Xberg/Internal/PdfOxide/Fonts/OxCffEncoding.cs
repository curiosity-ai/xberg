// Port of pdf_oxide 0.3.77 `fonts/cff_encoding.rs` — parse_cff_encoding,
// parse_cff_gid_mapping, parse_cff_gid_mapping_with_pdf_encoding and the CFF
// INDEX / DICT / charset / encoding-table primitives they need.
//
// Simple CFF fonts have two possible byte → glyph sources and ISO 32000-1 §9.6.6 decides
// between them:
//
//  1. The PDF font dictionary's /Encoding is authoritative for simple fonts. It gives
//     byte → glyph *name*; the CFF Charset resolves name → SID → GID.
//     ParseCffGidMappingWithPdfEncoding implements this path.
//  2. The CFF font program's own Encoding table (CFF Tech Note #5176 §12) gives
//     byte → GID directly. This is the fallback when there is no PDF-level encoding.
//     ParseCffGidMapping implements this path.
//
// Subsetter-emitted CFF Encoding tables are frequently sparse — prepress subsetters commonly
// emit only 0x20 → space and 0x41 → A while the Charset enumerates the full subset — so
// callers holding the PDF /Encoding must go through the _with_pdf_encoding entrypoint.

using System.Text;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>
/// The PDF font dictionary's /Encoding as far as CFF byte → name resolution needs it
/// (<c>font_dict::Encoding</c>). Declared here because font_dict.rs is not part of this
/// port slice; the eventual font-dictionary port adapts onto this shape.
/// </summary>
internal readonly struct OxPdfEncoding
{
    internal enum Kind
    {
        /// <summary>A named base encoding (WinAnsiEncoding, MacRomanEncoding, …).</summary>
        Standard,
        /// <summary>/BaseEncoding + /Differences already merged to byte → Unicode.</summary>
        Custom,
        /// <summary>No byte → name mapping available (typically a CID font).</summary>
        Identity,
    }

    internal Kind Tag { get; }

    /// <summary>Encoding name, set only when <see cref="Tag"/> is <see cref="Kind.Standard"/>.</summary>
    internal string? Name { get; }

    /// <summary>Merged byte → Unicode map, set only when <see cref="Tag"/> is <see cref="Kind.Custom"/>.</summary>
    internal IReadOnlyDictionary<byte, Rune>? CustomMap { get; }

    private OxPdfEncoding(Kind tag, string? name, IReadOnlyDictionary<byte, Rune>? map)
    {
        Tag = tag;
        Name = name;
        CustomMap = map;
    }

    internal static OxPdfEncoding Standard(string name) => new(Kind.Standard, name, null);

    internal static OxPdfEncoding Custom(IReadOnlyDictionary<byte, Rune> map) => new(Kind.Custom, null, map);

    internal static OxPdfEncoding Identity => new(Kind.Identity, null, null);
}

internal static class OxCffEncoding
{
    /// <summary>
    /// Standard CFF string IDs (SIDs) 0-390 per the Adobe CFF specification, Appendix A.
    /// </summary>
    private static readonly string[] SidNames =
    {
        ".notdef", "space", "exclam", "quotedbl", // 0
        "numbersign", "dollar", "percent", "ampersand", // 4
        "quoteright", "parenleft", "parenright", "asterisk", // 8
        "plus", "comma", "hyphen", "period", // 12
        "slash", "zero", "one", "two", // 16
        "three", "four", "five", "six", // 20
        "seven", "eight", "nine", "colon", // 24
        "semicolon", "less", "equal", "greater", // 28
        "question", "at", "A", "B", // 32
        "C", "D", "E", "F", // 36
        "G", "H", "I", "J", // 40
        "K", "L", "M", "N", // 44
        "O", "P", "Q", "R", // 48
        "S", "T", "U", "V", // 52
        "W", "X", "Y", "Z", // 56
        "bracketleft", "backslash", "bracketright", "asciicircum", // 60
        "underscore", "quoteleft", "a", "b", // 64
        "c", "d", "e", "f", // 68
        "g", "h", "i", "j", // 72
        "k", "l", "m", "n", // 76
        "o", "p", "q", "r", // 80
        "s", "t", "u", "v", // 84
        "w", "x", "y", "z", // 88
        "braceleft", "bar", "braceright", "asciitilde", // 92
        "exclamdown", "cent", "sterling", "fraction", // 96
        "yen", "florin", "section", "currency", // 100
        "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft", // 104
        "guilsinglright", "fi", "fl", "endash", // 108
        "dagger", "daggerdbl", "periodcentered", "paragraph", // 112
        "bullet", "quotesinglbase", "quotedblbase", "quotedblright", // 116
        "guillemotright", "ellipsis", "perthousand", "questiondown", // 120
        "grave", "acute", "circumflex", "tilde", // 124
        "macron", "breve", "dotaccent", "dieresis", // 128
        "ring", "cedilla", "hungarumlaut", "ogonek", // 132
        "caron", "emdash", "AE", "ordfeminine", // 136
        "Lslash", "Oslash", "OE", "ordmasculine", // 140
        "ae", "dotlessi", "lslash", "oslash", // 144
        "oe", "germandbls", "onesuperior", "logicalnot", // 148
        "mu", "trademark", "Eth", "onehalf", // 152
        "plusminus", "Thorn", "onequarter", "divide", // 156
        "brokenbar", "degree", "thorn", "threequarters", // 160
        "twosuperior", "registered", "minus", "eth", // 164
        "multiply", "threesuperior", "copyright", "Aacute", // 168
        "Acircumflex", "Adieresis", "Agrave", "Aring", // 172
        "Atilde", "Ccedilla", "Eacute", "Ecircumflex", // 176
        "Edieresis", "Egrave", "Iacute", "Icircumflex", // 180
        "Idieresis", "Igrave", "Ntilde", "Oacute", // 184
        "Ocircumflex", "Odieresis", "Ograve", "Otilde", // 188
        "Scaron", "Uacute", "Ucircumflex", "Udieresis", // 192
        "Ugrave", "Yacute", "Ydieresis", "Zcaron", // 196
        "aacute", "acircumflex", "adieresis", "agrave", // 200
        "aring", "atilde", "ccedilla", "eacute", // 204
        "ecircumflex", "edieresis", "egrave", "iacute", // 208
        "icircumflex", "idieresis", "igrave", "ntilde", // 212
        "oacute", "ocircumflex", "odieresis", "ograve", // 216
        "otilde", "scaron", "uacute", "ucircumflex", // 220
        "udieresis", "ugrave", "yacute", "ydieresis", // 224
        "zcaron", "exclamsmall", "Hungarumlautsmall", "dollaroldstyle", // 228
        "dollarsuperior", "ampersandsmall", "Acutesmall", "parenleftsuperior", // 232
        "parenrightsuperior", "twodotenleader", "onedotenleader", "zerooldstyle", // 236
        "oneoldstyle", "twooldstyle", "threeoldstyle", "fouroldstyle", // 240
        "fiveoldstyle", "sixoldstyle", "sevenoldstyle", "eightoldstyle", // 244
        "nineoldstyle", "commasuperior", "threequartersemdash", "periodsuperior", // 248
        "questionsmall", "asuperior", "bsuperior", "centsuperior", // 252
        "dsuperior", "esuperior", "isuperior", "lsuperior", // 256
        "msuperior", "nsuperior", "osuperior", "rsuperior", // 260
        "ssuperior", "tsuperior", "ff", "ffi", // 264
        "ffl", "parenleftinferior", "parenrightinferior", "Circumflexsmall", // 268
        "hyphensuperior", "Gravesmall", "Asmall", "Bsmall", // 272
        "Csmall", "Dsmall", "Esmall", "Fsmall", // 276
        "Gsmall", "Hsmall", "Ismall", "Jsmall", // 280
        "Ksmall", "Lsmall", "Msmall", "Nsmall", // 284
        "Osmall", "Psmall", "Qsmall", "Rsmall", // 288
        "Ssmall", "Tsmall", "Usmall", "Vsmall", // 292
        "Wsmall", "Xsmall", "Ysmall", "Zsmall", // 296
        "colonmonetary", "onefitted", "rupiah", "Tildesmall", // 300
        "exclamdownsmall", "centoldstyle", "Lslashsmall", "Scaronsmall", // 304
        "Zcaronsmall", "Dieresissmall", "Brevesmall", "Caronsmall", // 308
        "Dotaccentsmall", "Macronsmall", "figuredash", "hypheninferior", // 312
        "Ogoneksmall", "Ringsmall", "Cedillasmall", "questiondownsmall", // 316
        "oneeighth", "threeeighths", "fiveeighths", "seveneighths", // 320
        "onethird", "twothirds", "zerosuperior", "foursuperior", // 324
        "fivesuperior", "sixsuperior", "sevensuperior", "eightsuperior", // 328
        "ninesuperior", "zeroinferior", "oneinferior", "twoinferior", // 332
        "threeinferior", "fourinferior", "fiveinferior", "sixinferior", // 336
        "seveninferior", "eightinferior", "nineinferior", "centinferior", // 340
        "dollarinferior", "periodinferior", "commainferior", "Agravesmall", // 344
        "Aacutesmall", "Acircumflexsmall", "Atildesmall", "Adieresissmall", // 348
        "Aringsmall", "AEsmall", "Ccedillasmall", "Egravesmall", // 352
        "Eacutesmall", "Ecircumflexsmall", "Edieresissmall", "Igravesmall", // 356
        "Iacutesmall", "Icircumflexsmall", "Idieresissmall", "Ethsmall", // 360
        "Ntildesmall", "Ogravesmall", "Oacutesmall", "Ocircumflexsmall", // 364
        "Otildesmall", "Odieresissmall", "OEsmall", "Oslashsmall", // 368
        "Ugravesmall", "Uacutesmall", "Ucircumflexsmall", "Udieresissmall", // 372
        "Yacutesmall", "Thornsmall", "Ydieresissmall", "001.000", // 376
        "001.001", "001.002", "001.003", "Black", // 380
        "Bold", "Book", "Light", "Medium", // 384
        "Regular", "Roman", "Semibold", // 388
    };

    internal static string? SidToName(ushort sid) => sid < SidNames.Length ? SidNames[sid] : null;

    /// <summary>Reverse of <see cref="SidToName"/> over the predefined SID table.</summary>
    internal static ushort? GlyphNameToSid(string name)
    {
        for (ushort sid = 0; sid < 391; sid++)
        {
            if (SidToName(sid) == name)
            {
                return sid;
            }
        }
        return null;
    }

    /// <summary>A half-open [Start, End) byte range inside the CFF data.</summary>
    private readonly record struct CffRange(int Start, int End);

    /// <summary>Parse a CFF INDEX, yielding one range per entry plus the offset just past it.</summary>
    private static bool TryParseIndex(
        ReadOnlySpan<byte> data,
        int offset,
        out List<CffRange> entries,
        out int nextOffset)
    {
        entries = new List<CffRange>();
        nextOffset = 0;

        if (offset < 0 || offset + 2 > data.Length)
        {
            return false;
        }
        int count = (data[offset] << 8) | data[offset + 1];
        if (count == 0)
        {
            nextOffset = offset + 2;
            return true;
        }

        if (offset + 3 > data.Length)
        {
            return false;
        }
        int offSize = data[offset + 2];
        if (offSize == 0 || offSize > 4)
        {
            return false;
        }

        int offsetArrayStart = offset + 3;
        long offsetArrayLen = (long)(count + 1) * offSize;
        if (offsetArrayStart + offsetArrayLen > data.Length)
        {
            return false;
        }

        var offsets = new long[count + 1];
        for (int i = 0; i <= count; i++)
        {
            int p = offsetArrayStart + i * offSize;
            long val = 0;
            for (int j = 0; j < offSize; j++)
            {
                val = (val << 8) | data[p + j];
            }
            offsets[i] = val;
        }

        long dataStart = offsetArrayStart + offsetArrayLen;
        entries.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            // CFF INDEX offsets are 1-based.
            long start = dataStart + offsets[i] - 1;
            long end = dataStart + offsets[i + 1] - 1;
            if (start > data.Length || end > data.Length || start > end || start < 0)
            {
                entries.Clear();
                return false;
            }
            entries.Add(new CffRange((int)start, (int)end));
        }

        long next = dataStart + offsets[count] - 1;
        if (next < 0 || next > int.MaxValue)
        {
            entries.Clear();
            return false;
        }
        nextOffset = (int)next;
        return true;
    }

    /// <summary>Parse one CFF DICT operand (integer or real), returning value and size.</summary>
    private static bool TryParseDictOperand(ReadOnlySpan<byte> data, int pos, out int value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (pos >= data.Length)
        {
            return false;
        }
        int b0 = data[pos];
        switch (b0)
        {
            case >= 32 and <= 246:
                value = b0 - 139;
                consumed = 1;
                return true;
            case >= 247 and <= 250:
                if (pos + 1 >= data.Length)
                {
                    return false;
                }
                value = (b0 - 247) * 256 + data[pos + 1] + 108;
                consumed = 2;
                return true;
            case >= 251 and <= 254:
                if (pos + 1 >= data.Length)
                {
                    return false;
                }
                value = -(b0 - 251) * 256 - data[pos + 1] - 108;
                consumed = 2;
                return true;
            case 28:
                if (pos + 2 >= data.Length)
                {
                    return false;
                }
                value = (short)((data[pos + 1] << 8) | data[pos + 2]);
                consumed = 3;
                return true;
            case 29:
                if (pos + 4 >= data.Length)
                {
                    return false;
                }
                value = (data[pos + 1] << 24) | (data[pos + 2] << 16) | (data[pos + 3] << 8) | data[pos + 4];
                consumed = 5;
                return true;
            case 30:
            {
                // Real number: only its length matters here — encoding and charset offsets
                // are always integers.
                int i = pos + 1;
                while (i < data.Length)
                {
                    int nibble1 = (data[i] >> 4) & 0x0F;
                    int nibble2 = data[i] & 0x0F;
                    if (nibble1 == 0x0F || nibble2 == 0x0F)
                    {
                        value = 0;
                        consumed = i - pos + 1;
                        return true;
                    }
                    i++;
                }
                return false;
            }
            default:
                return false;
        }
    }

    /// <summary>Parse a CFF Top DICT for the Encoding (op 16) and charset (op 15) offsets.</summary>
    private static (int EncodingOffset, int CharsetOffset) ParseTopDict(ReadOnlySpan<byte> dictData)
    {
        int encodingOffset = 0; // Default: StandardEncoding
        int charsetOffset = 0;  // Default: ISOAdobe charset

        int pos = 0;
        var operandStack = new List<int>();

        while (pos < dictData.Length)
        {
            byte b0 = dictData[pos];
            if (b0 <= 21)
            {
                ushort op;
                if (b0 == 12)
                {
                    pos++;
                    if (pos >= dictData.Length)
                    {
                        break;
                    }
                    op = (ushort)((12 << 8) | dictData[pos]);
                }
                else
                {
                    op = b0;
                }

                if (operandStack.Count > 0)
                {
                    int last = operandStack[^1];
                    if (op == 16)
                    {
                        encodingOffset = last;
                    }
                    else if (op == 15)
                    {
                        charsetOffset = last;
                    }
                }

                operandStack.Clear();
                pos++;
            }
            else if (TryParseDictOperand(dictData, pos, out int val, out int consumed))
            {
                operandStack.Add(val);
                pos += consumed;
            }
            else
            {
                pos++;
            }
        }

        return (encodingOffset, charsetOffset);
    }

    /// <summary>
    /// Like <see cref="ParseTopDict"/> but also surfaces the CharStrings offset (op 17).
    /// The CharStrings INDEX count field is the real nGlyphs, needed to parse the full
    /// Charset for subsets enumerating more than 256 glyphs.
    /// </summary>
    private static (int CharStringsOffset, int EncodingOffset, int CharsetOffset)
        ParseTopDictWithCharStrings(ReadOnlySpan<byte> dictData)
    {
        int charStringsOffset = 0;
        int encodingOffset = 0;
        int charsetOffset = 0;

        int pos = 0;
        var operandStack = new List<int>();

        while (pos < dictData.Length)
        {
            byte b0 = dictData[pos];
            if (b0 <= 21)
            {
                ushort op;
                if (b0 == 12)
                {
                    pos++;
                    if (pos >= dictData.Length)
                    {
                        break;
                    }
                    op = (ushort)((12 << 8) | dictData[pos]);
                }
                else
                {
                    op = b0;
                }

                if (operandStack.Count > 0)
                {
                    int last = operandStack[^1];
                    switch (op)
                    {
                        case 15: charsetOffset = last; break;
                        case 16: encodingOffset = last; break;
                        case 17: charStringsOffset = last; break;
                    }
                }

                operandStack.Clear();
                pos++;
            }
            else if (TryParseDictOperand(dictData, pos, out int val, out int consumed))
            {
                operandStack.Add(val);
                pos += consumed;
            }
            else
            {
                pos++;
            }
        }

        return (charStringsOffset, encodingOffset, charsetOffset);
    }

    /// <summary>
    /// Read the 2-byte big-endian count at the start of a CFF INDEX header (CFF spec §5).
    /// For the CharStrings INDEX this count is nGlyphs.
    /// </summary>
    private static uint? ReadIndexCount(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            return null;
        }
        return (uint)((data[offset] << 8) | data[offset + 1]);
    }

    /// <summary>Parse the CFF charset table into a GID → SID list (GID 0 is always .notdef).</summary>
    private static List<ushort>? ParseCharset(ReadOnlySpan<byte> data, int offset, int numGlyphs)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return null;
        }

        var sids = new List<ushort>(numGlyphs) { 0 };

        byte format = data[offset];
        int pos = offset + 1;

        switch (format)
        {
            case 0:
                // Format 0: a flat array of SIDs.
                for (int i = 1; i < numGlyphs; i++)
                {
                    if (pos + 1 >= data.Length)
                    {
                        break;
                    }
                    sids.Add((ushort)((data[pos] << 8) | data[pos + 1]));
                    pos += 2;
                }
                break;

            case 1:
                // Format 1: ranges with a 1-byte nLeft.
                while (sids.Count < numGlyphs && pos + 2 < data.Length)
                {
                    ushort firstSid = (ushort)((data[pos] << 8) | data[pos + 1]);
                    ushort nLeft = data[pos + 2];
                    pos += 3;
                    for (int i = 0; i <= nLeft; i++)
                    {
                        if (sids.Count >= numGlyphs)
                        {
                            break;
                        }
                        sids.Add((ushort)(firstSid + i));
                    }
                }
                break;

            case 2:
                // Format 2: ranges with a 2-byte nLeft.
                while (sids.Count < numGlyphs && pos + 3 < data.Length)
                {
                    ushort firstSid = (ushort)((data[pos] << 8) | data[pos + 1]);
                    int nLeft = (data[pos + 2] << 8) | data[pos + 3];
                    pos += 4;
                    for (int i = 0; i <= nLeft; i++)
                    {
                        if (sids.Count >= numGlyphs)
                        {
                            break;
                        }
                        sids.Add((ushort)(firstSid + i));
                    }
                }
                break;

            default:
                return null;
        }

        return sids;
    }

    /// <summary>Parse the CFF Encoding table into a character code → GID map.</summary>
    private static Dictionary<byte, ushort>? ParseEncodingTable(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return null;
        }

        var codeToGid = new Dictionary<byte, ushort>();
        int format = data[offset] & 0x7F; // Bit 7 is the supplement flag
        bool hasSupplement = (data[offset] & 0x80) != 0;
        int pos = offset + 1;

        switch (format)
        {
            case 0:
            {
                if (pos >= data.Length)
                {
                    return null;
                }
                int nCodes = data[pos];
                pos++;
                for (int gid = 1; gid <= nCodes; gid++)
                {
                    if (pos >= data.Length)
                    {
                        break;
                    }
                    codeToGid[data[pos]] = (ushort)gid;
                    pos++;
                }
                break;
            }

            case 1:
            {
                if (pos >= data.Length)
                {
                    return null;
                }
                int nRanges = data[pos];
                pos++;
                ushort gid = 1;
                for (int r = 0; r < nRanges; r++)
                {
                    if (pos + 1 >= data.Length)
                    {
                        break;
                    }
                    byte first = data[pos];
                    int nLeft = data[pos + 1];
                    pos += 2;
                    for (int i = 0; i <= nLeft; i++)
                    {
                        codeToGid[(byte)(first + (byte)i)] = gid;
                        gid++;
                    }
                }
                break;
            }

            default:
                return null;
        }

        if (hasSupplement && pos < data.Length)
        {
            int nSups = data[pos];
            pos++;
            for (int i = 0; i < nSups; i++)
            {
                if (pos + 2 >= data.Length)
                {
                    break;
                }
                byte code = data[pos];
                ushort sid = (ushort)((data[pos + 1] << 8) | data[pos + 2]);
                pos += 3;
                // Supplements carry a SID, not a GID; it is stored as a pseudo-GID and the
                // caller resolves it through the charset.
                codeToGid[code] = sid;
            }
        }

        return codeToGid;
    }

    /// <summary>Resolve a glyph name from a SID via the predefined strings or the String INDEX.</summary>
    private static string? ResolveGlyphName(ushort sid, ReadOnlySpan<byte> cffData, List<CffRange> stringIndex)
    {
        if (sid <= 390)
        {
            return SidToName(sid);
        }
        int idx = sid - 391;
        if (idx < stringIndex.Count)
        {
            CffRange r = stringIndex[idx];
            try
            {
                return Encoding.UTF8.GetString(cffData[r.Start..r.End]);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Predefined charset 1 (Expert), Adobe Technical Note #5176 Appendix C: GID &#8594; SID.
    /// </summary>
    private static readonly ushort[] ExpertCharset =
    {
        0, 1, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 13, 14, 15, 99,
        239, 240, 241, 242, 243, 244, 245, 246, 247, 248, 27, 28, 249, 250, 251, 252,
        253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266, 109, 110,
        267, 268, 269, 270, 271, 272, 273, 274, 275, 276, 277, 278, 279, 280, 281, 282,
        283, 284, 285, 286, 287, 288, 289, 290, 291, 292, 293, 294, 295, 296, 297, 298,
        299, 300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312, 313, 314,
        315, 316, 317, 318, 158, 155, 163, 319, 320, 321, 322, 323, 324, 325, 326, 150,
        164, 169, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339, 340,
        341, 342, 343, 344, 345, 346, 347, 348, 349, 350, 351, 352, 353, 354, 355, 356,
        357, 358, 359, 360, 361, 362, 363, 364, 365, 366, 367, 368, 369, 370, 371, 372,
        373, 374, 375, 376, 377, 378,
    };

    /// <summary>Predefined charset 2 (Expert Subset), Adobe Technical Note #5176 Appendix C.</summary>
    private static readonly ushort[] ExpertSubsetCharset =
    {
        0, 1, 231, 232, 235, 236, 237, 238, 13, 14, 15, 99, 239, 240, 241, 242,
        243, 244, 245, 246, 247, 248, 27, 28, 249, 250, 251, 253, 254, 255, 256, 257,
        258, 259, 260, 261, 262, 263, 264, 265, 266, 109, 110, 267, 268, 269, 270, 272,
        300, 301, 302, 305, 314, 315, 158, 155, 163, 320, 321, 322, 323, 324, 325, 326,
        150, 164, 169, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339,
        340, 341, 342, 343, 344, 345, 346,
    };

    /// <summary>
    /// The font program's own glyph names indexed by GID, read from the CFF charset.
    ///
    /// This is the CFF half of the embedded-glyph-name lookup that §9.10.2 Priority 3c and the
    /// Item 1 punctuation recovery both consult; the `post` half lives in
    /// <see cref="OxEmbeddedGlyphNames"/>. Returns null when the data holds no parseable CFF
    /// and for CID-keyed CFFs, whose charset enumerates CIDs rather than string IDs.
    /// </summary>
    internal static string?[]? GlyphNamesByGid(ReadOnlySpan<byte> fontData)
    {
        if (fontData.Length < 4)
        {
            return null;
        }

        ReadOnlySpan<byte> cff = fontData[0] == 1 ? fontData : ExtractCffFromOpenType(fontData);
        if (cff.Length < 4 || cff[0] != 1)
        {
            return null;
        }

        int hdrSize = cff[2];
        if (!TryParseIndex(cff, hdrSize, out _, out int afterName))
        {
            return null;
        }
        if (!TryParseIndex(cff, afterName, out List<CffRange> topDicts, out int afterTopDict) ||
            topDicts.Count == 0)
        {
            return null;
        }
        if (!TryParseIndex(cff, afterTopDict, out List<CffRange> stringIndex, out _))
        {
            return null;
        }

        CffRange td = topDicts[0];
        (int charStringsOffset, int charsetOffset, bool hasRos) =
            ParseTopDictForCharset(cff[td.Start..td.End]);

        // A CID-keyed font's charset holds CIDs, so it names no glyphs.
        if (hasRos || charStringsOffset == 0)
        {
            return null;
        }

        // "The number of glyphs is the value of the count field in the CharStrings INDEX."
        if (ReadIndexCount(cff, charStringsOffset) is not { } count || count == 0 || count > ushort.MaxValue)
        {
            return null;
        }

        int[]? sids = CharsetGidToSid(cff, charsetOffset, (int)count);
        if (sids is null)
        {
            return null;
        }

        var names = new string?[sids.Length];
        for (int gid = 0; gid < sids.Length; gid++)
        {
            if (sids[gid] >= 0)
            {
                names[gid] = ResolveGlyphName((ushort)sids[gid], cff, stringIndex);
            }
        }
        return names;
    }

    /// <summary>
    /// Top DICT scan for the charset lookup: the CharStrings and charset offsets plus whether
    /// the font declares a /ROS, which marks it CID-keyed.
    /// </summary>
    private static (int CharStringsOffset, int CharsetOffset, bool HasRos)
        ParseTopDictForCharset(ReadOnlySpan<byte> dictData)
    {
        int charStringsOffset = 0;
        int charsetOffset = 0;
        bool hasRos = false;

        int pos = 0;
        var operandStack = new List<int>();

        while (pos < dictData.Length)
        {
            byte b0 = dictData[pos];
            if (b0 <= 21)
            {
                ushort op;
                if (b0 == 12)
                {
                    pos++;
                    if (pos >= dictData.Length)
                    {
                        break;
                    }
                    op = (ushort)((12 << 8) | dictData[pos]);
                }
                else
                {
                    op = b0;
                }

                if (op == ((12 << 8) | 30))
                {
                    hasRos = true;
                }
                else if (operandStack.Count > 0)
                {
                    int last = operandStack[^1];
                    switch (op)
                    {
                        case 15: charsetOffset = last; break;
                        case 17: charStringsOffset = last; break;
                    }
                }

                operandStack.Clear();
                pos++;
            }
            else if (TryParseDictOperand(dictData, pos, out int val, out int consumed))
            {
                operandStack.Add(val);
                pos += consumed;
            }
            else
            {
                pos++;
            }
        }

        return (charStringsOffset, charsetOffset, hasRos);
    }

    /// <summary>
    /// GID &#8594; SID for the whole font, with -1 where the charset names no glyph. Offsets
    /// 0/1/2 select the predefined ISOAdobe / Expert / Expert Subset charsets; anything else
    /// is a charset table at that offset. Returns null when the table is malformed or runs
    /// short of <paramref name="numGlyphs"/> entries, which is what makes the whole CFF
    /// unreadable rather than partially named.
    /// </summary>
    private static int[]? CharsetGidToSid(ReadOnlySpan<byte> data, int charsetOffset, int numGlyphs)
    {
        var sids = new int[numGlyphs];

        switch (charsetOffset)
        {
            case 0: // ISOAdobe: the first 229 SIDs in order.
                for (int gid = 0; gid < numGlyphs; gid++)
                {
                    sids[gid] = gid <= 228 ? gid : -1;
                }
                return sids;

            case 1:
                return FromPredefinedCharset(ExpertCharset, sids);

            case 2:
                return FromPredefinedCharset(ExpertSubsetCharset, sids);
        }

        if (charsetOffset < 0 || charsetOffset >= data.Length)
        {
            return null;
        }

        byte format = data[charsetOffset];
        int pos = charsetOffset + 1;
        // .notdef is never listed; it is GID 0 and SID 0 by definition.
        int remaining = numGlyphs - 1;
        int next = 1;

        switch (format)
        {
            case 0:
                if (pos + (2L * remaining) > data.Length)
                {
                    return null;
                }
                for (int i = 0; i < remaining; i++)
                {
                    sids[next++] = (data[pos] << 8) | data[pos + 1];
                    pos += 2;
                }
                return sids;

            case 1:
            case 2:
            {
                int leftSize = format == 1 ? 1 : 2;
                while (remaining > 0)
                {
                    if (pos + 2 + leftSize > data.Length)
                    {
                        return null;
                    }
                    int first = (data[pos] << 8) | data[pos + 1];
                    int left = leftSize == 1 ? data[pos + 2] : (data[pos + 2] << 8) | data[pos + 3];
                    pos += 2 + leftSize;

                    int run = left + 1;
                    if (run > remaining)
                    {
                        // The ranges must tile the glyph count exactly; a run past the end
                        // means the table does not describe this font.
                        return null;
                    }
                    for (int i = 0; i < run; i++)
                    {
                        int sid = first + i;
                        sids[next++] = sid <= ushort.MaxValue ? sid : -1;
                    }
                    remaining -= run;
                }
                return sids;
            }

            default:
                return null;
        }
    }

    private static int[] FromPredefinedCharset(ushort[] charset, int[] sids)
    {
        for (int gid = 0; gid < sids.Length; gid++)
        {
            sids[gid] = gid < charset.Length ? charset[gid] : -1;
        }
        return sids;
    }

    /// <summary>
    /// Extract the "CFF " table from an OpenType (sfnt) wrapper, or an empty span if the
    /// data is not an sfnt container.
    /// </summary>
    private static ReadOnlySpan<byte> ExtractCffFromOpenType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return default;
        }
        uint magic = ReadU32(data, 0);
        // "OTTO" or the TrueType 0x00010000 version tag.
        if (magic != 0x4F54544F && magic != 0x00010000)
        {
            return default;
        }
        int numTables = (data[4] << 8) | data[5];
        int pos = 12; // Table directory starts at offset 12
        for (int i = 0; i < numTables; i++)
        {
            if (pos + 16 > data.Length)
            {
                return default;
            }
            uint tag = ReadU32(data, pos);
            long offset = ReadU32(data, pos + 8);
            long length = ReadU32(data, pos + 12);
            // "CFF " = 0x43464620
            if (tag == 0x43464620 && offset + length <= data.Length)
            {
                return data.Slice((int)offset, (int)length);
            }
            pos += 16;
        }
        return default;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int pos) =>
        ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];

    /// <summary>
    /// <c>parse_cff_encoding</c> — extract the built-in encoding from a CFF font program as
    /// code → Unicode, running the CFF encoding → charset → glyph name → Unicode pipeline.
    /// Also accepts OpenType-wrapped CFF (FontFile3 with an sfnt container).
    /// </summary>
    internal static Dictionary<byte, Rune>? ParseCffEncoding(ReadOnlySpan<byte> fontData)
    {
        if (fontData.Length < 4)
        {
            return null;
        }

        ReadOnlySpan<byte> cffData;
        if (fontData[0] != 1)
        {
            cffData = ExtractCffFromOpenType(fontData);
            if (cffData.IsEmpty)
            {
                return null;
            }
        }
        else
        {
            cffData = fontData;
        }

        if (cffData.Length < 4 || cffData[0] != 1)
        {
            return null;
        }
        int hdrSize = cffData[2];

        if (!TryParseIndex(cffData, hdrSize, out _, out int afterName))
        {
            return null;
        }
        if (!TryParseIndex(cffData, afterName, out List<CffRange> topDicts, out int afterTopDict) ||
            topDicts.Count == 0)
        {
            return null;
        }
        if (!TryParseIndex(cffData, afterTopDict, out List<CffRange> stringIndex, out _))
        {
            return null;
        }

        CffRange td = topDicts[0];
        (int encodingOffset, int charsetOffset) = ParseTopDict(cffData[td.Start..td.End]);

        if (encodingOffset == 1)
        {
            // ExpertEncoding — rarely used for text.
            return null;
        }

        if (encodingOffset == 0)
        {
            // StandardEncoding with a custom charset: subset fonts in the wild use character
            // codes that equal GIDs rather than standard encoding positions, so build a
            // GID-keyed fallback map from the charset.
            if (charsetOffset > 2)
            {
                const int numGlyphs = 256;
                List<ushort>? charsetSids = ParseCharset(cffData, charsetOffset, numGlyphs);
                if (charsetSids is null)
                {
                    return null;
                }

                var fallback = new Dictionary<byte, Rune>();
                for (int gid = 0; gid < charsetSids.Count; gid++)
                {
                    if (gid == 0 || gid > 255)
                    {
                        continue;
                    }
                    string? glyphName = ResolveGlyphName(charsetSids[gid], cffData, stringIndex);
                    if (glyphName is null)
                    {
                        continue;
                    }
                    Rune? unicodeChar = OxGlyphNames.GlyphNameToUnicode(glyphName);
                    if (unicodeChar is not null)
                    {
                        fallback[(byte)gid] = unicodeChar.Value;
                    }
                }
                if (fallback.Count > 0)
                {
                    return fallback;
                }
            }
            return null;
        }

        // Custom encoding (encoding_offset > 1).
        Dictionary<byte, ushort>? codeToGid = ParseEncodingTable(cffData, encodingOffset);
        if (codeToGid is null)
        {
            return null;
        }

        int maxGid = 0;
        foreach (ushort g in codeToGid.Values)
        {
            if (g > maxGid)
            {
                maxGid = g;
            }
        }
        int numGlyphsForCharset = maxGid + 10;

        List<ushort> sids;
        if (charsetOffset == 0)
        {
            sids = new List<ushort>(numGlyphsForCharset);
            for (int i = 0; i < numGlyphsForCharset; i++)
            {
                sids.Add((ushort)i);
            }
        }
        else if (charsetOffset == 1 || charsetOffset == 2)
        {
            // Predefined Expert / ExpertSubset charsets.
            return null;
        }
        else
        {
            List<ushort>? parsed = ParseCharset(cffData, charsetOffset, numGlyphsForCharset);
            if (parsed is null)
            {
                return null;
            }
            sids = parsed;
        }

        var encodingMap = new Dictionary<byte, Rune>();
        foreach (KeyValuePair<byte, ushort> kv in codeToGid)
        {
            if (kv.Value >= sids.Count)
            {
                continue;
            }
            ushort sid = sids[kv.Value];
            string? glyphName = ResolveGlyphName(sid, cffData, stringIndex);
            if (glyphName is null)
            {
                continue;
            }
            Rune? unicodeChar = OxGlyphNames.GlyphNameToUnicode(glyphName);
            if (unicodeChar is not null)
            {
                encodingMap[kv.Key] = unicodeChar.Value;
            }
        }

        return encodingMap.Count == 0 ? null : encodingMap;
    }

    /// <summary>
    /// <c>parse_cff_gid_mapping</c> — byte code → glyph ID driven by the CFF font program's
    /// own Encoding table, so CFF subset fonts render without a Unicode cmap.
    /// </summary>
    internal static Dictionary<byte, ushort>? ParseCffGidMapping(ReadOnlySpan<byte> fontData)
    {
        if (fontData.Length < 4)
        {
            return null;
        }

        ReadOnlySpan<byte> cffData;
        if (fontData[0] != 1)
        {
            cffData = ExtractCffFromOpenType(fontData);
            if (cffData.IsEmpty)
            {
                return null;
            }
        }
        else
        {
            cffData = fontData;
        }

        if (cffData.Length < 4 || cffData[0] != 1)
        {
            return null;
        }
        int hdrSize = cffData[2];

        if (!TryParseIndex(cffData, hdrSize, out _, out int afterName))
        {
            return null;
        }
        if (!TryParseIndex(cffData, afterName, out List<CffRange> topDicts, out int afterTopDict) ||
            topDicts.Count == 0)
        {
            return null;
        }
        if (!TryParseIndex(cffData, afterTopDict, out _, out _))
        {
            return null;
        }

        CffRange td = topDicts[0];
        (int encodingOffset, int charsetOffset) = ParseTopDict(cffData[td.Start..td.End]);

        if (encodingOffset == 0 && charsetOffset > 2)
        {
            // StandardEncoding + custom charset: byte → SID (CFF Standard Encoding) →
            // GID (charset).
            const int numGlyphs = 256;
            List<ushort>? charsetSids = ParseCharset(cffData, charsetOffset, numGlyphs);
            if (charsetSids is not null)
            {
                var sidToGid = new Dictionary<ushort, ushort>();
                for (int gid = 0; gid < charsetSids.Count; gid++)
                {
                    if (gid > 0)
                    {
                        sidToGid.TryAdd(charsetSids[gid], (ushort)gid);
                    }
                }

                var map = new Dictionary<byte, ushort>();
                for (int byteCode = 0; byteCode < 256; byteCode++)
                {
                    string? glyphName = OxEncodingTables.GidToStandardGlyphName((ushort)byteCode);
                    if (glyphName is null)
                    {
                        continue;
                    }
                    ushort? sid = GlyphNameToSid(glyphName);
                    if (sid is null)
                    {
                        continue;
                    }
                    if (sidToGid.TryGetValue(sid.Value, out ushort gid))
                    {
                        map[(byte)byteCode] = gid;
                    }
                }
                if (map.Count > 0)
                {
                    return map;
                }
            }
            return null;
        }

        if (encodingOffset <= 1)
        {
            return null;
        }

        return ParseEncodingTable(cffData, encodingOffset);
    }

    /// <summary>
    /// <c>parse_cff_gid_mapping_with_pdf_encoding</c> — build byte → GID for a simple CFF font
    /// using the PDF /Encoding as the byte → glyph-name source and the CFF Charset as the
    /// glyph-name → GID resolver, per ISO 32000-1 §9.6.6.
    ///
    /// This is the correct model for simple Type 1 / TrueType / CFF fonts. The bug it fixes is
    /// prepress-authored subset CFFs whose internal Encoding lists only 0x20 → space and
    /// 0x41 → A while the Charset enumerates the full subset: resolving through the CFF
    /// Encoding dropped every other content byte to .notdef, producing bare-A glyphs on every
    /// separation plate. Returns the legacy path's result when this one resolves nothing, so a
    /// working font is never made worse.
    /// </summary>
    internal static Dictionary<byte, ushort>? ParseCffGidMappingWithPdfEncoding(
        ReadOnlySpan<byte> fontData,
        OxPdfEncoding pdfEncoding,
        IReadOnlyDictionary<byte, string> differences)
    {
        if (pdfEncoding.Tag == OxPdfEncoding.Kind.Identity)
        {
            // No byte → name mapping to supply; fall through to the CFF Encoding path.
            return ParseCffGidMapping(fontData);
        }

        if (fontData.Length < 4)
        {
            return null;
        }

        ReadOnlySpan<byte> cffData;
        if (fontData[0] != 1)
        {
            cffData = ExtractCffFromOpenType(fontData);
            if (cffData.IsEmpty)
            {
                return null;
            }
        }
        else
        {
            cffData = fontData;
        }

        if (cffData.Length < 4 || cffData[0] != 1)
        {
            return null;
        }
        int hdrSize = cffData[2];

        if (!TryParseIndex(cffData, hdrSize, out _, out int afterName))
        {
            return null;
        }
        if (!TryParseIndex(cffData, afterName, out List<CffRange> topDicts, out int afterTopDict) ||
            topDicts.Count == 0)
        {
            return null;
        }
        if (!TryParseIndex(cffData, afterTopDict, out List<CffRange> stringIndex, out _))
        {
            return null;
        }

        CffRange td = topDicts[0];
        (int charStringsOffset, _, int charsetOffset) = ParseTopDictWithCharStrings(cffData[td.Start..td.End]);

        // nGlyphs comes from the CharStrings INDEX header (CFF spec §9). Simple fonts address
        // at most 256 codes, but a subset's GID space can exceed 256 and a /Differences entry
        // pointing at a GID >256 must still resolve.
        int numGlyphs = 256;
        if (charStringsOffset > 0)
        {
            uint? count = ReadIndexCount(cffData, charStringsOffset);
            if (count is not null)
            {
                numGlyphs = (int)count.Value;
            }
        }

        List<ushort>? charsetSids;
        if (charsetOffset > 2)
        {
            charsetSids = ParseCharset(cffData, charsetOffset, numGlyphs);
            if (charsetSids is null)
            {
                return null;
            }
        }
        else
        {
            // charset offset 0/1/2 are the predefined ISOAdobe / Expert / ExpertSubset
            // charsets; the CFF Standard Encoding + charset path handles those.
            return ParseCffGidMapping(fontData);
        }

        Dictionary<byte, ushort> resolved =
            ResolveBytesViaPdfEncoding(charsetSids, cffData, stringIndex, pdfEncoding, differences);

        if (resolved.Count == 0)
        {
            // The PDF /Encoding scored zero hits against the Charset.
            return ParseCffGidMapping(fontData);
        }
        return resolved;
    }

    /// <summary>
    /// <c>resolve_bytes_via_pdf_encoding</c> — given a parsed Charset and String INDEX, build
    /// byte → GID from the PDF font dictionary's /Encoding and /Differences.
    /// </summary>
    private static Dictionary<byte, ushort> ResolveBytesViaPdfEncoding(
        List<ushort> charsetSids,
        ReadOnlySpan<byte> cffData,
        List<CffRange> stringIndex,
        OxPdfEncoding pdfEncoding,
        IReadOnlyDictionary<byte, string> differences)
    {
        // Glyph name → GID. The lowest GID wins on duplicate names: the first Charset
        // occurrence reflects the subsetter's primary mapping.
        var nameToGid = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int gid = 0; gid < charsetSids.Count; gid++)
        {
            if (gid == 0)
            {
                continue; // .notdef is implicit and not addressable by name
            }
            string? name = ResolveGlyphName(charsetSids[gid], cffData, stringIndex);
            if (name is not null)
            {
                nameToGid.TryAdd(name, (ushort)gid);
            }
        }

        var outMap = new Dictionary<byte, ushort>();
        for (int byteCode = 0; byteCode < 256; byteCode++)
        {
            byte b = (byte)byteCode;

            // §9.6.6: /Differences entries override the base predefined encoding.
            if (differences.TryGetValue(b, out string? diffName) &&
                nameToGid.TryGetValue(diffName, out ushort diffGid))
            {
                outMap[b] = diffGid;
                continue;
            }

            string? name = ResolveBaseByte(pdfEncoding, b);
            if (name is not null && nameToGid.TryGetValue(name, out ushort gid))
            {
                outMap[b] = gid;
            }
        }
        return outMap;
    }

    /// <summary>
    /// Base byte → glyph-name resolver per ISO 32000-1 §9.6.6.1 + Annex D. WinAnsi, MacRoman
    /// and StandardEncoding share ASCII names but diverge above 0x7F, so picking the wrong
    /// table mis-resolves high bytes for non-WinAnsi /BaseEncoding fonts.
    ///
    /// A Custom encoding carries the merged /BaseEncoding + /Differences byte → Unicode result
    /// but loses the named base, so its high bytes default to WinAnsi for compatibility.
    /// </summary>
    private static string? ResolveBaseByte(OxPdfEncoding pdfEncoding, byte b) => pdfEncoding.Tag switch
    {
        OxPdfEncoding.Kind.Standard => pdfEncoding.Name switch
        {
            "MacRomanEncoding" => MacRomanByteToName(b),
            "StandardEncoding" => StandardEncodingByteToName(b),
            // WinAnsiEncoding, MacExpertEncoding, PDFDocEncoding and anything unrecognised
            // fall through to WinAnsi: Mac Expert is a non-text variant and PDFDoc overlaps
            // WinAnsi.
            _ => OxEncodingTables.GidToStandardGlyphName(b),
        },
        OxPdfEncoding.Kind.Custom => OxEncodingTables.GidToStandardGlyphName(b),
        _ => null, // Identity is handled by the outer guard
    };

    /// <summary>
    /// Annex D Table D.2 (MacRomanEncoding) byte → glyph name. 0x20-0x7E shares glyph names
    /// with WinAnsi; 0x80-0xFF is the Mac OS Roman repertoire, which is not ISO-8859-1. The
    /// Apple-logo glyph at 0xF0 (PUA U+F8FF) has no portable glyph name.
    /// </summary>
    internal static string? MacRomanByteToName(byte b)
    {
        if (b >= 0x20 && b <= 0x7E)
        {
            return OxEncodingTables.GidToStandardGlyphName(b);
        }
        return b switch
        {
        0x80 => "Adieresis",
        0x81 => "Aring",
        0x82 => "Ccedilla",
        0x83 => "Eacute",
        0x84 => "Ntilde",
        0x85 => "Odieresis",
        0x86 => "Udieresis",
        0x87 => "aacute",
        0x88 => "agrave",
        0x89 => "acircumflex",
        0x8A => "adieresis",
        0x8B => "atilde",
        0x8C => "aring",
        0x8D => "ccedilla",
        0x8E => "eacute",
        0x8F => "egrave",
        0x90 => "ecircumflex",
        0x91 => "edieresis",
        0x92 => "iacute",
        0x93 => "igrave",
        0x94 => "icircumflex",
        0x95 => "idieresis",
        0x96 => "ntilde",
        0x97 => "oacute",
        0x98 => "ograve",
        0x99 => "ocircumflex",
        0x9A => "odieresis",
        0x9B => "otilde",
        0x9C => "uacute",
        0x9D => "ugrave",
        0x9E => "ucircumflex",
        0x9F => "udieresis",
        0xA0 => "dagger",
        0xA1 => "degree",
        0xA2 => "cent",
        0xA3 => "sterling",
        0xA4 => "section",
        0xA5 => "bullet",
        0xA6 => "paragraph",
        0xA7 => "germandbls",
        0xA8 => "registered",
        0xA9 => "copyright",
        0xAA => "trademark",
        0xAB => "acute",
        0xAC => "dieresis",
        0xAD => "notequal",
        0xAE => "AE",
        0xAF => "Oslash",
        0xB0 => "infinity",
        0xB1 => "plusminus",
        0xB2 => "lessequal",
        0xB3 => "greaterequal",
        0xB4 => "yen",
        0xB5 => "mu",
        0xB6 => "partialdiff",
        0xB7 => "summation",
        0xB8 => "product",
        0xB9 => "pi",
        0xBA => "integral",
        0xBB => "ordfeminine",
        0xBC => "ordmasculine",
        0xBD => "Omega",
        0xBE => "ae",
        0xBF => "oslash",
        0xC0 => "questiondown",
        0xC1 => "exclamdown",
        0xC2 => "logicalnot",
        0xC3 => "radical",
        0xC4 => "florin",
        0xC5 => "approxequal",
        0xC6 => "Delta",
        0xC7 => "guillemotleft",
        0xC8 => "guillemotright",
        0xC9 => "ellipsis",
        0xCA => "space", // nonbreakingspace; the canonical glyph name is "space"
        0xCB => "Agrave",
        0xCC => "Atilde",
        0xCD => "Otilde",
        0xCE => "OE",
        0xCF => "oe",
        0xD0 => "endash",
        0xD1 => "emdash",
        0xD2 => "quotedblleft",
        0xD3 => "quotedblright",
        0xD4 => "quoteleft",
        0xD5 => "quoteright",
        0xD6 => "divide",
        0xD7 => "lozenge",
        0xD8 => "ydieresis",
        0xD9 => "Ydieresis",
        0xDA => "fraction",
        0xDB => "currency",
        0xDC => "guilsinglleft",
        0xDD => "guilsinglright",
        0xDE => "fi",
        0xDF => "fl",
        0xE0 => "daggerdbl",
        0xE1 => "periodcentered",
        0xE2 => "quotesinglbase",
        0xE3 => "quotedblbase",
        0xE4 => "perthousand",
        0xE5 => "Acircumflex",
        0xE6 => "Ecircumflex",
        0xE7 => "Aacute",
        0xE8 => "Edieresis",
        0xE9 => "Egrave",
        0xEA => "Iacute",
        0xEB => "Icircumflex",
        0xEC => "Idieresis",
        0xED => "Igrave",
        0xEE => "Oacute",
        0xEF => "Ocircumflex",
        0xF0 => null, // Apple-logo PUA glyph; no portable name
        0xF1 => "Ograve",
        0xF2 => "Uacute",
        0xF3 => "Ucircumflex",
        0xF4 => "Ugrave",
        0xF5 => "dotlessi",
        0xF6 => "circumflex",
        0xF7 => "tilde",
        0xF8 => "macron",
        0xF9 => "breve",
        0xFA => "dotaccent",
        0xFB => "ring",
        0xFC => "cedilla",
        0xFD => "hungarumlaut",
        0xFE => "ogonek",
        0xFF => "caron",
            _ => null,
        };
    }

    /// <summary>
    /// Annex D Table D.1 (PostScript StandardEncoding) byte → glyph name. The high-byte range
    /// has its own repertoire (fraction at 0xA4, fi/fl at 0xAE/0xAF) which differs sharply
    /// from WinAnsi. Bytes StandardEncoding leaves unassigned return null.
    /// </summary>
    internal static string? StandardEncodingByteToName(byte b)
    {
        if (b >= 0x20 && b <= 0x7E)
        {
            return OxEncodingTables.GidToStandardGlyphName(b);
        }
        return b switch
        {
        0xA1 => "exclamdown",
        0xA2 => "cent",
        0xA3 => "sterling",
        0xA4 => "fraction",
        0xA5 => "yen",
        0xA6 => "florin",
        0xA7 => "section",
        0xA8 => "currency",
        0xA9 => "quotesingle",
        0xAA => "quotedblleft",
        0xAB => "guillemotleft",
        0xAC => "guilsinglleft",
        0xAD => "guilsinglright",
        0xAE => "fi",
        0xAF => "fl",
        0xB1 => "endash",
        0xB2 => "dagger",
        0xB3 => "daggerdbl",
        0xB4 => "periodcentered",
        0xB6 => "paragraph",
        0xB7 => "bullet",
        0xB8 => "quotesinglbase",
        0xB9 => "quotedblbase",
        0xBA => "quotedblright",
        0xBB => "guillemotright",
        0xBC => "ellipsis",
        0xBD => "perthousand",
        0xBF => "questiondown",
        0xC1 => "grave",
        0xC2 => "acute",
        0xC3 => "circumflex",
        0xC4 => "tilde",
        0xC5 => "macron",
        0xC6 => "breve",
        0xC7 => "dotaccent",
        0xC8 => "dieresis",
        0xCA => "ring",
        0xCB => "cedilla",
        0xCD => "hungarumlaut",
        0xCE => "ogonek",
        0xCF => "caron",
        0xE1 => "AE",
        0xE3 => "ordfeminine",
        0xE8 => "Lslash",
        0xE9 => "Oslash",
        0xEA => "OE",
        0xEB => "ordmasculine",
        0xF1 => "ae",
        0xF5 => "dotlessi",
        0xF8 => "lslash",
        0xF9 => "oslash",
        0xFA => "oe",
        0xFB => "germandbls",
            _ => null,
        };
    }
}
