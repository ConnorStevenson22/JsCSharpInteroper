using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>class MapView extends Emitter</c>: an entity (R15).</summary>
public sealed class MapView : JsObject
{
    internal MapView(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new MapView(options)</c> (R18).</summary>
    public static async Task<MapView> CreateAsync(JsScope scope, MapViewSnapshot props) =>
        (await scope.Realm.CallAsync(JsOp.Create(DemoMaps.MapViewId, w => MapViewSnapshot.WriteMembers(w, props)), scope.Read<MapView>))!;

    /// <summary><c>center: Point</c>: value-like, so by value (R16).</summary>
    public async Task<PointSnapshot> GetCenterAsync() => (await Realm.CallAsync(JsOp.Get(Handle, "center", byValue: true), PointSnapshot.Read))!;

    public Task SetCenterAsync(PointSnapshot value) => Realm.SendAsync(JsOp.Set(Handle, "center", w => PointSnapshot.Write(w, value)));

    public Task<double> GetZoomAsync() => Realm.GetNumberAsync(Handle, "zoom");

    public Task SetZoomAsync(double value) => Realm.SetAsync(Handle, "zoom", value);

    public Task<double> GetRotationAsync() => Realm.GetNumberAsync(Handle, "rotation");

    public Task SetRotationAsync(double value) => Realm.SetAsync(Handle, "rotation", value);

    /// <summary><c>basemap: Basemap</c>: an enum through the string fast path (R9).</summary>
    public async Task<Basemap> GetBasemapAsync() => BasemapNames.FromJs(await Realm.GetStringAsync(Handle, "basemap"));

    public Task SetBasemapAsync(Basemap value) => Realm.SetAsync(Handle, "basemap", BasemapNames.ToJs(value));

    /// <summary><c>title?: string | null</c> (R3).</summary>
    public Task<Opt<string>> GetTitleAsync() => Realm.CallAsync(JsOp.Get(Handle, "title"), static (ref r) => JsJson.ReadOpt(ref r, JsJson.ReadString));

    public Task SetTitleAsync(Opt<string> value) => Realm.SendAsync(JsOp.Set(Handle, "title", w => JsJson.WriteValue(w, value, JsJson.WriteString)));

    /// <summary><c>selected: Marker | null</c> (R3): a live result, owned by <paramref name="scope"/>.</summary>
    public Task<Opt<Marker>> GetSelectedAsync(JsScope scope) => Realm.CallAsync(JsOp.Get(Handle, "selected"), (ref r) => JsJson.ReadOpt(ref r, scope.Read<Marker>));

    public Task SetSelectedAsync(Opt<Marker> value) => Realm.SendAsync(JsOp.Set(Handle, "selected", w => JsJson.WriteValue(w, value, Marker.Write)));

    /// <summary><c>flyTo(target: FlyToTarget): Promise&lt;void&gt;</c> (R8).</summary>
    public Task FlyToAsync(FlyToTarget target) => Realm.SendAsync(JsOp.Call(Handle, "flyTo", w => JsJson.WriteValue(w, target)));

    /// <summary>The <c>click</c> event (R24); the subscription belongs to <paramref name="scope"/>.</summary>
    public Task<IAsyncDisposable> OnClickAsync(JsScope scope, Action<ClickEvent> handler) =>
        scope.SubscribeAsync(Handle, "on", "click", ClickEvent.Read, handler);

    /// <summary><c>watch("zoom", callback)</c>: the new value on each change; the subscription belongs to <paramref name="scope"/>.</summary>
    public Task<IAsyncDisposable> WatchZoomAsync(JsScope scope, Action<double> handler) =>
        scope.SubscribeAsync(Handle, "watch", "zoom", JsJson.ReadNumber, handler);

    public async Task<MapViewSnapshot> SnapshotAsync() => (await Realm.CallAsync(JsOp.Snapshot(Handle), MapViewSnapshot.Read))!;

    public Task ApplyAsync(MapViewSnapshot patch) => Realm.SendAsync(JsOp.Apply(Handle, w => MapViewSnapshot.WriteMembers(w, patch)));
}
