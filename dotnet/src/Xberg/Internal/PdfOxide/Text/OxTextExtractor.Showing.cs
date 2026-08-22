// Text showing and advance, ported from pdf_oxide-0.3.77 src/extractors/text.rs:
//   7415-7535  flush_tj_buffer
//   7536-7558  process_tj_array
//   7559-7759  process_tj_array_tiebreaker
//   7760-7812  process_tj_array_primary
//   7813-7830  create_boundary_context
//   7831-7864  partition_characters_by_boundaries
//   7865-8069  cluster_to_span
//   8070-8086  is_ligature_code
//   8087-8179  apply_ligature_decisions
//   8180-8280  advance_position_for_string
//   8281-8470  append_and_advance
//   8471-8667  append_advance_buffer
//   8668-8805  insert_space_as_span
//   8806-8832  advance_position_for_offset
//   8833-8850  fold_offset_into_buffer
//   8851-8974  flush_tj_span_buffer
//   8975-9180  show_text
//   9181-9189  char_count / clear
//
// This is where a `Tj`/`TJ` operator turns into geometry: every advance formula of
// ISO 32000-1 §9.4.4 lives here, and the three near-duplicate advance loops
// (`advance_position_for_string`, `append_and_advance`, `append_advance_buffer`) are
// kept apart exactly as upstream keeps them — they disagree in small ways that are
// load-bearing (the word-spacing gate on multi-byte codes, whether the UTF-8 repair
// path updates `accumulated_width`), and collapsing them moves span geometry.
//
// Rust measures `String::len()` in UTF-8 bytes, and the per-glyph width split keys
// off that count, so the port counts UTF-8 bytes there too rather than UTF-16 units.
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Content;
using Xberg.Internal.PdfOxide.Fonts;

namespace Xberg.Internal.PdfOxide.Text;

/// <summary>
/// `text::bidi::detect_visual_order_run` + `apply_rtl_verdict`, which live in a module
/// outside this port. A null <paramref name="charsWithX"/> stands for the callers that
/// cannot build per-character positions and so force the `Ambiguous` verdict.
/// </summary>
internal delegate string OxApplyRtlVerdictFn(
    string text,
    IReadOnlyList<(int CodePoint, float X)>? charsWithX,
    bool coarseVisualOrderHeuristic,
    bool isInvisibleRenderMode);

/// <summary>
/// `LigatureDecisionMaker::decide` (text/ligature_processor.rs) — true when the ligature
/// at <paramref name="ligature"/> should be expanded to its components.
/// </summary>
internal delegate bool OxLigatureSplitFn(CharacterInfo ligature, BoundaryContext context, CharacterInfo? next);

/// <summary>
/// `expand_ligature_to_chars` — the component characters of a ligature, each carrying an
/// equal share of the ligature's advance. Empty when the character is not a ligature.
/// </summary>
internal delegate IReadOnlyList<(char Char, float Width)> OxExpandLigatureFn(char ligature, float originalWidth);

/// <summary>
/// `PatternDetector::mark_pattern_contexts` — marks the characters of an email address or
/// URL as protected so word-boundary detection cannot split them.
/// </summary>
internal delegate void OxMarkPatternContextsFn(List<CharacterInfo> characters);

/// <summary>
/// The marked-content and configuration questions a showing operator asks that are answered
/// elsewhere in `TextExtractor`: which MCID scope and artifact type a span is born into,
/// whether the current content is suppressed entirely, and how negative a TJ offset must be
/// to count as a word gap.
/// </summary>
internal interface IOxShowingContext
{
    /// <summary>`current_mcid_scope` (text.rs:2845).</summary>
    OxMcidScope CurrentMcidScope();

    /// <summary>`current_artifact_type` (text.rs:7404) — the innermost enclosing artifact.</summary>
    OxArtifactType? CurrentArtifactType();

    /// <summary>`is_content_suppressed` (text.rs:3147).</summary>
    bool IsContentSuppressed();

    /// <summary>`calculate_adaptive_tj_threshold` (text.rs:2975).</summary>
    float CalculateAdaptiveTjThreshold();
}

internal sealed partial class OxTextExtractor
{
    // State, OxTjBuffer and the glyph decode live in OxTextExtractor.State.cs and
    // OxTextExtractor.Decoding.cs.

    /// <summary>Seam to the separately-ported bidi module; see <see cref="OxApplyRtlVerdictFn"/>.</summary>
    internal OxApplyRtlVerdictFn ApplyRtlVerdict = DefaultApplyRtlVerdict;

    /// <summary>Seam to `text::ligature_processor`; see <see cref="OxLigatureSplitFn"/>.</summary>
    internal OxLigatureSplitFn LigatureShouldSplit = DefaultLigatureShouldSplit;

    /// <summary>Seam to `text::ligature_processor`; see <see cref="OxExpandLigatureFn"/>.</summary>
    internal OxExpandLigatureFn ExpandLigatureToChars = DefaultExpandLigatureToChars;

    /// <summary>
    /// Seam to `extractors::pattern_detector`, which is not ported. Until it is, no
    /// character is marked protected, which is what a `PatternPreservationConfig` with
    /// pattern preservation switched off would also do.
    /// </summary>
    internal OxMarkPatternContextsFn MarkPatternContexts = static _ => { };

    /// <summary>
    /// The marked-content and threshold questions the showing path asks; resolved on demand
    /// so a bare extractor is usable, as <see cref="MergeContext"/> is.
    /// </summary>
    internal IOxShowingContext ShowingContext
    {
        get => _showingContext ??= new DefaultShowingContext(this);
        set => _showingContext = value;
    }
    private IOxShowingContext? _showingContext;

    // ---- flush_tj_buffer (text.rs:7415) ------------------------------------------

    /// <summary>
    /// Turn one accumulated TJ run into a single span. Everything the span needs was
    /// captured when the buffer was created, because any change to font, colour or text
    /// state ends the buffer — so nothing read here can have moved since.
    /// </summary>
    internal void FlushTjBuffer(OxTjBuffer buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        // accumulated_width is text space; user_h_scale carries it to user space.
        float totalWidth = buffer.AccumulatedWidth * buffer.UserHScale;

        float effectiveFontSize = buffer.EffectiveFontSize;
        OxFontWeight fontWeight = buffer.FontWeight;
        bool isItalicSpan = buffer.IsItalic;

        string fontNameSpan = buffer.FontName ?? "Unknown";
        buffer.FontName = null;

        string text = buffer.Unicode.ToString();
        buffer.Unicode = new StringBuilder();

        // #826: the geometric visual-order detector when char_widths gives a position per
        // character, and only otherwise the coarse "net advance is positive" heuristic —
        // accumulated_width only ever sums positive glyph advances, so that heuristic is
        // true of nearly every RTL buffer and on its own reverses runs that were already
        // stored in logical order.
        if (Utf8ByteLen(text) > 1)
        {
            if (AnyRtl(text))
            {
                List<Rune> chars = RunesOf(text);
                List<(int CodePoint, float X)>? charsWithX = null;
                if (chars.Count == buffer.CharWidths.Count && buffer.CharWidths.Count != 0)
                {
                    charsWithX = new List<(int, float)>(chars.Count);
                    float cursorTextSpace = 0.0f;
                    for (int i = 0; i < chars.Count; i++)
                    {
                        float userX = buffer.UserPosX + cursorTextSpace * buffer.UserHScale;
                        charsWithX.Add((chars[i].Value, userX));
                        cursorTextSpace += buffer.CharWidths[i];
                    }
                }

                text = ApplyRtlVerdict(
                    text,
                    charsWithX,
                    buffer.AccumulatedWidth > 0.0f,
                    buffer.RenderMode is 3 or 7);
            }
        }

        List<float> charWidths = buffer.CharWidths;
        buffer.CharWidths = new List<float>();
        for (int i = 0; i < charWidths.Count; i++)
        {
            charWidths[i] *= buffer.UserHScale;
        }

        var span = new OxTextSpan
        {
            Text = text,
            Bbox = RawRect(buffer.UserPosX, buffer.UserPosY, totalWidth, effectiveFontSize),
            FontName = fontNameSpan,
            FontSize = effectiveFontSize,
            FontWeight = fontWeight,
            Color = new OxColor(buffer.FillColorRgb.R, buffer.FillColorRgb.G, buffer.FillColorRgb.B),
            Mcid = buffer.Mcid,
            McidScope = ShowingContext.CurrentMcidScope(),
            Sequence = SpanSequenceCounter,
            SplitBoundaryBefore = false,
            OffsetSemantic = false,
            CharSpacing = buffer.CharSpace,
            WordSpacing = buffer.WordSpace,
            HorizontalScaling = buffer.HorizontalScaling,
            IsItalic = isItalicSpan,
            IsMonospace = buffer.IsMonospace,
            PrimaryDetected = false,
            ArtifactType = ShowingContext.CurrentArtifactType(),
            CharWidths = charWidths,
            CharXOffsets = new List<float>(),
            HeadingLevel = null,
            RotationDegrees = buffer.RotationDegrees,
            Wmode = buffer.Wmode,
            TextRise = buffer.TextRise,
            RtlDrawLogical = false,
        };
        SpanSequenceCounter += 1;

        if (!ShowingContext.IsContentSuppressed())
        {
            Spans.Add(span);
        }
    }

