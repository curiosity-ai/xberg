// Port of pdf_oxide 0.3.77 `src/fonts/predefined_cidfont.rs`
// (CharacterCollection, KNOWN_CMAP_SUFFIXES, strip_cmap_suffix, collection_for_bare_name, is_predefined).
//
// ISO 32000-2:2020 §9.7.5.2 requires a conforming processor to support the Adobe-CNS1,
// Adobe-GB1, Adobe-Japan1 and Adobe-KR collections. In practice that means recognising one of
// Adobe's well-known CIDFont base names on a font with no /FontFile* and materialising glyphs
// from a covering face; this module is the name -> character-collection registry that gate uses.
namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>One of the four Adobe predefined character collections this port recognises.</summary>
internal enum OxCharacterCollection
{
    /// <summary>Adobe-Japan1: JIS X 0208 / 0212, kana, kanji.</summary>
    AdobeJapan1,
    /// <summary>Adobe-GB1 (Simplified Chinese): GB 2312 + extensions.</summary>
    AdobeGB1,
    /// <summary>Adobe-CNS1 (Traditional Chinese): CNS 11643, Big5.</summary>
    AdobeCNS1,
    /// <summary>Adobe-Korea1 (Korean): KS X 1001.</summary>
    AdobeKorea1,
}

internal static class OxPredefinedCidFont
{
    /// <summary>
    /// Unicode code point for <paramref name="cid"/> under <paramref name="collection"/>, or null
    /// when the CID is outside the collection's table (caller should paint .notdef but keep the
    /// advance width).
    /// </summary>
    internal static uint? CidToUnicode(this OxCharacterCollection collection, ushort cid) => collection switch
    {
        OxCharacterCollection.AdobeJapan1 => OxCidMappings.LookupAdobeJapan1(cid),
        OxCharacterCollection.AdobeGB1 => OxCidMappings.LookupAdobeGb1(cid),
        OxCharacterCollection.AdobeCNS1 => OxCidMappings.LookupAdobeCns1(cid),
        OxCharacterCollection.AdobeKorea1 => OxCidMappings.LookupAdobeKorea1(cid),
        _ => null,
    };

    /// <summary>
    /// Adobe predefined CMap suffixes producers append to a base font name to form a
    /// combined-resource Type0 reference (<c>Ryumin-Light-Identity-V</c>, <c>STSong-Light-GBK-EUC-H</c>).
    /// </summary>
    /// <remarks>
    /// Sources: ISO 32000-1 Annex F plus Adobe TN #5078 / #5079 / #5080 / #5093. The single-letter
    /// legacy `H` / `V` names are genuine CMaps so they cannot be dropped, but longest-match wins
    /// in <see cref="StripCMapSuffix"/> so they never pre-empt a longer suffix.
    /// </remarks>
    private static readonly string[] KnownCMapSuffixes =
    [
        // Generic identity
        "Identity-H", "Identity-V",
        // Adobe-Japan1 CMaps (TN #5078)
        "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V", "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        "UniJIS-UTF8-H", "UniJIS-UTF8-V", "UniJIS-X0213-UTF32-H", "UniJIS-X0213-UTF32-V", "UniJIS-X02132004-UTF32-H", "UniJIS-X02132004-UTF32-V",
        "UniJISPro-UCS2-HW-V", "UniJISPro-UCS2-V", "UniJISX0213-UTF32-H", "UniJISX0213-UTF32-V", "UniJISX02132004-UTF32-H", "UniJISX02132004-UTF32-V",
        "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H", "90pv-RKSJ-V",
        "78ms-RKSJ-H", "78ms-RKSJ-V", "83pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H",
        "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V", "WP-Symbol",
        "Hojo-EUC-H", "Hojo-EUC-V", "Hojo-H", "Hojo-V", "Hankaku", "Hiragana",
        "Katakana", "Roman",
        // Adobe-GB1 CMaps (TN #5079)
        "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V", "UniGB-UTF8-H", "UniGB-UTF8-V",
        "GB-EUC-H", "GB-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBK2K-H", "GBK2K-V",
        "GBKp-EUC-H", "GBKp-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBT-EUC-H", "GBT-EUC-V",
        "GBT-H", "GBT-V", "GBTpc-EUC-H",
        // Adobe-CNS1 CMaps (TN #5080)
        "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H", "UniCNS-UTF16-V", "UniCNS-UTF8-H", "UniCNS-UTF8-V",
        "B5pc-H", "B5pc-V", "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V",
        "CNS-EUC-H", "CNS-EUC-V", "HKscs-B5-H", "HKscs-B5-V",
        // Adobe-Korea1 CMaps (TN #5093)
        "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V", "UniKS-UTF8-H", "UniKS-UTF8-V",
        "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
        "KSCpc-EUC-H",
    ];

