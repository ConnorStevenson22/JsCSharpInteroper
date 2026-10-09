using System.Text.Json;

namespace Interoper.Runtime.Tests;

/// <summary>Small reader/writer helpers for the hand-written shapes in DemoMaps.cs.</summary>
internal static class Json
{
    public delegate void PropertyReader(ref Utf8JsonReader reader, string name);

    public static void WriteDouble(Utf8JsonWriter writer, double value) => writer.WriteNumberValue(value);

    public static void WriteDoubles(Utf8JsonWriter writer, double[] values)
    {
        writer.WriteStartArray();
        foreach (var v in values)
        {
            writer.WriteNumberValue(v);
        }
        writer.WriteEndArray();
    }

    public static double[] ReadDoubles(ref Utf8JsonReader reader)
    {
        var values = new List<double>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetDouble());
        }
        return [.. values];
    }

    /// <summary>Calls <paramref name="property"/> on each property value; starts and ends on the braces.</summary>
    public static void ReadObject(ref Utf8JsonReader reader, PropertyReader property)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            property(ref reader, name);
        }
    }

    public static string Write<T>(T value, JsonWrite<T> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer, value);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Read<T>(string json, JsonRead<T> read)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        reader.Read();
        return read(ref reader);
    }
}
