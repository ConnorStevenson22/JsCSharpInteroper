using System.Text.Json;
using Interoper;

namespace GoldenSlice.Sync;

/// <summary>The snapshot of <c>class Line</c> (R16–R18): its data members, all optional.</summary>
public sealed record LineSnapshot : ShapeSnapshot
{
    /// <summary><c>path: [number, number][]</c> (R5).</summary>
    public Opt<double[][]> Path { get; init; }

    /// <summary>As a value: <c>new Line(bag)</c> in JS (R16, <c>$c</c>), its bag keeping the discriminant (R20).</summary>
    public static void Write(Utf8JsonWriter writer, LineSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("$c"u8, DemoMaps.LineId);
        writer.WriteString("type"u8, "line"u8);
        WriteMembers(writer, value);
        writer.WriteEndObject();
    }

    /// <summary>The members alone: a constructor bag or a patch (R18).</summary>
    public static void WriteMembers(Utf8JsonWriter writer, LineSnapshot value)
    {
        JsJson.WriteProperty(writer, "path"u8, value.Path, static (w, v) => JsJson.WriteArray(w, v, static (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber)));
    }

    public static new LineSnapshot Read(ref Utf8JsonReader reader)
    {
        Opt<double[][]> path = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "path": path = JsJson.ReadOpt(ref reader, static (ref r) => JsJson.ReadArray(ref r, static (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber))); break;
                default: reader.Skip(); break;
            }
        }
        return new LineSnapshot { Path = path };
    }
}
