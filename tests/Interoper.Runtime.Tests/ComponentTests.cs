using System.Text.Json;
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
public sealed class ComponentTests
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

    private sealed class Services(JsRealm realm) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(JsRealm) ? realm : null;
    }
}
