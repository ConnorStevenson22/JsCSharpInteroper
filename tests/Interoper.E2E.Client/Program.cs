using Interoper;
using Interoper.E2E.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddInteroper(Fixture.Metadata, GoldenSlice.Async.DemoMaps.Classes);
await builder.Build().RunAsync();
