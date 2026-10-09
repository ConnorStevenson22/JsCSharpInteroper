using System.Text.Json.Nodes;
using Interoper.Contract;

namespace Interoper.E2E.Client;

/// <summary>What generated bindings for fixtures/demo-maps will provide, reduced to what the scope check needs.</summary>
public static class DemoMaps
{
    public const string Metadata = "/demo-maps.meta.js";

    public static readonly JsClassRegistry Classes = new JsClassRegistry()
        .Add(TransportContract.Point, (realm, handle) => new DemoObject(realm, handle))
        .Add(TransportContract.MapView, (realm, handle) => new DemoObject(realm, handle))
        .Add(TransportContract.Marker, (realm, handle) => new DemoObject(realm, handle));

    public static async Task<DemoObject> CreateAsync(JsScope scope, int classId, string bag = "{}") =>
        Wrap(scope, await SendAsync(scope, $$"""{"op":"create","t":{{classId}},"args":[{{bag}}]}"""));

    public static async Task<DemoObject> GetAsync(JsScope scope, DemoObject target, string name) =>
        Wrap(scope, await SendAsync(scope, $$"""{"op":"get","h":{{target.Handle}},"name":"{{name}}"}"""));

    private static DemoObject Wrap(JsScope scope, JsonNode? handle) => scope.Wrap<DemoObject>((int)handle!["$h"]!, (int)handle["$t"]!);

    private static Task<JsonNode?> SendAsync(JsScope scope, string op) =>
        scope.Realm.CallAsync(op, static (ref reader) => JsonNode.Parse(ref reader));
}

public sealed class DemoObject(JsRealm realm, int handle) : JsObject(realm, handle);
