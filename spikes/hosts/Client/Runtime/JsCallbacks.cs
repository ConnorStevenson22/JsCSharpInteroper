using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Microsoft.JSInterop;

namespace HostsSpike.Client.Runtime;

/// <summary>
/// One registry for every JS → .NET callback (events, watches, callback parameters).
/// JS reaches it through a single entry point per host instead of one DotNetObjectReference per callback.
/// </summary>
public static partial class JsCallbacks
{
    private static readonly ConcurrentDictionary<int, Action<string>> Handlers = new();
    private static int _next;

    public static int Register(Action<string> handler)
    {
        var id = Interlocked.Increment(ref _next);
        Handlers[id] = handler;
        return id;
    }

    public static void Unregister(int id) => Handlers.TryRemove(id, out _);

    internal static void Dispatch(int id, string payload)
    {
        if (Handlers.TryGetValue(id, out var handler)) handler(payload);
    }

    /// <summary>WebAssembly entry point: JS calls this synchronously.</summary>
    [JSExport, SupportedOSPlatform("browser")]
    public static void DispatchFromJs(int id, string payload) => Dispatch(id, payload);
}

/// <summary>Server and Hybrid entry point: one DotNetObjectReference per circuit/WebView.</summary>
public sealed class JsCallbackSink
{
    [JSInvokable]
    public void Dispatch(int id, string payload) => JsCallbacks.Dispatch(id, payload);
}
