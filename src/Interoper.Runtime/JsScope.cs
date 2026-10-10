using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    private readonly List<Action> streams = []; // completes each open stream (StreamAsync)
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

    /// <summary>
    /// Reads a <c>{"$h","$t"}</c> handle and wraps it (<see cref="Wrap{T}"/>): the reader for live results in
    /// generated code.
    /// </summary>
    public T Read<T>(ref Utf8JsonReader reader) where T : JsObject
    {
        var (handle, classId) = ReadHandle(ref reader);
        return Wrap<T>(handle, classId);
    }

    /// <summary>
    /// Reads a handle of a live type the registry can't make, a generic one such as <c>Collection&lt;Marker&gt;</c>
    /// (mapping §2): the cached object for the handle, or the one <paramref name="create"/> makes, owned by this scope too.
    /// </summary>
    public T Read<T>(ref Utf8JsonReader reader, Func<JsRealm, int, T> create) where T : JsObject
    {
        var (handle, _) = ReadHandle(ref reader);
        lock (gate)
        {
            ThrowIfDisposed(handle);
            return (T)Realm.Wrap(this, handle, create);
        }
    }

    private static (int Handle, int ClassId) ReadHandle(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a handle {{\"$h\",\"$t\"}}, got {reader.TokenType}");
        }
        int? handle = null;
        var classId = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.ValueTextEquals("$h"u8) ? 'h' : reader.ValueTextEquals("$t"u8) ? 't' : ' ';
            reader.Read();
            switch (property)
            {
                case 'h': handle = reader.GetInt32(); break;
                case 't': classId = reader.GetInt32(); break;
                default: reader.Skip(); break;
            }
        }
        return handle is { } h ? (h, classId) : throw new JsonException("Expected a handle: the object has no \"$h\"");
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

    // --- Callbacks (R21) ---
    // A .NET delegate JS can call: registered with this scope, which releases it, and sent as {"$f":id}
    // (JsJson.WriteCallback). Each argument is read with its reader, with this scope, which owns the handles in them;
    // a missing one is undefined. A result is written with its writer; an Action's is undefined.

    public int Callback(Action handler) => Register(_ =>
    {
        handler();
        return null;
    });

    public int Callback<T1>(Action<T1> handler, JsonRead<T1> read1) => Register(args =>
    {
        var r = Arguments(args);
        handler(Next(ref r, read1));
        return null;
    });

    public int Callback<T1, T2>(Action<T1, T2> handler, JsonRead<T1> read1, JsonRead<T2> read2) => Register(args =>
    {
        var r = Arguments(args);
        handler(Next(ref r, read1), Next(ref r, read2));
        return null;
    });

    public int Callback<T1, T2, T3>(Action<T1, T2, T3> handler, JsonRead<T1> read1, JsonRead<T2> read2, JsonRead<T3> read3) => Register(args =>
    {
        var r = Arguments(args);
        handler(Next(ref r, read1), Next(ref r, read2), Next(ref r, read3));
        return null;
    });

    public int Callback<T1, T2, T3, T4>(Action<T1, T2, T3, T4> handler, JsonRead<T1> read1, JsonRead<T2> read2, JsonRead<T3> read3,
        JsonRead<T4> read4) => Register(args =>
    {
        var r = Arguments(args);
        handler(Next(ref r, read1), Next(ref r, read2), Next(ref r, read3), Next(ref r, read4));
        return null;
    });

    public int Callback<TResult>(Func<TResult> handler, JsonWrite<TResult> write) => Register(_ => Result(write, handler()));

    public int Callback<T1, TResult>(Func<T1, TResult> handler, JsonRead<T1> read1, JsonWrite<TResult> write) => Register(args =>
    {
        var r = Arguments(args);
        return Result(write, handler(Next(ref r, read1)));
    });

    public int Callback<T1, T2, TResult>(Func<T1, T2, TResult> handler, JsonRead<T1> read1, JsonRead<T2> read2, JsonWrite<TResult> write) =>
        Register(args =>
        {
            var r = Arguments(args);
            return Result(write, handler(Next(ref r, read1), Next(ref r, read2)));
        });

    public int Callback<T1, T2, T3, TResult>(Func<T1, T2, T3, TResult> handler, JsonRead<T1> read1, JsonRead<T2> read2, JsonRead<T3> read3,
        JsonWrite<TResult> write) => Register(args =>
    {
        var r = Arguments(args);
        return Result(write, handler(Next(ref r, read1), Next(ref r, read2), Next(ref r, read3)));
    });

    public int Callback<T1, T2, T3, T4, TResult>(Func<T1, T2, T3, T4, TResult> handler, JsonRead<T1> read1, JsonRead<T2> read2,
        JsonRead<T3> read3, JsonRead<T4> read4, JsonWrite<TResult> write) => Register(args =>
    {
        var r = Arguments(args);
        return Result(write, handler(Next(ref r, read1), Next(ref r, read2), Next(ref r, read3), Next(ref r, read4)));
    });

    // The arguments arrive as a JSON array, by value (design §3.4); the reader starts on its `[`.
    private static Utf8JsonReader Arguments(string args)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(args));
        reader.Read();
        return reader;
    }

    // The next argument; past the last one, undefined.
    private static T Next<T>(ref Utf8JsonReader reader, JsonRead<T> read)
    {
        if (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            return read(ref reader);
        }
        var undefined = new Utf8JsonReader("{\"$u\":1}"u8);
        undefined.Read();
        return read(ref undefined);
    }

    private static string Result<T>(JsonWrite<T> write, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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

    /// <summary>
    /// Subscribes through the sync seam (sync mode): calls <c>target[method](name, listener)</c> (R24) with a listener
    /// that reads the first argument and calls <paramref name="handler"/>. The subscription and the listener belong to
    /// this scope; disposing the result releases both sooner. JS applies <paramref name="options"/> first; their
    /// <see cref="EventOptions.Fields"/> replace <paramref name="fields"/>, the generated default projection.
    /// </summary>
    public IDisposable Subscribe<T>(int handle, string method, string name, JsonRead<T> read, Action<T> handler, EventOptions? options,
        IReadOnlyList<string>? fields)
    {
        var listener = Register(Listener(read, handler));
        int subscription;
        try
        {
            subscription = (int)Realm.Call(JsOp.Subscribe(handle, method, name, listener, Projected(options, fields)), JsJson.ReadNumber);
        }
        catch
        {
            ReleaseListener(listener);
            throw;
        }
        Own(subscription);
        return new Subscription(this, subscription, listener);
    }

    /// <summary>Subscribes through the async seam (async mode); see <see cref="Subscribe{T}"/>.</summary>
    public Task<IAsyncDisposable> SubscribeAsync<T>(int handle, string method, string name, JsonRead<T> read, Action<T> handler,
        EventOptions? options, IReadOnlyList<string>? fields) =>
        SubscribeAsync(handle, method, name, Listener(read, handler), options, fields);

    private async Task<IAsyncDisposable> SubscribeAsync(int handle, string method, string name, Func<string, string?> callback,
        EventOptions? options, IReadOnlyList<string>? fields)
    {
        var listener = Register(callback);
        int subscription;
        try
        {
            subscription = (int)await Realm.CallAsync(JsOp.Subscribe(handle, method, name, listener, Projected(options, fields)), JsJson.ReadNumber);
        }
        catch
        {
            ReleaseListener(listener);
            throw;
        }
        Own(subscription);
        return new Subscription(this, subscription, listener);
    }

    /// <summary>
    /// The events or changes of <see cref="SubscribeAsync{T}"/> as a stream (async mode, R24). It subscribes when the
    /// enumeration starts, and unsubscribes when it ends: on <c>break</c>, cancellation or disposal. Disposing the scope
    /// completes it, even while it subscribes, so <c>await foreach</c> ends normally; a payload that can't be read ends it
    /// with that error. JS can't be paused, so values queue without bound until read.
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(int handle, string method, string name, JsonRead<T> read, EventOptions? options,
        IReadOnlyList<string>? fields, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<T>(new() { SingleReader = true });
        Action complete = () => channel.Writer.TryComplete();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            streams.Add(complete);
        }
        try
        {
            var deliver = Listener<T>(read, value => channel.Writer.TryWrite(value));
            IAsyncDisposable subscription;
            try
            {
                subscription = await SubscribeAsync(handle, method, name, args =>
                {
                    try
                    {
                        return deliver(args);
                    }
                    catch (Exception e)
                    {
                        channel.Writer.TryComplete(e);
                        return null;
                    }
                }, options, fields);
            }
            catch (ObjectDisposedException) when (channel.Reader.Completion.IsCompleted)
            {
                yield break; // the scope was disposed while JS subscribed
            }
            await using (subscription)
            {
                await foreach (var value in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return value;
                }
            }
        }
        finally
        {
            lock (gate)
            {
                streams.Remove(complete);
            }
        }
    }

    // What JS applies before the listener (R24): the options, projecting to the default fields unless they name others.
    private static EventOptions? Projected(EventOptions? options, IReadOnlyList<string>? fields) =>
        options?.Fields is not null || fields is null ? options : (options ?? new()) with { Fields = fields };

    // Events and watches use the first argument (R24).
    private static Func<string, string?> Listener<T>(JsonRead<T> read, Action<T> handler) => args =>
    {
        var reader = Arguments(args);
        handler(Next(ref reader, read));
        return null;
    };

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

    // Marks the scope disposed, completes its open streams, and returns the op that releases its handles (null if none),
    // its callbacks and its open batches.
    // A scope that never reached JS (prerendering) has neither, and touches no transport.
    private (string? Release, int[] Callbacks, JsBatch[] Batches) Take()
    {
        (string?, int[], JsBatch[]) taken;
        Action[] open;
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
            open = [.. streams];
            streams.Clear();
            taken = (handles.Count == 0 ? null : JsOp.Release(handles), owned, [.. batches]);
        }
        foreach (var complete in open)
        {
            complete(); // what's queued is still read, then the enumeration ends
        }
        return taken;
    }

    private void ReleaseListener(int listener)
    {
        lock (gate)
        {
            callbacks.Remove(listener);
        }
        Transport.Callbacks.Release(listener);
    }

    // A subscription disposed before its scope: whether it is still this scope's, and the op that releases its handle
    // (null when another scope owns it too). Nothing to do once the scope has released it.
    private bool Holds(int handle, int listener, out string? release)
    {
        lock (gate)
        {
            release = !disposed && Realm.OwnsAlone(this, handle) ? JsOp.Release([handle]) : null;
            return !disposed && callbacks.Contains(listener);
        }
    }

    // After the release went out: the scope forgets the handle and the listener. If the release failed, both stay
    // with the scope, which releases them when it is disposed.
    private void Forget(int handle, int listener)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            Realm.Disown(this, handle);
        }
        ReleaseListener(listener);
    }

    private sealed class Subscription(JsScope scope, int handle, int listener) : IDisposable, IAsyncDisposable
    {
        public void Dispose()
        {
            if (!scope.Holds(handle, listener, out var release))
            {
                return;
            }
            if (release is not null)
            {
                scope.Realm.Send(release);
            }
            scope.Forget(handle, listener);
        }

        public async ValueTask DisposeAsync()
        {
            if (!scope.Holds(handle, listener, out var release))
            {
                return;
            }
            try
            {
                if (release is not null)
                {
                    await scope.Realm.ReleaseAsync(release);
                }
            }
            catch (JSDisconnectedException)
            {
                // The circuit is gone, and the subscription with it.
            }
            scope.Forget(handle, listener);
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
