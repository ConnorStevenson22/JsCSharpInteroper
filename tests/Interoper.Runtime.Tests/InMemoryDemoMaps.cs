// Fakes of the fixtures/demo-maps classes the transport contract uses, for InMemoryTransport. They copy the JS
// behaviour the contract observes, nothing more.
using Interoper.Contract;

namespace Interoper.Runtime.Tests;

internal static class InMemoryDemoMaps
{
    public static InMemoryTransport Create() => new InMemoryTransport()
        .Define(new(TransportContract.Shape, "geometry.Shape", _ => throw new JsException("TypeError", "Shape is abstract", null))
        {
            Value = true,
            Members = ["type"],
        })
        .Define(new(TransportContract.Point, "geometry.Point", args => new FakePoint(Bag(args))) { Value = true, Members = ["type", "x", "y"] })
        .Define(new(TransportContract.MapView, "MapView", args => new FakeMapView(Bag(args)))
        {
            Members = ["center", "zoom", "rotation", "basemap", "cursor", "title", "selected", "markers"],
        })
        .Define(new(TransportContract.Marker, "Marker", args => new FakeMarker(Bag(args))) { Members = ["position", "label", "style"] });

    // The constructor bag: `new X({ … } = {})`.
    private static Dictionary<string, object?> Bag(object?[] args) => args.FirstOrDefault() as Dictionary<string, object?> ?? [];
}

/// <summary><c>core.Emitter</c>: <c>on</c> returns a disposable subscription; <c>emit</c> calls the listeners.</summary>
internal abstract class FakeEmitter : InMemoryObject
{
    private readonly Dictionary<string, List<JsFunction>> listeners = [];

    public override object? Call(string name, object?[] args)
    {
        switch (name)
        {
            case "on":
                var list = listeners.TryGetValue((string)args[0]!, out var l) ? l : listeners[(string)args[0]!] = [];
                var listener = (JsFunction)args[1]!;
                list.Add(listener);
                return new Subscription(() => list.Remove(listener));
            case "emit":
                var current = listeners.GetValueOrDefault((string)args[0]!) ?? [];
                foreach (var each in current.ToList())
                {
                    each(args[1]);
                }
                return current.Count > 0;
            default:
                return base.Call(name, args);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakePoint : InMemoryObject
{
    public FakePoint(Dictionary<string, object?> bag)
    {
        Set("x", bag.GetValueOrDefault("x", 0.0));
        Set("y", bag.GetValueOrDefault("y", 0.0));
    }

    public override string ClassName => "geometry.Point";

    public override object? Get(string name) => name == "type" ? "point" : base.Get(name);
}

internal sealed class FakeMapView : FakeEmitter
{
    public FakeMapView(Dictionary<string, object?> bag)
    {
        Set("center", bag.GetValueOrDefault("center") ?? new FakePoint([]));
        Set("zoom", bag.GetValueOrDefault("zoom", 1.0));
        Set("basemap", bag.GetValueOrDefault("basemap", "plain"));
        Set("selected", null);
    }

    public override string ClassName => "MapView";

    public override void Set(string name, object? value) =>
        base.Set(name, name == "zoom" ? Math.Clamp((double)value!, 0, 20) : value);

    public override object? Call(string name, object?[] args)
    {
        switch (name)
        {
            case "flyTo": // only the [x, y] target; the real one resolves after a timer
                var target = (List<object?>)args[0]!;
                Set("center", new FakePoint(new() { ["x"] = target[0], ["y"] = target[1] }));
                return Task.FromResult<object?>(Undefined.Value);
            case "markerAt": // no markers here
                return Task.FromResult<object?>(null);
            default:
                return base.Call(name, args);
        }
    }
}

internal sealed class FakeMarker : InMemoryObject
{
    public FakeMarker(Dictionary<string, object?> bag)
    {
        Set("label", bag.GetValueOrDefault("label", Undefined.Value));
        Set("labelFormatter", bag.GetValueOrDefault("labelFormatter", Undefined.Value));
    }

    public override string ClassName => "Marker";

    // Calls the formatter synchronously and uses its return value (R21).
    public override object? Get(string name) =>
        name == "displayLabel" ? (base.Get("labelFormatter") is JsFunction format ? format(this) : base.Get("label")) : base.Get(name);
}
