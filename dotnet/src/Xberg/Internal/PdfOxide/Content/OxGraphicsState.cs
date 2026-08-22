// Port of pdf_oxide 0.3.77 `src/content/graphics_state.rs` — `GraphicsState`,
// `GraphicsState::advance_text_matrix`, `is_dashed`, `is_dotted`, and
// `GraphicsStateStack`. The `Matrix` half of that file lives in the spine as
// `OxMatrix` and is used here rather than redefined.

namespace Xberg.Internal.PdfOxide.Content;

/// <summary>
/// Graphics state parameters: transformations, text state, colours and line
/// styles, tracked as operators execute (ISO 32000-1 §8.4).
/// </summary>
internal sealed class OxGraphicsState
{
    /// <summary>Current transformation matrix (user space to device space).</summary>
    internal OxMatrix Ctm = OxMatrix.Identity;

    /// <summary>Text matrix (text space to user space).</summary>
    internal OxMatrix TextMatrix = OxMatrix.Identity;

    /// <summary>Text line matrix — the position saved at the start of the line.</summary>
    internal OxMatrix TextLineMatrix = OxMatrix.Identity;

    // ── Text state ────────────────────────────────────────────────────────

    /// <summary>Character spacing (Tc).</summary>
    internal float CharSpace;

    /// <summary>Word spacing (Tw).</summary>
    internal float WordSpace;

    /// <summary>Horizontal scaling percentage (Tz).</summary>
    internal float HorizontalScaling = 100f;

    /// <summary>Text leading (TL).</summary>
    internal float Leading;

    /// <summary>Current font name.</summary>
    internal string? FontName;

    /// <summary>Current font size (Tf).</summary>
    internal float FontSize = 12f;

    /// <summary>Text rise (Ts).</summary>
    internal float TextRise;

    /// <summary>Text rendering mode (Tr).</summary>
    internal byte RenderMode;

    /// <summary>
    /// Writing mode of the selected font (0 = horizontal, 1 = vertical).
    /// Cached here rather than dereferenced from the font on every Tj/TJ so the
    /// advance helpers branch on a single primitive read; refreshed by Tf.
    /// Defaults to 0 so the horizontal fast path stays hot for states built
    /// without a Tf.
    /// </summary>
    internal byte TextWMode;

    // ── Colour ────────────────────────────────────────────────────────────

    /// <summary>Fill colour space name. PDF default is DeviceGray.</summary>
    internal string FillColorSpace = "DeviceGray";

    /// <summary>Stroke colour space name. PDF default is DeviceGray.</summary>
    internal string StrokeColorSpace = "DeviceGray";

    /// <summary>Fill colour as RGB.</summary>
    internal (float R, float G, float B) FillColorRgb = (0f, 0f, 0f);

    /// <summary>Stroke colour as RGB.</summary>
    internal (float R, float G, float B) StrokeColorRgb = (0f, 0f, 0f);

    /// <summary>Fill colour as CMYK, when a CMYK colour space is in use.</summary>
    internal (float C, float M, float Y, float K)? FillColorCmyk;

    /// <summary>Stroke colour as CMYK, when a CMYK colour space is in use.</summary>
    internal (float C, float M, float Y, float K)? StrokeColorCmyk;

    /// <summary>
    /// Raw fill-colour components from the most recent sc/scn/g/rg/k. Colour is
    /// evaluated to RGB eagerly; the originals are retained so a richer
    /// resolution path (e.g. PostScript Type 4 tint transforms) can re-evaluate
    /// them. Empty means no explicit fill colour has been set yet — the implicit
    /// PDF default is DeviceGray black, recorded as FillColorRgb = (0,0,0).
    /// </summary>
    internal List<float> FillColorComponents = new();

    /// <summary>Raw stroke-colour components from the most recent SC/SCN/G/RG/K.</summary>
    internal List<float> StrokeColorComponents = new();

    // ── Line style ────────────────────────────────────────────────────────

    /// <summary>Line width.</summary>
    internal float LineWidth = 1f;

    /// <summary>Dash pattern [on1, off1, …]; empty means a solid line.</summary>
    internal List<float> DashArray = new();

    /// <summary>Dash phase (offset into the pattern).</summary>
    internal float DashPhase;

    /// <summary>Line cap style (J): 0=butt, 1=round, 2=projecting square.</summary>
    internal byte LineCap;

    /// <summary>Line join style (j): 0=miter, 1=round, 2=bevel.</summary>
    internal byte LineJoin;

    /// <summary>Miter limit (M).</summary>
    internal float MiterLimit = 10f;

    /// <summary>Colour rendering intent (ri).</summary>
    internal string RenderingIntent = "RelativeColorimetric";

    /// <summary>Flatness tolerance (i), 0–100.</summary>
    internal float Flatness = 1f;

    // ── Transparency (from ExtGState) ─────────────────────────────────────

