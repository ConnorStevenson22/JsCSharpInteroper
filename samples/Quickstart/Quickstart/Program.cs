using Interoper;
using Microsoft.Extensions.FileProviders;
using Quickstart.Client;
using Quickstart.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
// The realm for Server components, over the async bindings (Maps/); the client registers its own (sync).
builder.Services.AddInteroper(Metadata.Path, Quickstart.Maps.DemoMaps.Classes);

var app = builder.Build();
// npm's packages, served where the generated import map points (App.razor). An app that ships would copy them into
// wwwroot, or map them to a CDN or a bundle (design §3.5).
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "node_modules"))),
    RequestPath = "/node_modules",
});
// The generated metadata module, the same in both modes: one copy serves both realms.
app.MapGet(Metadata.Path, () => Results.File(Path.Combine(app.Environment.ContentRootPath, "Maps", "demo-maps.meta.js"), "text/javascript"));
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Metadata).Assembly);
app.Run();
