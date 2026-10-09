using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>class Point extends Shape</c>: value-like (R16).</summary>
public sealed class Point : Shape
{
    internal Point(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Point(options)</c> (R18).</summary>
    public static async Task<Point> CreateAsync(JsScope scope, PointSnapshot props) =>
        (await scope.Realm.CallAsync(JsOp.Create(DemoMaps.PointId, w => PointSnapshot.WriteMembers(w, props)), scope.Read<Point>))!;

    public Task<double> GetXAsync() => Realm.GetNumberAsync(Handle, "x");

    public Task SetXAsync(double value) => Realm.SetAsync(Handle, "x", value);

    public Task<double> GetYAsync() => Realm.GetNumberAsync(Handle, "y");

    public Task SetYAsync(double value) => Realm.SetAsync(Handle, "y", value);

    public new async Task<PointSnapshot> SnapshotAsync() => (await Realm.CallAsync(JsOp.Snapshot(Handle), PointSnapshot.Read))!;

    public Task ApplyAsync(PointSnapshot patch) => Realm.SendAsync(JsOp.Apply(Handle, w => PointSnapshot.WriteMembers(w, patch)));
}
