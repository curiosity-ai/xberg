// Ported from pdf_oxide-0.3.77 `document.rs`:
//   stamp_char_x_offsets (11937), extract_chars_impl (16508), get_page_rotation (4294).
//
// The span merger reports each run's text and one box around it, but not where inside that
// box each glyph sits. `to_chars` reconstructs those positions by prefix-summing the span's
// nominal widths, which omit TJ kerning (§9.4.3) and therefore drift across a long run.
// This pass replaces the guess with the truth: the char-level extractor's own per-glyph
// origins, aligned onto the finished span text.
//
// Everything downstream that decomposes a span through `to_chars` — word extraction above
// all, and through it the table detector's cell granularity — reads these.
using System;
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Layout;

namespace Xberg.Internal.PdfOxide.Text;

internal static class OxCharXOffsets
{
    /// <summary>How far a glyph's baseline may sit from the span's, as a share of font size.</summary>
    private const float BaselineTolRatio = 0.6f;

    /// <summary>How far ahead the greedy alignment may look for the next matching glyph.</summary>
    private const int MatchWindow = 6;

    /// <summary>Rust's <c>f32::EPSILON</c>.</summary>
    private const float SingleEpsilon = 1.1920929e-7f;

    /// <summary>
    /// Copy the char extractor's per-glyph baseline x-origins onto each span
    /// (document.rs:11937).
    /// </summary>
    internal static void Stamp(PdfDocument doc, int pageIndex, List<OxTextSpan> spans, List<OxTextChar> accurate)
    {
        // Horizontal x-origins only mean anything in an unrotated frame, and the 180° mirror
        // is the one rotation that leaves every span in the displayed frame.
        if (GetPageRotation(doc, pageIndex) == 180)
        {
            return;
        }
        if (accurate.Count == 0 || spans.Count == 0)
        {
            return;
        }

        // Baseline index: char positions ordered by origin Y, so a span's baseline slice is a
        // binary-searched range instead of a scan over every glyph on the page.
        var byY = Enumerable.Range(0, accurate.Count).ToList();
        OxSpanCompare.SortStable(byY, (a, b) =>
            OxSpanCompare.SafeFloatCmp(accurate[a].OriginY, accurate[b].OriginY));
        float[] ys = byY.Select(i => accurate[i].OriginY).ToArray();

        foreach (var span in spans)
        {
            // A rotated run's glyphs advance vertically in the displayed frame, so a
            // horizontal-x stamp would not correspond; it keeps the prefix-sum path.
            if (span.RotationDegrees != 0.0f)
            {
                continue;
            }

            // Offsets carried over from a source span cannot be trusted for text that may
            // since have been edited.
            span.CharXOffsets.Clear();

            var glyphs = span.Text.EnumerateRunes().ToList();
            int n = glyphs.Count;
            if (n == 0)
            {
                continue;
            }

            float baselineTol = BaselineTolRatio * MathF.Max(span.FontSize, 1.0f);
            // The y-sorted index brackets a candidate range and the exact predicate selects
            // from it, so the result matches a full scan even where the bracket arithmetic
            // rounds differently — the widened bracket keeps the range a superset. Ordering
            // by (origin X, source index) reproduces a stable filter-then-sort: ties on X
            // keep their extraction order.
            float bracket = baselineTol + MathF.Abs(baselineTol) * 1e-6f + SingleEpsilon;
            int lo = PartitionPointLess(ys, span.Bbox.Y - bracket);
            int hi = PartitionPointLessOrEqual(ys, span.Bbox.Y + bracket);

            var idx = new List<int>();
            for (int k = lo; k < hi; k++)
            {
                int i = byY[k];
                if (MathF.Abs(accurate[i].OriginY - span.Bbox.Y) <= baselineTol)
                {
                    idx.Add(i);
                }
            }
            if (idx.Count == 0)
            {
                continue;
            }
            idx.Sort((a, b) =>
            {
                int c = OxSpanCompare.SafeFloatCmp(accurate[a].OriginX, accurate[b].OriginX);
                return c != 0 ? c : a.CompareTo(b);
            });

            // Greedy per-glyph alignment: anchor at the char nearest the span's left edge,
            // then walk the span's glyphs, matching each to the next equal char within a
            // short forward window. An all-or-nothing contiguous match would throw the whole
            // span away over one unmatched glyph — an inserted word-boundary space, a split
            // ligature, a combining mark — so unmatched positions are interpolated below
            // instead.
            int start = 0;
            for (int k = 0; k < idx.Count; k++)
            {
                if (accurate[idx[k]].OriginX >= span.Bbox.X - 0.5f)
                {
                    start = k;
                    break;
                }
            }

            var assigned = new float?[n];
            int li = start;
            for (int k = 0; k < n; k++)
            {
                char g = glyphs[k].IsBmp ? (char)glyphs[k].Value : glyphs[k].ToString()[0];
                int j = li;
                int steps = 0;
                while (j < idx.Count && steps < MatchWindow)
                {
                    if (accurate[idx[j]].Char == g)
                    {
                        assigned[k] = accurate[idx[j]].OriginX;
                        li = j + 1;
                        break;
                    }
                    j++;
                    steps++;
                }
            }

            // Below 60% real anchors the run is not recognisably this span's; fall back.
            int anchors = assigned.Count(a => a.HasValue);
            if (anchors * 5 < n * 3)
            {
                continue;
            }

            // Fill the gaps: an unmatched glyph takes the nearest preceding anchor plus the
            // prefix sum of the locally accurate widths between them, or walks back from the
            // nearest following one. Over the short runs between anchors the drift this
            // reintroduces is sub-point.
            List<float> cw = span.CharWidths;
            float WidthAt(int i) => cw.Count == n ? cw[i] : span.Bbox.Width / n;

            var offs = new float[n];
            (int K, float X)? last = null;
            for (int k = 0; k < n; k++)
            {
                if (assigned[k] is float x)
                {
                    offs[k] = x;
                    last = (k, x);
                }
                else if (last is (int lk, float lx))
                {
                    float acc = 0.0f;
                    for (int i = lk; i < k; i++) acc += WidthAt(i);
                    offs[k] = lx + acc;
                }
            }
            if (!assigned[0].HasValue)
            {
                int fk = Array.FindIndex(assigned, a => a.HasValue);
                if (fk >= 0)
                {
                    float fx = assigned[fk]!.Value;
                    for (int k = 0; k < fk; k++)
                    {
                        float acc = 0.0f;
                        for (int i = k; i < fk; i++) acc += WidthAt(i);
                        offs[k] = fx - acc;
                    }
                }
            }

            span.CharXOffsets.Clear();
            span.CharXOffsets.AddRange(offs);
        }
    }

    /// <summary>
    /// The page's <c>/Rotate</c>, normalized to [0, 360) (document.rs:4294). §7.7.3.3 requires
    /// a multiple of 90; anything else is invalid and reads as 0 rather than being floored.
    /// </summary>
    internal static int GetPageRotation(PdfDocument doc, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= doc.PageCount) return 0;
        double? raw = doc.Resolve(doc.Pages[pageIndex].Get("Rotate")).AsNumber();
        int value = raw is double d && double.IsFinite(d) ? (int)d : 0;
        int n = ((value % 360) + 360) % 360;
        return n % 90 == 0 ? n : 0;
    }

    /// <summary>Index of the first element not less than <paramref name="value"/>.</summary>
    private static int PartitionPointLess(float[] sorted, float value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Index of the first element greater than <paramref name="value"/>.</summary>
    private static int PartitionPointLessOrEqual(float[] sorted, float value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
