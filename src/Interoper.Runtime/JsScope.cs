using Microsoft.JSInterop;

namespace Interoper;

/// <summary>
/// Owns handles and .NET callbacks (design §3.2, R21); a component owns one. Disposal releases every handle in
/// one <c>release</c> op, and every callback. One type serves both modes: <see cref="Dispose"/> sends through
/// the sync seam (WebAssembly only), <see cref="DisposeAsync"/> through the async seam.
/// </summary>
public sealed class JsScope(JsRealm realm) : IDisposable, IAsyncDisposable
{
    private readonly List<int> callbacks = [];
    private bool disposed;

    public JsRealm Realm { get; } = realm;

    /// <summary>
    /// Where callbacks run: a component's dispatcher. Null runs them where the host delivers them.
    /// </summary>
    public Func<Action, Task>? Dispatch { get; init; }

    /// <inheritdoc cref="JsRealm.Transport"/>
    public IJsTransport Transport => Realm.Transport;

    /// <inheritdoc cref="JsRealm.SyncTransport"/>
    public IJsSyncTransport SyncTransport => Realm.SyncTransport;

    /// <summary>
    /// The live object for a <c>{"$h","$t"}</c> handle: the same C# object for the same JS object, of the most-derived
    /// type registered for the class id (R19), read through any base. A new handle belongs to this scope.
    /// </summary>
    public T Wrap<T>(int handle, int classId) where T : JsObject
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return (T)Realm.Wrap(this, handle, classId);
    }

    /// <summary>Registers a callback (R21) that this scope releases; send it as <c>{"$f":id}</c>.</summary>
    public int Register(Func<string, string?> handler)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = Transport.Callbacks.Register(Dispatch is null ? handler : args => Dispatched(handler, args));
        lock (callbacks)
        {
            callbacks.Add(id);
        }
        return id;
    }

    // Run inline (always on WebAssembly, or when already on the dispatcher), JS gets the result as without a
    // dispatcher. Posted for later, JS has moved on and gets undefined.
    private string? Dispatched(Func<string, string?> handler, string args)
    {
        string? result = null;
        var run = Dispatch!(() => result = handler(args));
        if (!run.IsCompleted)
        {
            return null;
        }
        run.GetAwaiter().GetResult();
        return result;
    }

    public void Dispose()
    {
        if (Take() is { } release)
        {
            JsEnvelope.Check(SyncTransport.Invoke(release));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Take() is not { } release)
        {
            return;
        }
        try
        {
            JsEnvelope.Check(await Transport.InvokeAsync(release));
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and every handle with it.
        }
    }

    // Marks the scope disposed, releases its callbacks, and returns the op that releases its handles (null if none).
    // A scope that never reached JS (prerendering) touches no transport.
    private string? Take()
    {
        if (disposed)
        {
            return null;
        }
        disposed = true;
        lock (callbacks)
        {
            foreach (var id in callbacks)
            {
                Transport.Callbacks.Release(id);
            }
            callbacks.Clear();
        }
        var handles = Realm.Forget(this);
        return handles.Count == 0 ? null : JsOp.Release(handles);
    }
}
