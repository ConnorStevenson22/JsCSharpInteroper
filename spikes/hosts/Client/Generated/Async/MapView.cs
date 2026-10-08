// Generated with `--api async` (every host): JS property `zoom` -> Task<double> GetZoomAsync() / Task SetZoomAsync(double).
using System.Text.Json;
using HostsSpike.Client.Runtime;

namespace HostsSpike.Client.Generated.Async;

public sealed class MapView : IAsyncDisposable
{
    private readonly IJsTransport _js;

    private MapView(IJsTransport js, int handle) => (_js, Handle) = (js, handle);

    public int Handle { get; }

    public static async Task<MapView> CreateAsync(IJsTransport js, MapViewSnapshot properties) =>
        new(js, await js.CreateAsync("demo.MapView", JsonSerializer.Serialize(properties, GeneratedJson.Options)));

    public Task<double> GetZoomAsync() => _js.GetNumberAsync(Handle, "zoom");
    public Task SetZoomAsync(double value) => _js.SetNumberAsync(Handle, "zoom", value);

    public Task<double> GetRotationAsync() => _js.GetNumberAsync(Handle, "rotation");
    public Task SetRotationAsync(double value) => _js.SetNumberAsync(Handle, "rotation", value);

    /// <summary>One round trip for every data property.</summary>
    public async Task<MapViewSnapshot> SnapshotAsync() =>
        JsonSerializer.Deserialize<MapViewSnapshot>(await _js.SnapshotAsync(Handle, "zoom,rotation"), GeneratedJson.Options)!;

    public async Task<IAsyncDisposable> OnClickAsync(Action<ClickEvent> handler)
    {
        var id = JsCallbacks.Register(payload => handler(JsonSerializer.Deserialize<ClickEvent>(payload, GeneratedJson.Options)!));
        await _js.SubscribeAsync(Handle, "click", id);
        return new Subscription(id);
    }

    public async ValueTask DisposeAsync() => await _js.ReleaseAsync(Handle);

    private sealed class Subscription(int id) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            JsCallbacks.Unregister(id);
            return ValueTask.CompletedTask;
        }
    }
}
