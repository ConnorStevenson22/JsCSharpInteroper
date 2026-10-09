namespace Interoper;

/// <summary>
/// The base of every generated live class: a JS object's handle in a realm (design §3.2). Get instances through
/// <see cref="JsScope.Wrap{T}"/>, so one JS object is always one C# object; whatever a live object returns is
/// owned by the scope the caller passes, not by the object.
/// </summary>
public abstract class JsObject(JsRealm realm, int handle)
{
    public JsRealm Realm { get; } = realm;

    public int Handle { get; } = handle;
}
