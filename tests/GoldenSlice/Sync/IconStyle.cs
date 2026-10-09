using System.Text.Json;
using Interoper;

namespace GoldenSlice.Sync;

/// <summary><c>interface IconStyle { type: "icon"; name: IconName; color?: ColorInput | null }</c> (R15, R20).</summary>
public sealed record IconStyle
{
    public required IconName Name { get; init; }
    public ColorInput Color { get; init; }

    public static void Write(Utf8JsonWriter writer, IconStyle value)
    {
        writer.WriteStartObject();
        writer.WriteString("type"u8, "icon"u8);
        writer.WritePropertyName("name"u8);
        IconName.Write(writer, value.Name);
        JsJson.WriteProperty(writer, "color"u8, value.Color);
        writer.WriteEndObject();
    }

    public static IconStyle Read(ref Utf8JsonReader reader)
    {
        IconName name = default;
        ColorInput color = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "name": name = IconName.Read(ref reader); break;
                case "color": color = JsJson.ReadUnion<ColorInput>(ref reader); break;
                default: reader.Skip(); break;
            }
        }
        return new IconStyle { Name = name, Color = color };
    }
}
