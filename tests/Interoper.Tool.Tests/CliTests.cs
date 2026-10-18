using Interoper.Generator;
using Interoper.Generator.Ir;

namespace Interoper.Tool.Tests;

/// <summary>A consumer whose bindings have been generated once, into <c>Seed</c>, with the real extractor.</summary>
public sealed class SeededConsumer : IDisposable
{
    public SeededConsumer()
    {
        (ExitCode, Error) = Consumer.Run(Consumer.Generate("Seed"));
    }

    public Consumer Consumer { get; } = new();

    public int ExitCode { get; }

    public string Error { get; }

    public string Seed => Path.Combine(Consumer.Directory, "Seed");

    /// <summary>A new output folder holding a copy of the seed's IR cache.</summary>
    public string OutWithCache(string name)
    {
        var outDir = Path.Combine(Consumer.Directory, name);
        Consumer.CopyDirectory(Path.Combine(Seed, ".interoper"), Path.Combine(outDir, ".interoper"));
        return outDir;
    }

    public void Dispose() => Consumer.Dispose();
}

/// <summary><c>dotnet interoper generate</c> (design §5): the IR cache, the report, stale files and exit codes.</summary>
public sealed class CliTests(SeededConsumer seeded) : IClassFixture<SeededConsumer>
{
    /// <summary>A Node.js that doesn't exist: a run that gets past the cache fails with exit code 2.</summary>
    private const string NoNode = "/nonexistent/node";

    private static readonly IrDocument GoldenIr = IrDocument.Load(Consumer.Repo("fixtures", "demo-maps", "demo-maps.ir.json"));

    private static readonly Profile FixtureProfile = Profile.Load(Consumer.Repo("fixtures", "demo-maps", "profile.json"));

    private Consumer Consumer => seeded.Consumer;

