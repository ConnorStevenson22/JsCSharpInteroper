using System.Runtime.CompilerServices;
using Interoper.Contract;

namespace Interoper.Runtime.Tests;

/// <summary>The golden slice's suites (one per mode) against <see cref="InMemoryTransport"/> and the fixture fakes.</summary>
public sealed class GoldenSliceTests
{
    public static TheoryData<string> SyncCases() => [.. GoldenSyncSuite.Cases.Select(c => c.Name)];

    public static TheoryData<string> AsyncCases() => [.. GoldenAsyncSuite.Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(SyncCases))]
    public async Task Sync(string name) =>
        await GoldenSyncSuite.Cases.Single(c => c.Name == name).Run(await Realm(GoldenSlice.Sync.DemoMaps.Classes));

    [Theory]
    [MemberData(nameof(AsyncCases))]
    public async Task Async(string name) =>
        await GoldenAsyncSuite.Cases.Single(c => c.Name == name).Run(await Realm(GoldenSlice.Async.DemoMaps.Classes));

    // In memory only: in the browser an element's runtime class is found by its tag, which comes with the Razor components.
    [Fact] // §5, R22
    public async Task TheElementDrivesItsLiveView()
    {
        await using var scope = new JsScope(await Realm(GoldenSlice.Async.DemoMaps.Classes));
        var element = await GoldenSlice.Async.DemoMapElement.CreateAsync(scope, new()); // a custom element's constructor takes no bag
        var view = await element.GetViewAsync(scope);
        Assert.Same(view, await element.GetViewAsync(scope));
        await element.SetZoomAsync(3);
        Assert.Equal(3, await view.GetZoomAsync());

        var clicks = new List<GoldenSlice.Async.ClickEvent>();
        await using (await view.OnClickAsync(scope, clicks.Add))
        {
            await element.ClickAtAsync(8, 0); // the button is optional
            await element.ClickAtAsync(0, 0, GoldenSlice.Async.DemoMapElementClickAtButton._2);
        }
        Assert.Equal([(1.0, GoldenSlice.Async.ClickEventButton._0), (0.0, GoldenSlice.Async.ClickEventButton._2)],
            clicks.Select(c => (c.MapPoint.X.Value, c.Button)));
    }

    [Fact] // design §1: data types are identical in both modes; only live classes, the registry and module functions differ
    public void BothModesHaveTheSameDataTypes()
    {
        var root = Path.Combine(SourceFolder(), "..", "GoldenSlice");
        string[] live =
        [
            "DemoMaps.cs", "Observable.cs", "Emitter.cs", "Collection.cs", "Shape.cs", "Point.cs", "Line.cs", "Polygon.cs", "MapView.cs", "Marker.cs",
            "DemoMapElement.cs", "Layers/Layer.cs", "Layers/TileLayer.cs", "Measure.cs", "Styles.cs", "Pin.cs", "Ops/A.cs", "Ops/B.cs",
        ];
        var files = Files(Path.Combine(root, "Async"));
        Assert.Equal(files, Files(Path.Combine(root, "Sync")));
        foreach (var file in files.Except(live))
        {
            var async = File.ReadAllText(Path.Combine(root, "Async", file));
            Assert.Equal(async.Replace("GoldenSlice.Async", "GoldenSlice.Sync"), File.ReadAllText(Path.Combine(root, "Sync", file)));
        }

        // A mode's files, by their path in it (R27: a folder per namespace).
        static List<string> Files(string folder) =>
            [.. Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order()];
    }

    private static string SourceFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static async Task<JsRealm> Realm(JsClassRegistry classes)
    {
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(InMemoryDemoMaps.Create()), classes);
        await realm.StartAsync();
        return realm;
    }
}
