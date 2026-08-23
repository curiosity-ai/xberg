// The glyph-decoding half of pdf_oxide's text extractor, ported from
// pdf_oxide-0.3.77 src/extractors/text.rs:
//   PRESERVE_UNMAPPED_GLYPHS + accessors (L28-66), snap_run_rotation (L1928-1955),
//   impl TjBuffer::{new, append} (L1958-2098), fallback_char_to_unicode (L2100-2258),
//   ByteMode (L2260-2270), font_has_utf8_cmap (L2272-2288), get_byte_mode (L2290-2342),
//   TextCharIter (L2344-2390), decode_text_to_unicode (L2392-2468).
//
// `TjBuffer::is_empty` lives with the field set as `OxTjBuffer.IsEmpty`; `OxTjBuffer` is
// not a partial class, so `append` arrives here as an extension method instead.
//
// Rust walks `chars()`, i.e. Unicode scalar values, so every place a mapping can
// yield a supplementary-plane character (CJK Ext B, emoji, plane-1 math alphanumerics)
// iterates runes here; iterating UTF-16 chars would split a surrogate pair and the
// control-character filter would judge each half separately.
using System;
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;

namespace Xberg.Internal.PdfOxide.Text;

/// <summary>Byte grouping mode for CID font character code decoding (text.rs:2260).</summary>
internal enum OxByteMode
{
    /// <summary>Single-byte codes (simple fonts, some predefined CMaps).</summary>
    OneByte,
    /// <summary>Always 2-byte codes (Identity-H/V, UCS2).</summary>
    TwoByte,
    /// <summary>Shift-JIS variable-width (1 or 2 bytes depending on lead byte).</summary>
    ShiftJIS,
}

/// <summary>
/// Reports the <c>begincodespacerange</c> code width of a /ToUnicode CMap, which
/// <see cref="OxTextDecoding.GetByteMode"/> treats as authoritative (§9.7.5) for Type0
/// fonts whose /Encoding is a CMap stream rather than a recognisable predefined name.
/// <see cref="IOxCMap"/> does not surface it, so the check is expressed as an optional
/// probe: a CMap implementation that knows its width opts in, and one that does not
/// falls through to the encoding-name rules exactly as a 1-byte CMap would.
/// </summary>
internal interface IOxCMapCodeWidth
{
    /// <summary><c>LazyCMap::code_width()</c> — 1 or 2.</summary>
    byte CodeWidth { get; }
}

/// <summary>
/// Turns the bytes of a text-showing operator into Unicode under the current font.
/// </summary>
internal static class OxTextDecoding
{
    // ---- PRESERVE_UNMAPPED_GLYPHS (text.rs:28-66) --------------------------------

    // The extractor is single-threaded per page, so a plain static is enough where the
    // Rust needs an AtomicBool.
    private static bool _preserveUnmappedGlyphs;

    /// <summary>
    /// True when the high-level accessors should preserve <c>U+FFFD</c> glyphs. The
    /// historical default is to drop them silently, which leaves a page whose visible
    /// glyphs all map to U+FFFD (the MSAM10 math-symbol font, say) extracting as empty
    /// while per-glyph extraction — which always keeps them — reports content.
    /// </summary>
    internal static bool PreserveUnmappedGlyphs => _preserveUnmappedGlyphs;

    /// <summary>Sets the flag and returns its previous value.</summary>
    internal static bool SetPreserveUnmappedGlyphs(bool preserve)
    {
        bool previous = _preserveUnmappedGlyphs;
        _preserveUnmappedGlyphs = preserve;
        return previous;
    }

    // ---- snap_run_rotation (text.rs:1928) ----------------------------------------

    /// <summary>Tolerance, in degrees, for calling a rotation a clean quadrant.</summary>
    private const float SnapTolDeg = 5.0f;

