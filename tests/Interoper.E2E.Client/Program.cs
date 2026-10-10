using System.Runtime.InteropServices.JavaScript;
using Interoper;
using Interoper.E2E.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
// Components use the realm the app registers, so the sync-mode components page gets the sync bindings' registry. The
// path is read once, when the app starts: this relies on each test opening a fresh page, not navigating within one.
var path = OperatingSystem.IsBrowser() ? JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("pathname") : null;
builder.Services.AddInteroper(Fixture.Metadata, path == "/components-wasm-sync" ? GoldenSlice.Sync.DemoMaps.Classes : GoldenSlice.Async.DemoMaps.Classes);
await builder.Build().RunAsync();
