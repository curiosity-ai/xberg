// The extractor's lifecycle and page-level entry points, ported from
// pdf_oxide-0.3.77 src/extractors/text.rs:
//   2763-2942  new / with_config / set_page_index / current_mcid_scope / with_merging_config /
//              set_resources / set_document / take_mc_actualtext_mcids / set_excluded_layers /
//              set_excluded_inks / set_document_ptr / prepare_for_span_extraction /
//              execute_operator_public / flush_public
//   2975-3116  calculate_adaptive_tj_threshold / analyze_tj_distribution
//   3117-3176  update_artifact_state / update_layer_state / is_content_suppressed
//   3177-3308  placed_pdf_text_dominates / text_duplication_fraction
//   3309-3388  parse_artifact_type / decode_pdf_text_string / resolve_bdc_properties
//   3389-3491  resolve_color_space / is_excluded_ink_color_space / check_ocg_excluded
//   3492-3551  get_current_actual_text / peek_current_actual_text / mark_actual_text_emitted
//   3552-3611  calculate_average_glyph_width
//   3612-3751  add_font / add_font_shared / get_font_set / share_truetype_cmaps
//   3752-3929  extract_text_spans / extract / extract_into_self / extract_owned
//
// `decode_pdf_text_string`, `resolve_bdc_properties` and `check_ocg_excluded` are one-line
// delegates upstream: `optional_content.rs` owns the canonical bodies and shares them with
// the rendering path. That module is not ported, so those bodies are inlined here
// (optional_content.rs:168-370) rather than left as a seam nothing implements — the OCG
// decision is made inside the BDC handler and has to be reachable from it.
//
// A content-stream operand and a document object are one type in Rust (`Object`), so a BDC
// property list resolves the same way whether it was written inline or reached through
// /Properties. The port keeps two object models, so an inline operand dictionary is lifted
// into the document model here and everything downstream sees a single shape.
using System;
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;

namespace Xberg.Internal.PdfOxide.Text;


internal sealed partial class OxTextExtractor
{
    // ---- new / with_config (text.rs:2763-2823) -----------------------------------

    /// <summary>`TextExtractor::new` — default configuration.</summary>
    internal OxTextExtractor() : this(OxTextExtractionConfig.New())
    {
    }

    /// <summary>`TextExtractor::with_config`.</summary>
    internal OxTextExtractor(OxTextExtractionConfig config)
    {
        Config = config;
        WordBoundaryMode = config.WordBoundaryMode;

        // Span mode is the spec-compliant default; character mode is opt-in.
        ExtractSpans = true;

        // Page(0) until `SetPageIndex` says otherwise; Form XObject `Do` invocations push
        // their own scope on top of it.
        McidScopeStack.Add(OxMcidScope.Page(0));
    }

    // ---- page identity and configuration (text.rs:2825-2942) ---------------------

    /// <summary>
    /// Stamp the extractor with the page index it is processing, so spans carry the right
    /// <see cref="OxMcidScope"/> whenever no Form XObject scope sits on top of it.
    /// </summary>
    internal void SetPageIndex(int pageIndex)
    {
        // The first entry is always the page scope — `Do` pushes Form scopes above it and
        // pops them before extraction ends — so it is updated in place.
        if (McidScopeStack.Count > 0)
        {
            McidScopeStack[0] = OxMcidScope.Page(pageIndex);
        }
        else
        {
            McidScopeStack.Add(OxMcidScope.Page(pageIndex));
        }
    }

    /// <summary>The scope stamped on every new span: the top of the stack.</summary>
    internal OxMcidScope CurrentMcidScope() =>
        McidScopeStack.Count > 0 ? McidScopeStack[^1] : OxMcidScope.Page(0);

    /// <summary>Builder-style counterpart of Rust's self-consuming `with_merging_config`.</summary>
    internal OxTextExtractor WithMergingConfig(OxSpanMergingConfig mergingConfig)
    {
        MergingConfig = mergingConfig;
        return this;
    }

    internal void SetResources(PdfObject resources) => Resources = resources;

    internal void SetDocument(PdfDocument document) => Document = document;

    /// <summary>
    /// Drain the MCIDs whose BDC carried an inline /ActualText. The document layer stashes
    /// them per page so the struct-tree /ActualText applier honours MC-scope-wins precedence
    /// (§14.6, §14.9.4).
    /// </summary>
    internal HashSet<int> TakeMcActualTextMcids()
    {
        var taken = new HashSet<int>(McActualTextMcids);
        McActualTextMcids.Clear();
        return taken;
    }

    /// <summary>Layer (OCG) names whose BDC "OC" scopes are suppressed.</summary>
    internal void SetExcludedLayers(IEnumerable<string> layers)
    {
        ExcludedLayers.Clear();
        foreach (string layer in layers)
        {
            ExcludedLayers.Add(layer);
        }
    }

