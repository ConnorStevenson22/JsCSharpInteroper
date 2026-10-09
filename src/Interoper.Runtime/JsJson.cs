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

    // Readers for required primitives.
    public static double ReadNumber(ref Utf8JsonReader reader) => reader.GetDouble();

    public static string ReadString(ref Utf8JsonReader reader) => reader.GetString()!;

    public static bool ReadBoolean(ref Utf8JsonReader reader) => reader.GetBoolean();

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
