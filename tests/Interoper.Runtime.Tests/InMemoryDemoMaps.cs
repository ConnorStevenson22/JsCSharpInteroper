// C# fakes of the fixtures/demo-maps classes, for InMemoryTransport: the transport contract and the golden slice run
// against them. They copy the fixture's JS behaviour member for member; the class table copies the golden slice's
// tests/GoldenSlice/wwwroot/demo-maps.meta.js.
using GoldenSlice.Async;
using Interoper.Contract;

namespace Interoper.Runtime.Tests;

internal static class InMemoryDemoMaps
{
    public static InMemoryTransport Create() => new InMemoryTransport()
        .Define(new(DemoMaps.ShapeId, "geometry.Shape", _ => throw new JsException("TypeError", "Shape is abstract", null))
        {
            Value = true,
            Members = ["type"],
        })
        .Define(new(DemoMaps.PointId, "geometry.Point", args => new FakePoint(Bag(args))) { Value = true, Members = ["type", "x", "y"] })
        .Define(new(DemoMaps.LineId, "geometry.Line", args => new FakeLine(Bag(args))) { Value = true, Members = ["type", "path"] })
        .Define(new(DemoMaps.PolygonId, "geometry.Polygon", args => new FakePolygon(Bag(args))) { Value = true, Members = ["type", "vertices"] })
        .Define(new(DemoMaps.MapViewId, "MapView", args => new FakeMapView(Bag(args)))
        {
            Members = ["center", "zoom", "rotation", "basemap", "title"],
        })
        .Define(new(DemoMaps.MarkerId, "Marker", args => new FakeMarker(Bag(args))) { Members = ["position", "label", "style"] })
        .DefineModule(TransportContract.Measure, new FakeMeasure());

    // The constructor bag: `new X({ … } = {})`.
    private static Dictionary<string, object?> Bag(object?[] args) => args.FirstOrDefault() as Dictionary<string, object?> ?? [];
}

/// <summary><c>core.Observable</c>: <c>watch</c> calls back on every change, with the new and the old value.</summary>
internal abstract class FakeObservable : InMemoryObject
{
    private readonly Dictionary<string, List<JsFunction>> watchers = [];

    public override void Set(string name, object? value)
    {
        var old = base.Get(name);
        if (Equals(old, value)) return; // Object.is
        base.Set(name, value);
        foreach (var watcher in watchers.GetValueOrDefault(name)?.ToList() ?? [])
        {
            watcher(value, old);
        }
    }

    public override object? Call(string name, object?[] args) =>
        name == "watch" ? Listen(watchers, (string)args[0]!, (JsFunction)args[1]!) : base.Call(name, args);

