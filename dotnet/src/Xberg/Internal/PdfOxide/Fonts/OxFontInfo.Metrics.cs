// Ported from pdf_oxide `fonts/font_dict.rs`:
//   get_vertical_metrics (2620-2640), get_glyph_width (2940-3000),
//   get_standard_font_width (3001-3027), classify_std14 (3028-3086),
//   std14_width dispatch (3087-3097), get_space_glyph_width (3585-3648),
//   get_byte_to_width_table (3960-3989), get_font_weight / compute_font_weight
//   (4760-4870), is_bold (4872-4886), has_explicit_widths (4888-4906).
//
// Widths are f32 throughout: the gap-correction thresholds downstream were
// calibrated on single-precision advances.
using System;

namespace Xberg.Internal.PdfOxide.Fonts;

internal sealed partial class OxFontInfo
{
    /// <summary>
    /// Vertical advance and origin offset for a CID, in 1000ths of em: /W2 entry, else /DW2,
    /// else the spec defaults. Read per glyph whenever <see cref="Wmode"/> is 1.
    /// </summary>
    public OxVerticalMetrics GetVerticalMetrics(ushort cid)
    {
        if (CidVerticalMetrics is not null && CidVerticalMetrics.TryGetValue(cid, out var m)) return m;
        return CidDefaultVerticalMetrics;
    }

    /// <summary>
    /// Glyph width in 1000ths of em (§9.7.4); multiply by font size / 1000 for user space.
    ///
    /// Type0 fonts read /W and then /DW — but only when /DW was explicitly present. Table 117
    /// makes the default for a missing /DW 1000 units; this deliberately deviates, because
    /// many non-fullwidth CID fonts omit /DW and returning 1000 over-estimates their glyphs
    /// and disables the gap-correction heuristic. Purely fullwidth CJK fonts that omit /DW
    /// may be under-estimated in exchange — the right trade for the mixed-script case.
    /// </summary>
    public float GetGlyphWidth(ushort charCode)
    {
        if (Subtype == "Type0")
        {
            if (CidWidths is not null && CidWidths.TryGetValue(charCode, out float width)) return width;
            if (HasExplicitDw) return CidDefaultWidth;
            // Fall through to DefaultWidth — the same path as a simple font without /Widths.
        }

        if (Widths is not null && FirstChar is not null)
        {
            long index = (long)charCode - FirstChar.Value;
            if (index >= 0 && index < Widths.Length) return Widths[index];
        }

        float? std = GetStandardFontWidth(charCode);
        if (std is not null) return std.Value;

        return DefaultWidth;
    }

    /// <summary>
    /// Standard-14 built-in metrics, for fonts without /Widths or codes outside
    /// [FirstChar, LastChar]. A code the /Widths array does cover keeps its explicit width;
    /// codes outside the range (commonly space, below a FirstChar of 66) prefer named-font
    /// metrics over the generic <see cref="DefaultWidth"/>, which is usually too wide.
    /// </summary>
    private float? GetStandardFontWidth(ushort charCode)
    {
        if (Widths is not null && FirstChar is not null)
        {
            long index = (long)charCode - FirstChar.Value;
            if (index >= 0 && index < Widths.Length) return null; // covered by explicit widths
        }

        // A pure function of BaseFont, but this runs once per glyph, so it is memoized.
        if (!_std14MemoInit)
        {
            _std14MemoInit = true;
            _std14Memo = ClassifyStd14();
        }
        if (_std14Memo is null) return null;

        OxStd14Flags std14 = _std14Memo.Value;
        if (std14.IsCourier) return 600.0f; // monospace
        return OxStd14.Width(std14, std14.IsTimes, std14.IsBold, (byte)charCode);
    }

    /// <summary>
    /// Classify BaseFont against the Standard-14 set (Annex D). Matching is exact after the
    /// subset prefix is stripped — a `contains` test would read "HelveticaCorp-Custom" as
    /// Helvetica and hand it the wrong metrics.
    /// </summary>
    private OxStd14Flags? ClassifyStd14()
    {
        string rawName = BaseFont;
        int idx = rawName.IndexOf('+');
        string name = rawName;
        if (idx >= 0)
        {
            string suffix = rawName.Substring(idx + 1);
            if (suffix.Length > 0) name = suffix;
        }

        // "Helvetica-Oblique" is what virtually every real-world PDF writes; the spec's
        // canonical PostScript name is "HelveticaOblique". Both are accepted.
        bool isStandard14 = name switch
        {
            "Courier" or "Courier-Bold" or "Courier-BoldOblique" or "Courier-Oblique"
                or "Helvetica" or "Helvetica-Bold" or "Helvetica-BoldOblique" or "Helvetica-Oblique"
                or "HelveticaOblique"
                or "Times-Roman" or "Times-Bold" or "Times-BoldItalic" or "Times-Italic"
                or "Symbol" or "ZapfDingbats" => true,
            _ => false,
        };
        if (!isStandard14) return null;

        bool isTimes = name.StartsWith("Times", StringComparison.Ordinal);
        bool isHelvetica = name.StartsWith("Helvetica", StringComparison.Ordinal);
        bool isCourier = name.StartsWith("Courier", StringComparison.Ordinal);

        if (!isTimes && !isHelvetica && !isCourier) return null;

        return new OxStd14Flags(
            isTimes,
            isCourier,
            name.Contains("Bold", StringComparison.Ordinal),
            name.Contains("BoldItalic", StringComparison.Ordinal),
            isHelvetica,
            name.Contains("Italic", StringComparison.Ordinal));
    }

