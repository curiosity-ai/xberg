using Xberg.Internal.Qr;
using Xberg.Types;

namespace Xberg.Core;

/// <summary>
/// Decode the QR codes in an image's bytes, ported from Rust <c>extractors/qr.rs</c>.
/// </summary>
public static class QrDetection
{
    /// <summary>
    /// Every QR code found in <paramref name="imageBytes"/>.
    /// </summary>
    /// <param name="imageBytes">The encoded image — PNG, JPEG, WebP, BMP, TIFF, GIF.</param>
    /// <param name="formatHint">
    /// Unused: the container format is detected from the bytes. Kept so a future backend can take
    /// it without an API break, which is why upstream carries it too.
    /// </param>
    /// <returns>
    /// An empty list for empty input, an image that will not decode, no grids found, or grids
    /// that all fail their error correction. Never throws — the caller distinguishes "ran and
    /// found nothing" from "did not run" by whether it called this at all.
    /// </returns>
    public static List<QrCode> Detect(ReadOnlySpan<byte> imageBytes, string? formatHint = null)
    {
        var results = new List<QrCode>();
        foreach (var found in QrScanner.Detect(imageBytes))
        {
            results.Add(new QrCode
            {
                Payload = found.Payload,
                Confidence = 1.0f,
                Bbox = new QrBoundingBox
                {
                    X = (uint)found.X,
                    Y = (uint)found.Y,
                    Width = (uint)found.Width,
                    Height = (uint)found.Height,
                },
            });
        }
        return results;
    }
}
