using Microsoft.JSInterop;

namespace HostsSpike.Client.Runtime;

/// <summary>
/// Server (SignalR) and Hybrid (WebView bridge): the same JS module, reached through IJSRuntime.
/// Every call is a real round trip, so these ValueTasks complete asynchronously.
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

    public ValueTask<int> CreateAsync(string jsClass, string propertiesJson) => _module.InvokeAsync<int>("create", jsClass, propertiesJson);
    public ValueTask<double> GetNumberAsync(int handle, string property) => _module.InvokeAsync<double>("getNumber", handle, property);
    public ValueTask SetNumberAsync(int handle, string property, double value) => _module.InvokeVoidAsync("setNumber", handle, property, value);
    public ValueTask<string> SnapshotAsync(int handle, string properties) => _module.InvokeAsync<string>("snapshot", handle, properties);
    public ValueTask SubscribeAsync(int handle, string eventName, int callbackId) => _module.InvokeVoidAsync("subscribe", handle, eventName, callbackId);
    public ValueTask ReleaseAsync(int handle) => _module.InvokeVoidAsync("release", handle);
    public ValueTask RaiseAsync(int handle, string eventName, string payloadJson) => _module.InvokeVoidAsync("raise", handle, eventName, payloadJson);

    public async ValueTask DisposeAsync()
    {
        _sink.Dispose();
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { } // circuit already gone
    }
}
