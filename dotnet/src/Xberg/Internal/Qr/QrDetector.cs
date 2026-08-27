namespace Xberg.Internal.Qr;

/// <summary>
/// A locator pattern — one of the three corner squares every QR code carries, found by its
/// distinctive 1:1:3:1:1 run of black and white.
/// </summary>
internal sealed class CapStone
{
    public required QrPoint[] Corners { get; set; }
    public required QrPoint Center { get; set; }

    /// <summary>The local perspective: which way this corner is skewed.</summary>
    public required Perspective C { get; set; }

    public CapStone Clone() => new()
    {
        Corners = (QrPoint[])Corners.Clone(),
        Center = Center,
        C = C.Clone(),
    };
}

/// <summary>Three capstones that look like the corners of one code.</summary>
internal sealed record CapStoneGroup(CapStone A, CapStone B, CapStone C);

/// <summary>
/// Find the locator patterns in a prepared image, ported from rqrr's <c>detect.rs</c>.
/// </summary>
internal static class QrDetector
{
    private readonly record struct LinePosition(int Left, int Stone, int Right);

    /// <summary>
    /// Scan the image row by row for the 1:1:3:1:1 pattern and confirm each candidate.
    /// </summary>
    public static List<CapStone> CapstonesFromImage(QrPreparedImage img)
    {
        var result = new List<CapStone>();

        for (int y = 0; y < img.Height; y++)
        {
            var finder = new LineScanner(img.GetPixelAt(0, y));
            for (int x = 1; x < img.Width; x++)
            {
                if (finder.Advance(img.GetPixelAt(x, y)) is not { } linepos) continue;
                if (!IsCapstone(img, linepos, y)) continue;
                if (CreateCapstone(img, linepos, y) is { } cap) result.Add(cap);
            }

            // A virtual white pixel past the end, so a capstone sitting flush against the right
            // edge still triggers the check that a colour change would have.
            if (finder.Advance(PixelColor.White) is { } tail)
            {
                if (!IsCapstone(img, tail, y)) continue;
                if (CreateCapstone(img, tail, y) is { } cap) result.Add(cap);
            }
        }

        return result;
    }

    /// <summary>
    /// Tracks black/white run lengths along one row and reports when the last five match
    /// 1:1:3:1:1 — the cross-section of a locator pattern.
    /// </summary>
    private sealed class LineScanner
    {
        private readonly int[] _lookbehind = new int[5];
        private byte _lastColor;
        private int _runLength = 1;
        private int _colorChanges;
        private int _currentPosition;

        public LineScanner(byte initialColor) => _lastColor = initialColor;

        public LinePosition? Advance(byte color)
        {
            _currentPosition++;

            if (_lastColor == color) { _runLength++; return null; }

            _lastColor = color;
            for (int i = 0; i < 4; i++) _lookbehind[i] = _lookbehind[i + 1];
            _lookbehind[4] = _runLength;
            _runLength = 1;
            _colorChanges++;

            if (!TestForCapstone()) return null;

            int sum = 0;
            foreach (int v in _lookbehind) sum += v;
            int stoneSum = _lookbehind[2] + _lookbehind[3] + _lookbehind[4];
            return new LinePosition(
                _currentPosition - sum,
                _currentPosition - stoneSum,
                _currentPosition - _lookbehind[4]);
        }

        private bool TestForCapstone()
        {
            // The pattern reads `> x xxx x <`, so it can only have completed on a change back to
            // white, and only once five runs have been seen.
            if (_lastColor != PixelColor.White || _colorChanges < 5) return false;

            ReadOnlySpan<int> check = stackalloc int[] { 1, 1, 3, 1, 1 };
            int avg = (_lookbehind[0] + _lookbehind[1] + _lookbehind[3] + _lookbehind[4]) / 4;
            int err = avg * 3 / 4;

            for (int i = 0; i < 5; i++)
                if (_lookbehind[i] < check[i] * avg - err || _lookbehind[i] > check[i] * avg + err)
                    return false;

            return true;
        }
    }

    /// <summary>
    /// Whether a candidate position really is an unclaimed locator pattern: left and right joined
    /// into one ring, the stone separate from it, and their areas in roughly the 37.5% ratio the
    /// 1:3 nesting implies.
    /// </summary>
    private static bool IsCapstone(QrPreparedImage img, LinePosition linepos, int y)
    {
        var ringReg = img.GetRegion(linepos.Right, y);
        var stoneReg = img.GetRegion(linepos.Stone, y);

        if (img.GetPixelAt(linepos.Left, y) != img.GetPixelAt(linepos.Right, y)) return false;
        if (ringReg.Kind != ColoredRegionKind.Unclaimed || stoneReg.Kind != ColoredRegionKind.Unclaimed)
            return false;

        int ratio = stoneReg.PixelCount * 100 / ringReg.PixelCount;
        return ringReg.Color != stoneReg.Color && 10 < ratio && ratio < 70;
    }

