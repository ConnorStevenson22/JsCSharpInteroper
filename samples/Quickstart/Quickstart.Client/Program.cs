using Interoper;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quickstart.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
// The realm for WebAssembly components, over the sync bindings (Maps/).
builder.Services.AddInteroper(Metadata.Path, Quickstart.Client.Maps.DemoMaps.Classes);
await builder.Build().RunAsync();