    // ---- process_tj_array (text.rs:7536) -----------------------------------------

    /// <summary>Dispatch a TJ array to the configured word-boundary mode.</summary>
    internal void ProcessTjArray(IReadOnlyList<OxTextElement> array)
    {
        switch (WordBoundaryMode)
        {
            case WordBoundaryMode.Tiebreaker:
                ProcessTjArrayTiebreaker(array);
                break;
            case WordBoundaryMode.Primary:
                ProcessTjArrayPrimary(array);
                break;
        }
    }

    // ---- process_tj_array_tiebreaker (text.rs:7559) -------------------------------

    /// <summary>
    /// The default TJ path. Per §9.4.4 NOTE 6 a shown string should be as long as possible,
    /// so consecutive strings accumulate into one buffer and only a TJ offset past the
    /// word-gap threshold ends it; word boundaries are a tiebreaker, not the primary signal.
    /// </summary>
    internal void ProcessTjArrayTiebreaker(IReadOnlyList<OxTextElement> array)
    {
        // The per-character record feeding word-boundary detection; rebuilt per TJ array.
        TjCharacterArray.Clear();
        CurrentXPosition = 0.0f;

        float fontSize = StateStack.Current.FontSize;
        float horizontalScaling = StateStack.Current.HorizontalScaling / 100.0f;
        string? fontName = StateStack.Current.FontName;
        float charSpace = StateStack.Current.CharSpace;
        float wordSpace = StateStack.Current.WordSpace;

        OxTjBuffer buffer = OxTextDecoding.NewTjBuffer(StateStack.Current, CurrentMcid, CachedCurrentFont);

        for (int idx = 0; idx < array.Count; idx++)
        {
            OxTextElement element = array[idx];
            if (element is OxTextElement.Str str)
            {
                if (fontName is not null && Fonts.TryGetValue(fontName, out OxFontInfo? font))
                {
                    foreach (byte b in str.Bytes)
                    {
                        // Normalized through the encoding so boundary detection sees real
                        // characters rather than the raw codes of a custom encoding.
                        char? encoded = font.GetEncodedChar(b);
                        int charCode = encoded is char ch ? ch : b;

                        float glyphWidth = font.GetGlyphWidth(b);
                        bool isLigature = IsLigatureCode((uint)charCode);

                        TjCharacterArray.Add(new CharacterInfo
                        {
                            Code = charCode,
                            GlyphId = null,
                            Width = glyphWidth,
                            XPosition = CurrentXPosition,
                            // Filled in when the next element turns out to be an offset.
                            TjOffset = null,
                            FontSize = fontSize,
                            IsLigature = isLigature,
                            OriginalLigature = null,
                            ProtectedFromSplit = false,
                        });

                        float charAdvance = glyphWidth * horizontalScaling
                            + charSpace
                            + (b == 0x20 ? wordSpace : 0.0f);
                        CurrentXPosition += charAdvance;
                    }
                }

                AppendAdvanceBuffer(buffer, str.Bytes);
            }
            else if (element is OxTextElement.Offset off)
            {
                float offset = off.Value;

                // The running sums make the justified/normal verdict a constant-time read;
                // the history is capped so a pathological page cannot grow it without bound.
                if (TjOffsetHistory.Count < 10000)
                {
                    double x = offset;
                    TjSum += x;
                    TjSumSq += x * x;
                    TjOffsetHistory.Add(offset);
                    TjStatsLen = TjOffsetHistory.Count;
                }

                // The offset applies after the preceding string, so it belongs to the last
                // character recorded.
                if (TjCharacterArray.Count > 0)
                {
                    TjCharacterArray[^1].TjOffset = (int)offset;
                }

                float threshold = ShowingContext.CalculateAdaptiveTjThreshold();
                if (offset < threshold)
                {
                    // Split words ("diffe rent", "cha nge") are handled at merge time by the
                    // intra-word kerning guard in the space decision, which has the full
                    // bbox; an earlier guard here misclassified the genuinely narrow
                    // inter-word gaps of tightly justified LaTeX output.
                    bool bufferEndsWithSpace = buffer.Unicode.Length > 0
                        && char.IsWhiteSpace(buffer.Unicode[buffer.Unicode.Length - 1]);

                    FlushTjBuffer(buffer);

                    // "word " + " next" would otherwise come out with two spaces.
                    bool nextElementStartsWithSpace = false;
                    if (idx + 1 < array.Count && array[idx + 1] is OxTextElement.Str nextStr)
                    {
                        nextElementStartsWithSpace = nextStr.Bytes.Length > 0
                            && nextStr.Bytes[0] is 0x20 or 0x09 or 0x0A or 0x0D;
                    }

                    if (!bufferEndsWithSpace && !nextElementStartsWithSpace)
                    {
                        InsertSpaceAsSpan();
                    }

                    // Applied before the new buffer is created so its user_pos_x is the
                    // actual draw position of the next string; anchoring at the pre-offset
                    // position leaves every later span on the line short by this tx.
                    AdvancePositionForOffset(offset);

                    buffer = OxTextDecoding.NewTjBuffer(StateStack.Current, CurrentMcid, CachedCurrentFont);
                }
                else
                {
                    AdvancePositionForOffset(offset);
                    // The matrix moved, so the buffer's advance record has to move with it.
                    // Historically only the matrix did, which left the reconstructed glyph
                    // positions of justified body text drifting points behind the render.
                    FoldOffsetIntoBuffer(buffer, offset);
                }
            }
        }

        if (!buffer.IsEmpty)
        {
            FlushTjBuffer(buffer);
        }
    }

    // ---- process_tj_array_primary (text.rs:7760) ----------------------------------

    /// <summary>
    /// The primary-detection TJ path: word boundaries are detected over the character array
    /// and each cluster between them becomes its own span.
    /// </summary>
    internal void ProcessTjArrayPrimary(IReadOnlyList<OxTextElement> array)
    {
        // Nothing collected yet — the character array is filled by the tiebreaker path, so
        // the first TJ array of a text object always goes through it.
        if (TjCharacterArray.Count == 0)
        {
            ProcessTjArrayTiebreaker(array);
            return;
        }

        // Before detection, so an email address or URL cannot be split at a boundary.
        MarkPatternContexts(TjCharacterArray);

        BoundaryContext context = CreateBoundaryContext();

        // The script profile lets the detector skip the detectors that cannot fire.
        DocumentScript script = DocumentScriptDetector.DetectFromCharacters(TjCharacterArray);
        var detector = new WordBoundaryDetector().WithDocumentScript(script);
        List<int> boundaries = detector.DetectWordBoundaries(TjCharacterArray, context);

        if (boundaries.Count == 0)
        {
            ProcessTjArrayTiebreaker(array);
            return;
        }

        ApplyLigatureDecisions();

        List<List<CharacterInfo>> clusters =
            PartitionCharactersByBoundaries(TjCharacterArray, boundaries);

        foreach (List<CharacterInfo> cluster in clusters)
        {
            if (cluster.Count != 0)
            {
                ClusterToSpan(cluster);
            }
        }
    }

