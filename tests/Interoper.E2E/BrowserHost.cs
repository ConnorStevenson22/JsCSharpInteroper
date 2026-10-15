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
        (host, BaseUrl) = await StartHostAsync();
        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync();
    }

    // Starts the test host on a free port, with extra configuration arguments (`--Key=value`) if given.
    internal static async Task<(Process Process, Uri BaseUrl)> StartHostAsync(params string[] arguments)
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
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        try
        {
            return (process, await ListeningUrlAsync(process, "The test host"));
        }
        catch
        {
            await StopAsync(process);
            throw;
        }
    }

    // Opens the page in a fresh context, waits for it to finish, and returns what it printed. The first browser
    // error fails the test at once. The page is the test host's, unless another app's base URL is given; it has a
    // minute to finish, unless the test gives it longer.
    public async Task<string> RunPageAsync(string path, Uri? baseUrl = null, TimeSpan? timeout = null)
    {
        await using var page = await OpenAsync(path, baseUrl);
        return await page.ResultAsync(timeout);
    }

    // Opens the page in a fresh context, for a test that drives it; browser errors fail the test. The page is the test
    // host's, unless another app's base URL is given.
    public async Task<BrowserPage> OpenAsync(string path, Uri? baseUrl = null)
    {
        // A fixed locale: .NET on WebAssembly rejects the POSIX one headless Chromium reports on Linux.
        var context = await Browser.NewContextAsync(new() { BaseURL = (baseUrl ?? BaseUrl).ToString(), Locale = "en-US" });
        var page = new BrowserPage(path, context, await context.NewPageAsync());
        await page.Page.GotoAsync(path);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        playwright?.Dispose();
        await StopAsync(host);
    }

    internal static async Task StopAsync(Process? process)
    {
        if (process is null) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
    }

    // Kestrel logs the port it bound; the output is drained for the app's whole life so logging never blocks.
    internal static async Task<Uri> ListeningUrlAsync(Process process, string name)
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
        lock (log) throw new InvalidOperationException($"{name} {why}:\n{log}");
    }

    private static string Metadata(string key) =>
        typeof(BrowserHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    [GeneratedRegex(@"Now listening on: (http://\S+)")]
    private static partial Regex ListeningOn();
}

/// <summary>A page in its own browser context. The first browser error it reports fails the test.</summary>
public sealed class BrowserPage : IAsyncDisposable
{
    private readonly string path;
    private readonly IBrowserContext context;
    private readonly TaskCompletionSource<string> error = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BrowserPage(string path, IBrowserContext context, IPage page)
    {
        (this.path, this.context, Page) = (path, context, page);
        page.Console += (_, m) => { if (m.Type == "error") error.TrySetResult(m.Text); };
        page.PageError += (_, e) => error.TrySetResult(e);
    }

    public IPage Page { get; }

    // Waits for the page's #result to be done and returns its text.
    public async Task<string> ResultAsync(TimeSpan? timeout = null)
    {
        // A minute by default: WebAssembly boots first.
        var done = Page.Locator("#result[data-done]").WaitForAsync(new() { Timeout = (float)(timeout ?? TimeSpan.FromMinutes(1)).TotalMilliseconds });
        if (await Task.WhenAny(done, error.Task) == error.Task) Assert.Fail($"{path}: {await error.Task}");
        await done;
        var result = await Page.Locator("#result").TextContentAsync() ?? "";
        ThrowIfFailed();
        return result;
    }

    public void ThrowIfFailed()
    {
        if (error.Task.IsCompleted) Assert.Fail($"{path}: {error.Task.Result}");
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
