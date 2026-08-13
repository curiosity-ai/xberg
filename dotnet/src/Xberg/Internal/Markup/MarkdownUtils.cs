using Xberg.Types;

namespace Xberg.Internal.Markup;

/// <summary>
/// Ports the shared helpers from Rust `crates/xberg/src/extractors/markdown_utils.rs`.
/// </summary>
internal static class MarkdownUtils
{
    /// <summary>
    /// Decode a base64 `data:` image URI into an <see cref="ExtractedImage"/>.
    /// Returns <c>null</c> when the URI is not a base64 data URI in a recognized image format.
    /// `image/svg+xml` is recognized alongside the raster formats (Rust issue #145).
    /// </summary>
    public static ExtractedImage? DecodeDataUriImage(string uri, uint index)
    {
        if (!uri.StartsWith("data:", StringComparison.Ordinal)) return null;
        string afterData = uri["data:".Length..];

        int comma = afterData.IndexOf(',');
        if (comma < 0) return null;
        string mimeAndEncoding = afterData[..comma];
        string data = afterData[(comma + 1)..];

        if (!mimeAndEncoding.Contains("base64", StringComparison.Ordinal)) return null;

        string format;
        if (mimeAndEncoding.Contains("image/png", StringComparison.Ordinal)) format = "png";
        else if (mimeAndEncoding.Contains("image/jpeg", StringComparison.Ordinal)) format = "jpeg";
        else if (mimeAndEncoding.Contains("image/gif", StringComparison.Ordinal)) format = "gif";
        else if (mimeAndEncoding.Contains("image/webp", StringComparison.Ordinal)) format = "webp";
        else if (mimeAndEncoding.Contains("image/svg+xml", StringComparison.Ordinal)) format = "svg";
        else return null;

        string cleaned = data.Replace("\n", "", StringComparison.Ordinal)
                             .Replace("\r", "", StringComparison.Ordinal);

        byte[] decoded;
        try { decoded = Convert.FromBase64String(cleaned); }
        catch (FormatException) { return null; }

        return new ExtractedImage
        {
            Data = decoded,
            Format = format,
            ImageIndex = index,
            IsMask = false,
        };
    }
}
