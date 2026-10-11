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
        Assert.Equal(expected, File.ReadAllText(Path.Combine(outDir, "interoper-report.txt")));
        Assert.EndsWith(expected, error, StringComparison.Ordinal);
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
    public void StaleGeneratedFilesAreDeletedAndOthersKept()
    {
        var outDir = seeded.OutWithCache("Stale");
        Directory.CreateDirectory(Path.Combine(outDir, "Gone"));
        File.WriteAllText(Path.Combine(outDir, "Old.cs"), $"{Bindings.Header}\n#nullable enable\n");
        File.WriteAllText(Path.Combine(outDir, "Gone", "Older.cs"), $"{Bindings.Header}\n");
        File.WriteAllText(Path.Combine(outDir, "old.meta.js"), $"{Bindings.Header}\nexport default {{}};\n");
        File.WriteAllText(Path.Combine(outDir, "MapView.Extensions.cs"), "namespace Acme.Maps;\n");
        File.WriteAllText(Path.Combine(outDir, "notes.txt"), $"{Bindings.Header}\n");

        var (exitCode, error) = Consumer.Run(Consumer.Generate("Stale"), NoNode);
        Assert.True(exitCode == 0, error);
        Assert.False(File.Exists(Path.Combine(outDir, "Old.cs")));
        Assert.False(File.Exists(Path.Combine(outDir, "old.meta.js")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "Gone")));
        Assert.True(File.Exists(Path.Combine(outDir, "MapView.Extensions.cs")));
        Assert.True(File.Exists(Path.Combine(outDir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, ".interoper", "ir.json")));
        Assert.Contains("3 deleted.", error, StringComparison.Ordinal);
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
    public void BadUsageExitsWith2(params string[] args)
    {
        var (exitCode, error) = Consumer.Run(args, NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains(Cli.Usage, error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Consumer.Directory, "Out")));
    }

    [Fact]
    public void HelpExitsWith0() => Assert.Equal((0, Cli.Usage + Environment.NewLine), Consumer.Run(["--help"]));

    [Fact]
    public void AMissingPackageExitsWith2()
    {
        var (exitCode, error) = Consumer.Run(["generate", "@acme/nowhere", "--api", "sync", "--namespace", "Acme", "--out", "Out"], NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("error: `@acme/nowhere` isn't installed: there's no node_modules/@acme/nowhere", error, StringComparison.Ordinal);
        Assert.Contains("Run `npm install @acme/nowhere`.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileForAnotherPackageExitsWith2()
    {
        seeded.OutWithCache("OtherProfile");
        var profile = Path.Combine(Consumer.Directory, "other.json");
        File.WriteAllText(profile, """{ "package": "@acme/other", "events": { "mapProperty": "$events" } }""");
        string[] args = ["generate", "@acme/demo-maps", "--api", "sync", "--namespace", "Acme", "--out", "OtherProfile", "--profile", "other.json"];
        var (exitCode, error) = Consumer.Run(args, NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("The profile is for @acme/other, the IR for @acme/demo-maps.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeScriptOtherThan6ExitsWith2()
    {
        using var consumer = new Consumer();
        var typeScript = Path.Combine(consumer.Directory, "node_modules", "typescript");
        Directory.Delete(typeScript);
        Directory.CreateDirectory(typeScript);
        File.WriteAllText(Path.Combine(typeScript, "package.json"), """{ "name": "typescript", "version": "5.9.3" }""");
        var (exitCode, error) = consumer.Run(Consumer.Generate("Out"), NoNode);
        Assert.Equal(2, exitCode);
        Assert.Contains("error: TypeScript 5.9.3 is installed", error, StringComparison.Ordinal);
        Assert.Contains($"Run `npm install -D typescript@{Cli.TypeScriptPin}`.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTypeScriptPinIsTheExtractors()
    {
        using var package = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Consumer.Repo("src", "interoper-extractor", "package.json")));
        Assert.Equal(Cli.TypeScriptPin, package.RootElement.GetProperty("dependencies").GetProperty("typescript").GetString());
    }
}
