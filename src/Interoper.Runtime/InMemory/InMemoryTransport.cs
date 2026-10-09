using System.Text.Json;
using System.Text.Json.Nodes;

namespace Interoper;

/// <summary>
/// Both seams without a browser: a small C# copy of interoper.js over fake JS classes (<see cref="InMemoryObject"/>).
/// It speaks the same JSON protocol (design §3.4), so the same contract tests run against it and in the browser.
/// Modules are always loaded; batches aren't emulated yet. Single-threaded, like JS.
/// </summary>
public sealed class InMemoryTransport : IJsSyncTransport, IJsTransport
{
    private const string PromiseInSync = "The op returned a Promise; send it through invoke.";

    private readonly Dictionary<int, object> handles = [];
    private readonly Dictionary<object, int> ids = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> subscriptions = [];
    private readonly Dictionary<int, InMemoryClass> classes = [];
    private readonly Dictionary<string, int> classIds = [];
    private readonly Dictionary<string, InMemoryObject> modules = [];
    private int lastId;

    public JsCallbacks Callbacks { get; } = new();

    /// <summary>Registers a fake class, as the generated metadata table would list it.</summary>
    public InMemoryTransport Define(InMemoryClass entry)
    {
        classes.Add(entry.Id, entry);
        classIds.Add(entry.Name, entry.Id);
        return this;
    }

    /// <summary>Registers a fake module: <paramref name="exports"/> serves ops sent with <c>module</c>.</summary>
    public InMemoryTransport DefineModule(string specifier, InMemoryObject exports)
    {
        modules.Add(specifier, exports);
        return this;
    }

