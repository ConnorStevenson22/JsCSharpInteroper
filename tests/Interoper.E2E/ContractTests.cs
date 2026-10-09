using Interoper.Contract;

namespace Interoper.E2E;

/// <summary>
/// The transport contract (tests/Interoper.Runtime.Tests/TransportContract.cs) through each seam on each host
/// (design §3.1, §6), against fixtures/demo-maps. Each page runs every case and prints one line per case.
/// </summary>
public sealed class ContractTests(BrowserHost host)
{
    [Theory]
    [InlineData("wasm-sync")] // sync mode on WebAssembly: JsImportTransport → invokeSync
    [InlineData("wasm")] // async mode on WebAssembly: JsImportTransport → invoke
    [InlineData("wasm-jsruntime")] // async mode on WebAssembly through IJSRuntime: the Server/Hybrid path
    [InlineData("server")] // async mode on Server: JsRuntimeTransport
    public async Task PassesTheContract(string path)
    {
        var expected = TransportContract.Cases.Select(c => $"PASS {c.Name}");
        Assert.Equal(expected, (await RunAsync(path)).Split('\n'));
    }

    [Fact]
    public async Task TwoTransportsOnOnePageAreIndependent()
    {
        Assert.Equal("PASS two transports on one page", await RunAsync("wasm-two-transports"));
    }

    [Fact]
    public async Task SyncModeFailsFastOnServer()
    {
        var output = await RunAsync("server-sync");
        Assert.StartsWith(nameof(InvalidOperationException), output, StringComparison.Ordinal);
        Assert.Contains("regenerate with `--api async`", output, StringComparison.Ordinal);
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
