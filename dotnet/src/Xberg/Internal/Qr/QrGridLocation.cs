namespace Xberg.Internal.Qr;

/// <summary>
/// Where a skewed QR square sits in an image, and how to read modules out of it. Ported from
/// rqrr's <c>identify/grid.rs</c>.
/// </summary>
internal sealed class SkewedGridLocation
{
    public required int GridSize { get; init; }
    public required Perspective C { get; init; }

    /// <summary>
    /// Fit a grid to three capstones, or null when they do not corner a readable code.
    /// </summary>
    /// <remarks>
    /// Timing patterns between the capstones give the size; for version 2 and up the fourth
    /// corner's alignment pattern is searched for and used, because three corners alone do not
    /// pin the perspective of a large code tightly enough.
    /// </remarks>
    public static SkewedGridLocation? FromGroup(QrPreparedImage img, CapStoneGroup group)
    {
        var a = group.A;
        var b = group.B;
        var c = group.C;

        // The hypotenuse runs A to C, with B to its left; swap A and C if the winding is wrong,
        // so the three are always clockwise.
        var h0 = a.Center;
        var hd = new QrPoint(c.Center.X - a.Center.X, c.Center.Y - a.Center.Y);

        if ((b.Center.X - h0.X) * -hd.Y + (b.Center.Y - h0.Y) * hd.X > 0)
        {
            (a, c) = (c, a);
            hd = new QrPoint(-hd.X, -hd.Y);
        }

        // Rotate each capstone so corner 0 is its top-left with respect to the grid.
        RotateCapstone(a, h0, hd);
        RotateCapstone(b, h0, hd);
        RotateCapstone(c, h0, hd);

        var caps = new CapStoneGroup(a, b, c);
        int gridSize = MeasureTimingPattern(img, caps);

        // Estimate the fourth corner by extending the edges of A and C.
        var align = QrGeometry.LineIntersect(a.Corners[0], a.Corners[1], c.Corners[0], c.Corners[3]);
        if (align is not { } alignPoint) return null;

        if (gridSize > 21)
        {
            var found = FindAlignmentPattern(img, alignPoint, a, c);
            if (found is not { } located) return null;
            alignPoint = located;

            // Walk the alignment pattern's own region to its leftmost point along the hypotenuse,
            // which is a better corner than the seed the spiral happened to land on.
            int score = -hd.Y * alignPoint.X + hd.X * alignPoint.Y;
            var finder = new LeftMostFinder(hd, alignPoint, score);
            img.RepaintAndApply(alignPoint.X, alignPoint.Y, PixelColor.Alignment, finder.Update);
            alignPoint = finder.Best;
        }

        if (VersionFromGridSize(gridSize) >= QrVersionDb.Versions.Length) return null;

        var perspective = SetupPerspective(img, caps, alignPoint, gridSize);
        if (perspective is null) return null;

        return new SkewedGridLocation { GridSize = gridSize, C = perspective };
    }

    private static int VersionFromGridSize(int gridSize) => (gridSize - 17) / 4;

    private static Perspective? SetupPerspective(
        QrPreparedImage img, CapStoneGroup caps, QrPoint align, int gridSize)
    {
        var initial = Perspective.Create(
            new[] { caps.B.Corners[0], caps.C.Corners[0], align, caps.A.Corners[0] },
            gridSize - 7, gridSize - 7);
        if (initial is null) return null;
        return JigglePerspective(img, initial, gridSize);
    }

    /// <summary>Rotate a capstone's corners so corner 0 is the one nearest the code's centre.</summary>
    private static void RotateCapstone(CapStone cap, QrPoint h0, QrPoint hd)
    {
        int bestIdx = 0;
        long bestScore = long.MaxValue;
        for (int i = 0; i < cap.Corners.Length; i++)
        {
            long score = (long)(cap.Corners[i].X - h0.X) * -hd.Y + (long)(cap.Corners[i].Y - h0.Y) * hd.X;
            // `min_by_key` keeps the *first* minimum, so the comparison is strict.
            if (score < bestScore) { bestScore = score; bestIdx = i; }
        }

        var rotated = new QrPoint[4];
        for (int i = 0; i < 4; i++) rotated[i] = cap.Corners[(i + bestIdx) % 4];
        cap.Corners = rotated;
        cap.C = Perspective.Create(rotated, 7.0, 7.0)
                ?? throw new InvalidOperationException("a rotated capstone perspective cannot fail");
    }

