namespace Xberg.Internal.Qr;

/// <summary>What a pixel has been claimed for during the search.</summary>
/// <remarks>
/// Stored as a byte per pixel: 0 white, 1 black, 2 capstone, 3 alignment, 4 scratch, and 5+ a
/// "discarded" region index. Regions are recoloured rather than tracked separately, which is how
/// rqrr avoids a second buffer.
/// </remarks>
internal static class PixelColor
{
    public const byte White = 0;
    public const byte Black = 1;
    public const byte CapStone = 2;
    public const byte Alignment = 3;
    public const byte Tmp1 = 4;
    public const byte DiscardedBase = 5;
}

/// <summary>A run of pixels on one row, as flood fill discovers it.</summary>
internal readonly record struct FillRow(int Left, int Right, int Y);

/// <summary>What <see cref="QrPreparedImage.GetRegion"/> found at a pixel.</summary>
internal readonly record struct ColoredRegion(
    ColoredRegionKind Kind, byte Color, int SrcX, int SrcY, int PixelCount);

internal enum ColoredRegionKind { Unclaimed, CapStone, Alignment, Tmp1 }

/// <summary>
/// A binarised image that the QR search mutates as it goes, ported from rqrr's
/// <c>prepare.rs</c>.
/// </summary>
/// <remarks>
/// Black regions are recoloured into one of 251 "discarded" shades as they are examined, with an
/// LRU that repaints the least recently used shade back to black when it runs out. That is what
/// keeps the search from re-examining the same huge region over and over.
/// </remarks>
internal sealed class QrPreparedImage
{
    private readonly byte[] _pixels;
    public int Width { get; }
    public int Height { get; }

    private const int CacheCapacity = 251;

    /// <summary>Region index to what is known about it, in least-recently-used order.</summary>
    private readonly LinkedList<(byte Key, ColoredRegion Region)> _lru = new();
    private readonly Dictionary<byte, LinkedListNode<(byte Key, ColoredRegion Region)>> _cache = new();