    // ---- create_boundary_context (text.rs:7813) -----------------------------------

    /// <summary>The §9.3 text-state parameters the boundary detector measures gaps against.</summary>
    internal BoundaryContext CreateBoundaryContext()
    {
        OxGraphicsState state = StateStack.Current;
        return new BoundaryContext(state.FontSize)
        {
            HorizontalScaling = state.HorizontalScaling,
            WordSpacing = state.WordSpace,
            CharSpacing = state.CharSpace,
        };
    }

    // ---- partition_characters_by_boundaries (text.rs:7831) ------------------------

    /// <summary>Cut the character array into clusters at the detected boundary indices.</summary>
    internal List<List<CharacterInfo>> PartitionCharactersByBoundaries(
        IReadOnlyList<CharacterInfo> characters,
        IReadOnlyList<int> boundaries)
    {
        if (boundaries.Count == 0)
        {
            return new List<List<CharacterInfo>> { new(characters) };
        }

        var clusters = new List<List<CharacterInfo>>();
        int prev = 0;

        foreach (int boundaryIdx in boundaries)
        {
            if (boundaryIdx > prev)
            {
                clusters.Add(Slice(characters, prev, boundaryIdx));
            }
            prev = boundaryIdx;
        }

        if (prev < characters.Count)
        {
            clusters.Add(Slice(characters, prev, characters.Count));
        }

        return clusters;
    }

    // ---- cluster_to_span (text.rs:7865) -------------------------------------------

    /// <summary>
    /// Turn one detected word cluster into a span, taking its bbox from the character
    /// positions the TJ walk recorded rather than from a buffer's accumulated advance.
    /// </summary>
    internal void ClusterToSpan(IReadOnlyList<CharacterInfo> cluster)
    {
        if (cluster.Count == 0)
        {
            return;
        }

        OxMcidScope mcidScope = ShowingContext.CurrentMcidScope();
        OxGraphicsState state = StateStack.Current;

        float textMinX = cluster[0].XPosition;
        CharacterInfo last = cluster[^1];
        float textMaxX = last.XPosition + last.Width;
        float textWidth = MathF.Max(textMaxX - textMinX, 0.0f);

        float height = MathF.Abs(cluster[0].FontSize) * MathF.Max(MathF.Abs(state.TextMatrix.D), 1.0f);

        OxMatrix textMatrix = state.TextMatrix;
        OxMatrix ctm = state.Ctm;
        OxPoint textPos = textMatrix.TransformPoint(textMinX, 0.0f);
        OxPoint userPos = ctm.TransformPoint(textPos.X, textPos.Y);

        float userWidth = textWidth * MathF.Abs(textMatrix.A) * MathF.Abs(ctm.A);

        var bbox = RawRect(
            userPos.X,
            userPos.Y,
            // The larger of the two, so a degenerate matrix cannot collapse the span.
            MathF.Max(userWidth, textWidth),
            height);

        OxFontInfo? clusterFont = null;
        if (state.FontName is string clusterFontName)
        {
            Fonts.TryGetValue(clusterFontName, out clusterFont);
        }

        var textBuilder = new StringBuilder();
        if (clusterFont is not null)
        {
            foreach (CharacterInfo charInfo in cluster)
            {
                string? decoded = clusterFont.CharToUnicode((uint)charInfo.Code);
                if (decoded is not null)
                {
                    textBuilder.Append(decoded);
                }
            }
        }
        string unicodeText = textBuilder.ToString();

        // A producer may store RTL either visually (glyphs drawn left to right in a
        // right-to-left script) or logically (drawn right to left because it ran its own
        // bidi pass). Only geometry separates the two, and they need opposite treatment.
        if (Utf8ByteLen(unicodeText) > 1 && cluster.Count >= 2)
        {
            if (AnyRtl(unicodeText))
            {
                // One pair per source character: a ligature decodes to several characters
                // that all share the source glyph's x, so the first stands in for them.
                var charsWithX = new List<(int CodePoint, float X)>(cluster.Count);
                foreach (CharacterInfo ci in cluster)
                {
                    string? decoded = clusterFont?.CharToUnicode((uint)ci.Code);
                    Rune? decodedFirst = decoded is null ? null : FirstRuneOf(decoded);
                    if (decodedFirst is Rune r)
                    {
                        OxPoint p = textMatrix.TransformPoint(ci.XPosition, 0.0f);
                        float userX = ctm.TransformPoint(p.X, p.Y).X;
                        charsWithX.Add((r.Value, userX));
                    }
                }

                // The pre-#537 heuristic, kept as the ambiguous fallback so short RTL runs
                // (2-3 characters, below the geometric detector's confidence floor) keep
                // behaving exactly as they did.
                OxPoint firstP = textMatrix.TransformPoint(cluster[0].XPosition, 0.0f);
                float firstX = ctm.TransformPoint(firstP.X, firstP.Y).X;
                OxPoint lastP = textMatrix.TransformPoint(last.XPosition, 0.0f);
                float lastX = ctm.TransformPoint(lastP.X, lastP.Y).X;

                unicodeText = ApplyRtlVerdict(
                    unicodeText,
                    charsWithX,
                    lastX > firstX,
                    state.RenderMode is 3 or 7);
            }
        }

        OxFontWeight fontWeight = clusterFont is not null && clusterFont.IsBold()
            ? OxFontWeight.Bold
            : OxFontWeight.Normal;
        bool isItalic = clusterFont is not null && clusterFont.IsItalic();

        var span = new OxTextSpan
        {
            Text = unicodeText,
            Bbox = bbox,
            FontName = state.FontName ?? "Unknown",
            FontSize = cluster[0].FontSize,
            FontWeight = fontWeight,
            Color = new OxColor(state.FillColorRgb.R, state.FillColorRgb.G, state.FillColorRgb.B),
            Mcid = CurrentMcid,
            McidScope = mcidScope,
            Sequence = SpanSequenceCounter,
            SplitBoundaryBefore = false,
            OffsetSemantic = false,
            CharSpacing = state.CharSpace,
            WordSpacing = state.WordSpace,
            HorizontalScaling = state.HorizontalScaling,
            IsItalic = isItalic,
            IsMonospace = false,
            PrimaryDetected = true,
            ArtifactType = null,
            CharWidths = new List<float>(),
            CharXOffsets = new List<float>(),
            HeadingLevel = null,
            RotationDegrees = OxTextDecoding.SnapRunRotation(state.Ctm.Multiply(state.TextMatrix)),
            Wmode = state.TextWMode,
            // A ratio of font size, so it stays comparable regardless of text/CTM scale.
            TextRise = state.FontSize > 0.0f ? state.TextRise / state.FontSize : 0.0f,
            RtlDrawLogical = false,
        };

        SpanSequenceCounter += 1;
        if (!ShowingContext.IsContentSuppressed())
        {
            Spans.Add(span);
        }
    }

    // ---- is_ligature_code (text.rs:8070) ------------------------------------------

    /// <summary>The five standard Latin ligatures, U+FB00 (ff) through U+FB04 (ffl).</summary>
    internal static bool IsLigatureCode(uint code) => code is >= 0xFB00 and <= 0xFB04;

    // ---- apply_ligature_decisions (text.rs:8087) ----------------------------------

