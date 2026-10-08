// What the generator would emit for a JS class `demo.MapView` with `zoom: number`,
// `rotation: number` and a `click` event: every JS property becomes Get{Name}Async / Set{Name}Async.
using System.Text.Json;
using System.Text.Json.Serialization;
using HostsSpike.Client.Runtime;

namespace HostsSpike.Client.Generated;

public sealed class MapView : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IJsTransport _js;

    private MapView(IJsTransport js, int handle) => (_js, Handle) = (js, handle);

    public int Handle { get; }

    public static async ValueTask<MapView> CreateAsync(IJsTransport js, MapViewProperties properties) =>
        new(js, await js.CreateAsync("demo.MapView", JsonSerializer.Serialize(properties, Json)));

    public ValueTask<double> GetZoomAsync() => _js.GetNumberAsync(Handle, "zoom");
    public ValueTask SetZoomAsync(double value) => _js.SetNumberAsync(Handle, "zoom", value);

    public ValueTask<double> GetRotationAsync() => _js.GetNumberAsync(Handle, "rotation");
    public ValueTask SetRotationAsync(double value) => _js.SetNumberAsync(Handle, "rotation", value);

    /// <summary>One round trip for every data property.</summary>
    public async ValueTask<MapViewProperties> SnapshotAsync() =>
        JsonSerializer.Deserialize<MapViewProperties>(await _js.SnapshotAsync(Handle, "zoom,rotation"), Json)!;

    public async ValueTask<IAsyncDisposable> OnClickAsync(Action<ClickEvent> handler)
    {
        var id = JsCallbacks.Register(payload => handler(JsonSerializer.Deserialize<ClickEvent>(payload, Json)!));
        await _js.SubscribeAsync(Handle, "click", id);
        return new Subscription(id);
    }

    public ValueTask DisposeAsync() => _js.ReleaseAsync(Handle);

    private sealed class Subscription(int id) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            JsCallbacks.Unregister(id);
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record MapViewProperties
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Zoom { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Rotation { get; init; }
}

public sealed record ClickEvent(double X, double Y);
