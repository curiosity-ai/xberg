using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>
/// Central <see cref="JsonSerializerOptions"/> for the port. Field names are emitted in
/// snake_case to match the Rust `serde` output that the golden reference files use.
/// Absent (null) fields are omitted, mirroring serde's `skip_serializing_if = "Option::is_none"`.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = SerdeJsonEncoder.Shared,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        // serde always writes a fractional part, so an integral float is `0.0`, not `0`.
        o.Converters.Add(new SerdeDoubleConverter());
        o.Converters.Add(new SerdeSingleConverter());
        // The tagged-union converters are applied via [JsonConverter] attributes on their types.
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
