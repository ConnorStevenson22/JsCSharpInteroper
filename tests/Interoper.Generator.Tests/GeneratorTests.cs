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

        // An abstract entity class has no Create, but the registry makes it: JS can hand one back by its own class id.
        Assert.Contains("public partial class Layer : global::Interoper.JsObject", File(result, "Layers/Layer.cs"));
        Assert.DoesNotContain("Create", File(result, "Layers/Layer.cs"));
        Assert.Contains("public sealed partial class TileLayer : Layer", File(result, "Layers/TileLayer.cs"));
        var registry = File(result, "Maps.cs");
        Assert.Contains("""
                public const int LengthId = 1;
                public const int LayerId = 2;
                public const int TileLayerId = 3;

                public static global::Interoper.JsClassRegistry Classes { get; } = new global::Interoper.JsClassRegistry()
                    .Add(LengthId, static (realm, handle) => new Length(realm, handle))
                    .Add(LayerId, static (realm, handle) => new global::Acme.Maps.Layers.Layer(realm, handle))
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
            { "version": 1, "package": { "name": "@acme/widgets", "version": "1" }, "modules": [
              { "specifier": "@acme/widgets/widgets/Thing.js", "declarations": [{ "kind": "class", "name": "Thing" }] }] }
            """);
        var result = Generator.Generate(ir, new Profile { Package = "@acme/widgets" }, new(Api.Sync, "Acme"));

        Assert.Equal(["Widgets/Thing.cs", "Widgets/ThingSnapshot.cs", "Widgets2.cs", "widgets.meta.js"], result.Files.Select(f => f.Path));
        Assert.Contains("public static partial class Widgets2", File(result, "Widgets2.cs")); // next to the namespace Acme.Widgets
        Assert.Contains("global::Interoper.JsOp.Create(global::Acme.Widgets2.ThingId, ", File(result, "Widgets/Thing.cs"));
        Assert.Equal("IOP2006 Acme.Widgets: the registry is Widgets2: a namespace has its name (R27)", Assert.Single(result.Diagnostics).ToString());
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
            { "kind": "class", "name": "Tag", "members": [
              { "kind": "property", "name": "data", "type": { "kind": "primitive", "name": "any" } },
              { "kind": "method", "name": "send", "signatures": [{ "params": [{ "name": "message", "type": { "kind": "primitive", "name": "unknown" } }],
                "returns": { "kind": "primitive", "name": "object" } }] }] }
            """));
        var tag = File(result, "Tag.cs");
        const string Read = "(ref r) => global::Interoper.JsJson.ReadOpt(ref r, (ref r1) => global::Interoper.JsAny.Read(ref r1, scope))";
        if (api == Api.Sync)
        {
            Assert.Contains($"public global::Interoper.Opt<global::Interoper.JsAny> GetData(global::Interoper.JsScope scope) =>", tag);
            Assert.Contains($"Realm.Call(global::Interoper.JsOp.Get(Handle, \"data\"), {Read});", tag);
            Assert.Contains("public void SetData(global::Interoper.Opt<global::Interoper.JsAny> value) =>", tag);
            Assert.Contains("public global::Interoper.JsAny Send(global::Interoper.JsScope scope, global::Interoper.Opt<global::Interoper.JsAny> message) =>", tag);
        }
        else
        {
            Assert.Contains("public global::System.Threading.Tasks.Task<global::Interoper.Opt<global::Interoper.JsAny>> GetDataAsync(global::Interoper.JsScope scope) =>", tag);
        }
        Assert.DoesNotContain("Data", File(result, "TagSnapshot.cs")); // R17: it may hold a handle
        Assert.Equal(
        [
            "IOP2001 Tag.data: `any` is JsAny (§2)",
            "IOP2001 Tag.send(message): `unknown` is JsAny (§2)",
            "IOP2001 Tag.send: `object` is JsAny (§2)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R2, §5: a setter's wider type is written only, so it may have an entity case; a getter of it is skipped
    public void ASettersWiderTypeMayHaveAnEntityCase()
    {
        const string Target = """{ "kind": "union", "types": [{ "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }, { "kind": "primitive", "name": "string" }] }""";
        var result = Generate($$"""
            { "kind": "class", "name": "Layer" },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "target", "type": { "kind": "primitive", "name": "string" }, "writeType": {{Target}} },
              { "kind": "property", "name": "current", "type": {{Target}} }] }
            """);
        var view = File(result, "View.cs");
        Assert.Contains("public string Target => Realm.GetString(Handle, \"target\");", view);
        Assert.Contains("public void SetTarget(ViewTargetInput value) =>", view);
        Assert.Contains("throw new global::System.Text.Json.JsonException(\"ViewTargetInput is only written (R2)\")", File(result, "ViewTargetInput.cs"));
        Assert.DoesNotContain("Current", view);
        Assert.Equal("IOP2007 View.current: skipped: uses a union with the case Layer, which needs a scope to be read (R17, R19)",
            Assert.Single(result.Diagnostics).ToString());
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

    [Fact] // R27: generated code names the runtime's and .NET's types with global::, so a type or member of their name hides none
    public void RuntimeAndDotNetTypesAreGloballyQualified()
    {
        var result = Generate(Api.Async, ("a.js", """
            { "kind": "interface", "name": "Options", "members": [
              { "kind": "property", "name": "jsJson", "optional": true, "type": { "kind": "primitive", "name": "number" } },
              { "kind": "property", "name": "opt", "optional": true, "type": { "kind": "primitive", "name": "string" } }] },
            { "kind": "class", "name": "Task", "members": [
              { "kind": "method", "name": "run", "signatures": [{ "returns": { "kind": "ref", "name": "Promise", "args": [{ "kind": "primitive", "name": "void" }] } }] }] }
            """));
        var options = File(result, "Options.cs");
        Assert.Contains("public global::Interoper.Opt<double> JsJson { get; init; }", options);
        Assert.Contains("global::Interoper.JsJson.WriteProperty(writer, \"jsJson\"u8, value.JsJson, global::Interoper.JsJson.WriteNumber);", options);
        Assert.Contains("public global::Interoper.Opt<string> Opt { get; init; }", options);
        var task = File(result, "Task.cs");
        Assert.Contains("public sealed partial class Task : global::Interoper.JsObject", task);
        Assert.Contains("public global::System.Threading.Tasks.Task RunAsync() =>", task);
        Assert.All(result.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)), f => Assert.DoesNotContain("using ", f.Text));
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
        Assert.Contains("public global::System.IDisposable WatchOpacity(global::Interoper.JsScope scope, global::System.Action<double> handler) =>", File(result, "Layer.cs"));
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
                    public global::System.IDisposable OnPointer(global::Interoper.JsScope scope, global::System.Action<PointerEvent> handler, global::Interoper.EventOptions? options = null) =>
                        scope.Subscribe(Handle, "on", "pointer", PointerEvent.Read, handler, options, {Fields});
                """, layer);
            Assert.Contains("""
                    public global::System.IDisposable WatchOpacity(global::Interoper.JsScope scope, global::System.Action<double> handler) =>
                        scope.Subscribe(Handle, "watch", "opacity", global::Interoper.JsJson.ReadNumber, handler, null, null);
                """, layer);
            Assert.DoesNotContain("StreamAsync", layer);
            return;
        }
        Assert.Contains($"""
                public global::System.Threading.Tasks.Task<global::System.IAsyncDisposable> OnPointerAsync(global::Interoper.JsScope scope, global::System.Action<PointerEvent> handler, global::Interoper.EventOptions? options = null) =>
                    scope.SubscribeAsync(Handle, "on", "pointer", PointerEvent.Read, handler, options, {Fields});

                public global::System.Collections.Generic.IAsyncEnumerable<PointerEvent> OnPointerStreamAsync(global::Interoper.JsScope scope, global::Interoper.EventOptions? options = null, global::System.Threading.CancellationToken cancellationToken = default) =>
                    scope.StreamAsync(Handle, "on", "pointer", PointerEvent.Read, options, {Fields}, cancellationToken);
            """, layer);
        Assert.Contains("""
                public global::System.Collections.Generic.IAsyncEnumerable<double> WatchOpacityStreamAsync(global::Interoper.JsScope scope, global::System.Threading.CancellationToken cancellationToken = default) =>
                    scope.StreamAsync(Handle, "watch", "opacity", global::Interoper.JsJson.ReadNumber, null, null, cancellationToken);
            """, layer);
    }

    [Fact] // R15, §5: an iterator method is left out, of a class as of an interface, which is still a data record
    public void AnIteratorMethodIsLeftOut()
    {
        static string Iterating(string kind, string name) => $$"""
            { "kind": "{{kind}}", "name": "{{name}}", "members": [
              { "kind": "property", "name": "size", "type": {{Number}} },
              { "kind": "method", "name": "values", "signatures": [{ "params": [], "returns": { "kind": "ref", "name": "IterableIterator", "args": [{{Number}}] } }] },
              { "kind": "method", "name": "entries", "signatures": [{ "params": [], "returns": { "kind": "ref", "name": "Generator", "args": [{{Number}}] } }] }] }
            """;
        var result = Generate(Iterating("class", "Bag") + ", " + Iterating("interface", "Entries") + """
            , { "kind": "function", "name": "count", "signatures": [{ "params": [{ "name": "entries", "type": { "kind": "ref", "name": "Entries", "module": "@acme/maps/a.js" } }], "returns": { "kind": "primitive", "name": "number" } }] }
            """);

        Assert.Equal(
        [
            "IOP2007 Bag.values: skipped: uses `IterableIterator`",
            "IOP2007 Bag.entries: skipped: uses `Generator`",
            "IOP2007 Entries.values: skipped: uses `IterableIterator`",
            "IOP2007 Entries.entries: skipped: uses `Generator`",
        ], result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("public double Size", File(result, "Bag.cs"));
        Assert.Contains("public sealed partial record Entries", File(result, "Entries.cs"));
        Assert.Contains("public required double Size { get; init; }", File(result, "Entries.cs"));
        Assert.Contains("public static double Count(global::Interoper.JsRealm realm, Entries entries) =>", File(result, "A.cs"));
    }

    [Theory] // R24: an event without a payload has a parameterless handler, sends nothing and has no stream
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AnEventWithoutAPayloadHasAParameterlessHandler(Api api)
    {
        var result = Generate(new Profile { Package = "@acme/maps", Events = new(Subscribe: "on") }, api, ("a.js", """
            { "kind": "class", "name": "Session", "events": [
              { "name": "tick", "type": { "kind": "primitive", "name": "void" } },
              { "name": "expire", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "null" }, { "kind": "primitive", "name": "undefined" }] } }] }
            """));

        Assert.Empty(result.Diagnostics);
        var session = File(result, "Session.cs");
        Assert.Contains(api == Api.Sync
            ? """
                    public global::System.IDisposable OnTick(global::Interoper.JsScope scope, global::System.Action handler, global::Interoper.EventOptions? options = null) =>
                        scope.Subscribe(Handle, "on", "tick", handler, options);
                """
            : """
                    public global::System.Threading.Tasks.Task<global::System.IAsyncDisposable> OnTickAsync(global::Interoper.JsScope scope, global::System.Action handler, global::Interoper.EventOptions? options = null) =>
                        scope.SubscribeAsync(Handle, "on", "tick", handler, options);
                """, session);
        Assert.Contains(api == Api.Sync ? "OnExpire(global::Interoper.JsScope scope, global::System.Action handler" : "OnExpireAsync(global::Interoper.JsScope scope, global::System.Action handler", session);
        Assert.DoesNotContain("StreamAsync", session);
    }

    [Theory] // R27
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void ATypeInAnotherNamespaceIsGloballyQualified(Api api)
    {
        var result = Generate(api, ("figure/Figure.js", """
            { "kind": "class", "name": "Figure", "abstract": true },
            { "kind": "interface", "name": "Frame", "members": [{ "kind": "property", "name": "width", "type": { "kind": "primitive", "name": "number" } }] },
            { "kind": "class", "name": "Trail", "extends": { "kind": "ref", "name": "Figure", "module": "@acme/maps/figure/Figure.js" }, "members": [
              { "kind": "property", "name": "frame", "type": { "kind": "ref", "name": "Frame", "module": "@acme/maps/figure/Figure.js" } }] }
            """), ("View.js", """
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "frame", "type": { "kind": "ref", "name": "Frame", "module": "@acme/maps/figure/Figure.js" } },
              { "kind": "method", "name": "goTo", "signatures": [{ "params": [{ "name": "target", "type": { "kind": "ref", "name": "Figure", "module": "@acme/maps/figure/Figure.js" } }],
                "returns": { "kind": "primitive", "name": "void" } }] }] }
            """));

        Assert.Equal(["Figure/Figure.cs", "Figure/FigureSnapshot.cs", "Figure/Frame.cs", "Figure/Trail.cs", "Figure/TrailSnapshot.cs",
            "View.cs", "ViewSnapshot.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path));
        // In namespace Acme.Maps.Figure, `Figure` alone would be the class, so another namespace's types are qualified.
        var view = File(result, "View.cs");
        Assert.Contains("namespace Acme.Maps;", view);
        Assert.Contains("global::Acme.Maps.Figure.Frame.Read", view);
        Assert.Contains("global::Acme.Maps.Figure.Figure target", view);
        Assert.Contains("public global::Interoper.Opt<global::Acme.Maps.Figure.Frame> Frame { get; init; }", File(result, "ViewSnapshot.cs"));
        // A type in the same namespace keeps its plain name; the registry is in the root namespace.
        var trail = File(result, "Figure/Trail.cs");
        Assert.Contains("namespace Acme.Maps.Figure;", trail);
        Assert.Contains("public sealed partial class Trail : Figure", trail);
        Assert.Contains("global::Interoper.JsOp.Create(global::Acme.Maps.Maps.TrailId, ", trail);
        Assert.Contains("Frame.Read", trail);
        Assert.DoesNotContain("global::Acme.Maps.Figure.Frame", trail);
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
        Assert.Contains($$"""
                public {{(sync ? "void Each" : "global::System.Threading.Tasks.Task EachAsync")}}(global::Interoper.JsScope scope, global::System.Action<Marker, double, global::Interoper.Opt<string>> callback) =>
                    Realm.{{(sync ? "Send" : "SendAsync")}}(global::Interoper.JsOp.Call(Handle, "each", w => global::Interoper.JsJson.WriteCallback(w, scope.Callback(callback, scope.Read<Marker>, global::Interoper.JsJson.ReadNumber, static (ref r1) => global::Interoper.JsJson.ReadOpt(ref r1, global::Interoper.JsJson.ReadString)))));
            """, layer);
        // An optional callback whose type has no null is sent as undefined when it's missing, here before a later argument.
        Assert.Contains("if (callback is not null || !delay.IsUndefined) global::Interoper.JsJson.WriteCallback(w, callback is null ? null : scope.Callback(callback), undefined: true);", layer);
        Assert.Contains($"(global::Interoper.JsScope scope, global::System.Action? callback = default, global::Interoper.Opt<double> delay = default)", layer);
        // A callback property is setter-only: JS can't hand the function back.
        Assert.Contains($"public {(sync ? "void SetOnLoad" : "global::System.Threading.Tasks.Task SetOnLoadAsync")}(global::Interoper.JsScope scope, global::System.Action value) =>", layer);
        Assert.DoesNotContain(sync ? " OnLoad " : "GetOnLoadAsync", layer);
        // A module function with a callback takes the scope instead of the realm.
        Assert.Contains($"public static {(sync ? "void Visit" : "global::System.Threading.Tasks.Task VisitAsync")}(global::Interoper.JsScope scope, global::System.Action<Marker> callback) =>", File(result, "A.cs"));
        Assert.DoesNotContain(result.Files, f => f.Path == "Formatter.cs"); // a callback alias has no C# type
        string[] skipped = ["IOP2007 Layer.many: skipped: uses a callback with more than 4 parameters (R21)"];
        if (sync)
        {
            Assert.Contains("""
                    public void SetFormatter(global::Interoper.JsScope scope, global::System.Func<Marker, string>? value) =>
                        Realm.Send(global::Interoper.JsOp.Set(Handle, "formatter", w => global::Interoper.JsJson.WriteCallback(w, value is null ? null : scope.Callback(value, scope.Read<Marker>, global::Interoper.JsJson.WriteString))));
                """, layer);
            Assert.Contains("""
                    public Marker Pick(global::Interoper.JsScope scope, global::System.Func<Marker, global::Interoper.Opt<bool>> callback) =>
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
        Assert.Contains(sync ? "public global::Interoper.Opt<T> First(global::Interoper.JsScope scope) =>" : "public global::System.Threading.Tasks.Task<global::Interoper.Opt<T>> FirstAsync(global::Interoper.JsScope scope) =>", list);
        Assert.Contains("(ref r) => global::Interoper.JsJson.ReadOpt(ref r, scope.Read<T>)", list);
        var maps = File(result, "Maps.cs");
        Assert.Contains("public const int ListId = 2;", maps);
        Assert.DoesNotContain(".Add(ListId", maps);
        // Its owner reads it with a factory, as the registry can't make one of a type argument.
        var view = File(result, "View.cs");
        Assert.Contains(sync ? "public List<Marker> GetMarkers(global::Interoper.JsScope scope) =>" : "public async global::System.Threading.Tasks.Task<List<Marker>> GetMarkersAsync(global::Interoper.JsScope scope) =>", view);
        Assert.Contains("(ref r) => scope.Read(ref r, static (realm, handle) => new List<Marker>(realm, handle))", view);
        // A generic data interface is a generic record; a record holding an entity is read with a scope.
        Assert.Contains("public sealed partial record Change<T> where T : global::Interoper.JsObject", File(result, "Change.cs"));
        var hit = File(result, "Hit.cs");
        Assert.Contains("public static Hit Read(ref global::System.Text.Json.Utf8JsonReader reader, global::Interoper.JsScope scope)", hit);
        Assert.Contains("""case "marker": marker = scope.Read<Marker>(ref reader); break;""", hit);
        Assert.Contains("(ref r) => Hit.Read(ref r, scope)", view);
        Assert.Contains(sync ? "public static Hit LastHit(global::Interoper.JsScope scope) =>" : "public static async global::System.Threading.Tasks.Task<Hit> LastHitAsync(global::Interoper.JsScope scope) =>", File(result, "A.cs"));
        Assert.Equal(
        [
            "IOP2007 List.constructor: skipped: its parameter isn't a bag, so there's no Create (R18)",
            "IOP2007 View.numbers: skipped: uses `List` of a type that isn't an entity (§2)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // §5, §2, R2, R27: a setter that can't be mapped is dropped and its getter kept; a generic union alias is its union
    public void ASetterThatCantBeMappedIsDroppedAndAGenericUnionAliasIsInstantiated()
    {
        const string T = """{ "kind": "typeParameter", "name": "T" }""";
        const string Marker = """{ "kind": "ref", "name": "Marker", "module": "@acme/maps/a.js" }""";
        static string Alias(string name, params string[] args) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js", "args": [{{string.Join(", ", args)}}] }""";
        var oneOrMany = $$"""{ "kind": "union", "types": [{{T}}, { "kind": "array", "element": {{T}} }] }""";
        var result = Generate(Api.Sync, ("a.js", $$"""
            { "kind": "class", "name": "Marker" },
            { "kind": "alias", "name": "OneOrMany", "typeParameters": [{ "name": "T" }], "type": {{oneOrMany}} },
            { "kind": "alias", "name": "Same", "typeParameters": [{ "name": "V" }], "type": {{Alias("OneOrMany", """{ "kind": "typeParameter", "name": "V" }""")}} },
            { "kind": "alias", "name": "Many", "typeParameters": [{ "name": "T", "default": {{Marker}} }], "type": {{oneOrMany}} },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "marker", "type": {{Marker}}, "writeType": {{Alias("OneOrMany", Marker)}} },
              { "kind": "property", "name": "pin", "type": {{Marker}}, "writeType": {{Alias("Same", Marker)}} },
              { "kind": "property", "name": "flag", "type": {{Marker}}, "writeType": {{Alias("Many")}} },
              { "kind": "property", "name": "size", "type": {{Number}}, "writeType": {{Alias("OneOrMany", Number)}} },
              { "kind": "property", "name": "labels", "type": { "kind": "array", "element": {{String}} }, "writeType": { "kind": "unresolved", "text": "LabelList" } }] }
            """));

        var view = File(result, "View.cs");
        // OneOrMany<Marker> is Marker | Marker[], an anonymous union only written (R2: it has entity cases); so are an
        // alias of it and a use that takes the default.
        Assert.Contains("public Marker GetMarker(global::Interoper.JsScope scope) =>", view);
        Assert.Contains("public void SetMarker(ViewMarkerInput value) =>", view);
        Assert.Contains("public ViewMarkerInput(Marker[] value) : this(value, 3) { }", File(result, "ViewMarkerInput.cs"));
        Assert.Contains("public ViewPinInput(Marker[] value) : this(value, 3) { }", File(result, "ViewPinInput.cs"));
        Assert.Contains("public ViewFlagInput(Marker[] value) : this(value, 3) { }", File(result, "ViewFlagInput.cs"));
        // OneOrMany<number> has a value-type case (R2), and LabelList doesn't resolve: the getters stay.
        Assert.Contains("public double Size\n", view.Replace(" =>", "\n"));
        Assert.DoesNotContain("SetSize", view);
        Assert.Contains("public string[] Labels =>\n", view); // read-only
        Assert.DoesNotContain("SetLabels", view);
        Assert.Equal(
        [
            "IOP2007 OneOrMany: skipped: uses generic `OneOrMany` (in `OneOrMany`)",
            "IOP2007 Same: skipped: uses generic `Same` (in `Same`)",
            "IOP2007 Many: skipped: uses generic `Many` (in `Many`)",
            "IOP2007 View.size: setter dropped: uses a union with the value-type case double (R2)",
            "IOP2007 View.labels: setter dropped: uses unresolved `LabelList`",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // §2: a recursive generic union alias stops expanding, and what uses it is skipped
    public void ARecursiveGenericUnionAliasStops()
    {
        const string T = """{ "kind": "typeParameter", "name": "T" }""";
        const string Marker = """{ "kind": "ref", "name": "Marker", "module": "@acme/maps/a.js" }""";
        static string Alias(string name, string arg) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js", "args": [{{arg}}] }""";
        var result = Generate(Api.Sync, ("a.js", $$"""
            { "kind": "class", "name": "Marker" },
            { "kind": "alias", "name": "Chain", "typeParameters": [{ "name": "T" }], "type": { "kind": "union", "types": [{{T}},
              { "kind": "object", "members": [{ "kind": "property", "name": "next", "type": {{Alias("Chain", T)}} }] }] } },
            { "kind": "alias", "name": "Tree", "typeParameters": [{ "name": "T" }], "type": { "kind": "union", "types": [{{T}}, { "kind": "array", "element": {{Alias("Tree", T)}} }] } },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "chain", "type": {{Alias("Chain", Marker)}} },
              { "kind": "property", "name": "tree", "type": {{Alias("Tree", Marker)}} }] }
            """));

        Assert.Equal(
        [
            "IOP2007 Chain: skipped: uses generic `Chain` (in `Chain`)",
            "IOP2007 Tree: skipped: uses generic `Tree` (in `Tree`)",
            "IOP2007 View.chain: skipped: uses generic `Chain` (in `Chain`)",
            "IOP2007 View.tree: skipped: uses generic `Tree` (in `Tree`)",
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
        Assert.Contains($"public new {(sync ? "TileLayer" : "async global::System.Threading.Tasks.Task<TileLayer>")} Clone{m}(global::Interoper.JsScope scope, double x)", tileLayer); // the same parameters
        Assert.Contains($"public {(sync ? "double" : "global::System.Threading.Tasks.Task<double>")} Refresh{m}(string x)", tileLayer); // other parameters: an overload
        Assert.Contains(sync ? "public new string Title" : "public new global::System.Threading.Tasks.Task SetTitleAsync(string value)", tileLayer);
        Assert.Contains($"public new {(sync ? "global::System.IDisposable" : "global::System.Threading.Tasks.Task<global::System.IAsyncDisposable>")} WatchTitle{m}(global::Interoper.JsScope scope, global::System.Action<string> handler)", tileLayer);
        // Through every emitted base, whatever its namespace.
        var view = File(result, "Views/View.cs");
        Assert.Contains($"public new {(sync ? "double" : "global::System.Threading.Tasks.Task<double>")} Refresh{m}(string x)", view);
        Assert.Contains($"public new {(sync ? "ViewSnapshot" : "async global::System.Threading.Tasks.Task<ViewSnapshot>")} Snapshot{m}()", view);
        Assert.Contains($"public {(sync ? "void" : "global::System.Threading.Tasks.Task")} Apply{m}(ViewSnapshot patch)", view); // another snapshot type
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

    [Fact] // R16, R18, R26: a member a subclass redeclares is in its snapshot once; with another type it's new
    public void ARedeclaredMemberIsInTheSnapshotOnce()
    {
        static string Ref(string name) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js" }""";
        var result = Generate($$"""
            { "kind": "interface", "name": "Anchor", "members": [{ "kind": "property", "name": "id", "type": {{String}} }] },
            { "kind": "interface", "name": "AnchorProps", "members": [{ "kind": "property", "name": "id", "optional": true, "type": {{String}} }] },
            { "kind": "class", "name": "Figure", "abstract": true, "members": [
              { "kind": "property", "name": "kind", "readonly": true, "type": {{String}} },
              { "kind": "property", "name": "anchor", "type": {{Ref("Anchor")}} },
              { "kind": "property", "name": "size", "type": {{Number}} }] },
            { "kind": "class", "name": "Tile", "extends": {{Ref("Figure")}}, "members": [
              { "kind": "property", "name": "kind", "readonly": true, "type": { "kind": "literal", "value": "tile" } },
              { "kind": "property", "name": "anchor", "type": {{Ref("Anchor")}}, "writeType": {{Ref("AnchorProps")}} },
              { "kind": "property", "name": "size", "type": {{String}} }] }
            """, new Profile { Package = "@acme/maps", ValueClasses = ["Figure", "Tile"] });

        var tile = File(result, "TileSnapshot.cs");
        Assert.DoesNotContain("Anchor {", tile); // the base's
        Assert.DoesNotContain("JsAnchor", tile);
        Assert.Contains("public new global::Interoper.Opt<string> Size { get; init; }", tile);
        Assert.Single(tile.Split('\n'), l => l.Contains("\"anchor\"u8", StringComparison.Ordinal));
        Assert.Contains("""2: { module: "@acme/maps/a.js", export: "Tile", name: "a.Tile", value: true, members: ["anchor", "size", "kind"] },""", File(result, "maps.meta.js"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact] // R15, R2, R19: a record drops an optional member it can't map, and the unions it's in keep it; a required one skips it
    public void ARecordDropsAnOptionalMemberItCantMap()
    {
        const string Pairs = """{ "kind": "array", "element": { "kind": "tuple", "elements": [{ "kind": "primitive", "name": "number" }, { "kind": "primitive", "name": "string" }] } }""";
        static string Ref(string name) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js" }""";
        var result = Generate($$"""
            { "kind": "interface", "name": "TrackProps", "members": [
              { "kind": "property", "name": "stops", "type": { "kind": "array", "element": { "kind": "array", "element": {{Number}} } } },
              { "kind": "property", "name": "legs", "optional": true, "type": {{Pairs}} }] },
            { "kind": "interface", "name": "DotProps", "members": [{ "kind": "property", "name": "x", "type": {{Number}} }] },
            { "kind": "interface", "name": "LegProps", "members": [{ "kind": "property", "name": "legs", "type": {{Pairs}} }] },
            { "kind": "alias", "name": "FigureProps", "type": { "kind": "union", "types": [{{Ref("TrackProps")}}, {{Ref("DotProps")}}] } },
            { "kind": "function", "name": "draw", "signatures": [{ "params": [{ "name": "props", "type": {{Ref("FigureProps")}} }], "returns": {{Ref("FigureProps")}} }] }
            """);

        var track = File(result, "TrackProps.cs");
        Assert.Contains("public required double[][] Stops { get; init; }", track);
        Assert.DoesNotContain("Legs", track);
        Assert.Contains("public static FigureProps Draw(global::Interoper.JsRealm realm, FigureProps props) =>", File(result, "A.cs"));
        Assert.Contains("JsonTokenType.StartObject when global::Interoper.JsJson.HasProperty(reader, \"x\"u8) => DotProps.Read(ref reader),", File(result, "FigureProps.cs"));
        Assert.DoesNotContain("LegProps.cs", result.Files.Select(f => f.Path));
        Assert.Equal(
        [
            "IOP2007 TrackProps.legs: skipped: uses a tuple of different types",
            "IOP2007 LegProps: skipped: uses a tuple of different types (in `LegProps`)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R15, R17: a record that drops its only entity member isn't scoped, so a union reads it
    public void ARecordThatDropsItsEntityMemberIsntScoped()
    {
        static string Ref(string name) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js" }""";
        var result = Generate($$"""
            { "kind": "class", "name": "Marker" },
            { "kind": "interface", "name": "Spec", "members": [
              { "kind": "property", "name": "x", "type": {{Number}} },
              { "kind": "property", "name": "owner", "optional": true, "type": { "kind": "tuple", "elements": [{{Ref("Marker")}}, {{String}}] } }] },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "spec", "type": { "kind": "union", "types": [{{Ref("Spec")}}, {{String}}] } }] }
            """);

        Assert.Contains("public static Spec Read(ref global::System.Text.Json.Utf8JsonReader reader)\n", File(result, "Spec.cs"));
        Assert.Contains("public ViewSpec Spec\n", File(result, "View.cs"));
        Assert.Equal("IOP2007 Spec.owner: skipped: uses a tuple of different types", Assert.Single(result.Diagnostics).ToString());
    }

    [Fact] // design §2: the profile is the first package's, so its runtime class prefix is too
    public void TheRuntimeClassPrefixIsTheFirstPackagesOnly()
    {
        var result = Packages(Api.Sync, new Profile { Package = "@acme/maps", RuntimeClassPrefix = "acme." }, """
            { "specifier": "@acme/maps/Map.js", "declarations": [{ "kind": "class", "name": "Map" }] },
            { "specifier": "@acme/widgets/legend", "package": "@acme/widgets", "declarations": [{ "kind": "class", "name": "Legend" }] }
            """);

        Assert.Contains("""1: { module: "@acme/maps/Map.js", export: "Map", name: "acme.Map", members: [] },""", File(result, "maps.meta.js"));
        Assert.Contains("""2: { module: "@acme/widgets/legend", export: "Legend", name: "legend.Legend", members: [] },""", File(result, "maps.meta.js"));
    }

    [Theory] // R13, R28: a component's attributes, properties, events and @bind; a DOM element is written only
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void ACustomElementIsAComponent(Api api)
    {
        var result = Elements(api, [("chart.js", $$"""
            { "kind": "class", "name": "ChartElement", "members": [
              { "kind": "property", "name": "level", "type": {{Number}} },
              { "kind": "property", "name": "maxLevel", "type": {{Number}} },
              { "kind": "property", "name": "note", "optional": true, "type": {{String}} },
              { "kind": "property", "name": "caption", "type": { "kind": "union", "types": [{{String}}, { "kind": "primitive", "name": "null" }] } },
              { "kind": "property", "name": "host", "type": { "kind": "ref", "name": "HTMLElement" } },
              { "kind": "method", "name": "frame", "signatures": [{ "returns": { "kind": "ref", "name": "HTMLElement" } }] }] },
            { "kind": "class", "name": "XChart" }
            """)], $$"""
            { "tag": "x-chart", "class": { "kind": "ref", "name": "ChartElement", "module": "@acme/maps/chart.js" },
              "attributes": [{ "name": "level", "property": "level" }, { "name": "compact", "type": { "kind": "primitive", "name": "boolean" } },
                { "name": "data", "type": { "kind": "array", "element": {{Number}} } }],
              "events": [{ "name": "max-level-change", "detail": { "kind": "object", "members": [
                  { "kind": "property", "name": "level", "type": {{Number}} }, { "kind": "property", "name": "maxLevel", "type": {{Number}} }] } },
                { "name": "level-change", "detail": { "kind": "object", "members": [{ "kind": "property", "name": "level", "type": {{Number}} }] } }],
              "slots": [] }
            """);

        Assert.Equal([
            "IOP2007 ChartElement.frame: skipped: uses a DOM element JS returns (R13)",
            "IOP2007 <x-chart> data: skipped: an attribute without a property is a string, number, boolean or string enum (R28)",
        ], result.Diagnostics.Select(d => d.ToString()));
        var element = File(result, "ChartElement.cs");
        Assert.Contains(api == Api.Sync ? "public void SetHost(global::Microsoft.AspNetCore.Components.ElementReference value)" : "public global::System.Threading.Tasks.Task SetHostAsync(global::Microsoft.AspNetCore.Components.ElementReference value)", element);
        Assert.DoesNotContain("GetHost", element);
        var component = File(result, "XChartComponent.cs");
        Assert.Contains("public sealed partial class XChartComponent : global::Interoper.JsComponentBase", component);
        Assert.Contains("public bool Compact { get; set; }", component);
        Assert.Contains("public global::Microsoft.AspNetCore.Components.EventCallback<ChartElement> OnElementReady { get; set; }", component);
        Assert.Contains("""if (supplied.Contains(nameof(Compact))) builder.AddAttribute(3, "compact", Compact);""", component); // present or absent
        // The longest property wins: max-level-change binds maxLevel, and level-change binds level.
        Assert.Contains("var echo = echoesMaxLevel.IsEcho(detail.MaxLevel);", component);
        Assert.Contains("var echo = echoesLevel.IsEcho(detail.Level);", component);
        Assert.Contains("public global::Microsoft.AspNetCore.Components.EventCallback<double> LevelChanged { get; set; }", component);
        Assert.Contains("public global::Microsoft.AspNetCore.Components.EventCallback<LevelChangeDetail> OnLevelChange { get; set; }", component);
        // A null optional property is undefined; a null one whose type has null is JS null.
        Assert.Contains("""global::Interoper.JsOp.Set(Element.Handle, "note", w => { if (value is { } v) global::Interoper.JsJson.WriteString(w, v); else global::Interoper.JsJson.WriteUndefined(w); })""", component);
        Assert.Contains("""global::Interoper.JsOp.Set(Element.Handle, "caption", w => { if (value is { } v) global::Interoper.JsJson.WriteString(w, v); else w.WriteNullValue(); })""", component);
        // Async mode loads the element's module before wrapping the element; sync mode preloads it.
        Assert.Contains(api == Api.Sync ? "Element = await ElementAsync<ChartElement>(element);" : """Element = await ElementAsync<ChartElement>(element, "@acme/maps/chart.js");""", component);
        var events = File(result, "MapsEvents.cs");
        Assert.Contains("""[global::Microsoft.AspNetCore.Components.EventHandler("onlevel-change", typeof(global::Interoper.JsEventArgs), true, true)]""", events);
        Assert.Contains("public static partial class EventHandlers", events); // the only class Razor looks for them on
        var meta = File(result, "maps.meta.js");
        Assert.Contains("""tag: "x-chart", members: ["level", "maxLevel", "note", "caption"] },""", meta);
        Assert.Contains("""{ name: "level-change", fields: ["level"] },""", meta);
    }

    [Theory] // R28, design §3.2: an abstract element class named like its tag; an attribute the manifest types as a primitive
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AnAbstractElementClassNamedLikeItsTagIsRegisteredAndItsComponentIsSuffixed(Api api)
    {
        var result = Elements(api, [("chart.js", $$"""
            { "kind": "class", "name": "XChart", "abstract": true, "members": [
              { "kind": "property", "name": "label", "type": { "kind": "union", "types": [{{String}}, { "kind": "array", "element": {{String}} }] } },
              { "kind": "property", "name": "tags", "type": { "kind": "union", "types": [{{String}}, { "kind": "array", "element": {{String}} }] } }] }
            """)], $$"""
            { "tag": "x-chart", "class": { "kind": "ref", "name": "XChart", "module": "@acme/maps/chart.js" },
              "attributes": [{ "name": "label", "property": "label", "type": {{String}} }, { "name": "tags", "property": "tags" }], "events": [], "slots": [] }
            """);

        Assert.Empty(result.Diagnostics); // R28: {Name}Component is the convention, not a rename
        // The element's live object is read by its tag's class id, so the registry makes the abstract class (design §3.2).
        Assert.Contains("public sealed partial class XChart : global::Interoper.JsObject", File(result, "XChart.cs"));
        Assert.Contains(".Add(XChartId, static (realm, handle) => new XChart(realm, handle))", File(result, "Maps.cs"));
        var component = File(result, "XChartComponent.cs");
        Assert.Contains("public sealed partial class XChartComponent : global::Interoper.JsComponentBase", component);
        // label renders as the manifest's string attribute; tags, without one, is set as its union property.
        Assert.Contains("public string Label { get; set; } = default!;", component);
        Assert.Contains("""if (supplied.Contains(nameof(Label))) builder.AddAttribute(""", component);
        Assert.Contains("public XChartTags Tags { get; set; }", component);
        Assert.Contains("""global::Interoper.JsOp.Set(Element.Handle, "tags", """, component);
        Assert.DoesNotContain("""JsOp.Set(Element.Handle, "label", """, component);
    }

    [Fact] // R28, R24: elements that share an event name share its registration, which sends all their fields
    public void ElementsSharingAnEventShareItsFields()
    {
        var result = Elements(Api.Async, [("a.js", """{ "kind": "class", "name": "AElement" }"""), ("b.js", """{ "kind": "class", "name": "BElement" }""")],
            $$"""
            { "tag": "x-a", "class": { "kind": "ref", "name": "AElement", "module": "@acme/maps/a.js" }, "attributes": [], "slots": [],
              "events": [{ "name": "pick", "detail": { "kind": "object", "members": [{ "kind": "property", "name": "x", "type": {{Number}} }] } }] }
            """,
            $$"""
            { "tag": "x-b", "class": { "kind": "ref", "name": "BElement", "module": "@acme/maps/b.js" }, "attributes": [], "slots": [],
              "events": [{ "name": "pick", "detail": { "kind": "object", "members": [{ "kind": "property", "name": "x", "type": {{Number}} }, { "kind": "property", "name": "y", "type": {{Number}} }] } }] }
            """);

        Assert.Contains("""{ name: "pick", fields: ["x", "y"] },""", File(result, "maps.meta.js"));
        Assert.Single(File(result, "MapsEvents.cs").Split('\n'), l => l.Contains("\"onpick\"", StringComparison.Ordinal));
    }

    [Fact] // R28, R11, R27: an inline detail is an inline record; a name a declared type has, in its namespace, gets a suffix
    public void AnInlineDetailWhoseNameIsTakenGetsASuffix()
    {
        var result = Elements(Api.Async, [
            ("chart.js", $$"""
                { "kind": "class", "name": "ChartElement" },
                { "kind": "interface", "name": "LevelChangeDetail", "members": [{ "kind": "property", "name": "other", "type": {{String}} }] }
                """),
            ("other.js", $$"""{ "kind": "interface", "name": "ZoomChangeDetail", "members": [{ "kind": "property", "name": "other", "type": {{String}} }] }""")],
            $$"""
            { "tag": "x-chart", "class": { "kind": "ref", "name": "ChartElement", "module": "@acme/maps/chart.js" }, "attributes": [], "slots": [],
              "events": [{ "name": "level-change", "detail": { "kind": "object", "members": [{ "kind": "property", "name": "level", "type": {{Number}} }] } },
                { "name": "zoom-change", "detail": { "kind": "object", "members": [{ "kind": "property", "name": "zoom", "type": {{Number}} }] } }] }
            """);

        Assert.Equal([
            "IOP2006 Acme.Maps.LevelChangeDetail: the inline record is LevelChangeDetail2: LevelChangeDetail is taken (R27)",
            "IOP2006 Acme.Maps.ZoomChangeDetail: the inline record is ZoomChangeDetail2: ZoomChangeDetail is taken (R27)",
        ], result.Diagnostics.Select(d => d.ToString()));
        var component = File(result, "XChart.cs");
        Assert.Contains("EventCallback<LevelChangeDetail2> OnLevelChange", component);
        Assert.Contains("OnLevelChange.InvokeAsync(e.Read(LevelChangeDetail2.Read));", component);
        Assert.Contains("public required double Level { get; init; }", File(result, "LevelChangeDetail2.cs"));
        Assert.Contains("public required string Other { get; init; }", File(result, "LevelChangeDetail.cs"));
        Assert.Contains("OnZoomChange.InvokeAsync(e.Read(ZoomChangeDetail2.Read));", component); // a sibling module's declared name too
        Assert.Contains("public required double Zoom { get; init; }", File(result, "ZoomChangeDetail2.cs"));
        Assert.Contains("public required string Other { get; init; }", File(result, "ZoomChangeDetail.cs"));
        var meta = File(result, "maps.meta.js");
        Assert.Contains("""{ name: "level-change", fields: ["level"] },""", meta);
        Assert.Contains("""{ name: "zoom-change", fields: ["zoom"] },""", meta);
    }

    [Fact] // R28, R17: a detail that holds an entity is read with the component's scope, which owns its handles
    public void ADetailWithAnEntityIsReadWithTheScope()
    {
        var result = Elements(Api.Async, [("chart.js", """
            { "kind": "class", "name": "ChartElement" },
            { "kind": "interface", "name": "PickDetail", "members": [{ "kind": "property", "name": "target", "type": { "kind": "ref", "name": "ChartElement", "module": "@acme/maps/chart.js" } }] }
            """)], """
            { "tag": "x-chart", "class": { "kind": "ref", "name": "ChartElement", "module": "@acme/maps/chart.js" }, "attributes": [], "slots": [],
              "events": [{ "name": "pick", "detail": { "kind": "ref", "name": "PickDetail", "module": "@acme/maps/chart.js" } }] }
            """);

        Assert.Empty(result.Diagnostics);
        var component = File(result, "XChart.cs");
        Assert.Contains("var scope = Scope;", component);
        Assert.Contains("var detail = e.Read((ref r) => PickDetail.Read(ref r, scope));", component);
        Assert.Contains("""{ name: "pick", fields: ["target"] },""", File(result, "maps.meta.js"));
    }

    [Fact] // R27: another package's types are in a namespace named after it, and its runtime names are paths in it
    public void AnotherPackagesModulesHaveItsNamespace()
    {
        var result = Packages(Api.Sync, """
            { "specifier": "@acme/maps/MapView.js", "declarations": [{ "kind": "class", "name": "MapView" }] },
            { "specifier": "@acme/widgets", "package": "@acme/widgets", "declarations": [
              { "kind": "class", "name": "Legend", "members": [{ "kind": "property", "name": "view", "type": { "kind": "ref", "name": "MapView", "module": "@acme/maps/MapView.js" } }] },
              { "kind": "function", "name": "legendFor", "signatures": [{ "params": [{ "name": "view", "type": { "kind": "ref", "name": "MapView", "module": "@acme/maps/MapView.js" } }],
                "returns": { "kind": "ref", "name": "Legend", "module": "@acme/widgets" } }] }] },
            { "specifier": "@acme/widgets/themes/dark.js", "package": "@acme/widgets", "declarations": [{ "kind": "interface", "name": "Theme" }] }
            """);

        Assert.Equal(["MapView.cs", "MapViewSnapshot.cs", "Widgets/Legend.cs", "Widgets/LegendSnapshot.cs", "Widgets/Themes/Theme.cs", "Widgets/Widgets.cs", "Maps.cs", "maps.meta.js"],
            result.Files.Select(f => f.Path));
        var legend = File(result, "Widgets/Legend.cs");
        Assert.Contains("namespace Acme.Maps.Widgets;", legend);
        Assert.Contains("public global::Acme.Maps.MapView GetView(global::Interoper.JsScope scope) =>", legend);
        Assert.Contains("global::Interoper.JsOp.Create(global::Acme.Maps.Maps.LegendId, ", legend);
        Assert.Contains("public static Legend LegendFor(global::Interoper.JsScope scope, global::Acme.Maps.MapView view) =>", File(result, "Widgets/Widgets.cs"));
        Assert.Contains("namespace Acme.Maps.Widgets.Themes;", File(result, "Widgets/Themes/Theme.cs"));
        // The package's root export is named by the declaration alone.
        Assert.Contains("""2: { module: "@acme/widgets", export: "Legend", name: "Legend", members: [] },""", File(result, "maps.meta.js"));
        Assert.Empty(result.Diagnostics);
    }

    [Theory] // R18, R28, design §3.5: an internal module's declarations are imported from the module that re-exports them
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AnInternalModulesDeclarationsAreImportedFromTheirReexport(Api api)
    {
        var result = Packages(api, new Profile { Package = "@acme/maps" }, $$"""
            { "specifier": "@acme/widgets/dist/swatch.js", "package": "@acme/widgets", "internal": true, "declarations": [
              { "kind": "class", "name": "Swatch", "importFrom": { "module": "@acme/widgets" } },
              { "kind": "class", "name": "Chip", "export": "default", "importFrom": { "module": "@acme/widgets", "export": "Chip" } },
              { "kind": "function", "name": "swatchFor", "importFrom": { "module": "@acme/widgets" }, "signatures": [{ "returns": {{String}} }] },
              { "kind": "function", "name": "tint", "importFrom": { "module": "@acme/widgets/tints", "export": "tintOf" }, "signatures": [{ "returns": {{String}} }] }] }
            """, """
            { "tag": "x-swatch", "class": { "kind": "ref", "name": "Swatch", "module": "@acme/widgets/dist/swatch.js" } }
            """);

        Assert.Equal(["IOP2006 @acme/widgets/dist/swatch.js: its functions are Swatch2: Swatch is taken (R27)"], result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("global::Interoper.JsOp.Create(", File(result, "Widgets/Dist/Swatch.cs"));
        var meta = File(result, "maps.meta.js");
        Assert.Contains("""1: { module: "@acme/widgets", export: "Swatch", name: "dist.swatch.Swatch", tag: "x-swatch", members: [] },""", meta);
        Assert.Contains("""2: { module: "@acme/widgets", export: "Chip", name: "dist.swatch.Chip", members: [] },""", meta);
        var functions = File(result, "Widgets/Dist/Swatch2.cs"); // its file is swatch.js, and the class Swatch is taken
        Assert.Contains("""private const string Module = "@acme/widgets";""", functions);
        Assert.Contains("""global::Interoper.JsOp.Call(Module, "swatchFor")""", functions);
        Assert.Contains("""global::Interoper.JsOp.Call("@acme/widgets/tints", "tintOf")""", functions); // re-exported elsewhere
        Assert.Contains(api == Api.Sync ? """.Preload("@acme/widgets", "@acme/widgets/tints");""" : "new global::Interoper.JsClassRegistry()", File(result, "Maps.cs"));
        Assert.Contains(api == Api.Sync ? "ElementAsync<Swatch>(element)" : """ElementAsync<Swatch>(element, "@acme/widgets")""", File(result, "Widgets/Dist/XSwatch.cs"));
    }

    [Theory] // R16, R18, R28, design §3.5: what an internal module declares, and nothing re-exports, can't be imported anywhere
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void WhatCantBeImportedHasNoCreateFunctionComponentOrModule(Api api)
    {
        var result = Packages(api, new Profile { Package = "@acme/maps", ValueClasses = ["Chip"] }, $$"""
            { "specifier": "@acme/widgets/legend", "package": "@acme/widgets", "declarations": [
              { "kind": "class", "name": "Legend", "members": [{ "kind": "property", "name": "swatch", "type": { "kind": "ref", "name": "Swatch", "module": "@acme/widgets/dist/swatch.js" } }] }] },
            { "specifier": "@acme/widgets/dist/swatch.js", "package": "@acme/widgets", "internal": true, "declarations": [
              { "kind": "class", "name": "Swatch", "members": [{ "kind": "property", "name": "color", "type": {{String}} }] },
              { "kind": "class", "name": "Chip", "members": [{ "kind": "property", "name": "size", "type": {{Number}} }] },
              { "kind": "function", "name": "describe", "signatures": [{ "returns": {{String}} }] }] }
            """, """
            { "tag": "x-swatch", "class": { "kind": "ref", "name": "Swatch", "module": "@acme/widgets/dist/swatch.js" } }
            """);

        Assert.Equal(["Widgets/Legend.cs", "Widgets/LegendSnapshot.cs", "Widgets/Dist/Swatch.cs", "Widgets/Dist/SwatchSnapshot.cs", "Widgets/Dist/Chip.cs",
            "Widgets/Dist/ChipSnapshot.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path)); // no functions, no component
        Assert.Contains("global::Interoper.JsOp.Create(", File(result, "Widgets/Legend.cs"));
        var swatch = File(result, "Widgets/Dist/Swatch.cs");
        Assert.DoesNotContain("global::Interoper.JsOp.Create(", swatch); // its instances come from JS, read as any other
        Assert.Contains(api == Api.Sync ? "public string Color" : "public global::System.Threading.Tasks.Task<string> GetColorAsync()", swatch);
        // Neither is imported: not to construct, not for $c, not preloaded.
        var meta = File(result, "maps.meta.js");
        Assert.Contains("""1: { name: "dist.swatch.Chip", value: true, members: ["size"] },""", meta);
        Assert.Contains("""3: { name: "dist.swatch.Swatch", members: ["color"] },""", meta);
        Assert.Contains(api == Api.Sync ? """.Preload("@acme/widgets/legend");""" : "new global::Interoper.JsClassRegistry()", File(result, "Maps.cs"));
        Assert.Equal(
        [
            "IOP2007 Swatch.constructor: skipped: it can't be imported (its module is internal), so there's no Create (R18)",
            "IOP2007 Chip.constructor: skipped: it can't be imported (its module is internal), so there's no Create (R18) and it can't be sent as a value (R16)",
            "IOP2007 describe: skipped: it can't be imported (its module is internal)",
            "IOP2007 <x-swatch>: skipped: its class can't be imported (its module is internal), so nothing defines the element (R28)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R28: an element whose class the extractor couldn't resolve, or that isn't the IR's, is reported
    public void AnElementWithoutItsClassIsReported()
    {
        var result = Elements(Api.Sync, [("chart.js", """{ "kind": "class", "name": "ChartElement" }""")],
            """{ "tag": "x-chart", "class": { "kind": "ref", "name": "ChartElement", "module": "@acme/maps/chart.js" } }""",
            """{ "tag": "x-gauge", "class": { "kind": "unresolved", "text": "GaugeElement" }, "events": [{ "name": "x-gauge-turn" }] }""",
            """{ "tag": "x-plain", "class": { "kind": "ref", "name": "HTMLElement" } }""");

        Assert.Contains("XChart.cs", result.Files.Select(f => f.Path));
        Assert.Equal(
        [
            "IOP2007 <x-gauge>: skipped: its class `GaugeElement` isn't a class the IR declares (R28)",
            "IOP2007 <x-plain>: skipped: its class `HTMLElement` isn't a class the IR declares (R28)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R27: a namespace two packages, or a package and a folder, share holds both's types; same-named ones are renamed
    public void PackagesWithTheSameNameShareANamespace()
    {
        var result = Packages(Api.Sync, new Profile { Package = "@acme/maps" }, """
            { "specifier": "@acme/maps/widgets/Legend.js", "declarations": [{ "kind": "class", "name": "Legend" }] },
            { "specifier": "@acme/widgets/legend", "package": "@acme/widgets", "declarations": [{ "kind": "class", "name": "Legend" }, { "kind": "interface", "name": "Entry" }] },
            { "specifier": "@other/widgets", "package": "@other/widgets", "declarations": [{ "kind": "class", "name": "Legend" }, { "kind": "interface", "name": "Entry" }] }
            """);

        Assert.Equal(["Widgets/Legend.cs", "Widgets/LegendSnapshot.cs", "Widgets/LegendLegend.cs", "Widgets/LegendLegendSnapshot.cs", "Widgets/Entry.cs",
            "Widgets/WidgetsLegend.cs", "Widgets/WidgetsLegendSnapshot.cs", "Widgets/WidgetsEntry.cs", "Maps.cs", "maps.meta.js"], result.Files.Select(f => f.Path));
        Assert.Equal(
        [
            "IOP2006 @acme/widgets/legend: its Legend is LegendLegend: @acme/maps/widgets/Legend.js has one too (R27)",
            "IOP2006 @other/widgets: its Legend is WidgetsLegend: @acme/maps/widgets/Legend.js has one too (R27)",
            "IOP2006 @other/widgets: its Entry is WidgetsEntry: @acme/widgets/legend has one too (R27)",
        ], result.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R27, R18: the fixtures' IR of two packages: demo-widgets' types are in DemoWidgets, its internal module is skipped where it must be
    public void TheWidgetsFixtureIsInItsOwnNamespace()
    {
        var result = Generator.Generate(Fixture.WithWidgetsIr, Fixture.Profile, new(Api.Async, "Acme.Maps"));

        Assert.Contains("namespace Acme.Maps.DemoWidgets;", File(result, "DemoWidgets/Legend.cs"));
        Assert.Contains("public async global::System.Threading.Tasks.Task<global::Acme.Maps.MapView> GetViewAsync(global::Interoper.JsScope scope) =>", File(result, "DemoWidgets/Legend.cs"));
        Assert.Contains("namespace Acme.Maps.DemoWidgets.Dist.Support;", File(result, "DemoWidgets/Dist/Support/LegendEntry.cs"));
        Assert.Contains("public sealed partial class DemoLegend : global::Interoper.JsComponentBase", File(result, "DemoWidgets/DemoLegend.cs"));
        Assert.Contains("IOP2007 describe: skipped: it can't be imported (its module is internal)", result.Diagnostics.Select(d => d.ToString()));
        // The root re-exports the internal swatch.js, so Swatch has Create, and its functions are called through the root.
        Assert.Contains("global::Interoper.JsOp.Create(", File(result, "DemoWidgets/Dist/Support/Swatch.cs"));
        Assert.Contains("""private const string Module = "@acme/demo-widgets";""", File(result, "DemoWidgets/Dist/Support/Swatch2.cs"));
        // Deliberate (R27): legend.js's functions are a class named after the file, and the class Legend has that name.
        Assert.Contains("public static partial class Legend2", File(result, "DemoWidgets/Legend2.cs"));
        Assert.Contains("IOP2006 @acme/demo-widgets/legend: its functions are Legend2: Legend is taken (R27)", result.Diagnostics.Select(d => d.ToString()));
        // The manifest names the zoom label's module by an export subpath; its class is in an internal module that the
        // subpath's index re-exports, so the component imports it from there.
        Assert.Contains("public sealed partial class DemoZoomLabel : global::Interoper.JsComponentBase", File(result, "DemoWidgets/Dist/Components/ZoomLabel/DemoZoomLabel.cs"));
        Assert.Contains("""module: "@acme/demo-widgets/components/zoom-label", export: "ZoomLabelElement", """, File(result, "demo-maps.meta.js"));
        Assert.Contains("""{ name: "demo-zoom-label-copy", fields: ["text"] },""", File(result, "demo-maps.meta.js"));
        // demo-maps' own types are where they are without demo-widgets.
        var alone = Generator.Generate(Fixture.Ir, Fixture.Profile, new(Api.Async, "Acme.Maps"));
        Assert.Empty(alone.Files.Select(f => f.Path).Except(result.Files.Select(f => f.Path)));
    }

    [Theory] // R11, R15, R19, R27: an inline object type is a record named after its owner, without the optional members it can't map
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void AnInlineObjectTypeIsARecordWithoutTheOptionalMembersItCantMap(Api api)
    {
        // set shape(v: (PolyProps & { type: "poly" }) | (DotProps & { type: "dot" })), the intersections resolved (ir.md).
        var result = Generate(api, ("a.js", $$"""
            { "kind": "class", "name": "Feature", "members": [
              { "kind": "property", "name": "shape", "type": {{Number}}, "writeType": { "kind": "union", "discriminant": "type", "types": [
                { "kind": "object", "members": [
                  { "kind": "property", "name": "stops", "optional": true, "type": { "kind": "array", "element": { "kind": "array", "element": {{Number}} } } },
                  { "kind": "property", "name": "legs", "optional": true, "type": { "kind": "array", "element": { "kind": "tuple", "elements": [{{Number}}, {{String}}] } } },
                  { "kind": "property", "name": "type", "type": { "kind": "literal", "value": "poly" } }] },
                { "kind": "object", "members": [
                  { "kind": "property", "name": "radius", "type": {{Number}} },
                  { "kind": "property", "name": "type", "type": { "kind": "literal", "value": "dot" } }] }] } },
              { "kind": "property", "name": "size", "type": { "kind": "object", "members": [{ "kind": "property", "name": "w", "type": {{Number}} }] } }] }
            """));

        Assert.Equal(["IOP2007 Feature.shape.legs: skipped: uses a tuple of different types"], result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains(api == Api.Sync ? "public void SetShape(FeatureShapeInput value) =>" : "public global::System.Threading.Tasks.Task SetShapeAsync(FeatureShapeInput value) =>",
            File(result, "Feature.cs"));
        Assert.Contains("public FeatureShapeInput(FeatureShapeInputPoly value) : this(value, 2) { }", File(result, "FeatureShapeInput.cs"));
        Assert.Contains("public FeatureShapeInput(FeatureShapeInputDot value) : this(value, 3) { }", File(result, "FeatureShapeInput.cs"));
        var poly = File(result, "FeatureShapeInputPoly.cs");
        Assert.Contains("public sealed partial record FeatureShapeInputPoly", poly);
        Assert.Contains("public global::Interoper.Opt<double[][]> Stops { get; init; }", poly);
        Assert.Contains("""writer.WriteString("type"u8, "poly"u8);""", poly); // R20
        Assert.DoesNotContain("Legs", poly);
        Assert.Contains("public required double W { get; init; }", File(result, "FeatureSize.cs"));
        Assert.Contains("public global::Interoper.Opt<FeatureSize> Size { get; init; }", File(result, "FeatureSnapshot.cs"));
    }

    [Fact] // R11, R15, R21: a callback's arguments are data, so their inline records drop the optional members they can't map; a callback alias's once
    public void ACallbacksInlineRecordsDropTheOptionalMembersTheyCantMap()
    {
        // Bag can't be mapped; nothing that only an inline record's dropped member reaches is emitted.
        var result = Generate($$"""
            { "kind": "alias", "name": "Visit", "type": { "kind": "function", "params": [{ "name": "e", "type": { "kind": "object", "members": [{{X}}, {{Legs}}] } }], "returns": {{Void}} } },
            { "kind": "interface", "name": "Bag", "members": [{ "kind": "property", "name": "run", "inherited": true, "type": { "kind": "function", "returns": { "kind": "ref", "name": "IterableIterator", "args": [{{Number}}] } } }] },
            { "kind": "class", "name": "Feature", "members": [
              { "kind": "method", "name": "each", "signatures": [{ "params": [{ "name": "cb", "type": { "kind": "function", "params": [{ "name": "e", "type": { "kind": "object", "members": [{{X}}, {{Legs}},
                { "kind": "property", "name": "bag", "optional": true, "type": { "kind": "ref", "name": "Bag", "module": "@acme/maps/a.js" } }] } }], "returns": {{Void}} } }], "returns": {{Void}} }] },
              { "kind": "method", "name": "visit", "signatures": [{ "params": [{ "name": "cb", "type": { "kind": "ref", "name": "Visit", "module": "@acme/maps/a.js" } }], "returns": {{Void}} }] },
              { "kind": "method", "name": "revisit", "signatures": [{ "params": [{ "name": "cb", "type": { "kind": "ref", "name": "Visit", "module": "@acme/maps/a.js" } }], "returns": {{Void}} }] }] }
            """);

        Assert.Equal([
            "IOP2007 Visit(e).legs: skipped: uses a tuple of different types",
            "IOP2007 Bag: skipped: uses `IterableIterator` (in `Bag`)",
            "IOP2007 Feature.each(cb)(e).legs: skipped: uses a tuple of different types",
            "IOP2007 Feature.each(cb)(e).bag: skipped: uses `IterableIterator` (in `Bag`)",
        ], result.Diagnostics.Select(d => d.ToString()));
        var feature = File(result, "Feature.cs");
        Assert.Contains("public void Each(global::Interoper.JsScope scope, global::System.Action<FeatureEachCbE> cb) =>", feature);
        Assert.Contains("public void Visit(global::Interoper.JsScope scope, global::System.Action<VisitE> cb) =>", feature); // the alias's record, once
        Assert.Contains("public void Revisit(global::Interoper.JsScope scope, global::System.Action<VisitE> cb) =>", feature);
        Assert.DoesNotContain(result.Files, f => f.Path.StartsWith("FeatureVisit", StringComparison.Ordinal) || f.Path.StartsWith("FeatureRevisit", StringComparison.Ordinal));
        foreach (var record in new[] { File(result, "FeatureEachCbE.cs"), File(result, "VisitE.cs") })
        {
            Assert.Contains("public required double X { get; init; }", record);
            Assert.DoesNotContain("Legs", record);
            Assert.DoesNotContain("Bag", record);
        }
        Assert.DoesNotContain("Bag.cs", result.Files.Select(f => f.Path));
    }

    [Fact] // R11, §2: an inline record isn't generic, so one with a type parameter can't be mapped; an optional member is dropped
    public void AnInlineRecordWithATypeParameterCantBeMapped()
    {
        var meta = """{ "kind": "object", "members": [{ "kind": "property", "name": "item", "type": { "kind": "typeParameter", "name": "T" } }] }""";
        var result = Generate($$"""
            { "kind": "interface", "name": "Box", "typeParameters": [{ "name": "T" }], "members": [{{X}}, { "kind": "property", "name": "meta", "optional": true, "type": {{meta}} }] },
            { "kind": "interface", "name": "Crate", "typeParameters": [{ "name": "T" }], "members": [{ "kind": "property", "name": "meta", "type": {{meta}} }] }
            """);

        Assert.Equal([
            "IOP2007 Box.meta: skipped: uses an inline record with the type parameter `T` (§2)",
            "IOP2007 Crate: skipped: uses an inline record with the type parameter `T` (§2) (in `Crate`)",
        ], result.Diagnostics.Select(d => d.ToString()));
        Assert.DoesNotContain("Meta", File(result, "Box.cs"));
        Assert.DoesNotContain(result.Files, f => f.Path.StartsWith("Crate", StringComparison.Ordinal) || f.Path.StartsWith("BoxMeta", StringComparison.Ordinal));
    }

    [Fact] // R11, R27: an inline record doesn't take a declared type's name; it gets a suffix
    public void AnInlineRecordTakesANameNoDeclaredTypeHas()
    {
        var result = Generate($$"""
            { "kind": "interface", "name": "FeatureSize", "members": [{{X}}] },
            { "kind": "class", "name": "Feature", "members": [
              { "kind": "property", "name": "size", "type": { "kind": "object", "members": [{ "kind": "property", "name": "w", "type": {{Number}} }] } },
              { "kind": "property", "name": "box", "type": { "kind": "ref", "name": "FeatureSize", "module": "@acme/maps/a.js" } }] }
            """);

        Assert.Equal(["IOP2006 Acme.Maps.FeatureSize: the inline record is FeatureSize2: FeatureSize is taken (R27)"], result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("public FeatureSize2 Size", File(result, "Feature.cs"));
        Assert.Contains("public FeatureSize Box", File(result, "Feature.cs"));
        Assert.Contains("public required double W { get; init; }", File(result, "FeatureSize2.cs"));
        Assert.Contains("public required double X { get; init; }", File(result, "FeatureSize.cs"));
    }

    [Fact] // R11, R27, R2: an inline record a union narrows to keeps its owner's name, as does a union alias's
    public void ANarrowedInlineRecordKeepsItsName()
    {
        var result = Generate(new Profile { Package = "@acme/maps", Include = ["@acme/maps/a.js"] }, Api.Sync, ("a.js", $$"""
            { "kind": "alias", "name": "Owner", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "Layer", "module": "@acme/maps/b.js" }, { "kind": "object", "members": [{{X}}] }] } },
            { "kind": "class", "name": "Feature", "members": [
              { "kind": "property", "name": "mark", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "Layer", "module": "@acme/maps/b.js" }, { "kind": "object", "members": [{{X}}] }] } },
              { "kind": "property", "name": "owner", "type": { "kind": "ref", "name": "Owner", "module": "@acme/maps/a.js" } }] }
            """), ("b.js", """{ "kind": "class", "name": "Layer" }"""));

        Assert.Equal([
            "IOP2007 FeatureMark: case `Layer` dropped: a class outside the selection (R2)",
            "IOP2007 Owner: case `Layer` dropped: a class outside the selection (R2)",
        ], result.Diagnostics.Select(d => d.ToString()).Order(StringComparer.Ordinal)); // no IOP2006
        Assert.Contains("public FeatureMark Mark", File(result, "Feature.cs"));
        Assert.Contains("public Owner Owner", File(result, "Feature.cs"));
        Assert.Contains("public required double X { get; init; }", File(result, "FeatureMark.cs"));
        Assert.Contains("public required double X { get; init; }", File(result, "Owner.cs"));
    }

    [Fact] // R28, R11, R27: a component doesn't take an inline record's name
    public void AComponentDoesntTakeAnInlineRecordsName()
    {
        var result = Elements(Api.Sync, [("map.js", $$"""
            { "kind": "class", "name": "DemoMap", "members": [{ "kind": "property", "name": "component", "type": { "kind": "object", "members": [{{X}}] } }] }
            """)], """
            { "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMap", "module": "@acme/maps/map.js" }, "attributes": [], "events": [], "slots": [] }
            """);

        Assert.Equal(["IOP2006 <demo-map>: its component is DemoMapComponent2: DemoMap and DemoMapComponent are taken (R28)"], result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("public sealed partial record DemoMapComponent", File(result, "DemoMapComponent.cs"));
        Assert.Contains("public sealed partial class DemoMapComponent2 : global::Interoper.JsComponentBase", File(result, "DemoMapComponent2.cs"));
    }

    [Fact] // R11, R27, R2: a union's inline cases are named by a string tag they share, or else by their position in the union (IOP2006)
    public void InlineUnionCasesAreNamedByTheirTagOrPosition()
    {
        // draw(shape: (PolyProps & { type: "poly" }) | (PointProps & { type: "point" }), mark: Layer | { x: number } | { y: string }), resolved.
        var result = Generate(new Profile { Package = "@acme/maps", Include = ["@acme/maps/a.js"] }, Api.Sync, ("a.js", $$"""
            { "kind": "class", "name": "Feature", "members": [
              { "kind": "method", "name": "draw", "signatures": [{ "params": [{ "name": "shape", "type": { "kind": "union", "types": [
                { "kind": "object", "members": [{ "kind": "property", "name": "type", "type": { "kind": "literal", "value": "poly" } }, { "kind": "property", "name": "stops", "type": { "kind": "array", "element": {{Number}} } }] },
                { "kind": "object", "members": [{ "kind": "property", "name": "type", "type": { "kind": "literal", "value": "point" } }, {{X}}] }] } },
                { "name": "mark", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "Layer", "module": "@acme/maps/b.js" },
                  { "kind": "object", "members": [{{X}}] }, { "kind": "object", "members": [{ "kind": "property", "name": "y", "type": {{String}} }] }] } }],
                "returns": {{Void}} }] }] }
            """), ("b.js", """{ "kind": "class", "name": "Layer" }"""));

        Assert.Equal([
            "IOP2007 FeatureDrawMark: case `Layer` dropped: a class outside the selection (R2)",
            "IOP2006 Acme.Maps.FeatureDrawMark: its case 2 is FeatureDrawMarkCase2: the inline cases have no string tag (R27)",
            "IOP2006 Acme.Maps.FeatureDrawMark: its case 3 is FeatureDrawMarkCase3: the inline cases have no string tag (R27)",
        ], result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("public FeatureDrawShape(FeatureDrawShapePoly value)", File(result, "FeatureDrawShape.cs"));
        Assert.Contains("public FeatureDrawShape(FeatureDrawShapePoint value)", File(result, "FeatureDrawShape.cs"));
        Assert.Contains("public FeatureDrawMark(FeatureDrawMarkCase2 value)", File(result, "FeatureDrawMark.cs"));
        Assert.Contains("public FeatureDrawMark(FeatureDrawMarkCase3 value)", File(result, "FeatureDrawMark.cs"));
    }

    [Fact] // R24, R11: an inline payload is projected to its record's members, as a named one is
    public void AnInlinePayloadIsProjected()
    {
        var result = Generate($$"""
            { "kind": "class", "name": "Feature", "events": [{ "name": "move", "type": { "kind": "object", "members": [{{X}}, { "kind": "property", "name": "y", "type": {{Number}} }] } }] }
            """, new Profile { Package = "@acme/maps", Events = new(Subscribe: "on") });

        Assert.Empty(result.Diagnostics);
        Assert.Contains("""scope.Subscribe(Handle, "on", "move", FeatureMove.Read, handler, options, ["x", "y"]);""", File(result, "Feature.cs"));
        Assert.Contains("public required double Y { get; init; }", File(result, "FeatureMove.cs"));
    }

    private const string Void = """{ "kind": "primitive", "name": "void" }""";
    private const string X = """{ "kind": "property", "name": "x", "type": { "kind": "primitive", "name": "number" } }""";
    private const string Legs = """{ "kind": "property", "name": "legs", "optional": true, "type": { "kind": "tuple", "elements": [{ "kind": "primitive", "name": "number" }, { "kind": "primitive", "name": "string" }] } }""";

    private const string Number = """{ "kind": "primitive", "name": "number" }""";
    private const string String = """{ "kind": "primitive", "name": "string" }""";


    /// <summary>The modules of <c>@acme/maps</c>, by their path in it, and its custom elements.</summary>
    private static GeneratorResult Elements(Api api, (string Path, string Declarations)[] modules, params string[] elements)
    {
        var json = string.Join(",\n", modules.Select(m => $$"""{ "specifier": "@acme/maps/{{m.Path}}", "declarations": [{{m.Declarations}}] }"""));
        var ir = IrDocument.Parse($$"""{ "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [{{json}}], "elements": [{{string.Join(",", elements)}}] }""");
        return Generator.Generate(ir, new Profile { Package = "@acme/maps" }, new(api, "Acme.Maps"));
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

    /// <summary>An IR of <c>@acme/maps</c> with the given modules, which may be another package's, and elements.</summary>
    private static GeneratorResult Packages(Api api, string modules) => Packages(api, new Profile { Package = "@acme/maps" }, modules);

    private static GeneratorResult Packages(Api api, Profile profile, string modules, string elements = "")
    {
        var ir = IrDocument.Parse($$"""{ "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [{{modules}}], "elements": [{{elements}}] }""");
        return Generator.Generate(ir, profile, new(api, "Acme.Maps"));
    }

    private static string File(GeneratorResult result, string path) => result.Files.Single(f => f.Path == path).Text;
}
