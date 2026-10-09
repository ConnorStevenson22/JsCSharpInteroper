namespace Interoper;

/// <summary>
/// The JS side as one Blazor circuit, WebAssembly app or WebView sees it (design §3.1, §3.2): a started
/// transport, the live types its class ids map to, and the identity cache. <c>AddInteroper()</c> registers one
/// per DI scope; tests build one over <see cref="InMemoryTransport"/>.
/// </summary>
public sealed class JsRealm(Func<Task<IJsTransport>> start, JsClassRegistry classes) : IAsyncDisposable
{
    // The identity cache: handle id → the live object (weakly), and the scope that owns the handle.
    private readonly Dictionary<int, Entry> objects = [];
    private Task? starting;
    private IJsTransport? transport;

    public JsClassRegistry Classes { get; } = classes;

    /// <summary>The async seam. Throws <see cref="JsRealmUnavailableException"/> until the realm has started.</summary>
    public IJsTransport Transport => transport ?? throw new JsRealmUnavailableException();

    /// <summary>The sync seam: only <see cref="JsImportTransport"/> on WebAssembly provides it.</summary>
    public IJsSyncTransport SyncTransport => Transport as IJsSyncTransport ?? throw new InvalidOperationException(
        "Bindings generated in sync mode need .NET running in the browser (Blazor WebAssembly) on the JsImport " +
        "transport. On Server and Hybrid, use bindings generated in async mode: regenerate with `--api async`.");

    /// <summary>Starts the transport once; a failed start can be retried.</summary>
    public Task StartAsync()
    {
        if (starting is null or { IsFaulted: true } or { IsCanceled: true })
        {
            starting = StartTransportAsync();
        }
        return starting;
    }

    private async Task StartTransportAsync() => transport = await start();

    /// <summary>Releases a transport that owns resources (<see cref="JsRuntimeTransport"/>'s module and sink).</summary>
    public async ValueTask DisposeAsync()
    {
        if (transport is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>The live object for a handle: the cached one, or a new one of the class's most-derived type.</summary>
    internal JsObject Wrap(JsScope scope, int handle, int classId)
    {
        lock (objects)
        {
            if (objects.TryGetValue(handle, out var entry))
            {
                if (entry.Object?.TryGetTarget(out var live) == true)
                {
                    return live;
                }
            }
            else
            {
                // The first scope to receive a handle owns it, even if no live type can be made for it.
                objects.Add(handle, entry = new(scope));
            }
            var created = Classes.Create(entry.Owner, handle, classId);
            entry.Object = new(created);
            return created;
        }
    }

    /// <summary>Forgets every handle <paramref name="scope"/> owns and returns them, for one <c>release</c>.</summary>
    internal List<int> Forget(JsScope scope)
    {
        lock (objects)
        {
            var owned = objects.Where(p => p.Value.Owner == scope).Select(p => p.Key).ToList();
            owned.ForEach(handle => objects.Remove(handle));
            return owned;
        }
    }

    private sealed class Entry(JsScope owner)
    {
        public JsScope Owner { get; } = owner;

        public WeakReference<JsObject>? Object { get; set; }
    }
}

/// <summary>
/// A live call before the realm has started: while a component prerenders there is no JS realm (design §3.2).
/// </summary>
public sealed class JsRealmUnavailableException() : InvalidOperationException(
    "There is no JS realm yet: live JS calls need an interactive component (not prerendering), whose realm has started.");
