// The transport contract (design §3.1, §3.4): one suite that runs in memory (InMemoryContractTests) and in the
// browser on every host (tests/Interoper.E2E). Compiled into both, so it has no test framework.
using System.Text.Json;
using System.Text.Json.Nodes;
using static Interoper.Contract.Check;

namespace Interoper.Contract;

/// <summary>One seam of one transport, as generated code in one mode would use it.</summary>
public sealed class Seam
{
    private readonly IJsSyncTransport? sync;
    private readonly IJsTransport? async;

    private Seam(IJsSyncTransport? sync, IJsTransport? async, bool callbacksReturn) =>
        (this.sync, this.async, CallbacksReturn) = (sync, async, callbacksReturn);

    public static Seam Sync(IJsSyncTransport transport) => new(transport, null, callbacksReturn: true);

    /// <param name="callbacksReturn">See <see cref="CallbacksReturn"/>.</param>
    public static Seam Async(IJsTransport transport, bool callbacksReturn) => new(null, transport, callbacksReturn);

    public bool IsSync => sync is not null;

    /// <summary>The async seam, for the cases that build a realm over it; null for a sync seam.</summary>
    public IJsTransport? AsyncTransport => async;

    /// <summary>
    /// Whether JS gets a callback's return value synchronously (R21): yes when .NET runs on JS's thread, no
    /// across IJSRuntime, where JS gets a Promise.
    /// </summary>
    public bool CallbacksReturn { get; }

    public JsCallbacks Callbacks => sync?.Callbacks ?? async!.Callbacks;

    public Task<string> Send(string op) => sync is null ? async!.InvokeAsync(op) : Task.FromResult(sync.Invoke(op));

    public Task<int> Census() => sync is null ? async!.CensusAsync() : Task.FromResult(sync.Census());

    public Task<double> GetNumber(int h, string name) => sync is null ? async!.GetNumberAsync(h, name) : Task.FromResult(sync.GetNumber(h, name));

    public Task<string> GetString(int h, string name) => sync is null ? async!.GetStringAsync(h, name) : Task.FromResult(sync.GetString(h, name));

    public Task<bool> GetBoolean(int h, string name) => sync is null ? async!.GetBooleanAsync(h, name) : Task.FromResult(sync.GetBoolean(h, name));

    public Task SetNumber(int h, string name, double value)
    {
        if (sync is null) return async!.SetNumberAsync(h, name, value);
        sync.SetNumber(h, name, value);
        return Task.CompletedTask;
    }

    public Task SetString(int h, string name, string value)
    {
        if (sync is null) return async!.SetStringAsync(h, name, value);
        sync.SetString(h, name, value);
        return Task.CompletedTask;
    }

    public Task SetBoolean(int h, string name, bool value)
    {
        if (sync is null) return async!.SetBooleanAsync(h, name, value);
        sync.SetBoolean(h, name, value);
        return Task.CompletedTask;
    }
}

/// <summary>The cases, against fixtures/demo-maps. A failed case throws <see cref="ContractException"/>.</summary>
public static class TransportContract
{
    // Class ids from the demo-maps metadata (tests/GoldenSlice/wwwroot/demo-maps.meta.js).
    public const int Shape = 1, Point = 2, MapView = 5, Marker = 6;
    public const string Measure = "@acme/demo-maps/measure.js";

    public static readonly IReadOnlyList<(string Name, Func<Seam, Task> Run)> Cases =
    [
        ("create, get, set and release", CreateGetSet),
        ("typed fast paths", FastPaths),
        ("$c value arguments (R16)", ValueArguments),
        ("Promise calls", Promises),
        ("$f callback with a synchronous return value (R21)", SyncReturnCallback),
        ("$f callback that re-enters the seam (R21)", ReentrantCallback),
        ("$f callback whose handler throws (R21)", ThrowingCallback),
        ("module function call", ModuleCall),
        ("subscribe and release (R24)", Subscribe),
        ("subscribe with options: JS calls the event's methods, then sends only the fields (R24)", SubscribeWithOptions),
        ("errors become JsException", Errors),
        ("batch op: failures listed by index, the rest applied", BatchOp),
        ("BeginBatch: N writes are one round trip, a read flushes first", BeginBatch),
        ("wrap returns its value in wire form (R13, R28)", Wrap),
    ];

