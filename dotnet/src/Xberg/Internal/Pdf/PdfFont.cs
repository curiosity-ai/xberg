// PDF font model for text extraction: simple fonts (Type1/TrueType/Type3) and
// composite Type0/CID fonts. Provides code iteration, code→Unicode, and glyph widths.
// Ports the text-relevant behaviour of pdf_oxide's FontInfo (crates/pdf/oxide).

namespace Xberg.Internal.Pdf;

public sealed class PdfFont
{
    public bool IsType0;
    public string BaseFont = "";
    public string Subtype = "";

    // Simple font: code (0-255) -> unicode string (from encoding + Differences).
    private string[]? _encoding;
    // Simple font widths.
    private Dictionary<int, double>? _widths;
    public int FirstChar;
    public double MissingWidth;

    // ToUnicode (both simple and Type0).
    private PdfCMap? _toUnicode;

    // Type0: encoding CMap (bytes->code, code->CID), CID widths.
    private PdfCMap? _cmap;
    private Dictionary<int, double>? _cidWidths;
    public double DefaultWidth = 1000.0;

    public bool IsBold;
    public bool IsItalic;
    public double FontMatrixA = 0.001;
    public double Ascent = 0.95;
    public double Descent = -0.35;

    public static PdfFont Load(PdfDict fontDict, PdfDocument doc)
    {
        var f = new PdfFont();
        f.Subtype = doc.Resolve(fontDict.Get("Subtype")).AsName() ?? "";
        f.BaseFont = doc.Resolve(fontDict.Get("BaseFont")).AsName() ?? "";

        // ToUnicode
        if (doc.Resolve(fontDict.Get("ToUnicode")) is PdfStream tuStream)
        {
            try { f._toUnicode = PdfCMap.ParseToUnicode(doc.DecodeStream(tuStream)); }
            catch { f._toUnicode = null; }
        }

        if (f.Subtype == "Type0")
        {
            f.IsType0 = true;
            LoadType0(f, fontDict, doc);
        }
        else
        {
            LoadSimple(f, fontDict, doc);
        }
        // Bold/italic hint from base font name.
        string bf = f.BaseFont.ToLowerInvariant();
        if (bf.Contains("bold")) f.IsBold = true;
        if (bf.Contains("italic") || bf.Contains("oblique")) f.IsItalic = true;
        return f;
    }

    private static void LoadSimple(PdfFont f, PdfDict fontDict, PdfDocument doc)
    {
        // Base encoding.
        string[] baseEnc;
        bool symbolic = false;
        var descriptor = doc.Resolve(fontDict.Get("FontDescriptor")).AsDict();
        if (descriptor != null)
        {
            int flags = (int)(doc.Resolve(descriptor.Get("Flags")).AsLong() ?? 0);
            symbolic = (flags & 4) != 0 && (flags & 32) == 0;
            if ((flags & (1 << 18)) != 0) f.IsBold = true;
            if ((flags & (1 << 6)) != 0) f.IsItalic = true;
            f.MissingWidth = doc.Resolve(descriptor.Get("MissingWidth")).AsNumber() ?? 0;
            var asc = doc.Resolve(descriptor.Get("Ascent")).AsNumber();
            var desc = doc.Resolve(descriptor.Get("Descent")).AsNumber();
            if (asc.HasValue && asc.Value != 0) f.Ascent = asc.Value / 1000.0;
            if (desc.HasValue && desc.Value != 0) f.Descent = desc.Value / 1000.0;
        }

        var encObj = doc.Resolve(fontDict.Get("Encoding"));
        string? baseName = null;
        PdfArray? diffs = null;
        if (encObj is PdfName en) baseName = en.Value;
        else if (encObj.AsDict() is PdfDict ed)
        {
            baseName = doc.Resolve(ed.Get("BaseEncoding")).AsName();
            diffs = doc.Resolve(ed.Get("Differences")).AsArray();
        }

        baseEnc = PdfEncodings.ByName(baseName)
            ?? (f.Subtype == "TrueType" && !symbolic ? PdfEncodings.WinAnsi : PdfEncodings.Standard);
        // Type3 FontMatrix
        if (f.Subtype == "Type3" && doc.Resolve(fontDict.Get("FontMatrix")).AsArray() is PdfArray fm && fm.Items.Count >= 1)
            f.FontMatrixA = doc.Resolve(fm.Items[0]).AsNumber() ?? 0.001;

        var enc = new string[256];
        Array.Copy(baseEnc, enc, 256);
        if (diffs != null)
        {
            int cur = 0;
            foreach (var it in diffs.Items)
            {
                var r = doc.Resolve(it);
                if (r is PdfNumber n) cur = (int)n.Value;
                else if (r is PdfName gn)
                {
                    if (cur >= 0 && cur < 256) enc[cur] = PdfEncodings.GlyphNameToUnicode(gn.Value);
                    cur++;
                }
            }
        }
        f._encoding = enc;

        // Widths
        f.FirstChar = (int)(doc.Resolve(fontDict.Get("FirstChar")).AsLong() ?? 0);
        var wArr = doc.Resolve(fontDict.Get("Widths")).AsArray();
        if (wArr != null)
        {
            f._widths = new Dictionary<int, double>();
            for (int i = 0; i < wArr.Items.Count; i++)
            {
                double w = doc.Resolve(wArr.Items[i]).AsNumber() ?? 0;
                f._widths[f.FirstChar + i] = w;
            }
        }
    }

