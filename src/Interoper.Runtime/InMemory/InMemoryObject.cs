namespace Interoper;

/// <summary>
/// An instance of a fake JS class in <see cref="InMemoryTransport"/>. JS values are <see cref="Undefined.Value"/>,
/// <c>null</c>, <c>double</c>, <c>string</c>, <c>bool</c>, <c>List&lt;object?&gt;</c> (arrays),
/// <c>Dictionary&lt;string, object?&gt;</c> (plain objects), <see cref="JsFunction"/> (functions, such as
/// callbacks), <see cref="Task"/> or <c>Task&lt;object?&gt;</c> (Promises) and other in-memory objects.
/// Override <see cref="Get"/>, <see cref="Set"/> and <see cref="Call"/> for accessors and methods; throw
/// <see cref="JsException"/> for a JS error. A method that subscribes returns an <see cref="IDisposable"/>, or a plain
/// object with a <c>dispose</c> or <c>remove</c> function.
/// </summary>
public abstract class InMemoryObject
{
    private readonly Dictionary<string, object?> properties = [];

    /// <summary>The runtime class name, as the profile's class-name property reports it.</summary>
    public abstract string ClassName { get; }

    public virtual object? Get(string name) => properties.TryGetValue(name, out var value) ? value : Undefined.Value;

    public virtual void Set(string name, object? value) => properties[name] = value;

    public virtual object? Call(string name, object?[] args) =>
        throw new JsException("TypeError", $"{ClassName}.{name} is not a function", null);
}

/// <summary>A JS function in <see cref="InMemoryTransport"/>.</summary>
public delegate object? JsFunction(params object?[] args);

/// <summary>
/// A fake JS class: its entry in the generated metadata table, plus a constructor that takes the JS arguments.
/// </summary>
public sealed record InMemoryClass(int Id, string Name, Func<object?[], InMemoryObject> Construct)
{
    /// <summary>A value-like class (R16): sent as <c>$c</c> data where values go by value.</summary>
    public bool Value { get; init; }

    /// <summary>The snapshot members.</summary>
    public string[] Members { get; init; } = [];
}
