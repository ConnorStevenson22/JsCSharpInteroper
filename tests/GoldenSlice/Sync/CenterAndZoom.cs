using System.Text.Json;
using Interoper;

namespace GoldenSlice.Sync;

/// <summary><c>interface CenterAndZoom { center: [number, number]; zoom: number }</c>: a data record (R15, R5).</summary>
public sealed record CenterAndZoom
{
    public required double[] Center { get; init; }
    public required double Zoom { get; init; }

    public static void Write(Utf8JsonWriter writer, CenterAndZoom value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("center"u8);
        JsJson.WriteArray(writer, value.Center, JsJson.WriteNumber);
        writer.WritePropertyName("zoom"u8);
        JsJson.WriteNumber(writer, value.Zoom);
        writer.WriteEndObject();
    }

    public static CenterAndZoom Read(ref Utf8JsonReader reader)
    {
        double[] center = [];
        double zoom = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "center": center = JsJson.ReadArray(ref reader, JsJson.ReadNumber); break;
                case "zoom": zoom = JsJson.ReadNumber(ref reader); break;
                default: reader.Skip(); break;
            }
        }
        return new CenterAndZoom { Center = center, Zoom = zoom };
    }
}