    // Adobe-Japan1 — Mincho / Gothic family + Heisei + Kozuka
    private static readonly HashSet<string> Japan1Names = new(StringComparer.Ordinal)
    {
        "Ryumin-Light", "Ryumin-Medium", "Ryumin-Regular", "Ryumin-Heavy",
        "Ryumin-Bold", "Ryumin-Ultra", "GothicBBB-Medium", "GothicMB101-Bold",
        "FutoGoB101-Bold", "FutoMinA101-Bold", "Jun101-Light", "MidashiGo-MB31",
        "MidashiMin-MA31", "HeiseiMin-W3", "HeiseiMin-W5", "HeiseiMin-W7",
        "HeiseiMin-W9", "HeiseiKakuGo-W3", "HeiseiKakuGo-W5", "HeiseiKakuGo-W7",
        "HeiseiKakuGo-W9", "HeiseiMaruGo-W4", "KozMinPro-Regular", "KozMinPro-Light",
        "KozMinPro-Medium", "KozMinPro-Bold", "KozMinPro-Heavy", "KozMinProVI-Regular",
        "KozMinProVI-Light", "KozMinProVI-Medium", "KozMinProVI-Bold", "KozMinProVI-Heavy",
        "KozGoPro-Regular", "KozGoPro-Light", "KozGoPro-Medium", "KozGoPro-Bold",
        "KozGoPro-Heavy", "KozGoProVI-Regular", "KozGoProVI-Light", "KozGoProVI-Medium",
        "KozGoProVI-Bold", "KozGoProVI-Heavy", "Kozuka-Mincho-Pro-VI-R", "Kozuka-Gothic-Pro-VI-M",
    };

    // Adobe-GB1 — STSong / STHeiti / SimSun / SimHei
    private static readonly HashSet<string> Gb1Names = new(StringComparer.Ordinal)
    {
        "STSong-Light", "STSongStd-Light", "STSong-Regular", "STSongStd-Regular",
        "STHeiti-Regular", "STHeiti-Light", "STHeitiSC-Regular", "STHeitiSC-Light",
        "STKaiti-Regular", "STKaitiStd-Regular", "STFangsong-Light", "STFangsong-Regular",
        "SimSun", "SimHei", "SimSun-ExtB", "AdobeSongStd-Light",
        "AdobeHeitiStd-Regular", "AdobeKaitiStd-Regular", "AdobeFangsongStd-Regular",
    };

    // Adobe-CNS1 — Traditional Chinese (MHei / MSung / MingLiU / DFKai)
    private static readonly HashSet<string> Cns1Names = new(StringComparer.Ordinal)
    {
        "MHei-Medium", "MSung-Light", "MSung-Medium", "MSungStd-Light",
        "MSungStd-Medium", "MSungStd-Light-Acro", "MingLiU", "PMingLiU",
        "MingLiU-ExtB", "PMingLiU-ExtB", "DFKaiShu-SB-Estd-BF", "DFKaiSho-W5",
        "HeiseiKakuGoStd-W5", "AdobeMingStd-Light", "AdobeFanHeitiStd-Bold", "AdobeSongStd-Bold",
    };

