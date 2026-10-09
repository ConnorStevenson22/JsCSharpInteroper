using Interoper.Contract;

namespace Interoper.E2E;

/// <summary>
/// The golden slice (tests/GoldenSlice) in the browser (plan phase 1): each mode's suite on each host it supports, on
/// a realm built for the page's Interop:Transport setting. Each page runs every case and prints one line per case.
/// </summary>
public sealed class GoldenSliceTests(BrowserHost host)
{
    [Theory]
    [InlineData("golden-wasm-sync", true)] // sync mode on WebAssembly: Auto picks JsImport
    [InlineData("golden-wasm", false)] // async mode on WebAssembly: Auto picks JsImport
    [InlineData("golden-wasm-jsruntime", false)] // async mode on WebAssembly with Transport=JsRuntime: the Hybrid path
    [InlineData("golden-server", false)] // async mode on Server: Auto picks JsRuntime
    public async Task PassesItsSuite(string path, bool sync)
    {
        var cases = sync ? GoldenSyncSuite.Cases : GoldenAsyncSuite.Cases;
        Assert.Equal(cases.Select(c => $"PASS {c.Name}"), (await host.RunPageAsync(path)).Split('\n'));
    }

    [Fact]
    public async Task SyncBindingsFailFastOnServer()
    {
        var lines = (await host.RunPageAsync("golden-server-sync")).Split('\n');
        Assert.Equal(GoldenSyncSuite.Cases.Count, lines.Length);
        Assert.All(lines, line => Assert.Contains(": InvalidOperationException: Bindings generated in sync mode", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Contains("regenerate with `--api async`", line, StringComparison.Ordinal));
    }
}
