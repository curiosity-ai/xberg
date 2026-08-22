// Ported from pdf_oxide `document.rs`: `postprocess_spans` (l. 11646-11891) and the
// helpers it calls — `is_cm_or_symbol_font` (11568), `fix_digit_logicalnot_decimal`
// (11585), `merge_drop_cap_initials` (11438), `rotate_span_bbox` (11362) /
// `map_span_into_rotated_frame` (11384), `char_widths_boundary_split` (7889),
// `apply_super_sub_script_substitutions` (12210) with `run_is_signed_number`,
// `compute_band_anchors`, `build_y_band_index` and `span_is_token_internal`, and
// `apply_combining_mark_composition` (12097).
//
// This is the span source the WORD path consumes. `extract_spans` runs it over the raw
// spans and `extract_words` reads `extract_spans` through `pipeline::page_reading_order`;
// the plain-text path instead goes through `extract_spans_filtered_with_reading_order`,
// which does only `drop_offpage_spans` plus an ordering pass. The two are deliberately
// separate: the word path's text carries the super/sub-script substitutions and the
// combining-mark compositions, and on a `/Rotate`d page its geometry is mapped into the
// displayed frame, none of which the plain-text path sees.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Layout;

namespace Xberg.Internal.PdfOxide.Text;

internal static class OxSpanPostprocess
{
    // Three of the Rust's steps are absent here, each because it cannot change what this
    // path returns:
    //
    //   * the erase-region filter — the port exposes no redaction API, so the region map is
    //     always empty;
    //   * `mark_running_artifact_spans` — it only stamps `artifact_type`, and the word path
    //     asks `page_reading_order` for the artifact-inclusive variant, so nothing
    //     downstream of here reads the flag;
    //   * `annotation_content_spans` — the port has no annotation span source. This one WOULD
    //     add spans, so it is a real gap rather than a no-op, and belongs with whoever ports
    //     that source. Note what it actually admits, which is narrower than its name: the Rust
    //     (`document.rs:8902`) takes only `/FreeText` and `/Stamp`, skipping `/Widget` (already
    //     covered by `extract_widget_spans`), `/Popup`, anything flagged hidden/invisible/NoView
    //     in `/F`, and — deliberately, with a comment saying so — `/Text` sticky notes, whose
    //     `/Contents` is reviewer comment text shown in a pop-up rather than painted on the
    //     page. The text comes from `/Contents`, falling back to the appearance stream, and the
    //     span's box is `/Rect` with a font size of 12 and a sequence base of 2_000_000.

    /// <summary>Line-band height the super/sub-script anchor search works in.</summary>
    private const float LineBandPt = 4.0f;

