// Standard PDF simple-font encodings (ISO 32000-1 Annex D) and glyph-name → Unicode
// resolution (Adobe Glyph List subset + uniXXXX / uXXXXXX conventions).
using System.Text;

namespace Xberg.Internal.Pdf;

public static class PdfEncodings
{
    public static readonly string[] WinAnsi = BuildWinAnsi();
    public static readonly string[] MacRoman = BuildMacRoman();
    public static readonly string[] Standard = BuildStandard();
    public static readonly string[] PdfDoc = BuildPdfDoc();

    public static string[]? ByName(string? name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "StandardEncoding" => Standard,
        "PDFDocEncoding" => PdfDoc,
        _ => null,
    };

    private static string[] AsciiBase()
    {
        var a = new string[256];
        for (int i = 0; i < 256; i++) a[i] = "";
        for (int i = 0x20; i <= 0x7E; i++) a[i] = ((char)i).ToString();
        return a;
    }

    private static string[] BuildWinAnsi()
    {
        var a = AsciiBase();
        var hi = new (int, int)[]
        {
            (0x80,0x20AC),(0x82,0x201A),(0x83,0x0192),(0x84,0x201E),(0x85,0x2026),(0x86,0x2020),(0x87,0x2021),
            (0x88,0x02C6),(0x89,0x2030),(0x8A,0x0160),(0x8B,0x2039),(0x8C,0x0152),(0x8E,0x017D),(0x91,0x2018),
            (0x92,0x2019),(0x93,0x201C),(0x94,0x201D),(0x95,0x2022),(0x96,0x2013),(0x97,0x2014),(0x98,0x02DC),
            (0x99,0x2122),(0x9A,0x0161),(0x9B,0x203A),(0x9C,0x0153),(0x9E,0x017E),(0x9F,0x0178),
        };
        foreach (var (c, u) in hi) a[c] = ((char)u).ToString();
        for (int i = 0xA0; i <= 0xFF; i++) a[i] = ((char)i).ToString();
        return a;
    }

    private static string[] BuildStandard()
    {
        var a = AsciiBase();
        a[0x27] = "’"; // quoteright
        a[0x60] = "‘"; // quoteleft
        var hi = new (int, int)[]
        {
            (0xA1,0x00A1),(0xA2,0x00A2),(0xA3,0x00A3),(0xA4,0x2044),(0xA5,0x00A5),(0xA6,0x0192),(0xA7,0x00A7),
            (0xA8,0x00A4),(0xA9,0x0027),(0xAA,0x201C),(0xAB,0x00AB),(0xAC,0x2039),(0xAD,0x203A),(0xAE,0xFB01),
            (0xAF,0xFB02),(0xB1,0x2013),(0xB2,0x2020),(0xB3,0x2021),(0xB4,0x00B7),(0xB6,0x00B6),(0xB7,0x2022),
            (0xB8,0x201A),(0xB9,0x201E),(0xBA,0x201D),(0xBB,0x00BB),(0xBC,0x2026),(0xBD,0x2030),(0xBF,0x00BF),
            (0xC1,0x0060),(0xC2,0x00B4),(0xC3,0x02C6),(0xC4,0x02DC),(0xC5,0x00AF),(0xC6,0x02D8),(0xC7,0x02D9),
            (0xC8,0x00A8),(0xCA,0x02DA),(0xCB,0x00B8),(0xCD,0x02DD),(0xCE,0x02DB),(0xCF,0x02C7),(0xD0,0x2014),
            (0xE1,0x00C6),(0xE3,0x00AA),(0xE8,0x0141),(0xE9,0x00D8),(0xEA,0x0152),(0xEB,0x00BA),(0xF1,0x00E6),
            (0xF5,0x0131),(0xF8,0x0142),(0xF9,0x00F8),(0xFA,0x0153),(0xFB,0x00DF),
        };
        foreach (var (c, u) in hi) a[c] = ((char)u).ToString();
        return a;
    }

