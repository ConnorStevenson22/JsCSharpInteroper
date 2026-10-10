using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Async = GoldenSlice.Async;
using Sync = GoldenSlice.Sync;

namespace Interoper.Runtime.Tests;

/// <summary>
/// DOM elements (R13) and the generated components (R28) without a browser; tests/Interoper.E2E/ComponentTests runs
/// them interactively.
/// </summary>
public sealed partial class ComponentTests
{
    [Fact] // R13
    public void AnElementReferenceIsWrittenAsItsCaptureId()
    {
        Assert.Equal("""{"$e":"a1"}""", Json.Write(new ElementReference("a1"), JsElement.Write));
        var e = Assert.Throws<InvalidOperationException>(() => Json.Write(default(ElementReference), JsElement.Write));
        Assert.StartsWith("The ElementReference has no element yet", e.Message, StringComparison.Ordinal);
    }

    [Fact] // R13: a DOM-typed property is setter-only; the element is the one rendered for the reference
    public async Task SetContainerSendsTheRenderedElement()
    {
        var container = new FakeContainer();
        var transport = InMemoryDemoMaps.Create().DefineElement("c1", container);
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), Async.DemoMaps.Classes);
        await realm.StartAsync();
        await using var scope = new JsScope(realm);
        var view = await Async.MapView.CreateAsync(scope, new());
        var element = Handle(transport, JsOp.Wrap(w => JsElement.Write(w, new("c1")))); // one JS object has one handle
        await view.SetContainerAsync(new ElementReference("c1"));
        Assert.Equal(element, Handle(transport, JsOp.Get(view.Handle, "container")));
        await view.SetContainerAsync(null);
        Assert.Null(Handle(transport, JsOp.Get(view.Handle, "container")));

        var sync = new JsScope(new JsRealm(() => Task.FromResult<IJsTransport>(transport), Sync.DemoMaps.Classes));
        await sync.Realm.StartAsync();
        var syncView = Sync.MapView.Create(sync, new());
        syncView.SetContainer(new ElementReference("c1"));
        Assert.Equal(element, Handle(transport, JsOp.Get(syncView.Handle, "container")));
        sync.Dispose();
    }

    [Fact] // R28
    public void ACustomEventsDetailIsReadFromItsJson()
    {
        var args = new JsEventArgs { Json = """{"x":1,"y":2,"mapX":3,"mapY":4}""" };
        Assert.Equal(new Async.DemoMapClickDetail { X = 1, Y = 2, MapX = 3, MapY = 4 }, args.Read(Async.DemoMapClickDetail.Read));
        Assert.True(new JsEventArgs { Json = """{"$u":1}""" }.Read(static (ref r) => JsJson.ReadOpt(ref r, JsJson.ReadNumber)).IsUndefined);
    }

    [Fact] // R28: static rendering (and prerendering) renders what the parent supplied as attributes, and the slots
    public async Task StaticRenderingRendersSuppliedAttributesAndSlots()
    {
        Assert.Equal("""<demo-map basemap="3d-relief" zoom="0.5"><div slot="toolbar" style="display:contents"><b>tools</b></div>overlay</demo-map>""",
            await RenderAsync<Async.DemoMap>(new()
            {
                [nameof(Async.DemoMap.Basemap)] = Async.Basemap._3dRelief,
                [nameof(Async.DemoMap.Zoom)] = 0.5,
                [nameof(Async.DemoMap.Center)] = new Async.PointSnapshot { X = 1, Y = 1 }, // set once interactive
                [nameof(Async.DemoMap.Toolbar)] = (RenderFragment)(b => b.AddMarkupContent(0, "<b>tools</b>")),
                [nameof(Async.DemoMap.ChildContent)] = (RenderFragment)(b => b.AddContent(0, "overlay")),
            }));
        // Not supplied, not rendered: the element keeps its own defaults. Unmatched attributes are rendered as given.
        Assert.Equal("""<demo-map id="m" zoom="0"></demo-map>""",
            await RenderAsync<Sync.DemoMap>(new() { ["id"] = "m", [nameof(Sync.DemoMap.Zoom)] = 0.0 }));
    }

    [Fact] // R28: once interactive, the component loads its element's module, wraps the element, sets the properties, then is ready
    public async Task AnInteractiveComponentSetsItsElementsPropertiesThenIsReady()
    {
        var element = new FakeDemoMapElement();
        var transport = new GatedTransport(InMemoryDemoMaps.Create().DefineElement("map", element));
        transport.Gate.SetResult();
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), Async.DemoMaps.Classes);
        await using var renderer = new InteractiveRenderer(new Services(realm));
        var ready = new TaskCompletionSource<Async.DemoMapElement>();
        var map = await renderer.RenderAsync<Async.DemoMap>(new()
        {
            [nameof(Async.DemoMap.Center)] = new Async.PointSnapshot { X = 2, Y = 3 },
            [nameof(Async.DemoMap.OnElementReady)] = EventCallback.Factory.Create<Async.DemoMapElement>(new object(), ready.SetResult),
        });

        var live = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Same(map.Element, live);
        Assert.Equal(new Async.PointSnapshot { X = 2, Y = 3 }, await (await live.GetViewAsync(new JsScope(realm))).GetCenterAsync());
        Assert.Equal(["load @acme/demo-maps/demo-map.js", "wrap", "set center"], transport.Ops.Take(3));
        Assert.Empty(renderer.Errors);
    }

    [Fact] // R28: a component disposed while it fetches its element stops quietly, and the handle is released
    public async Task AComponentDisposedWhileItFetchesItsElementStops()
    {
        var transport = new GatedTransport(InMemoryDemoMaps.Create().DefineElement("map", new FakeDemoMapElement()));
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), Async.DemoMaps.Classes);
        await using var renderer = new InteractiveRenderer(new Services(realm));
        var ready = false;
        var map = await renderer.RenderAsync<Async.DemoMap>(new()
        {
            [nameof(Async.DemoMap.Center)] = new Async.PointSnapshot { X = 2, Y = 3 },
            [nameof(Async.DemoMap.OnElementReady)] = EventCallback.Factory.Create<Async.DemoMapElement>(new object(), () => ready = true),
        });
        await transport.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // the wrap op is in flight

        await renderer.Dispatcher.InvokeAsync(map.DisposeAsync().AsTask);
        transport.Gate.SetResult();
        for (var i = 0; i < 200 && transport.Census() > 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, transport.Census()); // the element's handle reached the disposed scope, which released it
        await renderer.Dispatcher.InvokeAsync(() => { });
        Assert.Empty(renderer.Errors);
        Assert.Null(map.Element);
        Assert.False(ready);
        Assert.DoesNotContain("set center", transport.Ops);
    }

    [Fact] // R28: @bind's echoes, the change events of values the component sent, aren't changes
    public void EchoesOfSentValuesAreNotChanges()
    {
        // Two quick changes: the echo of the first arrives after the second was sent, and so does the second's.
        var echoes = new JsEchoes<double>();
        echoes.Sent(3);
        echoes.Sent(3); // a render with the same value sends nothing
        Assert.True(echoes.IsEcho(3));
        echoes.Sent(4);
        echoes.Sent(5);
        Assert.True(echoes.IsEcho(4));
        Assert.True(echoes.IsEcho(5));
        // A change from JS, which the parent then renders: the element has it already, so no echo comes.
        Assert.False(echoes.IsEcho(7));
        echoes.Sent(7);
        echoes.Sent(8);
        Assert.False(echoes.IsEcho(7)); // 7 never echoed, so a 7 now is a change
        Assert.True(echoes.IsEcho(8));
        // An echo of the last value means the older ones have arrived or never will.
        echoes.Sent(9);
        echoes.Sent(10);
        Assert.True(echoes.IsEcho(10));
        Assert.False(echoes.IsEcho(9));
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var realm = new JsRealm(() => throw new InvalidOperationException("static rendering never starts the realm"), Async.DemoMaps.Classes);
        var services = new Services(realm);
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    // The handle of an op's result, or null for JS null.
    private static int? Handle(InMemoryTransport transport, string op)
    {
        var value = JsonDocument.Parse(transport.Invoke(op)).RootElement.GetProperty("v");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetProperty("$h").GetInt32();
    }

    private sealed class FakeContainer : InMemoryObject
    {
        public override string ClassName => "";
    }

    // A renderer of our own is the only way to run components interactively without a browser (bUnit has one too);
    // the renderer types warn that they change between releases (BL0006), which a test can live with.
#pragma warning disable BL0006
    /// <summary>An interactive renderer, so components start the realm and run <c>OnAfterRenderAsync</c>.</summary>
    private sealed class InteractiveRenderer(IServiceProvider services) : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, NullLoggerFactory.Instance)
    {
        public List<Exception> Errors { get; } = [];

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        protected override RendererInfo RendererInfo { get; } = new("Test", isInteractive: true);

        public Task<T> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        {
            var component = (T)InstantiateComponent(typeof(T));
            await RenderRootComponentAsync(AssignRootComponentId(component), ParameterView.FromDictionary(parameters));
            return component;
        });

        protected override void HandleException(Exception exception) => Errors.Add(exception);

        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    /// <summary>
    /// The in-memory transport, with each wrap op held until <see cref="Gate"/> opens, and its element taken to be the
    /// one defined as <c>map</c>, whatever id the renderer gave it. It records each op's kind (and name or module).
    /// </summary>
    private sealed partial class GatedTransport(InMemoryTransport inner) : IJsTransport
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Ops { get; } = [];

        public JsCallbacks Callbacks => inner.Callbacks;

        public int Census() => inner.Census();

        public async Task<string> InvokeAsync(string opJson)
        {
            var op = JsonDocument.Parse(opJson).RootElement;
            var kind = op.GetProperty("op").GetString()!;
            Ops.Add(kind switch
            {
                "load" => $"load {op.GetProperty("modules")[0].GetString()}",
                "set" => $"set {op.GetProperty("name").GetString()}",
                _ => kind,
            });
            if (kind == "wrap")
            {
                Reached.TrySetResult();
                await Gate.Task;
                opJson = ElementId().Replace(opJson, "\"$e\":\"map\"");
            }
            return inner.Invoke(opJson);
        }

        [GeneratedRegex("\"\\$e\":\"[^\"]*\"")]
        private static partial Regex ElementId();

        public Task<double> GetNumberAsync(int handle, string name) => inner.GetNumberAsync(handle, name);

        public Task<string> GetStringAsync(int handle, string name) => inner.GetStringAsync(handle, name);

        public Task<bool> GetBooleanAsync(int handle, string name) => inner.GetBooleanAsync(handle, name);

        public Task SetNumberAsync(int handle, string name, double value) => inner.SetNumberAsync(handle, name, value);

        public Task SetStringAsync(int handle, string name, string value) => inner.SetStringAsync(handle, name, value);

        public Task SetBooleanAsync(int handle, string name, bool value) => inner.SetBooleanAsync(handle, name, value);

        public Task<int> CensusAsync() => inner.CensusAsync();
    }

    private sealed class Services(JsRealm realm) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(JsRealm) ? realm : null;
    }
}
