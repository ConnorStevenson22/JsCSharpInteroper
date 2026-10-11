using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GoldenSlice.Async;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Interoper.Runtime.Tests;

/// <summary>
/// Realm, scopes and identity (design §3.1, §3.2) over the in-memory fixture fakes, through the golden slice's async
/// bindings (tests/GoldenSlice), which its suites cover in full.
/// </summary>
public sealed class RealmTests
{
    private static readonly JsClassRegistry Classes = DemoMaps.Classes;

    private readonly RecordingTransport transport = new(InMemoryDemoMaps.Create());

    private async Task<JsRealm> StartedRealm(JsClassRegistry? classes = null)
    {
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), classes ?? Classes);
        await realm.StartAsync();
        return realm;
    }

    [Fact]
    public async Task OneJsObjectIsOneCSharpObjectOfItsMostDerivedType() // R19
    {
        var scope = new JsScope(await StartedRealm());
        var view = await MapView.CreateAsync(scope, new());
        var marker = await Marker.CreateAsync(scope, new());
        await view.SetSelectedAsync(marker);
        Assert.Same(marker, (await view.GetSelectedAsync(scope)).Value);
        Assert.Same(view, scope.Wrap<MapView>(view.Handle, DemoMaps.MapViewId));

        Shape polygon = await Polygon.CreateAsync(scope, new());
        Assert.IsType<Polygon>(await polygon.CloneAsync(scope)); // read through the base: the registry picks Polygon
    }

    [Fact]
    public async Task DisposalReleasesEveryHandleInOneOpAndEveryCallback() // R21, R24
    {
        var realm = await StartedRealm();
        var baseline = (transport.Census(), transport.Callbacks.Count);
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var marker = await Marker.CreateAsync(scope, new());
        await view.OnClickAsync(scope, _ => { }); // a subscription handle and a listener
        Assert.Equal((baseline.Item1 + 3, baseline.Item2 + 1), (transport.Census(), transport.Callbacks.Count));

        transport.Ops.Clear();
        await scope.DisposeAsync();
        var release = JsonNode.Parse(Assert.Single(transport.Ops))!;
        Assert.Equal("release", (string?)release["op"]);
        var ids = release["ids"]!.AsArray().Select(id => (int)id!).ToList();
        Assert.Equal(3, ids.Count); // the view, the marker and the subscription
        Assert.Contains(view.Handle, ids);
        Assert.Contains(marker.Handle, ids);
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count));

        await scope.DisposeAsync(); // once only
        Assert.Single(transport.Ops);
        Assert.Throws<ObjectDisposedException>(() => scope.Wrap<MapView>(view.Handle, DemoMaps.MapViewId));
        Assert.Throws<ObjectDisposedException>(() => scope.Wrap<Marker>(marker.Handle, DemoMaps.MarkerId));
    }

    [Fact]
    public async Task ASubscriptionCanBeReleasedBeforeItsScope() // R24
    {
        var realm = await StartedRealm();
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var baseline = (transport.Census(), transport.Callbacks.Count);
        var watch = scope.Subscribe(view.Handle, "watch", "zoom", JsJson.ReadNumber, _ => { }, null, null); // sync mode
        var click = await scope.SubscribeAsync(view.Handle, "on", "click", ClickEvent.Read, _ => { }, null, null); // async mode

        transport.Ops.Clear();
        watch.Dispose();
        await click.DisposeAsync();
        Assert.All(transport.Ops, op => Assert.StartsWith("""{"op":"release","ids":[""", op, StringComparison.Ordinal));
        Assert.Equal(2, transport.Ops.Count);
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count));

        watch.Dispose(); // once only
        await click.DisposeAsync();
        await scope.DisposeAsync();
        Assert.Equal(3, transport.Ops.Count); // the scope's own release
    }

    [Fact]
    public async Task AStreamSubscribesWhenFirstReadAndUnsubscribesWhenCancelled() // R24
    {
        var memory = InMemoryDemoMaps.Create();
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(memory), Classes);
        await realm.StartAsync();
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var baseline = (memory.Census(), memory.Callbacks.Count);
        using var cancel = new CancellationTokenSource();
        var clicks = view.OnClickStreamAsync(scope, new EventOptions { PreventDefault = true }, cancel.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.Equal(baseline, (memory.Census(), memory.Callbacks.Count)); // nothing until it's read

        var next = clicks.MoveNextAsync();
        Assert.Equal((baseline.Item1 + 1, baseline.Item2 + 1), (memory.Census(), memory.Callbacks.Count));
        // The options went with the subscribe op, projecting to the record's members by default.
        Assert.Equal("""{"preventDefault":true,"fields":["type","x","y","button","mapPoint"]}""", Assert.Single(memory.SubscribeOptions));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
        await clicks.DisposeAsync();
        Assert.Equal(baseline, (memory.Census(), memory.Callbacks.Count));
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task AStreamWhoseScopeIsDisposedWhileItSubscribesEndsNormally() // R24
    {
        var realm = await StartedRealm();
        var baseline = (transport.Census(), transport.Callbacks.Count);
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var zooms = view.WatchZoomStreamAsync(scope, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var held = new TaskCompletionSource();
        transport.Hold = held.Task;
        var next = zooms.MoveNextAsync().AsTask(); // the subscribe op is in flight
        var disposing = scope.DisposeAsync().AsTask();
        transport.Hold = null;
        held.SetResult();
        await disposing;

        Assert.False(await next);
        await zooms.DisposeAsync();
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count)); // the late subscription is released too
    }

    [Fact]
    public async Task AStreamEndsWithTheErrorOfAPayloadItCantRead() // R24
    {
        var realm = await StartedRealm();
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var baseline = (transport.Census(), transport.Callbacks.Count);
        var clicks = view.OnClickStreamAsync(scope, cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var next = clicks.MoveNextAsync();
        await realm.CallAsync(JsOp.Call(view.Handle, "emit", w =>
        {
            w.WriteStringValue("click");
            w.WriteStartObject();
            w.WriteString("x"u8, "not a number");
            w.WriteEndObject();
        }), JsJson.ReadBoolean);

        await Assert.ThrowsAnyAsync<Exception>(async () => await next);
        await clicks.DisposeAsync();
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count));
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task AFailedSubscribeReleasesItsListener() // R24
    {
        var realm = await StartedRealm();
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var baseline = (transport.Census(), transport.Callbacks.Count);
        Assert.ThrowsAny<Exception>(() => scope.Subscribe(view.Handle, "noSuchMethod", "zoom", JsJson.ReadNumber, _ => { }, null, null));
        await Assert.ThrowsAnyAsync<Exception>(() => scope.SubscribeAsync(view.Handle, "noSuchMethod", "click", ClickEvent.Read, _ => { }, null, null));
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count));
    }

    [Fact]
    public async Task ReadMatchesTheHandleAndClassAndSkipsOtherProperties()
    {
        var scope = new JsScope(await StartedRealm());
        var view = await MapView.CreateAsync(scope, new());
        var json = $$"""{"x":{"y":[1]},"$t":{{DemoMaps.MapViewId}},"$h":{{view.Handle}}}""";
        Assert.Same(view, Json.Read(json, scope.Read<MapView>));
        var error = Assert.Throws<System.Text.Json.JsonException>(() => Json.Read("""{"$t":1}""", scope.Read<MapView>));
        Assert.Contains("\"$h\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRegistryCanPreloadModules() // design §3.5: sync mode loads its modules up front
    {
        await StartedRealm(new JsClassRegistry().Preload("a.js").Preload("b.js"));
        Assert.Equal("""{"op":"load","modules":["a.js","b.js"]}""", Assert.Single(transport.Ops));
    }

    [Fact]
    public async Task SyncDisposalReleasesThroughTheSyncSeam()
    {
        var realm = await StartedRealm();
        var baseline = transport.Census();
        using (var scope = new JsScope(realm))
        {
            await MapView.CreateAsync(scope, new());
            await MapView.CreateAsync(scope, new());
            transport.Ops.Clear();
        }
        Assert.StartsWith("""{"op":"release","ids":[""", Assert.Single(transport.Ops), StringComparison.Ordinal);
        Assert.Equal(baseline, transport.Census());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandleIsReleasedWhenItsLastScopeIsDisposed(bool childFirst)
    {
        var realm = await StartedRealm();
        var baseline = transport.Census();
        var (parent, child) = (new JsScope(realm), new JsScope(realm));
        var view = await MapView.CreateAsync(parent, new());
        Assert.Same(view, child.Wrap<MapView>(view.Handle, DemoMaps.MapViewId));

        var (first, last) = childFirst ? (child, parent) : (parent, child);
        transport.Ops.Clear();
        await first.DisposeAsync();
        Assert.Empty(transport.Ops); // the other scope still owns it
        Assert.Equal(baseline + 1, transport.Census());
        await last.DisposeAsync();
        Assert.Single(transport.Ops);
        Assert.Equal(baseline, transport.Census());
    }

    [Fact]
    public async Task TheIdentityCacheIsWeakButOwnershipIsNot()
    {
        var realm = await StartedRealm();
        var scope = new JsScope(realm);
        var handle = (int)(await Send(scope, """{"op":"create","t":5,"args":[]}"""))!["$h"]!;
        var weak = WrapAndDrop(scope, handle);
        GC.Collect();
        Assert.False(weak.IsAlive);

        Assert.NotNull(scope.Wrap<MapView>(handle, DemoMaps.MapViewId)); // a new object for the same handle
        await scope.DisposeAsync();
        Assert.Equal(0, transport.Census());
    }

    [Fact]
    public async Task AHandleThatArrivesAfterDisposalIsReleased()
    {
        var realm = await StartedRealm();
        var baseline = transport.Census();
        var scope = new JsScope(realm);
        var create = scope.Transport.InvokeAsync("""{"op":"create","t":5,"args":[]}"""); // in flight
        await scope.DisposeAsync();
        var handle = (int)JsEnvelope.Read(await create, static (ref reader) => JsonNode.Parse(ref reader))!["$h"]!;

        Assert.Throws<ObjectDisposedException>(() => scope.Wrap<MapView>(handle, DemoMaps.MapViewId));
        Assert.Equal(baseline, transport.Census());
    }

    [Fact]
    public async Task SyncDisposalWithoutTheSyncSeamLeavesEverythingForDisposeAsync()
    {
        var realm = new JsRealm(async () => await JsRuntimeTransport.StartAsync(new StubRuntime(), "/meta.js"), Classes);
        await realm.StartAsync();
        var scope = new JsScope(realm);
        scope.Own(1);
        Assert.Throws<InvalidOperationException>(scope.Dispose);
        scope.Own(2); // not disposed yet
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WrapAndDrop(JsScope scope, int handle) => new(scope.Wrap<MapView>(handle, DemoMaps.MapViewId));

    [Fact]
    public async Task AnUnknownClassIdFailsButItsHandleIsStillReleased()
    {
        var scope = new JsScope(await StartedRealm());
        var handle = (int)(await Send(scope, """{"op":"create","t":5,"args":[]}"""))!["$h"]!;
        var e = Assert.Throws<JsException>(() => scope.Wrap<MapView>(handle, 0));
        Assert.Equal(("InteroperError", "No live type is registered for class id 0."), (e.Name, e.Message));

        await scope.DisposeAsync();
        Assert.Equal(0, transport.Census());
    }

    [Fact] // R24: an event without a payload calls a parameterless handler, and nothing of the event is sent
    public async Task AnEventWithoutAPayloadCallsAParameterlessHandler()
    {
        var scope = new JsScope(await StartedRealm());
        var emitter = (int)(await Send(scope, $$"""{"op":"create","t":{{DemoMaps.EmitterId}},"args":[]}"""))!["$h"]!;
        var fired = 0;
        using var sync = scope.Subscribe(emitter, "on", "tick", () => fired++, null);
        await using var async = await scope.SubscribeAsync(emitter, "on", "tick", () => fired++, null);
        Assert.Equal(2, transport.Ops.Count(op => op.Contains("\"name\":\"tick\"", StringComparison.Ordinal) && op.EndsWith("\"options\":{\"fields\":[]}}", StringComparison.Ordinal)));

        await scope.Realm.CallAsync(JsOp.Call(emitter, "emit", w => { w.WriteStringValue("tick"); w.WriteStartObject(); w.WriteNumber("x", 1); w.WriteEndObject(); }), JsJson.ReadBoolean);
        Assert.Equal(2, fired);
        await scope.DisposeAsync();
    }

    [Fact] // design §2, R19 step 1: JS can hand back an abstract class's own instance (an element's class by its tag, say)
    public async Task AnAbstractClassIdIsRegisteredAsItsOwnLiveType()
    {
        var scope = new JsScope(await StartedRealm());
        var handle = (int)(await Send(scope, """{"op":"create","t":5,"args":[]}"""))!["$h"]!;
        Assert.Equal(typeof(GoldenSlice.Async.Layers.Layer), scope.Wrap<JsObject>(handle, DemoMaps.LayerId).GetType());
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task DisposalSwallowsADroppedCircuit()
    {
        var scope = new JsScope(await StartedRealm());
        await MapView.CreateAsync(scope, new());
        transport.Fail = new JSDisconnectedException("gone");
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task BeforeTheRealmStartsLiveCallsThrowAndDisposalIsANoOp()
    {
        // A prerendering component's realm never starts.
        var scope = new JsScope(new JsRealm(() => throw new InvalidOperationException("never started"), Classes));
        Assert.Throws<JsRealmUnavailableException>(() => scope.Transport);
        Assert.Throws<JsRealmUnavailableException>(() => scope.SyncTransport);
        Assert.Throws<JsRealmUnavailableException>(() => scope.Register(_ => null));
        await scope.DisposeAsync();
        scope.Dispose();
    }

    [Fact]
    public async Task AFailedStartCanBeRetried()
    {
        var attempts = 0;
        var realm = new JsRealm(
            () => ++attempts == 1 ? Task.FromException<IJsTransport>(new JsException("Error", "import failed", null)) : Task.FromResult<IJsTransport>(transport),
            Classes);
        await Assert.ThrowsAsync<JsException>(realm.StartAsync);
        await realm.StartAsync();
        await realm.StartAsync();
        Assert.Equal(2, attempts);
        Assert.Same(transport, realm.Transport);
    }

    [Fact]
    public async Task SyncBindingsFailFastWithoutTheSyncSeam()
    {
        var js = new StubRuntime();
        var realm = new JsRealm(async () => await JsRuntimeTransport.StartAsync(js, "/meta.js"), Classes);
        await realm.StartAsync();
        var e = Assert.Throws<InvalidOperationException>(() => realm.SyncTransport);
        Assert.Contains("regenerate with `--api async`", e.Message, StringComparison.Ordinal);

        await realm.DisposeAsync();
        Assert.True(js.Disposed); // the realm releases its transport's module
    }

    [Fact]
    public async Task ACallbackReadsItsArgumentsWithItsScopeAndWritesItsResult() // R21
    {
        var realm = await StartedRealm();
        var baseline = transport.Callbacks.Count;
        var scope = new JsScope(realm);
        var view = await MapView.CreateAsync(scope, new());
        var calls = new List<(MapView, double, Opt<string>)>();
        var id = scope.Callback((MapView v, double n, Opt<string> s) => { calls.Add((v, n, s)); return $"{n}{(s.TryGetValue(out string? t) ? t : "")}"; },
            scope.Read<MapView>, JsJson.ReadNumber, static (ref r) => JsJson.ReadOpt(ref r, JsJson.ReadString), JsJson.WriteString);
        var arguments = $$"""[{"$t":{{DemoMaps.MapViewId}},"$h":{{view.Handle}}},2""";

        Assert.Equal("\"2!\"", transport.Callbacks.InvokeCallback(id, arguments + ",\"!\"]"));
        Assert.Equal("\"3\"", transport.Callbacks.InvokeCallback(id, arguments.Replace(",2", ",3") + "]")); // a missing argument is undefined
        Assert.Equal([(view, 2.0, new Opt<string>("!")), (view, 3.0, default)], calls);
        Assert.Null(transport.Callbacks.InvokeCallback(scope.Callback(() => { }), "[]")); // an Action's result is undefined

        await scope.DisposeAsync();
        Assert.Equal(baseline, transport.Callbacks.Count); // released with their scope
    }

    [Fact] // §2: a generic class has no registry entry; its reader is given a factory
    public async Task ReadWithAFactoryKeepsIdentity()
    {
        var scope = new JsScope(await StartedRealm());
        var view = await MapView.CreateAsync(scope, new());
        var json = $$"""{"$t":0,"$h":{{view.Handle}}}""";
        Assert.Same(view, Json.Read(json, (ref r) => scope.Read<MapView>(ref r, static (_, _) => throw new InvalidOperationException())));
        // Another type is an error, not a cast exception.
        var error = Assert.Throws<JsException>(() => Json.Read(json, (ref r) => scope.Read<Marker>(ref r, static (_, _) => throw new InvalidOperationException())));
        Assert.Equal("InteroperError", error.Name);
        Assert.Throws<JsException>(() => scope.Wrap<Marker>(view.Handle, DemoMaps.MapViewId));
    }

    [Fact]
    public async Task CallbacksRunOnTheDispatcher() // R21
    {
        var realm = await StartedRealm();
        var posted = new Queue<Action>();
        var calls = 0;
        Func<string, string?> formatter = _ => { calls++; return "\"#\""; };

        // Posted for later: JS has moved on and gets undefined.
        var later = new JsScope(realm) { Dispatch = action => { posted.Enqueue(action); return new TaskCompletionSource().Task; } };
        var marker = await Send(later, $$$"""{"op":"create","t":6,"args":[{"labelFormatter":{"$f":{{{later.Register(formatter)}}}}}]}""");
        Assert.Equal("{}", await later.Transport.InvokeAsync($$"""{"op":"get","h":{{marker!["$h"]}},"name":"displayLabel"}"""));
        Assert.Equal(0, calls);
        posted.Dequeue()();
        Assert.Equal(1, calls);

        // Run inline (already on the dispatcher): JS gets the result.
        var inline = new JsScope(realm) { Dispatch = action => { action(); return Task.CompletedTask; } };
        marker = await Send(inline, $$$"""{"op":"create","t":6,"args":[{"labelFormatter":{"$f":{{{inline.Register(formatter)}}}}}]}""");
        Assert.Equal("""{"v":"#"}""", await inline.Transport.InvokeAsync($$"""{"op":"get","h":{{marker!["$h"]}},"name":"displayLabel"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Auto")]
    [InlineData("jsruntime")]
    public async Task AddInteroperPicksJsRuntimeOutsideTheBrowser(string? configured)
    {
        await using var services = Services(configured);
        await using var scope = services.CreateAsyncScope();
        var realm = scope.ServiceProvider.GetRequiredService<JsRealm>();
        await realm.StartAsync();
        Assert.IsType<JsRuntimeTransport>(realm.Transport);

        await using var other = services.CreateAsyncScope();
        Assert.NotSame(realm, other.ServiceProvider.GetRequiredService<JsRealm>()); // one realm per circuit
    }

    [Fact]
    public async Task AddInteroperFailsFastOnJsImportOutsideTheBrowserOrOnABadSetting()
    {
        await using var services = Services("JsImport");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(services.GetRequiredService<JsRealm>().StartAsync);
        Assert.Contains("regenerate with `--api async`", e.Message, StringComparison.Ordinal);

        await using var bad = Services("Fast");
        e = Assert.Throws<InvalidOperationException>(bad.GetRequiredService<JsRealm>);
        Assert.Equal("Interop:Transport must be Auto, JsRuntime or JsImport, not 'Fast'.", e.Message);
    }

    private static ServiceProvider Services(string? transportSetting) => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Interop:Transport"] = transportSetting })
            .Build())
        .AddSingleton<IJSRuntime>(new StubRuntime())
        .AddInteroper("/meta.js", Classes)
        .BuildServiceProvider();

    // A raw op, for what the golden slice doesn't bind.
    private static Task<JsonNode?> Send(JsScope scope, string op) =>
        scope.Realm.CallAsync(op, static (ref reader) => JsonNode.Parse(ref reader));
}
