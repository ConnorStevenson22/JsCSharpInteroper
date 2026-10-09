using Interoper;

namespace GoldenSlice.Sync;

/// <summary><c>class MapView extends Emitter</c>: an entity (R15).</summary>
public sealed class MapView : JsObject
{
    internal MapView(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new MapView(options)</c> (R18).</summary>
    public static MapView Create(JsScope scope, MapViewSnapshot props) =>
        scope.Realm.Call(JsOp.Create(DemoMaps.MapViewId, w => MapViewSnapshot.WriteMembers(w, props)), scope.Read<MapView>)!;

    /// <summary><c>center: Point</c>: value-like, so by value (R16).</summary>
    public PointSnapshot Center
    {
        get => Realm.Call(JsOp.Get(Handle, "center", byValue: true), PointSnapshot.Read)!;
        set => Realm.Send(JsOp.Set(Handle, "center", w => PointSnapshot.Write(w, value)));
    }

    public double Zoom
    {
        get => Realm.SyncTransport.GetNumber(Handle, "zoom");
        set => Realm.SyncTransport.SetNumber(Handle, "zoom", value);
    }

    public double Rotation
    {
        get => Realm.SyncTransport.GetNumber(Handle, "rotation");
        set => Realm.SyncTransport.SetNumber(Handle, "rotation", value);
    }

    /// <summary><c>basemap: Basemap</c>: an enum through the string fast path (R9).</summary>
    public Basemap Basemap
    {
        get => BasemapNames.FromJs(Realm.SyncTransport.GetString(Handle, "basemap"));
        set => Realm.SyncTransport.SetString(Handle, "basemap", BasemapNames.ToJs(value));
    }

    /// <summary><c>title?: string | null</c> (R3).</summary>
    public Opt<string> Title
    {
        get => Realm.Call(JsOp.Get(Handle, "title"), static (ref r) => JsJson.ReadOpt(ref r, JsJson.ReadString));
        set => Realm.Send(JsOp.Set(Handle, "title", w => JsJson.WriteValue(w, value, JsJson.WriteString)));
    }

    /// <summary><c>selected: Marker | null</c> (R3): a live result, owned by <paramref name="scope"/>.</summary>
    public Opt<Marker> GetSelected(JsScope scope) => Realm.Call(JsOp.Get(Handle, "selected"), (ref r) => JsJson.ReadOpt(ref r, scope.Read<Marker>));

    public void SetSelected(Opt<Marker> value) => Realm.Send(JsOp.Set(Handle, "selected", w => JsJson.WriteValue(w, value, Marker.Write)));

    /// <summary><c>flyTo(target: FlyToTarget): Promise&lt;void&gt;</c>: a Promise is a Task in both modes (R8).</summary>
    public Task FlyToAsync(FlyToTarget target) => Realm.SendAsync(JsOp.Call(Handle, "flyTo", w => JsJson.WriteValue(w, target)));

    /// <summary>The <c>click</c> event (R24); the subscription belongs to <paramref name="scope"/>.</summary>
    public IDisposable OnClick(JsScope scope, Action<ClickEvent> handler) =>
        scope.Subscribe(Handle, "on", "click", ClickEvent.Read, handler);

    /// <summary><c>watch("zoom", callback)</c>: the new value on each change; the subscription belongs to <paramref name="scope"/>.</summary>
    public IDisposable WatchZoom(JsScope scope, Action<double> handler) =>
        scope.Subscribe(Handle, "watch", "zoom", JsJson.ReadNumber, handler);

    public MapViewSnapshot Snapshot() => Realm.Call(JsOp.Snapshot(Handle), MapViewSnapshot.Read)!;

    public void Apply(MapViewSnapshot patch) => Realm.Send(JsOp.Apply(Handle, w => MapViewSnapshot.WriteMembers(w, patch)));
}