    private static string[] BuildMacRoman()
    {
        var a = AsciiBase();
        int[] hi = {
            0x00C4,0x00C5,0x00C7,0x00C9,0x00D1,0x00D6,0x00DC,0x00E1,0x00E0,0x00E2,0x00E4,0x00E3,0x00E5,0x00E7,0x00E9,0x00E8,
            0x00EA,0x00EB,0x00ED,0x00EC,0x00EE,0x00EF,0x00F1,0x00F3,0x00F2,0x00F4,0x00F6,0x00F5,0x00FA,0x00F9,0x00FB,0x00FC,
            0x2020,0x00B0,0x00A2,0x00A3,0x00A7,0x2022,0x00B6,0x00DF,0x00AE,0x00A9,0x2122,0x00B4,0x00A8,0x2260,0x00C6,0x00D8,
            0x221E,0x00B1,0x2264,0x2265,0x00A5,0x00B5,0x2202,0x2211,0x220F,0x03C0,0x222B,0x00AA,0x00BA,0x03A9,0x00E6,0x00F8,
            0x00BF,0x00A1,0x00AC,0x221A,0x0192,0x2248,0x2206,0x00AB,0x00BB,0x2026,0x00A0,0x00C0,0x00C3,0x00D5,0x0152,0x0153,
            0x2013,0x2014,0x201C,0x201D,0x2018,0x2019,0x00F7,0x25CA,0x00FF,0x0178,0x2044,0x20AC,0x2039,0x203A,0xFB01,0xFB02,
            0x2021,0x00B7,0x201A,0x201E,0x2030,0x00C2,0x00CA,0x00C1,0x00CB,0x00C8,0x00CD,0x00CE,0x00CF,0x00CC,0x00D3,0x00D4,
            0xF8FF,0x00D2,0x00DA,0x00DB,0x00D9,0x0131,0x02C6,0x02DC,0x00AF,0x02D8,0x02D9,0x02DA,0x00B8,0x02DD,0x02DB,0x02C7,
        };
        for (int i = 0; i < hi.Length; i++) a[0x80 + i] = char.ConvertFromUtf32(hi[i]);
        return a;
    }

    private static string[] BuildPdfDoc()
    {
        var a = AsciiBase();
        for (int i = 0xA0; i <= 0xFF; i++) a[i] = ((char)i).ToString();
        // Fill 0x80-0x9F with WinAnsi-like specials where defined.
        for (int i = 0x80; i <= 0x9F; i++) if (WinAnsi[i].Length > 0) a[i] = WinAnsi[i];
        return a;
    }

    /// <summary>Resolve an Adobe glyph name to its Unicode string.</summary>
    public static string GlyphNameToUnicode(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        // Strip suffix after '.', e.g. "a.sc".
        int dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];
        if (name.Length == 0) return "";

        if (AglMap.TryGetValue(name, out var s)) return s;

