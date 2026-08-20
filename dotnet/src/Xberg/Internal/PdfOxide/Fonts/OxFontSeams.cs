// Joins the seams the parallel ports declared against each other.
//
// `character_mapper.rs` reaches `cmap.rs`'s CMap through one call (`cmap.get(&code)`)
// and `cid_mappings.rs` through the per-registry lookup functions. Each module was
// ported independently against a declared shape rather than a concrete type; this is
// where those shapes meet the implementations.
using System;

namespace Xberg.Internal.PdfOxide.Fonts;

/// <summary>The one call `CharacterMapper` makes into a parsed /ToUnicode CMap.</summary>
internal sealed partial class OxCMap : IOxCMap
{
    // Explicit implementation: the port's own `Get` is internal, and an interface member
    // has to be public. Forwarding keeps the ported surface exactly as it was written.
    string? IOxCMap.Get(uint code) => Get(code);
}

internal static class OxFontSeams
{
    /// <summary>
    /// Route the character mapper's CID lookups to the Adobe registry tables. Registered
    /// once; the mapper resolves the Identity ordering itself and only reaches here for
    /// the CJK and Arabic collections.
    /// </summary>
    internal static void Install()
    {
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
