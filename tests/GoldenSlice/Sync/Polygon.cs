using Interoper;

namespace GoldenSlice.Sync;

/// <summary><c>class Polygon extends Shape</c>: value-like (R16).</summary>
public sealed class Polygon : Shape
{
    internal Polygon(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Polygon(options)</c> (R18).</summary>
    public static Polygon Create(JsScope scope, PolygonSnapshot props) =>
        scope.Realm.Call(JsOp.Create(DemoMaps.PolygonId, w => PolygonSnapshot.WriteMembers(w, props)), scope.Read<Polygon>)!;

    /// <summary><c>vertices: [number, number][]</c> (R5).</summary>
    public double[][] Vertices
    {
        get => Realm.Call(JsOp.Get(Handle, "vertices"), static (ref r) => JsJson.ReadArray(ref r, static (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber)))!;
        set => Realm.Send(JsOp.Set(Handle, "vertices", w => JsJson.WriteArray(w, value, static (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber))));
    }

    public new PolygonSnapshot Snapshot() => Realm.Call(JsOp.Snapshot(Handle), PolygonSnapshot.Read)!;

    public void Apply(PolygonSnapshot patch) => Realm.Send(JsOp.Apply(Handle, w => PolygonSnapshot.WriteMembers(w, patch)));
}
