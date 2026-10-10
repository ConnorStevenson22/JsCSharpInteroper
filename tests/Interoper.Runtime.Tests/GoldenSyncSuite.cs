// The golden slice's behaviour in sync mode (tests/GoldenSlice/Sync), as a user of `--api sync` bindings sees it.
// One suite that runs in memory (GoldenSliceTests) and on WebAssembly (tests/Interoper.E2E), and fails fast on Server.
// Compiled into both, so it has no test framework.
using GoldenSlice.Sync;
using GoldenSlice.Sync.Layers;
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
        ("a collection: add, remove, at and toArray, one C# object per JS one (§2)", InScope(Collections)),
        ("a collection's change event is a CollectionChangeEvent<Marker> (R17, R24)", InScope(CollectionChange)),
        ("a collection of an abstract class reads its items as the most-derived type (R4, R19)", InScope(LayersOfAnAbstractClass)),
        ("Find with a predicate: JS uses its result (R21)", InScope(Find)),
        ("ForEach calls back with each item and its index (R21)", InScope(ForEach)),
        ("a callback property is setter-only, and JS uses its result (R21)", InScope(LabelFormatter)),
        ("callbacks are released with their scope (R21)", InScope(CallbacksAreReleasedWithTheirScope)),
        ("handles in a callback's arguments that nothing reads are released with its scope (R21)", InScope(UnreadArgumentsAreReleased)),
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
        Equal("3d-relief", scope.Realm.GetString(view.Handle, "basemap"));
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

    private static async Task EventAndWatch(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var baseline = await Count(scope.Realm);
        var (clicks, zooms) = (new List<ClickEvent>(), new List<double>());
        using (view.OnClick(scope, clicks.Add))
        using (view.WatchZoom(scope, zooms.Add))
        {
            Expect(Click(scope, view), "the click to reach a listener");
            view.Zoom = 5;
        }
        Equal(baseline, await Count(scope.Realm)); // released before the scope
        Expect(!Click(scope, view), "no listener after release");
        view.Zoom = 6;

        Equal(new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, clicks.Single());
        Equal(5.0, zooms.Single());
    }

    private static async Task EventOptionsAndFields(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var baseline = await Count(scope.Realm);
        var (whole, some) = (new List<ClickEvent>(), new List<ClickEvent>());
        // By default JS sends the record's members; the flags are harmless on an event without the methods.
        using (view.OnClick(scope, whole.Add, new EventOptions { PreventDefault = true, StopPropagation = true }))
        using (view.OnClick(scope, some.Add, new EventOptions { Fields = ["x", "mapPoint"] }))
        {
            Expect(Click(scope, view), "the click to reach a listener");
        }
        Equal(baseline, await Count(scope.Realm));
        Equal(new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, whole.Single());
        Equal(new ClickEvent { X = 1, Y = 0, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } }, some.Single()); // y wasn't sent
    }

    private static void ModuleFunction(JsScope scope) =>
        Equal(5.0, Measure.Distance(scope.Realm, new PointSnapshot { X = 0, Y = 0 }, new PointSnapshot { X = 3, Y = 4 }));

    private static void ExtensibleEnum(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot { Cursor = Cursor.Pointer });
        Equal<Opt<Cursor>>(Cursor.Pointer, view.Cursor);
        view.Cursor = new Cursor("grab"); // any CSS cursor
        Equal<Opt<Cursor>>(new Cursor("grab"), view.Cursor);
        Equal(new MapViewSnapshot { Center = new PointSnapshot { X = 0, Y = 0 }, Zoom = 1, Rotation = 0, Basemap = Basemap.Plain, Cursor = new Cursor("grab") }, view.Snapshot());
        view.Cursor = default;
        Expect(view.Cursor.IsUndefined, "an unset cursor to be undefined");
    }

    private static void Overloads(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 1 });
        Equal(new PointSnapshot { X = 2, Y = 0 }, view.ScreenToMap(2, 4)); // one pixel is 2^-zoom map units
        Equal(new PointSnapshot { X = 2, Y = 0 }, view.ScreenToMap(new double[] { 2, 4 }));
    }

    private static async Task PromiseOfAnEntity(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        Expect((await view.MarkerAtAsync(scope, 0, 0)).IsNull, "no marker at the center");
    }

    private static void UnionModuleFunction(JsScope scope)
    {
        Equal(new Rgba { R = 255, G = 0, B = 0, A = 1 }, Styles.ToRgba(scope.Realm, "#ff0000"));
        Equal(new Rgba { R = 0, G = 0, B = 255, A = 1 }, Styles.ToRgba(scope.Realm, "blue"));
        Equal(new Rgba { R = 1, G = 2, B = 3, A = 0.5 }, Styles.ToRgba(scope.Realm, new double[] { 1, 2, 3, 0.5 }));
        Equal(new Rgba { R = 1, G = 2, B = 3, A = 1 }, Styles.ToRgba(scope.Realm, new Rgba { R = 1, G = 2, B = 3 }));
    }

    private static async Task Watches(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var marker = Marker.Create(scope, new MarkerSnapshot());
        var baseline = await Count(scope.Realm);
        var seen = new List<string>();
        Opt<Marker> selected = default;
        using (view.WatchCenter(scope, center => seen.Add($"center {center.X},{center.Y}")))
        using (view.WatchRotation(scope, rotation => seen.Add($"rotation {rotation}")))
        using (view.WatchBasemap(scope, basemap => seen.Add($"basemap {basemap}")))
        using (view.WatchCursor(scope, cursor => seen.Add($"cursor {cursor}")))
        using (view.WatchTitle(scope, title => seen.Add($"title {title}")))
        using (view.WatchSelected(scope, value => selected = value))
        using (marker.WatchLabel(scope, label => seen.Add($"label {label}")))
        using (marker.WatchStyle(scope, style => seen.Add($"style {(style.TryGetValue(out IconStyle? icon) ? icon.Name : style)}")))
        using (marker.WatchPosition(scope, position => seen.Add($"position {position.X},{position.Y}")))
        {
            view.Center = new PointSnapshot { X = 1, Y = 2 };
            view.Rotation = 400;
            view.Basemap = Basemap.Night;
            view.Cursor = Cursor.Crosshair;
            view.Title = null;
            view.SetSelected(marker);
            marker.Label = "a";
            marker.Style = new IconStyle { Name = IconName.Pin7 };
            marker.SetPosition(new double[] { 3, 4 });
        }
        Equal(baseline, await Count(scope.Realm)); // released before the scope
        view.Rotation = 10;
        Equal("center 1,2; rotation 40; basemap Night; cursor crosshair; title null; label a; style pin-7; position 3,4", string.Join("; ", seen));
        Expect(selected.TryGetValue(out Marker? m) && ReferenceEquals(m, marker), "the selected marker to be the marker");
    }

    private static void Bases(JsScope scope)
    {
        Expect(MapView.Create(scope, new MapViewSnapshot()) is Emitter and Observable, "a MapView to be an Emitter and an Observable");
        Expect(Emitter.Create(scope, new EmitterSnapshot()) is Emitter and not MapView, "an Emitter to be just an Emitter");
        Expect(Observable.Create(scope, new ObservableSnapshot()).GetType() == typeof(Observable), "an Observable to be just an Observable");
        Expect(Point.Create(scope, new PointSnapshot()) is Observable, "a Point to be an Observable");
    }

    private static void DefaultExport(JsScope scope)
    {
        var tile = TileLayer.Create(scope, new TileLayerSnapshot { Url = "u", Title = "Streets" });
        Equal(new TileLayerSnapshot { Url = "u", Title = "Streets", Visible = true }, tile.Snapshot());
        TileLayer copy = tile.Clone(scope); // TileLayer's clone hides Layer's
        Expect(!ReferenceEquals(copy, tile), "a clone to be another object");
        Equal(new TileLayerSnapshot { Url = "u", Title = "Streets", Visible = true }, copy.Snapshot());
        Layer layer = tile;
        Expect(layer.Clone(scope) is TileLayer { Url: "u" }, "a Layer's clone to be a TileLayer");
    }

    private static void RestParameter(JsScope scope)
    {
        Equal(12.0, Measure.Perimeter(scope.Realm, new PointSnapshot { X = 0, Y = 0 }, new PointSnapshot { X = 3, Y = 0 }, new PointSnapshot { X = 3, Y = 4 }));
        Equal(0.0, Measure.Perimeter(scope.Realm));
    }

    private static void Namespaces(JsScope scope)
    {
        Equal("GoldenSlice.Sync.Layers", typeof(TileLayer).Namespace);
        var tile = TileLayer.Create(scope, new TileLayerSnapshot());
        Expect(tile is Layer and Observable, "a TileLayer to be a Layer and an Observable");
        tile.Title = null;
        tile.Visible = false;
        Equal(new TileLayerSnapshot { Url = "", Title = null, Visible = false }, tile.Snapshot());
    }

    private static void Collections(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var markers = view.GetMarkers(scope);
        Expect(ReferenceEquals(markers, view.GetMarkers(scope)), "one JS collection to be one C# object");
        var (a, b) = (Marker.Create(scope, new MarkerSnapshot { Label = "a" }), Marker.Create(scope, new MarkerSnapshot { Label = "b" }));
        markers.Add(a);
        markers.Add(b);
        Equal(2.0, markers.Length);
        Expect(markers.ToArray(scope) is [var first, var second] && ReferenceEquals(first, a) && ReferenceEquals(second, b), "toArray to give the markers, in order");
        Expect(markers.Remove(a) && !markers.Remove(a), "remove to find the marker once");
        Expect(markers.At(scope, 0).TryGetValue(out Marker? left) && ReferenceEquals(left, b), "the other marker to be at 0");
        Expect(markers.At(scope, 1).IsUndefined, "nothing at 1");
    }

    private static void CollectionChange(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var markers = view.GetMarkers(scope);
        var marker = Marker.Create(scope, new MarkerSnapshot());
        var changes = new List<CollectionChangeEvent<Marker>>();
        using (markers.OnChange(scope, changes.Add))
        {
            markers.Add(marker);
            markers.Remove(marker);
        }
        markers.Add(marker); // released: no event
        Expect(changes is [{ Added: [var added], Removed: [] }, { Added: [], Removed: [var removed] }] && ReferenceEquals(added, marker) && ReferenceEquals(removed, marker),
            "an add and a remove, each with the marker itself");
    }

    private static void LayersOfAnAbstractClass(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        var layers = view.GetLayers(scope);
        var tile = TileLayer.Create(scope, new TileLayerSnapshot { Url = "u" });
        layers.Add(tile);
        Expect(layers.At(scope, 0).TryGetValue(out Layer? layer) && layer is TileLayer { Url: "u" } && ReferenceEquals(layer, tile), "the Layer to be the TileLayer");
        Expect(layers.ToArray(scope) is [TileLayer], "toArray to give the TileLayer");
    }

    private static void Find(JsScope scope)
    {
        var markers = MapView.Create(scope, new MapViewSnapshot()).GetMarkers(scope);
        var (a, b) = (Marker.Create(scope, new MarkerSnapshot { Label = "a" }), Marker.Create(scope, new MarkerSnapshot { Label = "b" }));
        markers.Add(a);
        markers.Add(b);
        var indexes = new List<double>();
        var found = markers.Find(scope, (marker, index) =>
        {
            indexes.Add(index);
            return marker.DisplayLabel == "b"; // a call back into JS, from JS
        });
        Expect(found.TryGetValue(out Marker? m) && ReferenceEquals(m, b), "find to return the marker the predicate accepts");
        Equal("0,1", string.Join(",", indexes));
        Expect(markers.Find(scope, (_, _) => false).IsUndefined, "nothing when the predicate accepts none");
    }

    private static void ForEach(JsScope scope)
    {
        var markers = MapView.Create(scope, new MapViewSnapshot()).GetMarkers(scope);
        var (a, b) = (Marker.Create(scope, new MarkerSnapshot { Label = "a" }), Marker.Create(scope, new MarkerSnapshot { Label = "b" }));
        markers.Add(a);
        markers.Add(b);
        var seen = new List<string>();
        markers.ForEach(scope, (marker, index) => seen.Add($"{index} {marker.Label} {ReferenceEquals(marker, index == 0 ? a : b)}"));
        Equal("0 a True; 1 b True", string.Join("; ", seen));
    }

    private static void LabelFormatter(JsScope scope)
    {
        var marker = Marker.Create(scope, new MarkerSnapshot { Label = "a" });
        Marker? formatted = null;
        marker.SetLabelFormatter(scope, m =>
        {
            formatted = m;
            return $"{m.Label}!";
        });
        Equal("a!", marker.DisplayLabel); // JS calls the formatter, and uses what it returns
        Expect(ReferenceEquals(formatted, marker), "the formatter to get the marker itself");
        marker.SetLabelFormatter(scope, null);
        Equal("a", marker.DisplayLabel);
    }

    private static async Task CallbacksAreReleasedWithTheirScope(JsScope scope)
    {
        var markers = MapView.Create(scope, new MapViewSnapshot()).GetMarkers(scope);
        using (var setup = new JsScope(scope.Realm))
        {
            markers.Add(Marker.Create(setup, new MarkerSnapshot())); // no .NET scope owns the marker any more
        }
        var baseline = await Count(scope.Realm);
        using (var inner = new JsScope(scope.Realm))
        {
            markers.ForEach(inner, (_, _) => { });
            Expect(markers.Find(inner, (_, _) => true).HasValue, "a marker");
            // The inner scope owns both callbacks, and the marker they got (forEach also passes the collection, already owned).
            Equal((baseline.Handles + 1, baseline.Callbacks + 2), await Count(scope.Realm));
        }
        Equal(baseline, await Count(scope.Realm));
    }

    private static async Task UnreadArgumentsAreReleased(JsScope scope)
    {
        var view = MapView.Create(scope, new MapViewSnapshot());
        using (var setup = new JsScope(scope.Realm))
        {
            view.SetSelected(Marker.Create(setup, new MarkerSnapshot())); // no .NET scope owns it any more
        }
        var baseline = await Count(scope.Realm);
        using (var inner = new JsScope(scope.Realm))
        {
            var seen = new List<bool>();
            using (view.WatchSelected(inner, selected => seen.Add(selected.IsNull)))
            {
                view.SetSelected(null); // JS passes (value, old): nothing reads the old marker
            }
            Equal("True", string.Join(",", seen));
        }
        Equal(baseline, await Count(scope.Realm));
    }

    // --- Helpers ---

    // Each case has its own scope; once it is disposed, the census and the callbacks are back to their baseline.
    private static Func<JsRealm, Task> InScope(Action<JsScope> body) => InScope(scope =>
    {
        body(scope);
        return Task.CompletedTask;
    });

    // The census goes through the async seam, so on Server only the bindings fail fast. A failed case reports its own
    // exception: the scope is then released through the async seam, which every host has.
    private static Func<JsRealm, Task> InScope(Func<JsScope, Task> body) => async realm =>
    {
        var baseline = await Count(realm);
        var scope = new JsScope(realm);
        try
        {
            await body(scope);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
        scope.Dispose();
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
    private static Shape Held(JsScope scope, JsObject target, string name) => scope.Realm.Call(JsOp.Get(target.Handle, name), scope.Read<Shape>)!;

    private static bool Click(JsScope scope, MapView view) => scope.Realm.Call(JsOp.Call(view.Handle, "emit", w =>
    {
        w.WriteStringValue("click");
        ClickEvent.Write(w, new ClickEvent { X = 1, Y = -2, Button = ClickEventButton._0, MapPoint = new PointSnapshot { X = 1, Y = 2 } });
    }), JsJson.ReadBoolean);
}
