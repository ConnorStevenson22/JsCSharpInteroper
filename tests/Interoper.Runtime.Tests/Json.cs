using System.Text;
using System.Text.Json;

namespace Interoper.Runtime.Tests;

/// <summary>Generated readers and writers to and from JSON text.</summary>
internal static class Json
{
    public static string Write<T>(T value, JsonWrite<T> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Read<T>(string json, JsonRead<T> read)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return read(ref reader);
    }
}
