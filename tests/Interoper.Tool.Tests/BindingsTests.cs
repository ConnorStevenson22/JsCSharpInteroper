using Interoper.Generator;

namespace Interoper.Tool.Tests;

/// <summary><see cref="Bindings.Write"/>: what the CLI and the golden slice write with.</summary>
public sealed class BindingsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("interoper-bindings-").FullName;

    [Fact]
    public void WritesOnlyWhatChangedAndDeletesWhatTheLastRunProducedAndThisOneDoesnt()
    {
        GeneratedFile[] first = [new("A.cs", $"{Bindings.Header}\n// a\n"), new("Layers/B.cs", $"{Bindings.Header}\n// b\n")];
        Assert.Equal(["A.cs", "Layers/B.cs"], Bindings.WriteTracked(folder, first).Written);

        var again = Bindings.WriteTracked(folder, first);
        Assert.Empty(again.Written);
        Assert.Empty(again.Deleted);

        var next = Bindings.WriteTracked(folder + Path.DirectorySeparatorChar, [new("A.cs", $"{Bindings.Header}\n// a2\n")]);
        Assert.Equal(["A.cs"], next.Written);
        Assert.Equal(["Layers/B.cs"], next.Deleted);
        Assert.False(Directory.Exists(Path.Combine(folder, "Layers")));
        Assert.True(Directory.Exists(folder));
        Assert.Equal(["A.cs"], File.ReadAllLines(Path.Combine(folder, Bindings.CacheFolder, Bindings.ManifestFile)));
    }

    [Fact]
    public void WithoutAManifestNothingIsDeleted()
    {
        File.WriteAllText(Path.Combine(folder, "Other.cs"), $"{Bindings.Header}\n");
        Assert.Empty(Bindings.WriteTracked(folder, [new("A.cs", $"{Bindings.Header}\n")]).Deleted);
        Assert.True(File.Exists(Path.Combine(folder, "Other.cs")));
    }

    [Fact]
    public void ACaseOnlyRenameEndsWithTheNewName()
    {
        // The stale file is deleted before the new one is written, so on a case-insensitive file system, where both
        // names are one file, the new one isn't deleted with it and takes its new name.
        Bindings.WriteTracked(folder, [new("Mapview.cs", $"{Bindings.Header}\n// old\n")]);
        var changes = Bindings.WriteTracked(folder, [new("MapView.cs", $"{Bindings.Header}\n// new\n")]);
        Assert.Equal(["Mapview.cs"], changes.Deleted);
        Assert.Equal(["MapView.cs"], changes.Written);
        Assert.Equal(["MapView.cs"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal($"{Bindings.Header}\n// new\n", File.ReadAllText(Path.Combine(folder, "MapView.cs")));
    }

    [Fact]
    public void AManifestPathOutsideTheFolderIsIgnored()
    {
        var outside = Path.Combine(Path.GetDirectoryName(folder)!, $"{Path.GetFileName(folder)}-outside.cs");
        File.WriteAllText(outside, $"{Bindings.Header}\n");
        try
        {
            Assert.Empty(Bindings.Write(folder, [], [$"../{Path.GetFileName(outside)}", outside]).Deleted);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("../Escape.cs", "A.cs")]
    [InlineData("A.cs", "a.cs")]
    [InlineData("A.cs", "A.cs")]
    public void PathsThatLeaveTheFolderOrDifferOnlyInCaseAreRejected(string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => Bindings.Write(folder, [new(first, ""), new(second, "")], []));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
