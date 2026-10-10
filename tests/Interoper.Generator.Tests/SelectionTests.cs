using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>What the profile's <c>include</c> selects, and what selection skips because it can't be mapped (mapping §8).</summary>
public sealed class SelectionTests
{
    [Fact]
    public void WithoutIncludeTheWholeFixtureIsSelected()
    {
        var selection = Selection.Select(Fixture.Ir, Fixture.Profile);

        Assert.Equal(
        [
            "MapView.js: Basemap, Cursor, CenterAndZoom, Camera, FlyToTarget, ClickEvent, MapViewEvents, MapView",
            "Marker.js: Marker",
            "core.js: Observable, Emitter",
            "demo-map.js: DemoMapClickDetail, DemoMapElement",
            "geometry.js: ShapeType, Bounds, Shape, Point, Line, Polygon",
            "layers/Layer.js: Layer",
            "layers/TileLayer.js: TileLayer",
            "measure.js: distance, perimeter",
            "styles.js: Rgba, ColorInput, IconName, FillStyle, StrokeStyle, IconStyle, Style, toRgba",
        ], Modules(selection));

        Assert.Equal(["center", "zoom", "rotation", "basemap", "cursor", "title", "selected", "screenToMap", "flyTo", "markerAt"], Members(selection, "MapView"));
        Assert.Equal(["click"], Class(selection, "MapView").Events.Select(e => e.Name));
        Assert.Equal(["bounds", "clone"], Members(selection, "Shape")); // not the discriminant `type`
        Assert.Equal(["type", "x", "y"], Members(selection, "Point")); // a constant (R20)
        Assert.Empty(Members(selection, "Observable")); // the runtime-class property and the watch method are never members
        Assert.Empty(Members(selection, "Emitter")); // nor is the event method; `emit` has a type parameter

        // R14 isn't supported yet, so flyTo loses its optional `options` (R22).
        var flyTo = Assert.IsType<MethodMember>(Class(selection, "MapView").Members.Single(m => m.Name == "flyTo"));
        Assert.Equal(["target"], Assert.Single(flyTo.Signatures).Params.Select(p => p.Name));
    }

    [Fact]
    public void AnIncludeGlobSelectsItsModulesAndTheBasesOfTheirClasses()
    {
        var selection = Select("@acme/demo-maps/geometry.js");
        Assert.Equal(["core.js: Observable", "geometry.js: ShapeType, Bounds, Shape, Point, Line, Polygon"], Modules(selection));
        Assert.Empty(selection.Diagnostics);
    }

    [Fact]
    public void AStarMatchesAnyCharactersSlashesIncluded()
    {
        Assert.Equal(Modules(Selection.Select(Fixture.Ir, Fixture.Profile)), Modules(Select("@acme/*")));
        Assert.Equal(["core.js: Observable", "geometry.js: ShapeType, Bounds, Shape, Point, Line, Polygon", "measure.js: distance, perimeter"],
            Modules(Select("*/measure.js", "*/geometry.js")));
        Assert.Equal(["core.js: Observable", "layers/Layer.js: Layer", "layers/TileLayer.js: TileLayer"], Modules(Select("@acme/demo-maps/layers/*")));
    }

