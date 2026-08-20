// Joins the seams the parallel font ports declared against each other.
//
// `font_dict.rs` reaches the glyph list, the CMap parser, the TrueType tables and
// the embedded font-program encodings by direct call; each was ported separately
// against a declared shape rather than a concrete type. This is the one place those
// shapes meet the implementations, so a port stays readable against its Rust source
// and no module has to know how another was built.
using System;
using System.Collections.Generic;
using System.Text;

using System.Threading;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>Registers every font seam with the module that implements it.</summary>
internal static class OxFontWiring
{
    private static readonly object InstallGate = new();

    /// <summary>
    /// Fill in any font seam that is not yet wired. Safe to call from any thread and on
    /// every font load — the seams are process-wide statics and a font may be built from
    /// anywhere.
    /// </summary>
    internal static void Install()
    {
        // Every assignment below is `??=`, so this fills whatever is missing and leaves the
        // rest alone. There is deliberately no "already ran" flag: a flag makes the call a
        // no-op forever after, and anything that clears a seam later — a test swapping in a
        // stub, most obviously — can then never get the real one back.
        lock (InstallGate)
        {

            OxFontSeams.GlyphNames ??= new GlyphNamesSeam();
            OxFontSeams.CMaps ??= new CMapSeam();
            OxFontSeams.TrueType ??= new TrueTypeSeam();
            OxFontSeams.FontPrograms ??= new FontProgramSeam();
            OxFontSeams.EncodingTables ??= new EncodingTablesSeam();
            OxFontSeams.PredefinedCidUnicode ??= new PredefinedCidSeam();

            OxCharacterMapper.CidMappingLookup ??= static (ordering, cid) => ordering switch
            {
                "GB1" => OxCidMappings.LookupAdobeGb1(cid),
                "Japan1" => OxCidMappings.LookupAdobeJapan1(cid),
                "CNS1" => OxCidMappings.LookupAdobeCns1(cid),
                "Korea1" => OxCidMappings.LookupAdobeKorea1(cid),
                "Arabic" or "Persian" => OxCidMappings.LookupAdobeArabic(cid),
                _ => null,
            };
        }
    }

    /// <summary>
    /// Lets `CharacterMapper` be fed a parsed /ToUnicode CMap. The mapper reaches a CMap
    /// for one lookup, so it declares a narrower seam than the font dictionary does.
    /// </summary>
    internal static IOxToUnicodeLookup AsToUnicodeLookup(OxCMap cmap) => new ToUnicodeAdapter(cmap);

    private sealed class ToUnicodeAdapter : IOxToUnicodeLookup
    {
        private readonly OxCMap _cmap;
        internal ToUnicodeAdapter(OxCMap cmap) => _cmap = cmap;
        public string? Get(uint code) => _cmap.Get(code);
    }

    /// <summary>
    /// The glyph-name tiers resolve to runes, since the uXXXXX form reaches the
    /// supplementary planes; a caller wanting a char only gets one when the result fits.
    /// </summary>
    private sealed class GlyphNamesSeam : IOxGlyphNames
    {
        public char? GlyphNameToUnicode(string glyphName) => AsChar(OxGlyphNames.GlyphNameToUnicode(glyphName));

        public string? GlyphNameToUnicodeString(string glyphName) =>
            OxGlyphNames.GlyphNameToUnicodeString(glyphName);

        public string? MapGlyphNameToUnicodeString(string glyphName) =>
            OxGlyphNames.GlyphNameToUnicodeUnified(glyphName);

        public char? AdobeGlyphListLookup(string glyphName) =>
            OxGlyphNames.TryLookupAgl(glyphName, out char value) ? value : null;

        private static char? AsChar(Rune? rune) =>
            rune is { } r && r.Utf16SequenceLength == 1 ? (char)r.Value : null;
    }

    private sealed class CMapSeam : IOxCMapProvider
    {
        public IOxCMap CreateLazy(byte[] rawStream) => new LazyCMapAdapter(new OxLazyCMap(rawStream));

        public byte? ParseWModeDirective(string cmapText) => OxCMap.ParseWModeDirective(cmapText);
    }

    /// <summary>
    /// Wraps the lazy CMap so the first lookup is what triggers parsing — a font whose
    /// /ToUnicode is never consulted must not pay for it.
    /// </summary>
    /// <remarks>
    /// Also carries the code width, which the byte-mode decision treats as authoritative
    /// before any encoding-name rule: a CJK font whose CMap declares a two-byte codespace
    /// must be read two bytes at a time even when its /Encoding name says nothing.
    /// </remarks>
    private sealed class LazyCMapAdapter : IOxCMap, Text.IOxCMapCodeWidth
    {
        private readonly OxLazyCMap _lazy;
        internal LazyCMapAdapter(OxLazyCMap lazy) => _lazy = lazy;

