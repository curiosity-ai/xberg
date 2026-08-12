// Ported from Rust `crates/xberg/src/extractors/image.rs` + `extraction/image.rs`.
//
// Metadata/EXIF only — the OCR path is dropped (out of scope for the port). This mirrors
// the Rust `config.effective_disable_ocr()` branch: build a metadata-only document carrying
// an Image element plus `ImageMetadata` (width, height, format, exif). Dimensions and format
// come from SixLabors.ImageSharp; EXIF from the ImageSharp ExifProfile (see ExifReader).

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using Xberg.Core;
using Xberg.Internal.Exif;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Image metadata extractor (PNG/JPEG/WebP/BMP/TIFF/GIF). No OCR.</summary>
public sealed class ImageExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/pjpeg",
        "image/webp",
        "image/bmp",
        "image/x-bmp",
        "image/x-ms-bmp",
        "image/tiff",
        "image/x-tiff",
        "image/gif",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        byte[] bytes = content.ToArray();

        ImageInfo info = Image.Identify(bytes);
        string format = FormatName(info.Metadata.DecodedImageFormat);
        var exif = ExifReader.Extract(info.Metadata.ExifProfile);

        var imageMeta = new ImageMetadata
        {
            Width = (uint)info.Width,
            Height = (uint)info.Height,
            Format = format,
            Exif = exif,
        };

        // build_image_internal_document(None, None): a single Image element referencing
        // image index 0 (no bytes stored in doc.images by default).
        var builder = new InternalDocumentBuilder("image");
        var kind = ElementKind.Image(0);
        builder.PushElement(new InternalElement
        {
            Id = InternalElementId.Generate(kind.Discriminant(), "", null, 0),
            Kind = kind,
            Text = "",
            Depth = 0,
            Layer = ContentLayer.Body,
        });

        var doc = builder.Build();
        doc.Metadata = new Metadata { Format = FormatMetadata.Image(imageMeta) };
        doc.MimeType = mimeType;
        return doc;
    }

    // Rust uses `format!("{:?}", image::ImageFormat).to_uppercase()`; ImageSharp's format
    // name maps 1:1 for the common raster formats (PNG/JPEG/GIF/BMP/TIFF/WEBP).
    private static string FormatName(IImageFormat? format) =>
        format?.Name.ToUpperInvariant() ?? "";
}
