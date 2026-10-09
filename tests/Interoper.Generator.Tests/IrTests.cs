using System.Text.Json;
using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>The IR model (docs/ir.md). Every field of the JSON maps to it, or loading fails.</summary>
public sealed class IrTests
{
    [Fact]
    public void LoadsTheGoldenIr()
    {
        var ir = Fixture.Ir;
        Assert.Equal(new IrPackage("@acme/demo-maps", "0.0.0"), ir.Package);
        Assert.Equal(["MapView", "Marker", "core", "demo-map", "geometry", "measure", "styles"],
            ir.Modules.Select(m => m.Specifier["@acme/demo-maps/".Length..^".js".Length]));

        var mapView = Declaration<ClassDeclaration>("MapView");
        Assert.Equal(new RefType("Emitter", "@acme/demo-maps/core.js"), mapView.Extends);
        Assert.True(Assert.Single(Assert.Single(mapView.Constructors).Params).Optional);
        Assert.Equal(new Event("click", new RefType("ClickEvent", "@acme/demo-maps/MapView.js")), Assert.Single(mapView.Events));
        var flyTo = Assert.IsType<MethodMember>(mapView.Members.Single(m => m.Name == "flyTo"));
        var signature = Assert.Single(flyTo.Signatures);
        Assert.Equal(["target", "options"], signature.Params.Select(p => p.Name));
        Assert.Equal([false, true], signature.Params.Select(p => p.Optional));
        var promise = Assert.IsType<RefType>(signature.Returns);
        Assert.Equal(("Promise", null), (promise.Name, promise.Module));
        Assert.Equal(new PrimitiveType("void"), Assert.Single(promise.Args));
        var screenToMap = Assert.IsType<MethodMember>(mapView.Members.Single(m => m.Name == "screenToMap"));
        Assert.Equal("0.2", screenToMap.Signatures[0].Doc?.Since);
        Assert.Equal("Pass `x` and `y`.", screenToMap.Signatures[1].Doc?.Deprecated);
        Assert.Equal(["MapView.flyTo"], screenToMap.Signatures[1].Doc?.See ?? []);

        var position = Assert.IsType<PropertyMember>(Declaration<ClassDeclaration>("Marker").Members[0]);
        Assert.Equal(new RefType("Point", "@acme/demo-maps/geometry.js"), position.Type);
        Assert.Equal(2, Assert.IsType<UnionType>(position.WriteType).Types.Count);

        var shape = Declaration<ClassDeclaration>("Shape");
        Assert.True(shape.Abstract);
        Assert.True(shape.Members[0] is PropertyMember { Name: "type", Abstract: true, Readonly: true });
        Assert.IsType<ThisType>(Assert.IsType<MethodMember>(shape.Members[2]).Signatures[0].Returns);

        Assert.Equal("type", Assert.IsType<UnionType>(Declaration<AliasDeclaration>("Style").Type).Discriminant);
        var button = Assert.IsType<UnionType>(Declaration<InterfaceDeclaration>("ClickEvent").Members.OfType<PropertyMember>().Single(m => m.Name == "button").Type);
        Assert.Equal([0, 1, 2], button.Types.Select(t => Assert.IsType<LiteralType>(t).Value.GetInt32()));
        Assert.Equal("3d-relief", Assert.IsType<LiteralType>(Assert.IsType<UnionType>(Declaration<AliasDeclaration>("Basemap").Type).Types[3]).Value.GetString());
        Assert.Equal("0", Declaration<InterfaceDeclaration>("FlyToOptions").Members.OfType<PropertyMember>().First().Doc?.Default);
        Assert.IsType<FunctionType>(Declaration<AliasDeclaration>("LabelFormatter").Type);
        var watch = Declaration<ClassDeclaration>("Observable").Members.OfType<MethodMember>().Single().Signatures[0];
        Assert.Equal(new UnresolvedType("keyof this"), Assert.IsType<IntersectionType>(watch.TypeParameters[0].Constraint).Types[0]);

        var element = Assert.Single(ir.Elements);
        Assert.Equal(new RefType("DemoMapElement", "@acme/demo-maps/demo-map.js"), element.Class);
        Assert.Equal(["basemap", "zoom"], element.Attributes.Select(a => a.Property));
        Assert.Equal(["demo-map-click", "demo-map-zoom-change", "demo-map-ready"], element.Events.Select(e => e.Name));
        Assert.Equal(["", "toolbar"], element.Slots.Select(s => s.Name));
    }

    [Fact]
    public void AnUnknownFieldIsAnError()
    {
        Assert.Throws<JsonException>(() => IrDocument.Parse("""{ "version": 1, "package": { "name": "p", "version": "1" }, "extra": 1 }"""));
        Assert.Throws<JsonException>(() => IrDocument.Parse("""{ "version": 1, "package": { "name": "p", "version": "1" }, "modules": [{ "specifier": "p/a.js", "declarations": [{ "kind": "enum", "name": "E", "flags": 1 }] }] }"""));
    }

    [Fact]
    public void OnlyVersionOneIsRead()
    {
        Assert.Equal("IR version 2 isn't supported; expected 1.",
            Assert.Throws<InvalidDataException>(() => IrDocument.Parse("""{ "package": { "name": "p", "version": "1" }, "added": 1, "version": 2 }""")).Message);
        Assert.Throws<InvalidDataException>(() => IrDocument.Parse("""{ "package": { "name": "p", "version": "1" } }"""));
    }

    private static T Declaration<T>(string name) where T : Declaration =>
        Assert.IsType<T>(Fixture.Ir.Modules.SelectMany(m => m.Declarations).Single(d => d.Name == name));
}