    /// <summary>
    /// Expand the ligatures that word-boundary detection decided sit at a word break, giving
    /// each component an equal share of the ligature's advance so the following characters
    /// keep their positions.
    /// </summary>
    internal void ApplyLigatureDecisions()
    {
        BoundaryContext context = CreateBoundaryContext();
        var result = new List<CharacterInfo>(TjCharacterArray.Count);
        int i = 0;

        // Rebuilt in one pass rather than spliced in place: inserting into the array per
        // ligature is quadratic, which cost a 50x slowdown on ligature-heavy documents.
        while (i < TjCharacterArray.Count)
        {
            CharacterInfo charInfo = TjCharacterArray[i];

            if (!charInfo.IsLigature)
            {
                result.Add(charInfo);
                i += 1;
                continue;
            }

            CharacterInfo? nextChar = i + 1 < TjCharacterArray.Count ? TjCharacterArray[i + 1] : null;

            if (LigatureShouldSplit(charInfo, context, nextChar))
            {
                // `char::from_u32(..).unwrap_or('?')`. A code outside the BMP is no ligature
                // either way, so it lands on the same empty expansion as the '?'.
                char ligatureChar = charInfo.Code is >= 0 and <= 0xFFFF && !char.IsSurrogate((char)charInfo.Code)
                    ? (char)charInfo.Code
                    : '?';
                float originalWidth = charInfo.Width;
                float originalX = charInfo.XPosition;
                float fontSize = charInfo.FontSize;

                IReadOnlyList<(char Char, float Width)> components =
                    ExpandLigatureToChars(ligatureChar, originalWidth);

                if (components.Count != 0)
                {
                    float xOffset = 0.0f;
                    result.Add(new CharacterInfo
                    {
                        Code = components[0].Char,
                        GlyphId = charInfo.GlyphId,
                        Width = components[0].Width,
                        XPosition = originalX,
                        TjOffset = charInfo.TjOffset,
                        FontSize = fontSize,
                        IsLigature = false,
                        OriginalLigature = new Rune(ligatureChar),
                        ProtectedFromSplit = charInfo.ProtectedFromSplit,
                    });
                    xOffset += components[0].Width;

                    for (int k = 1; k < components.Count; k++)
                    {
                        (char compChar, float compWidth) = components[k];
                        result.Add(new CharacterInfo
                        {
                            Code = compChar,
                            GlyphId = null,
                            Width = compWidth,
                            XPosition = originalX + xOffset,
                            TjOffset = null,
                            FontSize = fontSize,
                            IsLigature = false,
                            OriginalLigature = new Rune(ligatureChar),
                            ProtectedFromSplit = false,
                        });
                        xOffset += compWidth;
                    }
                }
                else
                {
                    result.Add(charInfo);
                }
            }
            else
            {
                result.Add(charInfo);
            }

            i += 1;
        }

        TjCharacterArray.Clear();
        TjCharacterArray.AddRange(result);
    }

    // ---- advance_position_for_string (text.rs:8180) -------------------------------

    /// <summary>
    /// Advance the text matrix by the width of a shown string and return that width, per
    /// §9.4.4.
    /// </summary>
    internal float AdvancePositionForString(ReadOnlySpan<byte> text)
    {
        OxGraphicsState state = StateStack.Current;
        float fontSize = state.FontSize;
        float horizontalScaling = state.HorizontalScaling;
        float charSpace = state.CharSpace;
        float wordSpace = state.WordSpace;
        byte wmode = state.TextWMode;

        OxFontInfo? font = CachedCurrentFont;

        // font_matrix_a carries glyph space to text space: 0.001 for Type1/TrueType, 1.0 for
        // a Type3 with an identity /FontMatrix. Assumes FontMatrix[1] == 0, which holds for
        // every standard font and virtually every Type3 in the wild.
        float fontMatrixA = font?.FontMatrixA ?? 0.001f;
        float fsFactor = fontSize * fontMatrixA;
        float hsFactor = horizontalScaling / 100.0f;
        float csHs = charSpace * hsFactor;
        float wsHs = wordSpace * hsFactor;

        float totalWidth;
        if (font is not null)
        {
            if (font.Subtype != "Type0")
            {
                float[] widthTable = font.GetByteToWidthTable();
                float wSum = 0.0f;
                foreach (byte b in text)
                {
                    float w = widthTable[b] * fsFactor * hsFactor;
                    w += csHs;
                    if (b == 0x20)
                    {
                        w += wsHs;
                    }
                    wSum += w;
                }
                totalWidth = wSum;
            }
            else if (wmode == 0)
            {
                float wSum = 0.0f;
                foreach ((ushort cid, int nbytes) in new OxTextCharIter(text, font))
                {
                    float w = font.GetGlyphWidth(cid) * fsFactor * hsFactor;
                    w += csHs;
                    // §9.3.3: Tw applies to the single-byte code 32 only, never to a byte 32
                    // inside a multi-byte code — a 2-byte CID 0x0020 in an Identity-H font
                    // taking Tw would over-advance and mis-position the whole run.
                    if (nbytes == 1 && cid == 32)
                    {
                        w += wsHs;
                    }
                    wSum += w;
                }
                totalWidth = wSum;
            }
            else
            {
                // Vertical: ty = w1y * Tfs + Tc + Tw, with no Th — Tz stretches glyphs on
                // the horizontal axis only (§9.3.4).
                float wSum = 0.0f;
                foreach ((ushort cid, int nbytes) in new OxTextCharIter(text, font))
                {
                    float w1y = font.GetVerticalMetrics(cid).W1y;
                    float w = w1y * fsFactor;
                    w += charSpace;
                    if (nbytes == 1 && cid == 32)
                    {
                        w += wordSpace;
                    }
                    wSum += w;
                }
                totalWidth = wSum;
            }
        }
        else
        {
            float defaultW = 500.0f * fsFactor * hsFactor + csHs;
            float spaceW = defaultW + wsHs;
            float wSum = 0.0f;
            foreach (byte b in text)
            {
                wSum += b == 0x20 ? spaceW : defaultW;
            }
            totalWidth = wSum;
        }

        StateStack.Current.AdvanceTextMatrix(totalWidth);

        return totalWidth;
    }

    // ---- append_and_advance (text.rs:8281) ----------------------------------------

    /// <summary>
    /// Decode, measure and advance in one pass over the bytes, into the extractor's own Tj
    /// span buffer. Merging the decode with the width walk saves a full per-byte pass on
    /// every showing operator of a text-heavy page.
    /// </summary>
    internal void AppendAndAdvance(ReadOnlySpan<byte> text)
    {
        // §7.3.4.2 sets an implementation limit of 32,767 bytes per string.
        if (text.Length > 32_767)
        {
            text = text[..32_767];
        }

        OxGraphicsState state = StateStack.Current;
        float fontSize = state.FontSize;
        float horizontalScaling = state.HorizontalScaling;
        float charSpace = state.CharSpace;
        float wordSpace = state.WordSpace;
        byte wmode = state.TextWMode;

        OxFontInfo? font = CachedCurrentFont;
        float fontMatrixA = font?.FontMatrixA ?? 0.001f;
        float fsFactor = fontSize * fontMatrixA;
        float hsFactor = horizontalScaling / 100.0f;
        float csHs = charSpace * hsFactor;
        float wsHs = wordSpace * hsFactor;

        OxTjBuffer buffer = TjSpanBuffer
            ?? throw new InvalidOperationException("tj_span_buffer initialized in begin_text_object");

        float totalWidth;
        if (font is not null)
        {
            if (font.Subtype != "Type0")
            {
                if (TryUtf8Repair(buffer, text, font, fsFactor, hsFactor, csHs, wsHs, out float utf8Width))
                {
                    // Upstream returns here without folding the width into
                    // accumulated_width — only the matrix moves. The buffer-parameter
                    // twin does fold it; the difference is upstream's and is kept.
                    StateStack.Current.AdvanceTextMatrix(utf8Width);
                    return;
                }

                char[] charTable = font.GetByteToCharTable();
                float[] widthTable = font.GetByteToWidthTable();
                float wSum = 0.0f;
                foreach (byte b in text)
                {
                    int bytesAdded = AppendByte(buffer.Unicode, charTable, font, b);

                    float w = widthTable[b] * fsFactor * hsFactor;
                    w += csHs;
                    if (b == 0x20)
                    {
                        w += wsHs;
                    }
                    wSum += w;

                    PushCharWidths(buffer.CharWidths, w, bytesAdded);
                }
                totalWidth = wSum;
            }
            else if (wmode == 0)
            {
                buffer.Append(text);
                float wSum = 0.0f;
                foreach ((ushort charCode, _) in new OxTextCharIter(text, font))
                {
                    float w = font.GetGlyphWidth(charCode) * fsFactor * hsFactor;
                    w += csHs;
                    if (charCode == 32)
                    {
                        w += wsHs;
                    }
                    wSum += w;
                    buffer.CharWidths.Add(w);
                }
                totalWidth = wSum;
            }
            else
            {
                buffer.Append(text);
                float wSum = 0.0f;
                foreach ((ushort charCode, _) in new OxTextCharIter(text, font))
                {
                    float w1y = font.GetVerticalMetrics(charCode).W1y;
                    float w = w1y * fsFactor;
                    w += charSpace;
                    if (charCode == 32)
                    {
                        w += wordSpace;
                    }
                    wSum += w;
                    buffer.CharWidths.Add(w);
                }
                totalWidth = wSum;
            }
        }
        else
        {
            buffer.Append(text);
            float defaultW = 500.0f * fsFactor * hsFactor + csHs;
            float spaceW = defaultW + wsHs;
            float wSum = 0.0f;
            foreach (byte b in text)
            {
                float w = b == 0x20 ? spaceW : defaultW;
                wSum += w;
                buffer.CharWidths.Add(w);
            }
            totalWidth = wSum;
        }

        buffer.AccumulatedWidth += totalWidth;

        StateStack.Current.AdvanceTextMatrix(totalWidth);
    }

