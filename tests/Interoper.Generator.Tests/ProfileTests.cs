using System.Text.Json;

namespace Interoper.Generator.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void LoadsTheFixtureProfile()
    {
        var profile = Fixture.Profile;
        Assert.Equal("@acme/demo-maps", profile.Package);
        Assert.Equal("className", profile.RuntimeClassProperty);
        Assert.Equal(["Shape", "Point", "Line", "Polygon"], profile.ValueClasses);
        Assert.Equal(new ProfileEvents("$events", "on"), profile.Events);
        Assert.Equal("watch", profile.Watch);
        Assert.Equal(["Collection"], profile.Collections);
        Assert.Null(profile.Include); // every module
    }

    [Fact]
    public void OnlyThePackageIsRequired()
    {
        var profile = Profile.Parse("""{ "package": "@acme/maps" }""");
        Assert.Null(profile.Include);
        Assert.Empty(profile.ValueClasses);
        Assert.Equal("", profile.RuntimeClassPrefix);
        Assert.Equal("acme.", Profile.Parse("""{ "package": "@acme/maps", "runtimeClassPrefix": "acme." }""").RuntimeClassPrefix);
        Assert.Throws<JsonException>(() => Profile.Parse("{}"));
    }

    [Fact]
    public void AnUnknownFieldIsAnError()
    {
        Assert.Throws<JsonException>(() => Profile.Parse("""{ "package": "@acme/maps", "valueClass": ["Point"] }"""));
        Assert.Throws<JsonException>(() => Profile.Parse("""{ "package": "@acme/maps", "events": { "map": "$events" } }"""));
    }
}
