using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>type ColorInput = string | number[] | Rgba</c> (R2).</summary>
[Union]
public readonly struct ColorInput : IJsUnion<ColorInput>, IEquatable<ColorInput>
{
    private readonly object? _ref;
    private readonly byte _tag; // 0 = undefined, 1 = null, 2 = string, 3 = double[], 4 = Rgba

    private ColorInput(object? value, byte tag) => (_ref, _tag) = (value, tag);
    public ColorInput(string value) : this(value, 2) { }
    public ColorInput(double[] value) : this(value, 3) { }
    public ColorInput(Rgba value) : this(value, 4) { }
    public ColorInput(Undefined? value) : this(null, value is null ? (byte)1 : (byte)0) { }

    public static implicit operator ColorInput(Undefined? value) => new(value);

    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    public bool HasValue => _tag != 1;
    public bool IsUndefined => _tag == 0;
    public bool IsNull => _tag == 1;

    public bool TryGetValue(out Undefined value)
    {
        value = Undefined.Value;
        return _tag == 0;
    }

    public bool TryGetValue([MaybeNullWhen(false)] out string value)
    {
        value = _tag == 2 ? (string)_ref! : null;
        return _tag == 2;
    }

    public bool TryGetValue([MaybeNullWhen(false)] out double[] value)
    {
        value = _tag == 3 ? (double[])_ref! : null;
        return _tag == 3;
    }

    public bool TryGetValue([MaybeNullWhen(false)] out Rgba value)
    {
        value = _tag == 4 ? (Rgba)_ref! : null;
        return _tag == 4;
    }

    // On the tag, not the CLR type: cases may share one (R20).
    public static void WriteValue(Utf8JsonWriter writer, ColorInput value)
    {
        switch (value._tag)
        {
            case 2: writer.WriteStringValue((string)value._ref!); break;
            case 3: JsJson.WriteArray(writer, (double[])value._ref!, JsJson.WriteNumber); break;
            case 4: Rgba.Write(writer, (Rgba)value._ref!); break;
        }
    }

    // R19 step 4: the cases differ by JSON token kind.
    public static ColorInput ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => JsJson.ReadString(ref reader),
        JsonTokenType.StartArray => JsJson.ReadArray(ref reader, JsJson.ReadNumber),
        JsonTokenType.StartObject => Rgba.Read(ref reader),
        var token => throw new JsonException($"ColorInput: unexpected {token}"),
    };

    public bool Equals(ColorInput other) => _tag == other._tag && Equals(_ref, other._ref);
    public override bool Equals(object? obj) => obj is ColorInput other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_tag, _ref);
    public override string ToString() => _tag switch { 0 => "undefined", 1 => "null", _ => $"{_ref}" };
}
