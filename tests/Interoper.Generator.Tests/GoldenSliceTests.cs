using System.Runtime.CompilerServices;

namespace Interoper.Generator.Tests;

/// <summary>
/// The golden slice (tests/GoldenSlice) is the fixture's generated bindings: the generator's output for the fixture's
/// IR and profile, in both modes. On a difference the test rewrites the files, deletes the ones it no longer generates,
/// and fails, listing them: review the diff, and a rerun passes.
/// </summary>
public sealed class GoldenSliceTests
{
    /// <summary>
    /// What the fixture still doesn't map, per mode, each with the reason. Fixing one removes its entry, so the list is
    /// the phase's progress meter.
    /// </summary>
    private static readonly (string Diagnostic, string Reason)[] Remaining =
    [
        ("IOP2007 FlyToOptions: skipped: uses `AbortSignal` (in `FlyToOptions`)", "AbortSignal (R14) isn't bridged yet"),
        ("IOP2007 MapViewOptions: skipped: uses `HTMLElement` (in `MapViewOptions`)", "DOM elements (R13) come with the Razor components"),
        ("IOP2007 MapView.container: skipped: uses `HTMLElement`", "DOM elements (R13) come with the Razor components"),
        ("IOP2007 MapView.markers: skipped: uses generic `Collection`", "collections come with generic live classes"),
        ("IOP2007 MapView.flyTo(options): optional parameter dropped (R22): uses `AbortSignal` (in `FlyToOptions`)", "AbortSignal (R14) isn't bridged yet"),
        ("IOP2007 LabelFormatter: skipped: uses a callback (in `LabelFormatter`)", "callbacks (R21) aren't generated yet"),
        ("IOP2007 MarkerOptions: skipped: uses a callback (in `LabelFormatter`) (in `MarkerOptions`)", "a data record can't hold a callback"),
        ("IOP2007 Marker.labelFormatter: skipped: uses a callback (in `LabelFormatter`)", "callbacks (R21) aren't generated yet"),
        ("IOP2007 Subscription: skipped: uses method `dispose` (in `Subscription`)", "an interface with a method isn't a data record (R15)"),
        ("IOP2007 ChangeCallback: skipped: uses generic `ChangeCallback` (in `ChangeCallback`)", "a generic callback type has no C# type"),
        ("IOP2007 Emitter.emit: skipped: uses type parameter `K`", "its payload depends on a type parameter"),
        ("IOP2007 CollectionChangeEvent: skipped: uses generic `CollectionChangeEvent` (in `CollectionChangeEvent`)", "generic records come with collections"),
        ("IOP2007 CollectionEvents: skipped: uses generic `CollectionEvents` (in `CollectionEvents`)", "generic records come with collections"),
        ("IOP2007 Collection: skipped: uses generic `Collection`", "collections come with generic live classes"),
        ("IOP2007 DemoMapElement.observedAttributes: skipped: a static member", "static members aren't bound"),
        ("IOP2007 DemoMapElementEventMap: skipped: uses `CustomEvent` (in `DemoMapElementEventMap`)", "custom events come with the Razor components"),
        ("IOP2007 iconNames: skipped: uses a variable", "variables aren't bound"),
    ];

    [Fact]
    public void TheGoldenSliceIsGenerated()
    {
        var root = GoldenSlice();
        var changed = new List<string>();
        var metadata = new List<string>();
        var diagnostics = new Dictionary<Api, string[]>();
        foreach (var api in new[] { Api.Sync, Api.Async })
        {
            var folder = Path.Combine(root, api.ToString());
            var result = Generator.Generate(Fixture.Ir, Fixture.Profile, new(api, $"GoldenSlice.{api}"));
            diagnostics[api] = [.. result.Diagnostics.Select(d => d.ToString())];

            foreach (var file in result.Files)
            {
                // The metadata module is the same in both modes (design §2), and served from wwwroot.
                var path = file.Path.EndsWith(".js", StringComparison.Ordinal) ? Path.Combine(root, "wwwroot", file.Path) : Path.Combine(folder, file.Path);
                if (file.Path.EndsWith(".js", StringComparison.Ordinal))
                {
                    metadata.Add(file.Text);
                }
                if (!File.Exists(path) || File.ReadAllText(path) != file.Text)
                {
                    File.WriteAllText(path, file.Text);
                    changed.Add(Path.GetRelativePath(root, path));
                }
            }
            foreach (var stale in Directory.GetFiles(folder, "*.cs").Where(f => result.Files.All(g => g.Path != Path.GetFileName(f))))
            {
                File.Delete(stale);
                changed.Add($"{Path.GetRelativePath(root, stale)} (deleted)");
            }
        }
        Assert.Equal(2, metadata.Count);
        Assert.Equal(metadata[0], metadata[1]);
        Assert.True(changed.Count == 0, $"Regenerated {string.Join(", ", changed)}: review the diff, then rerun.");
        Assert.Equal(Remaining.Select(r => r.Diagnostic), diagnostics[Api.Sync]);
        Assert.Equal(Remaining.Select(r => r.Diagnostic), diagnostics[Api.Async]);
    }

    private static string GoldenSlice([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "GoldenSlice");
}
