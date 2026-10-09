using System.Text.Json;

namespace GoldenSlice.Sync;

/// <summary><c>type Basemap = "plain" | "night" | "relief" | "3d-relief"</c>: a string-literal union (R9, names R25).</summary>
public enum Basemap
{
    Plain,
    Night,
    Relief,
    _3dRelief,
}

/// <summary>The name map of <see cref="Basemap"/> (R9).</summary>
public static class BasemapNames
{
    public static string ToJs(Basemap value) => value switch
    {
        Basemap.Plain => "plain",
        Basemap.Night => "night",
        Basemap.Relief => "relief",
        Basemap._3dRelief => "3d-relief",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static Basemap FromJs(string value) => value switch
    {
        "plain" => Basemap.Plain,
        "night" => Basemap.Night,
        "relief" => Basemap.Relief,
        "3d-relief" => Basemap._3dRelief,
        _ => throw new JsonException($"Basemap: unexpected \"{value}\""),
    };

    public static void Write(Utf8JsonWriter writer, Basemap value) => writer.WriteStringValue(ToJs(value));

    public static Basemap Read(ref Utf8JsonReader reader) => FromJs(reader.GetString()!);
}
