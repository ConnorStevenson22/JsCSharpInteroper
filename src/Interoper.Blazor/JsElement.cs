using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Interoper;

/// <summary>DOM elements on the wire (R13): an <see cref="ElementReference"/> is sent as <c>{"$e":id}</c>.</summary>
public static class JsElement
{
    /// <summary>
    /// Writes the reference as <c>{"$e":id}</c>, which interoper.js resolves to the element Blazor rendered for it, by
    /// the capture attribute (<c>_bl_id</c>) it gives every <c>@ref</c> element: the writer for DOM element types in
    /// generated code. A reference is only known once its element has rendered.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, ElementReference value)
    {
        if (string.IsNullOrEmpty(value.Id))
        {
            throw new InvalidOperationException("The ElementReference has no element yet: capture it with @ref, and send it after the first render.");
        }
        writer.WriteStartObject();
        writer.WriteString("$e"u8, value.Id);
        writer.WriteEndObject();
    }
}