        // uniXXXX (one or more 4-hex sequences).
        if (name.StartsWith("uni") && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var sb = new StringBuilder();
            bool ok = true;
            for (int i = 3; i + 4 <= name.Length; i += 4)
            {
                if (int.TryParse(name.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber, null, out int cp) && cp != 0)
                    sb.Append((char)cp);
                else { ok = false; break; }
            }
            if (ok && sb.Length > 0) return sb.ToString();
        }
        // uXXXX..XXXXXX
        if (name.Length >= 5 && name[0] == 'u' && IsHex(name, 1))
        {
            if (int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int cp) && cp > 0 && cp <= 0x10FFFF)
                return char.ConvertFromUtf32(cp);
        }
        // gXX / cidXX / index names → no unicode.
        // "gNN" or trailing digits fallback: single ASCII letter names.
        return "";
    }

    private static bool IsHex(string s, int from)
    {
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return s.Length > from;
    }

    // Adobe Glyph List — common subset covering Latin text, punctuation, ligatures, symbols.
    private static readonly Dictionary<string, string> AglMap = BuildAgl();

    private static Dictionary<string, string> BuildAgl()
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        void P(string n, int cp) => m[n] = char.ConvertFromUtf32(cp);
        // ASCII letters/digits
        for (char c = 'A'; c <= 'Z'; c++) m[c.ToString()] = c.ToString();
        for (char c = 'a'; c <= 'z'; c++) m[c.ToString()] = c.ToString();
        string[] digitNames = { "zero","one","two","three","four","five","six","seven","eight","nine" };
        for (int i = 0; i < 10; i++) m[digitNames[i]] = ((char)('0' + i)).ToString();
        // Punctuation / symbols
        P("space",0x20); P("exclam",0x21); P("quotedbl",0x22); P("numbersign",0x23); P("dollar",0x24);
        P("percent",0x25); P("ampersand",0x26); P("quotesingle",0x27); P("parenleft",0x28); P("parenright",0x29);
        P("asterisk",0x2A); P("plus",0x2B); P("comma",0x2C); P("hyphen",0x2D); P("period",0x2E); P("slash",0x2F);
        P("colon",0x3A); P("semicolon",0x3B); P("less",0x3C); P("equal",0x3D); P("greater",0x3E); P("question",0x3F);
        P("at",0x40); P("bracketleft",0x5B); P("backslash",0x5C); P("bracketright",0x5D); P("asciicircum",0x5E);
        P("underscore",0x5F); P("grave",0x60); P("braceleft",0x7B); P("bar",0x7C); P("braceright",0x7D); P("asciitilde",0x7E);
        P("quoteleft",0x2018); P("quoteright",0x2019); P("quotedblleft",0x201C); P("quotedblright",0x201D);
        P("quotesinglbase",0x201A); P("quotedblbase",0x201E); P("bullet",0x2022); P("dagger",0x2020); P("daggerdbl",0x2021);
        P("ellipsis",0x2026); P("emdash",0x2014); P("endash",0x2013); P("perthousand",0x2030); P("guilsinglleft",0x2039);
        P("guilsinglright",0x203A); P("guillemotleft",0x00AB); P("guillemotright",0x00BB); P("trademark",0x2122);
        P("fi",0xFB01); P("fl",0xFB02); P("ff",0xFB00); P("ffi",0xFB03); P("ffl",0xFB04);
        P("florin",0x0192); P("section",0x00A7); P("paragraph",0x00B6); P("periodcentered",0x00B7); P("cent",0x00A2);
        P("sterling",0x00A3); P("currency",0x00A4); P("yen",0x00A5); P("brokenbar",0x00A6); P("dieresis",0x00A8);
        P("copyright",0x00A9); P("ordfeminine",0x00AA); P("logicalnot",0x00AC); P("registered",0x00AE); P("macron",0x00AF);
        P("degree",0x00B0); P("plusminus",0x00B1); P("acute",0x00B4); P("mu",0x00B5); P("cedilla",0x00B8);
        P("ordmasculine",0x00BA); P("onequarter",0x00BC); P("onehalf",0x00BD); P("threequarters",0x00BE);
        P("questiondown",0x00BF); P("exclamdown",0x00A1); P("divide",0x00F7); P("multiply",0x00D7); P("minus",0x2212);
        P("fraction",0x2044); P("circumflex",0x02C6); P("tilde",0x02DC); P("breve",0x02D8); P("dotaccent",0x02D9);
        P("ring",0x02DA); P("hungarumlaut",0x02DD); P("ogonek",0x02DB); P("caron",0x02C7); P("nbspace",0x00A0);
        P("euro",0x20AC); P("Euro",0x20AC); P("nonbreakingspace",0x00A0);
        // Accented Latin
        P("Agrave",0x00C0); P("Aacute",0x00C1); P("Acircumflex",0x00C2); P("Atilde",0x00C3); P("Adieresis",0x00C4);
        P("Aring",0x00C5); P("AE",0x00C6); P("Ccedilla",0x00C7); P("Egrave",0x00C8); P("Eacute",0x00C9);
        P("Ecircumflex",0x00CA); P("Edieresis",0x00CB); P("Igrave",0x00CC); P("Iacute",0x00CD); P("Icircumflex",0x00CE);
        P("Idieresis",0x00CF); P("Eth",0x00D0); P("Ntilde",0x00D1); P("Ograve",0x00D2); P("Oacute",0x00D3);
        P("Ocircumflex",0x00D4); P("Otilde",0x00D5); P("Odieresis",0x00D6); P("Oslash",0x00D8); P("Ugrave",0x00D9);
        P("Uacute",0x00DA); P("Ucircumflex",0x00DB); P("Udieresis",0x00DC); P("Yacute",0x00DD); P("Thorn",0x00DE);
        P("germandbls",0x00DF); P("agrave",0x00E0); P("aacute",0x00E1); P("acircumflex",0x00E2); P("atilde",0x00E3);
        P("adieresis",0x00E4); P("aring",0x00E5); P("ae",0x00E6); P("ccedilla",0x00E7); P("egrave",0x00E8);
        P("eacute",0x00E9); P("ecircumflex",0x00EA); P("edieresis",0x00EB); P("igrave",0x00EC); P("iacute",0x00ED);
        P("icircumflex",0x00EE); P("idieresis",0x00EF); P("eth",0x00F0); P("ntilde",0x00F1); P("ograve",0x00F2);
        P("oacute",0x00F3); P("ocircumflex",0x00F4); P("otilde",0x00F5); P("odieresis",0x00F6); P("oslash",0x00F8);
        P("ugrave",0x00F9); P("uacute",0x00FA); P("ucircumflex",0x00FB); P("udieresis",0x00FC); P("yacute",0x00FD);
        P("thorn",0x00FE); P("ydieresis",0x00FF); P("dotlessi",0x0131); P("Lslash",0x0141); P("lslash",0x0142);
        P("OE",0x0152); P("oe",0x0153); P("Scaron",0x0160); P("scaron",0x0161); P("Zcaron",0x017D); P("zcaron",0x017E);
        P("Ydieresis",0x0178);
        return m;
    }
}
