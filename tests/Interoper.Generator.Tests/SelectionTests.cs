using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>What the profile's <c>include</c> selects from the golden IR.</summary>
public sealed class SelectionTests
{
    [Fact]
    public void TheGoldenSliceIsTheProfilesInclude()
    {
        var selection = Selection.Select(Fixture.Ir, Fixture.Profile);

        Assert.Equal(
        [
            "MapView.js: Basemap, CenterAndZoom, FlyToTarget, ClickEvent, MapView",
            "Marker.js: Marker",
            "geometry.js: Bounds, Shape, Point, Line, Polygon",
            "measure.js: distance",
            "styles.js: Rgba, ColorInput, IconName, FillStyle, StrokeStyle, IconStyle, Style",
        ], selection.Declarations.GroupBy(s => s.Module).Select(g => $"{g.Key["@acme/demo-maps/".Length..]}: {string.Join(", ", g.Select(s => s.Declaration.Name))}"));

        Assert.Equal(["center", "zoom", "rotation", "basemap", "title", "selected", "flyTo"], Members("MapView"));
        Assert.Equal(["click"], Class("MapView").Events.Select(e => e.Name));
        Assert.Equal(["zoom"], Selected("MapView").Watches.Select(p => p.Name));
        Assert.Equal(["position", "label", "style", "displayLabel"], Members("Marker"));
        Assert.Empty(Class("Marker").Events);
        Assert.Equal(["bounds", "clone"], Members("Shape")); // not the discriminant `type`, nor the runtime-class property
        Assert.Equal(["type", "x", "y"], Members("Point")); // a constant (R20)

        // R14 isn't supported yet, so flyTo loses its optional `options` (R22), and FlyToOptions isn't used.
        var flyTo = Assert.IsType<MethodMember>(Class("MapView").Members.Single(m => m.Name == "flyTo"));
        Assert.Equal(["target"], Assert.Single(flyTo.Signatures).Params.Select(p => p.Name));
        Assert.Equal("IOP2007 MapView.flyTo(options): optional parameter dropped (R22): uses `AbortSignal` (in `FlyToOptions`)",
            Assert.Single(selection.Diagnostics).ToString());

        Selected Selected(string name) => selection.Declarations.Single(s => s.Declaration.Name == name);
        ClassDeclaration Class(string name) => Assert.IsType<ClassDeclaration>(Selected(name).Declaration);
        IEnumerable<string> Members(string name) => Class(name).Members.Select(m => m.Name);
    }

    [Fact] // R6
    public void AUnionAliasInsideAUnionIsFlattened()
    {
        var names = Select("MapView.flyTo", "Shape").Declarations.Select(s => s.Declaration.Name);
        Assert.Contains("Bounds", names);
        Assert.DoesNotContain("Camera", names);
    }

    [Fact]
    public void AMemberThatUsesAnUnlistedClassIsSkipped()
    {
        var selection = Select("MapView.markerAt", "MapView.screenToMap", "MapView.flyTo");
        Assert.Empty(Assert.IsType<ClassDeclaration>(Assert.Single(selection.Declarations).Declaration).Members);
        Assert.Equal(
        [
            "IOP2007 MapView.screenToMap: skipped: uses unlisted class `Point`", // once for its two overloads
            "IOP2007 MapView.flyTo: skipped: uses unlisted class `Shape` (in `FlyToTarget`)",
            "IOP2007 MapView.markerAt: skipped: uses unlisted class `Marker`",
        ], selection.Diagnostics.Select(d => d.ToString()));
    }

    [Fact]
    public void WithoutIncludeEverythingIsConsidered()
    {
        var selection = Selection.Select(Fixture.Ir, Fixture.Profile with { Include = null });
        var names = selection.Declarations.Select(s => s.Declaration.Name).ToList();
        Assert.Contains("Cursor", names);
        Assert.Contains("Observable", names);
        Assert.DoesNotContain("Collection", names);
        Assert.Contains("IOP2007 Collection: skipped: uses generic `Collection`", selection.Diagnostics.Select(d => d.ToString()));
        Assert.Contains("IOP2007 iconNames: skipped: uses a variable", selection.Diagnostics.Select(d => d.ToString()));

        // The runtime-class property and the event and watch methods are never members.
        var observable = Assert.IsType<ClassDeclaration>(selection.Declarations.Single(s => s.Declaration.Name == "Observable").Declaration);
        Assert.Empty(observable.Members);
    }

    [Fact]
    public void ARecursiveDataTypeIsSkippedForWhatItUses()
    {
        // g checks A, which reaches B, which reaches A again before A's AbortSignal is seen: B must not pass for fine.
        var ir = IrDocument.Parse("""
            { "version": 1, "package": { "name": "p", "version": "1" }, "modules": [{ "specifier": "p/a.js", "declarations": [
              { "kind": "interface", "name": "A", "members": [
                { "kind": "property", "name": "b", "type": { "kind": "ref", "name": "B", "module": "p/a.js" } },
                { "kind": "property", "name": "signal", "type": { "kind": "ref", "name": "AbortSignal" } }] },
              { "kind": "interface", "name": "B", "members": [
                { "kind": "property", "name": "a", "type": { "kind": "ref", "name": "A", "module": "p/a.js" } }] },
              { "kind": "function", "name": "g", "signatures": [{ "params": [{ "name": "a", "type": { "kind": "ref", "name": "A", "module": "p/a.js" } }] }] },
              { "kind": "function", "name": "f", "signatures": [{ "params": [{ "name": "b", "type": { "kind": "ref", "name": "B", "module": "p/a.js" } }] }] }] }] }
            """);
        var selection = Selection.Select(ir, new Profile { Package = "p", Include = ["g", "f"] });
        Assert.Empty(selection.Declarations);
        Assert.Equal(["g", "f"], selection.Diagnostics.Select(d => d.Subject));
    }

    [Theory]
    [InlineData("Missing", "Profile include: Missing isn't a declaration of @acme/demo-maps.")]
    [InlineData("MapView.missing", "Profile include: MapView has no missing.")]
    [InlineData("MapView.on:missing", "Profile include: MapView has no on:missing.")]
    [InlineData("MapView.watch:flyTo", "Profile include: MapView has no watch:flyTo.")]
    [InlineData("Bounds.minX", "Profile include: Bounds isn't a class, so it's included whole.")]
    public void AnUnknownIncludeIsAnError(string include, string message) =>
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => Select(include)).Message);

    [Fact]
    public void TheProfileMustBeForTheIrsPackage() =>
        Assert.Throws<InvalidDataException>(() => Selection.Select(Fixture.Ir, Fixture.Profile with { Package = "@acme/maps" }));

    private static Selection Select(params string[] include) => Selection.Select(Fixture.Ir, Fixture.Profile with { Include = include });
}
