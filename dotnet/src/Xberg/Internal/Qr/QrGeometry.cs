namespace Xberg.Internal.Qr;

/// <summary>An integer point in image space.</summary>
internal struct QrPoint
{
    public int X;
    public int Y;
    public QrPoint(int x, int y) { X = x; Y = y; }
}

/// <summary>
/// A projective transform between grid coordinates and image coordinates, ported from rqrr's
/// <c>geometry.rs</c>.
/// </summary>
internal sealed class Perspective
{
    public readonly double[] C;

    private Perspective(double[] c) => C = c;

    /// <summary>
    /// Build the transform that maps the unit rectangle <c>(0,0)-(w,h)</c> onto
    /// <paramref name="rect"/>, or null when the quadrilateral is degenerate.
    /// </summary>
    public static Perspective? Create(QrPoint[] rect, double w, double h)
    {
        var c = new double[8];
        double x0 = rect[0].X, y0 = rect[0].Y;
        double x1 = rect[1].X, y1 = rect[1].Y;
        double x2 = rect[2].X, y2 = rect[2].Y;
        double x3 = rect[3].X, y3 = rect[3].Y;

        double wden = w * (x2 * y3 - x3 * y2 + (x3 - x2) * y1 + x1 * (y2 - y3));
        double hden = h * (x2 * y3 + x1 * (y2 - y3) - x3 * y2 + (x3 - x2) * y1);

        // `< Epsilon` rather than `abs() < Epsilon`: upstream's comparison is signed, so a
        // negative denominator — a quadrilateral wound the other way — is rejected too.
        if (wden < double.Epsilon || hden < double.Epsilon) return null;

        c[0] = (x1 * (x2 * y3 - x3 * y2)
                + x0 * (-x2 * y3 + x3 * y2 + (x2 - x3) * y1)
                + x1 * (x3 - x2) * y0) / wden;
        c[1] = -(x0 * (x2 * y3 + x1 * (y2 - y3) - x2 * y1) - x1 * x3 * y2
                 + x2 * x3 * y1
                 + (x1 * x3 - x2 * x3) * y0) / hden;
        c[2] = x0;
        c[3] = (y0 * (x1 * (y3 - y2) - x2 * y3 + x3 * y2)
                + y1 * (x2 * y3 - x3 * y2)
                + x0 * y1 * (y2 - y3)) / wden;
        c[4] = (x0 * (y1 * y3 - y2 * y3) + x1 * y2 * y3 - x2 * y1 * y3
                + y0 * (x3 * y2 - x1 * y2 + (x2 - x3) * y1)) / hden;
        c[5] = y0;
        c[6] = (x1 * (y3 - y2) + x0 * (y2 - y3) + (x2 - x3) * y1 + (x3 - x2) * y0) / wden;
        c[7] = (-x2 * y3 + x1 * y3 + x3 * y2 + x0 * (y1 - y2) - x3 * y1 + (x2 - x1) * y0) / hden;

        return new Perspective(c);
    }

    public Perspective Clone() => new((double[])C.Clone());

    /// <summary>Grid coordinates to image coordinates.</summary>
    public QrPoint Map(double u, double v)
    {
        double den = C[6] * u + C[7] * v + 1.0;
        double x = (C[0] * u + C[1] * v + C[2]) / den;
        double y = (C[3] * u + C[4] * v + C[5]) / den;
        // Rust's `f64::round` is half-away-from-zero; .NET's default is half-to-even.
        return new QrPoint((int)Math.Round(x, MidpointRounding.AwayFromZero),
                           (int)Math.Round(y, MidpointRounding.AwayFromZero));
    }

    /// <summary>Image coordinates back to grid coordinates.</summary>
    public (double U, double V) Unmap(QrPoint p)
    {
        double x = p.X, y = p.Y;
        double den = -C[0] * C[7] * y
                     + C[1] * C[6] * y
                     + (C[3] * C[7] - C[4] * C[6]) * x
                     + C[0] * C[4]
                     - C[1] * C[3];
        double u = -(C[1] * (y - C[5]) - C[2] * C[7] * y
                     + (C[5] * C[7] - C[4]) * x
                     + C[2] * C[4]) / den;
        double v = (C[0] * (y - C[5]) - C[2] * C[6] * y
                    + (C[5] * C[6] - C[3]) * x
                    + C[2] * C[3]) / den;
        return (u, v);
    }
}

internal static class QrGeometry
{
    /// <summary>Where the lines <c>p0-p1</c> and <c>q0-q1</c> cross, or null when parallel.</summary>
    public static QrPoint? LineIntersect(QrPoint p0, QrPoint p1, QrPoint q0, QrPoint q1)
    {
        // (a, b) is perpendicular to line p, (c, d) to line q.
        int a = -(p1.Y - p0.Y);
        int b = p1.X - p0.X;
        int c = -(q1.Y - q0.Y);
        int d = q1.X - q0.X;
        int e = a * p1.X + b * p1.Y;
        int f = c * q1.X + d * q1.Y;

        int det = a * d - b * c;
        if (det == 0) return null;
        return new QrPoint((d * e - b * f) / det, (-c * e + a * f) / det);
    }

    /// <summary>The pixels along the line from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IEnumerable<QrPoint> BresenhamScan(QrPoint from, QrPoint to)
    {
        int n = to.X - from.X;
        int d = to.Y - from.Y;
        int x = from.X;
        int y = from.Y;

        bool xDom = Math.Abs(n) > Math.Abs(d);
        if (xDom) (n, d) = (d, n);

        int nonStep = 1;
        if (n < 0) { n = -n; nonStep = -1; }

        int domStep = 1;
        if (d < 0) { d = -d; domStep = -1; }

        int a = n;
        for (int i = 0; i <= d; i++)
        {
            yield return new QrPoint(x, y);

            a += n;
            if (xDom)
            {
                x += domStep;
                if (a >= d) { y += nonStep; a -= d; }
            }
            else
            {
                y += domStep;
                if (a >= d) { x += nonStep; a -= d; }
            }
        }
    }
}
