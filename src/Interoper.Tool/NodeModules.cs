using System.Text.Json;

namespace Interoper.Tool;

/// <summary>An installed npm package: its folder and its <c>package.json</c>'s name and version.</summary>
internal sealed record InstalledPackage(string Name, string Version, string Directory)
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
            var json = Path.Combine(folder, "package.json");
            if (File.Exists(json))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(json));
                    var root = document.RootElement;
                    var version = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    return new(name, version, folder);
                }
                catch (JsonException e)
                {
                    throw new CliException($"{json} can't be read: {e.Message}");
                }
            }
        }
        return null;
    }
}