    /// <summary>
    /// One page's raw spans as `postprocess_spans` leaves them.
    /// </summary>
    /// <param name="doc">Document the page belongs to; read for its /Rotate.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="spans">Raw spans, consumed and returned reordered and rewritten.</param>
    /// <param name="chars">
    /// The char extractor's own glyphs for this page, used only for the closing x-origin stamp.
    /// </param>
    public static List<OxTextSpan> Run(
        PdfDocument doc, int pageIndex, List<OxTextSpan> spans, List<OxTextChar> chars)
    {
        var (llx, lly, urx, ury) = doc.GetPageMediaBox(pageIndex);
        OxReadingOrder.DropOffpageSpans(spans, (float)llx, (float)lly, (float)urx, (float)ury);

        // Recover decimal points mis-decoded as `¬` (U+00AC) in Computer-Modern math
        // subsets, where the /Differences names the decimal slot `logicalnot`.
        foreach (var span in spans)
        {
            if (IsCmOrSymbolFont(span.FontName) && span.Text.Contains('¬'))
                span.Text = FixDigitLogicalnotDecimal(span.Text);
        }

        // Re-attach oversized lone leading capitals to their word before the reading-order
        // sort can strand them (drop-cap / table-title initials).
        MergeDropCapInitials(spans);

        // Apply the page /Rotate to span geometry BEFORE ordering: a rotated page must be
        // read in its DISPLAYED orientation or the row-aware sort emits text in the wrong
        // order. The transform is selective, because a rotated page carries two very
        // different kinds of run:
        //
        //   * Horizontal content (RotationDegrees == 0) on a 90°/270° page — a landscape
        //     table stored rotated. It is horizontal in raw user space and reads correctly
        //     THERE; rotating its bbox only turns the RECTANGLE, while the glyph walk still
        //     lays advances along x and cannot express a now-vertical run, so every raw row
        //     would collapse onto one displayed band. These are LEFT RAW.
        //   * Rotated content (RotationDegrees == ±90) on a 90°/270° page — a chart axis, a
        //     sideways table, or a whole landscape page authored by drawing every glyph
        //     sideways in a portrait MediaBox. Here the page /Rotate must COMBINE with the
        //     content rotation into the correct upright displayed frame. These ARE mapped.
        //
        // 180° maps everything: the text stays horizontal and both axes simply mirror.
        int rot = OxCharXOffsets.GetPageRotation(doc, pageIndex);
        bool rotates = rot is 90 or 180 or 270;
        if (rotates)
        {
            float w = (float)(urx - llx), h = (float)(ury - lly);
            foreach (var s in spans)
            {
                if (rot != 180 && s.RotationDegrees == 0.0f) continue;
                MapSpanIntoRotatedFrame(s, rot, (float)llx, (float)lly, w, h);
            }
        }

        // Tategaki (vertical writing) intercept. Pages whose majority of spans were emitted
        // under WMode 1 need right-to-left, top-to-bottom ordering; row-aware and XY-cut
        // sorts assume horizontal flow and scramble vertical text.
        //
        // The horizontal branch is XY-cut here where the Rust gates it on
        // `is_multi_column_page` (and, before that, `sidebar_body_reading_order`) and
        // otherwise row-aware-sorts. Neither gate is ported because neither can change what
        // this path returns: `page_reading_order` re-orders the result with the same XY-cut
        // before `extract_words` ever sees it, and XY-cut's leaf comparator is a total order
        // on (Y descending, X ascending) rather than a stable pass over the incoming
        // sequence. What the gates would change is which spans neighbour each other during
        // that second XY-cut's heading-run scan, and there the ordering below is the one the
        // second pass would produce anyway.
        if (OxReadingOrder.IsTategakiPage(spans))
            spans = OxSpanCompare.SortVerticalTategaki(spans, s => s.Bbox);
        else
            spans = OxReadingOrder.OrderSpansColumnAware(spans);

        // Per-span rotation firewall: runs drawn with a rotated text matrix break the
        // axis-aligned assumptions of the sort above, so lift them out (preserving the
        // horizontal order) and re-append them as their own blocks.
        OxReadingOrder.ApplyRotationFirewall(spans);

        // Normalize Unicode typographic spaces to ASCII. Some producers encode word
        // separators as hair- or thin-space variants in their ToUnicode CMaps, and
        // normalising here gives every downstream consumer the same word boundaries.
        foreach (var span in spans)
        {
            if (NeedsSpaceNormalization(span.Text))
                span.Text = NormalizeUnicodeSpaces(span.Text);
        }

        // Apply the char-widths boundary split to the span text itself, so every consumer
        // sees the same word boundary the text assembler would insert.
        foreach (var span in spans)
        {
            if (CharWidthsBoundarySplit(span) is not int split) continue;
            span.Text = string.Concat(span.Text.AsSpan(0, split), " ", span.Text.AsSpan(split));
        }

        // Detect superscript / subscript runs and substitute ASCII digits with their
        // Unicode equivalents, but only where the run is sandwiched between alphabetic
        // body spans — chemistry and exponent context like "S²X" or "H₂O". The same
        // substitution on an author-affiliation marker ("name¹,²") is what the gate keeps
        // out, because those are conventionally kept in ASCII.
        ApplySuperSubScriptSubstitutions(spans);

        // Fold spacing-diacritic spans (´, `, ^, ~, ¨, …) into the base letter of the
        // following span when the diacritic is centred over the base glyph. PDFs that
        // pre-shape accented Latin emit the marks as separate show operators at the base
        // glyph's X, which without this reads "´Ecole" instead of "École".
        spans = ApplyCombiningMarkComposition(spans);

        // Stamp spec-aligned per-glyph x-origins onto the finalized spans so the glyph walk
        // reports real positions instead of drifting prefix-sums. Runs last, on the fully
        // post-processed spans, so the alignment sees the text the consumers do.
        if (spans.Count > 0 && chars.Count > 0) OxCharXOffsets.Stamp(doc, pageIndex, spans, chars);

        return spans;
    }