    [Fact]
    public void TheFirstRunExtractsTheGoldenIrWithItsKey()
    {
        Assert.True(seeded.ExitCode == 0, seeded.Error);
        Assert.Contains("IR: extracting @acme/demo-maps@0.0.0 with TypeScript 6.0.3", seeded.Error, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(Consumer.Repo("fixtures", "demo-maps", "demo-maps.ir.json")), File.ReadAllText(Path.Combine(seeded.Seed, ".interoper", "ir.json")));
        Assert.Equal(
            $"interoper {Cli.Version}\ntypescript 6.0.3\npackage @acme/demo-maps@0.0.0\nevents.mapProperty $events\n",
            File.ReadAllText(Path.Combine(seeded.Seed, ".interoper", "ir.key")));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(seeded.Seed, ".interoper", ".gitignore")));
        Assert.True(File.Exists(Path.Combine(seeded.Seed, "MapView.cs")));
        Assert.True(File.Exists(Path.Combine(seeded.Seed, "Layers", "TileLayer.cs")));
        Assert.True(File.Exists(Path.Combine(seeded.Seed, "demo-maps.meta.js")));
        var manifest = File.ReadAllLines(Path.Combine(seeded.Seed, ".interoper", "files.txt"));
        Assert.Contains("Layers/TileLayer.cs", manifest);
        Assert.Contains("demo-maps.meta.js", manifest);
        Assert.Equal(manifest.Order(StringComparer.Ordinal), manifest);
    }

    [Fact]
    public void AMatchingKeyReusesTheIrWithoutRunningNode()
    {
        var outDir = seeded.OutWithCache("Hit");
        var (exitCode, error) = Consumer.Run(Consumer.Generate("Hit"), NoNode);
        Assert.True(exitCode == 0, error);
        Assert.Contains($"IR: reused {Path.Combine(outDir, ".interoper", "ir.json")}", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outDir, "MapView.cs")));
    }

    [Fact]
    public void AChangedProfileEventMapReExtracts()
    {
        seeded.OutWithCache("EventMap");
        var profile = Path.Combine(Consumer.Directory, "event-map.json");
        File.WriteAllText(profile, File.ReadAllText(Consumer.ProfilePath).Replace("\"$events\"", "\"$on\"", StringComparison.Ordinal));
        string[] args = ["generate", "@acme/demo-maps", "--api", "async", "--namespace", "Acme.Maps", "--out", "EventMap", "--profile", "event-map.json"];
        var (exitCode, error) = Consumer.Run(args, NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("IR: extracting", error, StringComparison.Ordinal);
        Assert.Contains($"error: Node.js wasn't found (`{NoNode}`)", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Consumer.Directory, "EventMap", ".interoper", "ir.key")));
    }

    [Fact]
    public void ANewPackageVersionReExtracts()
    {
        using var consumer = new Consumer();
        Assert.Equal(0, consumer.Run(Consumer.Generate("Out")).ExitCode);
        var json = Path.Combine(consumer.Package, "package.json");
        File.WriteAllText(json, File.ReadAllText(json).Replace("\"version\": \"0.0.0\"", "\"version\": \"0.0.1\"", StringComparison.Ordinal));

        Assert.Equal(2, consumer.Run(Consumer.Generate("Out"), NoNode).ExitCode);
        var (exitCode, error) = consumer.Run(Consumer.Generate("Out"));
        Assert.True(exitCode == 0, error);
        Assert.Contains("IR: extracting @acme/demo-maps@0.0.1", error, StringComparison.Ordinal);
        var key = File.ReadAllText(Path.Combine(consumer.Directory, "Out", ".interoper", "ir.key"));
        Assert.Contains("\npackage @acme/demo-maps@0.0.1\n", key, StringComparison.Ordinal);
        Assert.Equal("0.0.1", IrDocument.Load(Path.Combine(consumer.Directory, "Out", ".interoper", "ir.json")).Package.Version);
    }

    [Fact]
    public void TheReportListsEachDiagnosticSortedWithCountsPerCode()
    {
        var outDir = seeded.OutWithCache("Report");
        var (exitCode, error) = Consumer.Run(Consumer.Generate("Report"), NoNode);
        Assert.True(exitCode == 0, error);

        var diagnostics = Generator.Generator.Generate(GoldenIr, FixtureProfile, new(Api.Async, "Acme.Maps")).Diagnostics
            .OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Subject, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(diagnostics);
        var expected = string.Join('\n', [
            $"Interoper {Cli.Version}",
            "api: async",
            "namespace: Acme.Maps",
            "packages: @acme/demo-maps@0.0.0",
            "",
            .. diagnostics.Select(d => d.ToString()),
            "",
            .. diagnostics.GroupBy(d => d.Code).Select(g => $"{g.Key}: {g.Count()}"),
            $"total: {diagnostics.Count}",
        ]) + "\n";
        var reportPath = Path.Combine(outDir, "interoper-report.txt");
        Assert.Equal(expected, File.ReadAllText(reportPath));
        // stderr has the counts and the report's path, not every diagnostic.
        var counts = string.Join('\n', diagnostics.GroupBy(d => d.Code).Select(g => $"{g.Key}: {g.Count()}")) + $"\ntotal: {diagnostics.Count}\n";
        Assert.EndsWith(counts + $"Report: {reportPath}\n", error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Pin.meta", error, StringComparison.Ordinal);
        Assert.Contains("IOP2001 Pin.meta: `any` is JsAny (§2)\nIOP2005 Collection.find:", expected, StringComparison.Ordinal);
        Assert.Contains("\nIOP2005: 2\n", expected, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningsAsErrorsFailsWithExitCode1AndStillWrites()
    {
        var outDir = seeded.OutWithCache("Strict");
        var (exitCode, error) = Consumer.Run(Consumer.Generate("Strict", "--warnings-as-errors"), NoNode);
        Assert.Equal(1, exitCode);
        Assert.Contains("diagnostics, and --warnings-as-errors is set.", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outDir, "MapView.cs")));
        Assert.True(File.Exists(Path.Combine(outDir, "interoper-report.txt")));
    }

    [Fact]
    public void StaleFilesFromTheLastRunAreDeletedAndOthersKept()
    {
        var outDir = seeded.OutWithCache("Stale");
        Directory.CreateDirectory(Path.Combine(outDir, "Gone"));
        File.WriteAllText(Path.Combine(outDir, "Old.cs"), $"{Bindings.Header}\n#nullable enable\n");
        File.WriteAllText(Path.Combine(outDir, "Gone", "Older.cs"), $"{Bindings.Header}\n");
        File.WriteAllText(Path.Combine(outDir, "old.meta.js"), $"{Bindings.Header}\nexport default {{}};\n");
        // Listed last time, but no longer carrying the header: someone took it over.
        File.WriteAllText(Path.Combine(outDir, "Adopted.cs"), "namespace Acme.Maps;\n");
        // Never listed: another generator's file with the same header, and a hand-written partial.
        File.WriteAllText(Path.Combine(outDir, "GlobalUsings.g.cs"), $"{Bindings.Header}\nglobal using System;\n");
        File.WriteAllText(Path.Combine(outDir, "MapView.Extensions.cs"), "namespace Acme.Maps;\n");
        File.WriteAllLines(Path.Combine(outDir, ".interoper", "files.txt"), ["Adopted.cs", "Gone/Older.cs", "MapView.cs", "Old.cs", "old.meta.js"]);

        var (exitCode, error) = Consumer.Run(Consumer.Generate("Stale"), NoNode);
        Assert.True(exitCode == 0, error);
        Assert.False(File.Exists(Path.Combine(outDir, "Old.cs")));
        Assert.False(File.Exists(Path.Combine(outDir, "old.meta.js")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "Gone")));
        Assert.True(File.Exists(Path.Combine(outDir, "Adopted.cs")));
        Assert.True(File.Exists(Path.Combine(outDir, "GlobalUsings.g.cs")));
        Assert.True(File.Exists(Path.Combine(outDir, "MapView.Extensions.cs")));
        Assert.True(File.Exists(Path.Combine(outDir, "MapView.cs")));
        Assert.Contains(": 71 written, 3 deleted.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Old.cs", File.ReadAllText(Path.Combine(outDir, ".interoper", "files.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedOutputIsLeftAlone()
    {
        var outer = seeded.OutWithCache("Outer");
        seeded.OutWithCache(Path.Combine("Outer", "Inner"));
        Assert.Equal(0, Consumer.Run(Consumer.Generate("Outer/Inner"), NoNode).ExitCode);
        Assert.Equal(0, Consumer.Run(Consumer.Generate("Outer"), NoNode).ExitCode);
        Assert.Equal(0, Consumer.Run(Consumer.Generate("Outer"), NoNode).ExitCode);
        Assert.True(File.Exists(Path.Combine(outer, "Inner", "MapView.cs")));
        Assert.True(File.Exists(Path.Combine(outer, "Inner", "Layers", "TileLayer.cs")));
    }

    [Fact]
    public void AFolderLinkedFromOutsideIsLeftAlone()
    {
        var outDir = seeded.OutWithCache("Linked");
        var outside = Directory.CreateDirectory(Path.Combine(Consumer.Directory, "Outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "Shared.cs"), $"{Bindings.Header}\n");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(outDir, "Shared"), outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't create symbolic links.");
        }
        Assert.Equal(0, Consumer.Run(Consumer.Generate("Linked"), NoNode).ExitCode);
        Assert.True(File.Exists(Path.Combine(outside, "Shared.cs")));
    }
    [Fact] // design §3.5: a base class whose module is exported for its typings only has no module to import
    public void ABaseClassExportedForItsTypingsOnlyIsNeverImported()
    {
        var package = Path.Combine(Consumer.Directory, "node_modules", "@acme", "labels");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "@acme/labels", "version": "1.0.0", "type": "module",
              "exports": { ".": { "types": "./index.d.ts", "import": "./index.js" }, "./base": { "types": "./base.d.ts" } } }
            """);
        File.WriteAllText(Path.Combine(package, "base.d.ts"), "export declare class LabelBase { text: string }");
        File.WriteAllText(Path.Combine(package, "index.d.ts"), """
            import { LabelBase } from "./base.js";
            export type { LabelBase } from "./base.js";
            export declare class Label extends LabelBase { constructor(props?: { text?: string }) }
            """);
        File.WriteAllText(Path.Combine(package, "index.js"), "export class Label { text = \"\"; constructor(props) { Object.assign(this, props); } }");

        var (exitCode, error) = Consumer.Run(["generate", "@acme/labels", "--api", "sync", "--namespace", "Acme.Labels", "--out", "Labels"]);

        Assert.True(exitCode == 0, error);
        var outDir = Path.Combine(Consumer.Directory, "Labels");
        // Only index.js is imported: the sync registry preloads it alone, the metadata module names no other, and the
        // import map has only the package's folder. base.js doesn't exist.
        var registry = File.ReadAllText(Path.Combine(outDir, "Labels.cs"));
        Assert.Contains(""".Preload("@acme/labels");""", registry);
        Assert.Contains("""["@acme/labels/"] = root + "@acme/labels/",""", registry);
        Assert.DoesNotContain("@acme/labels/base", registry, StringComparison.Ordinal);
        var meta = File.ReadAllText(Path.Combine(outDir, "labels.meta.js"));
        Assert.Contains("""1: { module: "@acme/labels", export: "Label", name: "Label", members: ["text"] },""", meta);
        Assert.Contains("""2: { name: "base.LabelBase", members: ["text"] },""", meta); // no module
        Assert.DoesNotContain("@acme/labels/base", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("global::Interoper.JsOp.Create(", File.ReadAllText(Assert.Single(Directory.GetFiles(outDir, "LabelBase.cs", SearchOption.AllDirectories))));
    }

    [Theory]
    [InlineData]
    [InlineData("build")]
    [InlineData("generate")]
    [InlineData("generate", "--api", "async", "--namespace", "Acme.Maps", "--out", "Out")]
    [InlineData("generate", "@acme/demo-maps", "--namespace", "Acme.Maps", "--out", "Out")]
    [InlineData("generate", "@acme/demo-maps", "--api", "both", "--namespace", "Acme.Maps", "--out", "Out")]
    [InlineData("generate", "@acme/demo-maps", "--api", "async", "--namespace", "Acme..Maps", "--out", "Out")]
    [InlineData("generate", "@acme/demo-maps", "--api", "async", "--namespace", "Acme.Maps")]
    [InlineData("generate", "@acme/demo-maps", "--api", "async", "--namespace", "Acme.Maps", "--out")]
    [InlineData("generate", "@acme/demo-maps", "--api", "async", "--api", "sync", "--namespace", "Acme.Maps", "--out", "Out")]
    [InlineData("generate", "@acme/demo-maps", "--api", "async", "--namespace", "Acme.Maps", "--out", "Out", "--verbose")]
    [InlineData("generate", "@acme/demo-maps", "@acme/demo-maps", "--api", "async", "--namespace", "Acme.Maps", "--out", "Out")]
    public void BadUsageExitsWith2(params string[] args)
    {
        var (exitCode, error) = Consumer.Run(args, NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains(Cli.Usage, error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Consumer.Directory, "Out")));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("generate", "--help")]
    [InlineData("generate", "@acme/demo-maps", "-h")]
    public void HelpExitsWith0(params string[] args) => Assert.Equal((0, Cli.Usage + Environment.NewLine), Consumer.Run(args));

    [Fact]
    public void AMissingPackageExitsWith2()
    {
        var (exitCode, error) = Consumer.Run(["generate", "@acme/nowhere", "--api", "sync", "--namespace", "Acme", "--out", "Out"], NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("error: `@acme/nowhere` isn't installed: there's no node_modules/@acme/nowhere", error, StringComparison.Ordinal);
        Assert.Contains("Run `npm install @acme/nowhere`.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileForAnotherPackageExitsWith2BeforeExtracting()
    {
        var profile = Path.Combine(Consumer.Directory, "other.json");
        File.WriteAllText(profile, """{ "package": "@acme/other" }""");
        string[] args = ["generate", "@acme/demo-maps", "--api", "sync", "--namespace", "Acme", "--out", "OtherProfile", "--profile", "other.json"];
        var (exitCode, error) = Consumer.Run(args, NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("error: The profile is for @acme/other, but the first package is @acme/demo-maps.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("IR:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeScriptOtherThan6ExitsWith2()
    {
        using var consumer = new Consumer();
        var typeScript = Path.Combine(consumer.Directory, "node_modules", "typescript");
        Directory.CreateDirectory(typeScript);
        File.WriteAllText(Path.Combine(typeScript, "package.json"), """{ "name": "typescript", "version": "5.9.3" }""");
        consumer.TypeScript = null; // the working directory's
        var (exitCode, error) = consumer.Run(Consumer.Generate("Out"), NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("error: TypeScript 5.9.3 is installed", error, StringComparison.Ordinal);
        Assert.Contains($"Run `npm install -D typescript@{Cli.TypeScriptPin}`.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTypeScriptExitsWith2()
    {
        using var consumer = new Consumer { TypeScript = null };
        var (exitCode, error) = consumer.Run(Consumer.Generate("Out"), NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains($"error: TypeScript isn't installed: the extractor needs TypeScript 6.x in your node_modules. Run `npm install -D typescript@{Cli.TypeScriptPin}`.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewDependencyVersionReExtracts()
    {
        using var consumer = new Consumer();
        string[] args = ["generate", "@acme/demo-widgets", "--api", "sync", "--namespace", "Acme.Widgets", "--out", "Out"];
        var (exitCode, error) = consumer.Run(args);
        Assert.True(exitCode == 0, error);
        var key = Path.Combine(consumer.Directory, "Out", ".interoper", "ir.key");
        Assert.EndsWith("\npackage @acme/demo-widgets@0.0.0\nevents.mapProperty\ndependency @acme/demo-maps@0.0.0\n", File.ReadAllText(key), StringComparison.Ordinal);
        Assert.Equal(0, consumer.Run(args, NoNode).ExitCode);

        var json = Path.Combine(consumer.Package, "package.json");
        File.WriteAllText(json, File.ReadAllText(json).Replace("\"version\": \"0.0.0\"", "\"version\": \"0.0.2\"", StringComparison.Ordinal));
        (exitCode, error) = consumer.Run(args, NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("IR: extracting @acme/demo-widgets@0.0.0", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileSystemErrorExitsWith2()
    {
        var outDir = seeded.OutWithCache("Unreadable");
        var key = Path.Combine(outDir, ".interoper", "ir.key");
        File.Delete(key);
        Directory.CreateDirectory(key); // a folder where the key goes: deleting it as a file fails
        var (exitCode, error) = Consumer.Run(Consumer.Generate("Unreadable"), NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("\nerror: ", error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheExtractorsTypeScriptMessageNamesThePin() =>
        Assert.Contains($"npm install -D typescript@{Cli.TypeScriptPin}", File.ReadAllText(Consumer.Repo("src", "interoper-extractor", "extract.mjs")), StringComparison.Ordinal);

    [Fact]
    public void TheTypeScriptPinIsTheExtractors()
    {
        using var package = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Consumer.Repo("src", "interoper-extractor", "package.json")));
        Assert.Equal(Cli.TypeScriptPin, package.RootElement.GetProperty("dependencies").GetProperty("typescript").GetString());
    }
}
