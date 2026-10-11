using System.ComponentModel;
using System.Diagnostics;
using Interoper.Generator.Ir;

namespace Interoper.Tool;

/// <summary>A failure that ends the run with exit code 2: bad usage, a missing tool, or a failed extraction.</summary>
internal sealed class CliException(string message) : Exception(message);

/// <summary>
/// The IR cache (design §5): <c>&lt;out&gt;/.interoper/ir.json</c>, reused while <c>ir.key</c> matches. The key is plain
/// text, one input per line: the tool's version, TypeScript's, each listed package's name and version, the profile's
/// event-map property (the only profile field the extractor reads, ir.md), and the version of each unlisted package the
/// IR has declarations from.
/// </summary>
internal sealed class IrCache(string outDirectory)
{
    public string Folder { get; } = Path.Combine(outDirectory, Bindings.CacheFolder);

    public string IrPath => Path.Combine(Folder, "ir.json");

    public string KeyPath => Path.Combine(Folder, "ir.key");

    /// <summary>The cached IR, when its key matches; null when it doesn't, or the cache is missing or unreadable.</summary>
    public IrDocument? TryLoad(ExtractionInputs inputs)
    {
        if (!File.Exists(KeyPath) || !File.Exists(IrPath))
        {
            return null;
        }
        try
        {
            var ir = IrDocument.Load(IrPath);
            return File.ReadAllText(KeyPath) == inputs.Key(ir) ? ir : null;
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Runs the extractor, then writes the key, so an extraction that fails leaves no key behind.</summary>
    public IrDocument Extract(ExtractionInputs inputs, CliEnvironment env, string? profilePath)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, ".gitignore"), "*\n");
        File.Delete(KeyPath);

        var start = new ProcessStartInfo(env.Node)
        {
            WorkingDirectory = env.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(env.ExtractorDirectory, "extract.mjs"));
        foreach (var package in inputs.Packages)
        {
            start.ArgumentList.Add(package.Directory);
        }
        if (profilePath is not null)
        {
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(profilePath);
        }
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(IrPath);
        start.Environment["INTEROPER_TYPESCRIPT"] = inputs.TypeScript.Directory;

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception)
        {
            throw new CliException($"Node.js wasn't found (`{env.Node}`): the extractor runs on it. Install Node.js 22 or later and put `node` on PATH.");
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            output.Wait();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new CliException($"The extractor failed (exit code {process.ExitCode}):\n{error.TrimEnd()}");
            }
        }

        IrDocument ir;
        try
        {
            ir = IrDocument.Load(IrPath);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            throw new CliException($"The extractor's IR ({IrPath}) can't be read: {e.Message}");
        }
        File.WriteAllText(KeyPath, inputs.Key(ir));
        return ir;
    }
}

/// <summary>What an extraction depends on, for the cache key.</summary>
internal sealed record ExtractionInputs(string ToolVersion, InstalledPackage TypeScript, IReadOnlyList<InstalledPackage> Packages, string? EventMapProperty)
{
    /// <summary>
    /// The key for an IR extracted from these inputs. The unlisted packages <paramref name="ir"/> has declarations from
    /// are found as Node finds them from the listed packages' folders.
    /// </summary>
    public string Key(IrDocument ir)
    {
        var lines = new List<string>
        {
            $"interoper {ToolVersion}",
            $"typescript {TypeScript.Version}",
        };
        lines.AddRange(Packages.Select(p => $"package {p}"));
        lines.Add($"events.mapProperty {EventMapProperty}".TrimEnd());
        var listed = Packages.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in ir.Modules.Select(m => m.Package).OfType<string>().Where(n => !listed.Contains(n)).Distinct().Order(StringComparer.Ordinal))
        {
            var found = Packages.Select(p => NodeModules.Find(RealPath(p.Directory), name)).FirstOrDefault(d => d is not null);
            lines.Add($"dependency {name}@{found?.Version ?? "?"}");
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string RealPath(string directory) =>
        new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory;
}