    protected static IDisposable Listen(Dictionary<string, List<JsFunction>> all, string name, JsFunction callback)
    {
        var list = all.TryGetValue(name, out var l) ? l : all[name] = [];
        list.Add(callback);
        return new Subscription(() => list.Remove(callback));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary><c>core.Emitter</c>: <c>on</c> returns a disposable subscription; <c>emit</c> calls the listeners.</summary>
internal abstract class FakeEmitter : FakeObservable
{
    private readonly Dictionary<string, List<JsFunction>> listeners = [];

    public override object? Call(string name, object?[] args)
    {
        switch (name)
        {
            case "on":
                return Listen(listeners, (string)args[0]!, (JsFunction)args[1]!);
            case "emit":
                var current = listeners.GetValueOrDefault((string)args[0]!) ?? [];
                foreach (var listener in current.ToList())
                {
                    listener(args[1]);
                }
                return current.Count > 0;
            default:
                return base.Call(name, args);
        }
    }
}

/// <summary><c>geometry.Shape</c>: <c>bounds</c> from the coordinates, and <c>clone()</c>.</summary>
internal abstract class FakeShape : FakeObservable
{
    protected abstract IEnumerable<List<object?>> Coordinates { get; }

    protected abstract FakeShape Clone();

    public override object? Get(string name) => name == "bounds" ? Bounds() : base.Get(name);

    public override object? Call(string name, object?[] args) => name == "clone" ? Clone() : base.Call(name, args);

    private Dictionary<string, object?> Bounds()
    {
        var points = Coordinates.Select(c => ((double)c[0]!, (double)c[1]!)).ToList();
        if (points.Count == 0) return new() { ["minX"] = 0.0, ["minY"] = 0.0, ["maxX"] = 0.0, ["maxY"] = 0.0 };
        return new()
        {
            ["minX"] = points.Min(p => p.Item1),
            ["minY"] = points.Min(p => p.Item2),
            ["maxX"] = points.Max(p => p.Item1),
            ["maxY"] = points.Max(p => p.Item2),
        };
    }

    // `value.map(([x, y]) => [x, y])`
    protected static List<object?> CopyPairs(object? value) =>
        [.. ((List<object?>)value!).Select(pair => (object?)new List<object?> { ((List<object?>)pair!)[0], ((List<object?>)pair!)[1] })];
}

internal sealed class FakePoint : FakeShape
{
    public FakePoint(Dictionary<string, object?> bag)
    {
        Set("x", bag.GetValueOrDefault("x", 0.0));
        Set("y", bag.GetValueOrDefault("y", 0.0));
    }

    public override string ClassName => "geometry.Point";

    protected override IEnumerable<List<object?>> Coordinates => [[Get("x"), Get("y")]];

    protected override FakeShape Clone() => new FakePoint(new() { ["x"] = Get("x"), ["y"] = Get("y") });

    public override object? Get(string name) => name == "type" ? "point" : base.Get(name);
}

internal sealed class FakeLine : FakeShape
{
    public FakeLine(Dictionary<string, object?> bag) => Set("path", bag.GetValueOrDefault("path") ?? new List<object?>());

    public override string ClassName => "geometry.Line";

    protected override IEnumerable<List<object?>> Coordinates => ((List<object?>)Get("path")!).Cast<List<object?>>();

    protected override FakeShape Clone() => new FakeLine(new() { ["path"] = Get("path") });

    public override object? Get(string name) => name == "type" ? "line" : base.Get(name);

    public override void Set(string name, object? value) => base.Set(name, name == "path" ? CopyPairs(value) : value);
}

internal sealed class FakePolygon : FakeShape
{
    public FakePolygon(Dictionary<string, object?> bag) => Set("vertices", bag.GetValueOrDefault("vertices") ?? new List<object?>());

    public override string ClassName => "geometry.Polygon";

    protected override IEnumerable<List<object?>> Coordinates => ((List<object?>)Get("vertices")!).Cast<List<object?>>();

    protected override FakeShape Clone() => new FakePolygon(new() { ["vertices"] = Get("vertices") });

    public override object? Get(string name) => name == "type" ? "polygon" : base.Get(name);

    public override void Set(string name, object? value) => base.Set(name, name == "vertices" ? CopyPairs(value) : value);
}

internal sealed class FakeMapView : FakeEmitter
{
    public FakeMapView(Dictionary<string, object?> bag)
    {
        Set("center", bag.GetValueOrDefault("center") ?? new FakePoint([]));
        Set("zoom", bag.GetValueOrDefault("zoom", 1.0));
        Set("rotation", bag.GetValueOrDefault("rotation", 0.0));
        Set("basemap", bag.GetValueOrDefault("basemap", "plain"));
        Set("cursor", bag.GetValueOrDefault("cursor", "default"));
        Set("title", bag.GetValueOrDefault("title", Undefined.Value));
        Set("container", bag.GetValueOrDefault("container"));
        Set("selected", null);
    }

    public override string ClassName => "MapView";

    public override void Set(string name, object? value) => base.Set(name, name switch
    {
        "zoom" => Math.Clamp((double)value!, 0, 20),
        "rotation" => (((double)value! % 360) + 360) % 360,
        "container" => value is Undefined ? null : value,
        _ => value,
    });

    public override object? Call(string name, object?[] args)
    {
        switch (name)
        {
            case "flyTo": // the real one resolves after a timer of `duration` ms
                FlyTo(args[0]);
                return Task.FromResult<object?>(Undefined.Value);
            case "markerAt": // the fakes have no markers collection
                return Task.FromResult<object?>(null);
            default:
                return base.Call(name, args);
        }
    }

    // The target is a Shape, an [x, y] pair, Bounds or { center, zoom }.
    private void FlyTo(object? target)
    {
        switch (target)
        {
            case FakeShape shape:
                Set("center", Midpoint((Dictionary<string, object?>)shape.Get("bounds")!));
                break;
            case List<object?> pair:
                Set("center", new FakePoint(new() { ["x"] = pair[0], ["y"] = pair[1] }));
                break;
            case Dictionary<string, object?> camera when camera.TryGetValue("center", out var center):
                var xy = (List<object?>)center!;
                Set("center", new FakePoint(new() { ["x"] = xy[0], ["y"] = xy[1] }));
                Set("zoom", camera["zoom"]);
                break;
            case Dictionary<string, object?> bounds:
                Set("center", Midpoint(bounds));
                break;
        }
    }

    private static FakePoint Midpoint(Dictionary<string, object?> b) => new(new()
    {
        ["x"] = ((double)b["minX"]! + (double)b["maxX"]!) / 2,
        ["y"] = ((double)b["minY"]! + (double)b["maxY"]!) / 2,
    });
}

internal sealed class FakeMarker : FakeObservable
{
    public FakeMarker(Dictionary<string, object?> bag)
    {
        Set("position", bag.GetValueOrDefault("position") ?? new FakePoint([]));
        Set("label", bag.GetValueOrDefault("label", Undefined.Value));
        Set("style", bag.GetValueOrDefault("style") ?? new Dictionary<string, object?> { ["type"] = "icon", ["name"] = "pin-1" });
        Set("labelFormatter", bag.GetValueOrDefault("labelFormatter", Undefined.Value));
    }

    public override string ClassName => "Marker";

    // The getter always returns a Point; the setter also takes an [x, y] pair.
    public override void Set(string name, object? value) =>
        base.Set(name, name == "position" && value is List<object?> xy ? new FakePoint(new() { ["x"] = xy[0], ["y"] = xy[1] }) : value);

    // Calls the formatter synchronously and uses its return value (R21).
    public override object? Get(string name) => name == "displayLabel"
        ? base.Get("labelFormatter") is JsFunction format ? format(this) : base.Get("label") is string label ? label : ""
        : base.Get(name);
}

/// <summary>The <c>measure.js</c> module. It reads fields only, so plain objects work as well as instances.</summary>
internal sealed class FakeMeasure : InMemoryObject
{
    public override string ClassName => "measure";

    public override object? Call(string name, object?[] args)
    {
        if (name != "distance") return base.Call(name, args);
        var (a, b) = (args[0]!, args[1]!);
        return Math.Sqrt(Math.Pow(Field(b, "x") - Field(a, "x"), 2) + Math.Pow(Field(b, "y") - Field(a, "y"), 2));
    }

    private static double Field(object value, string name) =>
        (double)(value is InMemoryObject o ? o.Get(name) : ((Dictionary<string, object?>)value)[name])!;
}