    // A component gets its element's handle this way, from {"$e":id}; any value comes back as a result would.
    private static async Task Wrap(Seam seam)
    {
        var baseline = await seam.Census();
        var h = await Create(seam, MapView);
        var wrapped = await Value(seam, $$$"""{"op":"wrap","value":{"$h":{{{h}}}}}""");
        Equal($$"""{"$h":{{h}},"$t":5}""", wrapped); // the same handle
        Equal("""{"a":[1,{"$u":1}],"b":null}""", await Value(seam, """{"op":"wrap","value":{"a":[1,{"$u":1}],"b":null}}"""));
        var e = await Throws(() => Value(seam, """{"op":"wrap","value":{"$e":"none"}}"""));
        Equal(("InteroperError", "No element has the capture attribute _bl_none; send an ElementReference after its first render"), (e.Name, e.Message));
        await Release(seam, h);
        Equal(baseline, await seam.Census());
    }

    private static async Task CreateGetSet(Seam seam)
    {
        var baseline = await seam.Census();
        var view = await Value(seam, """{"op":"create","t":5,"args":[{"zoom":3}]}""");
        Equal(MapView, (int)view!["$t"]!);
        var h = (int)view["$h"]!;
        Equal(baseline + 1, await seam.Census());

        Equal("{}", await seam.Send($$"""{"op":"set","h":{{h}},"name":"zoom","value":99}"""));
        Equal("""{"v":20}""", await seam.Send($$"""{"op":"get","h":{{h}},"name":"zoom"}""")); // the setter clamps
        Equal("{}", await seam.Send($$"""{"op":"get","h":{{h}},"name":"title"}""")); // undefined
        Equal("""{"v":null}""", await seam.Send($$"""{"op":"get","h":{{h}},"name":"selected"}"""));

        Equal("{}", await Release(seam, h));
        Equal(baseline, await seam.Census());
    }

    private static async Task FastPaths(Seam seam)
    {
        var baseline = (await seam.Census(), seam.Callbacks.Count);
        var h = await Create(seam, MapView);
        await seam.SetNumber(h, "zoom", 7);
        Equal(7.0, await seam.GetNumber(h, "zoom"));
        await seam.SetString(h, "basemap", "satellite");
        Equal("satellite", await seam.GetString(h, "basemap"));
        await seam.SetBoolean(h, "pinned", true);
        Equal(true, await seam.GetBoolean(h, "pinned"));

        // Fast paths serve members declared as non-null primitives; null or undefined is an error.
        var e = await Throws(() => seam.GetString(h, "title"));
        Equal(("InteroperError", "title is undefined; a typed fast path needs a value"), (e.Name, e.Message));
        e = await Throws(() => seam.GetBoolean(h, "selected"));
        Equal(("InteroperError", "selected is null; a typed fast path needs a value"), (e.Name, e.Message));

        await Release(seam, h);
        Equal(baseline, (await seam.Census(), seam.Callbacks.Count));
    }

    private static async Task ValueArguments(Seam seam)
    {
        var baseline = await seam.Census();
        var h = await Create(seam, MapView, """{"center":{"$c":2,"type":"point","x":1,"y":2}}""");
        // JS built a Point from the bag, so it reads back as one: by value, or as a handle with its class id.
        Equal("""{"$c":2,"type":"point","x":1,"y":2}""", await Value(seam, $$"""{"op":"get","h":{{h}},"name":"center","byValue":true}"""));
        var center = await Value(seam, $$"""{"op":"get","h":{{h}},"name":"center"}""");
        Equal(Point, (int)center!["$t"]!);
        await Release(seam, h, (int)center["$h"]!);
        Equal(baseline, await seam.Census());
    }