    // ---- append_advance_buffer (text.rs:8471) -------------------------------------

    /// <summary>
    /// <see cref="AppendAndAdvance"/> against an explicit buffer, which is what TJ array
    /// processing needs because it owns its buffer rather than the extractor's field.
    /// </summary>
    internal void AppendAdvanceBuffer(OxTjBuffer buffer, ReadOnlySpan<byte> text)
    {
        if (text.Length > 32_767)
        {
            text = text[..32_767];
        }

        OxGraphicsState state = StateStack.Current;
        float fontSize = state.FontSize;
        float horizontalScaling = state.HorizontalScaling;
        float charSpace = state.CharSpace;
        float wordSpace = state.WordSpace;
        byte wmode = state.TextWMode;

        OxFontInfo? font = CachedCurrentFont;
        float fontMatrixA = font?.FontMatrixA ?? 0.001f;
        float fsFactor = fontSize * fontMatrixA;
        float hsFactor = horizontalScaling / 100.0f;
        float csHs = charSpace * hsFactor;
        float wsHs = wordSpace * hsFactor;

        float totalWidth;
        if (font is not null)
        {
            if (font.Subtype != "Type0")
            {
                if (TryUtf8Repair(buffer, text, font, fsFactor, hsFactor, csHs, wsHs, out float utf8Width))
                {
                    buffer.AccumulatedWidth += utf8Width;
                    StateStack.Current.AdvanceTextMatrix(utf8Width);
                    return;
                }

                char[] charTable = font.GetByteToCharTable();
                float[] widthTable = font.GetByteToWidthTable();
                float wSum = 0.0f;
                foreach (byte b in text)
                {
                    int bytesAdded = AppendByte(buffer.Unicode, charTable, font, b);

                    float w = widthTable[b] * fsFactor * hsFactor;
                    w += csHs;
                    if (b == 0x20)
                    {
                        w += wsHs;
                    }
                    wSum += w;

                    PushCharWidths(buffer.CharWidths, w, bytesAdded);
                }
                totalWidth = wSum;
            }
            else if (wmode == 0)
            {
                buffer.Append(text);
                // The byte width comes from the CMap codespace, not a hardcoded 2, so a CJK
                // font whose encoding name matches none of the Identity-H/EUC patterns but
                // whose /ToUnicode declares a 2-byte range still measures correctly (§9.7.5).
                float wSum = 0.0f;
                foreach ((ushort cid, int nbytes) in new OxTextCharIter(text, font))
                {
                    float w = font.GetGlyphWidth(cid) * fsFactor * hsFactor;
                    w += csHs;
                    if (nbytes == 1 && cid == 32)
                    {
                        w += wsHs;
                    }
                    wSum += w;
                    buffer.CharWidths.Add(w);
                }
                totalWidth = wSum;
            }
            else
            {
                buffer.Append(text);
                float wSum = 0.0f;
                foreach ((ushort cid, int nbytes) in new OxTextCharIter(text, font))
                {
                    float w1y = font.GetVerticalMetrics(cid).W1y;
                    float w = w1y * fsFactor;
                    w += charSpace;
                    if (nbytes == 1 && cid == 32)
                    {
                        w += wordSpace;
                    }
                    wSum += w;
                    buffer.CharWidths.Add(w);
                }
                totalWidth = wSum;
            }
        }
        else
        {
            buffer.Append(text);
            float defaultW = 500.0f * fsFactor * hsFactor + csHs;
            float spaceW = defaultW + wsHs;
            float wSum = 0.0f;
            foreach (byte b in text)
            {
                float w = b == 0x20 ? spaceW : defaultW;
                wSum += w;
                buffer.CharWidths.Add(w);
            }
            totalWidth = wSum;
        }

        buffer.AccumulatedWidth += totalWidth;

        StateStack.Current.AdvanceTextMatrix(totalWidth);
    }

    // ---- insert_space_as_span (text.rs:8668) --------------------------------------

    /// <summary>
    /// Emit the word gap a large negative TJ offset stands for as its own span, marked
    /// <c>OffsetSemantic</c> so the merger knows the space came from an offset and not from
    /// a drawn glyph.
    /// </summary>
    internal void InsertSpaceAsSpan()
    {
        OxMcidScope mcidScope = ShowingContext.CurrentMcidScope();
        OxGraphicsState state = StateStack.Current;
        float fontSize = state.FontSize;
        OxMatrix textMatrix = state.TextMatrix;
        OxMatrix ctm = state.Ctm;
        OxMatrix combined = ctm.Multiply(textMatrix);
        float effectiveFontSize =
            fontSize * MathF.Sqrt(combined.D * combined.D + combined.B * combined.B);
        float wordSpace = state.WordSpace;
        float horizontalScaling = state.HorizontalScaling;
        byte wmode = state.TextWMode;

        // A quarter em plus Tw, scaled by Th horizontally; vertically Tz does not apply
        // (§9.3.4) and the same magnitude becomes a writing-axis step.
        //
        // The displacement is measured against the raw Tf size, not the Tm-scaled effective
        // size, so for producers that set `/F 1 Tf` with the real size in Tm this span is
        // narrower in device space than a quarter em. The downstream column and line
        // heuristics were tuned against exactly that geometry — widening it reorders text on
        // real documents — so char_widths below is kept consistent with this bbox instead.
        float spaceAdvance = wmode == 0
            ? (250.0f * fontSize / 1000.0f + wordSpace) * horizontalScaling / 100.0f
            : 250.0f * fontSize / 1000.0f + wordSpace;

        OxPoint textPos = textMatrix.TransformPoint(0.0f, 0.0f);
        OxPoint userPos = ctm.TransformPoint(textPos.X, textPos.Y);

        string fontNameSpace = state.FontName ?? "Unknown";
        bool isItalicSpace = false;
        if (state.FontName is string spaceFontName && Fonts.TryGetValue(spaceFontName, out OxFontInfo? spaceFont))
        {
            isItalicSpace = spaceFont.IsItalic();
        }

        // Geometry follows the writing axis, because column detection and line breaking read
        // width against height to decide a span's orientation.
        (float spaceWidth, float spaceHeight) = wmode == 0
            ? (spaceAdvance, effectiveFontSize)
            : (effectiveFontSize, MathF.Abs(spaceAdvance));

        var span = new OxTextSpan
        {
            Text = " ",
            Bbox = RawRect(userPos.X, userPos.Y, spaceWidth, spaceHeight),
            FontName = fontNameSpace,
            FontSize = effectiveFontSize,
            FontWeight = OxFontWeight.Normal,
            Color = new OxColor(state.FillColorRgb.R, state.FillColorRgb.G, state.FillColorRgb.B),
            Mcid = CurrentMcid,
            McidScope = mcidScope,
            Sequence = SpanSequenceCounter,
            SplitBoundaryBefore = false,
            OffsetSemantic = true,
            CharSpacing = state.CharSpace,
            WordSpacing = state.WordSpace,
            HorizontalScaling = state.HorizontalScaling,
            IsItalic = isItalicSpace,
            IsMonospace = false,
            PrimaryDetected = false,
            ArtifactType = ShowingContext.CurrentArtifactType(),
            // One synthetic space, one width entry, so the merger's lockstep
            // (char_widths.Count == rune count) holds from birth whatever the merge order.
            CharWidths = new List<float> { spaceWidth },
            CharXOffsets = new List<float>(),
            HeadingLevel = null,
            RotationDegrees = OxTextDecoding.SnapRunRotation(state.Ctm.Multiply(state.TextMatrix)),
            Wmode = state.TextWMode,
            TextRise = state.FontSize > 0.0f ? state.TextRise / state.FontSize : 0.0f,
            RtlDrawLogical = false,
        };
        SpanSequenceCounter += 1;

        if (!ShowingContext.IsContentSuppressed())
        {
            Spans.Add(span);
        }

        // The matrix is deliberately not advanced here: the caller applies the actual TJ
        // offset immediately after, and advancing by the synthetic width on top of it would
        // double-count the gap and give the next buffer a bbox one space too far right.
    }

