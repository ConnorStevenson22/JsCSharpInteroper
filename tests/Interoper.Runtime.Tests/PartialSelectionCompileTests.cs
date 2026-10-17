using System.Runtime.CompilerServices;
using Interoper.Generator;
using Interoper.Generator.Ir;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Interoper.Runtime.Tests;

/// <summary>
/// Mapping §8, R2, R27: what the generator emits for a partial selection compiles. Each module of the fixture on its
/// own leaves classes out, so unions drop or narrow cases and members are skipped; the generator's tests check that it
/// never throws for any module or pair (PartialSelectionTests), and this compiles each single module's bindings.
/// </summary>
public sealed class PartialSelectionCompileTests
{
    private static readonly IrDocument Ir = IrDocument.Load(Fixture("demo-maps.ir.json"));
    private static readonly Generator.Profile Profile = Generator.Profile.Load(Fixture("profile.json"));

    public static TheoryData<string, Api> Modules()
    {
        var data = new TheoryData<string, Api>();
        foreach (var module in Ir.Modules)
        {
            data.Add(module.Specifier, Api.Sync);
            data.Add(module.Specifier, Api.Async);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void EachModulesBindingsCompile(string module, Api api)
    {
        Compile(Generator.Generator.Generate(Ir, Profile with { Include = [module] }, new(api, "Acme.Maps")));
    }

    /// <summary>
    /// The shapes the fixture doesn't have compile too, with one module selected and with everything: inline records in a
    /// union, named by tag or position, a callback's and a payload's, one whose name a declared type has, and one with a
    /// type parameter (R11, R27); a union narrowed to its one selected case, an inline one too (R2); an event without a payload (R24); an
    /// interface with an iterator method, one with an inherited callback, and one with an inherited generic callback that only a
    /// union alias flattened into another reaches (R6); and an abstract element class named like
    /// its tag, with a property whose inline record has its component's name, a primitive attribute over a union property and an event whose detail is a union of inline records (R28).
    /// </summary>
    [Theory]
    [InlineData(Api.Sync, false)]
    [InlineData(Api.Async, false)]
    [InlineData(Api.Sync, true)]
    [InlineData(Api.Async, true)]
    public void TheShapesTheFixtureLacksCompile(Api api, bool everything)
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        const string String = """{ "kind": "primitive", "name": "string" }""";
        const string Void = """{ "kind": "primitive", "name": "void" }""";
        const string X = """{ "kind": "property", "name": "x", "type": { "kind": "primitive", "name": "number" } }""";
        var legs = $$"""{ "kind": "property", "name": "legs", "optional": true, "type": { "kind": "tuple", "elements": [{{Number}}, {{String}}] } }""";
        var meta = """{ "kind": "object", "members": [{ "kind": "property", "name": "item", "type": { "kind": "typeParameter", "name": "T" } }] }""";
        var ir = IrDocument.Parse($$"""
            { "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [
              { "specifier": "@acme/maps/a.js", "declarations": [
                { "kind": "class", "name": "Layer" },
                { "kind": "alias", "name": "Owner", "type": { "kind": "union", "types": [
                  { "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }, { "kind": "ref", "name": "Folder", "module": "@acme/maps/b.js" }] } },
                { "kind": "alias", "name": "Visit", "type": { "kind": "function", "params": [{ "name": "e", "type": { "kind": "object", "members": [{{X}}, {{legs}}] } }], "returns": {{Void}} } },
                { "kind": "interface", "name": "Bag", "members": [{ "kind": "property", "name": "run", "inherited": true, "type": { "kind": "function", "returns": { "kind": "ref", "name": "IterableIterator", "args": [{{Number}}] } } }] },
                { "kind": "interface", "name": "Box", "typeParameters": [{ "name": "T" }], "members": [{{X}}, { "kind": "property", "name": "meta", "optional": true, "type": {{meta}} }] },
                { "kind": "interface", "name": "Crate", "typeParameters": [{ "name": "T" }], "members": [{ "kind": "property", "name": "meta", "type": {{meta}} }] },
                { "kind": "interface", "name": "FeatureSize", "members": [{{X}}] },
                { "kind": "alias", "name": "Mark", "typeParameters": [{ "name": "T" }], "type": { "kind": "unresolved", "text": "Mark<T>" } },
                { "kind": "interface", "name": "Rack", "members": [{ "kind": "property", "name": "release", "inherited": true, "type": { "kind": "function", "typeParameters": [{ "name": "T" }],
                  "params": [{ "name": "key", "optional": true, "type": { "kind": "ref", "name": "Mark", "module": "@acme/maps/a.js", "args": [{ "kind": "typeParameter", "name": "T" }] } }], "returns": {{Void}} } }] },
                { "kind": "alias", "name": "Spot", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "Pin", "module": "@acme/maps/a.js" },
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "c" } }, {{X}}] }] } },
                { "kind": "alias", "name": "Pin", "type": { "kind": "union", "types": [
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "a" } }, { "kind": "property", "name": "n", "type": {{Number}} },
                    { "kind": "property", "name": "rack", "optional": true, "type": { "kind": "ref", "name": "Rack", "module": "@acme/maps/a.js" } }] },
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "b" } }, { "kind": "property", "name": "s", "type": {{String}} }] }] } },
                { "kind": "class", "name": "Feature", "members": [
                  { "kind": "method", "name": "each", "signatures": [{ "params": [{ "name": "cb", "type": { "kind": "function", "params": [{ "name": "e", "type": { "kind": "object", "members": [{{X}}, {{legs}},
                    { "kind": "property", "name": "bag", "optional": true, "type": { "kind": "ref", "name": "Bag", "module": "@acme/maps/a.js" } }] } }], "returns": {{Void}} } }], "returns": {{Void}} }] },
                  { "kind": "method", "name": "visit", "signatures": [{ "params": [{ "name": "cb", "type": { "kind": "ref", "name": "Visit", "module": "@acme/maps/a.js" } }], "returns": {{Void}} }] },
                  { "kind": "method", "name": "draw", "signatures": [{ "params": [{ "name": "shape", "type": { "kind": "union", "types": [
                    { "kind": "object", "members": [{ "kind": "property", "name": "type", "type": { "kind": "literal", "value": "poly" } }, { "kind": "property", "name": "stops", "type": { "kind": "array", "element": {{Number}} } }] },
                    { "kind": "object", "members": [{ "kind": "property", "name": "type", "type": { "kind": "literal", "value": "point" } }, {{X}}] }] } },
                    { "name": "mark", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "Folder", "module": "@acme/maps/b.js" },
                      { "kind": "object", "members": [{{X}}] }, { "kind": "object", "members": [{ "kind": "property", "name": "y", "type": {{String}} }] }] } }], "returns": {{Void}} }] },
                  { "kind": "property", "name": "owner", "type": { "kind": "ref", "name": "Owner", "module": "@acme/maps/a.js" } },
                  { "kind": "property", "name": "spot", "type": { "kind": "ref", "name": "Spot", "module": "@acme/maps/a.js" } },
                  { "kind": "property", "name": "mark", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "Folder", "module": "@acme/maps/b.js" }, { "kind": "object", "members": [{{X}}] }] } },
                  { "kind": "property", "name": "size", "type": { "kind": "object", "members": [{ "kind": "property", "name": "w", "type": {{Number}} }] } },
                  { "kind": "property", "name": "shape", "type": { "kind": "union", "discriminant": "type", "types": [
                    { "kind": "object", "members": [
                      { "kind": "property", "name": "stops", "optional": true, "type": { "kind": "array", "element": { "kind": "array", "element": {{Number}} } } },
                      { "kind": "property", "name": "legs", "optional": true, "type": { "kind": "array", "element": { "kind": "tuple", "elements": [{{Number}}, {{String}}] } } },
                      { "kind": "property", "name": "type", "type": { "kind": "literal", "value": "poly" } }] },
                    { "kind": "object", "members": [
                      { "kind": "property", "name": "radius", "type": {{Number}} },
                      { "kind": "property", "name": "type", "type": { "kind": "literal", "value": "dot" } }] }] } }],
                  "events": [{ "name": "tick", "type": {{Void}} }, { "name": "move", "type": { "kind": "object", "members": [{{X}}, { "kind": "property", "name": "y", "type": {{Number}} }] } }] },
                { "kind": "interface", "name": "Entries", "members": [
                  { "kind": "property", "name": "size", "type": {{Number}} },
                  { "kind": "method", "name": "values", "signatures": [{ "params": [], "returns": { "kind": "ref", "name": "IterableIterator", "args": [{{Number}}] } }] }] },
                { "kind": "function", "name": "count", "signatures": [{ "params": [{ "name": "entries", "type": { "kind": "ref", "name": "Entries", "module": "@acme/maps/a.js" } }], "returns": {{Number}} }] },
                { "kind": "class", "name": "XChart", "abstract": true, "members": [
                  { "kind": "property", "name": "label", "type": { "kind": "union", "types": [{{String}}, { "kind": "array", "element": {{String}} }] } },
                  { "kind": "property", "name": "component", "type": { "kind": "object", "members": [{{X}}] } }] }] },
              { "specifier": "@acme/maps/b.js", "declarations": [{ "kind": "class", "name": "Folder" }] }],
              "elements": [{ "tag": "x-chart", "class": { "kind": "ref", "name": "XChart", "module": "@acme/maps/a.js" },
                "attributes": [{ "name": "label", "property": "label", "type": {{String}} }],
                "events": [{ "name": "pick", "detail": { "kind": "union", "discriminant": "kind", "types": [
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "a" } },
                    { "kind": "property", "name": "legs", "optional": true, "type": { "kind": "tuple", "elements": [{{Number}}, {{String}}] } }] },
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "b" } }] }] } }] }] }
            """);
        var profile = new Generator.Profile { Package = "@acme/maps", Events = new(Subscribe: "on"), Include = everything ? null : ["@acme/maps/a.js"] };
        var result = Generator.Generator.Generate(ir, profile, new(api, "Acme.Maps"));
        Assert.Contains(result.Files, f => f.Path == "XChartComponent2.cs"); // XChartComponent is XChart.component's record
        Assert.Contains(result.Files, f => f.Path == "FeatureSize2.cs");
        Assert.Contains("IOP2007 <x-chart> on:pick.legs: skipped: uses a tuple of different types", result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("IOP2007 Feature.each(cb)(e).bag: skipped: uses `IterableIterator` (in `Bag`)", result.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("IOP2007 Spot.rack: skipped: uses generic `Mark` (in `Mark`) (in `Rack`)", result.Diagnostics.Select(d => d.ToString()));
        Compile(result);
    }

    /// <summary>
    /// Names and generics that real libraries have and the fixture doesn't compile too: an event payload with a required
    /// member it can't map (R24); classes and payloads with constrained type parameters (§2); a namespace named like a type
    /// (R27); flattened literal unions that share a value (R6); a record member named like the scoped record it holds
    /// (R17); and events a subclass of a generic collection redeclares (R26).
    /// </summary>
    [Theory]
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void TheNamesAndGenericsOfLargerLibrariesCompile(Api api)
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        const string Void = """{ "kind": "primitive", "name": "void" }""";
        const string T = """{ "kind": "typeParameter", "name": "T" }""";
        const string Input = """{ "kind": "ref", "name": "Input", "module": "@acme/maps/Marker.js" }""";
        const string Marker = """{ "kind": "ref", "name": "Marker", "module": "@acme/maps/Marker.js" }""";
        const string Promise = """{ "kind": "ref", "name": "Promise", "args": [{ "kind": "typeParameter", "name": "T" }] }""";
        static string Ref(string name, string module, params string[] args) =>
            $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/{{module}}", "args": [{{string.Join(", ", args)}}] }""";
        static string Literals(params string[] values) => $$"""{ "kind": "union", "types": [{{string.Join(", ", values.Select(v => $$"""{ "kind": "literal", "value": "{{v}}" }"""))}}] }""";
        var ir = IrDocument.Parse($$"""
            { "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [
              { "specifier": "@acme/maps/Marker.js", "declarations": [
                { "kind": "interface", "name": "Input", "members": [{ "kind": "property", "name": "button", "type": {{Number}} }] },
                { "kind": "interface", "name": "InputEvent", "typeParameters": [{ "name": "E", "constraint": {{Input}} }], "members": [
                  { "kind": "property", "name": "source", "type": { "kind": "typeParameter", "name": "E" } },
                  { "kind": "property", "name": "wait", "type": { "kind": "function", "typeParameters": [{ "name": "T" }], "params": [{ "name": "op", "type": { "kind": "function", "returns": {{Promise}} } }], "returns": {{Promise}} } },
                  { "kind": "method", "name": "stopPropagation", "signatures": [{ "params": [], "returns": {{Void}} }] }] },
                { "kind": "interface", "name": "PressEvent", "extends": [{{Ref("InputEvent", "Marker.js", Input)}}], "members": [{ "kind": "property", "name": "x", "type": {{Number}} }] },
                { "kind": "alias", "name": "Warm", "type": {{Literals("red", "amber")}} },
                { "kind": "alias", "name": "Cool", "type": {{Literals("amber", "blue")}} },
                { "kind": "class", "name": "Marker", "typeParameters": [{ "name": "V", "constraint": {{Ref("Icon", "marker/Icon.js")}} }], "members": [
                  { "kind": "property", "name": "icon", "type": { "kind": "typeParameter", "name": "V" } },
                  { "kind": "property", "name": "tone", "type": { "kind": "union", "types": [{{Ref("Warm", "Marker.js")}}, {{Ref("Cool", "Marker.js")}}] } },
                  { "kind": "property", "name": "caption", "type": {{Ref("Card", "marker/Icon.js")}} }],
                  "events": [{ "name": "press", "type": {{Ref("PressEvent", "Marker.js")}} }] },
                { "kind": "class", "name": "List", "typeParameters": [{ "name": "T" }], "events": [{ "name": "change", "type": {{Ref("Change", "Marker.js", T)}} }] },
                { "kind": "interface", "name": "Change", "typeParameters": [{ "name": "T" }], "members": [{ "kind": "property", "name": "added", "type": { "kind": "array", "element": {{T}} } }] },
                { "kind": "class", "name": "Route", "extends": {{Ref("List", "Marker.js", Marker)}}, "events": [{ "name": "change", "type": {{Ref("Change", "Marker.js", Marker)}} }] }] },
              { "specifier": "@acme/maps/marker/Icon.js", "declarations": [
                { "kind": "class", "name": "Icon" },
                { "kind": "interface", "name": "Caption", "members": [{ "kind": "property", "name": "icon", "type": {{Ref("Icon", "marker/Icon.js")}} }] },
                { "kind": "interface", "name": "Card", "members": [{ "kind": "property", "name": "caption", "optional": true, "type": {{Ref("Caption", "marker/Icon.js")}} }] }] }] }
            """);
        var profile = new Generator.Profile { Package = "@acme/maps", Events = new(Subscribe: "on"), Collections = ["List"] };
        var result = Generator.Generator.Generate(ir, profile, new(api, "Acme.Maps"));
        Assert.Contains(result.Files, f => f.Path == "Marker2/Icon.cs");
        Assert.Contains(result.Files, f => f.Path == "Marker.cs");
        Compile(result);
    }

    private static void Compile(GeneratorResult result)
    {
        var trees = result.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Text, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose), f.Path));
        // R29: the XML docs are checked (CS1570 and kin); undocumented members get none, so CS1591 is off, as in the golden slice.
        var compilation = CSharpCompilation.Create("Partial", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                specificDiagnosticOptions: [new("CS1591", ReportDiagnostic.Suppress)]));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
    }

    // The platform's assemblies, the runtime's and Blazor's.
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(JsObject).Assembly.Location, typeof(JsComponentBase).Assembly.Location, typeof(ComponentBase).Assembly.Location,
                typeof(Microsoft.AspNetCore.Components.Web.MouseEventArgs).Assembly.Location, typeof(Microsoft.JSInterop.IJSRuntime).Assembly.Location])
            .Distinct().Select(p => MetadataReference.CreateFromFile(p))]);

    private static string Fixture(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "fixtures", "demo-maps", file);
}
