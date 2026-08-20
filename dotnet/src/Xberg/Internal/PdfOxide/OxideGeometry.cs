// Ported from pdf_oxide `geometry/mod.rs` and the `Matrix` half of
// `content/graphics_state.rs`.
//
// Everything in this namespace works in single precision, as pdf_oxide does:
// the thresholds the whole text pipeline is calibrated against were tuned on
// f32 arithmetic, and widening them silently changes where spans break.
using System;

namespace Xberg.Internal.PdfOxide;

/// <summary>A 2D point in document space.</summary>
public readonly struct OxPoint
{
    public readonly float X;
    public readonly float Y;
    public OxPoint(float x, float y) { X = x; Y = y; }
}

/// <summary>
/// A rectangle in document space. <c>Y</c> is the lower edge in PDF user space, so
/// <see cref="Top"/> is the smaller Y — pdf_oxide's naming, kept so ported comparisons
/// read the same as the Rust they came from.
/// </summary>
public readonly struct OxRect
{
    public readonly float X, Y, Width, Height;

    /// <summary>Negative extents are normalized, so width and height are never negative.</summary>
    public OxRect(float x, float y, float width, float height)
    {
        if (width < 0) { x += width; width = -width; }
        if (height < 0) { y += height; height = -height; }
        X = x; Y = y; Width = width; Height = height;
    }

    /// <summary>Corner-to-corner, without normalizing — the extents may come out negative.</summary>
    public static OxRect FromPoints(float x0, float y0, float x1, float y1)
    {
        var r = default(OxRect);
        return r.WithRaw(x0, y0, x1 - x0, y1 - y0);
    }

    private OxRect(float x, float y, float w, float h, bool _) { X = x; Y = y; Width = w; Height = h; }
    private OxRect WithRaw(float x, float y, float w, float h) => new(x, y, w, h, false);

    public float Left => X;
    public float Right => X + Width;
    public float Top => Y;
    public float Bottom => Y + Height;
    public OxPoint Center => new(X + Width / 2.0f, Y + Height / 2.0f);
    public float Area => Width * Height;
    public OxRect Normalize() => new(X, Y, Width, Height);

    public bool Intersects(in OxRect other) =>
        Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

    public OxRect? Intersection(in OxRect other)
    {
        if (!Intersects(other)) return null;
        float x = MathF.Max(Left, other.Left);
        float y = MathF.Max(Top, other.Top);
        float right = MathF.Min(Right, other.Right);
        float bottom = MathF.Min(Bottom, other.Bottom);
        return new OxRect(x, y, right - x, bottom - y);
    }

    public bool ContainsPoint(in OxPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool ContainsRect(in OxRect other) =>
        other.Left >= Left && other.Right <= Right && other.Top >= Top && other.Bottom <= Bottom;

    public OxRect Union(in OxRect other) => FromPoints(
        MathF.Min(Left, other.Left), MathF.Min(Top, other.Top),
        MathF.Max(Right, other.Right), MathF.Max(Bottom, other.Bottom));

    public override string ToString() => $"({X}, {Y}, {Width}x{Height})";
}

/// <summary>
/// The PDF transformation matrix <c>[a b 0; c d 0; e f 1]</c> (ISO 32000-1 §8.3.3).
/// </summary>
public readonly struct OxMatrix
{
    public readonly float A, B, C, D, E, F;

    public OxMatrix(float a, float b, float c, float d, float e, float f)
    { A = a; B = b; C = c; D = d; E = e; F = f; }

    public static readonly OxMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => A == 1.0f && B == 0.0f && C == 0.0f && D == 1.0f && E == 0.0f && F == 0.0f;

    public static OxMatrix Translation(float tx, float ty) => new(1, 0, 0, 1, tx, ty);
    public static OxMatrix Scaling(float sx, float sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>
    /// The scale this matrix applies to a stroked line width. A non-uniform matrix has no
    /// single width scale, so this is the <c>sqrt(|det|)</c> approximation rasterizers use
    /// (ISO 32000-1 §8.4.3.2) — exact for uniform scaling and rotation.
    /// </summary>
    public float StrokeScale() => MathF.Sqrt(MathF.Abs(A * D - B * C));

    /// <summary><c>this × other</c>, i.e. this transform applied first.</summary>
    public OxMatrix Multiply(in OxMatrix other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        E * other.A + F * other.C + other.E,
        E * other.B + F * other.D + other.F);

    public OxPoint TransformPoint(float x, float y) => new(A * x + C * y + E, B * x + D * y + F);

    public override string ToString() => $"[{A} {B} {C} {D} {E} {F}]";
}