    private QrPreparedImage(byte[] pixels, int width, int height)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
    }

    public QrPreparedImage Clone()
    {
        var copy = new QrPreparedImage((byte[])_pixels.Clone(), Width, Height);
        foreach (var entry in _lru)
        {
            var node = copy._lru.AddLast(entry);
            copy._cache[entry.Key] = node;
        }
        return copy;
    }

    public byte GetPixel(int x, int y) => _pixels[y * Width + x];
    public void SetPixel(int x, int y, byte value) => _pixels[y * Width + x] = value;

    public byte GetPixelAt(int x, int y) => GetPixel(x, y);

    public byte GetPixelAtPoint(QrPoint p)
    {
        int x = Math.Clamp(p.X, 0, Width - 1);
        int y = Math.Clamp(p.Y, 0, Height - 1);
        return GetPixel(x, y);
    }

    /// <summary>
    /// Binarise a greyscale image with a running local average.
    /// </summary>
    /// <remarks>
    /// The average is carried in both directions along each row — left-to-right and
    /// right-to-left, alternating which one leads on odd rows — so a threshold near a sharp edge
    /// is not dragged by only one side of it. The window is a moving exponential average of width
    /// <c>w/8</c>, and a pixel is black when it sits more than 5% below the local mean.
    /// </remarks>
    public static QrPreparedImage Prepare(byte[] grey, int width, int height)
    {
        var pixels = (byte[])grey.Clone();
        var rowAverage = new int[width];
        int avgV = 0, avgU = 0;
        int thresholdS = Math.Max(width / 8, 1);

        for (int y = 0; y < height; y++)
        {
            Array.Clear(rowAverage);

            for (int x = 0; x < width; x++)
            {
                int v, u;
                if (y % 2 == 0) { v = width - 1 - x; u = x; }
                else { v = x; u = width - 1 - x; }

                avgV = avgV * (thresholdS - 1) / thresholdS + pixels[y * width + v];
                avgU = avgU * (thresholdS - 1) / thresholdS + pixels[y * width + u];
                rowAverage[v] += avgV;
                rowAverage[u] += avgU;
            }

            for (int x = 0; x < width; x++)
            {
                byte fill = pixels[y * width + x] < rowAverage[x] * (100 - 5) / (200 * thresholdS)
                    ? PixelColor.Black
                    : PixelColor.White;
                pixels[y * width + x] = fill;
            }
        }

        return new QrPreparedImage(pixels, width, height);
    }

    // ── regions ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Claim the region containing a pixel, recolouring it so it is not examined twice.
    /// </summary>
    public ColoredRegion GetRegion(int x, int y)
    {
        byte color = GetPixel(x, y);

        if (color >= PixelColor.DiscardedBase)
        {
            byte key = (byte)(color - PixelColor.DiscardedBase);
            var node = _cache[key];
            Touch(node);
            return node.Value.Region;
        }

        switch (color)
        {
            case PixelColor.Tmp1: return new ColoredRegion(ColoredRegionKind.Tmp1, 0, 0, 0, 0);
            case PixelColor.Alignment: return new ColoredRegion(ColoredRegionKind.Alignment, 0, 0, 0, 0);
            case PixelColor.CapStone: return new ColoredRegion(ColoredRegionKind.CapStone, 0, 0, 0, 0);
            case PixelColor.White: throw new InvalidOperationException("tried to colour a white patch");
        }

        byte regIdx;
        if (_cache.Count == CacheCapacity)
        {
            // Evict the least recently used shade and paint its region back to black, so the
            // pixels stay reachable rather than being stranded under a stale index.
            var lruNode = _lru.First!;
            var (evictedKey, evicted) = lruNode.Value;
            _lru.RemoveFirst();
            _cache.Remove(evictedKey);
            if (evicted.Kind == ColoredRegionKind.Unclaimed)
                FloodFill(evicted.SrcX, evicted.SrcY, evicted.Color, PixelColor.Black, null);
            regIdx = evictedKey;
        }
        else regIdx = (byte)_cache.Count;

        byte nextColor = (byte)(PixelColor.DiscardedBase + regIdx);
        int count = 0;
        RepaintAndApply(x, y, nextColor, row => count += row.Right - row.Left + 1);

        var region = new ColoredRegion(ColoredRegionKind.Unclaimed, nextColor, x, y, count);
        var added = _lru.AddLast((regIdx, region));
        _cache[regIdx] = added;
        return region;
    }

    private void Touch(LinkedListNode<(byte Key, ColoredRegion Region)> node)
    {
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    /// <summary>Repaint the region containing a pixel, reporting each filled run.</summary>
    public void RepaintAndApply(int x, int y, byte targetColor, Action<FillRow>? fill)
    {
        byte src = GetPixel(x, y);
        if (src == PixelColor.White || src == targetColor)
            throw new InvalidOperationException("cannot repaint with white or with the same colour");
        FloodFill(x, y, src, targetColor, fill);
    }

    /// <summary>
    /// Scanline flood fill.
    /// </summary>
    /// <remarks>
    /// Seeds are pushed once per contiguous run on the row above and below, not once per pixel,
    /// which is what keeps the queue bounded on a large region.
    /// </remarks>
    private void FloodFill(int x, int y, byte from, byte to, Action<FillRow>? fill)
    {
        if (from == to) throw new ArgumentException("flood fill source and target colours match");

        var queue = new Stack<(int X, int Y)>();
        queue.Push((x, y));

        while (queue.Count > 0)
        {
            var (px, py) = queue.Pop();
            if (GetPixel(px, py) == to || GetPixel(px, py) != from) continue;

            int left = px, right = px;
            while (left > 0 && GetPixel(left - 1, py) == from) left--;
            while (right < Width - 1 && GetPixel(right + 1, py) == from) right++;

            for (int i = left; i <= right; i++) SetPixel(i, py, to);

            fill?.Invoke(new FillRow(left, right, py));

            if (py > 0) SeedRow(queue, left, right, py - 1, from);
            if (py < Height - 1) SeedRow(queue, left, right, py + 1, from);
        }
    }

    private void SeedRow(Stack<(int, int)> queue, int left, int right, int y, byte from)
    {
        bool seededPrevious = false;
        for (int x = left; x <= right; x++)
        {
            if (GetPixel(x, y) == from)
            {
                if (!seededPrevious) queue.Push((x, y));
                seededPrevious = true;
            }
            else seededPrevious = false;
        }
    }
}
