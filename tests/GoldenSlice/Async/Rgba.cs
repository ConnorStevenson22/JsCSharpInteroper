using System.Text.Json;
using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>interface Rgba { r; g; b: number; a?: number }</c>: a data record (R15) with an optional member (R3).</summary>
public sealed record Rgba
{
    public required double R { get; init; }
    public required double G { get; init; }
    public required double B { get; init; }
    public Opt<double> A { get; init; }

    public static void Write(Utf8JsonWriter writer, Rgba value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("r"u8);
        JsJson.WriteNumber(writer, value.R);
        writer.WritePropertyName("g"u8);
        JsJson.WriteNumber(writer, value.G);
        writer.WritePropertyName("b"u8);
        JsJson.WriteNumber(writer, value.B);
        JsJson.WriteProperty(writer, "a"u8, value.A, JsJson.WriteNumber);
        writer.WriteEndObject();
    }

    public static Rgba Read(ref Utf8JsonReader reader)
    {
        double r = default, g = default, b = default;
        Opt<double> a = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "r": r = JsJson.ReadNumber(ref reader); break;
                case "g": g = JsJson.ReadNumber(ref reader); break;
                case "b": b = JsJson.ReadNumber(ref reader); break;
                case "a": a = JsJson.ReadOpt(ref reader, JsJson.ReadNumber); break;
                default: reader.Skip(); break;
            }
        }
        return new Rgba { R = r, G = g, B = b, A = a };
    }
}
