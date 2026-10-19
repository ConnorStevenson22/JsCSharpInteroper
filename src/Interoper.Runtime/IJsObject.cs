namespace Interoper;

/// <summary>
/// A JS object's handle in a realm: what <see cref="JsObject"/> is, and what a generated mixin interface's members use
/// (§5), so any live class with the mixin has them.
/// </summary>
public interface IJsObject
{
    JsRealm Realm { get; }

    int Handle { get; }
}