    /// <summary>
    /// Count the black/white transitions along the timing patterns to get the grid size.
    /// </summary>
    /// <remarks>
    /// Needs no global perspective — only that the capstone corners have been rotated to
    /// canonical order. The larger of the horizontal and vertical scans wins, then the count is
    /// rounded to the nearest legal size.
    /// </remarks>
    private static int MeasureTimingPattern(QrPreparedImage img, CapStoneGroup caps)
    {
        double[] us = { 6.5, 6.5, 0.5 };
        double[] vs = { 0.5, 6.5, 6.5 };
        var tpet0 = caps.A.C.Map(us[0], vs[0]);
        var tpet1 = caps.B.C.Map(us[1], vs[1]);
        var tpet2 = caps.C.C.Map(us[2], vs[2]);

        int hscan = TimingScan(img, tpet1, tpet2);
        int vscan = TimingScan(img, tpet1, tpet0);
        int scan = Math.Max(hscan, vscan);

        int size = scan + 13;
        int ver = (int)Math.Floor(size - 15.0) / 4;
        return ver * 4 + 17;
    }

    private static int TimingScan(QrPreparedImage img, QrPoint p0, QrPoint p1)
    {
        int count = 0;
        byte? previous = null;
        foreach (var p in QrGeometry.BresenhamScan(p0, p1))
        {
            byte pixel = img.GetPixelAtPoint(p);
            if (previous is { } prev && prev != pixel) count++;
            previous = pixel;
        }
        return count;
    }

    /// <summary>
    /// Spiral outwards from the estimated fourth corner until a region of about the right size
    /// turns up — the alignment pattern.
    /// </summary>
    private static QrPoint? FindAlignmentPattern(
        QrPreparedImage img, QrPoint alignSeed, CapStone c0, CapStone c2)
    {
        // Guess two more corners of the pattern so its area can be estimated.
        var (u0, v0) = c0.C.Unmap(alignSeed);
        var a = c0.C.Map(u0, v0 + 1.0);
        var (u2, v2) = c2.C.Unmap(alignSeed);
        var c = c2.C.Map(u2 + 1.0, v2);
        long cross = (long)(a.X - alignSeed.X) * -(c.Y - alignSeed.Y)
                     + (long)(a.Y - alignSeed.Y) * (c.X - alignSeed.X);
        int sizeEstimate = (int)Math.Abs(cross);

        int dir = 0;
        int stepSize = 1;

        while ((long)stepSize * stepSize < (long)sizeEstimate * 100)
        {
            int[] dxMap = { 1, 0, -1, 0 };
            int[] dyMap = { 0, -1, 0, 1 };

            for (int pass = 0; pass < stepSize; pass++)
            {
                // Upstream casts to `usize`, so a negative coordinate wraps to something huge and
                // fails the bounds test rather than indexing out of range. The signed test here
                // rejects the same points.
                int x = alignSeed.X;
                int y = alignSeed.Y;

                if (x >= 0 && y >= 0 && x < img.Width && y < img.Height
                    && img.GetPixelAt(x, y) != PixelColor.White)
                {
                    var region = img.GetRegion(x, y);
                    if (region.Kind == ColoredRegionKind.Unclaimed
                        && region.PixelCount >= sizeEstimate / 2
                        && region.PixelCount <= sizeEstimate * 2)
                        return alignSeed;
                }

                alignSeed = new QrPoint(alignSeed.X + dxMap[dir], alignSeed.Y + dyMap[dir]);
            }

            dir = (dir + 1) % 4;
            if ((dir & 1) == 0) stepSize++;
        }

        return null;
    }

    /// <summary>The point of a region farthest to the left of a reference line.</summary>
    private sealed class LeftMostFinder
    {
        private readonly QrPoint _lineP;
        public QrPoint Best;
        private int _score;

        public LeftMostFinder(QrPoint lineP, QrPoint best, int score)
        {
            _lineP = lineP;
            Best = best;
            _score = score;
        }

        public void Update(FillRow row)
        {
            int leftD = -_lineP.Y * row.Left + _lineP.X * row.Y;
            int rightD = -_lineP.Y * row.Right + _lineP.X * row.Y;

            if (leftD < _score) { _score = leftD; Best = new QrPoint(row.Left, row.Y); }
            if (rightD < _score) { _score = rightD; Best = new QrPoint(row.Right, row.Y); }
        }
    }