    private static async Task Promises(Seam seam)
    {
        var h = await Create(seam, MapView);
        var flyTo = $$"""{"op":"call","h":{{h}},"name":"flyTo","args":[[3,4]]}""";
        if (seam.IsSync)
        {
            // invokeSync can't wait: the call runs, and the Promise is an error.
            var e = await Throws(async () => JsEnvelope.Check(await seam.Send(flyTo)));
            Equal(("InteroperError", "The op returned a Promise; send it through invoke."), (e.Name, e.Message));
        }
        else
        {
            Equal("{}", await seam.Send(flyTo));
            Equal("""{"$c":2,"type":"point","x":3,"y":4}""", await Value(seam, $$"""{"op":"get","h":{{h}},"name":"center","byValue":true}"""));
            Equal("""{"v":null}""", await seam.Send($$"""{"op":"call","h":{{h}},"name":"markerAt","args":[0,0]}"""));
        }
        await Release(seam, h);
    }

    private static async Task SyncReturnCallback(Seam seam)
    {
        var baseline = (await seam.Census(), seam.Callbacks.Count);
        var calls = new List<string>();
        var formatter = seam.Callbacks.Register(args =>
        {
            calls.Add(args);
            return "\"#a\"";
        });
        var h = await Create(seam, Marker, $$$"""{"label":"a","labelFormatter":{"$f":{{{formatter}}}}}""");
        var label = await Value(seam, $$"""{"op":"get","h":{{h}},"name":"displayLabel"}""");
        if (seam.CallbacksReturn)
        {
            Equal("\"#a\"", label);
            Equal($$"""[{"$h":{{h}},"$t":6}]""", calls.Single()); // entities arrive as their handle
            await Release(seam, h);
        }
        else
        {
            // Across IJSRuntime the formatter returns a Promise, which JS doesn't expect (IOP2005).
            Equal(0, (int)label!["$t"]!);
            await Release(seam, h, (int)label["$h"]!);
        }
        seam.Callbacks.Release(formatter);
        Equal(baseline, (await seam.Census(), seam.Callbacks.Count));
    }

    private static async Task ReentrantCallback(Seam seam)
    {
        if (!seam.IsSync) return; // only a sync seam can call back into JS from inside a callback
        var h = 0;
        var formatter = seam.Callbacks.Register(_ => JsonValue.Create("#" + seam.GetString(h, "label").Result).ToJsonString());
        h = await Create(seam, Marker, $$$"""{"label":"a","labelFormatter":{"$f":{{{formatter}}}}}""");
        Equal("\"#a\"", await Value(seam, $$"""{"op":"get","h":{{h}},"name":"displayLabel"}"""));
        await Release(seam, h);
        seam.Callbacks.Release(formatter);
    }

    private static async Task ThrowingCallback(Seam seam)
    {
        // Across IJSRuntime the failure is a rejected Promise in JS instead, which nothing here observes.
        if (!seam.CallbacksReturn) return;
        var formatter = seam.Callbacks.Register(_ => throw new InvalidOperationException("formatter failed"));
        var h = await Create(seam, Marker, $$$"""{"labelFormatter":{"$f":{{{formatter}}}}}""");
        var e = await Throws(async () => JsEnvelope.Check(await seam.Send($$"""{"op":"get","h":{{h}},"name":"displayLabel"}""")));
        Expect(e.Message.Contains("formatter failed", StringComparison.Ordinal), $"the handler's message, got {e.Name}: {e.Message}");
        await Release(seam, h);
        seam.Callbacks.Release(formatter);
    }

    private static async Task ModuleCall(Seam seam)
    {
        var distance = $$"""{"op":"call","module":"{{Measure}}","name":"distance","args":[{"x":0,"y":0},{"x":3,"y":4}]}""";
        Equal("""{"v":5}""", await seam.Send(distance));
    }

