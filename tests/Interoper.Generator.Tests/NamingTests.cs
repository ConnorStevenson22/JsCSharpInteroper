namespace Interoper.Generator.Tests;

public sealed class NamingTests
{
    [Theory] // R25
    [InlineData("simple-marker", "SimpleMarker")]
    [InlineData("plain", "Plain")]
    [InlineData("3d-relief", "_3dRelief")]
    [InlineData("pin-120", "Pin120")]
    [InlineData("0", "_0")]
    [InlineData("", "Empty")]
    [InlineData("<", "LessThan")]
    [InlineData("<=", "LessThanOrEqual")]
    [InlineData(">", "GreaterThan")]
    [InlineData(">=", "GreaterThanOrEqual")]
    [InlineData("=", "Equal")]
    [InlineData("==", "Equal")]
    [InlineData("!=", "NotEqual")]
    [InlineData("+", "Plus")]
    [InlineData("-", "Minus")]
    [InlineData("*", "Star")]
    [InlineData("/", "Slash")]
    [InlineData("%", "Percent")]
    [InlineData("--", "MinusMinus")]
    [InlineData("<>", "LessThanGreaterThan")]
    [InlineData("&", "U0026")]
    public void LiteralsBecomeEnumMemberNames(string value, string name) =>
        Assert.Equal(name, Assert.Single(Naming.EnumMembers([value], "Basemap", [])));

    [Fact] // R25
    public void SymbolsAreSpelledAndOnlyTheEmptyStringIsEmpty() =>
        Assert.Equal(["LessThan", "LessThanOrEqual", "Equal", "Equal2", "Empty"], Naming.EnumMembers(["<", "<=", "=", "==", ""], "Operator", []));

    [Fact] // R25
    public void DuplicateEnumMemberNamesGetANumericSuffix()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Equal(["SimpleMarker", "SimpleMarker2", "SimpleMarker3", "Plain"],
            Naming.EnumMembers(["simple-marker", "simple_marker", "simpleMarker", "plain"], "Symbol", diagnostics));
        Assert.Equal(["IOP2006 Symbol: \"simple_marker\" is SimpleMarker2: SimpleMarker is taken (R25)", "IOP2006 Symbol: \"simpleMarker\" is SimpleMarker3: SimpleMarker is taken (R25)"],
            diagnostics.Select(d => d.ToString()));
    }

    [Theory] // R26
    [InlineData("displayLabel", "DisplayLabel")]
    [InlineData("zoom", "Zoom")]
    [InlineData("demo-map-click", "DemoMapClick")]
    [InlineData("$events", "Events")]
    public void MembersArePascalCased(string js, string name) => Assert.Equal(name, Naming.Pascal(js));

    [Fact] // R26
    public void AMemberThatCollidesIsRenamed()
    {
        var diagnostics = new List<Diagnostic>();
        HashSet<string> taken = ["Map", "GetZoomAsync", "SetZoomAsync", "SnapshotAsync"];
        Assert.Equal("FlyToAsync", Naming.Member(taken, "FlyToAsync", "Map.flyTo", diagnostics));
        Assert.Equal("JsGetZoomAsync", Naming.Member(taken, "GetZoomAsync", "Map.getZoom", diagnostics));
        Assert.Equal("JsToString", Naming.Member(taken, "ToString", "Map.toString", diagnostics));
        Assert.Equal("JsEquals", Naming.Member(taken, "Equals", "Map.equals", diagnostics));
        Assert.Equal("JsMap", Naming.Member(taken, "Map", "Map.map", diagnostics));
        Assert.Equal("JsMap2", Naming.Member(taken, "Map", "Map.Map", diagnostics));
        Assert.Equal(["Map.getZoom", "Map.toString", "Map.equals", "Map.map", "Map.Map"], diagnostics.Select(d => d.Subject));
        Assert.All(diagnostics, d => Assert.Equal(Diagnostic.Renamed, d.Code));
    }

    [Fact] // R27
    public void TypesKeepTheirNameInPascalCase()
    {
        Assert.Equal("MapView", Naming.Type("MapView"));
        Assert.Equal("Rgba", Naming.Type("rgba"));
        Assert.Equal("DemoMaps", Naming.Type("demo-maps"));
    }

    [Fact] // R27
    public void AnonymousTypesAreNamedAfterTheirOwner()
    {
        Assert.Equal("ClickEventButton", Naming.OwnedType("ClickEvent", "button"));
        Assert.Equal("MarkerPositionInput", Naming.OwnedType("Marker", "position", input: true));
    }

    [Theory] // R27
    [InlineData("@acme/maps/MapView.js", "Acme.Maps")]
    [InlineData("@acme/maps/layers/TileLayer.js", "Acme.Maps.Layers")]
    [InlineData("@acme/maps/layers/support/tile-info.js", "Acme.Maps.Layers.Support")]
    [InlineData("@acme/maps/3d/scene/Scene.js", "Acme.Maps._3d.Scene")]
    public void ANamespaceIsTheRootPlusTheModulesFolders(string specifier, string ns) =>
        Assert.Equal(ns, Naming.Namespace("Acme.Maps", "@acme/maps", specifier));

    [Theory] // R27: another package's modules have its name, without the scope, first
    [InlineData("@acme/widgets", "Acme.Maps.Widgets")]
    [InlineData("@acme/widgets/legend", "Acme.Maps.Widgets")]
    [InlineData("@acme/widgets/dist/support/entry.js", "Acme.Maps.Widgets.Dist.Support")]
    public void AModuleOfAnotherPackageHasItsNamespace(string specifier, string ns) =>
        Assert.Equal(ns, Naming.Namespace("Acme.Maps", "@acme/widgets", specifier, otherPackage: true));

    [Theory] // R27
    [InlineData("Acme.Maps", "Point", "Point.cs")]
    [InlineData("Acme.Maps.Layers", "TileLayer", "Layers/TileLayer.cs")]
    [InlineData("Acme.Maps.Layers.Parts", "Grid", "Layers/Parts/Grid.cs")]
    public void AFilePathMirrorsTheNamespace(string ns, string type, string path) =>
        Assert.Equal(path, Naming.FilePath("Acme.Maps", ns, type));
}