    [Fact]
    public void AMemberThatUsesAClassOutsideTheSelectionIsSkipped()
    {
        var selection = Select("*/MapView.js");
        Assert.Equal(["zoom", "rotation", "basemap", "cursor", "title"], Members(selection, "MapView"));
        Assert.Equal(
        [
            "IOP2007 FlyToTarget: skipped: uses class `Shape` outside the selection (in `FlyToTarget`)",
            "IOP2007 FlyToOptions: skipped: uses `AbortSignal` (in `FlyToOptions`)",
            "IOP2007 ClickEvent: skipped: uses class `Point` outside the selection (in `ClickEvent`)",
            "IOP2007 MapViewEvents: skipped: uses class `Point` outside the selection (in `ClickEvent`) (in `MapViewEvents`)",
            "IOP2007 MapViewOptions: skipped: uses class `Point` outside the selection (in `MapViewOptions`)",
            "IOP2007 MapView.center: skipped: uses class `Point` outside the selection",
            "IOP2007 MapView.container: skipped: uses `HTMLElement`",
            "IOP2007 MapView.selected: skipped: uses class `Marker` outside the selection",
            "IOP2007 MapView.markers: skipped: uses generic `Collection`",
            "IOP2007 MapView.layers: skipped: uses generic `Collection`",
            "IOP2007 MapView.screenToMap: skipped: uses class `Point` outside the selection", // once for its two overloads
            "IOP2007 MapView.flyTo: skipped: uses class `Shape` outside the selection (in `FlyToTarget`)",
            "IOP2007 MapView.markerAt: skipped: uses class `Marker` outside the selection",
            "IOP2007 MapView.on:click: skipped: uses class `Point` outside the selection (in `ClickEvent`)",
            "IOP2007 Emitter.emit: skipped: uses type parameter `K`",
        ], selection.Diagnostics.Select(d => d.ToString()));
    }

    [Fact] // R6
    public void AUnionAliasInsideAUnionIsFlattened()
    {
        var selection = Selection.Select(Ir("""
            { "kind": "alias", "name": "Target", "type": { "kind": "union", "types": [
              { "kind": "ref", "name": "Camera", "module": "@acme/maps/b.js" }, { "kind": "primitive", "name": "string" }] } }
            """, """
            { "kind": "alias", "name": "Camera", "type": { "kind": "union", "types": [
              { "kind": "array", "element": { "kind": "primitive", "name": "number" } }, { "kind": "ref", "name": "Box", "module": "@acme/maps/b.js" }] } },
            { "kind": "interface", "name": "Box", "members": [{ "kind": "property", "name": "w", "type": { "kind": "primitive", "name": "number" } }] }
            """), new Profile { Package = "@acme/maps", Include = ["@acme/maps/a.js"] });
        Assert.Equal(["Target", "Box"], selection.Declarations.Select(s => s.Declaration.Name)); // no Camera
    }

    [Fact]
    public void ARecursiveDataTypeIsSkippedForWhatItUses()
    {
        // A reaches B, which reaches A again before A's AbortSignal is seen: B must not pass for fine.
        var selection = Selection.Select(Ir("""
            { "kind": "interface", "name": "A", "members": [
              { "kind": "property", "name": "b", "type": { "kind": "ref", "name": "B", "module": "@acme/maps/a.js" } },
              { "kind": "property", "name": "signal", "type": { "kind": "ref", "name": "AbortSignal" } }] },
            { "kind": "interface", "name": "B", "members": [
              { "kind": "property", "name": "a", "type": { "kind": "ref", "name": "A", "module": "@acme/maps/a.js" } }] },
            { "kind": "function", "name": "f", "signatures": [{ "params": [{ "name": "b", "type": { "kind": "ref", "name": "B", "module": "@acme/maps/a.js" } }] }] }
            """), new Profile { Package = "@acme/maps" });
        Assert.Empty(selection.Declarations);
        Assert.Equal(["A", "B", "f"], selection.Diagnostics.Select(d => d.Subject));
    }

