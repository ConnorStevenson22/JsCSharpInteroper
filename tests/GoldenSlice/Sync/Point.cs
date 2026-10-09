using Interoper;

namespace GoldenSlice.Sync;

/// <summary><c>class Point extends Shape</c>: value-like (R16).</summary>
public sealed class Point : Shape
{
    internal Point(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Point(options)</c> (R18).</summary>
    public static Point Create(JsScope scope, PointSnapshot props) =>
        scope.Realm.Call(JsOp.Create(DemoMaps.PointId, w => PointSnapshot.WriteMembers(w, props)), scope.Read<Point>)!;

    public double X
    {
        get => Realm.SyncTransport.GetNumber(Handle, "x");
        set => Realm.SyncTransport.SetNumber(Handle, "x", value);
    }

    public double Y
    {
        get => Realm.SyncTransport.GetNumber(Handle, "y");
        set => Realm.SyncTransport.SetNumber(Handle, "y", value);
    }

    public new PointSnapshot Snapshot() => Realm.Call(JsOp.Snapshot(Handle), PointSnapshot.Read)!;

    public void Apply(PointSnapshot patch) => Realm.Send(JsOp.Apply(Handle, w => PointSnapshot.WriteMembers(w, patch)));
}
