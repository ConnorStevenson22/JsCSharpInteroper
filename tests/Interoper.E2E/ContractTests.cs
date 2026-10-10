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
        Assert.Equal(expected, (await host.RunPageAsync(path)).Split('\n'));
    }

    [Fact]
    public async Task TwoTransportsOnOnePageAreIndependent()
    {
        Assert.Equal("PASS two transports on one page", await host.RunPageAsync("wasm-two-transports"));
    }

    [Fact] // R24: JS calls preventDefault() before the event leaves for .NET, so a later DOM listener sees it
    public async Task PreventDefaultWorksOnServer()
    {
        Assert.Equal("""prevented True {"type":"click"}, plain False {"type":"click"}, census +1""", await host.RunPageAsync("server-prevent-default"));
    }

    [Fact]
    public async Task SyncModeFailsFastOnServer()
    {
        var output = await host.RunPageAsync("server-sync");
        Assert.StartsWith(nameof(InvalidOperationException), output, StringComparison.Ordinal);
        Assert.Contains("regenerate with `--api async`", output, StringComparison.Ordinal);
    }
}
