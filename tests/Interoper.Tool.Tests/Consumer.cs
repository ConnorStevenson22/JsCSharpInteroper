using System.Runtime.CompilerServices;

namespace Interoper.Tool.Tests;

/// <summary>
/// A consumer's folder, as <c>npm install</c> leaves it: <c>node_modules/@acme/demo-maps</c> (a copy of the fixture) and
/// <c>node_modules/typescript</c> (a link to the extractor's pinned one, so <c>npm install --prefix
/// src/interoper-extractor</c> must have run, as CLAUDE.md says).
/// </summary>
public sealed class Consumer : IDisposable
{
    public Consumer()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("interoper-tool-").FullName;
        var modules = Path.Combine(Directory, "node_modules");
        CopyDirectory(Repo("fixtures", "demo-maps"), Path.Combine(modules, "@acme", "demo-maps"));
        var typeScript = Repo("src", "interoper-extractor", "node_modules", "typescript");
        Assert.True(System.IO.Directory.Exists(typeScript), "Run `npm install --prefix src/interoper-extractor` first (CLAUDE.md).");
        System.IO.Directory.CreateSymbolicLink(Path.Combine(modules, "typescript"), typeScript);
    }

    public string Directory { get; }

    public string Package => Path.Combine(Directory, "node_modules", "@acme", "demo-maps");

    public string ProfilePath => Path.Combine(Package, "profile.json");

    /// <summary>Runs the CLI in this folder, with stderr captured.</summary>
    public (int ExitCode, string Error) Run(string[] args, string? node = null)
    {
        var error = new StringWriter();
        var env = new CliEnvironment(Directory, error);
        if (node is not null)
        {
            env = env with { Node = node };
        }
        return (Cli.Run(args, env), error.ToString());
    }

    /// <summary><c>generate @acme/demo-maps --api async</c> into <paramref name="outDir"/>, with the fixture's profile.</summary>
    public static string[] Generate(string outDir, params string[] more) =>
        ["generate", "@acme/demo-maps", "--api", "async", "--namespace", "Acme.Maps", "--out", outDir, "--profile", "node_modules/@acme/demo-maps/profile.json", .. more];

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

    public static string Repo(params string[] parts) => Path.GetFullPath(Path.Combine([RepoRoot(), .. parts]));

    public static void CopyDirectory(string from, string to)
    {
        foreach (var file in System.IO.Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string RepoRoot([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "..", "..");
}
