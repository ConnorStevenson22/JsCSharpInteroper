// Hand-written stand-in for what a generator would emit from @arcgis/core 5.x typings.
// The TypeScript source for each declaration is quoted above it.
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using UnionInterop.Runtime;

namespace UnionInterop.Generated.ArcGis;

// ---------------------------------------------------------------------------
// 1. String-literal union -> enum. No C# union needed (the most common union kind).
//    accessor style: "circle" | "square" | "cross" | "x" | "diamond" | "triangle" | "path";
// ---------------------------------------------------------------------------
[JsonConverter(typeof(JsonStringEnumConverter<SimpleMarkerStyle>))]
public enum SimpleMarkerStyle
{
    [JsonStringEnumMemberName("circle")] Circle,
    [JsonStringEnumMemberName("square")] Square,
    [JsonStringEnumMemberName("cross")] Cross,
    [JsonStringEnumMemberName("x")] X,
    [JsonStringEnumMemberName("diamond")] Diamond,
    [JsonStringEnumMemberName("triangle")] Triangle,
    [JsonStringEnumMemberName("path")] Path,
}

// ---------------------------------------------------------------------------
// 2. Primitive union.   code: string | number;   (CodedValue.code)
// ---------------------------------------------------------------------------
public union CodedValueCode(string, double);

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
// 3. Mixed union.   type ColorLike = string | readonly number[] | ColorJSON | RGBA;
//    ColorJSON is a number tuple, so it collapses into double[] with readonly number[]:
//    the generator has to de-duplicate *after* mapping, not before.
// ---------------------------------------------------------------------------
public sealed record Rgba
{
    public double? R { get; init; }
    public double? G { get; init; }
    public double? B { get; init; }
    public double? A { get; init; }
}

public union ColorLike(string, double[], Rgba);

// ---------------------------------------------------------------------------
// 4. Discriminated union of property bags (ArcGIS "autocasting").
//    geometry?: (PointProperties & { type: "point" }) | (PolylineProperties & { type: "polyline" })
//             | (ExtentProperties & { type: "extent" }) | ... | null;
//    The tag is a real, constant property, so it is serialized like any other.
// ---------------------------------------------------------------------------
[JsDiscriminator("type", "point")]
public sealed record PointProperties
{
    public string Type => "point";
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
}

[JsDiscriminator("type", "polyline")]
public sealed record PolylineProperties
{
    public string Type => "polyline";
    public double[][][]? Paths { get; init; }
}

[JsDiscriminator("type", "extent")]
public sealed record ExtentProperties
{
    public string Type => "extent";
    public double Xmin { get; init; }
    public double Ymin { get; init; }
    public double Xmax { get; init; }
    public double Ymax { get; init; }
}

[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]
public union GeometryProperties(PointProperties, PolylineProperties, ExtentProperties);

// ---------------------------------------------------------------------------
// 5. Untagged object union.  fetchPopupFeatures(hitTarget: ScreenPoint | ScreenRect)
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
//    get geometry(): GeometryUnion | null | undefined;   (GeometryUnion = Extent | Point | Polyline | ...)
//    set geometry(value: (PointProperties & {type:"point"}) | ... | null | undefined);
//    The getter and setter have different types (1,553 such accessors in @arcgis/core).
// ---------------------------------------------------------------------------
[JsProxyType("esri.geometry.Point"), JsonConverter(typeof(JsProxyConverter<Point>))]
public sealed class Point(IJSObjectReference reference) : JsProxy(reference), IJsProxy<Point>
{
    public static Point Wrap(IJSObjectReference reference) => new(reference);
    public ValueTask<double> GetXAsync() => Reference.GetValueAsync<double>("x");
}

[JsProxyType("esri.geometry.Polyline"), JsonConverter(typeof(JsProxyConverter<Polyline>))]
public sealed class Polyline(IJSObjectReference reference) : JsProxy(reference), IJsProxy<Polyline>
{
    public static Polyline Wrap(IJSObjectReference reference) => new(reference);
}

[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]
public union Geometry(Point, Polyline);

// What the setter accepts: an existing instance or a property bag. Flattened, because a
// nested union (Geometry inside GeometryOrProperties) would need two conversions.
[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]
public union GeometryOrProperties(Point, Polyline, PointProperties, PolylineProperties, ExtentProperties);

[JsProxyType("esri.Graphic"), JsonConverter(typeof(JsProxyConverter<Graphic>))]
public sealed class Graphic(IJSObjectReference reference) : JsProxy(reference), IJsProxy<Graphic>
{
    public static Graphic Wrap(IJSObjectReference reference) => new(reference);

    // A generated JS shim marshals the value: class instances come back as handles tagged with
    // __jsType (ArcGIS exposes `declaredClass`), plain objects come back as JSON.
    public ValueTask<Geometry?> GetGeometryAsync() =>
        Reference.InvokeAsync<Geometry?>("__interop.get", "geometry");

    public ValueTask SetGeometryAsync(GeometryOrProperties? value) =>
        Reference.InvokeVoidAsync("__interop.set", "geometry", value);
}

// ---------------------------------------------------------------------------
// 7. Optional + nullable property bag:  symbol?: SymbolProperties | null;
// ---------------------------------------------------------------------------
public sealed record GraphicProperties
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsOptional<GeometryProperties> Geometry { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsOptional<ColorLike> Color { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsOptional<string> PopupTitle { get; init; }
}
