using Microsoft.JSInterop;

namespace Interoper;

/// <summary>
/// The JS side as one Blazor circuit, WebAssembly app or WebView sees it (design §3.1, §3.2): a started
/// transport, the live types its class ids map to, and the identity cache. <c>AddInteroper()</c> registers one
/// per DI scope; tests build one over <see cref="InMemoryTransport"/>.
/// </summary>
public sealed class JsRealm(Func<Task<IJsTransport>> start, JsClassRegistry classes) : IAsyncDisposable
{
    // The identity cache: handle id → the live object (weakly), and the scopes that own the handle.
    private readonly Dictionary<int, Entry> objects = [];
    private Task? starting;
    private IJsTransport? transport;

    /// <summary>The async seam. Throws <see cref="JsRealmUnavailableException"/> until the realm has started.</summary>
    public IJsTransport Transport => transport ?? throw new JsRealmUnavailableException();

    /// <summary>The sync seam: only <see cref="JsImportTransport"/> on WebAssembly provides it.</summary>
    public IJsSyncTransport SyncTransport => Transport as IJsSyncTransport ?? throw new InvalidOperationException(
        "Bindings generated in sync mode need .NET running in the browser (Blazor WebAssembly) on the JsImport " +
        "transport. On Server and Hybrid, use bindings generated in async mode: regenerate with `--api async`.");

    internal bool IsStarted => transport is not null;

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

    /// <summary>
    /// The live object for a handle, now also owned by <paramref name="scope"/>: the cached one, or a new one of the
    /// class's most-derived type.
    /// </summary>
    internal JsObject Wrap(JsScope scope, int handle, int classId)
    {
        lock (objects)
        {
            // Owned even if no live type can be made for it, so the scope still releases it.
            var entry = Own(scope, handle);
            if (entry.Object?.TryGetTarget(out var live) == true)
            {
                return live;
            }
            var created = classes.Create(this, handle, classId);
            entry.Object = new(created);
            return created;
        }
    }

    /// <summary>Adds <paramref name="scope"/> to the owners of a handle.</summary>
    internal Entry Own(JsScope scope, int handle)
    {
        lock (objects)
        {
            if (!objects.TryGetValue(handle, out var entry))
            {
                objects.Add(handle, entry = new());
            }
            entry.Owners.Add(scope);
            return entry;
        }
    }

    /// <summary>
    /// Removes <paramref name="scope"/> from every handle it owns, and returns the handles no scope owns any more,
    /// for one <c>release</c>.
    /// </summary>
    internal List<int> Forget(JsScope scope)
    {
        lock (objects)
        {
            var unowned = new List<int>();
            foreach (var (handle, entry) in objects)
            {
                if (entry.Owners.Remove(scope) && entry.Owners.Count == 0)
                {
                    unowned.Add(handle);
                }
            }
            unowned.ForEach(handle => objects.Remove(handle));
            return unowned;
        }
    }

    /// <summary>
    /// Releases a handle that reached a disposed scope (its call was in flight), unless another scope owns it.
    /// Fire and forget: the caller gets <see cref="ObjectDisposedException"/>.
    /// </summary>
    internal void Abandon(int handle)
    {
        lock (objects)
        {
            if (objects.ContainsKey(handle))
            {
                return;
            }
        }
        _ = ReleaseAsync(handle);
    }

    private async Task ReleaseAsync(int handle)
    {
        try
        {
            await Transport.InvokeAsync(JsOp.Release([handle]));
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the handle with it.
        }
    }

    internal sealed class Entry
    {
        public HashSet<JsScope> Owners { get; } = [];

        public WeakReference<JsObject>? Object { get; set; }
    }
}

/// <summary>
/// A live call before the realm has started: while a component prerenders there is no JS realm (design §3.2).
/// </summary>
public sealed class JsRealmUnavailableException() : InvalidOperationException(
    "There is no JS realm yet: live JS calls need an interactive component (not prerendering), whose realm has started.");
