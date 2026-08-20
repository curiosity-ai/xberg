// Text-extraction configuration types, ported from pdf_oxide-0.3.77:
//   src/extractors/text.rs lines 58-800  (SpaceSource, SpaceDecision,
//                                         TextExtractionConfig, SpanMergingConfig)
//   src/config/extraction_profiles.rs    (DocumentType, ExtractionProfile)
//
// This file is where the whole span-merging pipeline's calibration lives: the
// numeric defaults below decide where every span breaks, so they are transcribed
// verbatim from the Rust `Default`/preset impls rather than re-derived.
namespace Xberg.Internal.PdfOxide.Text;

/// <summary>Source of a space decision in the unified pipeline (text.rs:73).</summary>
internal enum OxSpaceSource
{
    /// <summary>TJ offset value (negative offset past threshold). Confidence 0.95.</summary>
    TjOffset,

    /// <summary>Geometric gap between spans. Confidence 0.8.</summary>
    GeometricGap,

    /// <summary>Character transition heuristic (CamelCase, number-&gt;letter). Confidence 0.6.</summary>
    CharacterHeuristic,

    /// <summary>Space already present in the boundary. Confidence 1.0.</summary>
    AlreadyPresent,

    /// <summary>No space inserted.</summary>
    NoSpace,

    // Kept distinct from NoSpace so the per-line bimodal rescue can override ONLY
    // this purely-geometric suppression, never the semantic no-space rules
    // (complex-script, CJK, ligature).
    /// <summary>Suppressed by the intra-word kerning guard.</summary>
    IntraWordKerning,

    /// <summary>WordBoundaryDetector analysis. Confidence 0.85.</summary>
    WordBoundaryAnalysis,
}

/// <summary>Result of the unified space decision (text.rs:119).</summary>
internal readonly record struct OxSpaceDecision(bool InsertSpace, OxSpaceSource Source, float Confidence)
{
    /// <summary>Decide to insert a space from a specific source.</summary>
    internal static OxSpaceDecision Insert(OxSpaceSource source, float confidence) =>
        new(true, source, Math.Clamp(confidence, 0.0f, 1.0f));

    /// <summary>Decide not to insert a space.</summary>
    internal static OxSpaceDecision NoSpace(OxSpaceSource source, float confidence) =>
        new(false, source, Math.Clamp(confidence, 0.0f, 1.0f));
}

/// <summary>Document type classification for profile selection (extraction_profiles.rs:11).</summary>
internal enum OxDocumentType
{
    Academic,
    Policy,
    Government,
    Form,
    ScannedOcr,
    Mixed,
}

