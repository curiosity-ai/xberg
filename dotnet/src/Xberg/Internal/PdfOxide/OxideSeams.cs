// Seams between ported modules that would otherwise force one huge file.
//
// The span merger consults the page's fonts for exactly one thing — the width of
// each font's space glyph, which sets the geometric threshold a gap has to clear
// to count as a word break. Expressing that as an interface keeps the merger
// independent of how fonts are loaded.
namespace Xberg.Internal.PdfOxide;

/// <summary>What the space decision needs to know about the fonts a page used.</summary>
internal interface IOxSpanFonts
{
    /// <summary>
    /// The font's space-glyph advance in 1/1000 em (`FontInfo::get_space_glyph_width`), or
    /// null when the page declared no font by that name — which upstream distinguishes,
    /// falling back to a fraction of the font size and disabling the kerning guard.
    /// </summary>
    float? SpaceGlyphWidth(string fontName);
}
