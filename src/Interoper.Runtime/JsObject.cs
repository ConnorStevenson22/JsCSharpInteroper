namespace Interoper;

/// <summary>
/// The base of every generated live class: a JS object's handle, and the scope that owns it (design §3.2).
/// Get instances through <see cref="JsScope.Wrap{T}"/>, so one JS object is always one C# object.
/// </summary>
public abstract class JsObject(JsScope scope, int handle)
{
    public JsScope Scope { get; } = scope;

    public int Handle { get; } = handle;
}

/// <summary>
/// The most-derived-type registry (R19 step 1): class id → a factory for its live type. Generated code fills it,
/// with the class ids of its metadata module.
/// </summary>
public sealed class JsClassRegistry
{
    private readonly Dictionary<int, Func<JsScope, int, JsObject>> factories = [];

    public JsClassRegistry Add(int classId, Func<JsScope, int, JsObject> create)
    {
        factories.Add(classId, create);
        return this;
    }

    internal JsObject Create(JsScope scope, int handle, int classId) =>
        factories.TryGetValue(classId, out var create)
            ? create(scope, handle)
            : throw new JsException("InteroperError", $"No live type is registered for class id {classId}.", null);
}