    // ---- advance_position_for_offset (text.rs:8806) -------------------------------

    /// <summary>
    /// Apply a TJ number element, which shifts the position along the active writing axis:
    /// horizontally <c>tx = -offset / 1000 * Tfs * Th</c>, vertically the same without Th,
    /// since Tz is the horizontal glyph-stretching axis (§9.3.4).
    /// </summary>
    internal void AdvancePositionForOffset(float offset)
    {
        OxGraphicsState state = StateStack.Current;
        float fontSize = state.FontSize;
        float horizontalScaling = state.HorizontalScaling;
        byte wmode = state.TextWMode;

        float tx = wmode == 0
            ? -offset / 1000.0f * fontSize * horizontalScaling / 100.0f
            : -offset / 1000.0f * fontSize;

        StateStack.Current.AdvanceTextMatrix(tx);
    }

    // ---- fold_offset_into_buffer (text.rs:8833) -----------------------------------

    /// <summary>
    /// Fold a sub-threshold TJ offset into the buffer's advance record, so its char widths
    /// and accumulated width keep tracking the text-matrix position. The displacement is
    /// computed in text space, matching the units of the per-glyph advances. The offset
    /// belongs to the preceding glyph — it adjusts the spacing after it — so it lands on the
    /// last recorded advance; with no glyph recorded yet the matrix move alone already
    /// positions the next buffer.
    /// </summary>
    internal void FoldOffsetIntoBuffer(OxTjBuffer buffer, float offset)
    {
        if (buffer.CharWidths.Count == 0)
        {
            return;
        }

        OxGraphicsState state = StateStack.Current;
        float adv = state.TextWMode == 0
            ? -offset / 1000.0f * state.FontSize * state.HorizontalScaling / 100.0f
            : -offset / 1000.0f * state.FontSize;

        buffer.CharWidths[^1] += adv;
        buffer.AccumulatedWidth += adv;
    }

    // ---- flush_tj_span_buffer (text.rs:8851) --------------------------------------

    /// <summary>
    /// Flush the buffer that accumulates consecutive Tj operators into a single span. Unlike
    /// <see cref="FlushTjBuffer"/> the span records the §9.3.1 defaults for Tc/Tw/Tz rather
    /// than the values in force, because the run may have crossed several settings.
    /// </summary>
    internal void FlushTjSpanBuffer()
    {
        OxTjBuffer? taken = TjSpanBuffer;
        TjSpanBuffer = null;
        if (taken is not OxTjBuffer buffer || buffer.IsEmpty)
        {
            return;
        }

        float totalWidth = buffer.AccumulatedWidth * buffer.UserHScale;

        float effectiveFontSize = buffer.EffectiveFontSize;
        OxFontWeight fontWeight = buffer.FontWeight;
        bool isItalicBuf = buffer.IsItalic;

        string fontNameBuf = buffer.FontName ?? "Unknown";
        buffer.FontName = null;

        string text = buffer.Unicode.ToString();
        buffer.Unicode = new StringBuilder();

        if (Utf8ByteLen(text) > 1)
        {
            if (AnyRtl(text))
            {
                // char_widths are text-space relative widths, so absolute user-space x comes
                // from accumulating them, scaling by user_h_scale and offsetting by
                // user_pos_x.
                List<Rune> chars = RunesOf(text);
                List<(int CodePoint, float X)>? charsWithX = null;
                if (chars.Count == buffer.CharWidths.Count && buffer.CharWidths.Count != 0)
                {
                    charsWithX = new List<(int, float)>(chars.Count);
                    float cursorTextSpace = 0.0f;
                    for (int i = 0; i < chars.Count; i++)
                    {
                        float userX = buffer.UserPosX + cursorTextSpace * buffer.UserHScale;
                        charsWithX.Add((chars[i].Value, userX));
                        cursorTextSpace += buffer.CharWidths[i];
                    }
                }

                text = ApplyRtlVerdict(
                    text,
                    charsWithX,
                    buffer.AccumulatedWidth > 0.0f,
                    buffer.RenderMode is 3 or 7);
            }
        }

        List<float> charWidths = buffer.CharWidths;
        buffer.CharWidths = new List<float>();
        for (int i = 0; i < charWidths.Count; i++)
        {
            charWidths[i] *= buffer.UserHScale;
        }

        var span = new OxTextSpan
        {
            Text = text,
            Bbox = RawRect(buffer.UserPosX, buffer.UserPosY, totalWidth, effectiveFontSize),
            FontName = fontNameBuf,
            FontSize = effectiveFontSize,
            FontWeight = fontWeight,
            Color = new OxColor(buffer.FillColorRgb.R, buffer.FillColorRgb.G, buffer.FillColorRgb.B),
            Mcid = buffer.Mcid,
            McidScope = ShowingContext.CurrentMcidScope(),
            Sequence = SpanSequenceCounter,
            SplitBoundaryBefore = false,
            OffsetSemantic = false,
            CharSpacing = 0.0f,
            WordSpacing = 0.0f,
            HorizontalScaling = 100.0f,
            IsItalic = isItalicBuf,
            IsMonospace = buffer.IsMonospace,
            PrimaryDetected = false,
            ArtifactType = ShowingContext.CurrentArtifactType(),
            CharWidths = charWidths,
            CharXOffsets = new List<float>(),
            HeadingLevel = null,
            RotationDegrees = buffer.RotationDegrees,
            Wmode = buffer.Wmode,
            TextRise = buffer.TextRise,
            RtlDrawLogical = false,
        };
        SpanSequenceCounter += 1;

        if (!ShowingContext.IsContentSuppressed())
        {
            Spans.Add(span);
        }
    }

    // ---- show_text (text.rs:8975) -------------------------------------------------

