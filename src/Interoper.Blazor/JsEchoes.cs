namespace Interoper;

/// <summary>
/// R28: the values a component sent its element for a bound property (<c>@bind-Zoom</c>), so that the change events
/// they cause aren't reported back as changes. Otherwise an echo of an older value, arriving after a newer one was
/// sent (two quick changes on Server), would set the parent back, and the two values would chase each other.
/// </summary>
/// <remarks>
/// Echoes arrive in the order the values were sent. An event with the last value sent is its echo, and the older
/// ones have arrived or never will; an event with an older value is that value's echo. Any other is a change.
/// </remarks>
public sealed class JsEchoes<T>
{
    private readonly List<T> older = [];
    private (bool Known, T Value) sent, reported;
    private bool echoed;

    /// <summary>Records a value rendered or set; one equal to the last is the same render, and sends nothing.</summary>
    public void Sent(T value)
    {
        if (sent.Known && JsDiff.Same(sent.Value, value))
        {
            return;
        }
        if (sent.Known && !echoed)
        {
            older.Add(sent.Value);
        }
        sent = (true, value);
        echoed = reported.Known && JsDiff.Same(reported.Value, value); // the element has it already: no event comes
    }

    /// <summary>Whether a change event's value is the echo of a value sent, which isn't reported to the parent.</summary>
    public bool IsEcho(T value)
    {
        reported = (true, value);
        if (sent.Known && JsDiff.Same(sent.Value, value))
        {
            echoed = true;
            older.Clear();
            return true;
        }
        var i = older.FindIndex(v => JsDiff.Same(v, value));
        if (i < 0)
        {
            return false;
        }
        older.RemoveRange(0, i + 1);
        return true;
    }
}