/// <summary>
/// Pre-tuned per-document-type thresholds (extraction_profiles.rs:50).
/// A value type in Rust (<c>Copy</c>-like <c>const</c> presets), so it is a record
/// struct here and <c>Option&lt;ExtractionProfile&gt;</c> maps to <c>OxExtractionProfile?</c>.
/// </summary>
internal readonly record struct OxExtractionProfile(
    string Name,
    float TjOffsetThreshold,
    float WordMarginRatio,
    float SpaceThresholdEmRatio,
    float SpaceCharMultiplier,
    bool UseAdaptiveThreshold,
    bool EnableDocumentTypeDetection,
    bool EnableEmailDetection,
    bool EnableCitationDetection)
{
    /// <summary>Conservative — the historical default; minimal space insertion.</summary>
    internal static readonly OxExtractionProfile Conservative = new(
        "Conservative (Default)", -120.0f, 0.1f, 0.25f, 0.5f, false, false, false, false);

    // Calibrated for producers that emit a whole paragraph as one TJ array with
    // kerning between every glyph; -100 catches the word-boundary kerning those
    // PDFs use while staying above the in-word adjustment range.
    /// <summary>TJ-heavy — Lorem-Ipsum-style single-TJ-array paragraphs.</summary>
    internal static readonly OxExtractionProfile TjHeavy = new(
        "TJ-Heavy (Lorem-Ipsum-style PDFs)", -100.0f, 0.1f, 0.25f, 0.5f, false, false, false, false);

    /// <summary>Aggressive — liberal space insertion for producers that suppress spacing.</summary>
    internal static readonly OxExtractionProfile Aggressive = new(
        "Aggressive", -80.0f, 0.2f, 0.15f, 0.8f, false, false, false, false);

    /// <summary>Balanced — middle ground for general documents.</summary>
    internal static readonly OxExtractionProfile Balanced = new(
        "Balanced", -100.0f, 0.15f, 0.2f, 0.65f, false, false, false, false);

    /// <summary>Academic — tight spacing, preserves mathematical content.</summary>
    internal static readonly OxExtractionProfile Academic = new(
        "Academic", -105.0f, 0.12f, 0.18f, 0.6f, true, false, true, true);

    /// <summary>Policy — justified, dense paragraphs (regulations, GDPR).</summary>
    internal static readonly OxExtractionProfile Policy = new(
        "Policy", -110.0f, 0.18f, 0.22f, 0.7f, true, false, false, false);

    /// <summary>Form — preserves field alignment and boundaries.</summary>
    internal static readonly OxExtractionProfile Form = new(
        "Form", -120.0f, 0.08f, 0.2f, 0.5f, false, false, false, false);

    /// <summary>Government — mixed reports, tables, structured content.</summary>
    internal static readonly OxExtractionProfile Government = new(
        "Government", -105.0f, 0.14f, 0.2f, 0.65f, true, false, false, false);

    /// <summary>Scanned OCR — lenient spacing for OCR artifacts.</summary>
    internal static readonly OxExtractionProfile ScannedOcr = new(
        "Scanned OCR", -85.0f, 0.2f, 0.15f, 0.75f, true, false, false, false);

    /// <summary>Adaptive — auto-tunes from first-page analysis.</summary>
    internal static readonly OxExtractionProfile Adaptive = new(
        "Adaptive", -100.0f, 0.15f, 0.2f, 0.65f, true, true, false, false);

    /// <summary>Profile for a specific document type.</summary>
    internal static OxExtractionProfile ForDocumentType(OxDocumentType docType) => docType switch
    {
        OxDocumentType.Academic => Academic,
        OxDocumentType.Policy => Policy,
        OxDocumentType.Government => Government,
        OxDocumentType.Form => Form,
        OxDocumentType.ScannedOcr => ScannedOcr,
        _ => Balanced,
    };

    /// <summary>Names of all selectable profiles.</summary>
    internal static string[] AllProfiles() =>
    [
        Conservative.Name, Aggressive.Name, Balanced.Name, Academic.Name, Policy.Name,
        Form.Name, Government.Name, ScannedOcr.Name, Adaptive.Name,
    ];

    /// <summary>Look a profile up by its display name; null when unknown.</summary>
    internal static OxExtractionProfile? ByName(string name) => name switch
    {
        "Conservative (Default)" => Conservative,
        "Aggressive" => Aggressive,
        "Balanced" => Balanced,
        "Academic" => Academic,
        "Policy" => Policy,
        "Form" => Form,
        "Government" => Government,
        "Scanned OCR" => ScannedOcr,
        "Adaptive" => Adaptive,
        _ => null,
    };
}

/// <summary>
/// Adaptive threshold analysis settings (pdf_oxide src/extractors/gap_statistics.rs:108).
/// Provisional: only the fields <see cref="OxSpanMergingConfig"/> carries are needed here;
/// replace with the real gap_statistics port when it lands.
/// </summary>
internal sealed record OxAdaptiveThresholdConfig
{
    /// <summary>Multiplier applied to the median gap. Default 1.5.</summary>
    internal float MedianMultiplier { get; init; } = 1.5f;

    /// <summary>Threshold floor in PDF points. Default 0.05.</summary>
    internal float MinThresholdPt { get; init; } = 0.05f;

    // Raised from the documented 1.0pt because the old ceiling clamped computed
    // thresholds far too aggressively on wide-gap layouts.
    /// <summary>Threshold ceiling in PDF points. Default 100.0.</summary>
    internal float MaxThresholdPt { get; init; } = 100.0f;

    /// <summary>Use the interquartile range instead of the median. Default false.</summary>
    internal bool UseIqr { get; init; } = false;

    /// <summary>Minimum gap samples needed for meaningful statistics. Default 10.</summary>
    internal int MinSamples { get; init; } = 10;
}

/// <summary>
/// Configuration for text extraction heuristics (text.rs:161).
/// Builder members mirror Rust's by-value <c>mut self</c> chaining: each returns a
/// modified copy so an existing configuration is never mutated in place.
/// </summary>
internal sealed record OxTextExtractionConfig
{
    /// <summary>Profile whose thresholds override the individual settings. Default none.</summary>
    internal OxExtractionProfile? Profile { get; init; }

    // -120 preserves byte-identical output for the Rust regression sweep; callers
    // hitting TJ-heavy PDFs opt into -100 via WithSpaceThreshold or the TjHeavy profile.
    /// <summary>Static TJ negative-offset threshold in text-space units. Default -120.0.</summary>
    internal float SpaceInsertionThreshold { get; init; } = -120.0f;

