using System.Runtime.CompilerServices;

namespace Interoper.Generator.Tests;

/// <summary>
/// The golden slice (tests/GoldenSlice) is the fixture's generated bindings: the generator's output for the fixture's
/// IR and profile, in both modes, with a subfolder per namespace (R27). On a difference the test rewrites the files,
/// deletes the ones it no longer generates (and folders left empty), and fails, listing them: review the diff, and a
/// rerun passes.
/// </summary>
public sealed class GoldenSliceTests
{
    /// <summary>
    /// What the fixture still doesn't map, each with the reason, in one mode (<c>Mode</c>) or both (<c>null</c>). Fixing
    /// one removes its entry, so the list is the phase's progress meter.
    /// </summary>
    private static readonly (Api? Mode, string Diagnostic, string Reason)[] Remaining =
    [
        (null, "IOP2007 FlyToOptions.signal: skipped: uses `AbortSignal`", "AbortSignal (R14) isn't bridged yet; the record drops the optional member"),
        (null, "IOP2007 MarkerOptions.labelFormatter: skipped: uses a callback that isn't a parameter (R21)", "a data record can't hold a callback (R21); the record drops the optional member"),
        (null, "IOP2007 Subscription: skipped: uses method `dispose` (in `Subscription`)", "an interface with a method isn't a data record (R15)"),
        (null, "IOP2007 ChangeCallback: skipped: uses generic `ChangeCallback` (in `ChangeCallback`)", "a generic callback type has no C# type"),
        (null, "IOP2007 Emitter.emit: skipped: uses type parameter `K`", "its payload depends on a type parameter"),
        (null, "IOP2007 Collection.constructor: skipped: its parameter isn't a bag, so there's no Create (R18)", "a collection comes from its owner"),
        (null, "IOP2007 DemoMapElement.observedAttributes: skipped: a static member", "static members aren't bound"),
        (null, "IOP2001 Pin.meta: `any` is JsAny (§2)", "untyped by design: the app keeps anything there"),
        (null, "IOP2007 iconNames: skipped: uses a variable", "variables aren't bound"),
        (null, "IOP2006 @acme/demo-maps/ops/b.js: its Options is BOptions: @acme/demo-maps/ops/a.js has one too (R27)",
            "sibling modules declare the same name; the later one is renamed so both compile"),
        (Api.Async, "IOP2005 Marker.labelFormatter: skipped in async mode: JS uses its callback's result (R21)", "JS reads the result synchronously"),
        (Api.Async, "IOP2005 Collection.find: skipped in async mode: JS uses its callback's result (R21)", "JS reads the result synchronously"),
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
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, file.Text);
                    changed.Add(Path.GetRelativePath(root, path));
                }
            }
            var generated = result.Files.Select(f => Path.GetFullPath(Path.Combine(folder, f.Path))).ToHashSet();
            foreach (var stale in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Where(f => !generated.Contains(Path.GetFullPath(f))))
            {
                File.Delete(stale);
                changed.Add($"{Path.GetRelativePath(root, stale)} (deleted)");
            }
            foreach (var empty in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).OrderDescending().Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
            {
                Directory.Delete(empty);
            }
        }
        Assert.Equal(2, metadata.Count);
        Assert.Equal(metadata[0], metadata[1]);
        Assert.True(changed.Count == 0, $"Regenerated {string.Join(", ", changed)}: review the diff, then rerun.");
        foreach (var api in new[] { Api.Sync, Api.Async })
        {
            Assert.Equal(Remaining.Where(r => (r.Mode ?? api) == api).Select(r => r.Diagnostic), diagnostics[api]);
        }
    }

    private static string GoldenSlice([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "GoldenSlice");
}
