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
            Members = ["center", "zoom", "rotation", "basemap", "cursor", "title"],
        })
        .Define(new(DemoMaps.MarkerId, "Marker", args => new FakeMarker(Bag(args))) { Members = ["position", "label", "style"] })
        .Define(new(DemoMaps.ObservableId, "core.Observable", _ => new FakeObservable()))
        .Define(new(DemoMaps.EmitterId, "core.Emitter", _ => new FakeEmitter()))
        .Define(new(DemoMaps.CollectionId, "core.Collection", args => new FakeCollection(args.FirstOrDefault() as List<object?> ?? [])))
        .Define(new(DemoMaps.DemoMapElementId, "demo-map.DemoMapElement", _ => new FakeDemoMapElement()) { Members = ["basemap", "zoom", "center"] })
        .Define(new(DemoMaps.LayerId, "layers.Layer", _ => throw new JsException("TypeError", "Layer is abstract", null)) { Members = ["title", "visible"] })
        .Define(new(DemoMaps.TileLayerId, "layers.TileLayer", args => new FakeTileLayer(Bag(args))) { Members = ["url", "refreshInterval", "title", "visible"] })
        .Define(new(DemoMaps.PinId, "pins.Pin", args => new FakePin(Bag(args))) { Members = ["position", "label", "style"] })
        .DefineModule(TransportContract.Measure, new FakeMeasure())
        .DefineModule("@acme/demo-maps/ops/a.js", new FakeExecute((x, options) => x * (double)options.GetValueOrDefault("scale", 1.0)!))
        .DefineModule("@acme/demo-maps/ops/b.js", new FakeExecute((x, options) => x + (double)options.GetValueOrDefault("offset", 0.0)!))
        .DefineModule("@acme/demo-maps/styles.js", new FakeStyles());

    // The constructor bag: `new X({ … } = {})`.
    private static Dictionary<string, object?> Bag(object?[] args) => args.FirstOrDefault() as Dictionary<string, object?> ?? [];
}

/// <summary><c>core.Observable</c>: <c>watch</c> calls back on every change, with the new and the old value.</summary>
internal class FakeObservable : InMemoryObject
{
    private readonly Dictionary<string, List<JsFunction>> watchers = [];

    public override string ClassName => "core.Observable";

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
internal class FakeEmitter : FakeObservable
{
    private readonly Dictionary<string, List<JsFunction>> listeners = [];

    public override string ClassName => "core.Emitter";

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

/// <summary>
/// <c>core.Collection</c>: a live list that emits <c>change</c> on <c>add</c> and <c>remove</c>, and calls back from
/// <c>find</c> with each item and its index, and from <c>forEach</c> with the collection too.
/// </summary>
internal sealed class FakeCollection(List<object?> items) : FakeEmitter
{
    private readonly List<object?> items = [.. items];

    public override string ClassName => "core.Collection";

    public IReadOnlyList<object?> Items => items;

    public override object? Get(string name) => name == "length" ? (double)items.Count : base.Get(name);

    public override object? Call(string name, object?[] args)
    {
        switch (name)
        {
            case "at": // negative indexes count from the end
                var index = (int)(double)args[0]!;
                index = index < 0 ? items.Count + index : index;
                return index >= 0 && index < items.Count ? items[index] : Undefined.Value;
            case "add":
                items.Add(args[0]);
                Changed(added: [args[0]], removed: []);
                return Undefined.Value;
            case "remove":
                if (!items.Remove(args[0])) return false;
                Changed(added: [], removed: [args[0]]);
                return true;
            case "find":
                var predicate = (JsFunction)args[0]!;
                return items.Where((item, i) => Truthy(predicate(item, (double)i))).DefaultIfEmpty(Undefined.Value).First();
            case "forEach":
                var callback = (JsFunction)args[0]!;
                foreach (var (item, i) in items.ToList().Select((item, i) => (item, i)))
                {
                    callback(item, (double)i, this); // the collection too, which the type leaves out
                }
                return Undefined.Value;
            case "toArray":
                return items.ToList();
            default:
                return base.Call(name, args);
        }
    }

    private void Changed(List<object?> added, List<object?> removed) =>
        Call("emit", ["change", new Dictionary<string, object?> { ["added"] = added, ["removed"] = removed }]);

    private static bool Truthy(object? value) => value switch
    {
        null or Undefined => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        _ => true,
    };
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
    private readonly FakeCollection markers = new([]), layers = new([]);

