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
    /// interface with an iterator method, and one with an inherited callback; and an abstract element class named like
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
        Compile(result);
    }

    private static void Compile(GeneratorResult result)
    {
        var trees = result.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Text, new CSharpParseOptions(LanguageVersion.Preview), f.Path));
        var compilation = CSharpCompilation.Create("Partial", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
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
