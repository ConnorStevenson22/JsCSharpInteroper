using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Interoper;

/// <summary>
/// A value typed <c>any</c>, <c>unknown</c> or <c>object</c> (mapping §2, IOP2001): its JSON as JS sent it, untyped.
/// Plain objects and arrays are copies; any other object is a handle, <c>{"$h":id,"$t":classId}</c>, which the scope
/// that read the value owns. Read the value with a generated reader (<see cref="Read{T}"/>), such as
/// <c>JsJson.ReadNumber</c> or <c>scope.Read&lt;Marker&gt;</c>; written back, a handle is the same JS object again.
/// </summary>
public sealed class JsAny
{
    private JsAny(JsonElement json) => Json = json;

    /// <summary>The value's JSON.</summary>
    public JsonElement Json { get; }

    /// <summary>A value from its JSON.</summary>
    public static JsAny Parse(string json) => new(JsonElement.Parse(json));

    /// <summary>A value from a typed one and its writer, such as <c>JsAny.From(3.0, JsJson.WriteNumber)</c>.</summary>
    public static JsAny From<T>(T value, JsonWrite<T> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer, value);
        }
        return new(JsonElement.Parse(buffer.WrittenSpan));
    }

    /// <summary>The value as a typed one, read with <paramref name="read"/>.</summary>
    public T Read<T>(JsonRead<T> read)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(Json.GetRawText()));
        reader.Read();
        return read(ref reader);
    }

    /// <summary>Writes the value's JSON: the writer for <c>any</c> in generated code.</summary>
    public static void Write(Utf8JsonWriter writer, JsAny value) => value.Json.WriteTo(writer);

    /// <summary>
    /// Reads a value, and owns every handle in it with <paramref name="scope"/>: the reader for <c>any</c> in generated
    /// code.
    /// </summary>
    public static JsAny Read(ref Utf8JsonReader reader, JsScope scope)
    {
        var json = JsonElement.ParseValue(ref reader);
        Own(json, scope);
        return new(json);
    }

    private static void Own(JsonElement json, JsScope scope)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object when json.TryGetProperty("$h", out var handle) && handle.ValueKind == JsonValueKind.Number:
                scope.Own(handle.GetInt32());
                break;
            case JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                {
                    Own(property.Value, scope);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                {
                    Own(item, scope);
                }
                break;
        }
    }

    /// <summary>The value's JSON text.</summary>
    public override string ToString() => Json.GetRawText();
}
