using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace UnionInterop.Runtime;

/// <summary>A generated wrapper around a live JS object (class instances such as ArcGIS <c>Graphic</c>).</summary>
public interface IJsProxy<TSelf> where TSelf : IJsProxy<TSelf>
{
    IJSObjectReference Reference { get; }
    static abstract TSelf Wrap(IJSObjectReference reference);
}

public abstract class JsProxy(IJSObjectReference reference) : IAsyncDisposable
{
    public IJSObjectReference Reference { get; } = reference;
    public ValueTask DisposeAsync() => Reference.DisposeAsync();
}

/// <summary>
/// Serializes a proxy as the IJSObjectReference it wraps, so proxies can sit inside DTOs and unions.
/// Blazor's own IJSObjectReference reader rejects unknown properties, so the <c>__jsType</c> hint
/// is stripped before handing the payload over.
/// </summary>
public sealed class JsProxyConverter<T> : JsonConverter<T> where T : IJsProxy<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var id = doc.RootElement.GetProperty("__jsObjectId").GetInt64();
        var reference = JsonSerializer.Deserialize<IJSObjectReference>($$"""{"__jsObjectId":{{id}}}""", options)!;
        return T.Wrap(reference);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Reference, options);
}
