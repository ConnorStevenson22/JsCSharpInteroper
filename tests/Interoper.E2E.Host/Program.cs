using Interoper.E2E.Client;
using Interoper.E2E.Host.Components;
using Microsoft.Extensions.FileProviders;

// The browser-test host: Server and WebAssembly interactivity in one app (design §6).
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();
var demoMaps = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../../fixtures/demo-maps"));
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(demoMaps), RequestPath = "/demo-maps" });
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ContractSuite).Assembly);
app.Run();
