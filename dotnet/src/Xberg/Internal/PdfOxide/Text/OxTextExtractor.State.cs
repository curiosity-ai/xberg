// Ported from pdf_oxide `extractors/text.rs`: the `TextExtractor` struct (L2572-2735),
// `TjBuffer` (L1858-1882) and `MarkedContentContext` (L2517-2528).
//
// The extractor's behaviour is split across several files as a partial class, mirroring
// the sections of the Rust file. This one owns the state they all read and write, so the
// field set has one definition and the split is invisible to the logic.
using System.Collections.Generic;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;

namespace Xberg.Internal.PdfOxide.Text;

/// <summary>
/// A run of glyphs accumulated across consecutive Tj operators. Per ISO 32000-1 §9.4.4
/// NOTE 6 a text string should be as long as possible, so showing operators are folded
/// together until a positioning command or state change forces the run to end.
/// </summary>
internal sealed class OxTjBuffer
{
    internal System.Text.StringBuilder Unicode = new();
    internal OxMatrix StartMatrix = OxMatrix.Identity;
    internal string? FontName;
    internal (float R, float G, float B) FillColorRgb;
    internal float CharSpace;
    internal float WordSpace;
    internal float HorizontalScaling = 100.0f;
    internal int? Mcid;
    internal float AccumulatedWidth;
    internal OxFontInfo? CachedFont;
    internal float EffectiveFontSize;
    internal OxFontWeight FontWeight = OxFontWeight.Normal;
    internal bool IsItalic;
    internal bool IsMonospace;
    internal List<float> CharWidths = new();
    internal float UserPosX;
    internal float UserPosY;
    internal float UserHScale;
    internal float RotationDegrees;
    internal byte Wmode;
    internal float TextRise;
    internal byte RenderMode;

    internal bool IsEmpty => Unicode.Length == 0;
}

/// <summary>
/// One level of the marked-content stack (§14.6). Artifact and optional-content state is
/// inherited, so the extractor tracks the whole stack rather than just the innermost tag.
/// </summary>
internal sealed class OxMarkedContentContext
{
    internal string Tag = "";
    internal bool IsArtifact;
    internal OxArtifactType? ArtifactType;
    internal string? ActualText;
    internal bool ActualTextEmitted;
    internal string? Expansion;
    internal bool IsExcludedLayer;
    internal bool IsPlacedPdf;
    internal int? OwnMcid;
}

internal sealed partial class OxTextExtractor
{
    /// <summary>
    /// Fraction of a glyph's advance treated as overlap when spotting a duplicate. 0.30
    /// catches the render-pass duplicates (stroke+fill, bold shadow, outline+fill), which
    /// sit well under 5% of an advance apart, while staying below the heaviest kerning so
    /// narrow neighbours like `ll` or `ii` survive.
    /// </summary>
    internal const float DedupOverlapRatio = 0.30f;

    /// <summary>
    /// Absolute cap on that window, in points, for pathologically large advances — a drop
    /// cap or display line, where 30% of the advance would swallow real neighbours.
    /// </summary>
    internal const float DedupOverlapCapPt = 2.0f;

    internal OxGraphicsStateStack StateStack = new();
    internal readonly Dictionary<string, OxFontInfo> Fonts = new();

    internal List<OxTextSpan> Spans = new();
    internal List<OxTextChar> Chars = new();

    internal PdfObject? Resources;
    internal PdfDocument? Document;

    /// <summary>
    /// Form XObjects already walked, keyed by reference *and* the CTM in force at the `Do`.
    /// The same form stamped at several positions must be walked once per position, but a
    /// form that invokes itself under an unchanged CTM must not recurse.
    /// </summary>
    internal readonly HashSet<(int Number, int Generation, long M0, long M1, long M2, long M3, long M4, long M5)>
        ProcessedXObjects = new();

    internal readonly Dictionary<string, (int Number, int Generation)?> CachedXObjectRefs = new();
    internal uint XObjectDepth;
    internal uint XObjectDecodeCount;

    internal OxTextExtractionConfig Config = OxTextExtractionConfig.New();
    internal OxSpanMergingConfig MergingConfig = OxSpanMergingConfig.New();