    /// <summary>
    /// Width of the space glyph in 1000ths of em, which feeds the caller's geometric word-gap
    /// threshold (threshold = space width × ratio). Anything that is not really the space
    /// advance skews that threshold and mis-detects word boundaries.
    /// </summary>
    public float GetSpaceGlyphWidth()
    {
        // Identity-H/V Type0 fonts — nearly every embedded subset — map code 0x20 to CID 32,
        // an arbitrary font-internal glyph, not the space; the real space glyph is reached
        // through the font's CMap (§9.7.5.2, §9.10.2). Trusting /W[0x20] there yields ~0.5 em+
        // (TimesNewRomanPSMT reports 563), a threshold so wide that real justified word gaps
        // fall below it and adjacent words glue together.
        if (Subtype == "Type0")
        {
            if (Encoding.IsIdentity) return 250.0f;

            // A non-Identity predefined CMap (90ms-RKSJ-H, …) can map 0x20 to a real space CID,
            // so an explicit /W entry there is meaningful.
            if (CidWidths is not null && CidWidths.TryGetValue(0x20, out float cidW) && cidW >= 50.0f)
                return cidW;
            return 250.0f;
        }

        float w = GetGlyphWidth(0x20);

        // Many simple subset fonts (shaped Arabic out of browser print paths) omit a glyph for
        // 0x20 entirely, so this comes back ~0. A zero threshold reads every inter-glyph
        // kerning gap as a word boundary and shatters cursive words into single letters.
        return w < 50.0f ? 250.0f : w;
    }

    /// <summary>
    /// Pre-computed byte→width lookup for simple fonts, removing the per-byte bounds check
    /// and subtraction from the advance loop.
    /// </summary>
    public float[] GetByteToWidthTable()
    {
        if (_byteToWidthTable is not null) return _byteToWidthTable;

        var tbl = new float[256];
        for (int i = 0; i < 256; i++) tbl[i] = DefaultWidth;

        if (Widths is not null)
        {
            if (FirstChar is not null)
            {
                for (int idx = 0; idx < Widths.Length; idx++)
                {
                    long code = FirstChar.Value + idx;
                    if (code < 256) tbl[code] = Widths[idx];
                }
            }
        }
        else
        {
            // Standard-14 fonts ship without /Widths and §9.6.2.2 requires built-in metrics.
            // GetStandardFontWidth answers only for the Helvetica/Times/Courier variants, so
            // every other font keeps the DefaultWidth fallback.
            for (int byteCode = 0; byteCode < 256; byteCode++)
            {
                float? w = GetStandardFontWidth((ushort)byteCode);
                if (w is not null) tbl[byteCode] = w.Value;
            }
        }

        _byteToWidthTable = tbl;
        return tbl;
    }

    /// <summary>
    /// Font weight, by the §9.6.2 cascade: /FontWeight, then the ForceBold flag, then name
    /// heuristics, then StemV.
    /// </summary>
    public OxFontWeight GetFontWeight()
    {
        _weightMemo ??= ComputeFontWeight();
        return _weightMemo.Value;
    }

    private OxFontWeight ComputeFontWeight()
    {
        // PRIORITY 1: /FontWeight (Table 122) — the only non-heuristic source.
        if (FontWeight is not null) return OxFontWeightValues.FromPdfValue(FontWeight.Value);

        // PRIORITY 2: ForceBold (Table 123, bit 19).
        if (Flags is not null)
        {
            const int ForceBoldBit = 0x80000;
            if ((Flags.Value & ForceBoldBit) != 0) return OxFontWeight.Bold;
        }

        // PRIORITY 3: name heuristics, for fonts with no descriptor or missing fields.
        string nameLower = BaseFont.ToLowerInvariant();

        if (nameLower.Contains("black", StringComparison.Ordinal) || nameLower.Contains("heavy", StringComparison.Ordinal))
            return OxFontWeight.Black;
        if (nameLower.Contains("extrabold", StringComparison.Ordinal) || nameLower.Contains("ultrabold", StringComparison.Ordinal))
            return OxFontWeight.ExtraBold;
        if (nameLower.Contains("bold", StringComparison.Ordinal))
        {
            if (nameLower.Contains("semibold", StringComparison.Ordinal) || nameLower.Contains("demibold", StringComparison.Ordinal))
                return OxFontWeight.SemiBold;
            return OxFontWeight.Bold;
        }
        if (nameLower.Contains("medium", StringComparison.Ordinal)) return OxFontWeight.Medium;
        if (nameLower.Contains("light", StringComparison.Ordinal))
        {
            if (nameLower.Contains("extralight", StringComparison.Ordinal) || nameLower.Contains("ultralight", StringComparison.Ordinal))
                return OxFontWeight.ExtraLight;
            return OxFontWeight.Light;
        }
        if (nameLower.Contains("thin", StringComparison.Ordinal)) return OxFontWeight.Thin;

        // PRIORITY 4: StemV. Empirical, not mandated by the spec: >110 usually bold,
        // 80-110 medium, below 80 falls through to Normal.
        if (StemV is not null)
        {
            if (StemV.Value > 110.0f) return OxFontWeight.Bold;
            if (StemV.Value >= 80.0f) return OxFontWeight.Medium;
        }

        return OxFontWeight.Normal;
    }

    /// <summary>True when the weight is SemiBold (600) or heavier.</summary>
    public bool IsBold() => OxFontWeightValues.IsBold(GetFontWeight());

    /// <summary>
    /// Whether per-glyph widths come from the PDF (/Widths, /W or an explicit /DW) rather
    /// than the generic 500/550/600 fallback. When false every glyph reports the same
    /// advance, so bounding boxes computed from those advances mis-state the visible extent
    /// and collapse the real gaps between Tj-positioned words.
    /// </summary>
    public bool HasExplicitWidths() => Widths is not null || CidWidths is not null || HasExplicitDw;
}
