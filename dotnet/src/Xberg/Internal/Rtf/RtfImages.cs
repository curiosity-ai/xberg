// Ported from crates/xberg/src/extractors/rtf/images.rs
// Image metadata and binary data extraction from `\pict` groups.

using System.Text;

namespace Xberg.Internal.Rtf;

/// <summary>Parsed image data from a `\pict` group.</summary>
internal sealed class RtfImage
{
    public string Format { get; set; } = "jpeg";
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

internal static class RtfImages
{
    /// <summary>
    /// Extract image metadata string and binary data from within a `\pict` group.
    /// Mirrors Rust `extract_pict_image`.
    /// </summary>
    public static (string Metadata, RtfImage? Image) ExtractPictImage(CharCursor chars)
    {
        var metadata = new StringBuilder();
        string? imageType = null;
        string format = "jpeg"; // default
        int depth = 0;
        var hexChars = new StringBuilder();

        while (true)
        {
            int ch = chars.Peek();
            if (ch < 0) break;

            if (ch == '{')
            {
                depth += 1;
                chars.Next();
            }
            else if (ch == '}')
            {
                if (depth == 0)
                    break;
                depth -= 1;
                chars.Next();
            }
            else if (ch == '\\')
            {
                chars.Next();
                var (controlWord, value) = RtfEncoding.ParseRtfControlWord(chars);
                switch (controlWord)
                {
                    case "jpegblip":
                        imageType = "jpg";
                        format = "jpeg";
                        break;
                    case "pngblip":
                        imageType = "png";
                        format = "png";
                        break;
                    case "wmetafile":
                        imageType = "wmf";
                        format = "wmf";
                        break;
                    case "dibitmap":
                        imageType = "bmp";
                        format = "bmp";
                        break;
                    case "picwgoal":
                    case "pichgoal":
                        break;
                    case "bin":
                        // \binN means N raw binary bytes follow. Skip them.
                        if (value is int count)
                        {
                            int n = Math.Max(0, count);
                            for (int i = 0; i < n; i++)
                                chars.Next();
                        }
                        break;
                    default:
                        break;
                }
            }
            else if (ch == ' ' || ch == '\r' || ch == '\n')
            {
                chars.Next();
            }
            else
            {
                if (RtfChars.IsAsciiHexDigit(ch))
                    hexChars.Append((char)ch);
                chars.Next();
            }
        }

        if (imageType is not null)
        {
            metadata.Append("image.");
            metadata.Append(imageType);
        }
        if (metadata.Length == 0)
            metadata.Append("image.jpg");

        RtfImage? image = null;
        if (hexChars.Length > 0)
        {
            var data = HexDecode(hexChars.ToString());
            if (data is { Length: > 0 })
                image = new RtfImage { Format = format, Data = data };
        }

        return (metadata.ToString(), image);
    }

    private static byte[]? HexDecode(string hex)
    {
        if ((hex.Length & 1) != 0)
            return null; // odd length: `hex::decode` errors
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            var hi = RtfEncoding.HexDigitToU8((byte)hex[i * 2]);
            var lo = RtfEncoding.HexDigitToU8((byte)hex[i * 2 + 1]);
            if (hi is null || lo is null)
                return null;
            bytes[i] = (byte)((hi.Value << 4) | lo.Value);
        }
        return bytes;
    }
}
