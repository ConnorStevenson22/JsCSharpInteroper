using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Interoper;

namespace GoldenSlice.Async;

/// <summary>
/// <c>set position(value: Point | [number, number])</c>: the setter's wider type, named after its owner (R2, R27).
/// </summary>
[Union]
public readonly struct MarkerPositionInput : IJsUnion<MarkerPositionInput>, IEquatable<MarkerPositionInput>
{
    private readonly object? _ref;
    private readonly byte _tag; // 0 = undefined, 1 = null, 2 = PointSnapshot, 3 = double[]

    private MarkerPositionInput(object? value, byte tag) => (_ref, _tag) = (value, tag);
    public MarkerPositionInput(PointSnapshot value) : this(value, 2) { }
    public MarkerPositionInput(double[] value) : this(value, 3) { }
    public MarkerPositionInput(Undefined? value) : this(null, value is null ? (byte)1 : (byte)0) { }

    public static implicit operator MarkerPositionInput(Undefined? value) => new(value);

    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    public bool HasValue => _tag != 1;
    public bool IsUndefined => _tag == 0;
    public bool IsNull => _tag == 1;

    public bool TryGetValue(out Undefined value)
    {
        value = Undefined.Value;
        return _tag == 0;
    }

    public bool TryGetValue([MaybeNullWhen(false)] out PointSnapshot value)
    {
        value = _tag == 2 ? (PointSnapshot)_ref! : null;
        return _tag == 2;
    }

    public bool TryGetValue([MaybeNullWhen(false)] out double[] value)
    {
        value = _tag == 3 ? (double[])_ref! : null;
        return _tag == 3;
    }

    // On the tag, not the CLR type: cases may share one (R20).
    public static void WriteValue(Utf8JsonWriter writer, MarkerPositionInput value)
    {
        switch (value._tag)
        {
            case 2: PointSnapshot.Write(writer, (PointSnapshot)value._ref!); break;
            case 3: JsJson.WriteArray(writer, (double[])value._ref!, JsJson.WriteNumber); break;
        }
    }

    // R19 step 4: the cases differ by JSON token kind.
    public static MarkerPositionInput ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.StartArray => JsJson.ReadArray(ref reader, JsJson.ReadNumber),
        JsonTokenType.StartObject => PointSnapshot.Read(ref reader),
        var token => throw new JsonException($"MarkerPositionInput: unexpected {token}"),
    };

    public bool Equals(MarkerPositionInput other) => _tag == other._tag && Equals(_ref, other._ref);
    public override bool Equals(object? obj) => obj is MarkerPositionInput other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_tag, _ref);
    public override string ToString() => _tag switch { 0 => "undefined", 1 => "null", _ => $"{_ref}" };
}
