using Interoper;

namespace GoldenSlice.Async;

/// <summary>
/// <c>abstract class Shape</c>: value-like (R16), and a closed base, so a switch over its subclasses is exhaustive (R4).
/// </summary>
public closed class Shape : JsObject
{
    internal Shape(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>readonly bounds: Bounds</c>.</summary>
    public async Task<Bounds> GetBoundsAsync() => (await Realm.CallAsync(JsOp.Get(Handle, "bounds"), Bounds.Read))!;

    /// <summary><c>clone(): this</c>: declared as the base, the most-derived type at run time (R19).</summary>
    public async Task<Shape> CloneAsync(JsScope scope) => (await Realm.CallAsync(JsOp.Call(Handle, "clone"), scope.Read<Shape>))!;

    public async Task<ShapeSnapshot> SnapshotAsync() => (await Realm.CallAsync(JsOp.Snapshot(Handle), ShapeSnapshot.Read))!;

    public Task ApplyAsync(ShapeSnapshot patch) => Realm.SendAsync(JsOp.Apply(Handle, w => ShapeSnapshot.WriteMembers(w, patch)));
}
