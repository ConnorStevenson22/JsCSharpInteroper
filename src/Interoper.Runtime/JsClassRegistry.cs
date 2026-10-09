namespace Interoper;

/// <summary>
/// The most-derived-type registry (R19 step 1): class id → a factory for its live type. Generated code fills it,
/// with the class ids of its metadata module. Sync-mode code also lists the modules it uses (<see cref="Preload"/>).
/// </summary>
public sealed class JsClassRegistry
{
    private readonly Dictionary<int, Func<JsRealm, int, JsObject>> factories = [];

    public JsClassRegistry Add(int classId, Func<JsRealm, int, JsObject> create)
    {
        factories.Add(classId, create);
        return this;
    }

    /// <summary>
    /// Modules the realm loads when it starts (design §3.5): sync mode's <c>invokeSync</c> can't import them on
    /// first use.
    /// </summary>
    public JsClassRegistry Preload(params string[] modules)
    {
        Modules = [.. Modules, .. modules];
        return this;
    }

    internal string[] Modules { get; private set; } = [];

    internal JsObject Create(JsRealm realm, int handle, int classId) =>
        factories.TryGetValue(classId, out var create)
            ? create(realm, handle)
            : throw new JsException("InteroperError", $"No live type is registered for class id {classId}.", null);
}
