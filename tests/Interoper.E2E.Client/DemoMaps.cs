using System.Text.Json.Nodes;
using Interoper.Contract;

namespace Interoper.E2E.Client;

/// <summary>What generated bindings for fixtures/demo-maps will provide, reduced to what the scope check needs.</summary>
public static class DemoMaps
{
    public const string Metadata = "/demo-maps.meta.js";

    public static readonly JsClassRegistry Classes = new JsClassRegistry()
        .Add(TransportContract.Point, (scope, handle) => new DemoObject(scope, handle))
        .Add(TransportContract.MapView, (scope, handle) => new DemoObject(scope, handle))
        .Add(TransportContract.Marker, (scope, handle) => new DemoObject(scope, handle));

    public static async Task<DemoObject> CreateAsync(JsScope scope, int classId, string bag = "{}") =>
        Wrap(scope, await SendAsync(scope, $$"""{"op":"create","t":{{classId}},"args":[{{bag}}]}"""));

    public static async Task<DemoObject> GetAsync(DemoObject target, string name) =>
        Wrap(target.Scope, await SendAsync(target.Scope, $$"""{"op":"get","h":{{target.Handle}},"name":"{{name}}"}"""));

    private static DemoObject Wrap(JsScope scope, JsonNode? handle) => scope.Wrap<DemoObject>((int)handle!["$h"]!, (int)handle["$t"]!);

    private static async Task<JsonNode?> SendAsync(JsScope scope, string op) =>
        JsEnvelope.Read(await scope.Transport.InvokeAsync(op), static (ref reader) => JsonNode.Parse(ref reader));
}

public sealed class DemoObject(JsScope scope, int handle) : JsObject(scope, handle);