    /// <summary>
    /// Ink / separation names whose fill colour space suppresses text until the colour space
    /// changes. DeviceN is all-or-nothing — any matching ink in the array excludes the whole
    /// space, because tint values are not evaluated during extraction.
    /// </summary>
    internal void SetExcludedInks(IEnumerable<string> inks)
    {
        ExcludedInks.Clear();
        foreach (string ink in inks)
        {
            ExcludedInks.Add(ink);
        }
    }

    /// <summary>Convenience wrapper: identical to <see cref="SetDocument"/>.</summary>
    internal void SetDocumentPtr(PdfDocument doc) => SetDocument(doc);

    /// <summary>The <see cref="ExtractTextSpans"/> preamble on its own, for callers that
    /// drive the operator stream themselves.</summary>
    internal void PrepareForSpanExtraction()
    {
        ExtractSpans = true;
        Spans.Clear();
        SpanSequenceCounter = 0;
    }

    /// <summary>Public wrapper for the otherwise-private operator dispatch.</summary>
    internal void ExecuteOperatorPublic(OxOperator op) => ExecuteOperator(op);

    /// <summary>Public wrapper for the otherwise-private buffer flush.</summary>
    internal void FlushPublic() => FlushTjSpanBuffer();

    // ---- adaptive TJ threshold (text.rs:2975-3116) -------------------------------

    /// <summary>
    /// The TJ offset below which a positioning adjustment counts as a word break.
    ///
    /// Justified text distributes whitespace with arbitrary TJ offsets (§9.4.4), so the
    /// margin ratio triples once the offset distribution looks justified — an aggressive
    /// threshold there invents spaces out of ordinary justification kerning. With adaptive
    /// thresholds disabled this is the configured static threshold.
    /// </summary>
    internal float CalculateAdaptiveTjThreshold()
    {
        if (!Config.UseAdaptiveTjThreshold)
        {
            return Config.SpaceInsertionThreshold;
        }

        var state = StateStack.Current;
        float fontSize = state.FontSize;

        // §9.6.3 font metrics; 250 is Times-Roman's typical space advance, used when the page
        // never declared the font.
        float spaceWidthUnits = 250.0f;
        if (state.FontName is { } fontName && Fonts.TryGetValue(fontName, out var font))
        {
            spaceWidthUnits = font.GetSpaceGlyphWidth();
        }

        (bool isJustified, _) = AnalyzeTjDistribution();

        float marginRatio = isJustified
            ? Config.WordMarginRatio * 3.0f
            : Config.WordMarginRatio;

        // Font units are 1/1000 em, and the threshold is the negative offset a space has to
        // reach.
        return -((spaceWidthUnits * fontSize * marginRatio) / 1000.0f);
    }

    /// <summary>
    /// Whether the TJ offsets seen so far look justified, with the coefficient of variation
    /// they were judged by. Justified text spreads its offsets widely to fill the measure;
    /// evenly-set text does not, so CV &gt; 0.5 separates the two.
    /// </summary>
    internal (bool IsJustified, float Cv) AnalyzeTjDistribution()
    {
        int n = TjOffsetHistory.Count;
        if (n == 0)
        {
            return (false, 0.0f);
        }

        // The running accumulators are used while they still cover the history; a history
        // replaced wholesale is summed once here, in the same order, for the same result.
        double sum;
        double sumSq;
        if (TjStatsLen == n)
        {
            sum = TjSum;
            sumSq = TjSumSq;
        }
        else
        {
            sum = 0.0;
            sumSq = 0.0;
            foreach (float value in TjOffsetHistory)
            {
                double x = value;
                sum += x;
                sumSq += x * x;
            }
        }

        double nf = n;
        double mean = sum / nf;

        // E[x²] − E[x]², clamped at zero to absorb the cancellation a tiny spread causes.
        double variance = Math.Max((sumSq / nf) - (mean * mean), 0.0);
        double stdDev = Math.Sqrt(variance);

        float cv = Math.Abs(mean) > 0.001 ? (float)(stdDev / Math.Abs(mean)) : 0.0f;
        return (cv > 0.5f, cv);
    }

    // ---- marked-content derived state (text.rs:3117-3176) ------------------------

    /// <summary>Artifact state is inherited, so any ancestor being an artifact makes the
    /// current position one (§14.6).</summary>
    internal void UpdateArtifactState()
    {
        InsideArtifact = false;
        foreach (var ctx in MarkedContentStack)
        {
            if (ctx.IsArtifact)
            {
                InsideArtifact = true;
                break;
            }
        }
    }

    /// <summary>The same inheritance for excluded OCG layers and /PlacedPDF regions.</summary>
    internal void UpdateLayerState()
    {
        InsideExcludedLayer = false;
        InsidePlacedPdf = false;
        foreach (var ctx in MarkedContentStack)
        {
            if (ctx.IsExcludedLayer)
            {
                InsideExcludedLayer = true;
            }

            if (ctx.IsPlacedPdf)
            {
                InsidePlacedPdf = true;
            }
        }
    }