    /// <summary>Fill alpha (ca): 0.0 transparent to 1.0 opaque.</summary>
    internal float FillAlpha = 1f;

    /// <summary>Stroke alpha (CA): 0.0 transparent to 1.0 opaque.</summary>
    internal float StrokeAlpha = 1f;

    /// <summary>Blend mode (BM).</summary>
    internal string BlendMode = "Normal";

    // ── Overprint (ExtGState, ISO 32000-1 §11.7.4) ────────────────────────

    /// <summary>Overprint for non-stroking operations (/op). PDF default false.</summary>
    internal bool FillOverprint;

    /// <summary>Overprint for stroking operations (/OP). PDF default false.</summary>
    internal bool StrokeOverprint;

    /// <summary>Overprint mode (/OPM): 0 = standard, 1 = nonzero. PDF default 0.</summary>
    internal byte OverprintMode;

    /// <summary>
    /// Active spot inks paired with their tint values for the most recent fill,
    /// in source colorant declaration order (ISO 32000-1 §8.6.6.4 /Separation,
    /// §8.6.6.5 /DeviceN). /All and /None are surfaced verbatim so the §8.6.6.3
    /// reserved-name branch can dispatch on them. Empty means the fill came from
    /// a Device-family, CIE-based or Indexed source.
    /// </summary>
    internal List<(string Ink, float Tint)> FillSpotInks = new();

    /// <summary>Stroke-side counterpart of <see cref="FillSpotInks"/> (§8.6.5.1).</summary>
    internal List<(string Ink, float Tint)> StrokeSpotInks = new();

    /// <summary>
    /// Pattern selected by the most recent scn while the fill colour space is
    /// /Pattern (ISO 32000-1 §8.7.3). Kept so the fill path can look the pattern
    /// up in Resources/Pattern/&lt;name&gt;. Cleared when a device/CIE fill colour
    /// is set.
    /// </summary>
    internal string? FillPatternName;

    /// <summary>Deep-enough copy for q/Q: the mutable collections are cloned so
    /// a restored state never observes writes made inside the saved scope.</summary>
    internal OxGraphicsState Clone()
    {
        var copy = (OxGraphicsState)MemberwiseClone();
        copy.FillColorComponents = new List<float>(FillColorComponents);
        copy.StrokeColorComponents = new List<float>(StrokeColorComponents);
        copy.DashArray = new List<float>(DashArray);
        copy.FillSpotInks = new List<(string, float)>(FillSpotInks);
        copy.StrokeSpotInks = new List<(string, float)>(StrokeSpotInks);
        return copy;
    }

    /// <summary>
    /// Apply a text-space displacement to the text matrix on the active writing
    /// axis and return the resulting (Δe, Δf) user-space deltas.
    /// Per ISO 32000-1 §9.4.4 a show-text operator updates Tm := [1 0 0 1 tx 0] × Tm
    /// after horizontal advancement, which adds tx*a to Tm.e and tx*b to Tm.f. In
    /// vertical writing mode the displacement routes into the y-column instead:
    /// Tm := [1 0 0 1 0 ty] × Tm, adding ty*c to Tm.e and ty*d to Tm.f.
    /// This is the single axis-swap site for every advance helper.
    /// </summary>
    internal (float De, float Df) AdvanceTextMatrix(float displacement)
    {
        var tm = TextMatrix;
        (float de, float df) = TextWMode == 0
            ? (displacement * tm.A, displacement * tm.B)
            : (displacement * tm.C, displacement * tm.D);
        TextMatrix = tm with { E = tm.E + de, F = tm.F + df };
        return (de, df);
    }

    /// <summary>True when the current line style is dashed rather than solid.</summary>
    internal bool IsDashed() => DashArray.Count > 0;

    /// <summary>True when the dash pattern looks dotted: short, near-equal on/off runs.</summary>
    internal bool IsDotted()
    {
        if (DashArray.Count < 2)
        {
            return false;
        }

        float on = DashArray[0];
        float off = DashArray[1];
        return on < 5f && off < 5f && Math.Abs(on - off) < 2f;
    }
}

/// <summary>
/// Stack of graphics states for the q (save) and Q (restore) operators.
/// </summary>
internal sealed class OxGraphicsStateStack
{
    private readonly List<OxGraphicsState> _stack = new() { new OxGraphicsState() };

    /// <summary>The current graphics state; the stack is never empty.</summary>
    internal OxGraphicsState Current => _stack[^1];

    /// <summary>Stack depth, always at least 1.</summary>
    internal int Depth => _stack.Count;

    /// <summary>Save the current graphics state (q).</summary>
    internal void Save() => _stack.Add(Current.Clone());

    /// <summary>
    /// Restore the previous graphics state (Q). Unbalanced Q operators are
    /// common in real streams, so popping the last state is a no-op rather than
    /// an error.
    /// </summary>
    internal void Restore()
    {
        if (_stack.Count > 1)
        {
            _stack.RemoveAt(_stack.Count - 1);
        }
    }
}
