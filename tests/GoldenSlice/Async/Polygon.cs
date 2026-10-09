using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>class Polygon extends Shape</c>: value-like (R16).</summary>
public sealed class Polygon : Shape
{
    internal Polygon(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Polygon(options)</c> (R18).</summary>
    public static async Task<Polygon> CreateAsync(JsScope scope, PolygonSnapshot props) =>
        (await scope.Realm.CallAsync(JsOp.Create(DemoMaps.PolygonId, w => PolygonSnapshot.WriteMembers(w, props)), scope.Read<Polygon>))!;

    /// <summary><c>vertices: [number, number][]</c> (R5).</summary>
    public async Task<double[][]> GetVerticesAsync() =>
        (await Realm.CallAsync(JsOp.Get(Handle, "vertices"), static (ref r) => JsJson.ReadArray(ref r, static (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber))))!;

    public Task SetVerticesAsync(double[][] value) =>
        Realm.SendAsync(JsOp.Set(Handle, "vertices", w => JsJson.WriteArray(w, value, static (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber))));

    public new async Task<PolygonSnapshot> SnapshotAsync() => (await Realm.CallAsync(JsOp.Snapshot(Handle), PolygonSnapshot.Read))!;

    public Task ApplyAsync(PolygonSnapshot patch) => Realm.SendAsync(JsOp.Apply(Handle, w => PolygonSnapshot.WriteMembers(w, patch)));
}