    // Adobe-Korea1 — Korean (HYSMyeongJo / HYGoThic / Adobe-Myungjo)
    private static readonly HashSet<string> Korea1Names = new(StringComparer.Ordinal)
    {
        "HYSMyeongJo-Medium", "HYSMyeongJoStd-Medium", "HYGoThic-Medium", "HYGothic-Medium",
        "HYGothic-Bold", "HYGothicStd-Medium", "HYRGoThic-Medium", "HYHeadLine-Medium",
        "Adobe-MyungjoStd-Medium", "AdobeMyungjoStd-Medium", "AdobeGothicStd-Bold", "Batang",
        "BatangChe", "Dotum", "DotumChe", "Gulim",
        "GulimChe", "Gungsuh", "GungsuhChe",
    };

    /// <summary>
    /// Strip a trailing <c>-&lt;suffix&gt;</c> when the suffix is a recognised Adobe CMap name,
    /// preferring the longest match; returns <paramref name="name"/> unchanged when nothing matches.
    /// </summary>
    /// <remarks>
    /// The hyphen is part of the match so a hyphenless base name is never truncated — `Ryumin-Light`
    /// itself ends in `Light`, which must not be consumed. Longest-wins is what makes
    /// `STSong-Light-GBK-EUC-H` yield `STSong-Light` rather than the unresolvable
    /// `STSong-Light-GBK-EUC` that the short `-H` suffix would leave.
    /// </remarks>
    internal static string StripCMapSuffix(string name)
    {
        string? best = null;
        int bestLen = 0;
        foreach (string suffix in KnownCMapSuffixes)
        {
            int trailerLen = suffix.Length + 1;
            if (trailerLen <= bestLen || name.Length <= suffix.Length) continue;
            if (name[name.Length - trailerLen] != '-') continue;
            if (string.CompareOrdinal(name, name.Length - suffix.Length, suffix, 0, suffix.Length) != 0) continue;
            bestLen = trailerLen;
            best = name.Substring(0, name.Length - trailerLen);
        }
        return best ?? name;
    }

    /// <summary>Bare base-font name to character collection; null when unregistered.</summary>
    /// <remarks>
    /// Hand-curated and conservative: adding a name here promises the renderer will substitute
    /// fallback glyphs for it when the source PDF ships no outlines.
    /// </remarks>
    private static OxCharacterCollection? CollectionForBareName(string name)
    {
        if (Japan1Names.Contains(name)) return OxCharacterCollection.AdobeJapan1;
        if (Gb1Names.Contains(name)) return OxCharacterCollection.AdobeGB1;
        if (Cns1Names.Contains(name)) return OxCharacterCollection.AdobeCNS1;
        if (Korea1Names.Contains(name)) return OxCharacterCollection.AdobeKorea1;
        return null;
    }

    /// <summary>
    /// Decide whether <paramref name="baseFont"/> names a predefined Adobe CIDFont this port can
    /// substitute glyphs for, returning its character collection or null.
    /// </summary>
    /// <remarks>
    /// Name recognition is necessary but not sufficient: the caller must additionally require the
    /// font's /Encoding to resolve to an Identity charcode-to-CID mapping. Non-Identity predefined
    /// CMaps (90ms-RKSJ-H, GBK-EUC-H, B5pc-H, …) put raw Shift-JIS / EUC / Big5 codes — not CIDs —
    /// in the content stream, which the CID-to-Unicode tables would mis-decode.
    /// </remarks>
    internal static OxCharacterCollection? IsPredefined(string baseFont)
    {
        // The PDF subset prefix is exactly six ASCII uppercase letters then '+'. Only strip on that
        // exact shape so Asian-tooling base names that legitimately contain '+' survive intact.
        string afterPrefix = baseFont;
        if (baseFont.IndexOf('+') == 6)
        {
            bool allUpper = true;
            for (int i = 0; i < 6; i++)
            {
                if (baseFont[i] < 'A' || baseFont[i] > 'Z') { allUpper = false; break; }
            }
            if (allUpper) afterPrefix = baseFont.Substring(7);
        }
        return CollectionForBareName(StripCMapSuffix(afterPrefix));
    }
}
