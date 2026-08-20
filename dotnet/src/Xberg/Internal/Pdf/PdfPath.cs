using System;
using System.Collections.Generic;

namespace Xberg.Internal.Pdf;

/// <summary>A single path-construction operator, in device space.</summary>
/// <remarks>
/// Mirrors pdf_oxide <c>elements::path::PathOperation</c>. Coordinates are already
/// CTM-transformed when the operation is recorded, as the Rust extractor does.
/// </remarks>
public enum PathOpKind { MoveTo, LineTo, CurveTo, Rectangle, ClosePath }

public readonly struct PathOp
{
    public readonly PathOpKind Kind;
    public readonly double X1, Y1, X2, Y2, X3, Y3;

    private PathOp(PathOpKind kind, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        Kind = kind; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3;
    }

    public static PathOp MoveTo(double x, double y) => new(PathOpKind.MoveTo, x, y, 0, 0, 0, 0);
    public static PathOp LineTo(double x, double y) => new(PathOpKind.LineTo, x, y, 0, 0, 0, 0);
    public static PathOp CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(PathOpKind.CurveTo, x1, y1, x2, y2, x3, y3);
    /// <summary>The `re` operator: x, y, width, height (width/height may be negative).</summary>
    public static PathOp Rect(double x, double y, double w, double h) => new(PathOpKind.Rectangle, x, y, w, h, 0, 0);
    public static readonly PathOp Close = new(PathOpKind.ClosePath, 0, 0, 0, 0, 0, 0);
}

/// <summary>Axis-aligned rectangle in PDF device space; <c>Y</c> is the lower edge.</summary>
/// <remarks>Mirrors pdf_oxide's <c>geometry::Rect</c>, including its inverted naming
/// (<c>Top</c> is the smaller Y), so ported comparisons read the same as the Rust.</remarks>
public readonly struct PathRect
{
    public readonly double X, Y, Width, Height;

    /// <summary>Negative extents are normalized, as pdf_oxide's <c>Rect::new</c> does.</summary>
    public PathRect(double x, double y, double w, double h)
    {
        if (w < 0) { x += w; w = -w; }
        if (h < 0) { y += h; h = -h; }
        X = x; Y = y; Width = w; Height = h;
    }
    public double Left => X;
    public double Right => X + Width;
    public double Top => Y;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;

    public bool Intersects(in PathRect o) => Left < o.Right && Right > o.Left && Top < o.Bottom && Bottom > o.Top;
    public bool ContainsRect(in PathRect o) => o.Left >= Left && o.Right <= Right && o.Top >= Top && o.Bottom <= Bottom;
}

/// <summary>A painted path collected from a page's content stream.</summary>
public sealed class PdfPath
{
    public PathRect Bbox;
    public List<PathOp> Operations = new();
    public bool Stroked;
    public bool Filled;
    public double StrokeWidth;
    /// <summary>0 = butt, 1 = round, 2 = projecting square (the `J` operand).</summary>
    public int LineCap;

    public bool HasStroke => Stroked && StrokeWidth > 0.0;

    public bool IsStraightLine =>
        (Operations.Count == 2 && Operations[0].Kind == PathOpKind.MoveTo && Operations[1].Kind == PathOpKind.LineTo)
        || (Operations.Count == 3 && Operations[0].Kind == PathOpKind.MoveTo
            && Operations[1].Kind == PathOpKind.LineTo && Operations[2].Kind == PathOpKind.ClosePath);

    /// <summary>
    /// The geometric bbox inflated by the stroke (ISO 32000-1 §8.4.3.2 — half the line
    /// width straddles each side of the path).
    /// </summary>
    /// <remarks>
    /// Print-era producers draw a table rule as a ~1 pt segment stroked as wide as the
    /// table is tall, so the raw bbox bears no resemblance to the bar the reader sees.
    /// </remarks>
    public PathRect RenderedBbox()
    {
        if (!HasStroke) return Bbox;
        double half = StrokeWidth * 0.5;

        if (Operations.Count >= 2 && Operations[0].Kind == PathOpKind.MoveTo
            && Operations[1].Kind == PathOpKind.LineTo && IsStraightLine)
        {
            double dx = Operations[1].X1 - Operations[0].X1;
            double dy = Operations[1].Y1 - Operations[0].Y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            // A zero-length butt-capped segment paints nothing (§8.4.3.3), so its rendered
            // extent is its degenerate geometry; round/square caps paint a dot.
            if (len <= double.Epsilon && LineCap == 0) return Bbox;
            if (len > double.Epsilon)
            {
                double px = Math.Abs(dy / len * half), py = Math.Abs(dx / len * half);
                double cx = LineCap == 0 ? 0.0 : Math.Abs(dx / len * half);
                double cy = LineCap == 0 ? 0.0 : Math.Abs(dy / len * half);
                return new PathRect(Bbox.X - px - cx, Bbox.Y - py - cy,
                    Bbox.Width + 2.0 * (px + cx), Bbox.Height + 2.0 * (py + cy));
            }
        }

        return new PathRect(Bbox.X - half, Bbox.Y - half, Bbox.Width + 2.0 * half, Bbox.Height + 2.0 * half);
    }