    public FakeMapView(Dictionary<string, object?> bag)
    {
        Set("center", bag.GetValueOrDefault("center") ?? new FakePoint([]));
        Set("zoom", bag.GetValueOrDefault("zoom", 1.0));
        Set("rotation", bag.GetValueOrDefault("rotation", 0.0));
        Set("basemap", bag.GetValueOrDefault("basemap", "plain"));
        Set("cursor", bag.GetValueOrDefault("cursor", Undefined.Value));
        Set("title", bag.GetValueOrDefault("title", Undefined.Value));
        Set("container", bag.GetValueOrDefault("container"));
        Set("selected", null);
    }

    public override string ClassName => "MapView";

    public override object? Get(string name) => name switch
    {
        "markers" => markers,
        "layers" => layers,
        _ => base.Get(name),
    };

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
            case "screenToMap":
                return ScreenToMap(args);
            case "reset": // several properties in one call
                Set("zoom", 1.0);
                Set("rotation", 0.0);
                return Undefined.Value;
            case "markerAt": // the first marker within 10 pixels of the screen offset, or null
                var point = ScreenToMap(args);
                var tolerance = 10 / Math.Pow(2, (double)Get("zoom")!);
                return Task.FromResult(markers.Items.Cast<FakeMarker>().FirstOrDefault(m => Distance((FakePoint)m.Get("position")!, point) <= tolerance) as object);
            default:
                return base.Call(name, args);
        }
    }

    // An offset from the center, or an [x, y] pair, in pixels; one pixel is 2^-zoom map units.
    public FakePoint ScreenToMap(object?[] args)
    {
        var (x, y) = args[0] is List<object?> xy ? ((double)xy[0]!, (double)xy[1]!) : ((double)args[0]!, (double)args[1]!);
        var (center, scale) = ((FakePoint)Get("center")!, Math.Pow(2, (double)Get("zoom")!));
        return new FakePoint(new() { ["x"] = (double)center.Get("x")! + x / scale, ["y"] = (double)center.Get("y")! - y / scale });
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

    private static double Distance(FakePoint a, FakePoint b) =>
        Math.Sqrt(Math.Pow((double)a.Get("x")! - (double)b.Get("x")!, 2) + Math.Pow((double)a.Get("y")! - (double)b.Get("y")!, 2));

    private static FakePoint Midpoint(Dictionary<string, object?> b) => new(new()
    {
        ["x"] = ((double)b["minX"]! + (double)b["maxX"]!) / 2,
        ["y"] = ((double)b["minY"]! + (double)b["maxY"]!) / 2,
    });
}

internal class FakeMarker : FakeObservable
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

/// <summary>
/// <c>pins.Pin</c>: a Marker with the Draggable mixin, whose <c>on</c> returns a subscription with <c>remove()</c>, not
/// <c>dispose()</c>; <c>dragBy</c> moves the position and emits <c>drag</c>.
/// </summary>
internal sealed class FakePin(Dictionary<string, object?> bag) : FakeMarker(bag)
{
    private readonly List<JsFunction> drags = [];

    public override string ClassName => "pins.Pin";

    public override object? Call(string name, object?[] args)
    {
        switch (name)
        {
            case "on" when args[0] is "drag":
                var listener = (JsFunction)args[1]!;
                drags.Add(listener);
                return new Dictionary<string, object?> { ["remove"] = new JsFunction(_ => drags.Remove(listener)) };
            case "dragBy":
                var (dx, dy) = ((double)args[0]!, (double)args[1]!);
                var position = (FakePoint)Get("position")!;
                Set("position", new FakePoint(new() { ["x"] = (double)position.Get("x")! + dx, ["y"] = (double)position.Get("y")! + dy }));
                foreach (var drag in drags.ToList())
                {
                    drag(new Dictionary<string, object?> { ["dx"] = dx, ["dy"] = dy });
                }
                return Undefined.Value;
            default:
                return base.Call(name, args);
        }
    }
}

/// <summary><c>demo-map.DemoMapElement</c>: its properties are its view's, and <c>clickAt</c> emits the view's click.</summary>
internal sealed class FakeDemoMapElement : InMemoryObject
{
    private readonly FakeMapView view = new([]);

    public override string ClassName => "demo-map.DemoMapElement";

    public override object? Get(string name) => name switch
    {
        "view" => view,
        "basemap" or "zoom" or "center" => view.Get(name),
        _ => base.Get(name),
    };

    public override void Set(string name, object? value)
    {
        if (name is "basemap" or "zoom" or "center") view.Set(name, value);
        else base.Set(name, value);
    }

