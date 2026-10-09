using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Interoper.E2E.BrowserHost))]

namespace Interoper.E2E;

/// <summary>
/// Starts the test host (tests/Interoper.E2E.Host) as its own process on a free port, and one headless Chromium
/// that every test opens pages in.
/// </summary>
public sealed partial class BrowserHost : IAsyncLifetime
{
    private Process? host;
    private IPlaywright? playwright;

    public Uri BaseUrl { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var directory = Metadata("HostDirectory");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { Path.Combine(directory, Metadata("HostAssembly")), "--urls", "http://127.0.0.1:0", "--contentRoot", directory },
            Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Development" }, // serves static web assets from the build output
        };
        host = Process.Start(start)!;
        BaseUrl = await ListeningUrlAsync(host);

        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync();
    }

    // Opens the page in a fresh context, waits for it to finish, and returns what it printed. The first browser
    // error fails the test at once.
    public async Task<string> RunPageAsync(string path)
    {
        // A fixed locale: .NET on WebAssembly rejects the POSIX one headless Chromium reports on Linux.
        await using var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl.ToString(), Locale = "en-US" });
        var page = await context.NewPageAsync();
        var error = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Console += (_, m) => { if (m.Type == "error") error.TrySetResult(m.Text); };
        page.PageError += (_, e) => error.TrySetResult(e);

        await page.GotoAsync(path);
        var done = page.Locator("#result[data-done]").WaitForAsync(new() { Timeout = 60_000 }); // WebAssembly boots first
        if (await Task.WhenAny(done, error.Task) == error.Task) Assert.Fail($"{path}: {await error.Task}");
        await done;
        var result = await page.Locator("#result").TextContentAsync() ?? "";

        if (error.Task.IsCompleted) Assert.Fail($"{path}: {await error.Task}");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        playwright?.Dispose();
        if (host is not null)
        {
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            host.Dispose();
        }
    }

    // Kestrel logs the port it bound; the output is drained for the host's whole life so logging never blocks.
    private static async Task<Uri> ListeningUrlAsync(Process process)
    {
        var url = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
            if (ListeningOn().Match(e.Data) is { Success: true } match) url.TrySetResult(new Uri(match.Groups[1].Value));
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = process.WaitForExitAsync();
        var first = await Task.WhenAny(url.Task, exited, Task.Delay(TimeSpan.FromSeconds(60)));
        if (first == url.Task) return await url.Task;
        var why = first == exited ? $"exited with code {process.ExitCode}" : "didn't start within 60 s";
        lock (log) throw new InvalidOperationException($"The test host {why}:\n{log}");
    }

    private static string Metadata(string key) =>
        typeof(BrowserHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    [GeneratedRegex(@"Now listening on: (http://\S+)")]
    private static partial Regex ListeningOn();
}
