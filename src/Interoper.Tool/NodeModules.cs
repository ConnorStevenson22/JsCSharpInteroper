using System.Text.Json;

namespace Interoper.Tool;

/// <summary>
/// An installed npm package: its folder, and its <c>package.json</c>'s name, version and the packages it depends on
/// (<c>dependencies</c> and <c>peerDependencies</c>).
/// </summary>
internal sealed record InstalledPackage(string Name, string Version, string Directory, IReadOnlyList<string> Dependencies)
{
    public override string ToString() => $"{Name}@{Version}";
}

/// <summary>Finds packages as Node does: <c>node_modules/&lt;name&gt;</c> in a folder or the nearest one above it.</summary>
internal static class NodeModules
{
    public static InstalledPackage? Find(string from, string name)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(from)); dir is not null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "node_modules", name);
            if (File.Exists(Path.Combine(folder, "package.json")))
            {
                return Read(folder, name);
            }
        }
        return null;
    }

    /// <summary>The package in <paramref name="folder"/>, or null when it has no <c>package.json</c>.</summary>
    public static InstalledPackage? Read(string folder, string name)
    {
        var json = Path.Combine(folder, "package.json");
        if (!File.Exists(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(json));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(name, "", folder, []);
            }
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var dependencies = new[] { "dependencies", "peerDependencies" }
                .Where(p => root.TryGetProperty(p, out var d) && d.ValueKind == JsonValueKind.Object)
                .SelectMany(p => root.GetProperty(p).EnumerateObject().Select(d => d.Name))
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
            return new(name, version, Path.GetFullPath(folder), dependencies);
        }
        catch (JsonException e)
        {
            throw new CliException($"{json} can't be read: {e.Message}");
        }
    }
}
