using System.Text;
using System.Text.Json;

namespace Interoper;

/// <summary>
/// Writes ops (design §3.4) for generated code. Each takes the generated writer of its payload: a value, the arguments
/// in order, or a bag's members.
/// </summary>
public static class JsOp
{
    /// <summary><c>new X(bag)</c>: <paramref name="bag"/> writes the constructor bag's members (R18).</summary>
    public static string Create(int classId, Action<Utf8JsonWriter> bag) => Write(w =>
    {
        w.WriteString("op"u8, "create"u8);
        w.WriteNumber("t"u8, classId);
        w.WriteStartArray("args"u8);
        w.WriteStartObject();
        bag(w);
        w.WriteEndObject();
        w.WriteEndArray();
    });

    /// <summary>
    /// Reads a member. With <paramref name="byValue"/>, a value-like instance comes back as <c>$c</c> data (R16);
    /// without it, as a handle.
    /// </summary>
    public static string Get(int handle, string name, bool byValue = false) => Write(w =>
    {
        Target(w, "get"u8, handle, name);
        if (byValue)
        {
            w.WriteBoolean("byValue"u8, true);
        }
    });

    public static string Set(int handle, string name, Action<Utf8JsonWriter> value) => Write(w =>
    {
        Target(w, "set"u8, handle, name);
        w.WritePropertyName("value"u8);
        value(w);
    });

    /// <summary>Calls a method; <paramref name="args"/> writes the arguments in order (none when null).</summary>
    public static string Call(int handle, string name, Action<Utf8JsonWriter>? args = null, bool byValue = false) => Write(w =>
    {
        Target(w, "call"u8, handle, name);
        Arguments(w, args, byValue);
    });

    /// <summary>Calls a module function (design §2).</summary>
    public static string Call(string module, string name, Action<Utf8JsonWriter>? args = null, bool byValue = false) => Write(w =>
    {
        w.WriteString("op"u8, "call"u8);
        w.WriteString("module"u8, module);
        w.WriteString("name"u8, name);
        Arguments(w, args, byValue);
    });

    public static string Snapshot(int handle) => Write(w =>
    {
        w.WriteString("op"u8, "snapshot"u8);
        w.WriteNumber("h"u8, handle);
    });

    /// <summary>Sets a patch member by member (R18): <paramref name="patch"/> writes the members it sets.</summary>
    public static string Apply(int handle, Action<Utf8JsonWriter> patch) => Write(w =>
    {
        w.WriteString("op"u8, "apply"u8);
        w.WriteNumber("h"u8, handle);
        w.WriteStartObject("value"u8);
        patch(w);
        w.WriteEndObject();
    });

    /// <summary>
    /// <c>target[method](name, callback)</c>, which returns a subscription (R24); see <see cref="JsScope.Subscribe{T}"/>.
    /// <paramref name="options"/> go as <c>options</c>, left out when they set nothing.
    /// </summary>
    public static string Subscribe(int handle, string method, string name, int callback, EventOptions? options = null) => Write(w =>
    {
        w.WriteString("op"u8, "subscribe"u8);
        w.WriteNumber("h"u8, handle);
        w.WriteString("method"u8, method);
        w.WriteString("name"u8, name);
        w.WriteStartObject("callback"u8);
        w.WriteNumber("$f"u8, callback);
        w.WriteEndObject();
        if (options is { } o && (o.PreventDefault || o.StopPropagation || o.Fields is not null))
        {
            w.WriteStartObject("options"u8);
            if (o.PreventDefault)
            {
                w.WriteBoolean("preventDefault"u8, true);
            }
            if (o.StopPropagation)
            {
                w.WriteBoolean("stopPropagation"u8, true);
            }
            if (o.Fields is { } fields)
            {
                w.WriteStartArray("fields"u8);
                foreach (var field in fields)
                {
                    w.WriteStringValue(field);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
    });

    // The typed fast paths as plain ops (JsRuntimeTransport, InMemoryTransport, batches), and a scope's release.

    internal static string Set(int handle, string name, double value) => Set(handle, name, w => w.WriteNumberValue(value));

    internal static string Set(int handle, string name, string value) => Set(handle, name, w => w.WriteStringValue(value));

    internal static string Set(int handle, string name, bool value) => Set(handle, name, w => w.WriteBooleanValue(value));

    internal static string Load(IEnumerable<string> modules) => Write(w =>
    {
        w.WriteString("op"u8, "load"u8);
        w.WriteStartArray("modules"u8);
        foreach (var module in modules)
        {
            w.WriteStringValue(module);
        }
        w.WriteEndArray();
    });

    internal static string Release(IEnumerable<int> handles) =>
        $$"""{"op":"release","ids":[{{string.Join(",", handles)}}]}""";

    /// <summary>
    /// A fast path's result read from a <c>get</c> envelope. Fast paths serve members declared as non-null
    /// primitives, so null or undefined throws, as <c>getValue</c> does in interoper.js.
    /// </summary>
    internal static T Primitive<T>(string envelope, string name, JsonRead<T> read) where T : notnull
    {
        var value = JsEnvelope.Read(envelope, (ref reader) => JsJson.ReadOpt(ref reader, read));
        return value.TryGetValue(out T? v)
            ? v
            : throw new JsException("InteroperError", $"{name} is {value}; a typed fast path needs a value", null);
    }

    private static void Target(Utf8JsonWriter w, ReadOnlySpan<byte> op, int handle, string name)
    {
        w.WriteString("op"u8, op);
        w.WriteNumber("h"u8, handle);
        w.WriteString("name"u8, name);
    }

    private static void Arguments(Utf8JsonWriter w, Action<Utf8JsonWriter>? args, bool byValue)
    {
        w.WriteStartArray("args"u8);
        args?.Invoke(w);
        w.WriteEndArray();
        if (byValue)
        {
            w.WriteBoolean("byValue"u8, true);
        }
    }

    private static string Write(Action<Utf8JsonWriter> op)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            op(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
