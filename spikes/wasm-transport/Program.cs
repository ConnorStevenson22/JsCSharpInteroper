using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WasmTransport;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Bench>("#app");
await builder.Build().RunAsync();