    /// <summary>
    /// Snap a run's display rotation (from the composed <c>CTM × T_m</c> rotation block,
    /// <c>θ = atan2(b, a)</c>) to the nearest of 0 / 90 / 180 / -90 when it is within
    /// <see cref="SnapTolDeg"/> of one. Mirrored text (negative determinant) keeps its raw
    /// angle: the reading-order path already treats any non-zero rotation as its own block,
    /// and snapping a mirror to a quadrant would claim it is a clean rotation when it is not.
    /// </summary>
    internal static float SnapRunRotation(in OxMatrix combined)
    {
        float a = combined.A, b = combined.B, c = combined.C, d = combined.D;

        // Pure horizontal fast path, which covers virtually all text.
        if (MathF.Abs(b) < 1e-4f && MathF.Abs(c) < 1e-4f)
        {
            return 0.0f;
        }

        float deg = MathF.Atan2(b, a) * (180.0f / MathF.PI);
        while (deg > 180.0f) deg -= 360.0f;
        while (deg <= -180.0f) deg += 360.0f;

        float det = a * d - b * c;
        if (det < 0.0f)
        {
            return MathF.Abs(deg) < SnapTolDeg ? 0.0f : deg;
        }

        foreach (float q in Quadrants)
        {
            if (MathF.Abs(deg - q) <= SnapTolDeg) return q;
        }
        return deg;
    }

    private static readonly float[] Quadrants = { 0.0f, 90.0f, 180.0f, -90.0f };

    // ---- TjBuffer (text.rs:1958-2098) --------------------------------------------

    /// <summary>
    /// ISO 32000-1 §7.3.4.2 sets an implementation limit of 32,767 bytes per string;
    /// malformed producers exceed it and the excess is what blows text up.
    /// </summary>
    private const int MaxStringBytes = 32_767;

    /// <summary>
    /// A new empty buffer capturing the current text state (<c>TjBuffer::new</c>). Everything
    /// the flush needs is pre-computed here because a font or state change ends the buffer,
    /// so none of it can shift while the run accumulates.
    /// </summary>
    internal static OxTjBuffer NewTjBuffer(OxGraphicsState state, int? mcid, OxFontInfo? cachedFont)
    {
        OxMatrix combined = state.Ctm.Multiply(state.TextMatrix);
        float effectiveFontSize =
            state.FontSize * MathF.Sqrt(combined.D * combined.D + combined.B * combined.B);
        float userHScale = MathF.Sqrt(combined.A * combined.A + combined.C * combined.C);

        bool isMonospace = false;
        if (cachedFont is not null)
        {
            if (cachedFont.Flags is { } flags && (flags & 1) != 0)
            {
                isMonospace = true;
            }
            else
            {
                string name = cachedFont.BaseFont.ToUpperInvariant();
                isMonospace = name.Contains("COURIER", StringComparison.Ordinal)
                    || name.Contains("CONSOLAS", StringComparison.Ordinal)
                    || name.Contains("MONO", StringComparison.Ordinal)
                    || name.Contains("FIXED", StringComparison.Ordinal);
            }
        }

        OxPoint textPos = state.TextMatrix.TransformPoint(0.0f, 0.0f);
        OxPoint userPos = state.Ctm.TransformPoint(textPos.X, textPos.Y);

        return new OxTjBuffer
        {
            Unicode = new StringBuilder(),
            StartMatrix = state.TextMatrix,
            FontName = state.FontName,
            FillColorRgb = state.FillColorRgb,
            CharSpace = state.CharSpace,
            WordSpace = state.WordSpace,
            HorizontalScaling = state.HorizontalScaling,
            Mcid = mcid,
            AccumulatedWidth = 0.0f,
            CachedFont = cachedFont,
            EffectiveFontSize = effectiveFontSize,
            FontWeight = cachedFont is not null && cachedFont.IsBold()
                ? OxFontWeight.Bold
                : OxFontWeight.Normal,
            IsItalic = cachedFont is not null && cachedFont.IsItalic(),
            IsMonospace = isMonospace,
            CharWidths = new List<float>(),
            UserPosX = userPos.X,
            UserPosY = userPos.Y,
            UserHScale = userHScale,
            RotationDegrees = SnapRunRotation(combined),
            Wmode = state.TextWMode,
            // Stored as a ratio of font size so it stays comparable to a font-size fraction
            // regardless of text/CTM scale (§9.3.7).
            TextRise = state.FontSize > 0.0f ? state.TextRise / state.FontSize : 0.0f,
            RenderMode = state.RenderMode,
        };
    }