    /// <summary>Whether this path could be part of a table: a thin rule or a box.</summary>
    public bool IsTablePrimitive()
    {
        var rendered = RenderedBbox();

        // Very thin horizontal or vertical line: extends > 5.0 along its rendered axis,
        // geometrically thinner than 2.0 across it, and rendered at least 2:1 elongated —
        // a zero-length segment with a fat round/square cap (dot leaders, bullets) renders
        // as a square blob, not a ruling, and must not seed line clusters.
        if ((Math.Abs(rendered.Width) > 5.0 && Math.Abs(Bbox.Height) < 2.0
             && Math.Abs(rendered.Width) >= 2.0 * Math.Abs(rendered.Height))
            || (Math.Abs(rendered.Height) > 5.0 && Math.Abs(Bbox.Width) < 2.0
                && Math.Abs(rendered.Height) >= 2.0 * Math.Abs(rendered.Width)))
            return true;

        double w = Math.Abs(Bbox.Width), h = Math.Abs(Bbox.Height);
        return w > 5.0 && h > 5.0 && w < 1000.0 && h < 1000.0;
    }

    /// <summary>Minimum length, in points, for a straight line to count as a drawn rule.</summary>
    private const double RuledLineMinLengthPts = 20.0;

    /// <summary>Maximum thickness of a line still considered a rule.</summary>
    private const double GridEdgeAlignTolerancePts = 3.0;

    /// <summary>
    /// Whether this path is a drawn ruling line, as `pdf::rules::count_rules` counts them:
    /// a straight line long enough not to be decoration and thin enough not to be a
    /// diagonal on a chart-heavy page. Independent of <see cref="IsTablePrimitive"/> —
    /// a 2.5 pt rule is a rule but not a primitive.
    /// </summary>
    public bool IsRuleCandidate()
    {
        if (!IsStraightLine) return false;
        double w = Math.Abs(Bbox.Width), h = Math.Abs(Bbox.Height);
        return (w >= RuledLineMinLengthPts && h <= GridEdgeAlignTolerancePts)
            || (h >= RuledLineMinLengthPts && w <= GridEdgeAlignTolerancePts);
    }

    /// <summary>Horizontal and vertical drawn-rule counts for a page's paths.</summary>
    public static (int Horizontal, int Vertical) CountRules(List<PdfPath> paths)
    {
        int horizontal = 0, vertical = 0;
        foreach (var path in paths)
        {
            if (!path.IsStraightLine) continue;
            double w = Math.Abs(path.Bbox.Width), h = Math.Abs(path.Bbox.Height);
            if (w >= RuledLineMinLengthPts && h <= GridEdgeAlignTolerancePts) horizontal++;
            else if (h >= RuledLineMinLengthPts && w <= GridEdgeAlignTolerancePts) vertical++;
        }
        return (horizontal, vertical);
    }

    public static PathRect ComputeBbox(List<PathOp> ops)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case PathOpKind.MoveTo:
                case PathOpKind.LineTo:
                    minX = Math.Min(minX, op.X1); minY = Math.Min(minY, op.Y1);
                    maxX = Math.Max(maxX, op.X1); maxY = Math.Max(maxY, op.Y1);
                    break;
                case PathOpKind.CurveTo:
                    minX = Math.Min(minX, Math.Min(op.X1, Math.Min(op.X2, op.X3)));
                    minY = Math.Min(minY, Math.Min(op.Y1, Math.Min(op.Y2, op.Y3)));
                    maxX = Math.Max(maxX, Math.Max(op.X1, Math.Max(op.X2, op.X3)));
                    maxY = Math.Max(maxY, Math.Max(op.Y1, Math.Max(op.Y2, op.Y3)));
                    break;
                case PathOpKind.Rectangle:
                    minX = Math.Min(minX, op.X1); minY = Math.Min(minY, op.Y1);
                    maxX = Math.Max(maxX, op.X1 + op.X2); maxY = Math.Max(maxY, op.Y1 + op.Y2);
                    break;
                case PathOpKind.ClosePath:
                    break;
            }
        }
        if (minX == double.MaxValue) return new PathRect(0, 0, 0, 0);
        return new PathRect(minX, minY, maxX - minX, maxY - minY);
    }
}
