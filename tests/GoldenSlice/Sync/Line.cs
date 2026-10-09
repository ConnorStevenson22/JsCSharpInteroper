using Interoper;

namespace GoldenSlice.Sync;

/// <summary><c>class Line extends Shape</c>: value-like (R16).</summary>
public sealed class Line : Shape
{
    internal Line(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Line(options)</c> (R18).</summary>
    public static Line Create(JsScope scope, LineSnapshot props) =>
        scope.Realm.Call(JsOp.Create(DemoMaps.LineId, w => LineSnapshot.WriteMembers(w, props)), scope.Read<Line>)!;

    /// <summary><c>path: [number, number][]</c> (R5).</summary>
    public double[][] Path
    {
        get => Realm.Call(JsOp.Get(Handle, "path"), static (ref r) => JsJson.ReadArray(ref r, static (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber)))!;
        set => Realm.Send(JsOp.Set(Handle, "path", w => JsJson.WriteArray(w, value, static (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber))));
    }

    public new LineSnapshot Snapshot() => Realm.Call(JsOp.Snapshot(Handle), LineSnapshot.Read)!;

    public void Apply(LineSnapshot patch) => Realm.Send(JsOp.Apply(Handle, w => LineSnapshot.WriteMembers(w, patch)));
}
