// Port of pdf_oxide 0.3.77 `src/fonts/cid_mappings/mod.rs` (lookup_adobe_* entry points)
// and `src/fonts/cid_mappings/adobe_arabic.rs` (identity stub).
//
// CID -> Unicode for the Adobe predefined character collections (ISO 32000-1 §9.7.5.2).
// Upstream references: Adobe TN #5078 (Japan1), #5079 (GB1), #5080 (CNS1), #5093 (Korea1).
namespace Xberg.Internal.PdfOxide.Fonts;

internal static class OxCidMappings
{
    /// <summary>Adobe-GB1 (Simplified Chinese), CIDs 0-29063; corresponds to UniGB-UCS2-H.</summary>
    internal static uint? LookupAdobeGb1(ushort cid) => OxAdobeGb1.Lookup(cid);

    /// <summary>Adobe-Japan1 (Japanese), CIDs 0-23057; corresponds to UniJIS-UCS2-H.</summary>
    internal static uint? LookupAdobeJapan1(ushort cid) => OxAdobeJapan1.Lookup(cid);

    /// <summary>Adobe-CNS1 (Traditional Chinese), CIDs 0-19155; corresponds to UniCNS-UCS2-H.</summary>
    internal static uint? LookupAdobeCns1(ushort cid) => OxAdobeCns1.Lookup(cid);

    /// <summary>Adobe-Korea1 (Korean), CIDs 0-18351; corresponds to UniKS-UCS2-H.</summary>
    internal static uint? LookupAdobeKorea1(ushort cid) => OxAdobeKorea1.Lookup(cid);

    /// <summary>
    /// Adobe-Arabic-1 / Adobe-Persian-1. Identity mapping over the Arabic blocks only.
    /// </summary>
    /// <remarks>
    /// Adobe no longer publishes the registered Arabic/Persian UCS2 CMaps, so this is the
    /// §9.10.3 step-3 fallback ("emit the character code as the Unicode value") restricted to
    /// the Arabic blocks. Persian fonts (Nazanin, Yagut, Mitra, Lotus) that declare
    /// /Ordering (Persian|Arabic) commonly number their CIDs sequentially in the Arabic block,
    /// so this lands them correctly; without it they fall through to Identity-H and come out
    /// as Latin-Extended-B garbage.
    /// </remarks>
    internal static uint? LookupAdobeArabic(ushort cid)
    {
        if (cid >= 0x0600 && cid <= 0x06FF) return cid;   // Arabic
        if (cid >= 0xFB50 && cid <= 0xFDFF) return cid;   // Arabic Presentation Forms-A
        if (cid >= 0xFE70 && cid <= 0xFEFF) return cid;   // Arabic Presentation Forms-B
        return null;
    }
}
