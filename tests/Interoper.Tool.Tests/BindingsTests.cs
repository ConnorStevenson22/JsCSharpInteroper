using Interoper.Generator;

namespace Interoper.Tool.Tests;

/// <summary><see cref="Bindings.Write"/>: what the CLI and the golden slice write with.</summary>
public sealed class BindingsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("interoper-bindings-").FullName;

    [Fact]
    public void WritesOnlyWhatChangedAndDeletesWhatIsNoLongerGenerated()
    {
        GeneratedFile[] first = [new("A.cs", $"{Bindings.Header}\n// a\n"), new("Layers/B.cs", $"{Bindings.Header}\n// b\n")];
        Assert.Equal(new[] { "A.cs", Path.Combine("Layers", "B.cs") }, Bindings.Write(folder, first).Written);

        var again = Bindings.Write(folder, first);
        Assert.Empty(again.Written);
        Assert.Empty(again.Deleted);

        var cache = Path.Combine(folder, Bindings.CacheFolder);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "Cached.cs"), $"{Bindings.Header}\n");
        var next = Bindings.Write(folder + Path.DirectorySeparatorChar, [new("A.cs", $"{Bindings.Header}\n// a2\n")]);
        Assert.Equal(["A.cs"], next.Written);
        Assert.Equal([Path.Combine("Layers", "B.cs")], next.Deleted);
        Assert.False(Directory.Exists(Path.Combine(folder, "Layers")));
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(cache, "Cached.cs")));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
