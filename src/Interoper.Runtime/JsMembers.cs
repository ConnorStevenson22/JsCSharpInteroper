namespace Interoper;

/// <summary>
/// Members of a snapshot record (R18), from its generated <c>Members</c> class (<c>LayerSnapshot.Members.Visible</c>) and
/// combined with <c>|</c>: what <c>SnapshotAsync(members)</c> reads and <c>WatchAsync(scope, members, handler)</c> watches.
/// <c>default</c> is none.
/// </summary>
public readonly struct JsMembers<T> where T : class, IJsRecord<T>
{
    private readonly string[]? names;

    /// <summary>Members by their JS names, for generated code.</summary>
    public JsMembers(params string[] names) => this.names = names;

    /// <summary>The JS names, each once.</summary>
    public IReadOnlyList<string> Names => names ?? [];

    public static JsMembers<T> operator |(JsMembers<T> left, JsMembers<T> right) => new([.. left.Names.Union(right.Names)]);
}
