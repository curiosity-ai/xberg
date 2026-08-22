// Ported from pdf_oxide 0.3.77 `src/fonts/font_dict.rs` (`FontInfo::embedded_glyph_name`),
// which reads the embedded program's own glyph names through `ttf_parser::Face::glyph_name`:
// the `post` table first, then the `CFF ` charset. Those names are the authoritative source
// for §9.10.2 Priority 3c and for the Item 1 punctuation recovery in `char_to_unicode`.

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>
/// Glyph names carried by an embedded font program, indexed by GID.
/// </summary>
internal static class OxEmbeddedGlyphNames
{
    /// <summary>
    /// The 258 standard Macintosh glyph names a `post` Format 2.0 table indexes into for any
    /// name it does not spell out (Apple TrueType Reference Manual, chapter 6, `post`).
    /// </summary>
    private static readonly string[] MacintoshNames =
    {
        ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign", "dollar",
        "percent", "ampersand", "quotesingle", "parenleft", "parenright", "asterisk", "plus", "comma",
        "hyphen", "period", "slash", "zero", "one", "two", "three", "four",
        "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less",
        "equal", "greater", "question", "at", "A", "B", "C", "D",
        "E", "F", "G", "H", "I", "J", "K", "L",
        "M", "N", "O", "P", "Q", "R", "S", "T",
        "U", "V", "W", "X", "Y", "Z", "bracketleft", "backslash",
        "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d",
        "e", "f", "g", "h", "i", "j", "k", "l",
        "m", "n", "o", "p", "q", "r", "s", "t",
        "u", "v", "w", "x", "y", "z", "braceleft", "bar",
        "braceright", "asciitilde", "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis",
        "Udieresis", "aacute", "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla",
        "eacute", "egrave", "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis",
        "ntilde", "oacute", "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave",
        "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling", "section", "bullet",
        "paragraph", "germandbls", "registered", "copyright", "trademark", "acute", "dieresis", "notequal",
        "AE", "Oslash", "infinity", "plusminus", "lessequal", "greaterequal", "yen", "mu",
        "partialdiff", "summation", "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega",
        "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal",
        "Delta", "guillemotleft", "guillemotright", "ellipsis", "nonbreakingspace", "Agrave", "Atilde", "Otilde",
        "OE", "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright",
        "divide", "lozenge", "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright",
        "fi", "fl", "daggerdbl", "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex",
        "Ecircumflex", "Aacute", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave",
        "Oacute", "Ocircumflex", "apple", "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi",
        "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut",
        "ogonek", "caron", "Lslash", "lslash", "Scaron", "scaron", "Zcaron", "zcaron",
        "brokenbar", "Eth", "eth", "Yacute", "yacute", "Thorn", "thorn", "minus",
        "multiply", "onesuperior", "twosuperior", "threesuperior", "onehalf", "onequarter", "threequarters", "franc",
        "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla", "Cacute", "cacute", "Ccaron",
        "ccaron", "dcroat",
    };

    /// <summary>
    /// Glyph names for <paramref name="fontData"/> indexed by GID, or null when the program
    /// carries none — a `post` Format 3 table or a subset stripped of names. Entries are null
    /// where a GID has no name of its own; `.notdef` and empty names count as absent.
    /// </summary>
    internal static IReadOnlyList<string?>? ForFontData(byte[] fontData)
    {
        OxTrueTypeFont? face = OxTrueTypeFont.Parse(fontData);
        if (face is null)
        {
            return null;
        }

        int numGlyphs = face.NumGlyphs;
        string?[]? post = PostNames(face);
        string?[]? cff = OxCffEncoding.GlyphNamesByGid(fontData);
        if (post is null && cff is null)
        {
            return null;
        }

        var names = new string?[numGlyphs];
        bool foundAny = false;
        for (int gid = 0; gid < numGlyphs; gid++)
        {
            // `post` wins outright where it names the glyph at all: a name it spells as
            // `.notdef` is an answer, not a reason to consult the charset.
            string? name = post is not null && gid < post.Length ? post[gid] : null;
            name ??= cff is not null && gid < cff.Length ? cff[gid] : null;

            if (name is not null && name.Length > 0 && name != ".notdef")
            {
                names[gid] = name;
                foundAny = true;
            }
        }

        return foundAny ? names : null;
    }

    /// <summary>
    /// `post` Format 2.0 glyph names indexed by GID. Every other version stores no names, and
    /// a table too short or of an unknown version is not a `post` table at all.
    /// </summary>
    private static string?[]? PostNames(OxTrueTypeFont face)
    {
        if (!face.TryGetTable("post", out int offset, out int length) || length < 32)
        {
            return null;
        }

        byte[] data = face.RawData;
        var r = new OxBeReader(data, offset);
        if (!r.TryU32(out uint version))
        {
            return null;
        }
        if (version != 0x00010000 && version != 0x00020000 && version != 0x00025000
            && version != 0x00030000 && version != 0x00040000)
        {
            return null;
        }
        if (version != 0x00020000)
        {
            return Array.Empty<string?>();
        }

        r.Position = offset + 32;
        if (!r.TryU16(out ushort indexCount))
        {
            return null;
        }
        var indexes = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            if (!r.TryU16(out indexes[i]))
            {
                return null;
            }
        }

        // The names themselves follow as Pascal strings, in the order the indexes past 257
        // reference them.
        var pascal = new List<string>();
        long pos = r.Position;
        long end = Math.Min((long)offset + length, data.Length);
        while (pos < end)
        {
            int len = data[pos];
            pos++;
            if (pos + len > end)
            {
                break;
            }
            pascal.Add(System.Text.Encoding.UTF8.GetString(data, (int)pos, len));
            pos += len;
        }

        var names = new string?[indexCount];
        for (int gid = 0; gid < indexCount; gid++)
        {
            int index = indexes[gid];
            names[gid] = index < MacintoshNames.Length
                ? MacintoshNames[index]
                : (index - MacintoshNames.Length) < pascal.Count
                    ? pascal[index - MacintoshNames.Length]
                    : null;
        }
        return names;
    }
}
