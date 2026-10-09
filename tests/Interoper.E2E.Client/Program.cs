using Interoper;
using Interoper.E2E.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddInteroper(DemoMaps.Metadata, DemoMaps.Classes);
await builder.Build().RunAsync();
