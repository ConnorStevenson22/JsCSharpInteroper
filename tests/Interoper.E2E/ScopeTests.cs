namespace Interoper.E2E;

/// <summary>
/// Realm and scopes in the browser (design §3.1, §3.2): a component's scope disposal returns the census to its
/// baseline, prerendering has no realm, and a failed JsImport start can be retried.
/// </summary>
public sealed class ScopeTests(BrowserHost host)
{
    [Theory]
    [InlineData("server-scope")] // JsRuntimeTransport, one realm per circuit
    [InlineData("wasm-scope")] // the one JsImportTransport, shared by every scope
    [InlineData("auto-scope")] // InteractiveAuto: Server's realm on a first visit
    public async Task ScopeDisposalReturnsTheCensusToBaseline(string path)
    {
        using var http = new HttpClient { BaseAddress = host.BaseUrl };
        var prerendered = await http.GetStringAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("prerendered: JsRealmUnavailableException", prerendered, StringComparison.Ordinal);

        Assert.Equal("PASS scope disposal returns the census to baseline", await host.RunPageAsync(path));
    }

    [Fact]
    public async Task AFailedJsImportStartCanBeRetried()
    {
        Assert.Equal("PASS restarted, census 0", await host.RunPageAsync("wasm-restart"));
    }
}