    /// <summary>
    /// Append a shown string to the buffer (<c>TjBuffer::append</c>). Simple fonts take a
    /// lookup-table fast path because the full decode allocates a string per call, and a
    /// text-heavy page calls this once per showing operator.
    /// </summary>
    internal static void Append(this OxTjBuffer buffer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxStringBytes) bytes = bytes[..MaxStringBytes];

        OxFontInfo? font = buffer.CachedFont;

        if (font is not null && font.Subtype != "Type0")
        {
            // Some producers emit UTF-8 byte sequences inside PDF string literals for fonts
            // that declare only a Latin encoding and no /ToUnicode. When the whole slice is
            // valid UTF-8 and decodes to at least one non-Latin-1 code point, that cannot be
            // a coincidence of Latin-1 text, so read it as UTF-8 and recover the Cyrillic /
            // Greek / CJK instead of emitting mojibake.
            if (font.ToUnicode is null && bytes.Length >= 2)
            {
                bool hasHigh = false;
                foreach (byte b in bytes)
                {
                    if (b >= 0x80) { hasHigh = true; break; }
                }

                if (hasHigh && TryDecodeUtf8(bytes, out string decoded))
                {
                    bool anyAboveLatin1 = false;
                    foreach (Rune r in decoded.EnumerateRunes())
                    {
                        if (r.Value > 0xFF) { anyAboveLatin1 = true; break; }
                    }

                    if (anyAboveLatin1)
                    {
                        buffer.Unicode.Append(decoded);
                        return;
                    }
                }
            }

            char[] table = font.GetByteToCharTable();
            foreach (byte b in bytes)
            {
                char c = table[b];
                if (c != '\0')
                {
                    buffer.Unicode.Append(c);
                    continue;
                }

                // '\0' in the table means "multi-char mapping, U+FFFD, or unmapped" — rare
                // enough to be worth the full cascade only here.
                string? mapped = font.CharToUnicode(b);
                AppendFiltered(buffer.Unicode, mapped ?? FallbackCharToUnicode(b));
            }
            return;
        }

