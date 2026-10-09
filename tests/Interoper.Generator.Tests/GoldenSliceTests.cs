using System.Runtime.CompilerServices;

namespace Interoper.Generator.Tests;

/// <summary>
/// The golden slice (tests/GoldenSlice) is the generator's output for the fixture's IR and profile, in both modes. On a
/// difference the test rewrites the files and fails, listing them: review the diff, and a rerun passes.
/// </summary>
public sealed class GoldenSliceTests
{
    /// <summary>Written by hand until the generator emits live classes, the registry and module functions.</summary>
    private static readonly string[] HandWritten =
        ["DemoMaps.cs", "Shape.cs", "Point.cs", "Line.cs", "Polygon.cs", "MapView.cs", "Marker.cs", "Measure.cs"];

    [Fact]
    public void TheGoldenSliceIsGenerated()
    {
        var changed = new List<string>();
        foreach (var api in new[] { Api.Sync, Api.Async })
        {
            var folder = Path.Combine(GoldenSlice(), api.ToString());
            var result = Generator.Generate(Fixture.Ir, Fixture.Profile, new(api, $"GoldenSlice.{api}"));

            Assert.Equal(["IOP2007 MapView.flyTo(options): optional parameter dropped (R22): uses `AbortSignal` (in `FlyToOptions`)"],
                result.Diagnostics.Select(d => d.ToString()));
            Assert.Equal(Directory.GetFiles(folder, "*.cs").Select(Path.GetFileName).Except(HandWritten).Order(),
                result.Files.Select(f => f.Path).Order());
            foreach (var file in result.Files)
            {
                var path = Path.Combine(folder, file.Path);
                if (File.ReadAllText(path) != file.Text)
                {
                    File.WriteAllText(path, file.Text);
                    changed.Add($"{api}/{file.Path}");
                }
            }
        }
        Assert.True(changed.Count == 0, $"Regenerated {string.Join(", ", changed)}: review the diff, then rerun.");
    }

    private static string GoldenSlice([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "GoldenSlice");
}