    /// <summary>Adaptive threshold = -(average glyph width * this). Default 0.1 (pdfplumber's word_margin).</summary>
    internal float WordMarginRatio { get; init; } = 0.1f;

    // The Rust doc comment claims a `true` default; the actual `Default` impl is
    // `false`. The code wins — flipping it moves every TJ-driven space decision.
    /// <summary>Derive the TJ threshold from font geometry. Default false.</summary>
    internal bool UseAdaptiveTjThreshold { get; init; } = false;

    /// <summary>How the word-boundary detector participates. Default Tiebreaker.</summary>
    internal WordBoundaryMode WordBoundaryMode { get; init; } = WordBoundaryMode.Tiebreaker;

    /// <summary>New configuration with default values.</summary>
    internal static OxTextExtractionConfig New() => new();

    /// <summary>Configuration with a custom static space-insertion threshold (adaptive off).</summary>
    internal static OxTextExtractionConfig WithSpaceThreshold(float threshold) => new()
    {
        Profile = null,
        SpaceInsertionThreshold = threshold,
        WordMarginRatio = 0.1f,
        UseAdaptiveTjThreshold = false,
        WordBoundaryMode = WordBoundaryMode.Tiebreaker,
    };

    /// <summary>Configuration with a custom word margin ratio (adaptive on).</summary>
    internal static OxTextExtractionConfig WithWordMarginRatio(float ratio) => new()
    {
        Profile = null,
        SpaceInsertionThreshold = -120.0f, // fallback when font metrics are missing
        WordMarginRatio = ratio,
        UseAdaptiveTjThreshold = true,
        WordBoundaryMode = WordBoundaryMode.Tiebreaker,
    };

    /// <summary>Set the word margin ratio, which also switches adaptive thresholds on.</summary>
    internal OxTextExtractionConfig SetWordMarginRatio(float ratio) =>
        this with { WordMarginRatio = ratio, UseAdaptiveTjThreshold = true };

    /// <summary>Enable or disable adaptive TJ thresholds.</summary>
    internal OxTextExtractionConfig SetAdaptiveTjThreshold(bool enabled) =>
        this with { UseAdaptiveTjThreshold = enabled };

    /// <summary>Adopt a profile and apply its thresholds.</summary>
    internal OxTextExtractionConfig WithProfile(OxExtractionProfile profile) => this with
    {
        Profile = profile,
        SpaceInsertionThreshold = profile.TjOffsetThreshold,
        WordMarginRatio = profile.WordMarginRatio,
        UseAdaptiveTjThreshold = profile.UseAdaptiveThreshold,
    };
}

/// <summary>
/// Configuration for span merging behaviour (text.rs:445). All point-valued
/// thresholds are in PDF points (1/72 inch).
/// </summary>
internal sealed record OxSpanMergingConfig
{
    /// <summary>Gap, as a multiple of font size, that triggers a space. Default 0.25 (typography's 0.25-0.33em).</summary>
    internal float SpaceThresholdEmRatio { get; init; } = 0.25f;

    // Was 0.3; regression testing showed that fused words in policy documents,
    // whose real word spacing is only 0.1-0.3pt.
    /// <summary>Floor below which a positive gap is never a space. Default 0.1.</summary>
    internal float ConservativeThresholdPt { get; init; } = 0.1f;

    /// <summary>Gap above which spans belong to different columns and never merge. Default 5.0.</summary>
    internal float ColumnBoundaryThresholdPt { get; init; } = 5.0f;

    /// <summary>Overlap worse than this is a genuine collision, not font-metric noise. Default -0.5.</summary>
    internal float SevereOverlapThresholdPt { get; init; } = -0.5f;

    /// <summary>Derive <see cref="ConservativeThresholdPt"/> from the document's gap distribution. Default true.</summary>
    internal bool UseAdaptiveThreshold { get; init; } = true;

    /// <summary>Adaptive analysis settings; null means the defaults. Only read when adaptive is on.</summary>
    internal OxAdaptiveThresholdConfig? AdaptiveConfig { get; init; }

    /// <summary>Apply email-preserving spacing rules around "user@domain" shapes. Default false.</summary>
    internal bool DetectEmailPatterns { get; init; } = false;

    /// <summary>Gap-threshold multiplier used when testing for email context. Default 2.5.</summary>
    internal float EmailThresholdMultiplier { get; init; } = 2.5f;

    /// <summary>Treat smaller-font runs as superscript citation markers. Default false.</summary>
    internal bool DetectCitationMarkers { get; init; } = false;

