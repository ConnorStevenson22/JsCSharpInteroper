// The golden slice's behaviour in async mode (tests/GoldenSlice/Async), as a user of `--api async` bindings sees it.
// One suite that runs in memory (GoldenSliceTests) and on every host (tests/Interoper.E2E). Compiled into both, so it
// has no test framework.
using GoldenSlice.Async;
using GoldenSlice.Async.Layers;
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
        ("extensible enum: known and other values (R10)", InScope(ExtensibleEnum)),
        ("overloads (R23)", InScope(Overloads)),
        ("Promise of an optional entity (R8, R3)", InScope(PromiseOfAnEntity)),
        ("module function over a union (R2, §5)", InScope(UnionModuleFunction)),
        ("a watch on every property, delivered then released (§5)", InScope(Watches)),
        ("base classes are live classes (R19)", InScope(Bases)),
        ("a default export: create, a redeclared clone and the most-derived type (R4, R19, R26)", InScope(DefaultExport)),
        ("module function with a rest parameter (R22)", InScope(RestParameter)),
        ("module folders are namespaces (R27)", InScope(Namespaces)),
        ("click event options: Fields send only those members (R24)", InScope(EventOptionsAndFields)),
        ("event and watch streams: delivered, then released on break (R24)", InScope(Streams)),
        ("a stream ends when its scope is disposed (R24)", InScope(StreamEndsWithItsScope)),
        ("a stream ends when it's cancelled (R24)", InScope(StreamCancelled)),
        ("click event: by default only the record's members cross the wire (R24)", InScope(DefaultProjection)),
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

    private static async Task EventOptionsAndFields(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var baseline = await Count(scope.Realm);
        var whole = new TaskCompletionSource<ClickEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var some = new TaskCompletionSource<ClickEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        // By default JS sends the record's members; the flags are harmless on an event without the methods.
        await using (await view.OnClickAsync(scope, click => whole.TrySetResult(click), new EventOptions { PreventDefault = true, StopPropagation = true }))
        await using (await view.OnClickAsync(scope, click => some.TrySetResult(click), new EventOptions { Fields = ["x", "mapPoint"] }))
        {
            Expect(await Click(scope, view), "the click to reach a listener");
            Equal(new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, await whole.Task.WaitAsync(Timeout));
            Equal(new ClickEvent { X = 1, Y = 0, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, await some.Task.WaitAsync(Timeout)); // y wasn't sent
        }
        Equal(baseline, await Count(scope.Realm));
    }

    private static async Task Streams(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var baseline = await Count(scope.Realm);

        // The stream subscribes when it's first read; the clicks go out once JS holds the subscription.
        var clicking = WhenSubscribed(scope.Realm, baseline, async () =>
        {
            for (var x = 1; x <= 3; x++)
            {
                await Click(scope, view, x);
            }
        });
        var xs = new List<double>();
        await foreach (var click in view.OnClickStreamAsync(scope, new EventOptions { Fields = ["x"] }))
        {
            xs.Add(click.X);
            if (xs.Count == 3) break;
        }
        await clicking;
        Equal("1,2,3", string.Join(",", xs));
        Equal(baseline, await Count(scope.Realm)); // break released the subscription
        Expect(!await Click(scope, view), "no listener after break");

        var zooming = WhenSubscribed(scope.Realm, baseline, async () =>
        {
            await view.SetZoomAsync(4);
            await view.SetZoomAsync(5);
        });
        var zooms = new List<double>();
        await foreach (var zoom in view.WatchZoomStreamAsync(scope))
        {
            zooms.Add(zoom);
            if (zoom == 5) break;
        }
        await zooming;
        Equal("4,5", string.Join(",", zooms));
        Equal(baseline, await Count(scope.Realm));
    }

    private static async Task StreamEndsWithItsScope(JsScope scope)
    {
        var baseline = await Count(scope.Realm);
        var inner = new JsScope(scope.Realm);
        var view = await MapView.CreateAsync(inner, new MapViewSnapshot());
        var zooms = new List<double>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Read()
        {
            await foreach (var zoom in view.WatchZoomStreamAsync(inner))
            {
                zooms.Add(zoom);
                first.TrySetResult();
            }
        }
        var reading = Read();
        await WhenSubscribed(scope.Realm, (baseline.Handles + 1, baseline.Callbacks), () => view.SetZoomAsync(2));
        await first.Task.WaitAsync(Timeout);
        await inner.DisposeAsync();
        await reading.WaitAsync(Timeout); // ends normally
        Equal("2", string.Join(",", zooms));
        Equal(baseline, await Count(scope.Realm));
    }

    private static async Task StreamCancelled(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var baseline = await Count(scope.Realm);
        using var cancel = new CancellationTokenSource();
        var zooming = WhenSubscribed(scope.Realm, baseline, () => view.SetZoomAsync(3));
        var zooms = new List<double>();
        try
        {
            await foreach (var zoom in view.WatchZoomStreamAsync(scope, cancel.Token))
            {
                zooms.Add(zoom);
                await cancel.CancelAsync();
            }
            Expect(false, "cancellation to end the stream with OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }
        await zooming;
        Equal("3", string.Join(",", zooms));
        Equal(baseline, await Count(scope.Realm)); // cancellation released the subscription
    }

    private static async Task DefaultProjection(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot());
        var callback = scope.Register(_ => null);
        var clicked = new TaskCompletionSource<ClickEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (await view.OnClickAsync(scope, click => clicked.TrySetResult(click)))
        {
            var held = await Count(scope.Realm);
            // A payload with a live object and a function, which would each come back as a new handle if sent.
            await scope.Realm.CallAsync(JsOp.Call(view.Handle, "emit", w =>
            {
                w.WriteStringValue("click");
                w.WriteStartObject();
                w.WriteString("type"u8, "click");
                w.WriteNumber("x"u8, 1);
                w.WriteNumber("y"u8, -2);
                w.WriteNumber("button"u8, 2);
                w.WritePropertyName("mapPoint"u8);
                PointSnapshot.Write(w, new PointSnapshot { X = 1, Y = 2 });
                w.WritePropertyName("marker"u8);
                JsObject.Write(w, marker);
                w.WriteStartObject("format"u8);
                w.WriteNumber("$f"u8, callback);
                w.WriteEndObject();
                w.WriteEndObject();
            }), JsJson.ReadBoolean);
            Equal(new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._2, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, await clicked.Task.WaitAsync(Timeout));
            Equal(held, await Count(scope.Realm));
        }
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

    private static async Task ExtensibleEnum(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot { Cursor = Cursor.Pointer });
        Equal<Opt<Cursor>>(Cursor.Pointer, await view.GetCursorAsync());
        await view.SetCursorAsync(new Cursor("grab")); // any CSS cursor
        Equal<Opt<Cursor>>(new Cursor("grab"), await view.GetCursorAsync());
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 0, Y = 0 }, Zoom = 1, Rotation = 0, Basemap = Basemap.Plain, Cursor = new Cursor("grab") }, await view.SnapshotAsync());
        await view.SetCursorAsync(default);
        Expect((await view.GetCursorAsync()).IsUndefined, "an unset cursor to be undefined");
    }

    private static async Task Overloads(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 1 });
        Equal(new PointSnapshot { X = 2, Y = 0 }, await view.ScreenToMapAsync(2, 4)); // one pixel is 2^-zoom map units
        Equal(new PointSnapshot { X = 2, Y = 0 }, await view.ScreenToMapAsync(new double[] { 2, 4 }));
    }

    private static async Task PromiseOfAnEntity(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        Expect((await view.MarkerAtAsync(scope, 0, 0)).IsNull, "no marker at the center");
    }

    private static async Task UnionModuleFunction(JsScope scope)
    {
        Equal(new Rgba { R = 255, G = 0, B = 0, A = 1 }, await Styles.ToRgbaAsync(scope.Realm, "#ff0000"));
        Equal(new Rgba { R = 0, G = 0, B = 255, A = 1 }, await Styles.ToRgbaAsync(scope.Realm, "blue"));
        Equal(new Rgba { R = 1, G = 2, B = 3, A = 0.5 }, await Styles.ToRgbaAsync(scope.Realm, new double[] { 1, 2, 3, 0.5 }));
        Equal(new Rgba { R = 1, G = 2, B = 3, A = 1 }, await Styles.ToRgbaAsync(scope.Realm, new Rgba { R = 1, G = 2, B = 3 }));
    }

    private static async Task Watches(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot());
        var baseline = await Count(scope.Realm);
        string[] expected = ["center 1,2", "rotation 40", "basemap Night", "cursor crosshair", "title null", "label a", "style pin-7", "position 3,4"];
        var seen = new List<string>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new TaskCompletionSource<Opt<Marker>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Saw(string what)
        {
            lock (seen)
            {
                seen.Add(what);
                if (seen.Count == expected.Length) all.TrySetResult();
            }
        }
        await using (await view.WatchCenterAsync(scope, center => Saw($"center {center.X},{center.Y}")))
        await using (await view.WatchRotationAsync(scope, rotation => Saw($"rotation {rotation}")))
        await using (await view.WatchBasemapAsync(scope, basemap => Saw($"basemap {basemap}")))
        await using (await view.WatchCursorAsync(scope, cursor => Saw($"cursor {cursor}")))
        await using (await view.WatchTitleAsync(scope, title => Saw($"title {title}")))
        await using (await view.WatchSelectedAsync(scope, value => selected.TrySetResult(value)))
        await using (await marker.WatchLabelAsync(scope, label => Saw($"label {label}")))
        await using (await marker.WatchStyleAsync(scope, style => Saw($"style {(style.TryGetValue(out IconStyle? icon) ? icon.Name : style)}")))
        await using (await marker.WatchPositionAsync(scope, position => Saw($"position {position.X},{position.Y}")))
        {
            await view.SetCenterAsync(new PointSnapshot { X = 1, Y = 2 });
            await view.SetRotationAsync(400);
            await view.SetBasemapAsync(Basemap.Night);
            await view.SetCursorAsync(Cursor.Crosshair);
            await view.SetTitleAsync(null);
            await view.SetSelectedAsync(marker);
            await marker.SetLabelAsync("a");
            await marker.SetStyleAsync(new IconStyle { Name = IconName.Pin7 });
            await marker.SetPositionAsync(new double[] { 3, 4 });
            await all.Task.WaitAsync(Timeout);
            Expect((await selected.Task.WaitAsync(Timeout)).TryGetValue(out Marker? m) && ReferenceEquals(m, marker), "the selected marker to be the marker");
        }
        Equal(baseline, await Count(scope.Realm)); // released before the scope
        await view.SetRotationAsync(10);
        await view.GetRotationAsync(); // a round trip after the change: a call back would have come first
        lock (seen)
        {
            Equal(string.Join("; ", expected.Order()), string.Join("; ", seen.Order())); // each host delivers them in order, but Server may run them out of it
        }
    }

    private static async Task Bases(JsScope scope)
    {
        Expect(await MapView.CreateAsync(scope, new MapViewSnapshot()) is Emitter and Observable, "a MapView to be an Emitter and an Observable");
        Expect(await Emitter.CreateAsync(scope, new EmitterSnapshot()) is Emitter and not MapView, "an Emitter to be just an Emitter");
        Expect((await Observable.CreateAsync(scope, new ObservableSnapshot())).GetType() == typeof(Observable), "an Observable to be just an Observable");
        Expect(await Point.CreateAsync(scope, new PointSnapshot()) is Observable, "a Point to be an Observable");
    }

    private static async Task DefaultExport(JsScope scope)
    {
        var tile = await TileLayer.CreateAsync(scope, new TileLayerSnapshot { Url = "u", Title = "Streets" });
        Equal(new TileLayerSnapshot { Url = "u", Title = "Streets", Visible = true }, await tile.SnapshotAsync());
        TileLayer copy = await tile.CloneAsync(scope); // TileLayer's clone hides Layer's
        Expect(!ReferenceEquals(copy, tile), "a clone to be another object");
        Equal(new TileLayerSnapshot { Url = "u", Title = "Streets", Visible = true }, await copy.SnapshotAsync());
        Layer layer = tile;
        Expect(await layer.CloneAsync(scope) is TileLayer, "a Layer's clone to be a TileLayer");
    }

    private static async Task RestParameter(JsScope scope)
    {
        Equal(12.0, await Measure.PerimeterAsync(scope.Realm, new PointSnapshot { X = 0, Y = 0 }, new PointSnapshot { X = 3, Y = 0 }, new PointSnapshot { X = 3, Y = 4 }));
        Equal(0.0, await Measure.PerimeterAsync(scope.Realm));
    }

    private static async Task Namespaces(JsScope scope)
    {
        Equal("GoldenSlice.Async.Layers", typeof(TileLayer).Namespace);
        var tile = await TileLayer.CreateAsync(scope, new TileLayerSnapshot());
        Expect(tile is Layer and Observable, "a TileLayer to be a Layer and an Observable");
        await tile.SetTitleAsync(null);
        await tile.SetVisibleAsync(false);
        Equal(new TileLayerSnapshot { Url = "", Title = null, Visible = false }, await tile.SnapshotAsync());
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

    private static Task<bool> Click(JsScope scope, MapView view, double x = 1) => scope.Realm.CallAsync(JsOp.Call(view.Handle, "emit", w =>
    {
        w.WriteStringValue("click");
        ClickEvent.Write(w, new ClickEvent { X = x, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } });
    }), JsJson.ReadBoolean);

    // Runs the action once a stream has subscribed: JS then holds one more handle, and .NET one more listener.
    private static async Task WhenSubscribed(JsRealm realm, (int Handles, int Callbacks) before, Func<Task> action)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (await Count(realm) == before)
        {
            Expect(DateTime.UtcNow < deadline, "the stream to subscribe");
            await Task.Delay(10);
        }
        await action();
    }
}
