// Ported from Rust `crates/xberg/src/extraction/exif.rs`.
//
// The Rust code uses `nom-exif`; here we read EXIF via SixLabors.ImageSharp's ExifProfile.
// The tag → field-name mapping mirrors the Rust `TAGS` table so the emitted `exif` map keys
// match. Values are the profile value's display string.

using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Xberg.Internal.Exif;

internal static class ExifReader
{
    // Maps ImageSharp ExifTag numeric ids to the Rust field names.
    // (ExifTag<T>.Number is the TIFF/EXIF tag id.)
    private static readonly Dictionary<ushort, string> TagNames = new()
    {
        // Identity / provenance
        [0x010F] = "Make",
        [0x0110] = "Model",
        [0x0131] = "Software",
        [0x013C] = "HostComputer",
        [0x010E] = "ImageDescription",
        [0x8298] = "Copyright",
        [0xA431] = "CameraSerialNumber",
        [0xA420] = "ImageUniqueID",
        [0x9000] = "ExifVersion",
        // Timestamps
        [0x0132] = "DateTime",
        [0x9003] = "DateTimeOriginal",
        [0x9004] = "DateTimeDigitized",
        [0x9010] = "OffsetTime",
        [0x9011] = "OffsetTimeOriginal",
        [0x9012] = "OffsetTimeDigitized",
        [0x9290] = "SubSecTime",
        [0x9291] = "SubSecTimeOriginal",
        [0x9292] = "SubSecTimeDigitized",
        // Image geometry / resolution
        [0x0100] = "ImageWidth",
        [0x0101] = "ImageHeight",
        [0xA002] = "ExifImageWidth",
        [0xA003] = "ExifImageHeight",
        [0x0112] = "Orientation",
        [0x011A] = "XResolution",
        [0x011B] = "YResolution",
        [0x0128] = "ResolutionUnit",
        [0xA001] = "ColorSpace",
        // Exposure
        [0x829A] = "ExposureTime",
        [0x829D] = "FNumber",
        [0x9202] = "ApertureValue",
        [0x9201] = "ShutterSpeedValue",
        [0x8822] = "ExposureProgram",
        [0xA402] = "ExposureMode",
        [0x9204] = "ExposureBiasValue",
        [0x8827] = "ISO",
        [0x8830] = "SensitivityType",
        [0x9207] = "MeteringMode",
        [0x9208] = "LightSource",
        [0x9209] = "Flash",
        [0xA403] = "WhiteBalance",
        [0xA406] = "SceneCaptureType",
        [0x9206] = "SubjectDistance",
        [0xA40C] = "SubjectDistanceRange",
        [0x9214] = "SubjectArea",
        [0xA404] = "DigitalZoomRatio",
        [0xA408] = "Contrast",
        [0xA409] = "Saturation",
        [0xA40A] = "Sharpness",
        // Lens
        [0x920A] = "FocalLength",
        [0xA405] = "FocalLengthIn35mmFilm",
        [0xA433] = "LensMake",
        [0xA434] = "LensModel",
        [0xA432] = "LensSpecification",
        [0xA435] = "LensSerialNumber",
        // GPS
        [0x0001] = "GPSLatitudeRef",
        [0x0002] = "GPSLatitude",
        [0x0003] = "GPSLongitudeRef",
        [0x0004] = "GPSLongitude",
        [0x0005] = "GPSAltitudeRef",
        [0x0006] = "GPSAltitude",
        [0x0007] = "GPSTimeStamp",
        [0x001D] = "GPSDateStamp",
        [0x000D] = "GPSSpeed",
        [0x000C] = "GPSSpeedRef",
        [0x000F] = "GPSTrack",
        [0x000E] = "GPSTrackRef",
        [0x0011] = "GPSImgDirection",
        [0x0010] = "GPSImgDirectionRef",
        [0x0012] = "GPSMapDatum",
        [0x001B] = "GPSProcessingMethod",
        // Thumbnail
        [0x0201] = "ThumbnailOffset",
        [0x0202] = "ThumbnailLength",
    };

    /// <summary>Build the EXIF tag → display-string map from an image's EXIF profile.
    /// Returns an empty map when EXIF is absent.</summary>
    public static Dictionary<string, string> Extract(ExifProfile? profile)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (profile is null) return map;

        foreach (var value in profile.Values)
        {
            if (!TagNames.TryGetValue((ushort)value.Tag, out var name)) continue;
            string? text = FormatValue(value);
            if (!string.IsNullOrEmpty(text) && !map.ContainsKey(name))
                map[name] = text;
        }
        return map;
    }

    private static string? FormatValue(IExifValue value)
    {
        object? v = value.GetValue();
        if (v is null) return null;

        if (v is Array arr)
        {
            var parts = new List<string>(arr.Length);
            foreach (var item in arr)
                parts.Add(item?.ToString() ?? "");
            return string.Join(" ", parts);
        }
        return v.ToString();
    }
}
