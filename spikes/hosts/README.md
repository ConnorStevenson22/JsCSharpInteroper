# hosts spike

How do generated live objects behave on Blazor **WebAssembly**, **Server** and **Hybrid**, and what does each cost? The generator gets a switch, `--api sync|async`, that decides the shape of every live member:

```csharp
// --api sync   (WebAssembly only)            // --api async   (every host)
view.Zoom = 7;                                 await view.SetZoomAsync(7);
double zoom = view.Zoom;                       double zoom = await view.GetZoomAsync();
using var sub = view.OnClick(e => ...);        await using var sub = await view.OnClickAsync(e => ...);
MapViewSnapshot s = view.Snapshot();           MapViewSnapshot s = await view.SnapshotAsync();
```

`Client/Generated/Sync/MapView.cs` and `Client/Generated/Async/MapView.cs` are written the way the generator would emit each mode. Data types such as `MapViewSnapshot` are identical in both modes; only live members change.

| Seam | Used by | Implementations |
|---|---|---|
| `IJsSyncTransport` | sync mode | `JsImportTransport` (`[JSImport]`), WebAssembly only |
| `IJsTransport` (`Task`-based) | async mode | `JsImportTransport` (completed `Task`s) on WebAssembly; `JsRuntimeTransport` (`IJSRuntime`) on Server and Hybrid |

JS → .NET callbacks go through one `[JSExport]` method on WebAssembly and one `DotNetObjectReference` on Server/Hybrid.

**Hybrid can't run sync mode or use `[JSImport]`.** In a `BlazorWebView`, .NET runs natively in the app process while JS runs in the WebView; there's no shared WebAssembly runtime. Two checks confirm it:

- Calling `[JSImport]` outside the browser throws `PlatformNotSupportedException: System.Runtime.InteropServices.JavaScript is not supported on this platform`.
- Hybrid's `WebViewJSRuntime` derives from `JSRuntime` and is *not* an `IJSInProcessRuntime`: an async message bridge, the same shape as Server's.

Sync-mode bindings on Server fail fast: `These bindings were generated in sync mode, which only runs on Blazor WebAssembly … regenerate with --api async.`

In async mode, a runtime option (`"Interop": { "Transport": "Auto" | "JsRuntime" | "JsImport" }`) picks the transport, resolved **per component**:
- `Auto` picks `[JSImport]` on WebAssembly and `IJSRuntime` elsewhere.
- Under `InteractiveAuto`, the same component ran on the server first and in WebAssembly on later visits, and `Auto` followed it.
- `JsRuntime` reproduces Server behaviour inside WebAssembly, which is useful for parity tests.

## Run it

```bash
dotnet publish Server -c Release -o out && dotnet out/HostsSpike.Server.dll --urls http://127.0.0.1:5284
npm i playwright-core   # once, in this folder
CHROME=/path/to/chromium node run.mjs http://127.0.0.1:5284/wasm-sync http://127.0.0.1:5284/wasm http://127.0.0.1:5284/server
# also: /server-sync (fails fast), /wasm-jsruntime, /server-jsimport (fails fast), /auto (visit twice)
```

## Results

.NET 11 RC1, Mono WebAssembly, published Release build, headless Chromium, server on localhost. Two runs:

| One `zoom` access | sync mode, WebAssembly | async mode, WebAssembly | async mode, Server |
|---|---:|---:|---:|
| read | **0.63–0.69 µs**, 0 B | 1.1–1.2 µs, 48 B (the `Task<double>`) | 0.8–1.1 ms |
| write | 0.74–1.17 µs | ~1.5 µs | 0.73–0.78 ms |
| writes pipelined (`WhenAll`) | — | ~1.9 µs | **49–65 µs** |
| write order preserved | yes | yes | yes |
| snapshot / events | work (events arrive before `Raise()` returns) | work | work |

Earlier runs of the async-mode pages under `dotnet run` (not published) measured 1.5–3.3 µs on WebAssembly. The published build is the fair comparison.

What this means for the design:

- **Sync mode is the fastest WebAssembly option:** ~1.8× faster than async mode, with no allocation, and plain C# properties.
- **Async mode is still cheap on WebAssembly,** and it's the only mode that runs on Server and Hybrid, including `InteractiveAuto` apps whose components may render on the server.
- **On Server, latency is the whole cost.** Each awaited call is a SignalR round trip (~1 ms even on localhost), while pipelined writes cost ~50 µs each, in order. So async mode needs batching, snapshots and pipelining as first-class features.
- **Hybrid** uses the same `JsRuntimeTransport` over the WebView bridge. It isn't exercised end to end here: Hybrid hosts (MAUI, WPF, WinForms) don't run in this Linux container.
