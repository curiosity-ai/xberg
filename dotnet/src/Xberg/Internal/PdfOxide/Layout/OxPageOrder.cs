// Ported from pdf_oxide `pipeline/page_order.rs` (`page_reading_order`,
// `reading_frame_quadrant`, `order_in_rotated_frame`), the parts of
// `pipeline/mod.rs::TextPipeline::process` that survive without a structure tree
// (`is_vertical_majority`, `reorder_rtl_word_runs`) and `utils::dominant_rotation`
// (`lib.rs` l. 436).
//
// This is the canonical reading order the WORD path consumes: `extract_words` reads its
// spans through `page_reading_order`, never through the plain-text path's
// `extract_spans_with_reading_order`. The tiers resolve as follows here:
//
//   1. Logical structure order (`/StructTreeRoot`) — ported, in
//      <see cref="OxStructureOrder"/>: a tagged, non-suspect page is ordered by its MCID
//      pre-order, with the XY-cut kept for the spans the tree does not cover and for an
//      MCID sequence that zigzags across columns.
//   2. Article threads (`/Threads`) — not reachable either: `page_article_bead_rects`
//      only supplies bead rectangles behind a multi-column + order-divergence gate and
//      fails closed to the geometric tier without them.
//   3. Geometric — `StructureTreeStrategy`'s own fallback is `XYCutStrategy`, which is
//      what <see cref="OxReadingOrder.OrderSpansColumnAware"/> ports.
using System;
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.PdfOxide.Text;

namespace Xberg.Internal.PdfOxide.Layout;

internal static class OxPageOrder
{
    /// <summary>
    /// One page's post-processed spans in canonical reading order (`page_reading_order`).
    /// </summary>
    /// <param name="spans">The page's spans as `postprocess_spans` left them.</param>
    /// <param name="pageRotation">The page's /Rotate, normalized to [0, 360).</param>
    /// <param name="mediaBox">MediaBox corners, which fix the rotated reading frame.</param>
    /// <param name="mcidOrder">
    /// The page's logical-structure MCID sequence (`ReadingOrderContext::mcid_order`), or
    /// null for an untagged or suspect document, which is what sends the page down the
    /// geometric tier.
    /// </param>
    public static List<OxTextSpan> PageReadingOrder(
        List<OxTextSpan> spans, int pageRotation, (float Llx, float Lly, float Urx, float Ury) mediaBox,
        IReadOnlyList<int>? mcidOrder = null)
    {
        if (spans.Count == 0) return new List<OxTextSpan>();

        // Text-matrix-rotated content, gated to unrotated pages: on a /Rotate'd page
        // `postprocess_spans` already mapped the rotated-content spans into the displayed
        // frame, so their retained rotation describes the pre-display frame and re-rotating
        // here would double-transform.
        if (pageRotation == 0)
        {
            // A dominant rotation — a landscape table typeset on a portrait page — reorders
            // the WHOLE page in the rotated reading frame.
            if (ReadingFrameQuadrant(DominantRotation(spans)) is int rot)
                return OrderInRotatedFrame(spans, mediaBox, rot, mcidOrder);

            // Otherwise mirror the span path's per-span rotation firewall: rotated minority
            // runs (margin stamps, figure labels) break the axis-aligned assumptions of the
            // geometric strategies, so lift them out, order each rotation group in its
            // upright frame, and append them after the horizontal flow.
            if (spans.Any(s => s.RotationDegrees != 0.0f))
            {
                var rotated = spans.Where(s => s.RotationDegrees != 0.0f).ToList();
                var upright = spans.Where(s => s.RotationDegrees == 0.0f).ToList();
                var ordered = upright.Count == 0 ? new List<OxTextSpan>() : Process(upright, mcidOrder);
                ordered.AddRange(OxReadingOrder.OrderRotatedBlocks(rotated));
                return ordered;
            }
        }

        return Process(spans, mcidOrder);
    }

    /// <summary>
    /// The pipeline's own ordering step (`TextPipeline::process`): tategaki when the page is
    /// vertical-majority, the geometric XY-cut otherwise, then the RTL word-run reversal.
    /// </summary>
    private static List<OxTextSpan> Process(List<OxTextSpan> spans, IReadOnlyList<int>? mcidOrder)
    {
        // A vertical-majority page always wins over the configured strategy: none of the
        // left-to-right strategies can produce right-to-left column ordering correctly.
        var ordered = OxReadingOrder.IsTategakiPage(spans)
            ? OxSpanCompare.SortVerticalTategaki(spans, s => s.Bbox)
            : mcidOrder is { Count: > 0 }
                ? OxStructureOrder.Apply(spans, mcidOrder)
                : OxReadingOrder.OrderSpansColumnAware(spans);
        ReorderRtlWordRuns(ordered);
        return ordered;
    }

    /// <summary>
    /// The display-rotation quadrant that turns text of the given snapped rotation upright
    /// (`reading_frame_quadrant`).
    /// </summary>
    /// <remarks>
    /// 90° text (reading bottom-to-top) becomes readable when the page is displayed as if
    /// /Rotate 90, -90° under /Rotate 270 and 180° under /Rotate 180. Mirrored or free-angle
    /// runs, which the run-rotation snap reports as raw angles, have no quadrant frame.
    /// </remarks>
    internal static int? ReadingFrameQuadrant(float? degrees)
    {
        if (degrees is not float d) return null;
        if (MathF.Abs(d - 90.0f) < 0.5f) return 90;
        if (MathF.Abs(d - 180.0f) < 0.5f) return 180;
        if (MathF.Abs(d + 90.0f) < 0.5f) return 270;
        return null;
    }

