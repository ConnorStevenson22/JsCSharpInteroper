using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using static Microsoft.Playwright.Assertions;

namespace Interoper.E2E;

/// <summary>
/// docs/quickstart.md, as written (design §5, §6): in a copy of samples/Quickstart, the doc's pack commands fill the
/// copy's packages/ folder, the doc's steps install, generate both modes, build and run the app, and the async page on
/// Server and the sync page on WebAssembly both change the map's zoom. The doc's commands are read from the doc, so the
/// two can't drift apart.
/// </summary>
public sealed class QuickstartTests(BrowserHost host)
{
    private static readonly string[] Packages = ["Interoper.Runtime", "Interoper.Blazor", "Interoper.Tool"];

    [Fact]
    public async Task TheQuickstartRunsAsWritten()
    {
        var doc = File.ReadAllText(Repo("docs", "quickstart.md"));
        var app = Directory.CreateTempSubdirectory("interoper-quickstart-").FullName;
        Process? server = null;
        try
        {
            CopyDirectory(Repo("samples", "Quickstart"), app);
            var feed = Path.Combine(app, "packages");
            foreach (var command in Commands(doc, "pack"))
            {
                await RunAsync(command.Replace("samples/Quickstart/packages", $"\"{feed}\"", StringComparison.Ordinal), Repo());
            }
            var version = PackedVersion(feed);
            CheckPackages(feed, version);
            // NuGet keeps the first copy of a version it restores, so the app restores into a cache of its own: an
            // earlier run's packages of the same version can't shadow these.
            var cache = Path.Combine(app, ".nuget-packages");

            var steps = Commands(doc, "steps");
            Assert.StartsWith("dotnet run ", steps[^1], StringComparison.Ordinal);
            foreach (var command in steps[..^1])
            {
                await RunAsync(command, app, cache);
            }
            // Referencing Interoper.Blazor brings the runtime's analyzer, IOP3001.
            foreach (var project in new[] { "Quickstart", "Quickstart.Client" })
            {
                var analyzers = await RunAsync($"dotnet msbuild {project} -t:ResolveLockFileAnalyzers -getItem:Analyzer", app, cache);
                // From the app's own packages folder, so these packages, not an earlier run's.
                var analyzer = Path.Combine(cache, "interoper.runtime", version, "analyzers", "dotnet", "cs", "Interoper.Analyzers.dll");
                Assert.Contains(analyzer.Replace('\\', '/'), analyzers.Replace("\\\\", "/", StringComparison.Ordinal).Replace('\\', '/'), StringComparison.Ordinal);
            }

            // The last step, on a free port, without building again.
            server = Start($"{steps[^1]} --no-build -- --urls http://127.0.0.1:0", app, cache);
            var url = await BrowserHost.ListeningUrlAsync(server, "The quickstart app");
            await ZoomsAsync("zoom-server", url);
            await ZoomsAsync("zoom-wasm", url);
        }
        finally
        {
            if (server is not null)
            {
                server.Kill(entireProcessTree: true);
                server.WaitForExit();
                server.Dispose();
            }
            try
            {
                Directory.Delete(app, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: it's a temp folder, and a failure here mustn't hide the test's own.
            }
        }
    }

    // The page shows the map's zoom, 3, and a click on Zoom in sets it and reads it back.
    private async Task ZoomsAsync(string path, Uri url)
    {
        await using var browser = await host.OpenAsync(path, url);
        var zoom = browser.Page.Locator("#zoom");
        await Expect(zoom).ToHaveTextAsync("zoom 3", new() { Timeout = 60_000 }); // WebAssembly boots first
        await browser.Page.ClickAsync("#zoom-in");
        await Expect(zoom).ToHaveTextAsync("zoom 4");
        browser.ThrowIfFailed();
    }

    // The analyzer ships inside the runtime, under analyzers/, not as a reference; interoper.js is a static web asset;
    // the tool has the extractor's source.
    private static void CheckPackages(string feed, string version)
    {
        string[] Files(string id)
        {
            using var zip = ZipFile.OpenRead(Path.Combine(feed, $"{id}.{version}.nupkg"));
            return [.. zip.Entries.Select(e => e.FullName)];
        }
        var runtime = Files("Interoper.Runtime");
        Assert.Contains("analyzers/dotnet/cs/Interoper.Analyzers.dll", runtime);
        Assert.Contains("lib/net11.0/Interoper.Runtime.dll", runtime);
        Assert.Contains("lib/net11.0/Interoper.Runtime.xml", runtime);
        Assert.Contains("staticwebassets/interoper.js", runtime);
        Assert.DoesNotContain(runtime, f => f.StartsWith("lib/", StringComparison.Ordinal) && f.Contains("Analyzers", StringComparison.Ordinal));
        var blazor = Files("Interoper.Blazor");
        Assert.Contains("lib/net11.0/Interoper.Blazor.dll", blazor);
        Assert.Contains("lib/net11.0/Interoper.Blazor.xml", blazor);
        var tool = Files("Interoper.Tool");
        Assert.Contains("tools/net11.0/any/Interoper.Generator.dll", tool);
        Assert.Contains("tools/net11.0/any/extractor/extract.mjs", tool);
        Assert.Contains("tools/net11.0/any/extractor/program.mjs", tool);
    }

    private static string PackedVersion(string feed)
    {
        var name = Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(feed, "Interoper.Runtime.*.nupkg")));
        var version = name["Interoper.Runtime.".Length..];
        foreach (var id in Packages)
        {
            Assert.True(File.Exists(Path.Combine(feed, $"{id}.{version}.nupkg")), $"{id} {version} wasn't packed");
        }
        return version;
    }

    // The commands of the fenced block below `<!-- QuickstartTests: {name} -->`.
    private static string[] Commands(string doc, string name)
    {
        var marker = doc.IndexOf($"<!-- QuickstartTests: {name} -->", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"docs/quickstart.md has no `{name}` block");
        var lines = doc[marker..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("```bash", lines[0]);
        return [.. lines.Skip(1).TakeWhile(l => l != "```").Where(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'))];
    }

    private static async Task<string> RunAsync(string command, string directory, string? nugetPackages = null)
    {
        using var process = Start(command, directory, nugetPackages);
        var output = new StringBuilder();
        void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null) lock (output) output.AppendLine(e.Data);
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (TestContext.Current.CancellationToken.IsCancellationRequested) throw;
            lock (output) Assert.Fail($"`{command}` didn't finish within 5 minutes:\n{output}");
        }
        process.WaitForExit(); // the output's last lines
        lock (output)
        {
            Assert.True(process.ExitCode == 0, $"`{command}` exited with code {process.ExitCode}:\n{output}");
            return output.ToString();
        }
    }

    // Through the shell, as a reader would type it. The child doesn't inherit the test run's MSBuild settings, which
    // would point a nested build at this repo's.
    private static Process Start(string command, string directory, string? nugetPackages)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd") { Arguments = $"/c {command}" } // cmd parses its own command line
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = directory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }
        if (nugetPackages is not null)
        {
            start.Environment["NUGET_PACKAGES"] = nugetPackages;
        }
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet)
        {
            start.Environment["PATH"] = Path.GetDirectoryName(dotnet) + Path.PathSeparator + start.Environment["PATH"];
        }
        return Process.Start(start)!;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            // What a previous in-place run left: build output, installed packages, generated bindings.
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules" or "packages" or ".nuget-packages" or "Maps")) continue;
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string Repo(params string[] parts) => Path.GetFullPath(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "..", "..");
}
