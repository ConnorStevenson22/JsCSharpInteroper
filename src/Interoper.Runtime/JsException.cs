namespace Interoper;

/// <summary>An error thrown in JavaScript, with its JS name (<c>TypeError</c>, …), message and stack.</summary>
public class JsException(string name, string message, string? jsStack) : Exception(message)
{
    public string Name { get; } = name;

    public string? JsStack { get; } = jsStack;
}

/// <summary>
/// A <c>batch</c> op in which some ops failed (design §3.3, §3.4); the others still ran. Each failed op comes with
/// its index in the batch, in order.
/// </summary>
public sealed class JsBatchException(string message, string? jsStack, IReadOnlyList<(int Index, JsException Error)> errors)
    : JsException("BatchError", message, jsStack)
{
    public IReadOnlyList<(int Index, JsException Error)> Errors { get; } = errors;
}
