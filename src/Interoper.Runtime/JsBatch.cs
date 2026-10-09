namespace Interoper;

/// <summary>
/// The writes and void calls queued by <see cref="JsScope.BeginBatch"/> (design §3.3), sent as one <c>batch</c> op
/// when a read needs them, when the batch is disposed, or when its scope is.
/// </summary>
internal sealed class JsBatch(JsRealm realm, JsScope scope) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly List<string> ops = [];
    private bool closed;

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return !closed;
            }
        }
    }

    /// <summary>Queues an op, unless the batch is over (a call that outlived it, on another flow, sends at once).</summary>
    public bool TryAdd(string opJson)
    {
        lock (gate)
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
        lock (gate)
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
        lock (gate)
        {
            closed = true;
        }
        realm.EndBatch(this);
        scope.EndBatch(this);
        return new(FlushAsync());
    }
}
