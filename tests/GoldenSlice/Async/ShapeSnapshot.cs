using System.Text.Json;
using Interoper;

namespace GoldenSlice.Async;

/// <summary>The snapshot of <c>abstract class Shape</c> (R16–R18): closed, like the live <see cref="Shape"/> (R4).</summary>
public closed record ShapeSnapshot
{
    /// <summary>As a value: an instance of its class in JS (R16).</summary>
    public static void Write(Utf8JsonWriter writer, ShapeSnapshot value)
    {
        switch (value)
        {
            case PointSnapshot point: PointSnapshot.Write(writer, point); break;
            case LineSnapshot line: LineSnapshot.Write(writer, line); break;
            case PolygonSnapshot polygon: PolygonSnapshot.Write(writer, polygon); break;
        }
    }

    /// <summary>The members alone: a constructor bag or a patch (R18).</summary>
    public static void WriteMembers(Utf8JsonWriter writer, ShapeSnapshot value)
    {
        switch (value)
        {
            case PointSnapshot point: PointSnapshot.WriteMembers(writer, point); break;
            case LineSnapshot line: LineSnapshot.WriteMembers(writer, line); break;
            case PolygonSnapshot polygon: PolygonSnapshot.WriteMembers(writer, polygon); break;
        }
    }

    // R19 step 2: the discriminant's value picks the most-derived snapshot.
    public static ShapeSnapshot Read(ref Utf8JsonReader reader) => JsJson.PeekString(reader, "type"u8) switch
    {
        "point" => PointSnapshot.Read(ref reader),
        "line" => LineSnapshot.Read(ref reader),
        "polygon" => PolygonSnapshot.Read(ref reader),
        var type => throw new JsonException($"ShapeSnapshot: unexpected type {type}"),
    };
}