    // ── page /Rotate geometry ───────────────────────────────────────────────────────

    /// <summary>
    /// A span rectangle (already translated so the page origin sits at the axis origin)
    /// mapped through a clockwise page /Rotate, as an axis-aligned box in the displayed
    /// frame (`rotate_span_bbox`).
    /// </summary>
    /// <remarks>
    /// ISO 32000-1:2008 §7.7.3.3 makes the rotation clockwise and §8.3.3 gives the point
    /// transform. <paramref name="rot"/> must be a normalized multiple of 90; anything else
    /// returns the rectangle unchanged, and 0 is the identity.
    /// </remarks>
    internal static OxRect RotateSpanBbox(OxRect bbox, int rot, float pageW, float pageH)
    {
        (float X, float Y) Map(float x, float y) => rot switch
        {
            90 => (y, pageW - x),
            180 => (pageW - x, pageH - y),
            270 => (pageH - y, x),
            _ => (x, y),
        };

        var (ax, ay) = Map(bbox.X, bbox.Y);
        var (bx, by) = Map(bbox.X + bbox.Width, bbox.Y + bbox.Height);
        return new OxRect(MathF.Min(ax, bx), MathF.Min(ay, by), MathF.Abs(ax - bx), MathF.Abs(ay - by));
    }

    /// <summary>
    /// One span's bbox moved into the displayed frame of a /Rotate'd page: translate to the
    /// origin, rotate, translate back (`map_span_into_rotated_frame`).
    /// </summary>
    internal static void MapSpanIntoRotatedFrame(
        OxTextSpan s, int rot, float llx, float lly, float w, float h)
    {
        var rel = new OxRect(s.Bbox.X - llx, s.Bbox.Y - lly, s.Bbox.Width, s.Bbox.Height);
        var m = RotateSpanBbox(rel, rot, w, h);
        s.Bbox = new OxRect(llx + m.X, lly + m.Y, m.Width, m.Height);
    }

    // ── `¬` decimal recovery ────────────────────────────────────────────────────────