    /// <summary>
    /// Nudge each of the eight perspective coefficients up and down, keeping any change that
    /// improves how well the transform predicts the patterns the code must contain.
    /// </summary>
    private static Perspective JigglePerspective(
        QrPreparedImage img, Perspective perspective, int gridSize)
    {
        int best = FitnessAll(img, perspective, gridSize);
        var adjustments = new double[8];
        for (int i = 0; i < 8; i++) adjustments[i] = perspective.C[i] * 0.02;

        for (int pass = 0; pass < 5; pass++)
        {
            for (int i = 0; i < 16; i++)
            {
                int j = i >> 1;
                double old = perspective.C[j];
                double step = adjustments[j];
                perspective.C[j] = (i & 1) != 0 ? old + step : old - step;

                int test = FitnessAll(img, perspective, gridSize);
                if (test > best) best = test;
                else perspective.C[j] = old;
            }

            for (int i = 0; i < 8; i++) adjustments[i] *= 0.5;
        }

        return perspective;
    }

    /// <summary>
    /// How well a transform predicts the features a code of this size must have: the timing
    /// patterns, the three capstones, and every alignment pattern the version declares.
    /// </summary>
    private static int FitnessAll(QrPreparedImage img, Perspective perspective, int gridSize)
    {
        var info = QrVersionDb.Versions[VersionFromGridSize(gridSize)];
        int score = 0;

        for (int i = 0; i < gridSize - 14; i++)
        {
            int expect = (i & 1) != 0 ? 1 : -1;
            score += FitnessCell(img, perspective, i + 7, 6) * expect;
            score += FitnessCell(img, perspective, 6, i + 7) * expect;
        }

        score += FitnessCapstone(img, perspective, 0, 0);
        score += FitnessCapstone(img, perspective, gridSize - 7, 0);
        score += FitnessCapstone(img, perspective, 0, gridSize - 7);

        int apCount = 0;
        while (apCount < 7 && info.Apat[apCount] != 0) apCount++;

        for (int i = 1; i < Math.Max(0, apCount - 1); i++)
        {
            score += FitnessApat(img, perspective, 6, info.Apat[i]);
            score += FitnessApat(img, perspective, info.Apat[i], 6);
        }
        for (int i = 1; i < apCount; i++)
            for (int j = 1; j < apCount; j++)
                score += FitnessApat(img, perspective, info.Apat[i], info.Apat[j]);

        return score;
    }

    private static int FitnessApat(QrPreparedImage img, Perspective p, int cx, int cy) =>
        FitnessCell(img, p, cx, cy) - FitnessRing(img, p, cx, cy, 1) + FitnessRing(img, p, cx, cy, 2);

    private static int FitnessCapstone(QrPreparedImage img, Perspective p, int x, int y) =>
        FitnessCell(img, p, x + 3, y + 3)
        + FitnessRing(img, p, x + 3, y + 3, 1)
        - FitnessRing(img, p, x + 3, y + 3, 2)
        + FitnessRing(img, p, x + 3, y + 3, 3);

    private static int FitnessRing(QrPreparedImage img, Perspective p, int cx, int cy, int radius)
    {
        int score = 0;
        for (int i = 0; i < radius * 2; i++)
        {
            score += FitnessCell(img, p, cx - radius + i, cy - radius);
            score += FitnessCell(img, p, cx - radius, cy + radius - i);
            score += FitnessCell(img, p, cx + radius, cy - radius + i);
            score += FitnessCell(img, p, cx + radius - i, cy + radius);
        }
        return score;
    }

    /// <summary>Sample one module at nine points; dark scores up, light scores down.</summary>
    private static int FitnessCell(QrPreparedImage img, Perspective p, int x, int y)
    {
        double[] offsets = { 0.3, 0.5, 0.7 };
        int score = 0;
        for (int v = 0; v < 3; v++)
        {
            for (int u = 0; u < 3; u++)
            {
                var point = p.Map(x + offsets[u], y + offsets[v]);
                if (point.Y < 0 || point.Y >= img.Height || point.X < 0 || point.X >= img.Width)
                    continue;
                score += img.GetPixelAtPoint(point) != PixelColor.White ? 1 : -1;
            }
        }
        return score;
    }
}

/// <summary>A grid that reads its modules out of the image it was located in.</summary>
internal sealed class RefGridImage : IBitGrid
{
    private readonly SkewedGridLocation _grid;
    private readonly QrPreparedImage _img;

    public RefGridImage(SkewedGridLocation grid, QrPreparedImage img)
    {
        _grid = grid;
        _img = img;
    }

    public int Size => _grid.GridSize;

    public bool Bit(int y, int x) =>
        _img.GetPixelAtPoint(_grid.C.Map(x + 0.5, y + 0.5)) != PixelColor.White;
}
