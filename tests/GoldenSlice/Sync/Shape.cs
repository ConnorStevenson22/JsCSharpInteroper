using Interoper;

namespace GoldenSlice.Sync;

/// <summary>
/// <c>abstract class Shape</c>: value-like (R16), and a closed base, so a switch over its subclasses is exhaustive (R4).
/// </summary>
public closed class Shape : JsObject
{
    internal Shape(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>readonly bounds: Bounds</c>.</summary>
    public Bounds Bounds => Realm.Call(JsOp.Get(Handle, "bounds"), Bounds.Read)!;

    /// <summary><c>clone(): this</c>: declared as the base, the most-derived type at run time (R19).</summary>
    public Shape Clone(JsScope scope) => Realm.Call(JsOp.Call(Handle, "clone"), scope.Read<Shape>)!;

    public ShapeSnapshot Snapshot() => Realm.Call(JsOp.Snapshot(Handle), ShapeSnapshot.Read)!;

    public void Apply(ShapeSnapshot patch) => Realm.Send(JsOp.Apply(Handle, w => ShapeSnapshot.WriteMembers(w, patch)));
}
