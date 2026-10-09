using System.Collections.Concurrent;
using Microsoft.JSInterop;

namespace Interoper;

/// <summary>
/// The callback registry (R21): .NET handlers that JS reaches as <c>{"$f":id}</c>. A handler takes the
/// arguments as a JSON array (by value, design §3.4) and returns its result as JSON, or null for undefined.
/// One registry per transport, so one Server circuit can't reach another's handlers.
/// </summary>
public sealed class JsCallbacks
{
    private readonly ConcurrentDictionary<int, Func<string, string?>> handlers = new();
    private int lastId;

    /// <summary>The number of registered handlers, for leak checks.</summary>
    public int Count => handlers.Count;

    /// <summary>Registers a handler; send it as <c>{"$f":id}</c>, and release it with its owner.</summary>
    public int Register(Func<string, string?> handler)
    {
        var id = Interlocked.Increment(ref lastId);
        handlers[id] = handler;
        return id;
    }

    public void Release(int id) => handlers.TryRemove(id, out _);

    /// <summary>
    /// The sink JS calls: directly on WebAssembly, through a <see cref="DotNetObjectReference"/> elsewhere.
    /// It runs where the host delivers it: on JS's thread on WebAssembly, on the circuit's dispatcher on Server.
    /// Posting to a component's own dispatcher hooks in here.
    /// </summary>
    [JSInvokable]
    public string? InvokeCallback(int id, string argsJson) =>
        // A released handler can still be called (an event already on its way): JS gets undefined.
        handlers.TryGetValue(id, out var handler) ? handler(argsJson) : null;
}
