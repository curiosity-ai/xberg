using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xberg.Types;

/// <summary>
/// serde-faithful floating-point formatting. <c>serde_json</c> always writes a fractional part
/// or an exponent, so an f32/f64 holding an integral value serializes as <c>0.0</c>, never
/// <c>0</c> — while <see cref="System.Text.Json"/> writes the bare integer. Golden comparison is
/// string-exact, so the difference is not cosmetic.
/// </summary>
public static class SerdeFloat
{
    /// <summary>Render a double the way <c>serde_json</c> (ryu) would.</summary>
    public static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "null"; // serde_json cannot encode these

        // .NET's default double formatting is shortest-round-trip, matching ryu's choice of digits.
        string s = value.ToString(CultureInfo.InvariantCulture);

        int ei = s.IndexOfAny(new[] { 'e', 'E' });
        if (ei >= 0)
        {
            // serde writes `1e30` / `1e-7`; .NET writes `1E+30` / `1E-07`.
            string mantissa = s[..ei];
            string exp = s[(ei + 1)..];
            bool neg = exp.StartsWith('-');
            exp = exp.TrimStart('+', '-').TrimStart('0');
            if (exp.Length == 0) exp = "0";
            return mantissa + "e" + (neg ? "-" : "") + exp;
        }

        return s.Contains('.') ? s : s + ".0";
    }
}

/// <summary>Writes <see cref="double"/> using <see cref="SerdeFloat.Format"/>.</summary>
public sealed class SerdeDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteRawValue(SerdeFloat.Format(value), skipInputValidation: true);
}

/// <summary>Writes <see cref="float"/> using <see cref="SerdeFloat.Format"/>.</summary>
public sealed class SerdeSingleConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) =>
        // Round-trip through the f32 shortest representation, as serde does for f32.
        writer.WriteRawValue(SerdeFloat.Format(double.Parse(
            value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
            skipInputValidation: true);
}
