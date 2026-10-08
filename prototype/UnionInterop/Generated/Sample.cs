// Hand-written stand-in for what a generator would emit for a small, made-up JS library ("demo").
// The illustrative TypeScript for each declaration is quoted above it.
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using UnionInterop.Runtime;

namespace UnionInterop.Generated.Sample;

// ---------------------------------------------------------------------------
// 1. String-literal union -> enum. No C# union needed (the most common union kind).
//    style: "circle" | "square" | "diamond" | "triangle" | "star";
// ---------------------------------------------------------------------------
[JsonConverter(typeof(JsonStringEnumConverter<MarkerStyle>))]
public enum MarkerStyle
{
    [JsonStringEnumMemberName("circle")] Circle,
    [JsonStringEnumMemberName("square")] Square,
    [JsonStringEnumMemberName("diamond")] Diamond,
    [JsonStringEnumMemberName("triangle")] Triangle,
    [JsonStringEnumMemberName("star")] Star,
}

// ---------------------------------------------------------------------------
// 2. Primitive union.   id: string | number;
// ---------------------------------------------------------------------------
public union IdOrName(string, double);

// The compiler-generated union above boxes the double. Primitive unions are rare (0-3% of
// positions) but sit on hot paths (ids, sizes), so a generator can emit the non-boxing
// pattern instead; pattern matching then goes through TryGetValue.
[System.Runtime.CompilerServices.Union]
public readonly struct StringOrNumber : System.Runtime.CompilerServices.IUnion
{
    private readonly string? _string;
    private readonly double _number;
    private readonly byte _tag; // 0 = none, 1 = string, 2 = number

    public StringOrNumber(string value) => (_string, _tag) = (value, value is null ? (byte)0 : (byte)1);
    public StringOrNumber(double value) => (_number, _tag) = (value, (byte)2);

    public object? Value => _tag switch { 1 => _string, 2 => _number, _ => null };
    public bool HasValue => _tag != 0;
    public bool TryGetValue(out string value) { value = _string!; return _tag == 1; }
    public bool TryGetValue(out double value) { value = _number; return _tag == 2; }
}

// ---------------------------------------------------------------------------
// 3. Mixed union.   type ColorInput = string | number[] | [number, number, number] | RGBA;
//    The tuple and number[] both map to double[], so the generator de-duplicates
//    *after* mapping, not before.
// ---------------------------------------------------------------------------
public sealed record Rgba
{
    public double? R { get; init; }
    public double? G { get; init; }
    public double? B { get; init; }
    public double? A { get; init; }
}

public union ColorInput(string, double[], Rgba);

// ---------------------------------------------------------------------------
// 4. Discriminated union of plain-data bags.
//    shape?: (PointData & { type: "point" }) | (LineData & { type: "line" }) | (BoxData & { type: "box" }) | null;
//    The tag is a real, constant property, so it is serialized like any other.
// ---------------------------------------------------------------------------
[JsDiscriminator("type", "point")]
public sealed record PointData
{
    public string Type => "point";
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
}

[JsDiscriminator("type", "line")]
public sealed record LineData
{
    public string Type => "line";
    public double[][]? Points { get; init; }
}

[JsDiscriminator("type", "box")]
public sealed record BoxData
{
    public string Type => "box";
    public double Xmin { get; init; }
    public double Ymin { get; init; }
    public double Xmax { get; init; }
    public double Ymax { get; init; }
}

[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]
public union ShapeData(PointData, LineData, BoxData);

// ---------------------------------------------------------------------------
// 5. Untagged object union.  hitTest(target: ScreenPoint | ScreenRect)
//    No discriminator: only property names tell them apart -> built-in structural classifier.
// ---------------------------------------------------------------------------
public sealed record ScreenPoint
{
    public required double X { get; init; }
    public required double Y { get; init; }
}

public sealed record ScreenRect
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union HitTarget(ScreenPoint, ScreenRect);

// The common TypeScript reality: the members that tell the cases apart are optional.
public sealed record LooseScreenRect
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
}

[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union LooseHitTarget(ScreenPoint, LooseScreenRect);

// ---------------------------------------------------------------------------
// 6. Class instances -> proxies over IJSObjectReference.
//    get shape(): Point | Line | null;
//    set shape(value: Point | Line | PointData | LineData | BoxData | null);   // getter and setter differ
// ---------------------------------------------------------------------------
[JsProxyType("demo.Point"), JsonConverter(typeof(JsProxyConverter<Point>))]
public sealed class Point(IJSObjectReference reference) : JsProxy(reference), IJsProxy<Point>
{
    public static Point Wrap(IJSObjectReference reference) => new(reference);
    public ValueTask<double> GetXAsync() => Reference.GetValueAsync<double>("x");
}

[JsProxyType("demo.Line"), JsonConverter(typeof(JsProxyConverter<Line>))]
public sealed class Line(IJSObjectReference reference) : JsProxy(reference), IJsProxy<Line>
{
    public static Line Wrap(IJSObjectReference reference) => new(reference);
}

[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]
public union Shape(Point, Line);

// What the setter accepts: an existing instance or a data bag. Flattened, because a
// nested union (Shape inside ShapeOrData) would need two conversions.
[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]
public union ShapeOrData(Point, Line, PointData, LineData, BoxData);

[JsProxyType("demo.Feature"), JsonConverter(typeof(JsProxyConverter<Feature>))]
public sealed class Feature(IJSObjectReference reference) : JsProxy(reference), IJsProxy<Feature>
{
    public static Feature Wrap(IJSObjectReference reference) => new(reference);

    // A generated JS shim marshals the value: class instances come back as handles tagged with
    // __jsType (the JS runtime class), plain objects come back as JSON.
    public ValueTask<Shape?> GetShapeAsync() =>
        Reference.InvokeAsync<Shape?>("__interop.get", "shape");

    public ValueTask SetShapeAsync(ShapeOrData? value) =>
        Reference.InvokeVoidAsync("__interop.set", "shape", value);
}

// ---------------------------------------------------------------------------
// 7. Optional + nullable data bag:  color?: ColorInput | null;
// ---------------------------------------------------------------------------
public sealed record FeatureData
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsOptional<ShapeData> Shape { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsOptional<ColorInput> Color { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsOptional<string> Title { get; init; }
}
