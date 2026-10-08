// Generated with `--api sync` (WebAssembly only): JS property `zoom` -> double Zoom { get; set; }.
using System.Text.Json;
using HostsSpike.Client.Runtime;

namespace HostsSpike.Client.Generated.Sync;

public sealed class MapView : IDisposable
{
    private readonly IJsSyncTransport _js;

    private MapView(IJsSyncTransport js, int handle) => (_js, Handle) = (js, handle);

    public int Handle { get; }

    public static MapView Create(IJsSyncTransport js, MapViewSnapshot properties) =>
        new(js, js.Create("demo.MapView", JsonSerializer.Serialize(properties, GeneratedJson.Options)));

    public double Zoom
    {
        get => _js.GetNumber(Handle, "zoom");
        set => _js.SetNumber(Handle, "zoom", value);
    }

    public double Rotation
    {
        get => _js.GetNumber(Handle, "rotation");
        set => _js.SetNumber(Handle, "rotation", value);
    }

    /// <summary>One call for every data property.</summary>
    public MapViewSnapshot Snapshot() =>
        JsonSerializer.Deserialize<MapViewSnapshot>(_js.Snapshot(Handle, "zoom,rotation"), GeneratedJson.Options)!;

    public IDisposable OnClick(Action<ClickEvent> handler)
    {
        var id = JsCallbacks.Register(payload => handler(JsonSerializer.Deserialize<ClickEvent>(payload, GeneratedJson.Options)!));
        _js.Subscribe(Handle, "click", id);
        return new Subscription(id);
    }

    public void Dispose() => _js.Release(Handle);

    private sealed class Subscription(int id) : IDisposable
    {
        public void Dispose() => JsCallbacks.Unregister(id);
    }
}
