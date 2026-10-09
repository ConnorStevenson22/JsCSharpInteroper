using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Interoper;

/// <summary>
/// Implemented by every generated union struct (R2), so <see cref="JsJson"/> can handle its undefined
/// and null states once. The struct itself (tag, cases, conversions, <c>TryGetValue</c>) is generated.
/// </summary>
public interface IJsUnion<TSelf> : IUnion where TSelf : struct, IJsUnion<TSelf>
{
    bool IsUndefined { get; }
    bool IsNull { get; }

    /// <summary>The bottom case: a null reference is JS null, the singleton is undefined.</summary>
    static abstract implicit operator TSelf(Undefined? value);

    /// <summary>Writes the active case. Never called for undefined or null.</summary>
    static abstract void WriteValue(Utf8JsonWriter writer, TSelf value);

    /// <summary>Reads a case from the current token (R19). Never called on a null token.</summary>
    static abstract TSelf ReadValue(ref Utf8JsonReader reader);
}
