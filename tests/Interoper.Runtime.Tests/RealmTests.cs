using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Interoper.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Interoper.Runtime.Tests;

/// <summary>Realm, scopes and identity (design §3.1, §3.2) over the in-memory fixture fakes.</summary>
public sealed class RealmTests
{
    // Live types as generated code will declare and register them (the golden slice writes the real ones).
    private abstract class Shape(JsRealm realm, int handle) : JsObject(realm, handle);

    private sealed class Point(JsRealm realm, int handle) : Shape(realm, handle);

    private sealed class View(JsRealm realm, int handle) : JsObject(realm, handle);

    private sealed class Pin(JsRealm realm, int handle) : JsObject(realm, handle);

    private static readonly JsClassRegistry Classes = new JsClassRegistry()
        .Add(TransportContract.Point, (r, h) => new Point(r, h))
        .Add(TransportContract.MapView, (r, h) => new View(r, h))
        .Add(TransportContract.Marker, (r, h) => new Pin(r, h));

    private readonly RecordingTransport transport = new(InMemoryDemoMaps.Create());

    private async Task<JsRealm> StartedRealm()
    {
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), Classes);
        await realm.StartAsync();
        return realm;
    }

    [Fact]
    public async Task OneJsObjectIsOneCSharpObjectOfItsMostDerivedType() // R19
    {
        var scope = new JsScope(await StartedRealm());
        var view = await Create<View>(scope, TransportContract.MapView);

        var center = await Get<Shape>(scope, view, "center"); // read through the base: the registry picks Point
        Assert.IsType<Point>(center);
        Assert.Same(center, await Get<Point>(scope, view, "center"));
        Assert.Same(view, scope.Wrap<View>(view.Handle, TransportContract.MapView));
    }

    [Fact]
    public async Task DisposalReleasesEveryHandleInOneOpAndEveryCallback() // R21
    {
        var realm = await StartedRealm();
        var baseline = (transport.Census(), transport.Callbacks.Count);
        var scope = new JsScope(realm);
        var view = await Create<View>(scope, TransportContract.MapView);
        var center = await Get<Point>(scope, view, "center");
        var formatter = scope.Register(_ => "\"#\"");
        var pin = await Create<Pin>(scope, TransportContract.Marker, $$$"""{"labelFormatter":{"$f":{{{formatter}}}}}""");
        Assert.Equal((baseline.Item1 + 3, baseline.Item2 + 1), (transport.Census(), transport.Callbacks.Count));

        transport.Ops.Clear();
        await scope.DisposeAsync();
        var release = JsonNode.Parse(Assert.Single(transport.Ops))!;
        Assert.Equal("release", (string?)release["op"]);
        Assert.Equal([view.Handle, center.Handle, pin.Handle], release["ids"]!.AsArray().Select(id => (int)id!).Order());
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count));

        await scope.DisposeAsync(); // once only
        Assert.Single(transport.Ops);
        Assert.Throws<ObjectDisposedException>(() => scope.Wrap<View>(view.Handle, TransportContract.MapView));
    }

    [Fact]
    public async Task SyncDisposalReleasesThroughTheSyncSeam()
    {
        var realm = await StartedRealm();
        var baseline = transport.Census();
        using (var scope = new JsScope(realm))
        {
            await Create<View>(scope, TransportContract.MapView);
            await Create<View>(scope, TransportContract.MapView);
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
        var view = await Create<View>(parent, TransportContract.MapView);
        Assert.Same(view, child.Wrap<View>(view.Handle, TransportContract.MapView));

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

        Assert.NotNull(scope.Wrap<View>(handle, TransportContract.MapView)); // a new object for the same handle
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

        Assert.Throws<ObjectDisposedException>(() => scope.Wrap<View>(handle, TransportContract.MapView));
        Assert.Equal(baseline, transport.Census());
    }

    [Fact]
    public async Task AScopeOwnsBareHandlesSuchAsSubscriptions() // R21
    {
        var realm = await StartedRealm();
        var baseline = (transport.Census(), transport.Callbacks.Count);
        var scope = new JsScope(realm);
        var view = await Create<View>(scope, TransportContract.MapView);
        var listener = scope.Register(_ => null);
        var subscription = (int)(await Send(scope, $$$"""{"op":"subscribe","h":{{{view.Handle}}},"method":"on","name":"click","callback":{"$f":{{{listener}}}}}"""))!;
        scope.Own(subscription);

        transport.Ops.Clear();
        await scope.DisposeAsync();
        Assert.Single(transport.Ops);
        Assert.Equal(baseline, (transport.Census(), transport.Callbacks.Count));
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
    private static WeakReference WrapAndDrop(JsScope scope, int handle) => new(scope.Wrap<View>(handle, TransportContract.MapView));

    [Fact]
    public async Task AnUnknownClassIdFailsButItsHandleIsStillReleased()
    {
        var scope = new JsScope(await StartedRealm());
        var handle = (int)(await Send(scope, """{"op":"create","t":5,"args":[]}"""))!["$h"]!;
        var e = Assert.Throws<JsException>(() => scope.Wrap<View>(handle, 0));
        Assert.Equal(("InteroperError", "No live type is registered for class id 0."), (e.Name, e.Message));

        await scope.DisposeAsync();
        Assert.Equal(0, transport.Census());
    }

    [Fact]
    public async Task DisposalSwallowsADroppedCircuit()
    {
        var scope = new JsScope(await StartedRealm());
        await Create<View>(scope, TransportContract.MapView);
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
    public async Task CallbacksRunOnTheDispatcher() // R21
    {
        var realm = await StartedRealm();
        var posted = new Queue<Action>();
        var calls = 0;
        Func<string, string?> formatter = _ => { calls++; return "\"#\""; };

        // Posted for later: JS has moved on and gets undefined.
        var later = new JsScope(realm) { Dispatch = action => { posted.Enqueue(action); return new TaskCompletionSource().Task; } };
        var pin = await Create<Pin>(later, TransportContract.Marker, $$$"""{"labelFormatter":{"$f":{{{later.Register(formatter)}}}}}""");
        Assert.Equal("{}", await later.Transport.InvokeAsync($$"""{"op":"get","h":{{pin.Handle}},"name":"displayLabel"}"""));
        Assert.Equal(0, calls);
        posted.Dequeue()();
        Assert.Equal(1, calls);

        // Run inline (already on the dispatcher): JS gets the result.
        var inline = new JsScope(realm) { Dispatch = action => { action(); return Task.CompletedTask; } };
        pin = await Create<Pin>(inline, TransportContract.Marker, $$$"""{"labelFormatter":{"$f":{{{inline.Register(formatter)}}}}}""");
        Assert.Equal("""{"v":"#"}""", await inline.Transport.InvokeAsync($$"""{"op":"get","h":{{pin.Handle}},"name":"displayLabel"}"""));
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

    // --- What generated code will do ---

    private static async Task<T> Create<T>(JsScope scope, int classId, string bag = "{}") where T : JsObject =>
        Wrap<T>(scope, await Send(scope, $$"""{"op":"create","t":{{classId}},"args":[{{bag}}]}"""));

    private static async Task<T> Get<T>(JsScope scope, JsObject target, string name) where T : JsObject =>
        Wrap<T>(scope, await Send(scope, $$"""{"op":"get","h":{{target.Handle}},"name":"{{name}}"}"""));

    private static T Wrap<T>(JsScope scope, JsonNode? handle) where T : JsObject => scope.Wrap<T>((int)handle!["$h"]!, (int)handle["$t"]!);

    private static Task<JsonNode?> Send(JsScope scope, string op) =>
        scope.Realm.CallAsync(op, static (ref reader) => JsonNode.Parse(ref reader));
}
