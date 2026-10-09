namespace Interoper;

/// <summary>
/// The writes and void calls queued by <see cref="JsScope.BeginBatch"/> (design §3.3), sent as one <c>batch</c> op
/// when a read needs them, when the batch is disposed, or when its scope is.
/// </summary>
internal sealed class JsBatch(JsRealm realm, JsScope scope) : IAsyncDisposable
{
    private readonly List<string> ops = [];
    private bool closed;

    public bool IsOpen
    {
        get
        {
            lock (ops)
            {
                return !closed;
            }
        }
    }

    /// <summary>Queues an op, unless the batch is over (a call that outlived it, on another flow, sends at once).</summary>
    public bool TryAdd(string opJson)
    {
        lock (ops)
        {
            if (!closed)
            {
                ops.Add(opJson);
            }
            return !closed;
        }
    }

    public Task FlushAsync()
    {
        string batch;
        lock (ops)
        {
            if (ops.Count == 0)
            {
                return Task.CompletedTask;
            }
            batch = $$"""{"op":"batch","ops":[{{string.Join(",", ops)}}]}""";
            ops.Clear();
        }
        return realm.CheckAsync(batch);
    }

    // Not async: the async-local batch is cleared on the caller's flow, which an async method can't do.
    public ValueTask DisposeAsync()
    {
        lock (ops)
        {
            closed = true;
        }
        realm.EndBatch(this);
        scope.EndBatch(this);
        return new(FlushAsync());
    }
}
