using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>What the golden slice doesn't show; <see cref="GoldenSliceTests"/> covers the rest.</summary>
public sealed class GeneratorTests
{
    [Fact] // R10
    public void AnOpenEndedLiteralUnionIsAnExtensibleEnum()
    {
        var result = Generator.Generate(Fixture.Ir, Fixture.Profile with { Include = ["MapView.cursor"] }, new(Api.Sync, "Acme.Maps"));
        var cursor = File(result, "Cursor.cs");
        Assert.Contains("public readonly partial record struct Cursor(string Value)", cursor);
        Assert.Contains("public static Cursor Crosshair { get; } = new(\"crosshair\");", cursor);
        Assert.Contains("public Opt<Cursor> Cursor { get; init; }", File(result, "MapViewSnapshot.cs"));
    }

    [Fact] // §2: a TS enum is a C# enum
    public void EnumsKeepTheirMemberNames()
    {
        var result = Generate("""
            { "kind": "enum", "name": "Mode", "members": [{ "name": "fast", "value": "fast-mode" }, { "name": "slow", "value": "slow" }] },
            { "kind": "enum", "name": "Level", "members": [{ "name": "Low", "value": 1 }, { "name": "High", "value": 10 }] }
            """, "Mode", "Level");
        Assert.Contains("    Fast,\n    Slow,\n", File(result, "Mode.cs"));
        Assert.Contains("\"fast-mode\" => Mode.Fast,", File(result, "Mode.cs"));
        Assert.Contains("    Low = 1,\n    High = 10,\n", File(result, "Level.cs"));
    }

