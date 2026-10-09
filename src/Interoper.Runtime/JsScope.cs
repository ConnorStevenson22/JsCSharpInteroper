using System.Runtime.ExceptionServices;
using Microsoft.JSInterop;

namespace Interoper;

/// <summary>
/// Owns handles and .NET callbacks (design §3.2, R21); a component owns one, and uses it from its dispatcher.
/// Disposal releases every handle no other scope owns in one <c>release</c> op, then every callback. One type
/// serves both modes: <see cref="Dispose"/> sends through the sync seam (WebAssembly only),
/// <see cref="DisposeAsync"/> through the async seam.
/// </summary>
public sealed class JsScope(JsRealm realm) : IDisposable, IAsyncDisposable
{
    private readonly Lock gate = new(); // covers disposed, callbacks and this scope's ownership in the realm
    private readonly List<int> callbacks = [];
    private readonly List<JsBatch> batches = []; // open batches this scope began, flushed before its release
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
    /// The live object for a <c>{"$h","$t"}</c> handle, owned by this scope too: the same C# object for the same JS
    /// object, of the most-derived type registered for the class id (R19), read through any base.
    /// </summary>
    public T Wrap<T>(int handle, int classId) where T : JsObject
    {
        lock (gate)
        {
            ThrowIfDisposed(handle);
            return (T)Realm.Wrap(this, handle, classId);
        }
    }

    /// <summary>Owns a handle that has no live object, such as a subscription.</summary>
    public void Own(int handle)
    {
        lock (gate)
        {
            ThrowIfDisposed(handle);
            Realm.Own(this, handle);
        }
    }

    // A handle whose call was in flight when the scope was disposed is released, not leaked.
    private void ThrowIfDisposed(int handle)
    {
        if (disposed)
        {
            Realm.Abandon(handle);
            throw new ObjectDisposedException(nameof(JsScope));
        }
    }

    /// <summary>
    /// Starts a batch (async mode, design §3.3): until it is disposed, writes and void calls on this async flow are
    /// queued and their Tasks complete at once; a read sends the queue first, and disposal sends the rest, each in
    /// one round trip. Failed ops surface there as <see cref="JsBatchException"/>; the others still ran. Inside a
    /// batch, a nested one joins it. Sync mode doesn't batch; a scope with an open batch must be disposed with
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// Begin and dispose it in the same async method (<c>await using</c>): the batch covers that method's flow,
    /// so one begun in a helper method doesn't cover the helper's caller.
    /// </remarks>
    public IAsyncDisposable BeginBatch()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Realm.BeginBatch(this) is not { } batch)
            {
                return Joined.Instance;
            }
            batches.Add(batch);
            return batch;
        }
    }

    internal void EndBatch(JsBatch batch)
    {
        lock (gate)
        {
            batches.Remove(batch);
        }
    }

    /// <summary>Registers a callback (R21) that this scope releases; send it as <c>{"$f":id}</c>.</summary>
    public int Register(Func<string, string?> handler)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var id = Transport.Callbacks.Register(Dispatch is null ? handler : args => Dispatched(handler, args));
            callbacks.Add(id);
            return id;
        }
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
        // Before anything is forgotten: without a sync seam this throws, and DisposeAsync still releases everything.
        var sync = Realm.IsStarted ? SyncTransport : null;
        lock (gate)
        {
            if (batches.Count > 0)
            {
                throw new InvalidOperationException("A scope with an open batch must be disposed with DisposeAsync.");
            }
        }
        var (release, owned, _) = Take();
        try
        {
            if (release is not null)
            {
                JsEnvelope.Check(sync!.Invoke(release));
            }
        }
        finally
        {
            ReleaseCallbacks(owned);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var (release, owned, pending) = Take();
        try
        {
            // Queued ops go first: they may use handles this releases. Their errors surface after the release.
            ExceptionDispatchInfo? failed = null;
            foreach (var batch in pending)
            {
                try
                {
                    await batch.FlushAsync();
                }
                catch (Exception e)
                {
                    failed ??= ExceptionDispatchInfo.Capture(e);
                }
            }
            if (release is not null)
            {
                await Realm.ReleaseAsync(release);
            }
            failed?.Throw();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and every handle with it.
        }
        finally
        {
            ReleaseCallbacks(owned);
        }
    }

    // Marks the scope disposed and returns the op that releases its handles (null if none), its callbacks and its
    // open batches.
    // A scope that never reached JS (prerendering) has neither, and touches no transport.
    private (string? Release, int[] Callbacks, JsBatch[] Batches) Take()
    {
        lock (gate)
        {
            if (disposed)
            {
                return (null, [], []);
            }
            disposed = true;
            var handles = Realm.Forget(this);
            int[] owned = [.. callbacks];
            callbacks.Clear();
            return (handles.Count == 0 ? null : JsOp.Release(handles), owned, [.. batches]);
        }
    }

    private sealed class Joined : IAsyncDisposable
    {
        public static readonly Joined Instance = new();

        public ValueTask DisposeAsync() => default;
    }

    // After the release op: subscriptions are gone first, so JS raises no events into released callbacks (§3.1).
    private void ReleaseCallbacks(int[] owned)
    {
        foreach (var id in owned)
        {
            Transport.Callbacks.Release(id);
        }
    }
}
