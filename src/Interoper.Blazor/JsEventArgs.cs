using System.Text;
using System.Text.Json;

namespace Interoper;

/// <summary>
/// A custom event's arguments (R28): its detail as wire JSON, projected to the payload record's members. interoper.js
/// registers each event of the generated metadata with Blazor; the generated <c>[EventHandler]</c> attributes let
/// markup use <c>@ondemo-map-click</c>-style handlers. Read the detail with its generated reader.
/// </summary>
public sealed class JsEventArgs : EventArgs
{
    /// <summary>The detail in wire form (design §3.4); <c>null</c> for none, <c>{"$u":1}</c> for undefined.</summary>
    public string Json { get; set; } = "null";

    /// <summary>The detail, read with <paramref name="read"/>; handles in it are read with a scope, which owns them.</summary>
    public T Read<T>(JsonRead<T> read)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(Json));
        reader.Read();
        return read(ref reader);
    }
}
