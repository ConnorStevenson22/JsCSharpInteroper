namespace Interoper;

/// <summary>
/// The most-derived-type registry (R19 step 1): class id → a factory for its live type. Generated code fills it,
/// with the class ids of its metadata module.
/// </summary>
public sealed class JsClassRegistry
{
    private readonly Dictionary<int, Func<JsRealm, int, JsObject>> factories = [];

    public JsClassRegistry Add(int classId, Func<JsRealm, int, JsObject> create)
    {
        factories.Add(classId, create);
        return this;
    }

    internal JsObject Create(JsRealm realm, int handle, int classId) =>
        factories.TryGetValue(classId, out var create)
            ? create(realm, handle)
            : throw new JsException("InteroperError", $"No live type is registered for class id {classId}.", null);
}
