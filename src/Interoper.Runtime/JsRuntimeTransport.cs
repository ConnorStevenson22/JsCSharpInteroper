using Microsoft.JSInterop;

namespace Interoper;

/// <summary>
/// <c>IJSRuntime</c>: Server (SignalR), Hybrid (the WebView bridge), or WebAssembly when forced. Async mode
/// only; every call is a round trip, so the typed fast paths are plain <c>get</c>/<c>set</c> ops.
/// </summary>
public sealed class JsRuntimeTransport : IJsTransport, IAsyncDisposable
{
    internal const string ModuleUrl = "/_content/Interoper.Runtime/interoper.js";

    private readonly IJSObjectReference module;
    private readonly DotNetObjectReference<JsCallbacks> sink;

    private JsRuntimeTransport(IJSObjectReference module, DotNetObjectReference<JsCallbacks> sink) =>
        (this.module, this.sink) = (module, sink);

    public JsCallbacks Callbacks => sink.Value;

    /// <summary>
    /// Imports its own instance of interoper.js and starts it with the generated metadata module. Each transport
    /// has its own handles, callbacks and census, even next to other transports on the same page.
    /// </summary>
    public static async Task<JsRuntimeTransport> StartAsync(IJSRuntime js, string metadataSpecifier)
    {
        // A unique query makes a separate module instance (Server circuits and WebAssembly can share a page).
        var module = await Call(js.InvokeAsync<IJSObjectReference>("import", $"{ModuleUrl}?t={Guid.NewGuid():N}"));
        var sink = DotNetObjectReference.Create(new JsCallbacks());
        var transport = new JsRuntimeTransport(module, sink);
        try
        {
            await Call(module.InvokeVoidAsync("startWithDotNetObject", metadataSpecifier, sink));
            return transport;
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    public Task<string> InvokeAsync(string opJson) => Call(module.InvokeAsync<string>("invoke", opJson));

    public Task<int> CensusAsync() => Call(module.InvokeAsync<int>("census"));

    public async Task<double> GetNumberAsync(int handle, string name) =>
        JsOp.Primitive(await InvokeAsync(JsOp.Get(handle, name)), name, JsJson.ReadNumber);

    public async Task<string> GetStringAsync(int handle, string name) =>
        JsOp.Primitive(await InvokeAsync(JsOp.Get(handle, name)), name, JsJson.ReadString);

    public async Task<bool> GetBooleanAsync(int handle, string name) =>
        JsOp.Primitive(await InvokeAsync(JsOp.Get(handle, name)), name, JsJson.ReadBoolean);

    public async Task SetNumberAsync(int handle, string name, double value) =>
        JsEnvelope.Check(await InvokeAsync(JsOp.Set(handle, name, value)));

    public async Task SetStringAsync(int handle, string name, string value) =>
        JsEnvelope.Check(await InvokeAsync(JsOp.Set(handle, name, value)));

    public async Task SetBooleanAsync(int handle, string name, bool value) =>
        JsEnvelope.Check(await InvokeAsync(JsOp.Set(handle, name, value)));

    /// <summary>
    /// Releases the sink and the module. Events that JS raises afterwards fail on the JS side, so release
    /// subscriptions first.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        sink.Dispose();
        try
        {
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the module with it.
        }
    }

    // invoke never rejects, so a JSException here means interoper.js itself failed (a failed import or start).
    // It becomes a JsException like every other JS error; JSDisconnectedException and timeouts pass through.
    private static async Task<T> Call<T>(ValueTask<T> call)
    {
        try
        {
            return await call;
        }
        catch (JSException e)
        {
            throw new JsException("Error", e.Message, null);
        }
    }

    private static async Task Call(ValueTask call)
    {
        try
        {
            await call;
        }
        catch (JSException e)
        {
            throw new JsException("Error", e.Message, null);
        }
    }
}