    private static void LoadType0(PdfFont f, PdfDict fontDict, PdfDocument doc)
    {
        var encObj = doc.Resolve(fontDict.Get("Encoding"));
        if (encObj is PdfName en)
        {
            if (en.Value.StartsWith("Identity")) f._cmap = PdfCMap.Identity(2);
            else f._cmap = PdfCMap.Identity(2); // predefined CJK CMaps approximated as 2-byte identity
        }
        else if (encObj is PdfStream cmapStream)
        {
            try { f._cmap = PdfCMap.ParseCid(doc.DecodeStream(cmapStream)); }
            catch { f._cmap = PdfCMap.Identity(2); }
        }
        else f._cmap = PdfCMap.Identity(2);

        var descFonts = doc.Resolve(fontDict.Get("DescendantFonts")).AsArray();
        PdfDict? cidFont = null;
        if (descFonts != null && descFonts.Items.Count > 0) cidFont = doc.Resolve(descFonts.Items[0]).AsDict();
        if (cidFont != null)
        {
            f.DefaultWidth = doc.Resolve(cidFont.Get("DW")).AsNumber() ?? 1000.0;
            var wArr = doc.Resolve(cidFont.Get("W")).AsArray();
            if (wArr != null) f._cidWidths = ParseCidWidths(wArr, doc);
            var descriptor = doc.Resolve(cidFont.Get("FontDescriptor")).AsDict();
            if (descriptor != null)
            {
                int flags = (int)(doc.Resolve(descriptor.Get("Flags")).AsLong() ?? 0);
                if ((flags & (1 << 18)) != 0) f.IsBold = true;
                if ((flags & (1 << 6)) != 0) f.IsItalic = true;
                var asc = doc.Resolve(descriptor.Get("Ascent")).AsNumber();
                var desc = doc.Resolve(descriptor.Get("Descent")).AsNumber();
                if (asc.HasValue && asc.Value != 0) f.Ascent = asc.Value / 1000.0;
                if (desc.HasValue && desc.Value != 0) f.Descent = desc.Value / 1000.0;
            }
        }
    }

    private static Dictionary<int, double> ParseCidWidths(PdfArray w, PdfDocument doc)
    {
        var map = new Dictionary<int, double>();
        int i = 0;
        while (i < w.Items.Count)
        {
            var first = doc.Resolve(w.Items[i]);
            if (first is not PdfNumber c) { i++; continue; }
            if (i + 1 >= w.Items.Count) break;
            var second = doc.Resolve(w.Items[i + 1]);
            if (second is PdfArray arr)
            {
                int cid = (int)c.Value;
                foreach (var it in arr.Items)
                    map[cid++] = doc.Resolve(it).AsNumber() ?? 0;
                i += 2;
            }
            else if (second is PdfNumber c2 && i + 2 < w.Items.Count)
            {
                double wv = doc.Resolve(w.Items[i + 2]).AsNumber() ?? 0;
                for (int cid = (int)c.Value; cid <= (int)c2.Value; cid++) map[cid] = wv;
                i += 3;
            }
            else i++;
        }
        return map;
    }

    /// <summary>Split a show-text byte string into character codes.</summary>
    public IEnumerable<int> DecodeCodes(byte[] bytes)
    {
        if (!IsType0)
        {
            foreach (var b in bytes) yield return b;
            yield break;
        }
        var cmap = _cmap ?? PdfCMap.Identity(2);
        int pos = 0;
        while (pos < bytes.Length)
        {
            int len = cmap.MatchCodeLength(bytes, pos);
            if (len <= 0) len = 1;
            if (pos + len > bytes.Length) len = bytes.Length - pos;
            int code = 0;
            for (int i = 0; i < len; i++) code = (code << 8) | bytes[pos + i];
            pos += len;
            yield return code;
        }
    }

    // Note: pdf_oxide only expands ligature chars (U+FB00–06 → "fi"/"fl"…) when the
    // mapping comes from a parsed embedded font-program encoding — NOT from ToUnicode.
    // Since this port doesn't parse embedded CFF/TrueType programs, we return the raw
    // Unicode (matching pdf_oxide's ToUnicode path, which is the common case).
    public string CharToUnicode(int code)
    {
        if (_toUnicode != null)
        {
            var s = _toUnicode.LookupUnicode(code);
            if (s != null) return s;
        }
        if (!IsType0)
        {
            if (_encoding != null && code >= 0 && code < 256)
            {
                var s = _encoding[code];
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (code >= 32 && code < 127) return ((char)code).ToString();
            return "";
        }
        // Type0 without ToUnicode: last resort, treat CID as unicode if plausible.
        int? cid = _cmap?.LookupCid(code);
        if (cid.HasValue && cid.Value >= 32 && cid.Value < 0xFFFE) return ((char)cid.Value).ToString();
        return "";
    }

    /// <summary>Glyph advance width in glyph-space units (1/1000 em typically).</summary>
    public double GlyphWidth(int code)
    {
        if (!IsType0)
        {
            if (_widths != null && _widths.TryGetValue(code, out var w)) return w;
            if (MissingWidth != 0) return MissingWidth;
            return 500.0;
        }
        int cid = _cmap?.LookupCid(code) ?? code;
        if (_cidWidths != null && _cidWidths.TryGetValue(cid, out var cw)) return cw;
        return DefaultWidth;
    }
}
