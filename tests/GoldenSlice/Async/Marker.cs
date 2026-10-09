using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>class Marker extends Observable</c>: an entity (R15).</summary>
public sealed class Marker : JsObject
{
    internal Marker(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Marker(options)</c> (R18).</summary>
    public static async Task<Marker> CreateAsync(JsScope scope, MarkerSnapshot props) =>
        (await scope.Realm.CallAsync(JsOp.Create(DemoMaps.MarkerId, w => MarkerSnapshot.WriteMembers(w, props)), scope.Read<Marker>))!;

    /// <summary><c>get position(): Point</c>: value-like, so by value (R16).</summary>
    public async Task<PointSnapshot> GetPositionAsync() => (await Realm.CallAsync(JsOp.Get(Handle, "position", byValue: true), PointSnapshot.Read))!;

    /// <summary><c>set position(value: Point | [number, number])</c>: wider than the getter (§5, R27).</summary>
    public Task SetPositionAsync(MarkerPositionInput value) => Realm.SendAsync(JsOp.Set(Handle, "position", w => JsJson.WriteValue(w, value)));

    /// <summary><c>label?: string | null</c> (R3).</summary>
    public Task<Opt<string>> GetLabelAsync() => Realm.CallAsync(JsOp.Get(Handle, "label"), static (ref r) => JsJson.ReadOpt(ref r, JsJson.ReadString));

    public Task SetLabelAsync(Opt<string> value) => Realm.SendAsync(JsOp.Set(Handle, "label", w => JsJson.WriteValue(w, value, JsJson.WriteString)));

    /// <summary><c>style: Style</c>, a union read back by its discriminant (R2, R19).</summary>
    public Task<Style> GetStyleAsync() => Realm.CallAsync(JsOp.Get(Handle, "style"), JsJson.ReadUnion<Style>);

    public Task SetStyleAsync(Style value) => Realm.SendAsync(JsOp.Set(Handle, "style", w => JsJson.WriteValue(w, value)));

    /// <summary><c>readonly displayLabel: string</c>.</summary>
    public Task<string> GetDisplayLabelAsync() => Realm.GetStringAsync(Handle, "displayLabel");

    public async Task<MarkerSnapshot> SnapshotAsync() => (await Realm.CallAsync(JsOp.Snapshot(Handle), MarkerSnapshot.Read))!;

    public Task ApplyAsync(MarkerSnapshot patch) => Realm.SendAsync(JsOp.Apply(Handle, w => MarkerSnapshot.WriteMembers(w, patch)));
}
