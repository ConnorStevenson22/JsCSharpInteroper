using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interoper;

/// <summary>
/// A generated record that reads without a scope: a data record (R15) or a snapshot (R18). Its own wire format serves
/// System.Text.Json too, through <see cref="JsRecordConverter{T}"/>, so app types can hold it.
/// </summary>
public interface IJsRecord<TSelf> where TSelf : class, IJsRecord<TSelf>
{
    /// <summary>Writes the record as one JSON object, with its constants (R20) and a value-like snapshot's <c>$c</c> (R16).</summary>
    static abstract void Write(Utf8JsonWriter writer, TSelf value);

    /// <summary>Reads the record from the object at the reader.</summary>
    static abstract TSelf Read(ref Utf8JsonReader reader);
}

/// <summary>System.Text.Json for a generated record, in its wire format (<see cref="IJsRecord{TSelf}"/>).</summary>
public sealed class JsRecordConverter<T> : JsonConverter<T> where T : class, IJsRecord<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => T.Read(ref reader);

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => T.Write(writer, value);
}
