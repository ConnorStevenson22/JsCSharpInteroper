using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Interoper.Generator;

namespace Interoper.Tool;

/// <param name="WorkingDirectory">Where packages are looked up (Node-style, upwards) and relative paths start.</param>
/// <param name="Error">Where messages and the report go (stderr).</param>
public sealed record CliEnvironment(string WorkingDirectory, TextWriter Error)
{
    /// <summary>The Node.js executable the extractor runs on.</summary>
    public string Node { get; init; } = "node";

    /// <summary>The extractor's source, shipped next to the tool (design §4).</summary>
    public string ExtractorDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "extractor");
}

/// <summary>
/// <c>dotnet interoper generate &lt;package&gt;... --api sync|async --namespace N --out DIR [--profile F]
/// [--warnings-as-errors]</c> (design §5). Exit codes: 0 done, 1 diagnostics with <c>--warnings-as-errors</c>, 2 bad
/// usage or a failed extraction.
/// </summary>
public static partial class Cli
{
    /// <summary>The TypeScript the extractor is pinned to (<c>src/interoper-extractor/package.json</c>).</summary>
    public const string TypeScriptPin = "6.0.3";

    public const string Usage =
        "usage: dotnet interoper generate <package>... --api sync|async --namespace <Namespace> --out <folder> [--profile <profile.json>] [--warnings-as-errors]";

    /// <summary>The tool's version: the first line of the cache key and of the report.</summary>
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static int Run(IReadOnlyList<string> args, CliEnvironment env)
    {
        if (args is [] or ["-h" or "--help" or "help"])
        {
            env.Error.WriteLine(Usage);
            return args is [] ? 2 : 0;
        }
        try
        {
            return args[0] == "generate"
                ? Generate(GenerateArgs.Parse(args.Skip(1).ToList()), env)
                : throw new UsageException($"unknown command `{args[0]}`");
        }
        catch (UsageException e)
        {
            env.Error.WriteLine($"error: {e.Message}");
            env.Error.WriteLine(Usage);
            return 2;
        }
        catch (CliException e)
        {
            env.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    private static int Generate(GenerateArgs args, CliEnvironment env)
    {
        var cwd = env.WorkingDirectory;
        var packages = args.Packages
            .Select(name => NodeModules.Find(cwd, name)
                ?? throw new CliException($"`{name}` isn't installed: there's no node_modules/{name} in {cwd} or above it. Run `npm install {name}`."))
            .ToList();
        var typeScript = NodeModules.Find(cwd, "typescript") ?? NodeModules.Find(env.ExtractorDirectory, "typescript")
            ?? throw new CliException($"TypeScript isn't installed: the extractor needs TypeScript 6.x in your node_modules. Run `npm install -D typescript@{TypeScriptPin}`.");
        if (!typeScript.Version.StartsWith("6.", StringComparison.Ordinal))
        {
            throw new CliException($"TypeScript {typeScript.Version} is installed ({typeScript.Directory}); the extractor needs 6.x. Run `npm install -D typescript@{TypeScriptPin}`.");
        }

        var profilePath = args.Profile is null ? null : Path.GetFullPath(Path.Combine(cwd, args.Profile));
        Profile profile;
        try
        {
            profile = profilePath is null ? new Profile { Package = packages[0].Name } : Profile.Load(profilePath);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new CliException($"The profile {profilePath} can't be read: {e.Message}");
        }

        var outDir = Path.GetFullPath(Path.Combine(cwd, args.Out));
        var cache = new IrCache(outDir);
        var inputs = new ExtractionInputs(Version, typeScript, packages, profile.Events?.MapProperty);
        var ir = cache.TryLoad(inputs);
        if (ir is not null)
        {
            env.Error.WriteLine($"IR: reused {cache.IrPath}");
        }
        else
        {
            env.Error.WriteLine($"IR: extracting {string.Join(", ", packages)} with TypeScript {typeScript.Version}");
            ir = cache.Extract(inputs, env, profilePath);
        }

        GeneratorResult result;
        try
        {
            result = Generator.Generator.Generate(ir, profile, new(args.Api, args.Namespace));
        }
        catch (InvalidDataException e)
        {
            throw new CliException(e.Message);
        }
        var changes = Bindings.Write(outDir, result.Files);
        var report = Report.Text(Version, args.Api, args.Namespace, packages, result.Diagnostics);
        File.WriteAllText(Path.Combine(outDir, Report.FileName), report);

        env.Error.WriteLine($"Generated {result.Files.Count} files in {outDir}: {changes.Written.Count} written, {changes.Deleted.Count} deleted.");
        env.Error.Write(report);
        if (args.WarningsAsErrors && result.Diagnostics.Count > 0)
        {
            env.Error.WriteLine($"error: {result.Diagnostics.Count} diagnostics, and --warnings-as-errors is set.");
            return 1;
        }
        return 0;
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed record GenerateArgs(IReadOnlyList<string> Packages, Api Api, string Namespace, string Out, string? Profile, bool WarningsAsErrors)
    {
        public static GenerateArgs Parse(IReadOnlyList<string> args)
        {
            var packages = new List<string>();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var warningsAsErrors = false;
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--api" or "--namespace" or "--out" or "--profile":
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new UsageException($"{arg} needs a value");
                        }
                        if (!values.TryAdd(arg, args[++i]))
                        {
                            throw new UsageException($"{arg} is given twice");
                        }
                        break;
                    case "--warnings-as-errors":
                        warningsAsErrors = true;
                        break;
                    case var _ when arg.StartsWith('-'):
                        throw new UsageException($"unknown option `{arg}`");
                    default:
                        packages.Add(arg);
                        break;
                }
            }
            if (packages.Count == 0)
            {
                throw new UsageException("name at least one package");
            }
            var api = Required(values, "--api") switch
            {
                "sync" => Api.Sync,
                "async" => Api.Async,
                var other => throw new UsageException($"--api is sync or async, not `{other}`"),
            };
            var ns = Required(values, "--namespace");
            if (!NamespacePattern().IsMatch(ns))
            {
                throw new UsageException($"--namespace `{ns}` isn't a C# namespace");
            }
            return new(packages, api, ns, Required(values, "--out"), values.GetValueOrDefault("--profile"), warningsAsErrors);
        }

        private static string Required(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) ? value : throw new UsageException($"{name} is required");
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex NamespacePattern();
}