    private static async Task Subscribe(Seam seam)
    {
        var baseline = (await seam.Census(), seam.Callbacks.Count);
        var h = await Create(seam, MapView);
        var clicks = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = seam.Callbacks.Register(args =>
        {
            clicks.TrySetResult(args);
            return null;
        });
        var subscription = await Value(seam, $$$"""{"op":"subscribe","h":{{{h}}},"method":"on","name":"click","callback":{"$f":{{{listener}}}}}""");

        // The library raises the event; the payload's Point arrives by value.
        const string Click = """{"type":"click","x":1,"y":-2,"button":0,"mapPoint":{"$c":2,"type":"point","x":1,"y":2}}""";
        var emit = $$"""{"op":"call","h":{{h}},"name":"emit","args":["click",{{Click}}]}""";
        Equal("true", await Value(seam, emit));
        Equal($"[{Click}]", await clicks.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await Release(seam, (int)subscription!);
        Equal("false", await Value(seam, emit));
        await Release(seam, h);
        seam.Callbacks.Release(listener);
        Equal(baseline, (await seam.Census(), seam.Callbacks.Count));
    }

    private static async Task SubscribeWithOptions(Seam seam)
    {
        var baseline = (await seam.Census(), seam.Callbacks.Count);
        var h = await Create(seam, MapView);
        var calls = new List<string>();
        var (all, again) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        int Register(string? name) => seam.Callbacks.Register(args =>
        {
            lock (calls)
            {
                calls.Add(name ?? args);
                if (calls.Count == 3) all.TrySetResult();
                if (calls.Count == 4) again.TrySetResult(calls[3]);
            }
            return null;
        });
        var (listener, preventDefault, stopPropagation) = (Register(null), Register("preventDefault"), Register("stopPropagation"));
        const string Options = """{"preventDefault":true,"stopPropagation":true,"fields":["x","mapPoint","missing"]}""";
        var subscription = await Value(seam, $$$"""{"op":"subscribe","h":{{{h}}},"method":"on","name":"click","callback":{"$f":{{{listener}}}},"options":{{{Options}}}}""");

        // A library event with these methods works as a DOM event does; nothing outside the fields crosses the wire.
        var click = $$$"""{"type":"click","x":1,"y":-2,"button":0,"mapPoint":{"$c":2,"type":"point","x":1,"y":2},"preventDefault":{"$f":{{{preventDefault}}}},"stopPropagation":{"$f":{{{stopPropagation}}}}}""";
        Equal("true", await Value(seam, $$"""{"op":"call","h":{{h}},"name":"emit","args":["click",{{click}}]}"""));
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string[] expected = ["preventDefault", "stopPropagation", """[{"x":1,"mapPoint":{"$c":2,"type":"point","x":1,"y":2}}]"""];
        // Across IJSRuntime each call is a message of its own, so only their set is certain.
        Equal(string.Join("\n", seam.CallbacksReturn ? expected : expected.Order(StringComparer.Ordinal)),
            string.Join("\n", seam.CallbacksReturn ? calls : calls.Order(StringComparer.Ordinal)));

        // An event without the methods is fine too.
        Equal("true", await Value(seam, $$"""{"op":"call","h":{{h}},"name":"emit","args":["click",{"x":2}]}"""));
        Equal("""[{"x":2}]""", await again.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Release(seam, (int)subscription!, h);
        seam.Callbacks.Release(listener);
        seam.Callbacks.Release(preventDefault);
        seam.Callbacks.Release(stopPropagation);
        Equal(baseline, (await seam.Census(), seam.Callbacks.Count));
    }

    private static async Task Errors(Seam seam)
    {
        var baseline = await seam.Census();
        var e = await Throws(async () => JsEnvelope.Check(await seam.Send("""{"op":"get","h":999999,"name":"zoom"}""")));
        Equal(("InteroperError", "Unknown handle: 999999"), (e.Name, e.Message));

        e = await Throws(async () => JsEnvelope.Check(await seam.Send("""{"op":"create","t":1,"args":[]}""")));
        Equal(("TypeError", "Shape is abstract"), (e.Name, e.Message));
        Expect(!string.IsNullOrEmpty(e.JsStack), "the error has a stack");

        e = await Throws(() => seam.GetNumber(999999, "zoom"));
        Equal(("InteroperError", "Unknown handle: 999999"), (e.Name, e.Message));
        e = await Throws(() => seam.SetString(999999, "basemap", "x"));
        Equal(("InteroperError", "Unknown handle: 999999"), (e.Name, e.Message));
        Equal(baseline, await seam.Census());
    }

    private static async Task BatchOp(Seam seam)
    {
        var h = await Create(seam, MapView);
        string Set(double zoom) => $$"""{"op":"set","h":{{h}},"name":"zoom","value":{{zoom}}}""";
        Equal("{}", await seam.Send($$"""{"op":"batch","ops":[{{Set(2)}},{{Set(3)}}]}"""));
        Equal(3.0, await seam.GetNumber(h, "zoom"));

        var noSuchMethod = $$"""{"op":"call","h":{{h}},"name":"noSuchMethod"}""";
        var e = await Throws(async () => JsEnvelope.Check(await seam.Send($$"""{"op":"batch","ops":[{{Set(4)}},{{noSuchMethod}},{{Set(5)}}]}""")));
        var batch = e as JsBatchException ?? throw new ContractException($"Expected a JsBatchException, got {e.GetType().Name}");
        Equal(("BatchError", "1 of 3 batched ops failed"), (batch.Name, batch.Message));
        Equal("1 TypeError", string.Join(", ", batch.Errors.Select(x => $"{x.Index} {x.Error.Name}")));
        Equal(5.0, await seam.GetNumber(h, "zoom"));
        await Release(seam, h);
    }

    // What async-mode generated code does through a realm and a scope (design §3.3). Sync mode doesn't batch.
    private static async Task BeginBatch(Seam seam)
    {
        if (seam.AsyncTransport is not { } transport) return;
        var realm = new JsRealm(() => Task.FromResult(transport), new JsClassRegistry());
        await realm.StartAsync();
        var baseline = await seam.Census();
        var scope = new JsScope(realm);
        var h = (int)(await realm.CallAsync("""{"op":"create","t":5,"args":[{}]}""", Node))!["$h"]!;
        scope.Own(h);
        string Set(string name, double value) => $$"""{"op":"set","h":{{h}},"name":"{{name}}","value":{{value}}}""";

        var start = realm.RoundTrips;
        await using (scope.BeginBatch())
        {
            await realm.SendAsync(Set("zoom", 3));
            await realm.SetAsync(h, "basemap", "satellite");
            await realm.SendAsync(Set("rotation", 15));
            await realm.SendAsync($$"""{"op":"call","h":{{h}},"name":"flyTo","args":[[3,4]]}"""); // awaited in JS
            Equal(start, realm.RoundTrips); // all queued
            Equal(3.0, await realm.GetNumberAsync(h, "zoom")); // the queue, then the read
            Equal(start + 2, realm.RoundTrips);
            await realm.SetAsync(h, "zoom", 4);
            await realm.SetAsync(h, "pinned", true);
        }
        Equal(start + 3, realm.RoundTrips);
        Equal("""{"$c":2,"type":"point","x":3,"y":4}""", await realm.CallAsync($$"""{"op":"get","h":{{h}},"name":"center","byValue":true}""", Node));
        Equal((4.0, "satellite", 15.0, true), (await realm.GetNumberAsync(h, "zoom"), await realm.GetStringAsync(h, "basemap"), await realm.GetNumberAsync(h, "rotation"), await realm.GetBooleanAsync(h, "pinned")));

        var e = await Throws(async () =>
        {
            await using (scope.BeginBatch())
            {
                await realm.SendAsync(Set("zoom", 6));
                await realm.SendAsync($$"""{"op":"call","h":{{h}},"name":"noSuchMethod"}""");
                await realm.SetAsync(h, "rotation", 30);
            }
        });
        Equal("1", string.Join(", ", ((JsBatchException)e).Errors.Select(x => x.Index)));
        Equal((6.0, 30.0), (await realm.GetNumberAsync(h, "zoom"), await realm.GetNumberAsync(h, "rotation")));

        // A scope disposed with a batch open sends the queue before releasing its handles.
        var batch = scope.BeginBatch();
        await realm.SetAsync(h, "zoom", 7);
        start = realm.RoundTrips;
        await scope.DisposeAsync();
        Equal(start + 2, realm.RoundTrips);
        await batch.DisposeAsync();
        Equal(start + 2, realm.RoundTrips);
        Equal(baseline, await seam.Census());
    }

    /// <summary>
    /// Two transports on one page: each has its own interoper.js instance, so its own handles, census and
    /// callback ids.
    /// </summary>
    public static async Task Independent(Seam a, Seam b)
    {
        var baseline = (await a.Census(), await b.Census());
        var (ha, hb) = (await Create(a, MapView), await Create(b, MapView));
        Equal((baseline.Item1 + 1, baseline.Item2 + 1), (await a.Census(), await b.Census()));

        var (gotA, gotB) = (new TaskCompletionSource<string>(), new TaskCompletionSource<string>());
        var (la, lb) = (a.Callbacks.Register(args => { gotA.TrySetResult(args); return null; }), b.Callbacks.Register(args => { gotB.TrySetResult(args); return null; }));
        var sa = await Value(a, $$$"""{"op":"subscribe","h":{{{ha}}},"method":"on","name":"click","callback":{"$f":{{{la}}}}}""");
        var sb = await Value(b, $$$"""{"op":"subscribe","h":{{{hb}}},"method":"on","name":"click","callback":{"$f":{{{lb}}}}}""");
        await a.Send($$"""{"op":"call","h":{{ha}},"name":"emit","args":["click",{"from":"a"}]}""");
        await b.Send($$"""{"op":"call","h":{{hb}},"name":"emit","args":["click",{"from":"b"}]}""");
        Equal("""[{"from":"a"}]""", await gotA.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Equal("""[{"from":"b"}]""", await gotB.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await Release(a, ha, (int)sa!);
        await Release(b, hb, (int)sb!);
        a.Callbacks.Release(la);
        b.Callbacks.Release(lb);
        Equal(baseline, (await a.Census(), await b.Census()));
    }

    // --- Helpers ---

    private static readonly JsonRead<JsonNode?> Node = static (ref reader) => JsonNode.Parse(ref reader);

    private static async Task<JsonNode?> Value(Seam seam, string op) => JsEnvelope.Read(await seam.Send(op), Node);

    private static async Task<int> Create(Seam seam, int classId, string bag = "{}") =>
        (int)(await Value(seam, $$"""{"op":"create","t":{{classId}},"args":[{{bag}}]}"""))!["$h"]!;

    private static Task<string> Release(Seam seam, params int[] ids) =>
        seam.Send($$"""{"op":"release","ids":[{{string.Join(",", ids)}}]}""");
}

/// <summary>Assertions for the framework-free suites: a failed one throws <see cref="ContractException"/>.</summary>
public static class Check
{
    public static void Expect(bool condition, string what)
    {
        if (!condition) throw new ContractException($"Expected {what}");
    }

    public static void Equal<T>(T expected, T actual) =>
        Expect(EqualityComparer<T>.Default.Equals(expected, actual), $"{expected}, got {actual}");

    public static void Equal(string expectedJson, JsonNode? actual) =>
        Expect(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actual), $"{expectedJson}, got {actual?.ToJsonString() ?? "null"}");

    public static async Task<JsException> Throws(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (JsException e)
        {
            return e;
        }
        throw new ContractException("Expected a JsException");
    }
}

public sealed class ContractException(string message) : Exception(message);
