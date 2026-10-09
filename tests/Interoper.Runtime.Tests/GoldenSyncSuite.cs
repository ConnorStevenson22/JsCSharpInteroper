// The golden slice's behaviour in sync mode (tests/GoldenSlice/Sync), as a user of `--api sync` bindings sees it.
// One suite that runs in memory (GoldenSliceTests) and on WebAssembly (tests/Interoper.E2E), and fails fast on Server.
// Compiled into both, so it has no test framework.
using GoldenSlice.Sync;
using static Interoper.Contract.Check;

namespace Interoper.Contract;

public static class GoldenSyncSuite
{
    public static readonly IReadOnlyList<(string Name, Func<JsRealm, Task> Run)> Cases =
    [
        ("create from a snapshot, snapshot and apply (R16–R18)", InScope(CreateSnapshotApply)),
        ("identity and the most-derived type (R4, R19)", InScope(Identity)),
        ("getter type ≠ setter type (R27)", InScope(GetterSetter)),
        ("unions and optionals read back (R2, R3, R19)", InScope(Unions)),
        ("enum round trip (R9, R25)", InScope(Enums)),
        ("Promise method (R8)", InScope(Promise)),
        ("click event and zoom watch, delivered then released (R24)", InScope(EventAndWatch)),
        ("module function", InScope(ModuleFunction)),
    ];

