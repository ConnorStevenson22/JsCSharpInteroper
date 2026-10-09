using Interoper;

namespace GoldenSlice.Sync;

/// <summary>The functions of <c>@acme/demo-maps/measure.js</c> (§5): data in, data out, one call.</summary>
public static class Measure
{
    private const string Module = "@acme/demo-maps/measure.js";

    /// <summary><c>distance(a: Point, b: Point): number</c>.</summary>
    public static double Distance(JsRealm realm, PointSnapshot a, PointSnapshot b) =>
        realm.Call(JsOp.Call(Module, "distance", w =>
        {
            PointSnapshot.Write(w, a);
            PointSnapshot.Write(w, b);
        }), JsJson.ReadNumber);
}