    /// <summary>
    /// What the type map can't map is skipped at selection, with what uses it: here the function <c>f(x: T)</c> and
    /// the interface <c>Holder { t: T }</c>. An ambiguous union is IOP2003 (R19), anything else IOP2007.
    /// </summary>
    [Theory]
    [InlineData( // R2: a literal next to other types
        """{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "literal", "value": "auto" }, { "kind": "array", "element": { "kind": "primitive", "name": "string" } }] } }""",
        "IOP2007", "the union `T` of a literal and other types")]
    [InlineData( // R2: unions don't box
        """{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "number" }] } }""",
        "IOP2007", "the union `T` with the value-type case double (R2)")]
    [InlineData( // §2: a TS enum
        """{ "kind": "enum", "name": "T", "members": [{ "name": "a", "value": "a" }, { "name": "b", "value": 1 }] }""",
        "IOP2007", "an enum of strings and numbers")]
    [InlineData( // §2: a number-literal union
        """{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "literal", "value": 0.5 }, { "kind": "literal", "value": 1 }] } }""",
        "IOP2007", "the non-integer enum value 0.5")]
    [InlineData( // R19 step 4
        """{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "array", "element": { "kind": "primitive", "name": "string" } }, { "kind": "array", "element": { "kind": "primitive", "name": "number" } }] } }""",
        "IOP2003", "the ambiguous union `T`: string[] and double[] are both JSON Array (R19)")]
    [InlineData( // R19 step 3: optional members don't count
        """
        { "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "X", "module": "@acme/maps/a.js" }, { "kind": "ref", "name": "Y", "module": "@acme/maps/a.js" }] } },
        { "kind": "interface", "name": "X", "members": [{ "kind": "property", "name": "a", "optional": true, "type": { "kind": "primitive", "name": "number" } }] },
        { "kind": "interface", "name": "Y", "members": [{ "kind": "property", "name": "b", "type": { "kind": "primitive", "name": "number" } }] }
        """,
        "IOP2003", "the ambiguous union `T`: X has no required member the other cases lack (R19)")]
    [InlineData( // R15: a data record
        """{ "kind": "interface", "name": "T", "indexSignatures": [{ "key": { "kind": "primitive", "name": "string" }, "type": { "kind": "primitive", "name": "number" } }] }""",
        "IOP2007", "an index signature")]
    [InlineData( // R17: an entity in a data record
        """
        { "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "layer", "type": { "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" } }] },
        { "kind": "class", "name": "Layer" }
        """,
        "IOP2007", "the entity `Layer` in a data position (R17)")]
    [InlineData( // §2: a type the generator doesn't map
        """{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "size", "type": { "kind": "object", "members": [] } }] }""",
        "IOP2007", "an inline object type")]
    [InlineData( // R19 step 2: the discriminant is read as a string
        """
        { "kind": "alias", "name": "T", "type": { "kind": "union", "discriminant": "kind", "types": [{ "kind": "ref", "name": "X", "module": "@acme/maps/a.js" }, { "kind": "ref", "name": "Y", "module": "@acme/maps/a.js" }] } },
        { "kind": "interface", "name": "X", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": 1 } }] },
        { "kind": "interface", "name": "Y", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": 2 } }] }
        """,
        "IOP2007", "the union `T`, whose discriminant `kind` isn't a string on every case (R19)")]
    [InlineData( // R2, R3: an alias of nothing but null and undefined
        """{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "null" }, { "kind": "primitive", "name": "undefined" }] } }""",
        "IOP2007", "only `null` and `undefined`")]
    [InlineData( // R2, R3: a member of nothing but null and undefined
        """{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "x", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "null" }, { "kind": "primitive", "name": "undefined" }] } }] }""",
        "IOP2007", "only `null` and `undefined`")]
    [InlineData( // R2, R3: arrays have no Opt elements
        """{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "xs", "type": { "kind": "array", "element": { "kind": "union", "types": [{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "null" }] } } }] }""",
        "IOP2007", "a nullable array or tuple element")]
    [InlineData( // R5: a tuple is an array of one C# type
        """{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "pair", "type": { "kind": "tuple", "elements": [{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "number" }] } }] }""",
        "IOP2007", "a tuple of different types")]
    public void WhatCantBeMappedIsSkippedWithWhatUsesIt(string declaration, string code, string problem)
    {
        var ir = Ir(declaration + """
            ,
            { "kind": "interface", "name": "Holder", "members": [{ "kind": "property", "name": "t", "type": { "kind": "ref", "name": "T", "module": "@acme/maps/a.js" } }] },
            { "kind": "function", "name": "f", "signatures": [{ "params": [{ "name": "x", "type": { "kind": "ref", "name": "T", "module": "@acme/maps/a.js" } }] }] }
            """);
        var result = Generator.Generate(ir, new Profile { Package = "@acme/maps" }, new(Api.Async, "Acme.Maps")); // and the generator doesn't throw
        Assert.Equal(
        [
            $"{code} T: skipped: uses {problem} (in `T`)",
            $"{code} Holder: skipped: uses {problem} (in `T`) (in `Holder`)",
            $"{code} f: skipped: uses {problem} (in `T`)",
        ], result.Diagnostics.Select(d => d.ToString()));
        Assert.DoesNotContain(Selection.Select(ir, new Profile { Package = "@acme/maps" }).Declarations, s => s.Declaration.Name is "T" or "Holder" or "f");
    }

