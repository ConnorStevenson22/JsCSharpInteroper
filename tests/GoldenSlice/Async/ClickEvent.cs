using System.Text.Json;
using Interoper;

namespace GoldenSlice.Async;

/// <summary>
/// <c>interface ClickEvent { type: "click"; x; y: number; button: 0 | 1 | 2; mapPoint: Point }</c>: a data record
/// (R15) whose discriminant is a constant (R20), and whose value-like member is a snapshot (R16).
/// </summary>
public sealed record ClickEvent
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required ClickEventButton Button { get; init; }
    public required PointSnapshot MapPoint { get; init; }

    public static void Write(Utf8JsonWriter writer, ClickEvent value)
    {
        writer.WriteStartObject();
        writer.WriteString("type"u8, "click"u8);
        writer.WritePropertyName("x"u8);
        JsJson.WriteNumber(writer, value.X);
        writer.WritePropertyName("y"u8);
        JsJson.WriteNumber(writer, value.Y);
        writer.WritePropertyName("button"u8);
        JsJson.WriteNumber(writer, (int)value.Button);
        writer.WritePropertyName("mapPoint"u8);
        PointSnapshot.Write(writer, value.MapPoint);
        writer.WriteEndObject();
    }

    public static ClickEvent Read(ref Utf8JsonReader reader)
    {
        double x = default, y = default;
        ClickEventButton button = default;
        PointSnapshot mapPoint = null!;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "x": x = JsJson.ReadNumber(ref reader); break;
                case "y": y = JsJson.ReadNumber(ref reader); break;
                case "button": button = (ClickEventButton)JsJson.ReadNumber(ref reader); break;
                case "mapPoint": mapPoint = PointSnapshot.Read(ref reader); break;
                default: reader.Skip(); break;
            }
        }
        return new ClickEvent { X = x, Y = y, Button = button, MapPoint = mapPoint };
    }
}