    private static void CreateSnapshotApply(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 3, Basemap = Basemap.Night, Title = "a" });
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 3, Rotation = 0, Basemap = Basemap.Night, Title = "a" }, view.Snapshot());
        Expect(Held(scope, view, "center") is Point, "JS to hold a Point built from the bag (R16)");

        view.Apply(new MapViewSnapshot { Zoom = 30, Title = null }); // unset members stay as they are
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 20, Rotation = 0, Basemap = Basemap.Night, Title = null }, view.Snapshot());

        var line = Line.Create(scope, new LineSnapshot { Path = new double[][] { [0, 1], [2, 3] } });
        line.Apply(new LineSnapshot { Path = new double[][] { [4, 5] } });
        Equal("4,5", Pairs(line.Path));
        Equal("4,5", Pairs(line.Snapshot().Path));
    }

    private static void Identity(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var marker = Marker.Create(scope, new MarkerSnapshot());
        Expect(view.GetSelected(scope).IsNull, "selected to start as null");
        view.SetSelected(marker);
        Expect(view.GetSelected(scope) is Marker selected && ReferenceEquals(selected, marker), "one JS object to be one C# object");

        Shape polygon = Polygon.Create(scope, new PolygonSnapshot { Vertices = new double[][] { [0, 0], [4, 0], [4, 2] } });
        var copy = polygon.Clone(scope); // declared as Shape
        Equal("polygon", Kind(copy));
        Expect(!ReferenceEquals(copy, polygon), "a clone to be another object");
        Equal(new Bounds { MinX = 0, MinY = 0, MaxX = 4, MaxY = 2 }, copy.Bounds);
        Expect(copy.Snapshot() is PolygonSnapshot { Vertices.HasValue: true }, "a Shape's snapshot to be a PolygonSnapshot");
    }

    private static void GetterSetter(JsScope scope)
    {
        var marker = Marker.Create(scope, new MarkerSnapshot { Position = new PointSnapshot { X = 1, Y = 2 } });
        Equal(new PointSnapshot { X = 1, Y = 2 }, marker.Position);
        marker.SetPosition(new double[] { 3, 4 }); // the setter makes it a Point
        Equal(new PointSnapshot { X = 3, Y = 4 }, marker.Position);
        marker.SetPosition(new PointSnapshot { X = 5, Y = 6 });
        Equal(new PointSnapshot { X = 5, Y = 6 }, marker.Position);
        Expect(Held(scope, marker, "position") is Point, "JS to hold a Point (R16)");
    }

    private static void Unions(JsScope scope)
    {
        var marker = Marker.Create(scope, new MarkerSnapshot());
        Equal<Style>(new IconStyle { Name = IconName.Pin1 }, marker.Style);

        Style fill = new FillStyle { Color = "red", Outline = new StrokeStyle { Color = new Rgba { R = 1, G = 2, B = 3 }, Width = 2 } };
        marker.Style = fill;
        Equal(fill, marker.Style);
        marker.Style = new StrokeStyle { Color = new double[] { 1, 2, 3 } };
        Expect(marker.Style.TryGetValue(out StrokeStyle? stroke) && stroke.Color.TryGetValue(out double[]? rgb) && rgb.SequenceEqual([1.0, 2, 3]), "a stroke with an array color");
        marker.Style = new IconStyle { Name = IconName.Pin120, Color = null };
        Equal<Style>(new IconStyle { Name = IconName.Pin120, Color = null }, marker.Style);

        Expect(marker.Label.IsUndefined, "an unset label to be undefined");
        marker.Label = "a";
        Equal<Opt<string>>("a", marker.Label);
        Equal("a", marker.DisplayLabel);
        marker.Label = null;
        Expect(marker.Label.IsNull, "a null label to be null");
    }

    private static void Enums(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot { Basemap = Basemap._3dRelief });
        Equal(Basemap._3dRelief, view.Basemap);
        Equal("3d-relief", scope.SyncTransport.GetString(view.Handle, "basemap"));
        foreach (var basemap in Enum.GetValues<Basemap>())
        {
            view.Basemap = basemap;
            Equal(basemap, view.Basemap);
        }
    }

    private static async Task Promise(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        await view.FlyToAsync(new PointSnapshot { X = 3, Y = 4 }); // a Shape: JS flies to the middle of its bounds
        Equal(new PointSnapshot { X = 3, Y = 4 }, view.Center);
        await view.FlyToAsync(new PolygonSnapshot { Vertices = new double[][] { [0, 0], [4, 0], [4, 2] } });
        Equal(new PointSnapshot { X = 2, Y = 1 }, view.Center);
        await view.FlyToAsync(new double[] { 5, 6 });
        Equal(new PointSnapshot { X = 5, Y = 6 }, view.Center);
        await view.FlyToAsync(new Bounds { MinX = 0, MinY = 0, MaxX = 2, MaxY = 4 });
        Equal(new PointSnapshot { X = 1, Y = 2 }, view.Center);
        await view.FlyToAsync(new CenterAndZoom { Center = [7, 8], Zoom = 9 });
        Equal((new PointSnapshot { X = 7, Y = 8 }, 9.0), (view.Center, view.Zoom));
    }

    private static void EventAndWatch(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var baseline = Count(scope.Realm);
        var (clicks, zooms) = (new List<ClickEvent>(), new List<double>());
        using (view.OnClick(scope, clicks.Add))
        using (view.WatchZoom(scope, zooms.Add))
        {
            Expect(Click(scope, view), "the click to reach a listener");
            view.Zoom = 5;
        }
        Equal(baseline, Count(scope.Realm)); // released before the scope
        Expect(!Click(scope, view), "no listener after release");
        view.Zoom = 6;

        Equal(new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, clicks.Single());
        Equal(5.0, zooms.Single());
    }

    private static void ModuleFunction(JsScope scope) =>
        Equal(5.0, Measure.Distance(scope.Realm, new PointSnapshot { X = 0, Y = 0 }, new PointSnapshot { X = 3, Y = 4 }));

    // --- Helpers ---

    // Each case has its own scope; once it is disposed, the census and the callbacks are back to their baseline.
    private static Func<JsRealm, Task> InScope(Action<JsScope> body) => InScope(scope =>
    {
        body(scope);
        return Task.CompletedTask;
    });

    private static Func<JsRealm, Task> InScope(Func<JsScope, Task> body) => async realm =>
    {
        var baseline = Count(realm);
        using (var scope = new JsScope(realm))
        {
            await body(scope);
        }
        Equal(baseline, Count(realm));
    };

    private static (int Handles, int Callbacks) Count(JsRealm realm) => (realm.SyncTransport.Census(), realm.SyncTransport.Callbacks.Count);

    private static string Kind(Shape shape) => shape switch
    {
        Point => "point",
        Line => "line",
        Polygon => "polygon",
    };

    private static string Pairs(Opt<double[][]> pairs) => pairs.TryGetValue(out double[][]? p) ? string.Join(";", p.Select(xy => string.Join(",", xy))) : $"{pairs}";

    // Probes of the JS side, outside the bindings: the live object a member holds, and the library raising a click.
    private static Shape Held(JsScope scope, JsObject target, string name) => scope.Realm.Call(JsOp.Get(target.Handle, name), scope.Read<Shape>)!;

    private static bool Click(JsScope scope, MapView view) => scope.Realm.Call(JsOp.Call(view.Handle, "emit", w =>
    {
        w.WriteStringValue("click");
        ClickEvent.Write(w, new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } });
    }), JsJson.ReadBoolean);
}
