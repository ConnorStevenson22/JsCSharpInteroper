using System.Reflection;
using System.Text.RegularExpressions;
using static Interoper.E2E.Shell;

namespace Interoper.E2E;

/// <summary>
/// Trimming and size (design §6): a Release publish of the WebAssembly client, trim analysis on, has no warnings of
/// ours, and what Interoper and the golden slice's bindings add to the download stays within a budget.
/// </summary>
[Collection(ReleaseBuilds)] // the quickstart's `dotnet pack` builds the same projects in Release
public sealed partial class SizeBudgetTests
{
    internal const string ReleaseBuilds = "Release builds";

    // The summed published size, in bytes, of the assemblies below (webcil .wasm, trimmed, uncompressed), with about
    // 10% headroom.
    private const long Budget = 438_000;

    private static readonly string[] Assemblies = ["Interoper.Runtime", "Interoper.Blazor", "GoldenSlice"];

    // Blazor's SDK hides trim analysis warnings by default, and these framework assemblies (RC 1) have some. The test
    // requires them too, as proof that the trimmer ran with its warnings on.
    private static readonly string[] FrameworkWarnings =
    [
        "IL2104: Assembly 'Microsoft.AspNetCore.Components' produced trim warnings.",
        "IL2104: Assembly 'Microsoft.JSInterop' produced trim warnings.",
    ];

    [Fact]
    public async Task TheWebAssemblyClientPublishesTrimmedWithinItsBudget()
    {
        // The runtime and the components are trimmable (IsAotCompatible), so the trimmer may cut them.
        foreach (var assembly in new[] { typeof(JsRealm).Assembly, typeof(JsComponentBase).Assembly })
        {
            Assert.Contains(assembly.GetCustomAttributes<AssemblyMetadataAttribute>(), a => a is { Key: "IsTrimmable", Value: "True" });
        }

        var output = Directory.CreateTempSubdirectory("interoper-size-").FullName;
        try
        {
            // Trim warnings on but not errors, so the test lists them instead of failing at the first; compiler
            // warnings are errors (TreatWarningsAsErrors). The trimmer links into a fresh folder, so it runs, and
            // warns, every time, not only when its inputs changed.
            var linked = Path.Combine(output, "linked") + Path.DirectorySeparatorChar;
            var log = await RunAsync(
                $"dotnet publish tests/Interoper.E2E.Client -c Release -o \"{output}\" -p:IntermediateLinkDir=\"{linked}\" -p:SuppressTrimAnalysisWarnings=false -p:ILLinkTreatWarningsAsErrors=false",
                Repo());
            var warnings = log.Split('\n').Select(l => Warning().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            Assert.DoesNotContain(warnings, w => !FrameworkWarnings.Any(f => w.StartsWith(f, StringComparison.Ordinal)));
            // The framework's warnings showing up is what proves trim analysis ran. When an upgrade fixes one, remove it.
            Assert.All(FrameworkWarnings, f => Assert.Contains(warnings, w => w.StartsWith(f, StringComparison.Ordinal)));

            var framework = Path.Combine(output, "wwwroot", "_framework");
            var sizes = Assemblies.ToDictionary(name => name, name =>
                new FileInfo(Assert.Single(Directory.GetFiles(framework, $"{name}.*.wasm"), f => Fingerprinted().IsMatch(Path.GetFileName(f)[(name.Length + 1)..]))).Length);
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ", sizes.Select(s => $"{s.Key} {s.Value:N0}")) + $", total {sizes.Values.Sum():N0} bytes");
            Assert.True(sizes.Values.Sum() <= Budget, $"{sizes.Values.Sum():N0} bytes is over the budget of {Budget:N0}: {string.Join(", ", sizes)}");
        }
        finally
        {
            try
            {
                Directory.Delete(output, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort, as in QuickstartTests.
            }
        }
    }

    // MSBuild's form: `file(line,col): warning CODE: text [project]`, or `file : warning CODE: …`.
    [GeneratedRegex(@": warning (.*?)(?: \[[^\]]*\])?\s*$")]
    private static partial Regex Warning();

    // What follows `{name}.`: the fingerprint and the extension, so Interoper.Runtime doesn't match Interoper.Runtime.Foo.
    [GeneratedRegex(@"^[a-z0-9]+\.wasm$")]
    private static partial Regex Fingerprinted();
}