    /// <summary>
    /// True for a Computer-Modern (<c>CM*</c>) or symbol font name, after stripping an
    /// <c>ABCDEF+</c> subset tag. Scopes the <c>¬</c>→<c>.</c> recovery.
    /// </summary>
    internal static bool IsCmOrSymbolFont(string fontName)
    {
        int plus = fontName.LastIndexOf('+');
        string based = plus >= 0 ? fontName[(plus + 1)..] : fontName;
        string lower = based.ToLowerInvariant();
        return lower.StartsWith("cm", StringComparison.Ordinal)
            || lower.Contains("symbol", StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrite a <c>¬</c> (U+00AC) that a math subset drew from its <c>logicalnot</c> slot
    /// as a decimal point: <c>digit ¬ digit</c> and <c>digit ¬ &lt;space&gt; digit</c> both
    /// become <c>digit.digit</c>, and the lone separating space is dropped so the number
    /// reads as one token.
    /// </summary>
    /// <remarks>
    /// The leading digit must abut the <c>¬</c> directly in both shapes, so a genuinely
    /// spaced negation (<c>5 ¬ 3</c>, <c>A ¬ B</c>) is left alone. Every other <c>¬</c>
    /// survives.
    /// </remarks>
    internal static string FixDigitLogicalnotDecimal(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '¬' && i > 0 && IsAsciiDigit(text[i - 1]))
            {
                if (i + 1 < text.Length && IsAsciiDigit(text[i + 1]))
                {
                    sb.Append('.');
                    continue;
                }
                if (i + 2 < text.Length && text[i + 1] == ' ' && IsAsciiDigit(text[i + 2]))
                {
                    sb.Append('.');
                    i++; // skip the single separating space
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    // ── drop caps ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-attach an oversized lone leading capital to the word it opens
    /// (`merge_drop_cap_initials`).
    /// </summary>
    /// <remarks>
    /// A genuine drop cap is oversized relative to the page's NORMAL body text, not merely
    /// relative to its right-hand neighbour: inline math such as "A_st" pairs a normal-size
    /// capital with a shrunken subscript, and gating on the neighbour alone would glue
    /// "A" + "st" into "Ast". The size gate is therefore anchored to the median size of
    /// multi-character spans — real words — so a body-size capital cannot qualify.
    /// </remarks>
    internal static void MergeDropCapInitials(List<OxTextSpan> spans)
    {
        int n = spans.Count;
        if (n < 2) return;

        var bodySizes = spans
            .Where(s => s.FontSize > 0.0f && s.Text.Length > 1)
            .Select(s => s.FontSize)
            .ToList();
        if (bodySizes.Count == 0) return;
        OxSpanCompare.SortStable(bodySizes, OxSpanCompare.SafeFloatCmp);
        float bodySize = bodySizes[bodySizes.Count / 2];

        // Span indices by left edge, plus the page's widest font, so each initial probes
        // only the spans whose left edge falls in its narrow candidate window instead of
        // rescanning the page. The per-candidate gap test below still decides.
        var order = Enumerable.Range(0, n).ToList();
        OxSpanCompare.SortStable(order, (a, b) => OxSpanCompare.SafeFloatCmp(spans[a].Bbox.X, spans[b].Bbox.X));
        float maxFs = 0.0f;
        foreach (var s in spans) maxFs = MathF.Max(maxFs, s.FontSize);

        var target = new int[n];
        Array.Fill(target, -1);
        for (int i = 0; i < n; i++)
        {
            var init = spans[i];
            if (init.Text.Length != 1 || init.FontSize <= 0.0f) continue;
            char lead = init.Text[0];
            if (lead is not (>= 'A' and <= 'Z')) continue;
            // The initial must be clearly oversized against normal body text.
            if (init.FontSize < bodySize * 1.5f) continue;

            float initRight = init.Bbox.X + init.Bbox.Width;
            float loX = initRight - maxFs * 0.5f;
            float hiX = initRight + maxFs * 0.12f;
            int lo = PartitionPoint(order, k => spans[k].Bbox.X < loX);
            int hi = PartitionPoint(order, k => spans[k].Bbox.X <= hiX);
            var cands = order.GetRange(lo, hi - lo);
            // Ascending original order keeps the strict-`<` minimum's first-wins tie-break.
            cands.Sort();

            int best = -1;
            float bestGap = float.MaxValue;
            foreach (int j in cands)
            {
                var body = spans[j];
                if (j == i || body.FontSize <= 0.0f) continue;
                if (body.Text.Length == 0 || !char.IsLetter(body.Text[0])) continue;
                // The continuation shares the initial's baseline. A tall initial also
                // overlaps the line ABOVE it, so a raw bbox test would let it reach up and
                // steal a word from that line; baseline proximity keeps the merge on the
                // initial's own line.
                if (MathF.Abs(init.Bbox.Y - body.Bbox.Y) > body.FontSize * 0.5f) continue;
                // Essentially touching. A genuine oversized initial is the first glyph of
                // one word, so its continuation begins within a hair of the initial's
                // advance — never across a word space, which would glue a standalone "A"
                // or "I" onto the next word.
                float gap = body.Bbox.X - initRight;
                if (gap < -body.FontSize * 0.5f || gap > body.FontSize * 0.12f) continue;
                if (MathF.Abs(gap) < bestGap)
                {
                    bestGap = MathF.Abs(gap);
                    best = j;
                }
            }
            target[i] = best;
        }

        var taken = new bool[n];
        var remove = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int j = target[i];
            if (j < 0) continue;
            if (taken[j] || remove[j] || remove[i]) continue; // a body receives at most one initial
            taken[j] = true;
            remove[i] = true;

            string initText = spans[i].Text;
            float initLeft = spans[i].Bbox.X;
            var body = spans[j];
            body.Text = initText + body.Text;
            float right = body.Bbox.X + body.Bbox.Width;
            float x = MathF.Min(initLeft, body.Bbox.X);
            body.Bbox = new OxRect(x, body.Bbox.Y, right - x, body.Bbox.Height);
        }

        var kept = new List<OxTextSpan>(n);
        for (int i = 0; i < n; i++) if (!remove[i]) kept.Add(spans[i]);
        spans.Clear();
        spans.AddRange(kept);
    }

    /// <summary>Index of the first element of <paramref name="order"/> failing the predicate.</summary>
    private static int PartitionPoint(List<int> order, Func<int, bool> stillTrue)
    {
        int lo = 0, hi = order.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (stillTrue(order[mid])) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ── whitespace and boundary normalization ───────────────────────────────────────

    private static bool NeedsSpaceNormalization(string text)
    {
        foreach (char c in text)
            if (c is (>= '\u2000' and <= '\u200B') or '\u202F' or '\u205F') return true;
        return false;
    }

    /// <summary>
    /// Typographic spaces folded to ASCII (`TextPostProcessor::normalize_unicode_spaces`).
    /// The zero-width space is not a visible character and is dropped rather than widened.
    /// </summary>
    internal static string NormalizeUnicodeSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // EN QUAD through HAIR SPACE, NARROW NO-BREAK SPACE, MEDIUM MATHEMATICAL SPACE.
            if (c is (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F') sb.Append(' ');
            else if (c == '\u200B') { }  // zero-width space: not a visible character
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The index at which a span's text concatenated two show runs, or null
    /// (`char_widths_boundary_split`). Fewer char widths than characters means the merger
    /// joined two runs, and the join point is the first character past the widths.
    /// </summary>
    /// <remarks>
    /// Only a letter→digit or lower→upper ASCII transition splits. Upper→lower is excluded
    /// because a ligature spanning that boundary inside a compound word would split falsely,
    /// and a non-ASCII boundary character is an encoding artifact (one fewer width entry from
    /// a Latin-2 diacritic), not a run concatenation.
    /// </remarks>
    internal static int? CharWidthsBoundarySplit(OxTextSpan span)
    {
        int cwLen = span.CharWidths.Count;
        if (cwLen == 0) return null;

        // Widths and text are both counted in codepoints, which UTF-16 surrogate pairs
        // would otherwise put out of step.
        var indices = CodepointStarts(span.Text);
        if (cwLen >= indices.Count) return null;

        int boundary = indices[cwLen];
        int prev = indices[cwLen - 1];
        char boundaryChar = span.Text[boundary];
        char prevChar = span.Text[prev];
        if (boundaryChar == ' ' || prevChar == ' ') return null;
        if (boundaryChar > 0x7F) return null;

        if ((char.IsLetter(prevChar) && IsAsciiDigit(boundaryChar))
            || (prevChar is >= 'a' and <= 'z' && boundaryChar is >= 'A' and <= 'Z'))
            return boundary;
        return null;
    }

    /// <summary>UTF-16 offsets at which each codepoint of <paramref name="text"/> begins.</summary>
    private static List<int> CodepointStarts(string text)
    {
        var starts = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1)
            starts.Add(i);
        return starts;
    }

    // ── super / subscript substitution ──────────────────────────────────────────────

    private static char? SuperForChar(char c) => c switch
    {
        '0' => '⁰', '1' => '¹', '2' => '²', '3' => '³', '4' => '⁴',
        '5' => '⁵', '6' => '⁶', '7' => '⁷', '8' => '⁸', '9' => '⁹',
        '+' => '⁺', '-' => '⁻', '=' => '⁼', '(' => '⁽', ')' => '⁾',
        _ => null,
    };

    private static char? SubForChar(char c) => c switch
    {
        '0' => '₀', '1' => '₁', '2' => '₂', '3' => '₃', '4' => '₄',
        '5' => '₅', '6' => '₆', '7' => '₇', '8' => '₈', '9' => '₉',
        '+' => '₊', '-' => '₋', '=' => '₌', '(' => '₍', ')' => '₎',
        _ => null,
    };

    /// <summary>
    /// Rewrite the digits of a super/subscript run into their Unicode counterparts
    /// (`apply_super_sub_script_substitutions`).
    /// </summary>
    /// <remarks>
    /// A run qualifies when its font is meaningfully smaller than the body font of its line
    /// band AND its baseline is raised or lowered against that band's anchor. Only runs made
    /// entirely of substitutable characters are rewritten, so a footnote callout letter falls
    /// through to the citation path unchanged.
    /// </remarks>
    internal static void ApplySuperSubScriptSubstitutions(List<OxTextSpan> spans)
    {
        int n = spans.Count;
        if (n < 2) return;

        var sortedByY = Enumerable.Range(0, n).ToList();
        OxSpanCompare.SortStable(sortedByY, (a, b) => OxSpanCompare.SafeFloatCmp(spans[a].Bbox.Y, spans[b].Bbox.Y));
        var bandAnchor = ComputeBandAnchors(spans, sortedByY, LineBandPt);
        var yIndex = BuildYBandIndex(spans, LineBandPt);

        for (int i = 0; i < n; i++)
        {
            var (anchorFs, anchorY) = bandAnchor[i];
            float currFs = spans[i].FontSize;
            // Skip the body span itself — it IS the anchor.
            if (anchorFs <= 0.0f || currFs >= anchorFs * 0.85f) continue;

            float yDelta = spans[i].Bbox.Y - anchorY;
            bool raised = yDelta > anchorFs * 0.15f;
            bool lowered = yDelta < -anchorFs * 0.15f;
            if (!raised && !lowered) continue;

            Func<char, char?> map = raised ? SuperForChar : SubForChar;
            string text = spans[i].Text;
            if (text.Length == 0) continue;
            bool allMappable = true;
            foreach (char c in text) if (map(c) is null) { allMappable = false; break; }
            if (!allMappable) continue;

            // A signed numeric exponent — scientific unit notation such as `s−1`, `m−2` —
            // stays ASCII. ToUnicode already decoded the intended characters and the
            // plaintext convention keeps these un-superscripted; rewriting them is both
            // wrong against that convention and, because the geometric classifier fires
            // inconsistently on borderline baselines, a source of non-determinism between
            // identical occurrences.
            if (RunIsSignedNumber(text)) continue;

            // Limit the substitution to clearly token-internal runs: the span must have a
            // base-sized neighbour on BOTH sides whose facing character is alphabetic and
            // roughly adjacent in X. Author-affiliation markers sit at the END of a line
            // with no following body letter, and the convention renders those as plain
            // ASCII digits.
            if (!SpanIsTokenInternal(spans, i, yIndex, LineBandPt)) continue;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text) sb.Append(map(c)!.Value);
            spans[i].Text = sb.ToString();
        }
    }

    /// <summary>
    /// True when the run opens with a minus sign and holds at least one digit — a scientific
    /// unit exponent the substitution must leave alone (`run_is_signed_number`).
    /// </summary>
    private static bool RunIsSignedNumber(string text)
    {
        if (text.Length == 0) return false;
        char first = text[0];
        if (first is not ('\u002D' or '\u2212' or '\u2010' or '\u2011')) return false;
        foreach (char c in text) if (IsAsciiDigit(c)) return true;
        return false;
    }

    /// <summary>
    /// For every span, the (max font size, anchor Y) over the spans within ±band of its Y,
    /// via a sliding-window maximum over the Y-sorted order (`compute_band_anchors`).
    /// </summary>
    /// <remarks>
    /// Equal maxima tie-break to the lowest-Y span. A substitution only fires when the span's
    /// own font is strictly smaller than the anchor, so the tie-break merely picks which
    /// equal-sized body span supplies the anchor Y — all of them within the band.
    /// </remarks>
    private static (float Fs, float Y)[] ComputeBandAnchors(
        List<OxTextSpan> spans, List<int> sortedByY, float band)
    {
        int n = sortedByY.Count;
        var bandAnchor = new (float, float)[n];
        float Y(int p) => spans[sortedByY[p]].Bbox.Y;
        float Fs(int p) => spans[sortedByY[p]].FontSize;

        // Deque of sorted positions, font size non-increasing front→back; positions are
        // pushed in increasing order, so the front is both the smallest position and the
        // window maximum.
        var deque = new LinkedList<int>();
        int lo = 0, hi = 0;
        for (int pos = 0; pos < n; pos++)
        {
            float cy = Y(pos);
            while (hi < n && Y(hi) <= cy + band)
            {
                while (deque.Last is { } back && Fs(back.Value) < Fs(hi)) deque.RemoveLast();
                deque.AddLast(hi);
                hi++;
            }
            while (lo < n && Y(lo) < cy - band)
            {
                if (deque.First is { } front && front.Value == lo) deque.RemoveFirst();
                lo++;
            }
            int best = deque.First!.Value;
            bandAnchor[sortedByY[pos]] = (Fs(best), Y(best));
        }
        return bandAnchor;
    }

    /// <summary>
    /// Span indices bucketed by Y band (`build_y_band_index`), so a same-line lookup scans
    /// only nearby bands. Querying band k's [k-2, k+2] neighbours is a guaranteed superset of
    /// every span within `band` points of any Y in band k, so the exact |Δy| filter on the
    /// result matches a full scan.
    /// </summary>
    private static Dictionary<int, List<int>> BuildYBandIndex(List<OxTextSpan> spans, float band)
    {
        var index = new Dictionary<int, List<int>>();
        for (int j = 0; j < spans.Count; j++)
        {
            int key = OxSpanCompare.RoundToI32(spans[j].Bbox.Y / band);
            if (!index.TryGetValue(key, out var bucket)) index[key] = bucket = new List<int>();
            bucket.Add(j);
        }
        return index;
    }

    private static IEnumerable<int> YBandCandidates(Dictionary<int, List<int>> yIndex, float y, float band)
    {
        int k = OxSpanCompare.RoundToI32(y / band);
        for (int b = k - 2; b <= k + 2; b++)
            if (yIndex.TryGetValue(b, out var bucket))
                foreach (int j in bucket) yield return j;
    }

    /// <summary>
    /// True when span <paramref name="i"/> has a base-sized alphabetic neighbour both before
    /// and after it on the same line band, within about one em horizontally
    /// (`span_is_token_internal`).
    /// </summary>
    /// <remarks>
    /// That captures "X²Y" / "H₂O" / "k₁ + …" and excludes a footnote marker hanging off the
    /// end of a word with no following body character.
    /// </remarks>
    private static bool SpanIsTokenInternal(
        List<OxTextSpan> spans, int i, Dictionary<int, List<int>> yIndex, float band)
    {
        var curr = spans[i];
        float currY = curr.Bbox.Y;
        float currX = curr.Bbox.X;
        float currRight = curr.Bbox.X + curr.Bbox.Width;

        float bodyFs = 0.0f;
        foreach (int j in YBandCandidates(yIndex, currY, band))
            if (MathF.Abs(spans[j].Bbox.Y - currY) <= 4.0f)
                bodyFs = MathF.Max(bodyFs, spans[j].FontSize);
        bodyFs = MathF.Max(bodyFs, 1.0f);

        float neighbourFsMin = bodyFs * 0.85f;
        float maxEm = bodyFs;
        bool hasLeft = false, hasRight = false;
        foreach (int j in YBandCandidates(yIndex, currY, band))
        {
            if (j == i) continue;
            var s = spans[j];
            if (MathF.Abs(s.Bbox.Y - currY) > 4.0f) continue;
            if (s.FontSize < neighbourFsMin) continue;
            if (s.Text.Length == 0) continue;

            float sRight = s.Bbox.X + s.Bbox.Width;
            // Small overlap is allowed: super/sub glyphs nest slightly under the body
            // letter's bounding box.
            float dxLeft = currX - sRight;
            if (sRight < currRight && dxLeft <= maxEm && dxLeft >= -maxEm * 0.5f
                && char.IsLetter(s.Text[^1]))
                hasLeft = true;

            float dxRight = s.Bbox.X - currRight;
            if (s.Bbox.X > currX && dxRight <= maxEm && dxRight >= -maxEm * 0.5f
                && char.IsLetter(s.Text[0]))
                hasRight = true;
        }
        return hasLeft && hasRight;
    }

    // ── combining marks ─────────────────────────────────────────────────────────────

    private static char? CombiningFor(char spacing) => spacing switch
    {
        '\u00B4' => '\u0301', // acute
        '\u0060' => '\u0300', // grave
        '\u005E' => '\u0302', // circumflex
        '\u02C6' => '\u0302', // modifier-letter circumflex
        '\u007E' => '\u0303', // tilde
        '\u02DC' => '\u0303', // small tilde
        '\u00A8' => '\u0308', // diaeresis
        '\u00AF' => '\u0304', // macron
        '\u02C9' => '\u0304', // modifier-letter macron
        '\u00B8' => '\u0327', // cedilla
        '\u02DA' => '\u030A', // ring above
        _ => null,
    };

    /// <summary>
    /// Fold a spacing diacritic into the base letter it sits over
    /// (`apply_combining_mark_composition`).
    /// </summary>
    /// <remarks>
    /// Two shapes are handled: the pair already merged into one span by the span merger
    /// (both glyphs shared a text-matrix origin), and the diacritic left on a span of its
    /// own immediately before the base. The result is NFC-composed, so a precomposed
    /// codepoint comes out where Unicode has one.
    /// </remarks>
    internal static List<OxTextSpan> ApplyCombiningMarkComposition(List<OxTextSpan> spans)
    {
        // Already-merged pairs: a leading diacritic plus an alphabetic base in one span.
        foreach (var span in spans)
        {
            if (span.Text.Length < 2) continue;
            if (CombiningFor(span.Text[0]) is not char combining) continue;
            char based = span.Text[1];
            if (!char.IsLetter(based)) continue;

            var composed = new StringBuilder(span.Text.Length + 2);
            composed.Append(based).Append(combining).Append(span.Text, 2, span.Text.Length - 2);
            span.Text = composed.ToString().Normalize(NormalizationForm.FormC);
        }

        // Pairwise: the diacritic on its own one-character span, the base on the next.
        int i = 0;
        while (i + 1 < spans.Count)
        {
            var mark = spans[i];
            if (mark.Text.Length != 1 || CombiningFor(mark.Text[0]) is not char combining)
            {
                i++;
                continue;
            }

            // Same line, with the diacritic anchored over the base letter's left edge.
            var next = spans[i + 1];
            bool sameLine = MathF.Abs(mark.Bbox.Y - next.Bbox.Y)
                < MathF.Max(mark.FontSize, next.FontSize) * 0.6f;
            bool overlapsX = MathF.Abs(mark.Bbox.X - next.Bbox.X) <= 1.5f;
            if (!(sameLine && overlapsX)) { i++; continue; }

            if (next.Text.Length == 0) { i++; continue; }
            char based = next.Text[0];
            if (!char.IsLetter(based)) { i++; continue; }

            var composed = new StringBuilder(next.Text.Length + 2);
            composed.Append(based).Append(combining).Append(next.Text, 1, next.Text.Length - 1);
            next.Text = composed.ToString().Normalize(NormalizationForm.FormC);
            // Empty out the diacritic span; the retain below drops it.
            mark.Text = "";
            i += 2;
        }

        return spans.Where(s => s.Text.Length != 0).ToList();
    }
}
