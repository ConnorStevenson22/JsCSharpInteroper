namespace Interoper;

/// <summary>
/// What JS does with an event before it reaches .NET (R24), sent with the <c>subscribe</c> op (design §3.4). Server and
/// Hybrid can't call <c>preventDefault()</c> in time from .NET, so JS does it.
/// </summary>
public sealed record EventOptions
{
    /// <summary>Calls the event's <c>preventDefault()</c>, when it has one, before the handler runs.</summary>
    public bool PreventDefault { get; init; }

    /// <summary>Calls the event's <c>stopPropagation()</c>, when it has one, before the handler runs.</summary>
    public bool StopPropagation { get; init; }

    /// <summary>
    /// The payload's top-level members to send, replacing the generated default: a data record payload's own members.
    /// Nothing else crosses the wire, and a record member that isn't sent is <c>default</c> (null for a reference).
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }
}
