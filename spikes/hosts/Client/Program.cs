using HostsSpike.Client.Runtime;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.Configure<InteropOptions>(builder.Configuration.GetSection("Interop"));
await builder.Build().RunAsync();