    /// <summary>The counterpart of each refused shape above, which the mapping checks must let through.</summary>
    [Theory]
    [InlineData("""{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "literal", "value": "auto" }, { "kind": "literal", "value": "none" }] } }""")] // R9
    [InlineData("""{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "string" }, { "kind": "array", "element": { "kind": "primitive", "name": "string" } }] } }""")] // R2, R19 step 4
    [InlineData("""{ "kind": "enum", "name": "T", "members": [{ "name": "a", "value": 1 }, { "name": "b", "value": 2 }] }""")] // §2
    [InlineData("""{ "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "literal", "value": 0 }, { "kind": "literal", "value": 1 }] } }""")] // §2
    [InlineData( // R19 step 2
        """
        { "kind": "alias", "name": "T", "type": { "kind": "union", "discriminant": "kind", "types": [{ "kind": "ref", "name": "X", "module": "@acme/maps/a.js" }, { "kind": "ref", "name": "Y", "module": "@acme/maps/a.js" }] } },
        { "kind": "interface", "name": "X", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "x" } }] },
        { "kind": "interface", "name": "Y", "members": [{ "kind": "property", "name": "kind", "type": { "kind": "literal", "value": "y" } }] }
        """)]
    [InlineData( // R19 step 3
        """
        { "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{ "kind": "ref", "name": "X", "module": "@acme/maps/a.js" }, { "kind": "ref", "name": "Y", "module": "@acme/maps/a.js" }] } },
        { "kind": "interface", "name": "X", "members": [{ "kind": "property", "name": "a", "type": { "kind": "primitive", "name": "number" } }] },
        { "kind": "interface", "name": "Y", "members": [{ "kind": "property", "name": "b", "type": { "kind": "primitive", "name": "number" } }] }
        """)]
    [InlineData("""{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "x", "optional": true, "type": { "kind": "union", "types": [{ "kind": "primitive", "name": "string" }, { "kind": "primitive", "name": "null" }] } }] }""")] // R3
    [InlineData("""{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "pair", "type": { "kind": "tuple", "elements": [{ "kind": "primitive", "name": "number" }, { "kind": "primitive", "name": "number" }] } }] }""")] // R5
    [InlineData( // R16: a value-like class in a data record
        """
        { "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "at", "type": { "kind": "ref", "name": "Spot", "module": "@acme/maps/a.js" } }] },
        { "kind": "class", "name": "Spot" }
        """)]
    public void WhatCanBeMappedIsEmittedWithWhatUsesIt(string declaration)
    {
        var result = Generator.Generate(Ir(declaration + """
            ,
            { "kind": "interface", "name": "Holder", "members": [{ "kind": "property", "name": "t", "type": { "kind": "ref", "name": "T", "module": "@acme/maps/a.js" } }] },
            { "kind": "function", "name": "f", "signatures": [{ "params": [{ "name": "x", "type": { "kind": "ref", "name": "T", "module": "@acme/maps/a.js" } }] }] }
            """), new Profile { Package = "@acme/maps", ValueClasses = ["Spot"] }, new(Api.Async, "Acme.Maps"));
        Assert.Empty(result.Diagnostics);
        Assert.Contains("T.cs", result.Files.Select(f => f.Path));
        Assert.Contains("Holder.cs", result.Files.Select(f => f.Path));
        Assert.Contains("public static Task FAsync(JsRealm realm, ", result.Files.Single(f => f.Path == "A.cs").Text);
    }

