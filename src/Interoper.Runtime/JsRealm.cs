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
    // The batch the current async flow is inside, so other components' calls on the same realm don't join it.
    private readonly AsyncLocal<JsBatch?> batch = new();
    private long roundTrips;
    private Task? starting;
    private IJsTransport? transport;

    /// <summary>The async seam. Throws <see cref="JsRealmUnavailableException"/> until the realm has started.</summary>
    public IJsTransport Transport => transport ?? throw new JsRealmUnavailableException();

    /// <summary>The sync seam: only <see cref="JsImportTransport"/> on WebAssembly provides it.</summary>
    public IJsSyncTransport SyncTransport => Transport as IJsSyncTransport ?? throw new InvalidOperationException(
        "Bindings generated in sync mode need .NET running in the browser (Blazor WebAssembly) on the JsImport " +
        "transport. On Server and Hybrid, use bindings generated in async mode: regenerate with `--api async`.");

    internal bool IsStarted => transport is not null;

    /// <summary>
    /// The calls this realm has made through the async seam: ops, batches and fast paths. Each is a round trip on
    /// Server and Hybrid.
    /// </summary>
    public long RoundTrips => Interlocked.Read(ref roundTrips);

    /// <summary>Starts the transport once, and loads the registry's preloaded modules; a failed start can be retried.</summary>
    public Task StartAsync()
    {
        if (starting is null or { IsFaulted: true } or { IsCanceled: true })
        {
            starting = StartTransportAsync();
        }
        return starting;
    }

    private async Task StartTransportAsync()
    {
        var started = await start();
        if (classes.Modules.Length > 0)
        {
            JsEnvelope.Check(await started.InvokeAsync(JsOp.Load(classes.Modules)));
        }
        transport = started;
    }

    /// <summary>Releases a transport that owns resources (<see cref="JsRuntimeTransport"/>'s module and sink).</summary>
    public async ValueTask DisposeAsync()
    {
        if (transport is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    // --- Calls (sync mode) ---
    // Through the sync seam, which only WebAssembly's JsImportTransport has; elsewhere SyncTransport fails fast.

    /// <summary>Runs an op that returns nothing.</summary>
    public void Send(string opJson) => JsEnvelope.Check(SyncTransport.Invoke(opJson));

    /// <summary>Runs an op and reads its result (<c>default</c> for undefined).</summary>
    public T? Call<T>(string opJson, JsonRead<T> read) => JsEnvelope.Read(SyncTransport.Invoke(opJson), read);

    public double GetNumber(int handle, string name) => SyncTransport.GetNumber(handle, name);

    public string GetString(int handle, string name) => SyncTransport.GetString(handle, name);

    public bool GetBoolean(int handle, string name) => SyncTransport.GetBoolean(handle, name);

    public void Set(int handle, string name, double value) => SyncTransport.SetNumber(handle, name, value);

    public void Set(int handle, string name, string value) => SyncTransport.SetString(handle, name, value);

    public void Set(int handle, string name, bool value) => SyncTransport.SetBoolean(handle, name, value);

    // --- Calls (async mode) ---
    // Generated code sends ops that return nothing through SendAsync and SetAsync, which a batch queues, and
    // everything else through CallAsync and the Get*Async fast paths, which send the queue first (design §3.3).

    /// <summary>Runs an op that returns nothing (a write or a void call); inside a batch it is queued.</summary>
    public Task SendAsync(string opJson) => batch.Value?.TryAdd(opJson) == true ? Task.CompletedTask : CheckAsync(opJson);

    /// <summary>Runs an op and reads its result (<c>default</c> for undefined), after any queued ops.</summary>
    public async Task<T?> CallAsync<T>(string opJson, JsonRead<T> read)
    {
        await FlushAsync();
        return JsEnvelope.Read(await InvokeAsync(opJson), read);
    }

    public Task<double> GetNumberAsync(int handle, string name) =>
        batch.Value is null ? Trip().GetNumberAsync(handle, name) : AfterFlush(() => Trip().GetNumberAsync(handle, name));

    public Task<string> GetStringAsync(int handle, string name) =>
        batch.Value is null ? Trip().GetStringAsync(handle, name) : AfterFlush(() => Trip().GetStringAsync(handle, name));

    public Task<bool> GetBooleanAsync(int handle, string name) =>
        batch.Value is null ? Trip().GetBooleanAsync(handle, name) : AfterFlush(() => Trip().GetBooleanAsync(handle, name));

    public Task SetAsync(int handle, string name, double value) =>
        batch.Value?.TryAdd(JsOp.Set(handle, name, value)) == true ? Task.CompletedTask : Trip().SetNumberAsync(handle, name, value);

    public Task SetAsync(int handle, string name, string value) =>
        batch.Value?.TryAdd(JsOp.Set(handle, name, value)) == true ? Task.CompletedTask : Trip().SetStringAsync(handle, name, value);

    public Task SetAsync(int handle, string name, bool value) =>
        batch.Value?.TryAdd(JsOp.Set(handle, name, value)) == true ? Task.CompletedTask : Trip().SetBooleanAsync(handle, name, value);

    private async Task<T> AfterFlush<T>(Func<Task<T>> read)
    {
        await FlushAsync();
        return await read();
    }

    private Task FlushAsync() => batch.Value?.FlushAsync() ?? Task.CompletedTask;

    private IJsTransport Trip()
    {
        var seam = Transport;
        Interlocked.Increment(ref roundTrips);
        return seam;
    }

    internal Task<string> InvokeAsync(string opJson) => Trip().InvokeAsync(opJson);

    internal async Task CheckAsync(string opJson) => JsEnvelope.Check(await InvokeAsync(opJson));

    /// <summary>
    /// Sends a <c>release</c> after the current flow's queue, which may use those handles (a scope disposed inside
    /// another scope's batch). Releases even if the flush fails, then throws.
    /// </summary>
    internal async Task ReleaseAsync(string releaseOp)
    {
        try
        {
            await FlushAsync();
        }
        finally
        {
            await CheckAsync(releaseOp);
        }
    }

    /// <summary>Starts a batch on the caller's async flow, or joins the one it is already in (null).</summary>
    internal JsBatch? BeginBatch(JsScope scope)
    {
        if (batch.Value is { IsOpen: true })
        {
            return null;
        }
        _ = Transport; // fails before the realm starts, like any live call
        return batch.Value = new JsBatch(this, scope);
    }

    internal void EndBatch(JsBatch ended)
    {
        if (batch.Value == ended)
        {
            batch.Value = null;
        }
    }

    /// <summary>
    /// The live object for a handle, now also owned by <paramref name="scope"/>: the cached one, or a new one of the
    /// class's most-derived type.
    /// </summary>
    internal JsObject Wrap(JsScope scope, int handle, int classId) => Wrap(scope, handle, classId, null);

    /// <summary>The same, of the type <paramref name="create"/> makes: a generic one, which the registry can't make.</summary>
    internal JsObject Wrap(JsScope scope, int handle, Func<JsRealm, int, JsObject> create) => Wrap(scope, handle, 0, create);

    private JsObject Wrap(JsScope scope, int handle, int classId, Func<JsRealm, int, JsObject>? create)
    {
        lock (objects)
        {
            // Owned even if no live type can be made for it, so the scope still releases it.
            var entry = Own(scope, handle);
            if (entry.Object?.TryGetTarget(out var live) == true)
            {
                return live;
            }
            var created = create is null ? classes.Create(this, handle, classId) : create(this, handle);
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

    /// <summary>Whether <paramref name="scope"/> is the only owner of a handle.</summary>
    internal bool OwnsAlone(JsScope scope, int handle)
    {
        lock (objects)
        {
            return objects.TryGetValue(handle, out var entry) && entry.Owners.Count == 1 && entry.Owners.Contains(scope);
        }
    }

    /// <summary>Removes <paramref name="scope"/> from the owners of a handle, once its release has gone out.</summary>
    internal void Disown(JsScope scope, int handle)
    {
        lock (objects)
        {
            if (objects.TryGetValue(handle, out var entry) && entry.Owners.Remove(scope) && entry.Owners.Count == 0)
            {
                objects.Remove(handle);
            }
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
        _ = AbandonAsync(handle);
    }

    private async Task AbandonAsync(int handle)
    {
        try
        {
            await ReleaseAsync(JsOp.Release([handle]));
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the handle with it. Other failures are lost: nobody awaits a background release.
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
