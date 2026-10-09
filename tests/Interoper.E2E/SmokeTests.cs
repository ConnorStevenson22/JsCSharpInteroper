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
        Assert.Equal(Transcript, await RunAsync(path));
    }

    [Fact]
    public async Task SyncModeFailsFastOnServer()
    {
        Assert.StartsWith(nameof(PlatformNotSupportedException), await RunAsync("server-sync"), StringComparison.Ordinal);
    }

    // Opens the page in a fresh context, waits for it to finish, and returns what it printed. The first browser
    // error fails the test at once.
    private async Task<string> RunAsync(string path)
    {
        // A fixed locale: .NET on WebAssembly rejects the POSIX one headless Chromium reports on Linux.
        await using var context = await host.Browser.NewContextAsync(new() { BaseURL = host.BaseUrl.ToString(), Locale = "en-US" });
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
}
