using System.Text.Json;
using Interoper;

namespace GoldenSlice.Sync;

/// <summary>The snapshot of <c>class Polygon</c> (R16–R18): its data members, all optional.</summary>
public sealed record PolygonSnapshot : ShapeSnapshot
{
    /// <summary><c>vertices: [number, number][]</c> (R5).</summary>
    public Opt<double[][]> Vertices { get; init; }

    /// <summary>As a value: <c>new Polygon(bag)</c> in JS (R16, <c>$c</c>), its bag keeping the discriminant (R20).</summary>
    public static void Write(Utf8JsonWriter writer, PolygonSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("$c"u8, DemoMaps.PolygonId);
        writer.WriteString("type"u8, "polygon"u8);
        WriteMembers(writer, value);
        writer.WriteEndObject();
    }

    /// <summary>The members alone: a constructor bag or a patch (R18).</summary>
    public static void WriteMembers(Utf8JsonWriter writer, PolygonSnapshot value)
    {
        JsJson.WriteProperty(writer, "vertices"u8, value.Vertices, static (w, v) => JsJson.WriteArray(w, v, static (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber)));
    }

    public static new PolygonSnapshot Read(ref Utf8JsonReader reader)
    {
        Opt<double[][]> vertices = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "vertices": vertices = JsJson.ReadOpt(ref reader, static (ref r) => JsJson.ReadArray(ref r, static (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber))); break;
                default: reader.Skip(); break;
            }
        }
        return new PolygonSnapshot { Vertices = vertices };
    }
}
