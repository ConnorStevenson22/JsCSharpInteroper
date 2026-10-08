using HostsSpike.Client.Runtime;
using HostsSpike.Server.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.Configure<InteropOptions>(builder.Configuration.GetSection("Interop")); // "Interop": { "Transport": "Auto" }

var app = builder.Build();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(HostsSpike.Client.Bench).Assembly);
app.Run();