    public string Invoke(string opJson)
    {
        try
        {
            // Like invokeSync, a Promise is reported after the op has run.
            var result = Run(opJson);
            return result is Task ? Failure(InteroperError(PromiseInSync)) : Success(result);
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    }

    public async Task<string> InvokeAsync(string opJson)
    {
        try
        {
            var result = Run(opJson);
            return Success(result is Task<object?> promise ? await promise : result);
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    }

    public int Census() => handles.Count;

    public Task<int> CensusAsync() => Task.FromResult(Census());

    public double GetNumber(int handle, string name) => JsOp.Primitive(Invoke(JsOp.Get(handle, name)), name, JsJson.ReadNumber);
    public string GetString(int handle, string name) => JsOp.Primitive(Invoke(JsOp.Get(handle, name)), name, JsJson.ReadString);
    public bool GetBoolean(int handle, string name) => JsOp.Primitive(Invoke(JsOp.Get(handle, name)), name, JsJson.ReadBoolean);
    public void SetNumber(int handle, string name, double value) => JsEnvelope.Check(Invoke(JsOp.Set(handle, name, value)));
    public void SetString(int handle, string name, string value) => JsEnvelope.Check(Invoke(JsOp.Set(handle, name, value)));
    public void SetBoolean(int handle, string name, bool value) => JsEnvelope.Check(Invoke(JsOp.Set(handle, name, value)));

    public Task<double> GetNumberAsync(int handle, string name) => Async(() => GetNumber(handle, name));
    public Task<string> GetStringAsync(int handle, string name) => Async(() => GetString(handle, name));
    public Task<bool> GetBooleanAsync(int handle, string name) => Async(() => GetBoolean(handle, name));
    public Task SetNumberAsync(int handle, string name, double value) => Async(() => { SetNumber(handle, name, value); return 0; });
    public Task SetStringAsync(int handle, string name, string value) => Async(() => { SetString(handle, name, value); return 0; });
    public Task SetBooleanAsync(int handle, string name, bool value) => Async(() => { SetBoolean(handle, name, value); return 0; });

    private static Task<T> Async<T>(Func<T> run)
    {
        try
        {
            return Task.FromResult(run());
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }

    // --- Ops ---
    // Each returns its result in wire form (a JsonNode, or Undefined.Value), or a Task<object?> of it.

    private object? Run(string opJson)
    {
        var op = (Dictionary<string, object?>)Revive(JsonNode.Parse(opJson))!;
        string Name() => (string)op["name"]!;
        bool ByValue() => op.GetValueOrDefault("byValue") is true;
        object?[] Args() => op.GetValueOrDefault("args") is List<object?> args ? [.. args] : [];
        // An op targets a handle (`h`) or a module's exports (`module`).
        InMemoryObject Target() => op.GetValueOrDefault("module") is string module
            ? modules.GetValueOrDefault(module) ?? throw InteroperError($"Unknown module: {module}")
            : Instance(op.GetValueOrDefault("h"));

        switch (op.GetValueOrDefault("op"))
        {
            case "create":
                return Wire(ClassEntry(op.GetValueOrDefault("t") is double t ? (int)t : 0).Construct(Args()));
            case "get":
                return Wire(Target().Get(Name()), ByValue());
            case "set":
                Target().Set(Name(), op.GetValueOrDefault("value", Undefined.Value));
                return Undefined.Value;
            case "call":
                var result = Target().Call(Name(), Args());
                return result is Task promise ? Then(promise, ByValue()) : Wire(result, ByValue());
            case "snapshot":
                return ToData(Target());
            case "apply":
                var target = Target();
                foreach (var (name, value) in (Dictionary<string, object?>)op["value"]!)
                {
                    target.Set(name, value);
                }
                return Undefined.Value;
            case "subscribe":
                return Subscribe(Target(), (string)op["method"]!, Name(), op["callback"]);
            case "release":
                ReleaseAll((List<object?>)op["ids"]!);
                return Undefined.Value;
            case "load":
                return Undefined.Value;
            case "batch":
                throw InteroperError("batch is not emulated yet");
            default:
                throw InteroperError($"Unknown op: {op.GetValueOrDefault("op")}");
        }
    }

    private async Task<object?> Then(Task promise, bool byValue)
    {
        await promise;
        return Wire(promise is Task<object?> withResult ? withResult.Result : Undefined.Value, byValue);
    }

    private JsonNode Subscribe(InMemoryObject target, string method, string name, object? callback)
    {
        if (target.Call(method, [name, callback]) is not IDisposable subscription)
        {
            throw InteroperError($"{method} returned no dispose()");
        }
        if (ids.ContainsKey(subscription))
        {
            throw InteroperError($"{method} returned an object that already has a handle");
        }
        var id = HandleOf(subscription);
        subscriptions.Add(id);
        return id;
    }

    // Releases every id, even when a Dispose() throws; then reports what failed.
    private void ReleaseAll(List<object?> list)
    {
        var errors = new List<Exception>();
        foreach (var id in list.Select(x => (int)(double)x!))
        {
            if (!handles.Remove(id, out var value))
            {
                continue;
            }
            ids.Remove(value);
            if (!subscriptions.Remove(id))
            {
                continue;
            }
            try
            {
                ((IDisposable)value).Dispose();
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        }
        if (errors.Count == 1)
        {
            throw errors[0];
        }
        if (errors.Count > 1)
        {
            throw new JsException("AggregateError", $"{errors.Count} dispose() calls failed", null);
        }
    }

    // --- Handles and classes ---

    private InMemoryObject Instance(object? handle) =>
        handle is double id && handles.TryGetValue((int)id, out var value)
            ? value as InMemoryObject ?? throw InteroperError($"Handle {id} is not an in-memory object")
            : throw InteroperError($"Unknown handle: {handle}");

    private int HandleOf(object value)
    {
        if (!ids.TryGetValue(value, out var id))
        {
            id = ++lastId;
            handles.Add(id, value);
            ids.Add(value, id);
        }
        return id;
    }

    private InMemoryClass ClassEntry(int id) =>
        classes.TryGetValue(id, out var entry) ? entry : throw InteroperError($"Unknown class id: {id}");

    private int ClassIdOf(InMemoryObject value) => classIds.GetValueOrDefault(value.ClassName);

    private static JsException InteroperError(string message) => new("InteroperError", message, null);

    // --- Wire format ---

    private object? Revive(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
                return array.Select(Revive).ToList();
            case JsonObject o when o.ContainsKey("$u"):
                return Undefined.Value;
            case JsonObject o when o.ContainsKey("$h"):
                return handles.TryGetValue(o["$h"]!.GetValue<int>(), out var value) ? value : throw InteroperError($"Unknown handle: {o["$h"]}");
            case JsonObject o when o.ContainsKey("$f"):
                return Callback(o["$f"]!.GetValue<int>());
            case JsonObject o when o.ContainsKey("$c"):
                var entry = ClassEntry(o["$c"]!.GetValue<int>());
                if (!entry.Value)
                {
                    throw InteroperError($"Class {entry.Name} is not loaded; only value-like classes can be sent as $c");
                }
                var bag = o.Where(p => p.Key != "$c").ToDictionary(p => p.Key, p => Revive(p.Value));
                return entry.Construct([bag]);
            case JsonObject o:
                return o.ToDictionary(p => p.Key, p => Revive(p.Value));
            default:
                return node.GetValueKind() switch
                {
                    JsonValueKind.Number => node.GetValue<double>(),
                    JsonValueKind.String => node.GetValue<string>(),
                    var kind => kind == JsonValueKind.True,
                };
        }
    }

    // A .NET callback. Its arguments go by value; its result is revived.
    private JsFunction Callback(int id) => args =>
    {
        var result = Callbacks.InvokeCallback(id, ((JsonNode)Wire(args.ToList(), byValue: true)!).ToJsonString());
        return result is null ? Undefined.Value : Revive(JsonNode.Parse(result));
    };

    // A value in wire form: Undefined.Value, or a JsonNode (null for JS null). Plain objects and arrays are
    // copied; other objects become handles, or with byValue, value-like instances become `$c` data.
    private object? Wire(object? value, bool byValue = false) => value switch
    {
        null => null,
        Undefined => Undefined.Value,
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        List<object?> list => new JsonArray([.. list.Select(x => WireItem(Wire(x, byValue)))]),
        Dictionary<string, object?> bag => WireObject(bag.Select(p => (p.Key, Wire(p.Value, byValue)))),
        InMemoryObject o when byValue && classes.TryGetValue(ClassIdOf(o), out var entry) && entry.Value => ToData(o),
        InMemoryObject o => new JsonObject { ["$h"] = HandleOf(o), ["$t"] = ClassIdOf(o) },
        JsFunction or Task => new JsonObject { ["$h"] = HandleOf(value), ["$t"] = 0 },
        _ => throw new InvalidOperationException($"A {value.GetType()} is not an in-memory JS value; see InMemoryObject."),
    };

    // Array items can't be omitted, so undefined is `$u`.
    private static JsonNode? WireItem(object? value) => value is Undefined ? new JsonObject { ["$u"] = 1 } : (JsonNode?)value;

    // The snapshot members of an object, with value-like members copied deeply and entities as handles (R17).
    private JsonObject ToData(InMemoryObject value)
    {
        var entry = ClassEntry(ClassIdOf(value));
        var data = WireObject(entry.Members.Select(name => (name, Wire(value.Get(name), byValue: true))));
        if (entry.Value)
        {
            data.Insert(0, "$c", entry.Id);
        }
        return data;
    }

    private static JsonObject WireObject(IEnumerable<(string Name, object? Value)> members)
    {
        var o = new JsonObject();
        foreach (var (name, value) in members)
        {
            if (value is not Undefined)
            {
                o[name] = (JsonNode?)value;
            }
        }
        return o;
    }

    private static string Success(object? result) =>
        result is Undefined ? "{}" : new JsonObject { ["v"] = (JsonNode?)result }.ToJsonString();

    private static string Failure(Exception e)
    {
        var (name, stack) = e is JsException js ? (js.Name, js.JsStack ?? e.StackTrace) : ("Error", e.StackTrace);
        var error = new JsonObject { ["name"] = name, ["message"] = e.Message };
        if (stack is not null)
        {
            error["stack"] = stack;
        }
        return new JsonObject { ["e"] = error }.ToJsonString();
    }
}