    /// <summary>
    /// The rotation shared by a strict majority of the page's non-blank runs, or null
    /// (`utils::dominant_rotation`). Unrotated runs count towards the total but never form a
    /// group, so a page of upright prose has no dominant rotation.
    /// </summary>
    internal static float? DominantRotation(IReadOnlyList<OxTextSpan> spans)
    {
        var groups = new List<(float Deg, int Count)>();
        int total = 0;
        foreach (var s in spans)
        {
            if (s.Text.Trim().Length == 0) continue;
            total++;
            if (s.RotationDegrees == 0.0f) continue;
            int g = groups.FindIndex(t => MathF.Abs(t.Deg - s.RotationDegrees) < 0.5f);
            if (g >= 0) groups[g] = (groups[g].Deg, groups[g].Count + 1);
            else groups.Add((s.RotationDegrees, 1));
        }
        if (total == 0 || groups.Count == 0) return null;

        var best = groups[0];
        foreach (var g in groups) if (g.Count > best.Count) best = g;
        return best.Count * 2 >= total ? best.Deg : null;
    }

    /// <summary>
    /// Order a dominant-rotation page in its rotated reading frame (`order_in_rotated_frame`):
    /// map every span origin through the display rotation so the text becomes horizontal, run
    /// the standard pipeline there, then map the origins back so callers see true page
    /// coordinates — only the ORDER reflects the rotated frame.
    /// </summary>
    /// <remarks>
    /// Rotated spans store text-local extents: the origin, the advance along the run as the
    /// width, and the font size as the height. Mapping into the reading frame therefore
    /// rotates the ORIGIN as a point and keeps the extents, which already describe the run in
    /// its own upright frame. The two maps are not inverses in single precision —
    /// <c>w - (w - x)</c> lands about one ULP of the page dimension away from <c>x</c>, some
    /// 6e-5 pt on a Letter page — so the returned origins carry that drift, as does anything
    /// measured from them.
    /// </remarks>
    private static List<OxTextSpan> OrderInRotatedFrame(
        List<OxTextSpan> spans, (float Llx, float Lly, float Urx, float Ury) mediaBox, int rot,
        IReadOnlyList<int>? mcidOrder)
    {
        float llx = mediaBox.Llx, lly = mediaBox.Lly;
        float w = mediaBox.Urx - llx, h = mediaBox.Ury - lly;

        foreach (var s in spans)
        {
            var (x, y) = MapOrigin(s.Bbox.X, s.Bbox.Y, rot, w, h, llx, lly);
            s.Bbox = new OxRect(x, y, s.Bbox.Width, s.Bbox.Height);
        }

        var ordered = Process(spans, mcidOrder);

        // Inverse map: the opposite quadrant applied with the rotated frame's dimensions,
        // which the quarter turns swap.
        (float fw, float fh) = rot % 180 == 90 ? (h, w) : (w, h);
        int inv = (360 - rot) % 360;
        foreach (var s in ordered)
        {
            var (x, y) = MapOrigin(s.Bbox.X, s.Bbox.Y, inv, fw, fh, llx, lly);
            s.Bbox = new OxRect(x, y, s.Bbox.Width, s.Bbox.Height);
        }
        return ordered;
    }

    /// <summary>A span origin turned through one quadrant of the page frame.</summary>
    private static (float X, float Y) MapOrigin(
        float x, float y, int rot, float fw, float fh, float llx, float lly)
    {
        float rx = x - llx, ry = y - lly;
        (float mx, float my) = rot switch
        {
            90 => (ry, fw - rx),
            180 => (fw - rx, fh - ry),
            270 => (fh - ry, rx),
            _ => (rx, ry),
        };
        return (llx + mx, lly + my);
    }

    /// <summary>
    /// Reverse each maximal run of consecutive same-line spans that is purely right-to-left
    /// (`reorder_rtl_word_runs`), so the emitted word order is logical rather than visual.
    /// </summary>
    /// <remarks>
    /// Each word's characters are left untouched — they are already logical. A run needs at
    /// least two entries to be worth reversing, and trailing blank spans stay outside the
    /// reversal so they keep separating the words they separated before.
    /// </remarks>
    private static void ReorderRtlWordRuns(List<OxTextSpan> ordered)
    {
        static bool IsSpace(OxTextSpan s) => s.Text.Trim().Length == 0;
        static bool IsRtlWord(OxTextSpan s)
        {
            bool hasRtl = false;
            foreach (var rune in s.Text.EnumerateRunes())
            {
                // A Latin letter disqualifies the whole word.
                if (rune.Value < 128 && char.IsAsciiLetter((char)rune.Value)) return false;
                if (ScriptSignals.IsRtlText(rune.Value)) hasRtl = true;
            }
            return hasRtl;
        }

        int i = 0;
        while (i < ordered.Count)
        {
            if (!IsRtlWord(ordered[i])) { i++; continue; }

            float y = ordered[i].Bbox.Y;
            int start = i;
            int end = i + 1;
            while (end < ordered.Count
                && MathF.Abs(ordered[end].Bbox.Y - y) < 2.0f
                && (IsRtlWord(ordered[end]) || IsSpace(ordered[end])))
                end++;

            int last = end;
            while (last > start + 1 && IsSpace(ordered[last - 1])) last--;
            if (last - start >= 2) ordered.Reverse(start, last - start);
            i = end;
        }
    }
}
