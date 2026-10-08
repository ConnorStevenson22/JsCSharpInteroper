// What the generator would emit for a few @arcgis/core 5.x shapes, using the value model in Js.cs.
using System.Runtime.CompilerServices;

namespace ValueModel;

public sealed record Rgba
{
    public Opt<double> R { get; init; }
    public Opt<double> G { get; init; }
    public Opt<double> B { get; init; }
    public Opt<double> A { get; init; }
}

/// <summary>
/// <c>type ColorLike = string | readonly number[] | ColorJSON | RGBA;</c> used as <c>color?: ColorLike | null</c>.
/// Generated as a non-boxing custom union whose default is undefined.
/// </summary>
[Union]
public readonly struct ColorLike : IUnion, IEquatable<ColorLike>
{
    private readonly object? _ref;
    private readonly byte _tag; // 0 = undefined, 1 = null, 2 = string, 3 = double[], 4 = Rgba

    private ColorLike(object? value, byte tag) => (_ref, _tag) = (value, tag);
    public ColorLike(string value) : this(value, 2) { }
    public ColorLike(double[] value) : this(value, 3) { }
    public ColorLike(Rgba value) : this(value, 4) { }
    public ColorLike(Undefined? value) : this(null, value is null ? (byte)1 : (byte)0) { }
    public static implicit operator ColorLike(Undefined? value) => new(value);

    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    public bool HasValue => _tag != 1;
    public bool TryGetValue(out Undefined value) { value = Undefined.Value; return _tag == 0; }
    public bool TryGetValue(out string value) { value = (_ref as string)!; return _tag == 2; }
    public bool TryGetValue(out double[] value) { value = (_ref as double[])!; return _tag == 3; }
    public bool TryGetValue(out Rgba value) { value = (_ref as Rgba)!; return _tag == 4; }

    public bool Equals(ColorLike other) => _tag == other._tag && Equals(_ref, other._ref);
    public override bool Equals(object? obj) => obj is ColorLike c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(_tag, _ref);
    public override string ToString() => _tag switch { 0 => "undefined", 1 => "null", 3 => "[" + string.Join(",", (double[])_ref!) + "]", _ => $"{_ref}" };
}

/// <summary>Snapshot / properties bag: unset members are undefined without any initializers.</summary>
public sealed record SimpleMarkerSymbolProperties
{
    public ColorLike Color { get; init; }
    public Opt<double> Size { get; init; }
    public Opt<SimpleMarkerStyle> Style { get; init; }
}

public enum SimpleMarkerStyle { Circle, Square, Cross, X, Diamond, Triangle, Path }

// Live-object class tree. `Geometry` is abstract in ArcGIS, so the generated proxy base is
// `closed`: a switch over the subclasses is exhaustive, like a union, but with shared members.
public closed class Geometry(int handle)
{
    public int Handle { get; } = handle;
}
public sealed class Point(int handle) : Geometry(handle);
public sealed class Polyline(int handle) : Geometry(handle);
public sealed class Polygon(int handle) : Geometry(handle);
