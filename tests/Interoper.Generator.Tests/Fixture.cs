using System.Runtime.CompilerServices;
using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>The demo-maps fixture: the golden IR and the profile.</summary>
internal static class Fixture
{
    public static IrDocument Ir { get; } = IrDocument.Load(PathOf("demo-maps.ir.json"));

    public static Profile Profile { get; } = Profile.Load(PathOf("profile.json"));

    private static string PathOf(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "fixtures", "demo-maps", file);
}
