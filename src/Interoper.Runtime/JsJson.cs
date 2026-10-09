using System.Text.Json;

namespace Interoper;

public delegate void JsonWrite<in T>(Utf8JsonWriter writer, T value);

public delegate T JsonRead<out T>(ref Utf8JsonReader reader);

/// <summary>
/// The three JS states on the wire, for generated readers and writers (no reflection, trim/AOT-safe).
/// Undefined is an omitted property, or <c>{"$u":1}</c> where it can't be omitted (array elements,
/// arguments); the marker is valid in both directions. Null is <c>null</c>. Readers start on the value
/// token; a property that is never read stays <c>default</c>, which is undefined.
/// </summary>
public static class JsJson
{
    private static readonly JsonEncodedText UndefinedMarker = JsonEncodedText.Encode("$u");

    public static void WriteProperty<T>(Utf8JsonWriter writer, ReadOnlySpan<byte> name, Opt<T> value, JsonWrite<T> write)
        where T : notnull
    {
        if (value.IsUndefined)
        {
            return;
        }
        writer.WritePropertyName(name);
        WriteValue(writer, value, write);
    }

    public static void WriteProperty<TUnion>(Utf8JsonWriter writer, ReadOnlySpan<byte> name, TUnion value)
        where TUnion : struct, IJsUnion<TUnion>
    {
        if (value.IsUndefined)
        {
            return;
        }
        writer.WritePropertyName(name);
        WriteValue(writer, value);
    }

    public static void WriteValue<T>(Utf8JsonWriter writer, Opt<T> value, JsonWrite<T> write) where T : notnull
    {
        if (value.TryGetValue(out T? v))
        {
            write(writer, v);
        }
        else if (value.IsNull)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteUndefined(writer);
        }
    }

    public static void WriteValue<TUnion>(Utf8JsonWriter writer, TUnion value) where TUnion : struct, IJsUnion<TUnion>
    {
        if (value.IsNull)
        {
            writer.WriteNullValue();
        }
        else if (value.IsUndefined)
        {
            WriteUndefined(writer);
        }
        else
        {
            TUnion.WriteValue(writer, value);
        }
    }

    public static Opt<T> ReadOpt<T>(ref Utf8JsonReader reader, JsonRead<T> read) where T : notnull =>
        reader.TokenType == JsonTokenType.Null ? null
        : ReadUndefined(ref reader) ? default
        : new Opt<T>(read(ref reader));

    public static TUnion ReadUnion<TUnion>(ref Utf8JsonReader reader) where TUnion : struct, IJsUnion<TUnion> =>
        reader.TokenType == JsonTokenType.Null ? (Undefined?)null
        : ReadUndefined(ref reader) ? default
        : TUnion.ReadValue(ref reader);

    // Readers and writers for required primitives and arrays.
    public static double ReadNumber(ref Utf8JsonReader reader) => reader.GetDouble();

    public static string ReadString(ref Utf8JsonReader reader) => reader.GetString()!;

    public static bool ReadBoolean(ref Utf8JsonReader reader) => reader.GetBoolean();

    public static void WriteNumber(Utf8JsonWriter writer, double value) => writer.WriteNumberValue(value);

    public static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);

    public static void WriteBoolean(Utf8JsonWriter writer, bool value) => writer.WriteBooleanValue(value);

    public static T[] ReadArray<T>(ref Utf8JsonReader reader, JsonRead<T> read)
    {
        var items = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            items.Add(read(ref reader));
        }
        return [.. items];
    }

    public static void WriteArray<T>(Utf8JsonWriter writer, T[] values, JsonWrite<T> write)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            write(writer, value);
        }
        writer.WriteEndArray();
    }

    // For generated union readers (R19), which classify an object before reading it. The reader is a copy, so the
    // caller's stays on the object's start.

    /// <summary>Whether the object starting at <paramref name="reader"/> has the property (R19 step 3).</summary>
    public static bool HasProperty(Utf8JsonReader reader, ReadOnlySpan<byte> name) => FindProperty(ref reader, name);

    /// <summary>The object's string property, such as a discriminant (R19 step 2); null when it has none.</summary>
    public static string? PeekString(Utf8JsonReader reader, ReadOnlySpan<byte> name) =>
        FindProperty(ref reader, name) && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

    // Leaves the reader on the property's value.
    private static bool FindProperty(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var found = reader.ValueTextEquals(name);
            reader.Read();
            if (found)
            {
                return true;
            }
            reader.Skip();
        }
        return false;
    }

    /// <summary>On <c>{"$u":1}</c>, skips it and returns true; otherwise leaves the reader where it was.</summary>
    private static bool ReadUndefined(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }
        var peek = reader;
        if (!peek.Read() || peek.TokenType != JsonTokenType.PropertyName || !peek.ValueTextEquals(UndefinedMarker.EncodedUtf8Bytes))
        {
            return false;
        }
        reader.Skip();
        return true;
    }

    private static void WriteUndefined(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber(UndefinedMarker, 1);
        writer.WriteEndObject();
    }
}
