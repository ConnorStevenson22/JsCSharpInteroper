using Microsoft.JSInterop;

namespace HostsSpike.Client.Runtime;

/// <summary>
/// Server (SignalR) and Hybrid (WebView bridge): the same JS module, reached through IJSRuntime.
/// Every call is a real round trip, so these Tasks complete asynchronously. Async mode only.
/// </summary>
public sealed class JsRuntimeTransport : IJsTransport, IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private readonly DotNetObjectReference<JsCallbackSink> _sink;

    private JsRuntimeTransport(IJSObjectReference module, DotNetObjectReference<JsCallbackSink> sink) =>
        (_module, _sink) = (module, sink);

    public string Name => "Server/Hybrid (IJSRuntime)";

    public static async Task<JsRuntimeTransport> CreateAsync(IJSRuntime js)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", "/interoper.js");
        var sink = DotNetObjectReference.Create(new JsCallbackSink());
        await module.InvokeVoidAsync("useDotNetSink", sink);
        return new JsRuntimeTransport(module, sink);
    }

    public Task<int> CreateAsync(string jsClass, string propertiesJson) => _module.InvokeAsync<int>("create", jsClass, propertiesJson).AsTask();
    public Task<double> GetNumberAsync(int handle, string property) => _module.InvokeAsync<double>("getNumber", handle, property).AsTask();
    public Task SetNumberAsync(int handle, string property, double value) => _module.InvokeVoidAsync("setNumber", handle, property, value).AsTask();
    public Task<string> SnapshotAsync(int handle, string properties) => _module.InvokeAsync<string>("snapshot", handle, properties).AsTask();
    public Task SubscribeAsync(int handle, string eventName, int callbackId) => _module.InvokeVoidAsync("subscribe", handle, eventName, callbackId).AsTask();
    public Task ReleaseAsync(int handle) => _module.InvokeVoidAsync("release", handle).AsTask();
    public Task RaiseAsync(int handle, string eventName, string payloadJson) => _module.InvokeVoidAsync("raise", handle, eventName, payloadJson).AsTask();

    public async ValueTask DisposeAsync()
    {
        _sink.Dispose();
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { } // circuit already gone
    }
}
