using System.Text;
using System.Text.Json;

namespace Interoper;

/// <summary>The few ops the runtime writes itself: the typed fast paths as plain <c>get</c>/<c>set</c> ops.</summary>
internal static class JsOp
{
    public static string Get(int handle, string name) => Write("get", handle, name, static _ => { });

    public static string Set(int handle, string name, double value) => Write("set", handle, name, w => w.WriteNumber("value"u8, value));

    public static string Set(int handle, string name, string value) => Write("set", handle, name, w => w.WriteString("value"u8, value));

    public static string Set(int handle, string name, bool value) => Write("set", handle, name, w => w.WriteBoolean("value"u8, value));

    /// <summary>
    /// A fast path's result read from a <c>get</c> envelope. Fast paths serve members declared as non-null
    /// primitives, so null or undefined throws, as <c>getValue</c> does in interoper.js.
    /// </summary>
    public static T Primitive<T>(string envelope, string name, JsonRead<T> read) where T : notnull
    {
        var value = JsEnvelope.Read(envelope, (ref reader) => JsJson.ReadOpt(ref reader, read));
        return value.TryGetValue(out T? v)
            ? v
            : throw new JsException("InteroperError", $"{name} is {value}; a typed fast path needs a value", null);
    }

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