        // Type0 (CID) fonts and the no-font case need the full multi-byte decode.
        buffer.Unicode.Append(DecodeTextToUnicode(bytes, font));
    }

    /// <summary>
    /// Push <paramref name="s"/> unless it is the bare replacement character and the flag
    /// says to drop it, filtering the control characters a broken encoding resolution leaves
    /// behind — tab, LF and CR are legitimate whitespace and survive.
    /// </summary>
    private static void AppendFiltered(StringBuilder sink, string s)
    {
        if (s == "�" && !PreserveUnmappedGlyphs) return;

        foreach (Rune r in s.EnumerateRunes())
        {
            if (r.Value >= 0x20 || r.Value == '\t' || r.Value == '\n' || r.Value == '\r')
            {
                sink.Append(r.ToString());
            }
        }
    }

    /// <summary>
    /// Strict UTF-8 decode that reports failure rather than throwing.
    /// </summary>
    /// <remarks>
    /// PDF byte strings are frequently not UTF-8 — they are PDFDocEncoding, a CMap's raw
    /// codes, or a subset font's own byte soup — so a strict decode failing is the common
    /// case, not the exceptional one. Driving that through `DecoderFallbackException` cost a
    /// throw per string: instrumenting one two-document run counted 1,506 of them, peaking at
    /// 242/sec. `Utf8.ToUtf16` answers the same question with a status code.
    /// </remarks>
    internal static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string decoded)
    {
        if (bytes.IsEmpty) { decoded = ""; return true; }

        char[]? rented = null;
        // A UTF-8 sequence never yields more UTF-16 units than it has bytes.
        Span<char> buf = bytes.Length <= 256
            ? stackalloc char[256]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(bytes.Length));
        try
        {
            var status = System.Text.Unicode.Utf8.ToUtf16(
                bytes, buf, out _, out int written, replaceInvalidSequences: false, isFinalBlock: true);
            if (status != System.Buffers.OperationStatus.Done) { decoded = ""; return false; }
            decoded = new string(buf[..written]);
            return true;
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Rejects invalid sequences instead of substituting, as <c>str::from_utf8</c> does.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ---- fallback_char_to_unicode (text.rs:2122) ---------------------------------

    /// <summary>
    /// Last-resort mapping for a character code the standard §9.10.2 cascade could not
    /// resolve. The tiers run in order: the curated tables (1 punctuation, 2 mathematical
    /// operators, 3 Greek, 4 currency), then 5 direct Unicode for any valid scalar, then
    /// <c>"?"</c> for a code that is not one — a lone surrogate or beyond U+10FFFF.
    ///
    /// Every arm of tiers 1-4 in 0.3.77 maps a code to the identically numbered code point,
    /// so they are subsumed by tier 5; they are listed rather than collapsed so a re-sync
    /// notices the day upstream makes one of them non-identity, which would change output.
    /// </summary>
    internal static string FallbackCharToUnicode(uint charCode)
    {
        switch (charCode)
        {
            // PRIORITY 1: common punctuation (em/en dash, curly quotes, bullet, ellipsis, degree)
            case 0x2014: case 0x2013: case 0x2018: case 0x2019: case 0x201C: case 0x201D:
            case 0x2022: case 0x2026: case 0x00B0:

            // PRIORITY 2: mathematical operators (common in academic papers)
            case 0x00B1: case 0x00D7: case 0x00F7: case 0x2202: case 0x2207: case 0x220F:
            case 0x2211: case 0x221A: case 0x221E: case 0x2260: case 0x2261: case 0x2264:
            case 0x2265: case 0x222B: case 0x2248: case 0x2282: case 0x2283: case 0x2286:
            case 0x2287: case 0x2208: case 0x2209: case 0x2200: case 0x2203: case 0x2205:
            case 0x2227: case 0x2228: case 0x00AC: case 0x2192: case 0x2190: case 0x2194:
            case 0x21D2: case 0x21D4:

            // PRIORITY 3: Greek letters, both cases
            case 0x03B1: case 0x03B2: case 0x03B3: case 0x03B4: case 0x03B5: case 0x03B6:
            case 0x03B7: case 0x03B8: case 0x03B9: case 0x03BA: case 0x03BB: case 0x03BC:
            case 0x03BD: case 0x03BE: case 0x03BF: case 0x03C0: case 0x03C1: case 0x03C2:
            case 0x03C3: case 0x03C4: case 0x03C5: case 0x03C6: case 0x03C7: case 0x03C8:
            case 0x03C9:
            case 0x0391: case 0x0392: case 0x0393: case 0x0394: case 0x0395: case 0x0396:
            case 0x0397: case 0x0398: case 0x0399: case 0x039A: case 0x039B: case 0x039C:
            case 0x039D: case 0x039E: case 0x039F: case 0x03A0: case 0x03A1: case 0x03A3:
            case 0x03A4: case 0x03A5: case 0x03A6: case 0x03A7: case 0x03A8: case 0x03A9:

            // PRIORITY 4: currency symbols
            case 0x20AC: case 0x00A3: case 0x00A5: case 0x00A2: case 0x20A3: case 0x20A4:
            case 0x20A9: case 0x20AA: case 0x20AB: case 0x20B9:
                return char.ConvertFromUtf32((int)charCode);

            // PRIORITY 5: direct Unicode for anything else that is a valid scalar value.
            default:
                return Rune.IsValid(charCode)
                    ? new Rune(charCode).ToString()
                    : "?";
        }
    }

    // ---- font_has_utf8_cmap / get_byte_mode (text.rs:2279, 2292) -----------------

    /// <summary>
    /// True when a Type0 font's /Encoding is a UTF-8 (variable-width) CMap — <c>Uni-Utf8-H</c>
    /// or the Adobe predefined <c>Uni*-UTF8-H</c> family. Such codes are 1-4 bytes and must be
    /// segmented by UTF-8 lead-byte rules rather than the fixed 1/2-byte
    /// <see cref="OxByteMode"/>. Matching on the CMap name keeps this isolated to those fonts.
    /// </summary>
    internal static bool FontHasUtf8CMap(OxFontInfo font)
    {
        if (font.Subtype != "Type0") return false;
        if (!font.Encoding.IsStandard) return false;

        string lower = font.Encoding.Name!.ToLowerInvariant();
        return lower.Contains("utf8", StringComparison.Ordinal)
            || lower.Contains("utf-8", StringComparison.Ordinal);
    }

    /// <summary>How many bytes one character code occupies for this font.</summary>
    internal static OxByteMode GetByteMode(OxFontInfo? font)
    {
        if (font is null || font.Subtype != "Type0") return OxByteMode.OneByte;

        // §9.7.5: `begincodespacerange` is authoritative. A CJK font whose /Encoding is a
        // custom CMap stream matches none of the name patterns below, and reading it
        // single-byte turns CJK into Latin garbage.
        if (font.ToUnicode is IOxCMapCodeWidth cw && cw.CodeWidth == 2)
        {
            return OxByteMode.TwoByte;
        }

        if (font.Encoding.IsIdentity) return OxByteMode.TwoByte;
        if (!font.Encoding.IsStandard) return OxByteMode.OneByte;

        string name = font.Encoding.Name!;

        // OneByteIdentity is the one "Identity" that is not two bytes wide. The bare Adobe
        // predefined "H"/"V" (e.g. Adobe-Japan1-H) are 2-byte by definition; without them
        // "あいうえお" came out as "CACCCECGCI".
        if ((name.Contains("Identity", StringComparison.Ordinal)
                && !name.Contains("OneByteIdentity", StringComparison.Ordinal))
            || name.Contains("UCS2", StringComparison.Ordinal)
            || name.Contains("UTF16", StringComparison.Ordinal)
            || name == "H"
            || name == "V")
        {
            return OxByteMode.TwoByte;
        }

        if (name.Contains("RKSJ", StringComparison.Ordinal)) return OxByteMode.ShiftJIS;

        if (name.Contains("EUC", StringComparison.Ordinal)
            || name.Contains("GBK", StringComparison.Ordinal)
            || name.Contains("GBpc", StringComparison.Ordinal)
            || name.Contains("GB-", StringComparison.Ordinal)
            || name.Contains("CNS", StringComparison.Ordinal)
            || name.Contains("B5", StringComparison.Ordinal)
            || name.Contains("KSC", StringComparison.Ordinal)
            || name.Contains("KSCms", StringComparison.Ordinal))
        {
            return OxByteMode.TwoByte;
        }

        return OxByteMode.OneByte;
    }

    // ---- decode_text_to_unicode (text.rs:2392) -----------------------------------

    /// <summary>
    /// Decode a shown string to Unicode under <paramref name="font"/>, dropping the control
    /// characters a failed encoding resolution leaves behind.
    /// </summary>
    internal static string DecodeTextToUnicode(ReadOnlySpan<byte> bytes, OxFontInfo? font)
    {
        var raw = new StringBuilder(bytes.Length);

        if (font is null)
        {
            // §9.6.6: with no font at all, Latin-1 maps bytes 0x00-0xFF straight onto
            // U+0000-U+00FF, which is the spec-sanctioned guess.
            foreach (byte b in bytes) raw.Append((char)b);
        }
        else if (font.Subtype != "Type0")
        {
            char[] table = font.GetByteToCharTable();
            foreach (byte b in bytes)
            {
                char c = table[b];
                if (c != '\0')
                {
                    raw.Append(c);
                    continue;
                }

                string s = font.CharToUnicode(b) ?? FallbackCharToUnicode(b);
                if (s != "�" || PreserveUnmappedGlyphs) raw.Append(s);
            }
        }
        else if (FontHasUtf8CMap(font))
        {
            // Codes here are 1-4 bytes segmented by UTF-8 lead-byte rules, which overflow the
            // 16-bit codes TextCharIter yields. The ToUnicode CMap of such a font is keyed by
            // the same multi-byte codes, so the segmented value resolves directly.
            int n = bytes.Length;
            int i = 0;
            while (i < n)
            {
                byte lead = bytes[i];
                int width = lead switch
                {
                    <= 0x7F => 1,
                    >= 0xC0 and <= 0xDF => 2,
                    >= 0xE0 and <= 0xEF => 3,
                    >= 0xF0 and <= 0xF7 => 4,
                    _ => 1, // invalid lead byte: consume one so the walk cannot stall
                };
                width = Math.Min(width, n - i);

                uint code = 0;
                for (int k = i; k < i + width; k++) code = (code << 8) | bytes[k];

                string s = font.CharToUnicode(code) ?? FallbackCharToUnicode(code);
                if (s != "�" || PreserveUnmappedGlyphs) raw.Append(s);
                i += width;
            }
        }
        else
        {
            foreach ((ushort charCode, _) in new OxTextCharIter(bytes, font))
            {
                string s = font.CharToUnicode(charCode) ?? FallbackCharToUnicode(charCode);
                if (s != "�" || PreserveUnmappedGlyphs) raw.Append(s);
            }
        }

        string rawResult = raw.ToString();
        var filtered = new StringBuilder(rawResult.Length);
        foreach (Rune r in rawResult.EnumerateRunes())
        {
            if (r.Value >= 0x20 || r.Value == '\t' || r.Value == '\n' || r.Value == '\r')
            {
                filtered.Append(r.ToString());
            }
        }
        return filtered.ToString();
    }
}