        public bool IsParsed => _lazy.Get() is not null;
        public int Count => _lazy.Get()?.Count ?? 0;
        public string? Lookup(uint code) => _lazy.Get()?.Get(code);
        public byte Wmode => _lazy.WMode();
        public byte CodeWidth => _lazy.CodeWidth();
    }

    private sealed class TrueTypeSeam : IOxTrueTypeProvider
    {
        public IOxTrueTypeCMap? CMapFromFontData(byte[] fontData)
        {
            var cmap = OxTrueTypeCMap.FromFontData(fontData);
            return cmap is null ? null : new TrueTypeCMapAdapter(cmap);
        }

        /// <summary>
        /// The TrueType port reads the `cmap` table, not `post` glyph names, so this tier
        /// is unavailable and the caller falls through to the next one.
        /// </summary>
        public IReadOnlyList<string?>? GlyphNames(byte[] fontData) => null;
    }

    private sealed class TrueTypeCMapAdapter : IOxTrueTypeCMap
    {
        private readonly OxTrueTypeCMap _cmap;
        internal TrueTypeCMapAdapter(OxTrueTypeCMap cmap) => _cmap = cmap;

        public bool IsEmpty => _cmap.IsEmpty;
        public int Count => _cmap.Count;
        public ushort? CodeToGid(ushort code) => _cmap.CodeToGid(code);

        // Format 12 maps past the BMP, where the seam's char cannot follow.
        public char? GetUnicode(ushort gid) =>
            _cmap.GetUnicode(gid) is { } cp && cp <= 0xFFFF ? (char)cp : null;
    }

    private sealed class FontProgramSeam : IOxFontProgramEncodings
    {
        public IReadOnlyDictionary<byte, char>? ParseType1Encoding(byte[] fontData) =>
            ToCharMap(OxType1Encoding.ParseType1Encoding(fontData));

        public IReadOnlyDictionary<byte, char>? ParseCffEncoding(byte[] fontData) =>
            ToCharMap(OxCffEncoding.ParseCffEncoding(fontData));

        public IReadOnlyDictionary<byte, ushort>? ParseCffGidMappingWithPdfEncoding(
            byte[] fontData, OxEncoding pdfEncoding, IReadOnlyDictionary<byte, string> differences) =>
            OxCffEncoding.ParseCffGidMappingWithPdfEncoding(fontData, ToPdfEncoding(pdfEncoding), differences);

        /// <summary>
        /// Both encoding parsers answer in runes; a built-in encoding slot that needs a
        /// surrogate pair has no byte-sized answer, so it is dropped rather than truncated.
        /// </summary>
        private static IReadOnlyDictionary<byte, char>? ToCharMap(Dictionary<byte, Rune>? source)
        {
            if (source is null)
            {
                return null;
            }

            var map = new Dictionary<byte, char>(source.Count);
            foreach (var (code, rune) in source)
            {
                if (rune.Utf16SequenceLength == 1)
                {
                    map[code] = (char)rune.Value;
                }
            }

            return map;
        }

        /// <summary>
        /// Both sides mean the same thing — /BaseEncoding merged with /Differences, keyed by
        /// byte — but the font dictionary resolved it to chars and the CFF reader works in
        /// runes.
        /// </summary>
        private static OxPdfEncoding ToPdfEncoding(OxEncoding encoding) => encoding.EncodingKind switch
        {
            OxEncoding.Kind.Standard => OxPdfEncoding.Standard(encoding.Name ?? ""),
            OxEncoding.Kind.Custom => OxPdfEncoding.Custom(ToRuneMap(encoding.Map)),
            _ => OxPdfEncoding.Identity,
        };

        private static IReadOnlyDictionary<byte, Rune> ToRuneMap(Dictionary<byte, char>? source)
        {
            var map = new Dictionary<byte, Rune>(source?.Count ?? 0);
            if (source is null)
            {
                return map;
            }

            foreach (var (code, value) in source)
            {
                if (Rune.TryCreate(value, out Rune rune))
                {
                    map[code] = rune;
                }
            }

            return map;
        }
    }

    private sealed class EncodingTablesSeam : IOxEncodingTables
    {
        public string? StandardEncodingLookup(string encoding, byte code) =>
            OxEncodingTables.StandardEncodingLookup(encoding, code);
    }

    private sealed class PredefinedCidSeam : IOxPredefinedCidUnicode
    {
        public uint? LookupAdobeGb1(ushort cid) => OxCidMappings.LookupAdobeGb1(cid);
        public uint? LookupAdobeJapan1(ushort cid) => OxCidMappings.LookupAdobeJapan1(cid);
        public uint? LookupAdobeCns1(ushort cid) => OxCidMappings.LookupAdobeCns1(cid);
        public uint? LookupAdobeKorea1(ushort cid) => OxCidMappings.LookupAdobeKorea1(cid);
        public uint? LookupAdobeArabic(ushort cid) => OxCidMappings.LookupAdobeArabic(cid);
    }
}
