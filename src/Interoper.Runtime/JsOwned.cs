namespace Interoper;

/// <summary>
/// A value and the child scope that owns it, as one (design §3.2), from <see cref="JsScope.CreateOwnedAsync{T}"/> or
/// <see cref="JsScope.CreateOwned{T}"/>. Disposal, or the parent scope's, runs the teardown once, then releases what
/// the child owns.
/// </summary>
public sealed class JsOwned<T> : IDisposable, IAsyncDisposable
{
    internal JsOwned(T value, JsScope scope) => (Value, Scope) = (value, scope);

    public T Value { get; }

    /// <summary>The child scope: pass it to calls whose results go with <see cref="Value"/>.</summary>
    public JsScope Scope { get; }

    public void Dispose() => Scope.Dispose();

    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}
