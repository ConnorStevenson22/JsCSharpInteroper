using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnionInterop.Runtime;

/// <summary>
/// TypeScript's <c>prop?: T | null</c> has three states: absent (undefined), null, and a value.
/// <c>T?</c> collapses the first two. <c>default(JsOptional&lt;T&gt;)</c> is "undefined" and is
/// omitted on the wire via <c>[JsonIgnore(Condition = WhenWritingDefault)]</c>.
/// </summary>
[JsonConverter(typeof(JsOptionalConverterFactory))]
public readonly struct JsOptional<T>
{
    private readonly T? _value;
    private readonly byte _state; // 0 = undefined, 1 = null, 2 = value

    private JsOptional(T? value, byte state) => (_value, _state) = (value, state);

    public static JsOptional<T> Undefined => default;
    public static JsOptional<T> Null => new(default, 1);
    public static implicit operator JsOptional<T>(T value) => value is null ? Null : new(value, 2);

    public bool IsUndefined => _state == 0;
    public bool IsNull => _state == 1;
    public bool HasValue => _state == 2;
    public T Value => HasValue ? _value! : throw new InvalidOperationException("No value.");

    public override string ToString() => _state switch { 0 => "undefined", 1 => "null", _ => $"{_value}" };
}

public sealed class JsOptionalConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsOptional<>);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(t.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<JsOptional<T>>
    {
        public override bool HandleNull => true;

        public override JsOptional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? JsOptional<T>.Null : (JsOptional<T>)JsonSerializer.Deserialize<T>(ref reader, options)!;

        public override void Write(Utf8JsonWriter writer, JsOptional<T> value, JsonSerializerOptions options)
        {
            if (value.HasValue) JsonSerializer.Serialize(writer, value.Value, options);
            else writer.WriteNullValue(); // undefined never gets here when the property is ignored-when-default
        }
    }
}
