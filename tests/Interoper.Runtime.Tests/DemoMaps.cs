// The shape the generator will emit for a few fixture types (fixtures/demo-maps), written by hand.
// The golden slice (plan phase 1) writes the real bindings; this file holds only what the value-model tests need.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Interoper.Runtime.Tests;

/// <summary><c>interface Rgba { r: number; g: number; b: number; a?: number }</c>: a data record (R15).</summary>
public sealed record Rgba
{
    public required double R { get; init; }
    public required double G { get; init; }
    public required double B { get; init; }
    public Opt<double> A { get; init; } // R3

    public static void Write(Utf8JsonWriter writer, Rgba value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("r"u8, value.R);
        writer.WriteNumber("g"u8, value.G);
        writer.WriteNumber("b"u8, value.B);
        JsJson.WriteProperty(writer, "a"u8, value.A, Json.WriteDouble);
        writer.WriteEndObject();
    }

    public static Rgba Read(ref Utf8JsonReader reader)
    {
        double r = 0, g = 0, b = 0;
        Opt<double> a = default;
        Json.ReadObject(ref reader, (ref Utf8JsonReader reader, string name) =>
        {
            switch (name)
            {
                case "r": r = reader.GetDouble(); break;
                case "g": g = reader.GetDouble(); break;
                case "b": b = reader.GetDouble(); break;
                case "a": a = JsJson.ReadOpt(ref reader, Json.ReadDouble); break;
                default: reader.Skip(); break;
            }
        });
        return new Rgba { R = r, G = g, B = b, A = a };
    }
}

/// <summary>
/// <c>type ColorInput = string | number[] | Rgba</c>, used as <c>color?: ColorInput | null</c> (R2).
/// Non-boxing; the cases are the mapped members plus the nullable bottom case <c>Undefined?</c>.
/// </summary>
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

    public bool TryGetValue(out Undefined value) { value = Undefined.Value; return _tag == 0; }
    public bool TryGetValue([MaybeNullWhen(false)] out string value) { value = _tag == 2 ? _ref as string : null; return _tag == 2; }
    public bool TryGetValue([MaybeNullWhen(false)] out double[] value) { value = _tag == 3 ? _ref as double[] : null; return _tag == 3; }
    public bool TryGetValue([MaybeNullWhen(false)] out Rgba value) { value = _tag == 4 ? _ref as Rgba : null; return _tag == 4; }

    public static void WriteValue(Utf8JsonWriter writer, ColorInput value)
    {
        // On the tag, not the CLR type: cases may share a CLR type.
        switch (value._tag)
        {
            case 2: writer.WriteStringValue((string)value._ref!); break;
            case 3: Json.WriteDoubles(writer, (double[])value._ref!); break;
            case 4: Rgba.Write(writer, (Rgba)value._ref!); break;
        }
    }

    // R19 step 4: the members differ by JSON token kind.
    public static ColorInput ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString()!,
        JsonTokenType.StartArray => Json.ReadDoubles(ref reader),
        JsonTokenType.StartObject => Rgba.Read(ref reader),
        _ => throw new JsonException($"ColorInput: unexpected {reader.TokenType}"),
    };

    public bool Equals(ColorInput other) => _tag == other._tag && Equals(_ref, other._ref);
    public override bool Equals(object? obj) => obj is ColorInput other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_tag, _ref);
    public override string ToString() => _tag switch { 0 => "undefined", 1 => "null", _ => $"{_ref}" };
}

/// <summary>
/// <c>interface StrokeStyle { type: "stroke"; color?: ColorInput | null; width?: number }</c>. No initializers:
/// unset members are undefined, so a partially filled record is a patch (R18). The discriminant is a
/// constant that is always written (R20).
/// </summary>
public sealed record StrokeStyle
{
    public ColorInput Color { get; init; }
    public Opt<double> Width { get; init; }

    public static void Write(Utf8JsonWriter writer, StrokeStyle value)
    {
        writer.WriteStartObject();
        writer.WriteString("type"u8, "stroke"u8);
        JsJson.WriteProperty(writer, "color"u8, value.Color);
        JsJson.WriteProperty(writer, "width"u8, value.Width, Json.WriteDouble);
        writer.WriteEndObject();
    }

    public static StrokeStyle Read(ref Utf8JsonReader reader)
    {
        var result = new StrokeStyle();
        Json.ReadObject(ref reader, (ref Utf8JsonReader reader, string name) =>
        {
            switch (name)
            {
                case "color": result = result with { Color = JsJson.ReadUnion<ColorInput>(ref reader) }; break;
                case "width": result = result with { Width = JsJson.ReadOpt(ref reader, Json.ReadDouble) }; break;
                default: reader.Skip(); break;
            }
        });
        return result;
    }
}

// Live classes for `abstract class Shape` and its subclasses (R4): the base is `closed`, so a switch
// over the subclasses is exhaustive. Handles and members come with later items.
public closed class Shape(int handle)
{
    public int Handle { get; } = handle;
}

public sealed class Point(int handle) : Shape(handle);

public sealed class Line(int handle) : Shape(handle);

public sealed class Polygon(int handle) : Shape(handle);
