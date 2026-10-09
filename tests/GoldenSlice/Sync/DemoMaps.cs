using Interoper;

namespace GoldenSlice.Sync;

/// <summary>
/// The class ids of demo-maps.meta.js, and the live type each one is (R19 step 1). Shape is abstract, so JS never
/// reports it. Sync mode loads every module it uses up front (design §3.5).
/// </summary>
public static class DemoMaps
{
    public const int ShapeId = 1, PointId = 2, LineId = 3, PolygonId = 4, MapViewId = 5, MarkerId = 6;

    public static JsClassRegistry Classes { get; } = new JsClassRegistry()
        .Add(PointId, static (realm, handle) => new Point(realm, handle))
        .Add(LineId, static (realm, handle) => new Line(realm, handle))
        .Add(PolygonId, static (realm, handle) => new Polygon(realm, handle))
        .Add(MapViewId, static (realm, handle) => new MapView(realm, handle))
        .Add(MarkerId, static (realm, handle) => new Marker(realm, handle))
        .Preload("@acme/demo-maps/geometry.js", "@acme/demo-maps/MapView.js", "@acme/demo-maps/Marker.js", "@acme/demo-maps/measure.js");
}
