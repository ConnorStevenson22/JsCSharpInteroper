using System.Runtime.CompilerServices;
using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>The fixtures' golden IRs, and demo-maps' profile.</summary>
internal static class Fixture
{
    public static IrDocument Ir { get; } = IrDocument.Load(PathOf("demo-maps", "demo-maps.ir.json"));

    /// <summary>
    /// demo-maps and demo-widgets in one IR, demo-maps first: demo-maps' IR with what demo-widgets adds to it (its golden
    /// file), in IR order, as the extractor's test checks.
    /// </summary>
    public static IrDocument WithWidgetsIr { get; } = WithWidgets(IrDocument.Load(PathOf("demo-widgets", "demo-widgets.ir.json")));

    public static Profile Profile { get; } = Profile.Load(PathOf("demo-maps", "profile.json"));

    private static IrDocument WithWidgets(IrDocument delta) => Ir with
    {
        Modules = [.. Ir.Modules.Concat(delta.Modules).OrderBy(m => m.Specifier, StringComparer.Ordinal)],
        Elements = [.. Ir.Elements.Concat(delta.Elements).OrderBy(e => e.Tag, StringComparer.Ordinal)],
    };

    private static string PathOf(string fixture, string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "fixtures", fixture, file);
}