    /// <summary>
    /// Emit one <see cref="OxTextChar"/> per glyph of a shown string, advancing the text
    /// matrix per §9.4.4. This is the per-glyph half of extraction, independent of the span
    /// buffers: it records device-space geometry for every character drawn.
    /// </summary>
    internal void ShowText(ReadOnlySpan<byte> text)
    {
        // §7.3.4.2 sets an implementation limit of 32,767 bytes per string.
        if (text.Length > 32_767)
        {
            text = text[..32_767];
        }

        OxGraphicsState state0 = StateStack.Current;
        float fontSize = state0.FontSize;
        float horizontalScaling = state0.HorizontalScaling;
        float charSpace = state0.CharSpace;
        float wordSpace = state0.WordSpace;
        (float R, float G, float B) fillColorRgb = state0.FillColorRgb;
        OxMatrix ctm = state0.Ctm;
        byte wmode = state0.TextWMode;

        OxFontInfo? font = CachedCurrentFont;

        foreach ((ushort charCode, _) in new OxTextCharIter(text, font))
        {
            // Re-read each glyph: earlier characters of this same string moved the matrix.
            OxMatrix textMatrix = StateStack.Current.TextMatrix;

            string unicodeString;
            if (font is not null)
            {
                unicodeString = font.CharToUnicode(charCode) ?? OxTextDecoding.FallbackCharToUnicode(charCode);
            }
            else if (charCode < 256 && charCode < 0x80)
            {
                unicodeString = ((char)charCode).ToString();
            }
            else
            {
                unicodeString = "?";
            }

            OxPoint textPos = textMatrix.TransformPoint(0.0f, 0.0f);
            OxPoint pos = ctm.TransformPoint(textPos.X, textPos.Y);

            OxMatrix combinedChar = ctm.Multiply(textMatrix);
            float effectiveFontSize = fontSize
                * MathF.Sqrt(combinedChar.D * combinedChar.D + combinedChar.B * combinedChar.B);

            float glyphWidthFontUnits = font is not null ? font.GetGlyphWidth(charCode) : 500.0f;

            float fontMatrixA = font?.FontMatrixA ?? 0.001f;
            float fsFactor = fontSize * fontMatrixA;
            float hsFactor = horizontalScaling / 100.0f;
            float glyphWidthUserSpace = glyphWidthFontUnits * fsFactor * hsFactor;

            // Horizontal: tx = (w0 * Tfs + Tc + Tw) * Th. Vertical: ty = w1y * Tfs + Tc + Tw,
            // with no Th, since Tz stretches glyphs on the X axis only (§9.3.4).
            float tx;
            if (wmode == 0)
            {
                tx = glyphWidthUserSpace
                    + charSpace * hsFactor
                    + (charCode == 32 ? wordSpace * hsFactor : 0.0f);
            }
            else
            {
                float w1y = font is not null
                    ? font.GetVerticalMetrics(charCode).W1y
                    : OxVerticalMetrics.SpecDefault.W1y;
                tx = w1y * fsFactor + charSpace + (charCode == 32 ? wordSpace : 0.0f);
            }

            float glyphWidthDeviceSpace = glyphWidthUserSpace * MathF.Abs(combinedChar.A);
            float txDeviceSpace = tx * MathF.Abs(combinedChar.A);
            float heightDeviceSpace = effectiveFontSize;

            OxFontWeight fontWeight = font is not null && font.IsBold() ? OxFontWeight.Bold : OxFontWeight.Normal;
            bool isItalicChar = font is not null && font.IsItalic();

            var color = new OxColor(fillColorRgb.R, fillColorRgb.G, fillColorRgb.B);

            OxMatrix finalMatrix = ctm.Multiply(textMatrix);
            float rotationDegrees = MathF.Atan2(finalMatrix.B, finalMatrix.A) * (180.0f / MathF.PI);

            // A malformed font can map one code to an unbounded string; past 8 characters it
            // is garbage rather than a ligature, so only the first is kept.
            List<Rune> runes = RunesOf(unicodeString);
            if (runes.Count > 8)
            {
                unicodeString = runes.Count > 0 ? runes[0].ToString() : "?";
                runes = RunesOf(unicodeString);
            }

            int charCount = runes.Count;
            float charWidthDevice = charCount > 0 ? glyphWidthDeviceSpace / charCount : glyphWidthDeviceSpace;
            float charWidthUser = charCount > 0 ? glyphWidthUserSpace / charCount : glyphWidthUserSpace;
            // Tc applies once per character *code*, not per output glyph, so spreading the
            // whole advance over a ligature's characters slightly over-distributes it — the
            // same trade-off the glyph width already makes.
            float renderedAdvancePerChar = charCount > 0 ? txDeviceSpace / charCount : txDeviceSpace;

            for (int charIndex = 0; charIndex < runes.Count; charIndex++)
            {
                Rune unicodeChar = runes[charIndex];
                bool shouldSkip = unicodeChar.Value == 0
                    || (Rune.IsControl(unicodeChar)
                        && unicodeChar.Value != '\t'
                        && unicodeChar.Value != '\n'
                        && unicodeChar.Value != '\r');

                if (shouldSkip)
                {
                    continue;
                }

                float xOffsetDevice = charIndex * charWidthDevice;
                float xOffsetUser = charIndex * charWidthUser;

                float charOriginX = pos.X + xOffsetDevice;
                float charOriginY = pos.Y;

                var textChar = new OxTextChar
                {
                    // OxTextChar carries a UTF-16 char; a supplementary glyph is recorded by
                    // its lead surrogate, which is what the char-level consumers index by.
                    Char = unicodeChar.IsBmp ? (char)unicodeChar.Value : unicodeChar.ToString()[0],
                    Bbox = new OxRect(charOriginX, charOriginY, charWidthDevice, heightDeviceSpace),
                    FontName = font?.BaseFont ?? "",
                    FontSize = effectiveFontSize,
                    FontWeight = fontWeight,
                    Color = color,
                    Mcid = CurrentMcid,
                    IsItalic = isItalicChar,
                    IsMonospace = false,
                    OriginX = charOriginX,
                    OriginY = charOriginY,
                    RotationDegrees = rotationDegrees,
                    AdvanceWidth = charWidthDevice,
                    RenderedAdvance = renderedAdvancePerChar,
                    Ascent = (font?.Ascent ?? 0.95f) * effectiveFontSize,
                    Descent = (font?.Descent ?? -0.35f) * effectiveFontSize,
                    Matrix = new[]
                    {
                        finalMatrix.A,
                        finalMatrix.B,
                        finalMatrix.C,
                        finalMatrix.D,
                        finalMatrix.E + xOffsetUser,
                        finalMatrix.F,
                    },
                };

                if (!ShowingContext.IsContentSuppressed())
                {
                    Chars.Add(textChar);
                }
            }

            StateStack.Current.AdvanceTextMatrix(tx);
        }
    }

    // ---- char_count / clear (text.rs:9181) ----------------------------------------

    /// <summary>The number of extracted characters.</summary>
    internal int CharCount() => Chars.Count;

    /// <summary>Discard the extracted characters.</summary>
    internal void Clear() => Chars.Clear();

    // ---- shared pieces of the three advance loops --------------------------------

    /// <summary>
    /// #317: some producers (Russian CAD exporters, Office in non-English locales) emit raw
    /// UTF-8 inside a string literal for a font that declares only a Latin encoding and no
    /// /ToUnicode, where byte-by-byte decoding yields mojibake such as `ÐÐ¸ÑÑ` for "Лист".
    /// The non-Latin-1 gate is what keeps genuine Latin-1 Supplement text (`Résumé`) out: it
    /// decodes entirely below U+0100 and is left alone. The advance is still measured from
    /// the byte widths, since those are the glyphs the producer actually drew; the decoded
    /// characters share it equally.
    /// </summary>
    private static bool TryUtf8Repair(
        OxTjBuffer buffer,
        ReadOnlySpan<byte> text,
        OxFontInfo font,
        float fsFactor,
        float hsFactor,
        float csHs,
        float wsHs,
        out float width)
    {
        width = 0.0f;
        if (font.ToUnicode is not null || text.Length < 2)
        {
            return false;
        }

        bool hasHigh = false;
        foreach (byte b in text)
        {
            if (b >= 0x80) { hasHigh = true; break; }
        }
        if (!hasHigh)
        {
            return false;
        }

        // On the show-operator path, so it runs per drawn string; most PDF byte strings are
        // not UTF-8, which made the failure path the common one and the throw the cost.
        if (!OxTextDecoding.TryDecodeUtf8(text, out string decoded)) return false;

        bool hasNonLatin1 = false;
        foreach (Rune r in decoded.EnumerateRunes())
        {
            if (r.Value > 0xFF) { hasNonLatin1 = true; break; }
        }
        if (!hasNonLatin1)
        {
            return false;
        }

        float[] widthTable = font.GetByteToWidthTable();
        float wSum = 0.0f;
        foreach (byte b in text)
        {
            float w = widthTable[b] * fsFactor * hsFactor;
            w += csHs;
            if (b == 0x20)
            {
                w += wsHs;
            }
            wSum += w;
        }

        int charCount = 0;
        foreach (Rune _ in decoded.EnumerateRunes()) charCount++;
        if (charCount > 0)
        {
            float perChar = wSum / charCount;
            foreach (Rune r in decoded.EnumerateRunes())
            {
                buffer.Unicode.Append(r.ToString());
                buffer.CharWidths.Add(perChar);
            }
        }

        width = wSum;
        return true;
    }

