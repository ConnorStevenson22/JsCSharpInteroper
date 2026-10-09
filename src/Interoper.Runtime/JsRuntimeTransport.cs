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
    /// Imports interoper.js and starts it with the generated metadata module. One transport per page: JS has
    /// one callback sink.
    /// </summary>
    public static async Task<JsRuntimeTransport> StartAsync(IJSRuntime js, string metadataSpecifier)
    {
        var module = await Call(js.InvokeAsync<IJSObjectReference>("import", ModuleUrl));
        var sink = DotNetObjectReference.Create(new JsCallbacks());
        await Call(module.InvokeVoidAsync("startWithDotNetObject", metadataSpecifier, sink));
        return new JsRuntimeTransport(module, sink);
    }

    public Task<string> InvokeAsync(string opJson) => Call(module.InvokeAsync<string>("invoke", opJson));

    public Task<int> CensusAsync() => Call(module.InvokeAsync<int>("census"));

    public async Task<double> GetNumberAsync(int handle, string name) =>
        JsEnvelope.Read(await InvokeAsync(JsOp.Get(handle, name)), JsOp.ReadNumber);

    public async Task<string?> GetStringAsync(int handle, string name) =>
        JsEnvelope.Read(await InvokeAsync(JsOp.Get(handle, name)), JsOp.ReadString);

    public async Task<bool> GetBooleanAsync(int handle, string name) =>
        JsEnvelope.Read(await InvokeAsync(JsOp.Get(handle, name)), JsOp.ReadBoolean);

    public async Task SetNumberAsync(int handle, string name, double value) =>
        JsEnvelope.Check(await InvokeAsync(JsOp.Set(handle, name, value)));

    public async Task SetStringAsync(int handle, string name, string? value) =>
        JsEnvelope.Check(await InvokeAsync(JsOp.Set(handle, name, value)));

    public async Task SetBooleanAsync(int handle, string name, bool value) =>
        JsEnvelope.Check(await InvokeAsync(JsOp.Set(handle, name, value)));

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
