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
        Assert.Equal(["Tag.cs"], result.Files.Select(f => f.Path));
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