    /// <summary>Font-size ratio below which a run reads as a citation marker. Default 0.75.</summary>
    internal float CitationFontSizeRatio { get; init; } = 0.75f;

    // Disabling this on character-by-character-positioned PDFs (common in academic
    // typesetting) can multiply the span count per page by 100x or more.
    /// <summary>When false, every Tm starts a fresh span regardless of position. Default true.</summary>
    internal bool MergeTmTjRuns { get; init; } = true;

    /// <summary>New configuration with default values.</summary>
    internal static OxSpanMergingConfig New() => new();

    /// <summary>Lower thresholds for dense layouts (author lists, grids).</summary>
    internal static OxSpanMergingConfig Aggressive() => new()
    {
        SpaceThresholdEmRatio = 0.15f,
        ConservativeThresholdPt = 0.1f,
        ColumnBoundaryThresholdPt = 5.0f,
        SevereOverlapThresholdPt = -0.5f,
        UseAdaptiveThreshold = false,
        AdaptiveConfig = null,
        DetectEmailPatterns = false,
        EmailThresholdMultiplier = 2.5f,
        DetectCitationMarkers = false,
        CitationFontSizeRatio = 0.75f,
        MergeTmTjRuns = true,
    };

    /// <summary>Higher thresholds for formal documents with reliable spacing.</summary>
    internal static OxSpanMergingConfig Conservative() => new()
    {
        SpaceThresholdEmRatio = 0.33f,
        ConservativeThresholdPt = 0.3f, // reduced from 0.5, which fused words in policy documents
        ColumnBoundaryThresholdPt = 5.0f,
        SevereOverlapThresholdPt = -0.5f,
        UseAdaptiveThreshold = false,
        AdaptiveConfig = null,
        DetectEmailPatterns = false,
        EmailThresholdMultiplier = 2.5f,
        DetectCitationMarkers = false,
        CitationFontSizeRatio = 0.75f,
        MergeTmTjRuns = true,
    };

    /// <summary>Configuration with the four geometric thresholds supplied explicitly.</summary>
    internal static OxSpanMergingConfig Custom(
        float spaceThresholdEm,
        float conservativePt,
        float columnBoundaryPt,
        float overlapPt) => new()
    {
        SpaceThresholdEmRatio = spaceThresholdEm,
        ConservativeThresholdPt = conservativePt,
        ColumnBoundaryThresholdPt = columnBoundaryPt,
        SevereOverlapThresholdPt = overlapPt,
        UseAdaptiveThreshold = false,
        AdaptiveConfig = null,
        DetectEmailPatterns = false,
        EmailThresholdMultiplier = 2.5f,
        DetectCitationMarkers = false,
        CitationFontSizeRatio = 0.75f,
        MergeTmTjRuns = true,
    };

    /// <summary>Default thresholds with adaptive analysis explicitly configured.</summary>
    internal static OxSpanMergingConfig Adaptive() => AdaptiveWithConfig(new OxAdaptiveThresholdConfig());

    /// <summary>Default thresholds with a caller-supplied adaptive configuration.</summary>
    internal static OxSpanMergingConfig AdaptiveWithConfig(OxAdaptiveThresholdConfig adaptiveConfig) => new()
    {
        SpaceThresholdEmRatio = 0.25f,
        ConservativeThresholdPt = 0.1f, // overridden by the adaptive calculation
        ColumnBoundaryThresholdPt = 5.0f,
        SevereOverlapThresholdPt = -0.5f,
        UseAdaptiveThreshold = true,
        AdaptiveConfig = adaptiveConfig,
        DetectEmailPatterns = false,
        EmailThresholdMultiplier = 2.5f,
        DetectCitationMarkers = false,
        CitationFontSizeRatio = 0.75f,
        MergeTmTjRuns = true,
    };

    /// <summary>Fixed-threshold behaviour with adaptive analysis off, for baseline regressions.</summary>
    internal static OxSpanMergingConfig Legacy() => new()
    {
        SpaceThresholdEmRatio = 0.25f,
        ConservativeThresholdPt = 0.1f,
        ColumnBoundaryThresholdPt = 5.0f,
        SevereOverlapThresholdPt = -0.5f,
        UseAdaptiveThreshold = false,
        AdaptiveConfig = null,
        DetectEmailPatterns = false,
        EmailThresholdMultiplier = 2.5f,
        DetectCitationMarkers = false,
        CitationFontSizeRatio = 0.75f,
        MergeTmTjRuns = true,
    };
}
