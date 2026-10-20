// The golden slice's behaviour in async mode (tests/GoldenSlice/Async), as a user of `--api async` bindings sees it.
// One suite that runs in memory (GoldenSliceTests) and on every host (tests/Interoper.E2E). Compiled into both, so it
// has no test framework.
using GoldenSlice.Async;
using GoldenSlice.Async.Layers;
using static Interoper.Contract.Check;
using Ops = GoldenSlice.Async.Ops;

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
        ("a collection: add, remove, at and toArray, one C# object per JS one (§2)", InScope(Collections)),
        ("a collection's change event is a CollectionChangeEvent<Marker> (R17, R24)", InScope(CollectionChange)),
        ("a collection of an abstract class reads its items as the most-derived type (R4, R19)", InScope(LayersOfAnAbstractClass)),
        ("ForEach calls back with each item and its index (R21)", InScope(ForEach)),
        ("callbacks are released with their scope (R21)", InScope(CallbacksAreReleasedWithTheirScope)),
        ("handles in a callback's arguments that nothing reads are released with its scope (R21)", InScope(UnreadArgumentsAreReleased)),
        ("Diff: arrays, unions and nested snapshots, then Apply (R18)", InScope(DiffThenApply)),
        ("a mixin keeps its class's base: a Pin is a Marker (R4, R19)", InScope(Mixin)),
        ("an exported mixin is an interface: a TileLayer is an IRefreshable (§5)", InScope(MixinInterface)),
        ("a subscription with remove() instead of dispose(), delivered then released (R24)", InScope(RemoveSubscription)),
        ("any is JsAny: data, or a handle the scope owns (§2)", InScope(Any)),
        ("a union with an entity case is a parameter's (R2, R17)", InScope(WrittenUnion)),
        ("same-named types of sibling modules (R27)", InScope(SiblingModules)),
        ("a custom element drives its live view, and its class is found by its tag (§5, R22, R28)", InScope(CustomElement)),
        ("a watch of chosen members: one call per burst, with their snapshot (R18)", InScope(MemberWatch)),
        ("chosen members read and watched through a base type, and streamed (R18)", InScope(MembersThroughABase)),
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
        Equal(new TileLayerSnapshot { Url = "u", RefreshInterval = 0, Title = "Streets", Visible = true }, await tile.SnapshotAsync());
        TileLayer copy = await tile.CloneAsync(scope); // TileLayer's clone hides Layer's
        Expect(!ReferenceEquals(copy, tile), "a clone to be another object");
        Equal(new TileLayerSnapshot { Url = "u", RefreshInterval = 0, Title = "Streets", Visible = true }, await copy.SnapshotAsync());
        Layer layer = tile;
        Expect(await layer.CloneAsync(scope) is TileLayer, "a Layer's clone to be a TileLayer");
    }

    private static async Task MixinInterface(JsScope scope)
    {
        IRefreshable refreshable = await TileLayer.CreateAsync(scope, new TileLayerSnapshot());
        await refreshable.SetRefreshIntervalAsync(30);
        await refreshable.RefreshAsync();
        Equal(30.0, await refreshable.GetRefreshIntervalAsync());
        Equal(1.0, await refreshable.GetRefreshCountAsync());
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
        Equal(new TileLayerSnapshot { Url = "", RefreshInterval = 0, Title = null, Visible = false }, await tile.SnapshotAsync());
    }

    private static async Task Collections(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var markers = await view.GetMarkersAsync(scope);
        Expect(ReferenceEquals(markers, await view.GetMarkersAsync(scope)), "one JS collection to be one C# object");
        var (a, b) = (await Marker.CreateAsync(scope, new MarkerSnapshot { Label = "a" }), await Marker.CreateAsync(scope, new MarkerSnapshot { Label = "b" }));
        await markers.AddAsync(a);
        await markers.AddAsync(b);
        Equal(2.0, await markers.GetLengthAsync());
        Expect(await markers.ToArrayAsync(scope) is [var first, var second] && ReferenceEquals(first, a) && ReferenceEquals(second, b), "toArray to give the markers, in order");
        Expect(await markers.RemoveAsync(a) && !await markers.RemoveAsync(a), "remove to find the marker once");
        Expect((await markers.AtAsync(scope, 0)).TryGetValue(out Marker? left) && ReferenceEquals(left, b), "the other marker to be at 0");
        Expect((await markers.AtAsync(scope, 1)).IsUndefined, "nothing at 1");
    }

    private static async Task CollectionChange(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var markers = await view.GetMarkersAsync(scope);
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot());
        var changes = new List<CollectionChangeEvent<Marker>>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (await markers.OnChangeAsync(scope, change =>
        {
            lock (changes)
            {
                changes.Add(change);
                if (changes.Count == 2) both.TrySetResult();
            }
        }))
        {
            await markers.AddAsync(marker);
            await markers.RemoveAsync(marker);
            await both.Task.WaitAsync(Timeout);
        }
        lock (changes) // each host delivers them in order, but Server may run them out of it
        {
            Expect(changes.Any(c => c is { Added: [var added], Removed: [] } && ReferenceEquals(added, marker)), "an add with the marker itself");
            Expect(changes.Any(c => c is { Added: [], Removed: [var removed] } && ReferenceEquals(removed, marker)), "a remove with the marker itself");
        }
    }

    private static async Task LayersOfAnAbstractClass(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var layers = await view.GetLayersAsync(scope);
        var tile = await TileLayer.CreateAsync(scope, new TileLayerSnapshot { Url = "u" });
        await layers.AddAsync(tile);
        Expect((await layers.AtAsync(scope, 0)).TryGetValue(out Layer? layer) && layer is TileLayer && ReferenceEquals(layer, tile), "the Layer to be the TileLayer");
        Expect(await layers.ToArrayAsync(scope) is [TileLayer], "toArray to give the TileLayer");
    }

    private static async Task ForEach(JsScope scope)
    {
        var markers = await (await MapView.CreateAsync(scope, new MapViewSnapshot())).GetMarkersAsync(scope);
        var (a, b) = (await Marker.CreateAsync(scope, new MarkerSnapshot()), await Marker.CreateAsync(scope, new MarkerSnapshot()));
        await markers.AddAsync(a);
        await markers.AddAsync(b);
        var seen = new List<string>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await markers.ForEachAsync(scope, (marker, index) =>
        {
            lock (seen)
            {
                seen.Add($"{index} {ReferenceEquals(marker, index == 0 ? a : b)}");
                if (seen.Count == 2) all.TrySetResult();
            }
        });
        await all.Task.WaitAsync(Timeout); // on Server, JS doesn't wait for .NET
        lock (seen)
        {
            Equal("0 True; 1 True", string.Join("; ", seen.Order()));
        }
    }

    private static async Task CallbacksAreReleasedWithTheirScope(JsScope scope)
    {
        var markers = await (await MapView.CreateAsync(scope, new MapViewSnapshot())).GetMarkersAsync(scope);
        await using (var setup = new JsScope(scope.Realm))
        {
            await markers.AddAsync(await Marker.CreateAsync(setup, new MarkerSnapshot())); // no .NET scope owns the marker any more
        }
        var baseline = await Count(scope.Realm);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var inner = new JsScope(scope.Realm))
        {
            await markers.ForEachAsync(inner, (_, _) => called.TrySetResult());
            await called.Task.WaitAsync(Timeout);
            // The inner scope owns the callback, and the marker it got (forEach also passes the collection, already owned).
            Equal((baseline.Handles + 1, baseline.Callbacks + 1), await Count(scope.Realm));
        }
        Equal(baseline, await Count(scope.Realm));
    }

    private static async Task UnreadArgumentsAreReleased(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        await using (var setup = new JsScope(scope.Realm))
        {
            await view.SetSelectedAsync(await Marker.CreateAsync(setup, new MarkerSnapshot())); // no .NET scope owns it any more
        }
        var baseline = await Count(scope.Realm);
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var inner = new JsScope(scope.Realm))
        {
            await using (await view.WatchSelectedAsync(inner, selected => changed.TrySetResult(selected.IsNull)))
            {
                await view.SetSelectedAsync(null); // JS passes (value, old): nothing reads the old marker
                Expect(await changed.Task.WaitAsync(Timeout), "the watch to see null");
            }
        }
        Equal(baseline, await Count(scope.Realm));
    }

    private static async Task DiffThenApply(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 3, Title = "a" });
        var before = await view.SnapshotAsync();
        // Same zoom, a new center, null for the title, and the rotation unset: a patch can't unset a member.
        var after = before with { Center = new PointSnapshot { X = 1, Y = 5 }, Title = null, Rotation = default };
        var patch = MapViewSnapshot.Diff(before, after);
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 5 }, Title = null }, patch);
        await view.ApplyAsync(patch);
        Equal(after with { Rotation = before.Rotation }, await view.SnapshotAsync());

        var line = await Line.CreateAsync(scope, new LineSnapshot { Path = new double[][] { [0, 1], [2, 3] } });
        var path = await line.SnapshotAsync();
        Equal(new LineSnapshot(), LineSnapshot.Diff(path, new LineSnapshot { Path = new double[][] { [0, 1], [2, 3] } })); // arrays element-wise
        await line.ApplyAsync(LineSnapshot.Diff(path, new LineSnapshot { Path = new double[][] { [0, 1], [4, 5] } }));
        Equal("0,1;4,5", Pairs((await line.SnapshotAsync()).Path));

        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot { Label = "m", Style = new FillStyle { Color = "red" } });
        var style = await marker.SnapshotAsync();
        Equal(new MarkerSnapshot(), MarkerSnapshot.Diff(style, style with { Style = new FillStyle { Color = "red" } }));
        var stroke = MarkerSnapshot.Diff(style, style with { Label = "m", Style = new StrokeStyle { Color = "blue", Width = 2 } });
        Equal(new MarkerSnapshot { Style = new StrokeStyle { Color = "blue", Width = 2 } }, stroke);
        await marker.ApplyAsync(stroke);
        Equal(style with { Style = new StrokeStyle { Color = "blue", Width = 2 } }, await marker.SnapshotAsync());
    }

    private static async Task Mixin(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot());
        var pin = await Pin.CreateAsync(scope, new PinSnapshot { Position = new PointSnapshot { X = 1, Y = 2 }, Label = "p" });
        await view.SetSelectedAsync(pin); // where a Marker is expected
        Expect((await view.GetSelectedAsync(scope)).TryGetValue(out Marker? selected) && selected is Pin && ReferenceEquals(selected, pin),
            "the selected marker to be the Pin");
        Equal("p", await pin.GetDisplayLabelAsync());
        await pin.DragByAsync(2, 3);
        Equal(new PointSnapshot { X = 3, Y = 5 }, await pin.GetPositionAsync());
    }

    private static async Task RemoveSubscription(JsScope scope)
    {
        var pin = await Pin.CreateAsync(scope, new PinSnapshot());
        var baseline = await Count(scope.Realm);
        var dragged = new TaskCompletionSource<PinDragEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using (await pin.OnDragAsync(scope, drag => { calls++; dragged.TrySetResult(drag); })) // its `on` returns a subscription with remove()
        {
            await pin.DragByAsync(1, 2);
            Equal(new PinDragEvent { Dx = 1, Dy = 2 }, await dragged.Task.WaitAsync(Timeout));
        }
        Equal(baseline, await Count(scope.Realm)); // released before the scope
        await pin.DragByAsync(3, 4);
        await pin.GetDisplayLabelAsync(); // a round trip after the drag: a call back would have come first
        Equal(1, calls);
    }

    private static async Task Any(JsScope scope)
    {
        var pin = await Pin.CreateAsync(scope, new PinSnapshot());
        Expect((await pin.GetMetaAsync(scope)).IsUndefined, "meta to start undefined");
        await pin.SetMetaAsync(JsAny.Parse("""{"id":7,"tags":["a"]}"""));
        Expect((await pin.GetMetaAsync(scope)).TryGetValue(out JsAny? meta) && meta.Json.GetProperty("id").GetDouble() == 7, "meta to read back as data");
        var marker = await Marker.CreateAsync(scope, new MarkerSnapshot());
        await pin.SetMetaAsync(JsAny.From(marker, JsObject.Write)); // a live object, as its handle
        Expect((await pin.GetMetaAsync(scope)).TryGetValue(out JsAny? held) && ReferenceEquals(held.Read(scope.Read<Marker>), marker),
            "the handle to be the same marker");
        await pin.SetMetaAsync(null);
        Expect((await pin.GetMetaAsync(scope)).IsNull, "meta to be null");
    }

    private static async Task WrittenUnion(JsScope scope)
    {
        Equal("point", await Measure.DescribeAsync(scope.Realm, new PointSnapshot()));
        Equal("polygon", await Measure.DescribeAsync(scope.Realm, new PolygonSnapshot { Vertices = new double[][] { [0, 0], [1, 0], [0, 1] } }));
        Equal("marker m", await Measure.DescribeAsync(scope.Realm, await Marker.CreateAsync(scope, new MarkerSnapshot { Label = "m" })));
        Equal("marker p", await Measure.DescribeAsync(scope.Realm, await Pin.CreateAsync(scope, new PinSnapshot { Label = "p" }))); // a subclass of the case
    }

    private static async Task CustomElement(JsScope scope)
    {
        // A custom element's constructor takes no bag. In the browser its class id comes from its tag, since its
        // `className` is its CSS class.
        var element = await DemoMapElement.CreateAsync(scope, new());
        var view = await element.GetViewAsync(scope);
        Expect(ReferenceEquals(view, await element.GetViewAsync(scope)), "one live view");
        await element.SetZoomAsync(3);
        Equal(3.0, await view.GetZoomAsync());

        var clicks = new List<ClickEvent>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (await view.OnClickAsync(scope, click => { clicks.Add(click); if (clicks.Count == 2) both.TrySetResult(); }))
        {
            await element.ClickAtAsync(8, 0); // the button is optional
            await element.ClickAtAsync(0, 0, DemoMapElementClickAtButton._2);
            await both.Task.WaitAsync(Timeout);
        }
        Equal("1 _0, 0 _2", string.Join(", ", clicks.Select(c => $"{c.MapPoint.X} {c.Button}")));
    }

    private static async Task SiblingModules(JsScope scope)
    {
        Equal(6.0, await Ops.A.ExecuteAsync(scope.Realm, 3, new Ops.Options { Scale = 2 }));
        Equal(5.0, await Ops.B.ExecuteAsync(scope.Realm, 3, new Ops.BOptions { Offset = 2 }));
        Equal(3.0, await Ops.B.ExecuteAsync(scope.Realm, 3));
    }

    private static async Task MemberWatch(JsScope scope)
    {
        var view = await MapView.CreateAsync(scope, new MapViewSnapshot { Zoom = 5, Rotation = 90 });
        var baseline = await Count(scope.Realm);
        var seen = new List<MapViewSnapshot>();
        var (first, second) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await using (await view.WatchAsync(scope, MapViewSnapshot.Members.Zoom | MapViewSnapshot.Members.Rotation, changed =>
        {
            lock (seen)
            {
                seen.Add(changed);
                (seen.Count == 1 ? first : second).TrySetResult();
            }
        }))
        {
            await using (scope.BeginBatch()) // one op: one JS turn
            {
                await view.SetZoomAsync(2);
                await view.SetZoomAsync(3);
                await view.SetRotationAsync(10);
                await view.SetTitleAsync("not watched");
            }
            await first.Task.WaitAsync(Timeout);
            await view.ResetAsync(); // the library's own burst
            await second.Task.WaitAsync(Timeout);
            await view.SetTitleAsync("still not watched");
        }
        Equal(baseline, await Count(scope.Realm)); // every listener released
        await view.SetZoomAsync(7);
        await view.GetZoomAsync(); // a round trip after the change: a call back would have come first
        Equal("3 10, 1 0", string.Join(", ", seen.Select(s => $"{s.Zoom} {s.Rotation}")));
        Equal(new MapViewSnapshot { Zoom = 1, Rotation = 0 }, seen[1]); // only the watched members
    }

    private static async Task MembersThroughABase(JsScope scope)
    {
        Layer layer = await TileLayer.CreateAsync(scope, new TileLayerSnapshot { Url = "u", Title = "t" });
        Equal(new LayerSnapshot { Visible = true }, await layer.SnapshotAsync(LayerSnapshot.Members.Visible));
        Equal(new TileLayerSnapshot { Url = "u", Title = "t" }, await ((TileLayer)layer).SnapshotAsync(TileLayerSnapshot.Members.Url | TileLayerSnapshot.Members.Title));

        var before = new LayerSnapshot { Title = "t", Visible = true };
        var changed = new TaskCompletionSource<LayerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (await layer.WatchAsync(scope, LayerSnapshot.Members.Visible | LayerSnapshot.Members.Title, s => changed.TrySetResult(s)))
        {
            await layer.SetVisibleAsync(false);
            var after = await changed.Task.WaitAsync(Timeout);
            Equal(new LayerSnapshot { Title = "t", Visible = false }, after);
            Equal(new LayerSnapshot { Visible = false }, LayerSnapshot.Diff(before, after)); // the patch
        }

        var showing = WhenSubscribed(scope.Realm, await Count(scope.Realm), () => layer.ApplyAsync(new LayerSnapshot { Title = "v", Visible = true }));
        await foreach (var shown in layer.WatchStreamAsync(scope, LayerSnapshot.Members.Visible))
        {
            Equal(new LayerSnapshot { Visible = true }, shown);
            break;
        }
        await showing;
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
