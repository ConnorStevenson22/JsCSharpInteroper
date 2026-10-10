using System.Runtime.CompilerServices;
using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>The fixtures' golden IRs, and demo-maps' profile.</summary>
internal static class Fixture
{
    public static IrDocument Ir { get; } = IrDocument.Load(PathOf("demo-maps", "demo-maps.ir.json"));

    /// <summary>demo-maps and demo-widgets in one IR, demo-maps first.</summary>
    public static IrDocument WithWidgetsIr { get; } = IrDocument.Load(PathOf("demo-widgets", "demo-widgets.ir.json"));

    public static Profile Profile { get; } = Profile.Load(PathOf("demo-maps", "profile.json"));

    private static string PathOf(string fixture, string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "fixtures", fixture, file);
}
