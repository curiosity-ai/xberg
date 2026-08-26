using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>Where a QR code sat inside its source image, in pixels.</summary>
public sealed class QrBoundingBox
{
    [JsonPropertyName("x")] public uint X { get; set; }
    [JsonPropertyName("y")] public uint Y { get; set; }
    [JsonPropertyName("width")] public uint Width { get; set; }
    [JsonPropertyName("height")] public uint Height { get; set; }
}

/// <summary>One decoded QR code.</summary>
public sealed class QrCode
{
    /// <summary>The decoded payload — text, a URL, a vCard string.</summary>
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";

    /// <summary>
    /// Detector confidence in [0, 1], or null where the decoder reports none.
    /// </summary>
    /// <remarks>
    /// Always 1.0 here: the ported rqrr backend exposes no per-grid confidence, and a successful
    /// decode — which means the Reed-Solomon syndromes came out clean — is high-confidence by
    /// construction.
    /// </remarks>
    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Confidence { get; set; }

    [JsonPropertyName("bbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QrBoundingBox? Bbox { get; set; }
}
