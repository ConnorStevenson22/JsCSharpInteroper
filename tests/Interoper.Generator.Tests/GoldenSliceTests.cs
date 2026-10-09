using System.Runtime.CompilerServices;

namespace Interoper.Generator.Tests;

/// <summary>
/// The golden slice (tests/GoldenSlice) is the generator's output for the fixture's IR and profile, in both modes. On a
/// difference the test rewrites the files, deletes the ones it no longer generates, and fails, listing them: review the
/// diff, and a rerun passes.
/// </summary>
public sealed class GoldenSliceTests
{
    [Fact]
    public void TheGoldenSliceIsGenerated()
    {
        var root = GoldenSlice();
        var changed = new List<string>();
        var metadata = new List<string>();
        foreach (var api in new[] { Api.Sync, Api.Async })
        {
            var folder = Path.Combine(root, api.ToString());
            var result = Generator.Generate(Fixture.Ir, Fixture.Profile, new(api, $"GoldenSlice.{api}"));
            Assert.Equal(["IOP2007 MapView.flyTo(options): optional parameter dropped (R22): uses `AbortSignal` (in `FlyToOptions`)"],
                result.Diagnostics.Select(d => d.ToString()));

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
    }

    private static string GoldenSlice([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "GoldenSlice");
}
