// The golden slice's behaviour in async mode (tests/GoldenSlice/Async), as a user of `--api async` bindings sees it.
// One suite that runs in memory (GoldenSliceTests) and on every host (tests/Interoper.E2E). Compiled into both, so it
// has no test framework.
using GoldenSlice.Async;
using static Interoper.Contract.Check;

namespace Interoper.Contract;

public static class GoldenAsyncSuite
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
        ("batching: N writes are one round trip", InScope(Batching)),
    ];

    private static async Task CreateSnapshotApply(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 3, Basemap = Basemap.Night, Title = "a" });
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 3, Rotation = 0, Basemap = Basemap.Night, Title = "a" }, await view.SnapshotAsync());
        Expect(await Held(scope, view, "center") is Point, "JS to hold a Point built from the bag (R16)");

        await view.ApplyAsync(new MapViewSnapshot { Zoom = 30, Title = null }); // unset members stay as they are
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 20, Rotation = 0, Basemap = Basemap.Night, Title = null }, await view.SnapshotAsync());

        var line = await Line.CreateAsync(scope, new LineSnapshot { Path = new double[][] { [0, 1], [2, 3] } });
        await line.ApplyAsync(new LineSnapshot { Path = new double[][] { [4, 5] } });
        Equal("4,5", Pairs(await line.GetPathAsync()));
        Equal("4,5", Pairs((await line.SnapshotAsync()).Path));
    }

    private static async Task Identity(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot());
        Expect((await view.GetSelectedAsync(scope)).IsNull, "selected to start as null");
        await view.SetSelectedAsync(marker);
        Expect(await view.GetSelectedAsync(scope) is Marker selected && ReferenceEquals(selected, marker), "one JS object to be one C# object");

        Shape polygon = await Polygon.CreateAsync(scope, new PolygonSnapshot { Vertices = new double[][] { [0, 0], [4, 0], [4, 2] } });
        var copy = await polygon.CloneAsync(scope); // declared as Shape
        Equal("polygon", Kind(copy));
        Expect(!ReferenceEquals(copy, polygon), "a clone to be another object");
        Equal(new Bounds { MinX = 0, MinY = 0, MaxX = 4, MaxY = 2 }, await copy.GetBoundsAsync());
        Expect(await copy.SnapshotAsync() is PolygonSnapshot { Vertices.HasValue: true }, "a Shape's snapshot to be a PolygonSnapshot");
    }

    private static async Task GetterSetter(JsScope scope)
    {
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot { Position = new PointSnapshot { X = 1, Y = 2 } });
        Equal(new PointSnapshot { X = 1, Y = 2 }, await marker.GetPositionAsync());
        await marker.SetPositionAsync(new double[] { 3, 4 }); // the setter makes it a Point
        Equal(new PointSnapshot { X = 3, Y = 4 }, await marker.GetPositionAsync());
        await marker.SetPositionAsync(new PointSnapshot { X = 5, Y = 6 });
        Equal(new PointSnapshot { X = 5, Y = 6 }, await marker.GetPositionAsync());
        Expect(await Held(scope, marker, "position") is Point, "JS to hold a Point (R16)");
    }

    private static async Task Unions(JsScope scope)
    {
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot());
        Equal<Style>(new IconStyle { Name = IconName.Pin1 }, await marker.GetStyleAsync());

        Style fill = new FillStyle { Color = "red", Outline = new StrokeStyle { Color = new Rgba { R = 1, G = 2, B = 3 }, Width = 2 } };
        await marker.SetStyleAsync(fill);
        Equal(fill, await marker.GetStyleAsync());
        await marker.SetStyleAsync(new StrokeStyle { Color = new double[] { 1, 2, 3 } });
        Expect((await marker.GetStyleAsync()).TryGetValue(out StrokeStyle? stroke) && stroke.Color.TryGetValue(out double[]? rgb) && rgb.SequenceEqual([1.0, 2, 3]), "a stroke with an array color");
        await marker.SetStyleAsync(new IconStyle { Name = IconName.Pin120, Color = null });
        Equal<Style>(new IconStyle { Name = IconName.Pin120, Color = null }, await marker.GetStyleAsync());

        Expect((await marker.GetLabelAsync()).IsUndefined, "an unset label to be undefined");
        await marker.SetLabelAsync("a");
        Equal<Opt<string>>("a", await marker.GetLabelAsync());
        Equal("a", await marker.GetDisplayLabelAsync());
        await marker.SetLabelAsync(null);
        Expect((await marker.GetLabelAsync()).IsNull, "a null label to be null");
    }

    private static async Task Enums(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot { Basemap = Basemap._3dRelief });
        Equal(Basemap._3dRelief, await view.GetBasemapAsync());
        Equal("3d-relief", await scope.Realm.GetStringAsync(view.Handle, "basemap"));
        foreach (var basemap in Enum.GetValues<Basemap>())
        {
            await view.SetBasemapAsync(basemap);
            Equal(basemap, await view.GetBasemapAsync());
        }
    }

    private static async Task Promise(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        await view.FlyToAsync(new PointSnapshot { X = 3, Y = 4 }); // a Shape: JS flies to the middle of its bounds
        Equal(new PointSnapshot { X = 3, Y = 4 }, await view.GetCenterAsync());
        await view.FlyToAsync(new PolygonSnapshot { Vertices = new double[][] { [0, 0], [4, 0], [4, 2] } });
        Equal(new PointSnapshot { X = 2, Y = 1 }, await view.GetCenterAsync());
        await view.FlyToAsync(new double[] { 5, 6 });
        Equal(new PointSnapshot { X = 5, Y = 6 }, await view.GetCenterAsync());
        await view.FlyToAsync(new Bounds { MinX = 0, MinY = 0, MaxX = 2, MaxY = 4 });
        Equal(new PointSnapshot { X = 1, Y = 2 }, await view.GetCenterAsync());
        await view.FlyToAsync(new CenterAndZoom { Center = [7, 8], Zoom = 9 });
        Equal((new PointSnapshot { X = 7, Y = 8 }, 9.0), (await view.GetCenterAsync(), await view.GetZoomAsync()));
    }

    private static async Task EventAndWatch(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var baseline = await Count(scope.Realm);
        var clicked = new TaskCompletionSource<ClickEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var zoomed = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using (await view.OnClickAsync(scope, click => clicked.TrySetResult(click)))
        await using (await view.WatchZoomAsync(scope, zoom => { calls++; zoomed.TrySetResult(zoom); }))
        {
            Expect(await Click(scope, view), "the click to reach a listener");
            await view.SetZoomAsync(5);
            Equal(new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, await clicked.Task.WaitAsync(Timeout));
            Equal(5.0, await zoomed.Task.WaitAsync(Timeout));
        }
        Equal(baseline, await Count(scope.Realm)); // released before the scope
        Expect(!await Click(scope, view), "no listener after release");
        await view.SetZoomAsync(6);
        await view.GetZoomAsync(); // a round trip after the change: a call back would have come first
        Equal(1, calls);
    }

    private static async Task ModuleFunction(JsScope scope) =>
        Equal(5.0, await Measure.DistanceAsync(scope.Realm, new PointSnapshot { X = 0, Y = 0 }, new PointSnapshot { X = 3, Y = 4 }));

    private static async Task Batching(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var start = scope.Realm.RoundTrips;
        await using (scope.BeginBatch())
        {
            await view.SetZoomAsync(4);
            await view.SetRotationAsync(370);
            await view.SetBasemapAsync(Basemap.Relief);
            await view.SetTitleAsync("batched");
            await view.SetCenterAsync(new PointSnapshot { X = 1, Y = 1 });
            await view.ApplyAsync(new MapViewSnapshot { Zoom = 5 });
            Equal(start, scope.Realm.RoundTrips); // all queued
        }
        Equal(start + 1, scope.Realm.RoundTrips);
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 1 }, Zoom = 5, Rotation = 10, Basemap = Basemap.Relief, Title = "batched" }, await view.SnapshotAsync());
    }

    // --- Helpers ---

    // Each case has its own scope; once it is disposed, the census and the callbacks are back to their baseline.
    private static Func<JsRealm, Task> InScope(Func<JsScope, Task> body) => async realm =>
    {
        var baseline = await Count(realm);
        await using (var scope = new JsScope(realm))
        {
            await body(scope);
        }
        Equal(baseline, await Count(realm));
    };

    private static async Task<(int Handles, int Callbacks)> Count(JsRealm realm) =>
        (await realm.Transport.CensusAsync(), realm.Transport.Callbacks.Count);

    private static string Kind(Shape shape) => shape switch
    {
        Point => "point",
        Line => "line",
        Polygon => "polygon",
    };

    private static string Pairs(Opt<double[][]> pairs) => pairs.TryGetValue(out double[][]? p) ? string.Join(";", p.Select(xy => string.Join(",", xy))) : $"{pairs}";

    // Probes of the JS side, outside the bindings: the live object a member holds, and the library raising a click.
    private static async Task<Shape> Held(JsScope scope, JsObject target, string name) =>
        (await scope.Realm.CallAsync(JsOp.Get(target.Handle, name), scope.Read<Shape>))!;

    private static Task<bool> Click(JsScope scope, MapView view) => scope.Realm.CallAsync(JsOp.Call(view.Handle, "emit", w =>
    {
        w.WriteStringValue("click");
        ClickEvent.Write(w, new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } });
    }), JsJson.ReadBoolean);
}