    public override object? Call(string name, object?[] args)
    {
        if (name != "clickAt") return base.Call(name, args);
        var (x, y) = ((double)args[0]!, (double)args[1]!);
        var click = new Dictionary<string, object?>
        {
            ["type"] = "click",
            ["x"] = x,
            ["y"] = y,
            ["button"] = args.Length > 2 && args[2] is double button ? button : 0.0,
            ["mapPoint"] = view.ScreenToMap([x, y]),
        };
        view.Call("emit", ["click", click]);
        return Undefined.Value;
    }
}

/// <summary><c>layers.Layer</c>: <c>clone()</c> copies the layer, of its own class.</summary>
internal abstract class FakeLayer : FakeObservable
{
    protected FakeLayer(Dictionary<string, object?> bag)
    {
        Set("title", bag.GetValueOrDefault("title"));
        Set("visible", bag.GetValueOrDefault("visible", true));
    }

    protected abstract FakeLayer Clone();

    public override object? Call(string name, object?[] args) => name == "clone" ? Clone() : base.Call(name, args);
}

/// <summary><c>layers.TileLayer</c>, the default export of its module, with the <c>Refreshable</c> mixin.</summary>
internal sealed class FakeTileLayer : FakeLayer
{
    public FakeTileLayer(Dictionary<string, object?> bag) : base(bag)
    {
        Set("url", bag.GetValueOrDefault("url", ""));
        Set("refreshInterval", 0.0);
        Set("refreshCount", 0.0);
    }

    public override string ClassName => "layers.TileLayer";

    public override object? Call(string name, object?[] args)
    {
        if (name != "refresh") return base.Call(name, args);
        Set("refreshCount", (double)Get("refreshCount")! + 1);
        return Undefined.Value;
    }

    protected override FakeLayer Clone() => new FakeTileLayer(new() { ["url"] = Get("url"), ["title"] = Get("title"), ["visible"] = Get("visible") });
}

/// <summary>The <c>styles.js</c> module: <c>toRgba</c> for a hex string or a color name, an array or an object.</summary>
internal sealed class FakeStyles : InMemoryObject
{
    private static readonly Dictionary<string, double[]> Named = new()
    {
        ["black"] = [0, 0, 0],
        ["white"] = [255, 255, 255],
        ["red"] = [255, 0, 0],
        ["green"] = [0, 128, 0],
        ["blue"] = [0, 0, 255],
    };

    public override string ClassName => "styles";

    public override object? Call(string name, object?[] args)
    {
        if (name != "toRgba") return base.Call(name, args);
        switch (args[0])
        {
            case string color:
                var rgb = color is ['#', ..] && color.Length == 7
                    ? [.. Enumerable.Range(0, 3).Select(i => (double)Convert.ToInt32(color.Substring(1 + (2 * i), 2), 16))]
                    : Named.GetValueOrDefault(color.ToLowerInvariant()) ?? throw new JsException("RangeError", $"Unknown color: {color}", null);
                return new Dictionary<string, object?> { ["r"] = rgb[0], ["g"] = rgb[1], ["b"] = rgb[2], ["a"] = 1.0 };
            case List<object?> rgba:
                return new Dictionary<string, object?> { ["r"] = rgba[0], ["g"] = rgba[1], ["b"] = rgba[2], ["a"] = rgba.Count > 3 ? rgba[3] : 1.0 };
            default: // { a: 1, ...color }
                var rgbaObject = (Dictionary<string, object?>)args[0]!;
                return new Dictionary<string, object?>(rgbaObject) { ["a"] = rgbaObject.GetValueOrDefault("a", 1.0) };
        }
    }
}

/// <summary>The <c>measure.js</c> module. It reads fields only, so plain objects work as well as instances.</summary>
internal sealed class FakeMeasure : InMemoryObject
{
    public override string ClassName => "measure";

    public override object? Call(string name, object?[] args) => name switch
    {
        "distance" => Distance(args[0]!, args[1]!),
        "perimeter" => args.Select((point, i) => Distance(point!, args[(i + 1) % args.Length]!)).Sum(), // the closed path through `...points`
        "describe" => args[0] is FakeShape shape ? shape.Get("type") : $"marker {((InMemoryObject)args[0]!).Get("displayLabel")}",
        _ => base.Call(name, args),
    };

    private static double Distance(object a, object b) =>
        Math.Sqrt(Math.Pow(Field(b, "x") - Field(a, "x"), 2) + Math.Pow(Field(b, "y") - Field(a, "y"), 2));

    private static double Field(object value, string name) =>
        (double)(value is InMemoryObject o ? o.Get(name) : ((Dictionary<string, object?>)value)[name])!;
}

/// <summary>The <c>ops/a.js</c> and <c>ops/b.js</c> modules: <c>execute(x, options?)</c>, each with its own options.</summary>
internal sealed class FakeExecute(Func<double, Dictionary<string, object?>, double> execute) : InMemoryObject
{
    public override string ClassName => "ops";

    public override object? Call(string name, object?[] args) =>
        name == "execute" ? execute((double)args[0]!, args.ElementAtOrDefault(1) as Dictionary<string, object?> ?? []) : base.Call(name, args);
}