    [Fact] // R22: a rest parameter is params T[], so one that isn't an array is dropped, as an optional one is
    public void ARestParameterThatIsntAnArrayIsDropped()
    {
        var selection = Selection.Select(Ir("""
            { "kind": "function", "name": "f", "signatures": [{ "params": [
              { "name": "a", "type": { "kind": "primitive", "name": "number" } },
              { "name": "pair", "rest": true, "type": { "kind": "tuple", "elements": [{ "kind": "primitive", "name": "number" }, { "kind": "primitive", "name": "string" }] } }] }] }
            """), new Profile { Package = "@acme/maps" });
        Assert.Equal(["a"], Assert.IsType<FunctionDeclaration>(Assert.Single(selection.Declarations).Declaration).Signatures[0].Params.Select(p => p.Name));
        Assert.Equal("IOP2007 f(pair): rest parameter dropped (R22): uses a type that isn't an array", Assert.Single(selection.Diagnostics).ToString());
    }

    [Fact] // R4, R19 step 2: a closed base is read by a string discriminant
    public void AClosedBaseWithoutADiscriminantIsSkippedWithWhatUsesIt()
    {
        var selection = Selection.Select(Ir("""
            { "kind": "class", "name": "Symbol", "abstract": true },
            { "kind": "class", "name": "Marker", "extends": { "kind": "ref", "name": "Symbol", "module": "@acme/maps/a.js" } },
            { "kind": "function", "name": "f", "signatures": [{ "params": [{ "name": "s", "type": { "kind": "ref", "name": "Symbol", "module": "@acme/maps/a.js" } }] }] }
            """), new Profile { Package = "@acme/maps", ValueClasses = ["Symbol", "Marker"] });
        const string Problem = "the closed base `Symbol`, whose subclasses have no string discriminant (R19)";
        Assert.Equal([$"IOP2007 Symbol: skipped: uses {Problem}", $"IOP2007 f: skipped: uses {Problem}"], selection.Diagnostics.Select(d => d.ToString()));
        Assert.Equal(["Marker"], selection.Declarations.Select(s => s.Declaration.Name));
    }

    [Fact]
    public void AGlobThatMatchesNoModuleIsAnError() =>
        Assert.Equal("Profile include: @acme/demo-maps/views/* matches no module of @acme/demo-maps.",
            Assert.Throws<InvalidDataException>(() => Select("@acme/demo-maps/views/*")).Message);

    [Fact]
    public void TheProfileMustBeForTheIrsPackage() =>
        Assert.Throws<InvalidDataException>(() => Selection.Select(Fixture.Ir, Fixture.Profile with { Package = "@acme/maps" }));

    private static Selection Select(params string[] include) => Selection.Select(Fixture.Ir, Fixture.Profile with { Include = include });

    /// <summary>The selected declarations by module, without the package (<c>measure.js: distance</c>).</summary>
    private static List<string> Modules(Selection selection) =>
        [.. selection.Declarations.GroupBy(s => s.Module).Select(g => $"{g.Key["@acme/demo-maps/".Length..]}: {string.Join(", ", g.Select(s => s.Declaration.Name))}")];

    private static ClassDeclaration Class(Selection selection, string name) =>
        Assert.IsType<ClassDeclaration>(selection.Declarations.Single(s => s.Declaration.Name == name).Declaration);

    private static IEnumerable<string> Members(Selection selection, string name) => Class(selection, name).Members.Select(m => m.Name);

    /// <summary>An IR of <c>@acme/maps</c> with the modules <c>a.js</c> and <c>b.js</c>.</summary>
    private static IrDocument Ir(string a, string b = "") => IrDocument.Parse($$"""
        { "version": 1, "package": { "name": "@acme/maps", "version": "1" }, "modules": [
          { "specifier": "@acme/maps/a.js", "declarations": [{{a}}] },
          { "specifier": "@acme/maps/b.js", "declarations": [{{b}}] }] }
        """);
}
