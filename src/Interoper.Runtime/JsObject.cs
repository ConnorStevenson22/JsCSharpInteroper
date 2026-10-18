using System.Text.Json;

namespace Interoper;

/// <summary>
/// The base of every generated live class: a JS object's handle in a realm (design §3.2). Get instances through
/// <see cref="JsScope.Wrap{T}(int, int)"/>, so one JS object is always one C# object; whatever a live object returns is
/// owned by the scope the caller passes, not by the object.
/// </summary>
public abstract class JsObject(JsRealm realm, int handle) : IJsObject
{
    public JsRealm Realm { get; } = realm;

    public int Handle { get; } = handle;

    /// <summary>Writes the object as its handle, <c>{"$h":id}</c>: the writer for live arguments in generated code.</summary>
    public static void Write(Utf8JsonWriter writer, JsObject value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("$h"u8, value.Handle);
        writer.WriteEndObject();
    }
}