/// <summary>
/// Walks a PDF string as character codes under the font's <see cref="OxByteMode"/>
/// (text.rs:2347), yielding the code and how many bytes it consumed.
/// </summary>
internal ref struct OxTextCharIter
{
    private readonly ReadOnlySpan<byte> _bytes;
    private readonly OxByteMode _byteMode;
    private int _index;
    private (ushort CharCode, int BytesConsumed) _current;

    internal OxTextCharIter(ReadOnlySpan<byte> bytes, OxFontInfo? font)
    {
        _bytes = bytes;
        _byteMode = OxTextDecoding.GetByteMode(font);
        _index = 0;
        _current = default;
    }

    public OxTextCharIter GetEnumerator() => this;

    public (ushort CharCode, int BytesConsumed) Current => _current;

    public bool MoveNext()
    {
        if (_index >= _bytes.Length) return false;

        ushort charCode;
        int consumed;

        if (_byteMode == OxByteMode.TwoByte && _index + 1 < _bytes.Length)
        {
            charCode = (ushort)((_bytes[_index] << 8) | _bytes[_index + 1]);
            consumed = 2;
        }
        else if (_byteMode == OxByteMode.ShiftJIS)
        {
            byte b = _bytes[_index];
            bool isLead = (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC);
            if (isLead && _index + 1 < _bytes.Length)
            {
                charCode = (ushort)((b << 8) | _bytes[_index + 1]);
                consumed = 2;
            }
            else
            {
                charCode = b;
                consumed = 1;
            }
        }
        else
        {
            // Includes a TwoByte font's trailing odd byte, which has no partner to pair with.
            charCode = _bytes[_index];
            consumed = 1;
        }

        _index += consumed;
        _current = (charCode, consumed);
        return true;
    }
}
