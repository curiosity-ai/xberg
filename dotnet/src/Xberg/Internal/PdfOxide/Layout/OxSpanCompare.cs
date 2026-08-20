// Ported from pdf_oxide's `utils` module (declared inline in `src/lib.rs`):
// `ROW_BAND_TOLERANCE_PT` (l. 393), `row_aware_span_cmp` (l. 408),
// `sort_vertical_tategaki` (l. 517), `safe_float_cmp` (l. 593) and
// `sort_by_row_band` (l. 614).
//
// Every sort in the reading-order pipeline funnels through these. Rust's
// `sort_by` is stable and the pipeline leans on that: two spans that compare
// equal keep extraction (sequence) order, which is the documented tie-breaker
// for same-baseline runs. `List<T>.Sort` is NOT stable, so every sort here goes
// through <see cref="SortStable"/> (LINQ `OrderBy`, a stable merge sort).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Xberg.Internal.PdfOxide.Layout;

internal static class OxSpanCompare
{
    /// <summary>
    /// Y-band tolerance used by <see cref="RowAwareSpanCmp"/>. Two spans whose top-Y
    /// differs by less than this are treated as one row: enough to absorb baseline
    /// jitter for 10-12pt body text and CJK glyph-cluster offsets, not enough to merge
    /// adjacent 14pt-leading lines.
    /// </summary>
    public const float RowBandTolerancePt = 3.0f;

    /// <summary>
    /// NaN-safe total order over floats: NaNs compare equal to each other and greater
    /// than every number, so a sort can never observe an inconsistent comparison.
    /// </summary>
    /// <remarks>
    /// Not <c>float.CompareTo</c>: that orders NaN below everything and separates
    /// -0.0 from 0.0, both of which would move spans relative to the Rust order.
    /// </remarks>
    public static int SafeFloatCmp(float a, float b)
    {
        bool na = float.IsNaN(a), nb = float.IsNaN(b);
        if (na && nb) return 0;
        if (na) return 1;
        if (nb) return -1;
        if (a < b) return -1;
        if (a > b) return 1;
        return 0;
    }

    /// <summary>
    /// Rust's <c>as i32</c> saturates at the integer bounds where a C# cast wraps to
    /// an arbitrary value, which would drop a degenerate-CTM span into a random band.
    /// </summary>
    public static int SaturatingI32(float v)
    {
        if (float.IsNaN(v)) return 0;
        if (v >= int.MaxValue) return int.MaxValue;
        if (v <= int.MinValue) return int.MinValue;
        return (int)v;
    }

    /// <summary>Rust's <c>f32::round</c>: halfway cases go away from zero.</summary>
    public static int RoundToI32(float v) =>
        SaturatingI32(MathF.Round(v, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Row-aware reading-order comparator: row band descending (larger Y is higher on
    /// the page, ISO 32000-1 §8.3.2.3), then X ascending within the row.
    /// </summary>
    /// <remarks>
    /// Banding keeps tabular layouts whose cells sit at slightly different Y (metric
    /// jitter, superscripts, CJK centering) from interleaving under a strict Y sort.
    /// The band key is an <c>int</c> on purpose: comparing raw Y values with a
    /// tolerance is not transitive and would not be a valid total order.
    /// </remarks>
    public static int RowAwareSpanCmp(float aY, float aX, float bY, float bX)
    {
        // Non-finite Y cannot be quantized — the saturating cast would collapse
        // distinct non-finite values into one band and order them unpredictably
        // against finite spans. Fall back to the NaN-last total order instead.
        if (!float.IsFinite(aY) || !float.IsFinite(bY))
        {
            int nf = SafeFloatCmp(bY, aY);
            return nf != 0 ? nf : SafeFloatCmp(aX, bX);
        }

        int bandA = RoundToI32(aY / RowBandTolerancePt);
        int bandB = RoundToI32(bY / RowBandTolerancePt);
        int c = bandB.CompareTo(bandA);
        return c != 0 ? c : SafeFloatCmp(aX, bX);
    }

    /// <summary>Stable sort in place — the tie-breaking behaviour Rust's `sort_by` has.</summary>
    public static void SortStable<T>(List<T> items, Comparison<T> cmp)
    {
        var ordered = items.OrderBy(x => x, Comparer<T>.Create(cmp)).ToList();
        for (int i = 0; i < items.Count; i++) items[i] = ordered[i];
    }

    /// <summary>
    /// Sort spans into row-band reading order (pdf_oxide `sort_by_row_band`, and the
    /// `ReadingOrder::TopToBottom` branch of `extract_spans_filtered_with_reading_order`).
    /// </summary>
    public static void SortSpansRowAware(List<OxTextSpan> spans) =>
        SortStable(spans, (a, b) =>
            RowAwareSpanCmp(a.Bbox.Y, a.Bbox.X, b.Bbox.Y, b.Bbox.X));

    /// <summary>
    /// Sort into tategaki (vertical-writing) reading order: right-to-left across
    /// columns, top-to-bottom within a column (Y descending, since PDF user-space Y
    /// grows upward).
    /// </summary>
    /// <remarks>
    /// Columns come from single-linkage clustering of X-centres rather than from a
    /// tolerance test inside the comparator: "within tol of each other" is not
    /// transitive, so it is not a valid sort order at all. Clustering first makes both
    /// sort keys discrete and precomputed. It also beats quantizing each centre into a
    /// fixed band, which splits two spans a couple of points apart whenever they
    /// straddle a bucket edge. The tolerance is the median span width — tategaki CJK
    /// body text is effectively monospaced, so that approximates the column pitch.
    /// </remarks>
    public static List<T> SortVerticalTategaki<T>(List<T> items, Func<T, OxRect> getBbox)
    {
        if (items.Count < 2) return items;

        var widths = items.Select(it => MathF.Max(getBbox(it).Width, 1.0f)).ToList();
        SortStable(widths, SafeFloatCmp);
        float tol = MathF.Max(widths[widths.Count / 2], 1.0f);

        var centers = items.Select(it => { var b = getBbox(it); return b.X + b.Width * 0.5f; }).ToList();
        var ys = items.Select(it => getBbox(it).Y).ToList();

        var order = Enumerable.Range(0, items.Count).ToList();
        SortStable(order, (a, b) => SafeFloatCmp(centers[b], centers[a]));

        var column = new int[items.Count];
        int current = 0;
        float prev = centers[order[0]];
        for (int k = 1; k < order.Count; k++)
        {
            int idx = order[k];
            float center = centers[idx];
            // A NaN gap never chains, so a non-finite centre always opens its own column.
            float gap = prev - center;
            if (float.IsNaN(gap) || gap > tol) current++;
            column[idx] = current;
            prev = center;
        }

        // Columns were numbered right-to-left, so ascending column id reads
        // right-to-left; Y descending reads top-to-bottom inside one.
        SortStable(order, (a, b) =>
        {
            int c = column[a].CompareTo(column[b]);
            return c != 0 ? c : SafeFloatCmp(ys[b], ys[a]);
        });

        return order.Select(i => items[i]).ToList();
    }
}