    [Fact] // R15, R3, R26
    public void ADataRecordHasItsBasesMembers()
    {
        var result = Generate("""
            { "kind": "interface", "name": "Base", "members": [{ "kind": "property", "name": "id", "type": { "kind": "primitive", "name": "string" } }] },
            { "kind": "interface", "name": "Options", "extends": [{ "kind": "ref", "name": "Base", "module": "@acme/maps/a.js" }], "members": [
              { "kind": "property", "name": "options", "type": { "kind": "primitive", "name": "number" } },
              { "kind": "property", "name": "default", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "boolean" }, { "kind": "primitive", "name": "null" }] } }] }
            """, "Options");
        var options = File(result, "Options.cs");
        Assert.Contains("""
                public required double JsOptions { get; init; }
                public required Opt<bool> Default { get; init; }
                public required string Id { get; init; }
            """, options);
        Assert.Contains("case \"default\": @default = JsJson.ReadOpt(ref reader, JsJson.ReadBoolean); break;", options);
        Assert.Contains("string id = null!;", options);
        Assert.Equal("IOP2006 Options.options: JsOptions: Options is taken (R26)", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R3
    public void AnAliasOfOneTypeIsThatType()
    {
        var result = Generate("""
            { "kind": "alias", "name": "Name", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "null" }] } },
            { "kind": "interface", "name": "Tag", "members": [{ "kind": "property", "name": "name", "type": { "kind": "ref", "name": "Name", "module": "@acme/maps/a.js" } }] }
            """, "Tag");
        Assert.Equal(["Tag.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path)); // no Name.cs; the registry and metadata module
        Assert.Contains("public required Opt<string> Name { get; init; }", File(result, "Tag.cs"));
    }

    [Theory] // R2, R19
    [InlineData("""{ "kind": "array", "element": { "kind": "primitive", "name": "string" } }, { "kind": "array", "element": { "kind": "primitive", "name": "number" } }""",
        "Pair: string[] and double[] are both JSON Array (R19, IOP2003).")]
    [InlineData("""{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "number" }""",
        "Pair: the value-type case double (R2 keeps unions non-boxing) isn't supported yet.")]
    public void UnionsThatCantBeGeneratedYetAreRefused(string types, string message)
    {
        var e = Assert.Throws<NotSupportedException>(() => Generate($$"""{ "kind": "alias", "name": "Pair", "type": { "kind": "union", "types": [{{types}}] } }""", "Pair"));
        Assert.Equal(message, e.Message);
    }

    [Fact] // R16, R23: an overload per signature; a value-like result is read by value
    public void EachOverloadIsAMethod()
    {
        var result = Generator.Generate(Fixture.Ir, Fixture.Profile with { Include = ["Shape", "Point", "Line", "Polygon", "MapView.screenToMap"] },
            new(Api.Sync, "Acme.Maps"));
        var view = File(result, "MapView.cs");
        Assert.Contains("""
                public PointSnapshot ScreenToMap(double x, double y) =>
                    Realm.Call(JsOp.Call(Handle, "screenToMap", w =>
                    {
                        JsJson.WriteNumber(w, x);
                        JsJson.WriteNumber(w, y);
                    }, byValue: true), PointSnapshot.Read)!;
            """, view);
        Assert.Contains("public PointSnapshot ScreenToMap(double[] offset) =>", view);
    }

    [Theory] // R8, §5: a Promise is a Task in both modes, and a live result needs the caller's scope
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void APromiseOfAnEntityTakesAScope(Api api)
    {
        var result = Generator.Generate(Fixture.Ir, Fixture.Profile with { Include = ["Marker", "MapView.markerAt"] }, new(api, "Acme.Maps"));
        Assert.Contains("""
                public Task<Opt<Marker>> MarkerAtAsync(JsScope scope, double x, double y) =>
                    Realm.CallAsync(JsOp.Call(Handle, "markerAt", w =>
                    {
                        JsJson.WriteNumber(w, x);
                        JsJson.WriteNumber(w, y);
                    }), (ref r) => JsJson.ReadOpt(ref r, scope.Read<Marker>));
            """, File(result, "MapView.cs"));
    }

    [Theory] // R22, §5, R27
    [InlineData(Api.Sync, "public void ClickAt(", "public MapView GetView(JsScope scope)")]
    [InlineData(Api.Async, "public Task ClickAtAsync(", "public async Task<MapView> GetViewAsync(JsScope scope)")]
    public void AnOptionalParameterIsUndefinedByDefault(Api api, string clickAt, string getView)
    {
        var result = Generator.Generate(Fixture.Ir, Fixture.Profile with { Include = ["DemoMapElement", "MapView.zoom"] }, new(api, "Acme.Maps"));
        var element = File(result, "DemoMapElement.cs");
        Assert.Contains("public sealed partial class DemoMapElement : JsObject", element); // its base, HTMLElement, isn't emitted
        Assert.Contains(clickAt + "double x, double y, Opt<DemoMapElementClickAtButton> button = default) =>", element);
        Assert.Contains("if (!button.IsUndefined) JsJson.WriteValue(w, button, static (w1, v1) => JsJson.WriteNumber(w1, (int)v1));", element);
        Assert.Contains(getView + " =>", element);
        Assert.Contains("IOP2007 DemoMapElement.observedAttributes: skipped: a static member", result.Diagnostics.Select(d => d.ToString()));
    }

    [Theory] // design §2, §3.5, R4, R19 step 1
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void TheRegistryAndMetadataNumberTheClasses(Api api)
    {
        var ir = IrDocument.Parse("""
            { "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [
              { "specifier": "@acme/maps/layers/Layer.js", "declarations": [
                { "kind": "class", "name": "Layer", "abstract": true },
                { "kind": "class", "name": "TileLayer", "extends": { "kind": "ref", "name": "Layer", "module": "@acme/maps/layers/Layer.js" } }] },
              { "specifier": "@acme/maps/units.js", "declarations": [{ "kind": "class", "name": "Length" }] }] }
            """);
        var result = Generator.Generate(ir, new Profile { Package = "@acme/maps", ValueClasses = ["Length"] }, new(api, "Acme.Maps"));

        Assert.Contains("public abstract partial class Layer : JsObject", File(result, "Layer.cs"));
        Assert.Contains("public sealed partial class TileLayer : Layer", File(result, "TileLayer.cs"));
        var registry = File(result, "Maps.cs");
        Assert.Contains("""
                public const int LengthId = 1;
                public const int LayerId = 2;
                public const int TileLayerId = 3;

                public static JsClassRegistry Classes { get; } = new JsClassRegistry()
                    .Add(LengthId, static (realm, handle) => new Length(realm, handle))
                    .Add(TileLayerId, static (realm, handle) => new TileLayer(realm, handle))
            """, registry);
        Assert.Equal(api == Api.Sync, registry.Contains("""        .Preload("@acme/maps/units.js", "@acme/maps/layers/Layer.js");"""));
        Assert.Equal("""
            // <auto-generated/>
            export default {
              classes: {
                1: { module: "@acme/maps/units.js", export: "Length", name: "units.Length", value: true, members: [] },
                2: { module: "@acme/maps/layers/Layer.js", export: "Layer", name: "layers.Layer", members: [] },
                3: { module: "@acme/maps/layers/Layer.js", export: "TileLayer", name: "layers.Layer.TileLayer", members: [] },
              },
            };

            """, File(result, "maps.meta.js"));
    }

    [Fact] // R3, R18, R22, R24
    public void AnEntityRepeatsItsBasesSnapshotMembersAndLeavesOutTrailingUndefinedArguments()
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        var result = Generate($$"""
            { "kind": "interface", "name": "Cursor", "members": [{ "kind": "property", "name": "css", "type": { "kind": "primitive", "name": "string" } }] },
            { "kind": "class", "name": "Layer", "members": [
              { "kind": "property", "name": "cursor", "optional": true, "type": { "kind": "ref", "name": "Cursor", "module": "@acme/maps/a.js" } }],
              "events": [{ "name": "load", "type": {{Number}} }] },
            { "kind": "class", "name": "TileLayer", "extends": { "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }, "members": [
              { "kind": "method", "name": "refresh", "signatures": [{ "params": [
                { "name": "delay", "optional": true, "type": {{Number}} }, { "name": "retries", "optional": true, "type": {{Number}} },
                { "name": "tiles", "rest": true, "type": { "kind": "array", "element": {{Number}} } }], "returns": { "kind": "primitive", "name": "void" } }] }] }
            """, "Layer", "TileLayer");

        // Opt<Cursor> Cursor hides the type Cursor, so its methods are called through global:: (CS0120).
        Assert.Contains("JsJson.ReadOpt(ref r, global::Acme.Maps.Cursor.Read)", File(result, "Layer.cs"));
        Assert.Contains("case \"cursor\": cursor = JsJson.ReadOpt(ref reader, global::Acme.Maps.Cursor.Read); break;", File(result, "LayerSnapshot.cs"));
        Assert.Contains("public Opt<Cursor> Cursor { get; init; }", File(result, "TileLayerSnapshot.cs"));
        Assert.Contains("""
                public void Refresh(Opt<double> delay = default, Opt<double> retries = default) =>
                    Realm.Send(JsOp.Call(Handle, "refresh", w =>
                    {
                        if (!delay.IsUndefined || !retries.IsUndefined) JsJson.WriteValue(w, delay, JsJson.WriteNumber);
                        if (!retries.IsUndefined) JsJson.WriteValue(w, retries, JsJson.WriteNumber);
                    }));
            """, File(result, "TileLayer.cs"));
        Assert.Equal(
        [
            "IOP2007 Layer.on:load: skipped: the profile names no subscribe method",
            "IOP2007 TileLayer.refresh(tiles): rest parameter dropped (R22)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R27
    public void TwoTypesOfOneNameAreRefused()
    {
        var e = Assert.Throws<NotSupportedException>(() => Generate("""
            { "kind": "interface", "name": "A" },
            { "kind": "function", "name": "f", "signatures": [{ "params": [], "returns": { "kind": "primitive", "name": "void" } }] }
            """, "A", "f"));
        Assert.Equal("A: two generated types have this name (R27).", e.Message);
    }

    private static GeneratorResult Generate(string declarations, params string[] include)
    {
        var ir = IrDocument.Parse($$"""
            { "version": 1, "package": { "name": "@acme/maps", "version": "1" },
              "modules": [{ "specifier": "@acme/maps/a.js", "declarations": [{{declarations}}] }] }
            """);
        return Generator.Generate(ir, new Profile { Package = "@acme/maps", Include = include }, new(Api.Sync, "Acme.Maps"));
    }

    private static string File(GeneratorResult result, string path) => result.Files.Single(f => f.Path == path).Text;
}
