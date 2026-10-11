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
            "MapView.js: Basemap, Cursor, CenterAndZoom, Camera, FlyToTarget, FlyToOptions, ClickEvent, MapViewEvents, MapViewOptions, MapView", // R15: FlyToOptions without its signal
            "Marker.js: MarkerOptions, Marker", // R15: without its callback
            "core.js: Observable, Emitter, CollectionChangeEvent, CollectionEvents, Collection",
            "demo-map.js: DemoMapClickDetail, DemoMapElement, DemoMapZoomChangeDetail", // R28: not the event map; a record for an inline detail
            "geometry.js: ShapeType, Bounds, Shape, Point, Line, Polygon",
            "layers/Layer.js: Layer",
            "layers/TileLayer.js: TileLayer",
            "measure.js: distance, perimeter, Measurable, describe",
            "ops/a.js: Options, execute",
            "ops/b.js: Options, execute",
            "pins.js: PinDragEvent, PinEvents, Pin",
            "styles.js: Rgba, ColorInput, IconName, FillStyle, StrokeStyle, IconStyle, Style, toRgba",
        ], Modules(selection));

        Assert.Equal(["center", "zoom", "rotation", "basemap", "cursor", "title", "container", "selected", "markers", "layers", "screenToMap", "flyTo", "markerAt"],
            Members(selection, "MapView")); // R13: the DOM container too
        Assert.Equal(["position", "label", "style", "labelFormatter", "displayLabel"], Members(selection, "Marker")); // R21: no LabelFormatter type
        Assert.Equal(["length", "at", "add", "remove", "find", "forEach", "toArray"], Members(selection, "Collection")); // §2: a generic live class
        Assert.Equal(["change"], Class(selection, "Collection").Events.Select(e => e.Name));
        Assert.Equal(["click"], Class(selection, "MapView").Events.Select(e => e.Name));
        Assert.Equal(["bounds", "clone"], Members(selection, "Shape")); // not the discriminant `type`
        Assert.Equal(["type", "x", "y"], Members(selection, "Point")); // a constant (R20)
        Assert.Empty(Members(selection, "Observable")); // the runtime-class property and the watch method are never members
        Assert.Empty(Members(selection, "Emitter")); // nor is the event method; `emit` has a type parameter

        // R14 isn't supported yet, so FlyToOptions drops its optional `signal` (R15), and flyTo keeps its `options`.
        var flyTo = Assert.IsType<MethodMember>(Class(selection, "MapView").Members.Single(m => m.Name == "flyTo"));
        Assert.Equal(["target", "options"], Assert.Single(flyTo.Signatures).Params.Select(p => p.Name));
        var flyToOptions = Assert.IsType<InterfaceDeclaration>(selection.Declarations.Single(s => s.Declaration.Name == "FlyToOptions").Declaration);
        Assert.Equal(["duration"], flyToOptions.Members.Select(m => m.Name));
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
        Assert.Equal(["core.js: Observable", "geometry.js: ShapeType, Bounds, Shape, Point, Line, Polygon", "measure.js: distance, perimeter, Measurable, describe"],
            Modules(Select("*/measure.js", "*/geometry.js")));
        Assert.Equal(["core.js: Observable", "layers/Layer.js: Layer", "layers/TileLayer.js: TileLayer"], Modules(Select("@acme/demo-maps/layers/*")));
    }

    [Fact]
    public void AMemberThatUsesAClassOutsideTheSelectionIsSkipped()
    {
        var selection = Select("*/MapView.js");
        // R2: flyTo only writes its FlyToTarget, which drops its Shape case; the generator reports that.
        Assert.Equal(["zoom", "rotation", "basemap", "cursor", "title", "container", "flyTo"], Members(selection, "MapView"));
        Assert.Equal(
        [
            "IOP2007 FlyToOptions.signal: skipped: uses `AbortSignal`", // R15: an optional member is dropped, a required one skips its record
            "IOP2007 ClickEvent: skipped: uses class `Point` outside the selection (in `ClickEvent`)",
            "IOP2007 MapViewEvents: skipped: uses class `Point` outside the selection (in `ClickEvent`) (in `MapViewEvents`)",
            "IOP2007 MapViewOptions.center: skipped: uses class `Point` outside the selection",
            "IOP2007 MapView.center: skipped: uses class `Point` outside the selection",
            "IOP2007 MapView.selected: skipped: uses class `Marker` outside the selection",
            "IOP2007 MapView.markers: skipped: uses class `Collection` outside the selection",
            "IOP2007 MapView.layers: skipped: uses class `Collection` outside the selection",
            "IOP2007 MapView.screenToMap: skipped: uses class `Point` outside the selection", // once for its two overloads
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
    [InlineData( // R21: a callback is a parameter's, never a data record's
        """{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "f", "type": { "kind": "function", "params": [], "returns": { "kind": "primitive", "name": "void" } } }] }""",
        "IOP2007", "a callback that isn't a parameter (R21)")]
    [InlineData( // R21: JsScope.Callback reads up to four arguments
        """
        { "kind": "alias", "name": "T", "type": { "kind": "function", "params": [
          { "name": "a", "type": { "kind": "primitive", "name": "number" } }, { "name": "b", "type": { "kind": "primitive", "name": "number" } },
          { "name": "c", "type": { "kind": "primitive", "name": "number" } }, { "name": "d", "type": { "kind": "primitive", "name": "number" } },
          { "name": "e", "type": { "kind": "primitive", "name": "number" } }], "returns": { "kind": "primitive", "name": "void" } } }
        """,
        "IOP2007", "a callback with more than 4 parameters (R21)")]
    [InlineData( // §2: a generic record's type parameters are entities
        """{ "kind": "interface", "name": "T", "typeParameters": [{ "name": "U", "constraint": { "kind": "primitive", "name": "string" } }], "members": [{ "kind": "property", "name": "u", "type": { "kind": "typeParameter", "name": "U" } }] }""",
        "IOP2007", "the type parameter `U` with a constraint (§2)")]
    [InlineData( // §2: so are its type arguments
        """
        { "kind": "alias", "name": "T", "type": { "kind": "ref", "name": "Box", "module": "@acme/maps/a.js", "args": [{ "kind": "primitive", "name": "number" }] } },
        { "kind": "interface", "name": "Box", "typeParameters": [{ "name": "U" }], "members": [{ "kind": "property", "name": "items", "type": { "kind": "array", "element": { "kind": "typeParameter", "name": "U" } } }] }
        """,
        "IOP2007", "`Box` of a type that isn't an entity (§2)")]
    [InlineData( // R11: an inline record, like a named one, has no index signature
        """{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "size", "type": { "kind": "object", "indexSignatures": [{ "key": { "kind": "primitive", "name": "string" }, "type": { "kind": "primitive", "name": "number" } }] } }] }""",
        "IOP2007", "an index signature")]
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
    [InlineData( // R17: an entity in a data record, which is then read with a scope
        """
        { "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "layer", "type": { "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" } }] },
        { "kind": "class", "name": "Layer" }
        """)]
    [InlineData( // §2: a generic record of entities
        """
        { "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "box", "type": { "kind": "ref", "name": "Box", "module": "@acme/maps/a.js", "args": [{ "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }] } }] },
        { "kind": "interface", "name": "Box", "typeParameters": [{ "name": "U" }], "members": [{ "kind": "property", "name": "items", "type": { "kind": "array", "element": { "kind": "typeParameter", "name": "U" } } }] },
        { "kind": "class", "name": "Layer" }
        """)]
    [InlineData( // §2: a type parameter's default doesn't matter, since C# always has the type argument
        """
        { "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "box", "type": { "kind": "ref", "name": "Box", "module": "@acme/maps/a.js", "args": [{ "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }] } }] },
        { "kind": "interface", "name": "Box", "typeParameters": [{ "name": "U", "default": { "kind": "primitive", "name": "any" } }], "members": [{ "kind": "property", "name": "items", "type": { "kind": "array", "element": { "kind": "typeParameter", "name": "U" } } }] },
        { "kind": "class", "name": "Layer" }
        """)]
    [InlineData("""{ "kind": "interface", "name": "T", "members": [{ "kind": "property", "name": "size", "type": { "kind": "object", "members": [{ "kind": "property", "name": "w", "type": { "kind": "primitive", "name": "number" } }] } }] }""")] // R11
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
        Assert.Contains("public static global::System.Threading.Tasks.Task FAsync(global::Interoper.JsRealm realm, ", result.Files.Single(f => f.Path == "A.cs").Text);
    }

    /// <summary>
    /// R2, R17: a union with a case that needs a scope to be read (an entity, or a record that holds one) can still be
    /// written, so a parameter takes it; what reads it, such as a record holding it, is skipped.
    /// </summary>
    [Theory]
    [InlineData("""{ "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" }""", "Layer")]
    [InlineData("""{ "kind": "ref", "name": "X", "module": "@acme/maps/a.js" }""", "X")]
    public void AUnionWithAnEntityCaseIsOnlyWritten(string entityCase, string name)
    {
        var result = Generator.Generate(Ir($$"""
            { "kind": "alias", "name": "T", "type": { "kind": "union", "types": [{{entityCase}}, { "kind": "primitive", "name": "string" }] } },
            { "kind": "interface", "name": "X", "members": [{ "kind": "property", "name": "layer", "type": { "kind": "ref", "name": "Layer", "module": "@acme/maps/a.js" } }] },
            { "kind": "class", "name": "Layer" },
            { "kind": "interface", "name": "Holder", "members": [{ "kind": "property", "name": "t", "type": { "kind": "ref", "name": "T", "module": "@acme/maps/a.js" } }] },
            { "kind": "function", "name": "f", "signatures": [{ "params": [{ "name": "x", "type": { "kind": "ref", "name": "T", "module": "@acme/maps/a.js" } }] }] }
            """), new Profile { Package = "@acme/maps" }, new(Api.Sync, "Acme.Maps"));
        Assert.Equal($"IOP2007 Holder: skipped: uses the union `T` with the case {name}, which needs a scope to be read (R17, R19) (in `Holder`)",
            Assert.Single(result.Diagnostics).ToString());
        Assert.DoesNotContain("Holder.cs", result.Files.Select(f => f.Path));
        Assert.Contains("public static void F(global::Interoper.JsRealm realm, T x) =>", result.Files.Single(f => f.Path == "A.cs").Text);
        Assert.Contains("public static T ReadValue(ref global::System.Text.Json.Utf8JsonReader reader) => throw new global::System.Text.Json.JsonException(\"T is only written (R2)\");",
            result.Files.Single(f => f.Path == "T.cs").Text);
    }

    [Fact] // §2, R2: a union struct isn't generic, so even a parameter's union has no case of a type parameter
    public void AUnionWithATypeParameterCaseIsSkipped()
    {
        var result = Generator.Generate(Ir("""
            { "kind": "class", "name": "Bag", "typeParameters": [{ "name": "T" }], "members": [
              { "kind": "method", "name": "add", "signatures": [{ "params": [{ "name": "item", "type": { "kind": "union", "types": [
                { "kind": "typeParameter", "name": "T" }, { "kind": "primitive", "name": "string" }] } }], "returns": { "kind": "primitive", "name": "void" } }] }] }
            """), new Profile { Package = "@acme/maps", Collections = ["Bag"] }, new(Api.Sync, "Acme.Maps"));
        Assert.Equal("IOP2007 Bag.add: skipped: uses a union with a case of the type parameter `T` (§2)", Assert.Single(result.Diagnostics).ToString());
        Assert.DoesNotContain("Add(", result.Files.Single(f => f.Path == "Bag.cs").Text);
    }

    [Fact] // R2, R19: a union drops a case outside the selection, reads the others and fails on it; one that can't tell the others apart is only written
    public void AUnionDropsACaseOutsideTheSelectionAndFailsOnlyOnIt()
    {
        const string Layer = """{ "kind": "ref", "name": "Layer", "module": "@acme/maps/b.js" }""";
        static string Ref(string name) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js" }""";
        static string Box(string name) => $$"""{ "kind": "interface", "name": "{{name}}", "members": [{ "kind": "property", "name": "w", "type": { "kind": "primitive", "name": "number" } }] }""";
        var ir = Ir($$"""
            { "kind": "alias", "name": "Target", "type": { "kind": "union", "types": [
              {{Layer}}, { "kind": "primitive", "name": "string" }, { "kind": "array", "element": { "kind": "primitive", "name": "number" } }, {{Ref("Box")}}] } },
            {{Box("Box")}}, {{Box("Disc")}},
            { "kind": "interface", "name": "Fill", "members": [{ "kind": "property", "name": "type", "type": { "kind": "literal", "value": "fill" } }] },
            { "kind": "interface", "name": "Stroke", "members": [{ "kind": "property", "name": "type", "type": { "kind": "literal", "value": "stroke" } }] },
            { "kind": "alias", "name": "Paint", "type": { "kind": "union", "discriminant": "type", "types": [{{Layer}}, {{Ref("Fill")}}, {{Ref("Stroke")}}] } },
            { "kind": "alias", "name": "Spot", "type": { "kind": "union", "types": [{{Layer}}, {{Ref("Box")}}, {{Ref("Disc")}}] } },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "target", "type": {{Ref("Target")}} },
              { "kind": "property", "name": "spot", "readonly": true, "type": {{Ref("Spot")}} },
              { "kind": "property", "name": "paint", "readonly": true, "type": {{Ref("Paint")}} },
              { "kind": "method", "name": "goTo", "signatures": [{ "params": [{ "name": "target", "type": {{Ref("Target")}} }, { "name": "spot", "type": {{Ref("Spot")}} }],
                "returns": { "kind": "primitive", "name": "void" } }] }] }
            """, """{ "kind": "class", "name": "Layer" }""");
        var result = Generator.Generate(ir, new Profile { Package = "@acme/maps", Include = ["@acme/maps/a.js"] }, new(Api.Sync, "Acme.Maps"));
        Assert.Equal(
        [
            "IOP2003 View.spot: skipped: uses the ambiguous union `Spot`: Box has no required member the other cases lack (R19)",
            "IOP2007 Target: case `Layer` dropped: a class outside the selection (R2)",
            "IOP2007 Paint: case `Layer` dropped: a class outside the selection (R2)",
            "IOP2007 Spot: case `Layer` dropped: a class outside the selection (R2)",
        ], result.Diagnostics.Select(d => d.ToString()));
        var view = result.Files.Single(f => f.Path == "View.cs").Text;
        Assert.Contains("public Target Target", view); // read, and written
        Assert.Contains("public void GoTo(Target target, Spot spot) =>", view);
        // Target reads its other cases, and fails on a handle, which is how JS sends a Layer.
        var target = result.Files.Single(f => f.Path == "Target.cs").Text;
        Assert.Contains("private readonly byte _tag; // 0 = undefined, 1 = null, 2 = string, 3 = double[], 4 = Box", target);
        Assert.Contains("""global::System.Text.Json.JsonTokenType.StartObject when global::Interoper.JsJson.HasProperty(reader, "$h"u8) => throw new global::System.Text.Json.JsonException("Target: a case outside the selection (R2)"),""", target);
        Assert.Contains("global::System.Text.Json.JsonTokenType.String => global::Interoper.JsJson.ReadString(ref reader),", target);
        // Paint is read by its discriminant, which a handle doesn't have.
        Assert.Contains("""null when global::Interoper.JsJson.HasProperty(reader, "$h"u8) => throw new global::System.Text.Json.JsonException("Paint: a case outside the selection (R2)"),""",
            result.Files.Single(f => f.Path == "Paint.cs").Text);
        // Spot can't tell Box from Disc, so it's only written, as a parameter's.
        Assert.Contains("throw new global::System.Text.Json.JsonException(\"Spot is only written (R2)\")", result.Files.Single(f => f.Path == "Spot.cs").Text);
    }

    [Fact] // R2, R6, R17: a union with one case left in the selection is that case; with none, what uses it is skipped
    public void AUnionWithOneCaseLeftIsThatCaseAndOneWithNoneIsSkipped()
    {
        static string A(string name) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/a.js" }""";
        static string B(string name) => $$"""{ "kind": "ref", "name": "{{name}}", "module": "@acme/maps/b.js" }""";
        static string OrNull(string type) => $$"""{ "kind": "union", "types": [{{type}}, { "kind": "primitive", "name": "null" }] }""";
        var ir = Ir($$"""
            { "kind": "class", "name": "Layer" },
            { "kind": "class", "name": "Sheet" },
            { "kind": "alias", "name": "Glyph", "type": { "kind": "union", "types": [{{B("FlatGlyph")}}, {{B("DeepGlyph")}}, {{B("NamedGlyph")}}] } },
            { "kind": "alias", "name": "Owner", "type": { "kind": "union", "types": [{{A("Layer")}}, {{B("TileLayer")}}, {{B("Folder")}}] } },
            { "kind": "interface", "name": "Holder", "members": [{ "kind": "property", "name": "layer", "optional": true, "type": {{OrNull(A("Owner"))}} }] },
            { "kind": "alias", "name": "LayerProps", "type": { "kind": "object", "members": [
              { "kind": "property", "name": "owner", "optional": true, "type": { "kind": "union", "types": [{{A("Sheet")}}, {{B("Basemap")}}, {{B("Folder")}}, { "kind": "primitive", "name": "null" }] } }] } },
            { "kind": "class", "name": "View", "members": [
              { "kind": "property", "name": "glyph", "readonly": true, "type": {{A("Glyph")}} },
              { "kind": "property", "name": "owner", "type": {{OrNull(A("Owner"))}} },
              { "kind": "property", "name": "props", "type": {{A("LayerProps")}} },
              { "kind": "property", "name": "holder", "type": {{A("Holder")}} }] }
            """, $$"""
            { "kind": "class", "name": "Dot" }, { "kind": "class", "name": "Star" }, { "kind": "class", "name": "Ring" }, { "kind": "class", "name": "Cross" },
            { "kind": "class", "name": "TileLayer" }, { "kind": "class", "name": "Folder" }, { "kind": "class", "name": "Basemap" },
            { "kind": "alias", "name": "FlatGlyph", "type": { "kind": "union", "types": [{{B("Dot")}}, {{B("Star")}}] } },
            { "kind": "alias", "name": "DeepGlyph", "type": {{B("Cross")}} },
            { "kind": "alias", "name": "NamedGlyph", "type": { "kind": "union", "types": [{{B("Ring")}}, {{B("DeepGlyph")}}] } }
            """);
        foreach (var api in new[] { Api.Sync, Api.Async }) // and the generator doesn't throw
        {
            var result = Generator.Generate(ir, new Profile { Package = "@acme/maps", Include = ["@acme/maps/a.js"] }, new(api, "Acme.Maps"));
            const string None = "the union `Glyph`, whose cases are all outside the selection (in `Glyph`)";
            Assert.Equal(
            [
                $"IOP2007 Glyph: skipped: uses {None}",
                $"IOP2007 View.glyph: skipped: uses {None}",
                "IOP2007 Owner: case `TileLayer` dropped: a class outside the selection (R2)",
                "IOP2007 Owner: case `Folder` dropped: a class outside the selection (R2)",
                "IOP2007 LayerPropsOwner: case `Basemap` dropped: a class outside the selection (R2)",
                "IOP2007 LayerPropsOwner: case `Folder` dropped: a class outside the selection (R2)",
            ], result.Diagnostics.Select(d => d.ToString()));
            // Owner is Layer, and LayerProps.owner is Sheet: entities, so the records that hold them are read with a scope (R17).
            Assert.Contains("public global::Interoper.Opt<Layer> Layer { get; init; }", File(result, "Holder.cs"));
            Assert.Contains("public static Holder Read(ref global::System.Text.Json.Utf8JsonReader reader, global::Interoper.JsScope scope)", File(result, "Holder.cs"));
            Assert.Contains("public global::Interoper.Opt<Sheet> Owner { get; init; }", File(result, "LayerProps.cs"));
            Assert.Contains(api == Api.Sync ? "public global::Interoper.Opt<Layer> GetOwner(global::Interoper.JsScope scope) =>"
                : "public global::System.Threading.Tasks.Task<global::Interoper.Opt<Layer>> GetOwnerAsync(global::Interoper.JsScope scope) =>", File(result, "View.cs"));
            Assert.DoesNotContain(result.Files, f => f.Path is "Owner.cs" or "Glyph.cs");
        }
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

    private static string File(GeneratorResult result, string path) => result.Files.Single(f => f.Path == path).Text;

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
