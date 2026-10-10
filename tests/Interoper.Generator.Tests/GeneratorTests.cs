using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>What the golden slice doesn't show; <see cref="GoldenSliceTests"/> covers the rest.</summary>
public sealed class GeneratorTests
{
    [Fact] // §2: a TS enum is a C# enum
    public void EnumsKeepTheirMemberNames()
    {
        var result = Generate("""
            { "kind": "enum", "name": "Mode", "members": [{ "name": "fast", "value": "fast-mode" }, { "name": "slow", "value": "slow" }] },
            { "kind": "enum", "name": "Level", "members": [{ "name": "Low", "value": 1 }, { "name": "High", "value": 10 }] }
            """);
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
            """);
        var options = File(result, "Options.cs");
        Assert.Contains("""
                public required double JsOptions { get; init; }
                public required global::Interoper.Opt<bool> Default { get; init; }
                public required string Id { get; init; }
            """, options);
        Assert.Contains("case \"default\": @default = global::Interoper.JsJson.ReadOpt(ref reader, global::Interoper.JsJson.ReadBoolean); break;", options);
        Assert.Contains("string id = null!;", options);
        Assert.Equal("IOP2006 Options.options: JsOptions: Options is taken (R26)", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R18, R26: a snapshot member can't take Diff's name; the runtime's JsDiff it calls is qualified, so it can take that one
    public void ASnapshotMemberNamedDiffIsRenamed()
    {
        var result = Generate("""
            { "kind": "class", "name": "Track", "members": [{ "kind": "property", "name": "diff", "type": { "kind": "primitive", "name": "number" } }] }
            """);
        var snapshot = File(result, "TrackSnapshot.cs");
        Assert.Contains("public global::Interoper.Opt<double> JsDiff { get; init; }", snapshot);
        Assert.Contains("JsDiff = after.JsDiff.IsUndefined || global::Interoper.JsDiff.Same(before.JsDiff, after.JsDiff) ? default : after.JsDiff,", snapshot);
        Assert.Equal("IOP2006 TrackSnapshot.diff: JsDiff: Diff is taken (R26)", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R3
    public void AnAliasOfOneTypeIsThatType()
    {
        var result = Generate("""
            { "kind": "alias", "name": "Name", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "null" }] } },
            { "kind": "interface", "name": "Tag", "members": [{ "kind": "property", "name": "name", "type": { "kind": "ref", "name": "Name", "module": "@acme/maps/a.js" } }] }
            """);
        Assert.Equal(["Tag.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path)); // no Name.cs; the registry and metadata module
        Assert.Contains("public required global::Interoper.Opt<string> Name { get; init; }", File(result, "Tag.cs"));
    }

    [Theory] // design §2, §3.5, R4, R19 step 1, R27
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

        Assert.Contains("public abstract partial class Layer : global::Interoper.JsObject", File(result, "Layers/Layer.cs"));
        Assert.Contains("public sealed partial class TileLayer : Layer", File(result, "Layers/TileLayer.cs"));
        var registry = File(result, "Maps.cs");
        Assert.Contains("""
                public const int LengthId = 1;
                public const int LayerId = 2;
                public const int TileLayerId = 3;

                public static global::Interoper.JsClassRegistry Classes { get; } = new global::Interoper.JsClassRegistry()
                    .Add(LengthId, static (realm, handle) => new Length(realm, handle))
                    .Add(TileLayerId, static (realm, handle) => new global::Acme.Maps.Layers.TileLayer(realm, handle))
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
    public void AnEntityRepeatsItsBasesSnapshotMembersAndLeavesOutTrailingUndefinedArgumentsBeforeASpread()
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
            """);

        // Opt<Cursor> Cursor hides the type Cursor, so its methods are called through global:: (CS0120).
        Assert.Contains("global::Interoper.JsJson.ReadOpt(ref r, global::Acme.Maps.Cursor.Read)", File(result, "Layer.cs"));
        Assert.Contains("case \"cursor\": cursor = global::Interoper.JsJson.ReadOpt(ref reader, global::Acme.Maps.Cursor.Read); break;", File(result, "LayerSnapshot.cs"));
        Assert.Contains("public global::Interoper.Opt<Cursor> Cursor { get; init; }", File(result, "TileLayerSnapshot.cs"));
        // R22: the rest parameter is params, and spread after the optional ones, which are written while it has elements.
        Assert.Contains("""
                public void Refresh(global::Interoper.Opt<double> delay = default, global::Interoper.Opt<double> retries = default, params double[] tiles) =>
                    Realm.Send(global::Interoper.JsOp.Call(Handle, "refresh", w =>
                    {
                        if (!delay.IsUndefined || !retries.IsUndefined || tiles.Length > 0) global::Interoper.JsJson.WriteValue(w, delay, global::Interoper.JsJson.WriteNumber);
                        if (!retries.IsUndefined || tiles.Length > 0) global::Interoper.JsJson.WriteValue(w, retries, global::Interoper.JsJson.WriteNumber);
                        foreach (var item in tiles) global::Interoper.JsJson.WriteNumber(w, item);
                    }));
            """, File(result, "TileLayer.cs"));
        Assert.Equal("IOP2007 Layer.on:load: skipped: the profile names no subscribe method", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R27: nothing refers to a module's static class, so it takes a free name
    public void AModuleClassWhoseNameIsTakenGetsASuffix()
    {
        var result = Generate("""
            { "kind": "interface", "name": "A" },
            { "kind": "function", "name": "f", "signatures": [{ "params": [], "returns": { "kind": "primitive", "name": "void" } }] }
            """);
        Assert.Equal(["A.cs", "A2.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path));
        Assert.Contains("public sealed partial record A", File(result, "A.cs"));
        Assert.Contains("public static partial class A2", File(result, "A2.cs"));
        Assert.Equal("IOP2006 @acme/maps/a.js: its functions are A2: A is taken (R27)", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R27: any other type whose name is taken is skipped
    public void ALaterTypeWhoseNameIsTakenIsSkipped()
    {
        var result = Generate("""
            { "kind": "interface", "name": "LayerSnapshot" },
            { "kind": "class", "name": "Layer" },
            { "kind": "interface", "name": "Maps" }
            """);
        Assert.Equal(["LayerSnapshot.cs", "Layer.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path));
        Assert.Contains("public sealed partial record LayerSnapshot", File(result, "LayerSnapshot.cs"));
        Assert.Contains("public static partial class Maps", File(result, "Maps.cs")); // the registry keeps its name
        Assert.Equal(
        [
            "IOP2006 Acme.Maps.LayerSnapshot: skipped: `LayerSnapshot.cs` is taken (R27)",
            "IOP2006 Acme.Maps.Maps: skipped: `Maps.cs` is taken (R27)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R27: the registry takes a free name when a folder's namespace has its name
    public void TheRegistryNamedLikeAFolderGetsASuffix()
    {
        var ir = IrDocument.Parse("""
            { "version": 1, "package": { "name": "@acme/core", "version": "1" }, "modules": [
              { "specifier": "@acme/core/core/Thing.js", "declarations": [{ "kind": "class", "name": "Thing" }] }] }
            """);
        var result = Generator.Generate(ir, new Profile { Package = "@acme/core" }, new(Api.Sync, "Acme"));

        Assert.Equal(["Core/Thing.cs", "Core/ThingSnapshot.cs", "Core2.cs", "core.meta.js"], result.Files.Select(f => f.Path));
        Assert.Contains("public static partial class Core2", File(result, "Core2.cs")); // next to the namespace Acme.Core
        Assert.Contains("global::Interoper.JsOp.Create(global::Acme.Core2.ThingId, ", File(result, "Core/Thing.cs"));
        Assert.Equal("IOP2006 Acme.Core: the registry is Core2: a namespace has its name (R27)", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R27: sibling modules' types of one name both compile; the later one is prefixed with its module's name
    public void SameNamedTypesOfSiblingModulesAreBothEmitted()
    {
        string Module(string member) => $$"""
            { "kind": "interface", "name": "Options", "members": [{ "kind": "property", "name": "{{member}}", "optional": true, "type": { "kind": "primitive", "name": "number" } }] },
            { "kind": "function", "name": "run", "signatures": [{ "params": [{ "name": "options", "type": { "kind": "ref", "name": "Options", "module": "@acme/maps/ops/{{member}}.js" } }],
              "returns": { "kind": "primitive", "name": "void" } }] }
            """;
        var result = Generate(Api.Sync, ("ops/scale.js", Module("scale")), ("ops/offset.js", Module("offset")));

        Assert.Equal(["Ops/Options.cs", "Ops/OffsetOptions.cs", "Ops/Scale.cs", "Ops/Offset.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path));
        Assert.Contains("public global::Interoper.Opt<double> Scale { get; init; }", File(result, "Ops/Options.cs"));
        Assert.Contains("public sealed partial record OffsetOptions", File(result, "Ops/OffsetOptions.cs"));
        Assert.Contains("public static void Run(global::Interoper.JsRealm realm, Options options) =>", File(result, "Ops/Scale.cs"));
        Assert.Contains("public static void Run(global::Interoper.JsRealm realm, OffsetOptions options) =>", File(result, "Ops/Offset.cs"));
        Assert.Equal("IOP2006 @acme/maps/ops/offset.js: its Options is OffsetOptions: @acme/maps/ops/scale.js has one too (R27)",
            Assert.Single(result.Diagnostics).ToString());
    }

    [Theory] // §2, R3, R17: `any`, `unknown` and `object` are JsAny, whose handles the reading scope owns
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AnyIsJsAny(Api api)
    {
        var result = Generate(api, ("a.js", """
            { "kind": "class", "name": "Graphic", "members": [
              { "kind": "property", "name": "attributes", "type": { "kind": "primitive", "name": "any" } },
              { "kind": "method", "name": "send", "signatures": [{ "params": [{ "name": "message", "type": { "kind": "primitive", "name": "unknown" } }],
                "returns": { "kind": "primitive", "name": "object" } }] }] }
            """));
        var graphic = File(result, "Graphic.cs");
        const string Read = "(ref r) => global::Interoper.JsJson.ReadOpt(ref r, (ref r1) => global::Interoper.JsAny.Read(ref r1, scope))";
        if (api == Api.Sync)
        {
            Assert.Contains($"public global::Interoper.Opt<global::Interoper.JsAny> GetAttributes(global::Interoper.JsScope scope) =>", graphic);
            Assert.Contains($"Realm.Call(global::Interoper.JsOp.Get(Handle, \"attributes\"), {Read});", graphic);
            Assert.Contains("public void SetAttributes(global::Interoper.Opt<global::Interoper.JsAny> value) =>", graphic);
            Assert.Contains("public global::Interoper.JsAny Send(global::Interoper.JsScope scope, global::Interoper.Opt<global::Interoper.JsAny> message) =>", graphic);
        }
        else
        {
            Assert.Contains("public Task<global::Interoper.Opt<global::Interoper.JsAny>> GetAttributesAsync(global::Interoper.JsScope scope) =>", graphic);
        }
        Assert.DoesNotContain("Attributes", File(result, "GraphicSnapshot.cs")); // R17: it may hold a handle
        Assert.Equal(
        [
            "IOP2001 Graphic.attributes: `any` is JsAny (§2)",
            "IOP2001 Graphic.send(message): `unknown` is JsAny (§2)",
            "IOP2001 Graphic.send: `object` is JsAny (§2)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R25: a rename is reported once, however many owners repeat the anonymous union that has it
    public void AnEnumMemberRenameIsReportedOnce()
    {
        const string Kind = """{ "kind": "union", "types": [{ "kind": "literal", "value": "a-b" }, { "kind": "literal", "value": "a_b" }] }""";
        var result = Generate($$"""
            { "kind": "interface", "name": "Fill", "members": [{ "kind": "property", "name": "kind", "type": {{Kind}} }] },
            { "kind": "interface", "name": "Stroke", "members": [{ "kind": "property", "name": "kind", "type": {{Kind}} }] },
            { "kind": "interface", "name": "Icon", "members": [{ "kind": "property", "name": "kind", "type": {{Kind}} }] }
            """);
        Assert.Contains("    AB,\n    AB2,\n", File(result, "StrokeKind.cs"));
        Assert.Equal("IOP2006 FillKind: \"a_b\" is AB2: AB is taken (R25), and in 2 other enums", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // R27: generated code names the runtime's types with global::, so a member of their name hides none of them
    public void RuntimeTypesAreGloballyQualified()
    {
        var result = Generate("""
            { "kind": "interface", "name": "Options", "members": [
              { "kind": "property", "name": "jsJson", "optional": true, "type": { "kind": "primitive", "name": "number" } },
              { "kind": "property", "name": "opt", "optional": true, "type": { "kind": "primitive", "name": "string" } }] }
            """);
        var options = File(result, "Options.cs");
        Assert.Contains("public global::Interoper.Opt<double> JsJson { get; init; }", options);
        Assert.Contains("global::Interoper.JsJson.WriteProperty(writer, \"jsJson\"u8, value.JsJson, global::Interoper.JsJson.WriteNumber);", options);
        Assert.Contains("public global::Interoper.Opt<string> Opt { get; init; }", options);
        Assert.DoesNotContain("using Interoper;", options);
    }

    [Fact] // §5: watches
    public void WithoutTheWatchMethodThereAreNoWatches()
    {
        var result = Generate("""
            { "kind": "class", "name": "Layer", "members": [
              { "kind": "property", "name": "opacity", "type": { "kind": "primitive", "name": "number" } },
              { "kind": "method", "name": "watch", "signatures": [{ "params": [], "returns": { "kind": "primitive", "name": "void" } }] }] },
            { "kind": "class", "name": "Graphic", "members": [
              { "kind": "property", "name": "opacity", "type": { "kind": "primitive", "name": "number" } }] }
            """, new Profile { Package = "@acme/maps", Watch = "watch" });
        Assert.Contains("public IDisposable WatchOpacity(global::Interoper.JsScope scope, Action<double> handler) =>", File(result, "Layer.cs"));
        Assert.DoesNotContain("Watch", File(result, "Graphic.cs"));
    }

    [Theory] // R24
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AnEventPayloadIsARecordWithoutMethodsAndItsMembersAreTheDefaultProjection(Api api)
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        const string Void = """{ "kind": "primitive", "name": "void" }""";
        var result = Generate(new Profile { Package = "@acme/maps", Events = new(Subscribe: "on"), Watch = "watch" }, api, ("a.js", $$"""
            { "kind": "interface", "name": "BaseEvent", "members": [
              { "kind": "property", "name": "time", "type": {{Number}} },
              { "kind": "method", "name": "stopPropagation", "signatures": [{ "params": [], "returns": {{Void}} }] }] },
            { "kind": "interface", "name": "PointerEvent", "extends": [{ "kind": "ref", "name": "BaseEvent", "module": "@acme/maps/a.js" }], "members": [
              { "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "pointer" } },
              { "kind": "property", "name": "x", "type": {{Number}} },
              { "kind": "method", "name": "preventDefault", "signatures": [{ "params": [], "returns": {{Void}} }] }] },
            { "kind": "class", "name": "Layer", "members": [
              { "kind": "property", "name": "opacity", "type": {{Number}} },
              { "kind": "method", "name": "watch", "signatures": [{ "params": [], "returns": {{Void}} }] }],
              "events": [{ "name": "pointer", "type": { "kind": "ref", "name": "PointerEvent", "module": "@acme/maps/a.js" } }] }
            """));

        Assert.Contains("public required double X { get; init; }", File(result, "PointerEvent.cs"));
        Assert.DoesNotContain("PreventDefault", File(result, "PointerEvent.cs"));
        Assert.DoesNotContain("StopPropagation", File(result, "BaseEvent.cs"));
        Assert.Equal(
        [
            "IOP2007 BaseEvent.stopPropagation: skipped: a method of an event payload (R24)",
            "IOP2007 PointerEvent.preventDefault: skipped: a method of an event payload (R24)",
        ], result.Diagnostics.Select(d => d.ToString()));

        // The record's wire members, constants and bases' members included, are the fields JS sends by default.
        var layer = File(result, "Layer.cs");
        const string Fields = """["kind", "x", "time"]""";
        if (api == Api.Sync)
        {
            Assert.Contains($"""
                    public IDisposable OnPointer(global::Interoper.JsScope scope, Action<PointerEvent> handler, global::Interoper.EventOptions? options = null) =>
                        scope.Subscribe(Handle, "on", "pointer", PointerEvent.Read, handler, options, {Fields});
                """, layer);
            Assert.Contains("""
                    public IDisposable WatchOpacity(global::Interoper.JsScope scope, Action<double> handler) =>
                        scope.Subscribe(Handle, "watch", "opacity", global::Interoper.JsJson.ReadNumber, handler, null, null);
                """, layer);
            Assert.DoesNotContain("StreamAsync", layer);
            return;
        }
        Assert.Contains($"""
                public Task<IAsyncDisposable> OnPointerAsync(global::Interoper.JsScope scope, Action<PointerEvent> handler, global::Interoper.EventOptions? options = null) =>
                    scope.SubscribeAsync(Handle, "on", "pointer", PointerEvent.Read, handler, options, {Fields});

                public IAsyncEnumerable<PointerEvent> OnPointerStreamAsync(global::Interoper.JsScope scope, global::Interoper.EventOptions? options = null, CancellationToken cancellationToken = default) =>
                    scope.StreamAsync(Handle, "on", "pointer", PointerEvent.Read, options, {Fields}, cancellationToken);
            """, layer);
        Assert.Contains("""
                public IAsyncEnumerable<double> WatchOpacityStreamAsync(global::Interoper.JsScope scope, CancellationToken cancellationToken = default) =>
                    scope.StreamAsync(Handle, "watch", "opacity", global::Interoper.JsJson.ReadNumber, null, null, cancellationToken);
            """, layer);
    }

    [Theory] // R27
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void ATypeInAnotherNamespaceIsGloballyQualified(Api api)
    {
        var result = Generate(api, ("geometry/Geometry.js", """
            { "kind": "class", "name": "Geometry", "abstract": true },
            { "kind": "interface", "name": "Extent", "members": [{ "kind": "property", "name": "width", "type": { "kind": "primitive", "name": "number" } }] },
            { "kind": "class", "name": "Polyline", "extends": { "kind": "ref", "name": "Geometry", "module": "@acme/maps/geometry/Geometry.js" }, "members": [
              { "kind": "property", "name": "extent", "type": { "kind": "ref", "name": "Extent", "module": "@acme/maps/geometry/Geometry.js" } }] }
            """), ("View.js", """
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "extent", "type": { "kind": "ref", "name": "Extent", "module": "@acme/maps/geometry/Geometry.js" } },
              { "kind": "method", "name": "goTo", "signatures": [{ "params": [{ "name": "target", "type": { "kind": "ref", "name": "Geometry", "module": "@acme/maps/geometry/Geometry.js" } }],
                "returns": { "kind": "primitive", "name": "void" } }] }] }
            """));

        Assert.Equal(["Geometry/Geometry.cs", "Geometry/GeometrySnapshot.cs", "Geometry/Extent.cs", "Geometry/Polyline.cs", "Geometry/PolylineSnapshot.cs",
            "View.cs", "ViewSnapshot.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path));
        // In namespace Acme.Maps.Geometry, `Geometry` alone would be the class, so another namespace's types are qualified.
        var view = File(result, "View.cs");
        Assert.Contains("namespace Acme.Maps;", view);
        Assert.Contains("global::Acme.Maps.Geometry.Extent.Read", view);
        Assert.Contains("global::Acme.Maps.Geometry.Geometry target", view);
        Assert.Contains("public global::Interoper.Opt<global::Acme.Maps.Geometry.Extent> Extent { get; init; }", File(result, "ViewSnapshot.cs"));
        // A type in the same namespace keeps its plain name; the registry is in the root namespace.
        var polyline = File(result, "Geometry/Polyline.cs");
        Assert.Contains("namespace Acme.Maps.Geometry;", polyline);
        Assert.Contains("public sealed partial class Polyline : Geometry", polyline);
        Assert.Contains("global::Interoper.JsOp.Create(global::Acme.Maps.Maps.PolylineId, ", polyline);
        Assert.Contains("Extent.Read", polyline);
        Assert.DoesNotContain("global::Acme.Maps.Geometry.Extent", polyline);
        Assert.Empty(result.Diagnostics);
    }

    [Fact] // R27: class id constants of classes with one name get a prefix; a type named like a namespace is skipped
    public void NamesThatClashAcrossNamespacesCompile()
    {
        const string Function = """{ "kind": "function", "name": "f", "signatures": [{ "returns": { "kind": "primitive", "name": "void" } }] }""";
        var result = Generate(Api.Sync, ("layers/Point.js", """{ "kind": "class", "name": "Point" }"""), ("Point.js", """{ "kind": "class", "name": "Point" }"""),
            ("Layers.js", """{ "kind": "interface", "name": "Layers" }"""), ("layers.js", Function));

        Assert.Equal(["Layers/Point.cs", "Layers/PointSnapshot.cs", "Point.cs", "PointSnapshot.cs", "Layers2.cs", "Maps.cs", "maps.meta.js"],
            result.Files.Select(f => f.Path));
        // Whatever the IR order, the root namespace's class keeps its name, and the others are prefixed by their namespace.
        Assert.Contains("""
                public const int LayersPointId = 1;
                public const int PointId = 2;

                public static global::Interoper.JsClassRegistry Classes { get; } = new global::Interoper.JsClassRegistry()
                    .Add(LayersPointId, static (realm, handle) => new global::Acme.Maps.Layers.Point(realm, handle))
                    .Add(PointId, static (realm, handle) => new Point(realm, handle))
            """, File(result, "Maps.cs"));
        Assert.Contains("global::Interoper.JsOp.Create(global::Acme.Maps.Maps.LayersPointId, ", File(result, "Layers/Point.cs"));
        Assert.Contains("public static partial class Layers2", File(result, "Layers2.cs")); // a module's functions take a free name
        Assert.Equal(
        [
            "IOP2006 Acme.Maps.Layers.Point: its class id is LayersPointId: other classes are named Point (R27)",
            "IOP2006 Acme.Maps.Layers: skipped: a namespace has its name (R27)",
            "IOP2006 @acme/maps/layers.js: its functions are Layers2: Layers is taken (R27)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R2, R5, R27: cases are told apart by their full names
    public void AUnionOfSameNamedTypesFromTwoFoldersHasBoth()
    {
        string Point(string member) =>
            $$"""{ "kind": "interface", "name": "Point", "members": [{ "kind": "property", "name": "{{member}}", "type": { "kind": "primitive", "name": "number" } }] }""";
        var result = Generate(Api.Sync, ("Point.js", Point("x")), ("geo/Point.js", Point("lat")), ("Spot.js", """
            { "kind": "alias", "name": "Spot", "type": { "kind": "union", "types": [
              { "kind": "ref", "name": "Point", "module": "@acme/maps/Point.js" }, { "kind": "ref", "name": "Point", "module": "@acme/maps/geo/Point.js" }] } }
            """));
        var spot = File(result, "Spot.cs");
        Assert.Contains("public Spot(Point value) : this(value, 2) { }", spot);
        Assert.Contains("public Spot(global::Acme.Maps.Geo.Point value) : this(value, 3) { }", spot);
        Assert.Empty(result.Diagnostics);
    }

    [Fact] // R22, R17: a rest parameter of entities
    public void ARestParameterOfEntitiesIsASpreadOfHandles()
    {
        var result = Generate("""
            { "kind": "class", "name": "Marker" },
            { "kind": "class", "name": "Layer", "members": [{ "kind": "method", "name": "add", "signatures": [{ "params": [
              { "name": "markers", "rest": true, "type": { "kind": "array", "element": { "kind": "ref", "name": "Marker", "module": "@acme/maps/a.js" } } }],
              "returns": { "kind": "primitive", "name": "void" } }] }] }
            """);
        Assert.Contains("""
                public void Add(params Marker[] markers) =>
                    Realm.Send(global::Interoper.JsOp.Call(Handle, "add", w =>
                    {
                        foreach (var item in markers) Marker.Write(w, item);
                    }));
            """, File(result, "Layer.cs"));
        Assert.Empty(result.Diagnostics);
    }

    [Theory] // R21
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void ACallbackIsADelegateItsScopeRegisters(Api api)
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        const string Void = """{ "kind": "primitive", "name": "void" }""";
        const string Marker = """{ "kind": "ref", "name": "Marker", "module": "@acme/maps/a.js" }""";
        string Parameter(string name, string type, bool optional = false) =>
            $$"""{ "name": "{{name}}", "type": {{type}}{{(optional ? ", \"optional\": true" : "")}} }""";
        string Callback(string returns, params string[] parameters) =>
            $$"""{ "kind": "function", "params": [{{string.Join(", ", parameters)}}], "returns": {{returns}} }""";
        string Method(string name, string callback, string returns) =>
            $$"""{ "kind": "method", "name": "{{name}}", "signatures": [{ "params": [{{Parameter("callback", callback)}}], "returns": {{returns}} }] }""";
        var each = Callback(Void, Parameter("item", Marker), Parameter("index", Number), Parameter("label", """{ "kind": "primitive", "name": "string" }""", optional: true));
        var pick = Callback("""{ "kind": "union", "types": [{ "kind": "primitive", "name": "boolean" }, { "kind": "primitive", "name": "undefined" }] }""", Parameter("item", Marker));
        var many = Callback(Void, [.. "abcde".Select(p => Parameter($"{p}", Number))]);
        var result = Generate(new Profile { Package = "@acme/maps" }, api, ("a.js", $$"""
            { "kind": "alias", "name": "Formatter", "type": {{Callback("""{ "kind": "primitive", "name": "string" }""", Parameter("marker", Marker))}} },
            { "kind": "class", "name": "Marker" },
            { "kind": "class", "name": "Layer", "members": [
              { "kind": "property", "name": "formatter", "optional": true, "type": { "kind": "union", "types": [
                { "kind": "ref", "name": "Formatter", "module": "@acme/maps/a.js" }, { "kind": "primitive", "name": "null" }] } },
              { "kind": "property", "name": "onLoad", "type": {{Callback(Void)}} },
              {{Method("each", each, Void)}}, {{Method("pick", pick, Marker)}}, {{Method("many", many, Void)}},
              { "kind": "method", "name": "later", "signatures": [{ "params": [{{Parameter("callback", Callback(Void), optional: true)}}, {{Parameter("delay", Number, optional: true)}}], "returns": {{Void}} }] }] },
            { "kind": "function", "name": "visit", "signatures": [{ "params": [{{Parameter("callback", Callback(Void, Parameter("marker", Marker)))}}], "returns": {{Void}} }] }
            """));

        // Each argument is read with the scope, which owns the handles in it, and a result is written back to JS.
        var (layer, sync) = (File(result, "Layer.cs"), api == Api.Sync);
        Assert.Contains("using System;", layer);
        Assert.Contains($$"""
                public {{(sync ? "void Each" : "Task EachAsync")}}(global::Interoper.JsScope scope, Action<Marker, double, global::Interoper.Opt<string>> callback) =>
                    Realm.{{(sync ? "Send" : "SendAsync")}}(global::Interoper.JsOp.Call(Handle, "each", w => global::Interoper.JsJson.WriteCallback(w, scope.Callback(callback, scope.Read<Marker>, global::Interoper.JsJson.ReadNumber, static (ref r1) => global::Interoper.JsJson.ReadOpt(ref r1, global::Interoper.JsJson.ReadString)))));
            """, layer);
        // An optional callback whose type has no null is sent as undefined when it's missing, here before a later argument.
        Assert.Contains("if (callback is not null || !delay.IsUndefined) global::Interoper.JsJson.WriteCallback(w, callback is null ? null : scope.Callback(callback), undefined: true);", layer);
        Assert.Contains($"(global::Interoper.JsScope scope, Action? callback = default, global::Interoper.Opt<double> delay = default)", layer);
        // A callback property is setter-only: JS can't hand the function back.
        Assert.Contains($"public {(sync ? "void SetOnLoad" : "Task SetOnLoadAsync")}(global::Interoper.JsScope scope, Action value) =>", layer);
        Assert.DoesNotContain(sync ? " OnLoad " : "GetOnLoadAsync", layer);
        // A module function with a callback takes the scope instead of the realm.
        Assert.Contains($"public static {(sync ? "void Visit" : "Task VisitAsync")}(global::Interoper.JsScope scope, Action<Marker> callback) =>", File(result, "A.cs"));
        Assert.DoesNotContain(result.Files, f => f.Path == "Formatter.cs"); // a callback alias has no C# type
        string[] skipped = ["IOP2007 Layer.many: skipped: uses a callback with more than 4 parameters (R21)"];
        if (sync)
        {
            Assert.Contains("""
                    public void SetFormatter(global::Interoper.JsScope scope, Func<Marker, string>? value) =>
                        Realm.Send(global::Interoper.JsOp.Set(Handle, "formatter", w => global::Interoper.JsJson.WriteCallback(w, value is null ? null : scope.Callback(value, scope.Read<Marker>, global::Interoper.JsJson.WriteString))));
                """, layer);
            Assert.Contains("""
                    public Marker Pick(global::Interoper.JsScope scope, Func<Marker, global::Interoper.Opt<bool>> callback) =>
                        Realm.Call(global::Interoper.JsOp.Call(Handle, "pick", w => global::Interoper.JsJson.WriteCallback(w, scope.Callback(callback, scope.Read<Marker>, static (w1, v1) => global::Interoper.JsJson.WriteValue(w1, v1, global::Interoper.JsJson.WriteBoolean)))), scope.Read<Marker>)!;
                """, layer);
            Assert.Equal(skipped, result.Diagnostics.Select(d => d.ToString()));
            return;
        }
        // Async mode can't give JS a result in time, so what uses one is skipped.
        Assert.DoesNotContain("Formatter", layer);
        Assert.DoesNotContain("Pick", layer);
        Assert.Equal(
        [
            .. skipped,
            "IOP2005 Layer.formatter: skipped in async mode: JS uses its callback's result (R21)",
            "IOP2005 Layer.pick: skipped in async mode: JS uses its callback's result (R21)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Theory] // §2, R17
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void ACollectionIsAGenericLiveClassAndARecordWithAnEntityIsReadWithAScope(Api api)
    {
        const string T = """{ "kind": "typeParameter", "name": "T" }""";
        const string Marker = """{ "kind": "ref", "name": "Marker", "module": "@acme/maps/a.js" }""";
        const string Hit = """{ "kind": "ref", "name": "Hit", "module": "@acme/maps/a.js" }""";
        string List(string item) => $$"""{ "kind": "ref", "name": "List", "module": "@acme/maps/a.js", "args": [{{item}}] }""";
        var result = Generate(new Profile { Package = "@acme/maps", Collections = ["List"] }, api, ("a.js", $$"""
            { "kind": "class", "name": "Marker" },
            { "kind": "interface", "name": "Change", "typeParameters": [{ "name": "T" }], "members": [{ "kind": "property", "name": "added", "type": { "kind": "array", "element": {{T}} } }] },
            { "kind": "class", "name": "List", "typeParameters": [{ "name": "T" }], "constructors": [{ "params": [{ "name": "items", "optional": true, "type": { "kind": "array", "element": {{T}} } }] }],
              "members": [{ "kind": "method", "name": "first", "signatures": [{ "returns": { "kind": "union", "types": [{{T}}, { "kind": "primitive", "name": "undefined" }] } }] }] },
            { "kind": "interface", "name": "Hit", "members": [
              { "kind": "property", "name": "marker", "type": {{Marker}} }, { "kind": "property", "name": "x", "type": { "kind": "primitive", "name": "number" } }] },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "markers", "readonly": true, "type": {{List(Marker)}} },
              { "kind": "property", "name": "numbers", "readonly": true, "type": {{List("""{ "kind": "primitive", "name": "number" }""")}} },
              { "kind": "method", "name": "hitAt", "signatures": [{ "returns": {{Hit}} }] }] },
            { "kind": "function", "name": "lastHit", "signatures": [{ "returns": {{Hit}} }] }
            """));
        var sync = api == Api.Sync;
        // The collection is generic over entities, with no Create and no registry entry: it comes from its owner.
        var list = File(result, "List.cs");
        Assert.Contains("public sealed partial class List<T> : global::Interoper.JsObject where T : global::Interoper.JsObject", list);
        Assert.DoesNotContain("Create", list);
        Assert.Contains(sync ? "public global::Interoper.Opt<T> First(global::Interoper.JsScope scope) =>" : "public Task<global::Interoper.Opt<T>> FirstAsync(global::Interoper.JsScope scope) =>", list);
        Assert.Contains("(ref r) => global::Interoper.JsJson.ReadOpt(ref r, scope.Read<T>)", list);
        var maps = File(result, "Maps.cs");
        Assert.Contains("public const int ListId = 2;", maps);
        Assert.DoesNotContain(".Add(ListId", maps);
        // Its owner reads it with a factory, as the registry can't make one of a type argument.
        var view = File(result, "View.cs");
        Assert.Contains(sync ? "public List<Marker> GetMarkers(global::Interoper.JsScope scope) =>" : "public async Task<List<Marker>> GetMarkersAsync(global::Interoper.JsScope scope) =>", view);
        Assert.Contains("(ref r) => scope.Read(ref r, static (realm, handle) => new List<Marker>(realm, handle))", view);
        // A generic data interface is a generic record; a record holding an entity is read with a scope.
        Assert.Contains("public sealed partial record Change<T> where T : global::Interoper.JsObject", File(result, "Change.cs"));
        var hit = File(result, "Hit.cs");
        Assert.Contains("public static Hit Read(ref Utf8JsonReader reader, global::Interoper.JsScope scope)", hit);
        Assert.Contains("""case "marker": marker = scope.Read<Marker>(ref reader); break;""", hit);
        Assert.Contains("(ref r) => Hit.Read(ref r, scope)", view);
        Assert.Contains(sync ? "public static Hit LastHit(global::Interoper.JsScope scope) =>" : "public static async Task<Hit> LastHitAsync(global::Interoper.JsScope scope) =>", File(result, "A.cs"));
        Assert.Equal(
        [
            "IOP2007 List.constructor: skipped: its parameter isn't a bag, so there's no Create (R18)",
            "IOP2007 View.numbers: skipped: uses `List` of a type that isn't an entity (§2)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Theory] // R26
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AMemberThatHidesABasesMemberIsNew(Api api)
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        const string String = """{ "kind": "primitive", "name": "string" }""";
        string Method(string name, string param, string returns) =>
            $$"""{ "kind": "method", "name": "{{name}}", "signatures": [{ "params": [{ "name": "x", "type": {{param}} }], "returns": {{returns}} }] }""";
        var layer = """{ "kind": "ref", "name": "Layer", "module": "@acme/maps/layers/Layer.js" }""";
        var tile = """{ "kind": "ref", "name": "TileLayer", "module": "@acme/maps/layers/TileLayer.js" }""";
        var result = Generate(new Profile { Package = "@acme/maps", Watch = "watch" }, api, ("layers/Layer.js", $$"""
            { "kind": "class", "name": "Layer", "members": [
              { "kind": "property", "name": "title", "type": {{String}} }, {{Method("clone", Number, layer)}}, {{Method("refresh", Number, Number)}},
              { "kind": "method", "name": "watch", "signatures": [{ "params": [], "returns": { "kind": "primitive", "name": "void" } }] }] }
            """), ("layers/TileLayer.js", $$"""
            { "kind": "class", "name": "TileLayer", "extends": {{layer}}, "members": [
              { "kind": "property", "name": "title", "type": {{String}} }, {{Method("clone", Number, tile)}}, {{Method("refresh", String, Number)}}] }
            """), ("views/View.js", $$"""
            { "kind": "class", "name": "View", "extends": {{tile}}, "members": [{{Method("refresh", String, Number)}}] }
            """));

        var (m, sync) = (api == Api.Sync ? "" : "Async", api == Api.Sync);
        var tileLayer = File(result, "Layers/TileLayer.cs");
        Assert.Contains($"public new {(sync ? "TileLayer" : "async Task<TileLayer>")} Clone{m}(global::Interoper.JsScope scope, double x)", tileLayer); // the same parameters
        Assert.Contains($"public {(sync ? "double" : "Task<double>")} Refresh{m}(string x)", tileLayer); // other parameters: an overload
        Assert.Contains(sync ? "public new string Title" : "public new Task SetTitleAsync(string value)", tileLayer);
        Assert.Contains($"public new {(sync ? "IDisposable" : "Task<IAsyncDisposable>")} WatchTitle{m}(global::Interoper.JsScope scope, Action<string> handler)", tileLayer);
        // Through every emitted base, whatever its namespace.
        var view = File(result, "Views/View.cs");
        Assert.Contains($"public new {(sync ? "double" : "Task<double>")} Refresh{m}(string x)", view);
        Assert.Contains($"public new {(sync ? "ViewSnapshot" : "async Task<ViewSnapshot>")} Snapshot{m}()", view);
        Assert.Contains($"public {(sync ? "void" : "Task")} Apply{m}(ViewSnapshot patch)", view); // another snapshot type
        Assert.DoesNotContain("public new", File(result, "Layers/Layer.cs"));
    }

    [Fact] // design §2: a default export is constructed by its export, and named by its declaration
    public void ADefaultExportKeepsItsNameAndTheRuntimeNameItsPrefix()
    {
        var result = Generate(new Profile { Package = "@acme/maps", RuntimeClassPrefix = "acme." }, Api.Async, ("layers/TileLayer.js", """
            { "kind": "class", "name": "TileLayer", "export": "default" }
            """), ("area.js", """
            { "kind": "function", "name": "area", "export": "default", "signatures": [{ "returns": { "kind": "primitive", "name": "number" } }] }
            """));

        Assert.Contains("public sealed partial class TileLayer : global::Interoper.JsObject", File(result, "Layers/TileLayer.cs"));
        Assert.Contains("""1: { module: "@acme/maps/layers/TileLayer.js", export: "default", name: "acme.layers.TileLayer", members: [] },""", File(result, "maps.meta.js"));
        Assert.Contains("""realm.CallAsync(global::Interoper.JsOp.Call(Module, "default"), global::Interoper.JsJson.ReadNumber);""", File(result, "Area.cs"));
    }

    private static GeneratorResult Generate(string declarations, Profile? profile = null)
    {
        var ir = IrDocument.Parse($$"""
            { "version": 1, "package": { "name": "@acme/maps", "version": "1" },
              "modules": [{ "specifier": "@acme/maps/a.js", "declarations": [{{declarations}}] }] }
            """);
        return Generator.Generate(ir, profile ?? new Profile { Package = "@acme/maps" }, new(Api.Sync, "Acme.Maps"));
    }

    /// <summary>The modules of <c>@acme/maps</c>, by their path in it.</summary>
    private static GeneratorResult Generate(Api api, params (string Path, string Declarations)[] modules) =>
        Generate(new Profile { Package = "@acme/maps" }, api, modules);

    private static GeneratorResult Generate(Profile profile, Api api, params (string Path, string Declarations)[] modules)
    {
        var json = string.Join(",\n", modules.Select(m => $$"""{ "specifier": "@acme/maps/{{m.Path}}", "declarations": [{{m.Declarations}}] }"""));
        var ir = IrDocument.Parse($$"""{ "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [{{json}}] }""");
        return Generator.Generate(ir, profile, new(api, "Acme.Maps"));
    }

    private static string File(GeneratorResult result, string path) => result.Files.Single(f => f.Path == path).Text;
}
