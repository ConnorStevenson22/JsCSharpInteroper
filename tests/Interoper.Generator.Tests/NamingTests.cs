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
    [InlineData("--", "Empty")]
    public void LiteralsBecomeEnumMemberNames(string value, string name) =>
        Assert.Equal(name, Assert.Single(Naming.EnumMembers([value], "Basemap", [])));

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
}
