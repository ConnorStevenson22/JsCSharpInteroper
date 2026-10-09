using Interoper;

namespace GoldenSlice.Async;

/// <summary><c>class Line extends Shape</c>: value-like (R16).</summary>
public sealed class Line : Shape
{
    internal Line(JsRealm realm, int handle) : base(realm, handle) { }

    /// <summary><c>new Line(options)</c> (R18).</summary>
    public static async Task<Line> CreateAsync(JsScope scope, LineSnapshot props) =>
        (await scope.Realm.CallAsync(JsOp.Create(DemoMaps.LineId, w => LineSnapshot.WriteMembers(w, props)), scope.Read<Line>))!;

    /// <summary><c>path: [number, number][]</c> (R5).</summary>
    public async Task<double[][]> GetPathAsync() =>
        (await Realm.CallAsync(JsOp.Get(Handle, "path"), static (ref r) => JsJson.ReadArray(ref r, static (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber))))!;

    public Task SetPathAsync(double[][] value) =>
        Realm.SendAsync(JsOp.Set(Handle, "path", w => JsJson.WriteArray(w, value, static (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber))));

    public new async Task<LineSnapshot> SnapshotAsync() => (await Realm.CallAsync(JsOp.Snapshot(Handle), LineSnapshot.Read))!;

    public Task ApplyAsync(LineSnapshot patch) => Realm.SendAsync(JsOp.Apply(Handle, w => LineSnapshot.WriteMembers(w, patch)));
}