    /// <summary>
    /// Measure a capstone's extent and skew, marking its ring and stone claimed so it is not
    /// found again.
    /// </summary>
    private static CapStone? CreateCapstone(QrPreparedImage img, LinePosition linepos, int y)
    {
        var start = new QrPoint(linepos.Right, y);

        // Two passes: the first finds the corner farthest from the seed, the second uses that as
        // a baseline to find all four.
        var first = new FirstCornerFinder(start);
        img.RepaintAndApply(linepos.Right, y, PixelColor.Tmp1, first.Update);

        var all = new AllCornerFinder(start, first.Best);
        img.RepaintAndApply(linepos.Right, y, PixelColor.CapStone, all.Update);
        var corners = all.Best;

        var c = Perspective.Create(corners, 7.0, 7.0);
        if (c is null) return null;

        return new CapStone { C = c, Corners = corners, Center = c.Map(3.5, 3.5) };
    }

    /// <summary>The point of a sheared rectangle farthest from a reference point — a corner.</summary>
    private sealed class FirstCornerFinder
    {
        private readonly QrPoint _initial;
        public QrPoint Best;
        private int _score = -1;

        public FirstCornerFinder(QrPoint initial) => _initial = initial;

        public void Update(FillRow row)
        {
            int dy = row.Y - _initial.Y;
            int lDx = row.Left - _initial.X;
            int rDx = row.Right - _initial.X;

            int lDist = lDx * lDx + dy * dy;
            int rDist = rDx * rDx + dy * dy;

            if (lDist > _score) { _score = lDist; Best = new QrPoint(row.Left, row.Y); }
            if (rDist > _score) { _score = rDist; Best = new QrPoint(row.Right, row.Y); }
        }
    }

    /// <summary>
    /// All four corners of a rectangle, from a point inside it and one known corner: the opposite
    /// corner lies farthest along the reference line, the other two farthest to either side.
    /// </summary>
    private sealed class AllCornerFinder
    {
        private readonly QrPoint _baseline;
        public readonly QrPoint[] Best;
        private readonly int[] _scores;

        public AllCornerFinder(QrPoint initial, QrPoint corner)
        {
            _baseline = new QrPoint(corner.X - initial.X, corner.Y - initial.Y);

            int parallel = initial.X * _baseline.X + initial.Y * _baseline.Y;
            int orthogonal = -initial.X * _baseline.Y + initial.Y * _baseline.X;

            Best = new[] { initial, initial, initial, initial };
            _scores = new[] { parallel, orthogonal, -parallel, -orthogonal };
        }

        public void Update(FillRow row)
        {
            int lPar = row.Left * _baseline.X + row.Y * _baseline.Y;
            int lOrt = -row.Left * _baseline.Y + row.Y * _baseline.X;
            Span<int> lScores = stackalloc int[] { lPar, lOrt, -lPar, -lOrt };

            int rPar = row.Right * _baseline.X + row.Y * _baseline.Y;
            int rOrt = -row.Right * _baseline.Y + row.Y * _baseline.X;
            Span<int> rScores = stackalloc int[] { rPar, rOrt, -rPar, -rOrt };

            for (int j = 0; j < 4; j++)
            {
                if (lScores[j] > _scores[j])
                {
                    _scores[j] = lScores[j];
                    Best[j] = new QrPoint(row.Left, row.Y);
                }
                if (rScores[j] > _scores[j])
                {
                    _scores[j] = rScores[j];
                    Best[j] = new QrPoint(row.Right, row.Y);
                }
            }
        }
    }

    // ── grouping ─────────────────────────────────────────────────────────────

    private readonly record struct Neighbor(int Index, double Distance);

    /// <summary>
    /// Pairs of capstones that could corner the same code as <paramref name="idx"/>, most
    /// symmetric first.
    /// </summary>
    public static List<(int H, int V)> FindAndRankPossibleNeighbors(List<CapStone> capstones, int idx)
    {
        const double ViabilityThreshold = 0.25;

        var (hlist, vlist) = FindPossibleNeighbors(capstones, idx);
        var scored = new List<(double Score, int H, int V)>();

        foreach (var hn in hlist)
        {
            foreach (var vn in vlist)
            {
                double score = hn.Distance < vn.Distance
                    ? Math.Abs(1.0 - hn.Distance / vn.Distance)
                    : Math.Abs(1.0 - vn.Distance / hn.Distance);
                if (score < ViabilityThreshold) scored.Add((score, hn.Index, vn.Index));
            }
        }

        // `sort_unstable_by` on the score. Ties keep whatever order the nested loops produced,
        // which is deterministic here because both lists are built in index order.
        scored.Sort((a, b) => a.Score.CompareTo(b.Score));
        return scored.Select(s => (s.H, s.V)).ToList();
    }

    /// <summary>
    /// Split the other capstones into those roughly horizontal from this one and those roughly
    /// vertical, judged in this capstone's own skewed frame rather than in image space.
    /// </summary>
    private static (List<Neighbor> H, List<Neighbor> V) FindPossibleNeighbors(
        List<CapStone> capstones, int idx)
    {
        var cap = capstones[idx];
        var hlist = new List<Neighbor>();
        var vlist = new List<Neighbor>();

        for (int other = 0; other < capstones.Count; other++)
        {
            if (other == idx) continue;

            var (u, v) = cap.C.Unmap(capstones[other].Center);
            u = Math.Abs(u - 3.5);
            v = Math.Abs(v - 3.5);

            if (u < 0.2 * v) hlist.Add(new Neighbor(other, v));
            if (v < 0.2 * u) vlist.Add(new Neighbor(other, u));
        }

        return (hlist, vlist);
    }
}
