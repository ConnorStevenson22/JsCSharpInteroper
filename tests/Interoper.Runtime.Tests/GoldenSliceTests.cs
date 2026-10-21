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

    [Fact] // design §1: data types are identical in both modes; only live classes and mixins (§5), the registry and module functions differ
    public void BothModesHaveTheSameDataTypes()
    {
        var root = Path.Combine(SourceFolder(), "..", "GoldenSlice");
        string[] live =
        [
            "DemoMaps.cs", "Observable.cs", "Emitter.cs", "Collection.cs", "Shape.cs", "Point.cs", "Line.cs", "Polygon.cs", "MapView.cs", "Marker.cs",
            "DemoMapElement.cs", "DemoMap.cs", "Layers/Layer.cs", "Layers/IRefreshable.cs", "Layers/TileLayer.cs", "Measure.cs", "Styles.cs", "Pin.cs", "Ops/A.cs", "Ops/B.cs",
            "Subscription.cs", "Tool.cs", "MeasureTool.cs", "Tools.cs",
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