    /// <summary>
    /// Whether emitted content should be discarded. Artifact filtering is deliberately not
    /// checked here: it travels on span metadata and is applied downstream, because many
    /// producers mark real page content as an artifact.
    /// </summary>
    internal bool IsContentSuppressed() =>
        InsideExcludedLayer
        || InsideExcludedInk
        || (InsidePlacedPdf && !PlacedPdfKeep);

    // ---- /PlacedPDF pre-scan (text.rs:3177-3308) ---------------------------------

    /// <summary>A placed region below this many characters is a decorative figure, not a body.</summary>
    private const int MinPlacedChars = 800;

    /// <summary>Above this share of repeated words the placed region is a duplicate overlay.</summary>
    private const double MaxDupFraction = 0.5;

    private static readonly byte[] PlacedPdfTag = Encoding.ASCII.GetBytes("PlacedPDF");

    /// <summary>
    /// Decide whether a page's /PlacedPDF text is kept rather than suppressed.
    ///
    /// The suppression assumes the placed region duplicates logical text living outside it,
    /// which holds for a draft-galley overlay but not for publishers who place the entire
    /// article body in one region — there suppressing drops the page. Three gates separate
    /// them: too little placed text is a figure; placed text that dwarfs the rest is the
    /// body; otherwise it is kept only when its words are mostly absent outside.
    ///
    /// Deliberately conservative: placed text inside a nested XObject is undercounted by this
    /// page-stream scan, and gate 1 then falls back to suppression.
    /// </summary>
    internal static bool PlacedPdfTextDominates(byte[] contentStream)
    {
        // Only pages that actually carry the InDesign tag pay for a parse.
        if (!ContainsBytes(contentStream, PlacedPdfTag))
        {
            return false;
        }

        var operators = OxContentParser.ParseContentStream(contentStream);

        var placedStack = new List<bool>();
        int placedChars = 0;
        int otherChars = 0;
        var placedTxt = new List<byte>();
        var otherTxt = new List<byte>();

        bool Inside()
        {
            foreach (bool p in placedStack)
            {
                if (p)
                {
                    return true;
                }
            }
            return false;
        }

        foreach (var op in operators)
        {
            switch (op)
            {
                case OxOperator.BeginMarkedContent bmc:
                    placedStack.Add(bmc.Tag == "PlacedPDF");
                    break;

                case OxOperator.BeginMarkedContentDict bdc:
                    placedStack.Add(bdc.Tag == "PlacedPDF");
                    break;

                case OxOperator.EndMarkedContent:
                    if (placedStack.Count > 0)
                    {
                        placedStack.RemoveAt(placedStack.Count - 1);
                    }
                    break;

                case OxOperator.Tj tj:
                    Bucket(Inside(), tj.Text, ref placedChars, ref otherChars, placedTxt, otherTxt);
                    break;

                case OxOperator.Quote quote:
                    Bucket(Inside(), quote.Text, ref placedChars, ref otherChars, placedTxt, otherTxt);
                    break;

                case OxOperator.DoubleQuote dq:
                    Bucket(Inside(), dq.Text, ref placedChars, ref otherChars, placedTxt, otherTxt);
                    break;

                case OxOperator.TJ tjArray:
                {
                    bool inside = Inside();
                    var txt = inside ? placedTxt : otherTxt;
                    foreach (var element in tjArray.Array)
                    {
                        if (element is OxTextElement.Str s)
                        {
                            if (inside)
                            {
                                placedChars += s.Bytes.Length;
                            }
                            else
                            {
                                otherChars += s.Bytes.Length;
                            }
                            txt.AddRange(s.Bytes);
                        }
                    }
                    txt.Add((byte)' ');
                    break;
                }
            }
        }

        // Gate 1: too little placed text — a decorative figure, suppress.
        if (placedChars < MinPlacedChars)
        {
            return false;
        }

        // Gate 2: the placed region dominates the page — whole-body placed, keep.
        if (SaturatingMul3(otherChars) < placedChars)
        {
            return true;
        }

        // Gate 3: substantial placed text against comparable outside text. Tokenising sits
        // behind the first two gates so the common single-column page allocates nothing.
        return TextDuplicationFraction(placedTxt, otherTxt) < MaxDupFraction;
    }

    private static void Bucket(
        bool inside, byte[] text, ref int placedChars, ref int otherChars,
        List<byte> placedTxt, List<byte> otherTxt)
    {
        if (inside)
        {
            placedChars += text.Length;
            placedTxt.AddRange(text);
            placedTxt.Add((byte)' ');
        }
        else
        {
            otherChars += text.Length;
            otherTxt.AddRange(text);
            otherTxt.Add((byte)' ');
        }
    }

