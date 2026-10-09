using Microsoft.Playwright;

namespace Interoper.E2E;

/// <summary>
/// One page per mode and host (design §3.1, §6). Each page plays the same protocol scenario against
/// fixtures/demo-maps through interoper.js (design §3.4) and prints the responses.
/// </summary>
public sealed class SmokeTests(BrowserHost host)
{
    private const string Transcript = """
        {}
        {"v":{"$h":1,"$t":5}}
        {}
        {"v":7}
        {"v":{"$c":2,"type":"point","x":1,"y":2}}
        {}
        census 0
        """;

    [Theory]
    [InlineData("wasm-sync")] // sync mode on WebAssembly: [JSImport] → invokeSync
    [InlineData("wasm")] // async mode on WebAssembly: [JSImport] → invoke
    [InlineData("wasm-jsruntime")] // async mode on WebAssembly through IJSRuntime: the Server/Hybrid path
    [InlineData("server")] // async mode on Server: IJSRuntime → invoke
    public async Task RunsTheProtocol(string path)
    {
        var (result, resources) = await RunAsync(path);

        Assert.Equal(Transcript, result);
        Assert.Contains(resources, url => url.Contains("/_content/Interoper.Runtime/interoper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncModeFailsFastOnServer()
    {
        var (result, _) = await RunAsync("server-sync");

        Assert.StartsWith(nameof(PlatformNotSupportedException), result, StringComparison.Ordinal);
    }

    // Opens the page in a fresh context, waits for it to finish, and returns what it printed and the resources
    // it loaded. Any browser error fails the test.
    private async Task<(string Result, string[] Resources)> RunAsync(string path)
    {
        // A fixed locale: .NET on WebAssembly rejects the POSIX one headless Chromium reports on Linux.
        await using var context = await host.Browser.NewContextAsync(new() { BaseURL = host.BaseUrl.ToString(), Locale = "en-US" });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);

        await page.GotoAsync(path);
        try
        {
            await page.Locator("#result[data-done]").WaitForAsync(new() { Timeout = 60_000 }); // WebAssembly boots first
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{path} didn't finish. Browser errors:\n{string.Join("\n", errors)}", e);
        }
        var result = await page.Locator("#result").TextContentAsync() ?? "";
        var resources = await page.EvaluateAsync<string[]>("performance.getEntriesByType('resource').map(e => e.name)");

        Assert.Empty(errors);
        return (result, resources);
    }
}
