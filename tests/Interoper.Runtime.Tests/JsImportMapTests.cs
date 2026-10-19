namespace Interoper.Runtime.Tests;

public class JsImportMapTests
{
    [Theory] // design §3.5: the addresses a browser keeps, each with a trailing slash
    [InlineData("/node_modules", "/node_modules/")]
    [InlineData("./_content/Lib/", "./_content/Lib/")]
    [InlineData("../esm", "../esm/")]
    [InlineData("https://cdn.example/npm", "https://cdn.example/npm/")]
    public void A_root_a_browser_keeps_gets_a_trailing_slash(string root, string expected) => Assert.Equal(expected, JsImportMap.Root(root));

    [Theory] // design §3.5: a browser silently drops a bare address, so the registry refuses it
    [InlineData("_content/Lib")]
    [InlineData("node_modules/")]
    [InlineData("")]
    public void A_bare_root_throws(string root) => Assert.Throws<ArgumentException>(() => JsImportMap.Root(root));

    [Fact] // design §3.5: the generated registry checks its root
    public void The_registry_checks_its_root()
    {
        Assert.Equal("/npm/@acme/demo-maps/", GoldenSlice.Async.DemoMaps.ImportMap("/npm")["@acme/demo-maps/"]);
        Assert.Throws<ArgumentException>(() => GoldenSlice.Async.DemoMaps.ImportMap("npm"));
    }
}