    private static int SaturatingMul3(int value) =>
        value > int.MaxValue / 3 ? int.MaxValue : value * 3;

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }
            if (j == needle.Length)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Fraction of the word tokens in <paramref name="a"/>, repeats counted, that also occur
    /// anywhere in <paramref name="b"/>. Tokens are lowercased runs of two or more ASCII
    /// alphanumerics; punctuation and single characters are ignored, and text with no tokens
    /// at all cannot be a duplicate of anything.
    /// </summary>
    internal static double TextDuplicationFraction(IReadOnlyList<byte> a, IReadOnlyList<byte> b)
    {
        var aTokens = Tokens(a);
        if (aTokens.Count == 0)
        {
            return 0.0;
        }

        var bSet = new HashSet<string>(Tokens(b), StringComparer.Ordinal);
        int shared = 0;
        foreach (string token in aTokens)
        {
            if (bSet.Contains(token))
            {
                shared++;
            }
        }
        return (double)shared / aTokens.Count;
    }

    private static List<string> Tokens(IReadOnlyList<byte> bytes)
    {
        var output = new List<string>();
        var current = new StringBuilder();
        foreach (byte b in bytes)
        {
            if ((b >= (byte)'0' && b <= (byte)'9')
                || (b >= (byte)'a' && b <= (byte)'z')
                || (b >= (byte)'A' && b <= (byte)'Z'))
            {
                current.Append((char)(b >= (byte)'A' && b <= (byte)'Z' ? b + 32 : b));
            }
            else if (current.Length > 0)
            {
                if (current.Length >= 2)
                {
                    output.Add(current.ToString());
                }
                current.Clear();
            }
        }
        if (current.Length >= 2)
        {
            output.Add(current.ToString());
        }
        return output;
    }

    // ---- BDC property lists (text.rs:3309-3388) ----------------------------------

    /// <summary>
    /// Classify an artifact from a BDC property list (§14.8.2.2). Some producers give a
    /// /Subtype with no /Type at all, so a bare pagination subtype still classifies.
    ///
    /// The pagination subtype is returned alongside the type because the marked-content stack
    /// can only store the type half: <see cref="OxArtifactType"/> in the spine carries no
    /// payload.
    /// </summary>
    internal static (OxArtifactType Type, OxPaginationSubtype? Subtype)? ParseArtifactType(PdfDict propsDict)
    {
        string? artifactTypeName = propsDict.Get("Type").AsName()?.ToLowerInvariant();
        string? subtypeName = propsDict.Get("Subtype").AsName()?.ToLowerInvariant();

        switch (artifactTypeName)
        {
            case "pagination":
            {
                var subtype = subtypeName switch
                {
                    "header" => OxPaginationSubtype.Header,
                    "footer" => OxPaginationSubtype.Footer,
                    "watermark" => OxPaginationSubtype.Watermark,
                    "pagenumber" or "page" => OxPaginationSubtype.PageNumber,
                    _ => OxPaginationSubtype.Other,
                };
                return (OxArtifactType.Pagination, subtype);
            }

            case "layout":
                return (OxArtifactType.Layout, null);

            case "page":
                return (OxArtifactType.Page, null);

            case "background":
                return (OxArtifactType.Background, null);

            case null:
                return subtypeName switch
                {
                    "header" => (OxArtifactType.Pagination, OxPaginationSubtype.Header),
                    "footer" => (OxArtifactType.Pagination, OxPaginationSubtype.Footer),
                    "watermark" => (OxArtifactType.Pagination, OxPaginationSubtype.Watermark),
                    _ => ((OxArtifactType, OxPaginationSubtype?)?)null,
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Decode a PDF text string (§7.9.2): UTF-16 with either BOM, otherwise UTF-8 where the
    /// bytes happen to be valid UTF-8 — non-conforming producers do emit raw UTF-8 — and
    /// PDFDocEncoding as the spec's default fallback.
    /// </summary>
    internal static string DecodePdfTextString(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return DecodeUtf16(bytes, bigEndian: true);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return DecodeUtf16(bytes, bigEndian: false);
        }

        // Same reasoning as `TryDecodeUtf8`: a PDF byte string failing a strict UTF-8 decode is
        // the ordinary case, so it is answered with a status code rather than a throw.
        if (OxTextDecoding.TryDecodeUtf8(bytes, out string utf8)) return utf8;
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                if (OxEncodingTables.PdfDocEncodingLookup(b) is { } mapped)
                {
                    sb.Append(mapped);
                }
            }
            return sb.ToString();
        }
    }

    private static string DecodeUtf16(byte[] bytes, bool bigEndian)
    {
        // Rust's `chunks_exact` drops a trailing odd byte rather than failing on it.
        int units = (bytes.Length - 2) / 2;
        var sb = new StringBuilder(units);
        for (int i = 0; i < units; i++)
        {
            byte hi = bytes[2 + (i * 2)];
            byte lo = bytes[3 + (i * 2)];
            sb.Append((char)(bigEndian ? (hi << 8) | lo : (lo << 8) | hi));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolve a BDC properties operand into a property dictionary: either the inline
    /// dictionary itself, or the /Properties resource entry a name refers to. The inline path
    /// works without a document attached, which is what makes a bare extractor usable.
    /// </summary>
    internal PdfDict? ResolveBdcProperties(OxOperand properties)
    {
        if (properties is OxOperand.Dict inline)
        {
            return DictToObject(inline);
        }

        string? propName = properties.AsName;
        if (propName is null || Resources is null || Document is null)
        {
            return null;
        }

        var resDict = Ox.Dict(Document, Resources);
        var propertiesDict = Ox.GetDict(Document, resDict, "Properties");
        return Ox.GetDict(Document, propertiesDict, propName);
    }

    /// <summary>
    /// Lift a content-stream operand into the document object model. ISO 32000-1 §7.8.2
    /// forbids streams inside a content stream, so every operand shape has a counterpart.
    /// </summary>
    private static PdfObject OperandToObject(OxOperand operand) => operand switch
    {
        OxOperand.Bool b => new PdfBool(b.Value),
        OxOperand.Integer i => new PdfNumber(i.Value, isInt: true),
        OxOperand.Real r => new PdfNumber(r.Value, isInt: false),
        OxOperand.Str s => new PdfString(s.Bytes),
        OxOperand.Name n => new PdfName(n.Value),
        OxOperand.Reference r => new PdfRef((int)r.Id, r.Gen),
        OxOperand.Array a => ArrayToObject(a),
        OxOperand.Dict d => DictToObject(d),
        _ => PdfObject.Null,
    };

    private static PdfArray ArrayToObject(OxOperand.Array array)
    {
        var result = new PdfArray();
        foreach (var item in array.Items)
        {
            result.Items.Add(OperandToObject(item));
        }
        return result;
    }

    private static PdfDict DictToObject(OxOperand.Dict dict)
    {
        var result = new PdfDict();
        foreach (var entry in dict.Entries)
        {
            result.Map[entry.Key] = OperandToObject(entry.Value);
        }
        return result;
    }

    // ---- colour spaces and ink exclusion (text.rs:3389-3491) ---------------------

    /// <summary>
    /// Resolve a named colour space from /Resources /ColorSpace. Device spaces are built in,
    /// but Separation and DeviceN spaces live in the page resources and only the array they
    /// resolve to names their inks.
    /// </summary>
    internal PdfArray? ResolveColorSpace(string name)
    {
        if (Resources is null)
        {
            return null;
        }

        var resDict = Ox.Dict(Document, Resources);
        var csDict = Ox.GetDict(Document, resDict, "ColorSpace");
        return Ox.GetArr(Document, csDict, name);
    }

    /// <summary>
    /// Whether a colour-space name resolves to an excluded ink: a Separation's ink name, or
    /// any name in a DeviceN's array. DeviceN is all-or-nothing because tint values are not
    /// evaluated here, so a process colorant sharing the definition excludes it too.
    /// </summary>
    internal bool IsExcludedInkColorSpace(string name)
    {
        if (ExcludedInks.Count == 0)
        {
            return false;
        }

        var csArray = ResolveColorSpace(name);
        if (csArray is not null && csArray.Items.Count >= 2)
        {
            // §8.6.6.2 / §8.6.6.3: the colorant slot can be an indirect reference. Some
            // subsetters share one names list across several DeviceN spaces, emitting
            // `[/DeviceN 4 0 R /DeviceCMYK <attrs>]`, so it is resolved before matching.
            switch (csArray.Items[0].AsName())
            {
                case "Separation":
                {
                    if (Ox.Name(Document, csArray.Items[1]) is { } inkName)
                    {
                        return ExcludedInks.Contains(inkName);
                    }
                    break;
                }

                case "DeviceN":
                {
                    if (Ox.Arr(Document, csArray.Items[1]) is { } inkNames)
                    {
                        foreach (var obj in inkNames.Items)
                        {
                            if (obj.AsName() is { } n && ExcludedInks.Contains(n))
                            {
                                return true;
                            }
                        }
                        return false;
                    }
                    break;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a resolved BDC property list names an excluded optional-content scope. A
    /// direct OCG is excluded when its /Name matches; an OCMD evaluates /VE when present and
    /// otherwise applies its /P policy over /OCGs membership, where an OCG is on exactly when
    /// it is not excluded (§8.11.2.4).
    /// </summary>
    internal bool CheckOcgExcluded(PdfDict propsDict)
    {
        if (Document is null)
        {
            return false;
        }

        if (propsDict.Get("Name") is { } ocgName)
        {
            return OcgNameIsExcluded(ocgName);
        }

        if (propsDict.Get("Type").AsName() == "OCMD")
        {
            // /VE takes precedence over /P.
            if (propsDict.Get("VE") is { } ve)
            {
                return !EvaluateVisibilityExpression(ve, 0);
            }

            var policy = OcmdPolicyFromName(propsDict.Get("P").AsName());

            if (propsDict.Get("OCGs") is not { } ocgs)
            {
                return false;
            }

            return OcmdIsHidden(CollectOcmdOcgNames(ocgs), policy);
        }

        return false;
    }

    /// <summary>An OCG's /Name is either a name token or a text string.</summary>
    private bool OcgNameIsExcluded(PdfObject nameObj)
    {
        if (nameObj.AsName() is { } nameStr)
        {
            return ExcludedLayers.Contains(nameStr);
        }
        if (nameObj.AsStringBytes() is { } nameBytes)
        {
            return ExcludedLayers.Contains(DecodePdfTextString(nameBytes));
        }
        return false;
    }

    /// <summary>An OCMD's visibility policy; /AnyOn is the default.</summary>
    private enum OcmdPolicy
    {
        AllOn,
        AnyOn,
        AnyOff,
        AllOff,
    }

    private static OcmdPolicy OcmdPolicyFromName(string? name) => name switch
    {
        "AllOn" => OcmdPolicy.AllOn,
        "AnyOff" => OcmdPolicy.AnyOff,
        "AllOff" => OcmdPolicy.AllOff,
        _ => OcmdPolicy.AnyOn,
    };

    /// <summary>
    /// The /Name of every OCG an OCMD's /OCGs entry reaches. The entry is either one OCG or
    /// an array of them, and references that fail to resolve are skipped.
    /// </summary>
    private List<PdfObject> CollectOcmdOcgNames(PdfObject ocgsObj)
    {
        var refs = new List<PdfObject>();
        if (Ox.Arr(Document, ocgsObj) is { } arr)
        {
            refs.AddRange(arr.Items);
        }
        else
        {
            refs.Add(ocgsObj);
        }

        var names = new List<PdfObject>(refs.Count);
        foreach (var obj in refs)
        {
            if (Ox.Dict(Document, obj) is { } d && d.Get("Name") is { } nameObj)
            {
                names.Add(nameObj);
            }
        }
        return names;
    }

    /// <summary>
    /// Apply an OCMD's /P policy to the on-state of its OCGs. An empty membership hides
    /// nothing — there is no group to be off.
    /// </summary>
    private bool OcmdIsHidden(List<PdfObject> names, OcmdPolicy policy)
    {
        if (names.Count == 0)
        {
            return false;
        }

        int on = 0;
        foreach (var name in names)
        {
            if (!OcgNameIsExcluded(name))
            {
                on++;
            }
        }
        int off = names.Count - on;

        return policy switch
        {
            OcmdPolicy.AllOn => on != names.Count,
            OcmdPolicy.AnyOn => on == 0,
            OcmdPolicy.AnyOff => off == 0,
            OcmdPolicy.AllOff => off != names.Count,
            _ => false,
        };
    }

    /// <summary>
    /// Evaluate an OCMD /VE visibility expression to visible/hidden. An operand is either an
    /// OCG dictionary — on unless its name is excluded — or a nested expression array whose
    /// first element is /And, /Or or /Not. Depth is bounded so hostile input cannot recurse
    /// without end, and every malformed shape resolves permissively rather than suppressing.
    /// </summary>
    private bool EvaluateVisibilityExpression(PdfObject expr, byte depth)
    {
        if (depth > 16)
        {
            return true;
        }

        var resolved = Ox.Resolve(Document, expr);

        if (resolved.AsDict() is { } d)
        {
            if (d.Get("Name") is { } name)
            {
                return !OcgNameIsExcluded(name);
            }
            return true;
        }

        if (resolved.AsArray() is not { } arr || arr.Items.Count == 0)
        {
            return true;
        }

        switch (arr.Items[0].AsName())
        {
            case "Not":
                return arr.Items.Count > 1 && !EvaluateVisibilityExpression(arr.Items[1], (byte)(depth + 1));

            case "And":
                for (int i = 1; i < arr.Items.Count; i++)
                {
                    if (!EvaluateVisibilityExpression(arr.Items[i], (byte)(depth + 1)))
                    {
                        return false;
                    }
                }
                return true;

            case "Or":
                for (int i = 1; i < arr.Items.Count; i++)
                {
                    if (EvaluateVisibilityExpression(arr.Items[i], (byte)(depth + 1)))
                    {
                        return true;
                    }
                }
                return false;

            default:
                return true;
        }
    }

    // ---- /ActualText (text.rs:3492-3551) ----------------------------------------

    /// <summary>
    /// The innermost active /ActualText (§14.9.4), which gives the real text for content
    /// represented non-standardly — ligatures, decorated glyphs.
    /// </summary>
    internal string? GetCurrentActualText()
    {
        for (int i = MarkedContentStack.Count - 1; i >= 0; i--)
        {
            if (MarkedContentStack[i].ActualText is { } text)
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>
    /// The innermost active /ActualText together with whether this scope already emitted it.
    ///
    /// The replacement stands for the ENTIRE marked-content sequence, so it is emitted once
    /// however many showing operators the sequence holds: the first emits and marks, and
    /// every later one suppresses emission entirely while still advancing the text matrix so
    /// outer-scope text lands correctly.
    /// </summary>
    internal (string? Text, bool AlreadyEmitted) PeekCurrentActualText()
    {
        for (int i = MarkedContentStack.Count - 1; i >= 0; i--)
        {
            var ctx = MarkedContentStack[i];
            if (ctx.ActualText is { } text)
            {
                return (text, ctx.ActualTextEmitted);
            }
        }
        return (null, false);
    }

    /// <summary>Mark the innermost scope's /ActualText as emitted.</summary>
    internal void MarkActualTextEmitted()
    {
        for (int i = MarkedContentStack.Count - 1; i >= 0; i--)
        {
            if (MarkedContentStack[i].ActualText is not null)
            {
                MarkedContentStack[i].ActualTextEmitted = true;
                return;
            }
        }
    }

    // ---- font set (text.rs:3552-3751) -------------------------------------------

    /// <summary>
    /// Mean width of a font's printable ASCII glyphs, in thousandths of an em. Falls back to
    /// the font's default width whenever the /Widths array or its bounds are absent.
    /// </summary>
    internal float CalculateAverageGlyphWidth(OxFontInfo font)
    {
        const uint PrintableAsciiStart = 32;
        const uint PrintableAsciiEnd = 126;

        if (font.Widths is not { } widths)
        {
            return font.DefaultWidth;
        }

        // Mapping a character code to a width index needs both bounds.
        if (font.FirstChar is not { } firstChar)
        {
            return font.DefaultWidth;
        }
        if (font.LastChar is not { } lastChar)
        {
            return font.DefaultWidth;
        }

        float totalWidth = 0.0f;
        int count = 0;

        for (uint charCode = PrintableAsciiStart; charCode <= PrintableAsciiEnd; charCode++)
        {
            if (charCode >= firstChar && charCode <= lastChar)
            {
                int index = (int)(charCode - firstChar);
                if (index < widths.Length)
                {
                    totalWidth += widths[index];
                    count++;
                }
            }
        }

        return count > 0 ? totalWidth / count : font.DefaultWidth;
    }

    /// <summary>Register a font resource name (e.g. "F1") with its loaded font.</summary>
    internal void AddFont(string name, OxFontInfo font) => Fonts[name] = font;

    /// <summary>
    /// The shared-font counterpart of <see cref="AddFont"/>. `FontInfo` is Arc-wrapped
    /// upstream to keep page-to-page reuse cheap; a managed reference is already shared, so
    /// the two entry points coincide and both are kept for call-site parity.
    /// </summary>
    internal void AddFontShared(string name, OxFontInfo font) => Fonts[name] = font;

    /// <summary>The current font set, for caching across pages.</summary>
    internal List<(string Name, OxFontInfo Font)> GetFontSet()
    {
        var result = new List<(string, OxFontInfo)>(Fonts.Count);
        foreach (var entry in Fonts)
        {
            result.Add((entry.Key, entry.Value));
        }
        return result;
    }

    /// <summary>
    /// Lend TrueType cmap tables between fonts sharing a base font name. A CIDFontType2
    /// Identity-H font with no embedded program has no cmap of its own, and another subset of
    /// the same face on the page can supply one.
    /// </summary>
    internal void ShareTrueTypeCmaps()
    {
        // The best cmap per stripped base font name: most glyph mappings wins, since that is
        // the widest Unicode coverage. Ties break on the smallest base font name so the
        // choice does not depend on dictionary iteration order.
        var bestCmaps = new Dictionary<string, (IOxTrueTypeCMap Cmap, string BaseFont)>(StringComparer.Ordinal);
        foreach (var font in Fonts.Values)
        {
            if (font.GetTrueTypeCMap() is not { } cmap)
            {
                continue;
            }

            string stripped = StripSubset(font.BaseFont);
            bool dominated = true;
            if (bestCmaps.TryGetValue(stripped, out var existing))
            {
                dominated = cmap.Count > existing.Cmap.Count
                    || (cmap.Count == existing.Cmap.Count
                        && string.CompareOrdinal(font.BaseFont, existing.BaseFont) < 0);
            }
            if (dominated)
            {
                bestCmaps[stripped] = (cmap, font.BaseFont);
            }
        }

        if (bestCmaps.Count == 0)
        {
            return;
        }

        foreach (var font in Fonts.Values)
        {
            if (font.HasTrueTypeCmap())
            {
                continue;
            }

            // Only Type0 CIDFontType2 with Identity encoding can use a borrowed cmap.
            if (font.Subtype != "Type0")
            {
                continue;
            }

            bool isIdentity = font.Encoding.IsIdentity
                || (font.Encoding.IsStandard
                    && font.Encoding.Name is { } encodingName
                    && encodingName.Contains("Identity", StringComparison.Ordinal));
            if (!isIdentity)
            {
                continue;
            }

            if (bestCmaps.TryGetValue(StripSubset(font.BaseFont), out var donor))
            {
                font.SetTrueTypeCmap(donor.Cmap);
            }
        }
    }

    /// <summary>Strip a subset prefix, e.g. "QQPMQK+Impact" to "Impact".</summary>
    private static string StripSubset(string name)
    {
        if (name.Length > 7 && name[6] == '+')
        {
            for (int i = 0; i < 6; i++)
            {
                if (name[i] < 'A' || name[i] > 'Z')
                {
                    return name;
                }
            }
            return name[7..];
        }
        return name;
    }

    // ---- extraction entry points (text.rs:3752-3929) ----------------------------

    /// <summary>
    /// Extract complete text spans from a content stream: the strings the PDF itself provides
    /// through Tj/TJ, post-processed into reading order.
    /// </summary>
    internal List<OxTextSpan> ExtractTextSpans(byte[] contentStream)
    {
        ExtractSpans = true;
        Spans.Clear();
        SpanSequenceCounter = 0;

        // Decided per page: whether a whole-body /PlacedPDF region must be kept.
        PlacedPdfKeep = PlacedPdfTextDominates(contentStream);

        if (ExcludedInks.Count == 0)
        {
            OxContentParser.ParseAndExecuteTextOnly(contentStream, op =>
            {
                ExecuteOperator(op);
                return true;
            });
        }
        else
        {
            // Ink filtering needs the colour operators (cs, rg, g, k) that the text-only
            // parser skips, so the full parser runs instead.
            foreach (var op in OxContentParser.ParseContentStream(contentStream))
            {
                ExecuteOperator(op);
            }
        }

        FlushTjSpanBuffer();

        // RTL draw direction is read off the raw stream order, before the reading-order sort
        // destroys it (§14.8.2.3.3 method 1).
        DetectRtlDrawDirection();

        // Super/subscript glyphs are snapped onto an adjacent baseline before the row-aware
        // sort, or every raised glyph forms its own Y-band above the body.
        SnapSuperscriptBaselines();

        SortSpansByReadingOrder();
        DeduplicateOverlappingSpans();
        MergeAdjacentSpans();

        // Resolve each span's font resource alias ("F1") to the /BaseFont name the char-level
        // API already reports. After merging, so span reconstruction still keys off the raw
        // alias exactly as before.
        var resolvedFonts = new List<string?>(Spans.Count);
        foreach (var span in Spans)
        {
            resolvedFonts.Add(
                Fonts.TryGetValue(span.FontName, out var font) && font.BaseFont.Length > 0
                    ? font.BaseFont
                    : null);
        }
        for (int i = 0; i < Spans.Count; i++)
        {
            if (resolvedFonts[i] is { } baseFont)
            {
                Spans[i].FontName = baseFont;
            }
        }

        var result = Spans;
        Spans = new List<OxTextSpan>();
        return result;
    }

    /// <summary>
    /// Extract individual positioned glyphs. Low level: <see cref="ExtractTextSpans"/> is
    /// what groups them the way the PDF's own text semantics do.
    /// </summary>
    internal List<OxTextChar> Extract(byte[] contentStream)
    {
        ExtractIntoSelf(contentStream);
        return new List<OxTextChar>(Chars);
    }

    /// <summary>Run character extraction and leave the result in <see cref="Chars"/>.</summary>
    internal void ExtractIntoSelf(byte[] contentStream)
    {
        ExtractSpans = false;
        Chars.Clear();

        // Spans are cleared too, so a stale page's spans cannot poison the XObject span cache.
        Spans.Clear();
        PlacedPdfKeep = PlacedPdfTextDominates(contentStream);

        if (ExcludedInks.Count == 0)
        {
            // Streamed rather than materialised, as the span pass does: the glyph pass runs
            // over the same content stream in the same page, and a second operator list of
            // a multi-megabyte stream is pure allocation.
            OxContentParser.ParseAndExecuteTextOnly(contentStream, op =>
            {
                ExecuteOperator(op);
                return true;
            });
        }
        else
        {
            // Ink filtering needs the colour operators the text-only parser skips.
            foreach (var op in OxContentParser.ParseContentStream(contentStream))
            {
                ExecuteOperator(op);
            }
        }

        // Content streams are in rendering order, not reading order, and some PDFs render the
        // same glyph several times for weight or shadow effects.
        SortByReadingOrder();
        DeduplicateOverlappingChars();
    }

    /// <summary>
    /// The same extraction as <see cref="Extract"/>, handing the buffer over instead of
    /// copying it. Leaves <see cref="Chars"/> empty, so callers that read it afterwards must
    /// keep using <see cref="Extract"/>.
    /// </summary>
    internal List<OxTextChar> ExtractOwned(byte[] contentStream)
    {
        ExtractIntoSelf(contentStream);
        var result = Chars;
        Chars = new List<OxTextChar>();
        return result;
    }
}