    internal int? CurrentMcid;

    /// <summary>MCIDs whose BDC carried inline /ActualText, so a struct-tree /ActualText
    /// does not override what the marked-content scope already said (§14.9.4).</summary>
    internal readonly HashSet<int> McActualTextMcids = new();

    internal readonly List<OxMarkedContentContext> MarkedContentStack = new();

    /// <summary>
    /// Set once a /ReversedChars sequence is seen (§14.8.2.3.3). Such producers draw RTL
    /// glyphs individually and mark real word boundaries with explicit spaces, so adding
    /// geometric word spaces on top would shatter every word.
    /// </summary>
    internal bool SawReversedChars;

    internal bool InsideArtifact;
    internal readonly HashSet<string> ExcludedLayers = new();
    internal bool InsideExcludedLayer;
    internal bool InsidePlacedPdf;

    /// <summary>
    /// Keeps /PlacedPDF text instead of suppressing it. The suppression assumes the placed
    /// region is a decorative overlay duplicating logical text outside it; some publishers
    /// place the entire article body inside one, where suppressing drops the page.
    /// </summary>
    internal bool PlacedPdfKeep;

    internal readonly HashSet<string> ExcludedInks = new();
    internal bool InsideExcludedInk;

    /// <summary>True for span extraction, false when collecting individual glyphs.</summary>
    internal bool ExtractSpans;

    internal OxTjBuffer? TjSpanBuffer;
    internal int SpanSequenceCounter;

    /// <summary>
    /// TJ offsets seen so far, with the running sums that let the distribution be judged in
    /// constant time — recomputing per offset made it quadratic in the offsets on a page.
    /// </summary>
    internal readonly List<float> TjOffsetHistory = new();
    internal double TjSum;
    internal double TjSumSq;
    internal int TjStatsLen;

    internal readonly List<CharacterInfo> TjCharacterArray = new();
    internal float CurrentXPosition;
    internal WordBoundaryMode WordBoundaryMode = WordBoundaryMode.Tiebreaker;

    /// <summary>Cached on Tf, so showing a string does not re-look-up the font each time.</summary>
    internal OxFontInfo? CachedCurrentFont;

    /// <summary>
    /// MCID scopes, innermost last (§14.7.4.3). The bottom is the page's own content
    /// stream; each Form XObject entered by `Do` pushes its own, so two forms on a page
    /// that both carry MCID 0 do not collide.
    /// </summary>
    internal readonly List<OxMcidScope> McidScopeStack = new();

    /// <summary>The fonts the space decision consults, over the loaded font set.</summary>
    internal IOxSpanFonts SpanFonts => _spanFonts ??= new LoadedFonts(this);
    private IOxSpanFonts? _spanFonts;

    /// <summary>
    /// The merger's view of the page's fonts. Resolved on demand so a bare extractor is
    /// usable without a wiring step; the merger and the space decision were ported
    /// separately and meet here rather than by direct reference.
    /// </summary>
    internal IOxSpanMergeContext MergeContext
    {
        get => _mergeContext ??= new LoadedFontContext(this);
        set => _mergeContext = value;
    }
    private IOxSpanMergeContext? _mergeContext;

    private sealed class LoadedFonts : IOxSpanFonts
    {
        private readonly OxTextExtractor _owner;
        internal LoadedFonts(OxTextExtractor owner) => _owner = owner;

        public float? SpaceGlyphWidth(string fontName) =>
            _owner.Fonts.TryGetValue(fontName, out var font) ? font.GetSpaceGlyphWidth() : null;
    }

    private sealed class LoadedFontContext : IOxSpanMergeContext
    {
        private readonly OxTextExtractor _owner;
        internal LoadedFontContext(OxTextExtractor owner) => _owner = owner;

        /// <summary>A font the page never declared counts as reliable, as upstream's
        /// `map(..).unwrap_or(true)` does.</summary>
        public bool HasExplicitWidths(string fontName) =>
            !_owner.Fonts.TryGetValue(fontName, out var font) || font.HasExplicitWidths();

        public bool SawReversedChars => _owner.SawReversedChars;
    }
}
