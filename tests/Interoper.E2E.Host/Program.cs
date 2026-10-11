using Interoper;
using Interoper.E2E.Client;
using Interoper.E2E.Host.Components;
using Microsoft.Extensions.FileProviders;

// The browser-test host: Server and WebAssembly interactivity in one app (design §6).
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddInteroper(Fixture.Metadata, GoldenSlice.Async.DemoMaps.Classes); // for Server components, and prerendering

var app = builder.Build();
var demoMaps = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../../fixtures/demo-maps"));
// Where npm would install it, so the generated import map (App.razor) resolves it.
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(demoMaps), RequestPath = "/node_modules/@acme/demo-maps" });
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ContractSuite).Assembly);
app.Run();
