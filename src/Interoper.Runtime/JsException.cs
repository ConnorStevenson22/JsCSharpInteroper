namespace Interoper;

/// <summary>An error thrown in JavaScript, with its JS name (<c>TypeError</c>, …), message and stack.</summary>
public class JsException(string name, string message, string? jsStack) : Exception(message)
{
    public string Name { get; } = name;

    public string? JsStack { get; } = jsStack;
}
