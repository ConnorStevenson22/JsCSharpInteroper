using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Interoper;

/// <summary>
/// A value typed <c>any</c>, <c>unknown</c> or <c>object</c> (mapping §2, IOP2001): its JSON as JS sent it, untyped.
/// Plain objects and arrays are copies; any other object is a handle, <c>{"$h":id,"$t":classId}</c>, which the scope
/// that read the value owns. Read the value with a generated reader (<see cref="Read{T}"/>), such as
/// <c>JsJson.ReadNumber</c> or <c>scope.Read&lt;Marker&gt;</c>; written back, a handle is the same JS object again. Two
/// values are equal when their JSON is (<see cref="JsonElement.DeepEquals"/>).
/// </summary>
public sealed class JsAny : IEquatable<JsAny>
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
        var reader = new Utf8JsonReader(JsonMarshal.GetRawUtf8Value(Json));
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
        // All at once: a scope disposed meanwhile releases every handle, not just those after the first.
        return scope.OwnAll(Handles(json)) ? new(json) : throw new ObjectDisposedException(nameof(JsScope));
    }

    /// <summary>
    /// Reads a value that has no handles, as a snapshot's <c>any</c> members are (mapping R17): the reader for them in
    /// generated code.
    /// </summary>
    public static JsAny ReadData(ref Utf8JsonReader reader)
    {
        var json = JsonElement.ParseValue(ref reader);
        return Handles(json).Any() ? throw new JsonException("A snapshot's value has a handle; read it with a scope.") : new(json);
    }

    private static IEnumerable<int> Handles(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object when json.TryGetProperty("$h", out var handle) && handle.ValueKind == JsonValueKind.Number => [handle.GetInt32()],
        JsonValueKind.Object => json.EnumerateObject().SelectMany(p => Handles(p.Value)),
        JsonValueKind.Array => json.EnumerateArray().SelectMany(Handles),
        _ => [],
    };

    public bool Equals(JsAny? other) => other is not null && JsonElement.DeepEquals(Json, other.Json);

    public override bool Equals(object? obj) => Equals(obj as JsAny);

    /// <summary>Its kind: equal values have the same one, whatever their spacing or property order.</summary>
    public override int GetHashCode() => Json.ValueKind.GetHashCode();

    /// <summary>The value's JSON text.</summary>
    public override string ToString() => Json.GetRawText();
}