    /// <summary>
    /// The simple-font decode of one byte, reporting the UTF-8 bytes appended — the quantity
    /// upstream reads off the buffer's length to split one advance across a multi-character
    /// mapping.
    /// </summary>
    private static int AppendByte(StringBuilder sink, char[] charTable, OxFontInfo font, byte b)
    {
        char c = charTable[b];
        if (c != '\0')
        {
            sink.Append(c);
            return Utf8ByteLen(c);
        }

        // '\0' in the table means a multi-character mapping, U+FFFD, or an unmapped byte.
        string? mapped = font.CharToUnicode(b);
        string s = mapped ?? OxTextDecoding.FallbackCharToUnicode(b);
        if (s == "�" && !OxTextDecoding.PreserveUnmappedGlyphs)
        {
            return 0;
        }

        int bytes = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            if (r.Value >= 0x20 || r.Value == '\t' || r.Value == '\n' || r.Value == '\r')
            {
                sink.Append(r.ToString());
                bytes += r.Utf8SequenceLength;
            }
        }
        return bytes;
    }

    /// <summary>Record the byte's advance, split evenly when it produced several characters.</summary>
    private static void PushCharWidths(List<float> charWidths, float w, int charsAdded)
    {
        if (charsAdded == 1)
        {
            charWidths.Add(w);
        }
        else if (charsAdded > 1)
        {
            float perChar = w / charsAdded;
            for (int k = 0; k < charsAdded; k++)
            {
                charWidths.Add(perChar);
            }
        }
    }

    // ---- seam defaults and small helpers ------------------------------------------

    /// <summary>
    /// `text::bidi::apply_rtl_verdict`, over the existing port of the same module's
    /// detector. Invisible text (render mode 3 or 7) is left alone: it is an OCR layer under
    /// an image and its order is the searchable one, whatever the geometry says.
    /// </summary>
    private static string DefaultApplyRtlVerdict(
        string text,
        IReadOnlyList<(int CodePoint, float X)>? charsWithX,
        bool coarseVisualOrderHeuristic,
        bool isInvisibleRenderMode)
    {
        if (isInvisibleRenderMode)
        {
            return text;
        }

        PdfBidi.RunOrder verdict = PdfBidi.RunOrder.Ambiguous;
        if (charsWithX is not null)
        {
            var pairs = new List<(char c, double x)>(charsWithX.Count);
            foreach ((int codePoint, float x) in charsWithX)
            {
                // Supplementary code points are in no RTL block the detector reads, so a
                // non-character stand-in classifies exactly as the scalar would.
                pairs.Add((codePoint <= 0xFFFF ? (char)codePoint : '\uFFFF', x));
            }
            verdict = PdfBidi.DetectVisualOrderRun(pairs);
        }

        return verdict switch
        {
            PdfBidi.RunOrder.Visual => PdfBidi.ReverseRtlKeepNumbers(text),
            PdfBidi.RunOrder.Logical => text,
            _ => coarseVisualOrderHeuristic ? PdfBidi.ReverseRtlKeepNumbers(text) : text,
        };
    }

    /// <summary>
    /// `LigatureDecisionMaker::decide`: a ligature at the end of the run has no boundary to
    /// split at, an explicit TJ offset past -100 or a geometric gap over half the font size
    /// is a word break, and anything else keeps the ligature whole.
    /// </summary>
    private static bool DefaultLigatureShouldSplit(CharacterInfo ligature, BoundaryContext context, CharacterInfo? next)
    {
        if (next is not CharacterInfo n)
        {
            return false;
        }

        if (n.TjOffset is int tjOffset && tjOffset < -100)
        {
            return true;
        }

        float ligatureEnd = ligature.XPosition + ligature.Width;
        float gap = n.XPosition - ligatureEnd;
        return gap > context.FontSize * 0.5f;
    }

    /// <summary>`expand_ligature_to_chars`: components with an equal share of the advance.</summary>
    private static IReadOnlyList<(char Char, float Width)> DefaultExpandLigatureToChars(
        char ligature, float originalWidth)
    {
        string? componentsStr = ligature switch
        {
            'ﬀ' => "ff",
            'ﬁ' => "fi",
            'ﬂ' => "fl",
            'ﬃ' => "ffi",
            'ﬄ' => "ffl",
            _ => null,
        };
        if (componentsStr is null)
        {
            return Array.Empty<(char, float)>();
        }

        float widthPerComponent = originalWidth / componentsStr.Length;
        var components = new List<(char, float)>(componentsStr.Length);
        foreach (char c in componentsStr)
        {
            components.Add((c, widthPerComponent));
        }
        return components;
    }

    /// <summary>The default answers, read off the state the extractor already tracks.</summary>
    /// <summary>
    /// Routes the four questions the showing half asks about extractor state back to the
    /// half that owns them.
    /// </summary>
    /// <remarks>
    /// These were re-implemented here while the two halves were ported in parallel and this
    /// file could not see the other. They agreed, but two copies of one rule only ever agree
    /// until someone edits one of them.
    /// </remarks>
    private sealed class DefaultShowingContext : IOxShowingContext
    {
        private readonly OxTextExtractor _owner;
        internal DefaultShowingContext(OxTextExtractor owner) => _owner = owner;

        public OxMcidScope CurrentMcidScope() => _owner.CurrentMcidScope();

        public OxArtifactType? CurrentArtifactType() => _owner.CurrentArtifactType();

        public bool IsContentSuppressed() => _owner.IsContentSuppressed();

        public float CalculateAdaptiveTjThreshold() => _owner.CalculateAdaptiveTjThreshold();
    }

    /// <summary>Rejects invalid sequences instead of substituting, as <c>str::from_utf8</c> does.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Rust assigns the span bbox's extents raw, and an RTL run genuinely produces a negative
    // width; OxRect's public constructor would normalize that by moving the origin, which
    // relocates the span.
    private static OxRect RawRect(float x, float y, float width, float height) =>
        width >= 0.0f && height >= 0.0f
            ? new OxRect(x, y, width, height)
            : OxRect.FromPoints(x, y, x + width, y + height);

    private static int Utf8ByteLen(string s) => Encoding.UTF8.GetByteCount(s);

    private static int Utf8ByteLen(char c) => c < 0x80 ? 1 : c < 0x800 ? 2 : 3;

    private static List<Rune> RunesOf(string s)
    {
        var runes = new List<Rune>(s.Length);
        foreach (Rune r in s.EnumerateRunes()) runes.Add(r);
        return runes;
    }

    private static Rune? FirstRuneOf(string s)
    {
        foreach (Rune r in s.EnumerateRunes()) return r;
        return null;
    }

    private static bool AnyRtl(string s)
    {
        foreach (Rune r in s.EnumerateRunes())
        {
            if (ScriptSignals.IsRtlText(r.Value)) return true;
        }
        return false;
    }

    private static List<CharacterInfo> Slice(IReadOnlyList<CharacterInfo> source, int start, int end)
    {
        var slice = new List<CharacterInfo>(end - start);
        for (int i = start; i < end; i++) slice.Add(source[i]);
        return slice;
    }
}
