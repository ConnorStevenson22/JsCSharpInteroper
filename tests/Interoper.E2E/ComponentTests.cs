using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Interoper.E2E;

/// <summary>
/// The generated components in the browser (R28): static SSR renders the supplied attributes and the slots; once
/// interactive, Element is live, Center was set after the first render, a click on the element's shadow surface
/// raises OnDemoMapClick with its detail, and @bind-Zoom works both ways.
/// </summary>
public sealed class ComponentTests(BrowserHost host)
{
    [Fact]
    public async Task StaticRenderingRendersTheAttributesAndTheSlots()
    {
        using var http = new HttpClient { BaseAddress = host.BaseUrl };
        var html = await http.GetStringAsync("components-static", TestContext.Current.CancellationToken);
        Assert.Contains("""<demo-map id="map" basemap="relief" zoom="2.5"><div slot="toolbar" style="display:contents"><b>tools</b></div><i>overlay</i></demo-map>""",
            html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("components-server")] // async mode, JsRuntime
    [InlineData("components-wasm")] // async mode, JsImport
    [InlineData("components-wasm-sync")] // sync mode
    public async Task TheComponentDrivesItsElement(string path)
    {
        await using var browser = await host.OpenAsync(path);
        var page = browser.Page;
        Assert.Equal("PASS view zoom 3, basemap Night, center 2,3", await browser.ResultAsync());

        // Through the shadow DOM's surface: the element raises demo-map-click, which reaches .NET once.
        var surface = page.Locator("demo-map #surface");
        var box = (await surface.BoundingBoxAsync())!;
        await surface.ClickAsync(new() { Position = new() { X = box.Width / 2 + 10, Y = box.Height / 2 - 5 } });
        await Expect(page.Locator("#state")).ToHaveTextAsync("zoom 3; clicks 10,-5; view ");

        // @bind-Zoom from .NET (a button in the toolbar slot) to the element, then from the element to .NET.
        await page.ClickAsync("#zoom-in");
        await Expect(page.Locator("demo-map")).ToHaveAttributeAsync("zoom", "4");
        await page.WaitForFunctionAsync("document.querySelector('demo-map').view.zoom === 4");
        await page.EvaluateAsync("document.querySelector('demo-map').zoom = 7");
        await Expect(page.Locator("#state")).ToHaveTextAsync("zoom 7; clicks 10,-5; view ");

        // Element.View is the live view.
        await page.ClickAsync("#read-view");
        await Expect(page.Locator("#state")).ToHaveTextAsync("zoom 7; clicks 10,-5; view zoom 7");
        browser.ThrowIfFailed();
    }
}
