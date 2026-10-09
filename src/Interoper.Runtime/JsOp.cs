using System.Text;
using System.Text.Json;

namespace Interoper;

/// <summary>The few ops the runtime writes itself: the envelope fallback for the typed fast paths.</summary>
internal static class JsOp
{
    public static string Get(int handle, string name) => Write("get", handle, name, static _ => { });

    public static string Set(int handle, string name, double value) => Write("set", handle, name, w => w.WriteNumber("value"u8, value));

    public static string Set(int handle, string name, string? value) => Write("set", handle, name, w => w.WriteString("value"u8, value));

    public static string Set(int handle, string name, bool value) => Write("set", handle, name, w => w.WriteBoolean("value"u8, value));

    public static double ReadNumber(ref Utf8JsonReader reader) => reader.GetDouble();

    public static string? ReadString(ref Utf8JsonReader reader) => reader.GetString();

    public static bool ReadBoolean(ref Utf8JsonReader reader) => reader.GetBoolean();

    private static string Write(string op, int handle, string name, Action<Utf8JsonWriter> value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("op"u8, op);
            writer.WriteNumber("h"u8, handle);
            writer.WriteString("name"u8, name);
            value(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
