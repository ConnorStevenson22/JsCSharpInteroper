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
    /// The shapes these fixes reach, which the fixture doesn't have, compile too: inline records in a union (R11), a union
    /// narrowed to its one selected case (R2), an event without a payload (R24), an interface with an iterator method,
    /// and an abstract element class named like its tag, with a primitive attribute over a union property and an event
    /// whose detail is a union of inline records (R28).
    /// </summary>
    [Theory]
    [InlineData(Api.Sync)]
    [InlineData(Api.Async)]
    public void TheShapesTheFixtureLacksCompile(Api api)
    {
        const string Number = """{ "kind": "primitive", "name": "number" }""";
        const string String = """{ "kind": "primitive", "name": "string" }""";
        var ir = IrDocument.Parse($$"""
            { "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [
              { "specifier": "@acme/maps/a.js", "declarations": [
                { "kind": "class", "name": "Layer" },
                { "kind": "alias", "name": "Owner", "type": { "kind": "union", "types": [
                  { "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }, { "kind": "ref", "name": "Folder", "module": "@acme/maps/b.js" }] } },
                { "kind": "class", "name": "Feature", "members": [
                  { "kind": "property", "name": "owner", "type": { "kind": "ref", "name": "Owner", "module": "@acme/maps/a.js" } },
                  { "kind": "property", "name": "size", "type": { "kind": "object", "members": [{ "kind": "property", "name": "w", "type": {{Number}} }] } },
                  { "kind": "property", "name": "shape", "type": { "kind": "union", "discriminant": "type", "types": [
                    { "kind": "object", "members": [
                      { "kind": "property", "name": "stops", "optional": true, "type": { "kind": "array", "element": { "kind": "array", "element": {{Number}} } } },
                      { "kind": "property", "name": "legs", "optional": true, "type": { "kind": "array", "element": { "kind": "tuple", "elements": [{{Number}}, {{String}}] } } },
                      { "kind": "property", "name": "type", "type": { "kind": "literal", "value": "poly" } }] },
                    { "kind": "object", "members": [
                      { "kind": "property", "name": "radius", "type": {{Number}} },
                      { "kind": "property", "name": "type", "type": { "kind": "literal", "value": "dot" } }] }] } }],
                  "events": [{ "name": "tick", "type": { "kind": "primitive", "name": "void" } }] },
                { "kind": "interface", "name": "Entries", "members": [
                  { "kind": "property", "name": "size", "type": {{Number}} },
                  { "kind": "method", "name": "values", "signatures": [{ "params": [], "returns": { "kind": "ref", "name": "IterableIterator", "args": [{{Number}}] } }] }] },
                { "kind": "function", "name": "count", "signatures": [{ "params": [{ "name": "entries", "type": { "kind": "ref", "name": "Entries", "module": "@acme/maps/a.js" } }], "returns": {{Number}} }] },
                { "kind": "class", "name": "XChart", "abstract": true, "members": [
                  { "kind": "property", "name": "label", "type": { "kind": "union", "types": [{{String}}, { "kind": "array", "element": {{String}} }] } }] }] },
              { "specifier": "@acme/maps/b.js", "declarations": [{ "kind": "class", "name": "Folder" }] }],
              "elements": [{ "tag": "x-chart", "class": { "kind": "ref", "name": "XChart", "module": "@acme/maps/a.js" },
                "attributes": [{ "name": "label", "property": "label", "type": {{String}} }],
                "events": [{ "name": "pick", "detail": { "kind": "union", "discriminant": "kind", "types": [
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "a" } },
                    { "kind": "property", "name": "legs", "optional": true, "type": { "kind": "tuple", "elements": [{{Number}}, {{String}}] } }] },
                  { "kind": "object", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "b" } }] }] } }] }] }
            """);
        var result = Generator.Generator.Generate(ir, new Generator.Profile { Package = "@acme/maps", Events = new(Subscribe: "on"), Include = ["@acme/maps/a.js"] }, new(api, "Acme.Maps"));
        Assert.Contains(result.Files, f => f.Path == "XChartComponent.cs");
        Assert.Contains("IOP2007 <x-chart> on:pick.legs: skipped: uses a tuple of different types", result.Diagnostics.Select(d => d.ToString()));
        Compile(result);
    }

    private static void Compile(GeneratorResult result)
    {
        var trees = result.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Text, new CSharpParseOptions(LanguageVersion.Preview), f.Path));
        var compilation = CSharpCompilation.Create("Partial", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
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
