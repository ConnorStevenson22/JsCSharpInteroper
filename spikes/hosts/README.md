# hosts spike

Can **one** generated live-object API, async get/set methods that mirror the JS property names, run unchanged on Blazor **WebAssembly**, **Server** and **Hybrid**? And what does each host cost?

```csharp
await view.SetZoomAsync(7);                 // JS: view.zoom = 7
double zoom = await view.GetZoomAsync();    // JS: view.zoom
await using var sub = await view.OnClickAsync(e => ...);
```

`Client/Generated/MapView.cs` is written the way the generator would emit it. It only talks to `IJsTransport`, which has two implementations:

| Transport | Hosts | How |
|---|---|---|
| `JsImportTransport` | WebAssembly | `[JSImport]` calls that return already-completed `ValueTask`s; JS → .NET via one `[JSExport]` method |
| `JsRuntimeTransport` | Server, Hybrid | `IJSRuntime` / `IJSObjectReference` to the same JS module; JS → .NET via one `DotNetObjectReference` |

`Client/Bench.razor` runs the same code on every page below.

## The transport switch

`InteropOptions.Transport` is bound from configuration (`"Interop": { "Transport": "Auto" }`) in both apps:

| Mode | Behaviour |
|---|---|
| `Auto` (default) | `[JSImport]` when .NET runs inside the browser, otherwise `IJSRuntime`. Decided **per component at runtime**. |
| `JsRuntime` | Force `IJSRuntime` everywhere. On WebAssembly this reproduces Server/Hybrid behaviour, which is handy for parity tests. |
| `JsImport` | Force `[JSImport]`. Only possible when .NET runs in the browser; elsewhere it fails fast with a clear message. |

Hybrid can't take the WebAssembly path. In a `BlazorWebView`, .NET runs natively in the app process while JS runs in the WebView, so there's no shared WebAssembly runtime. Two checks confirm it:

- Calling `[JSImport]` outside the browser throws `PlatformNotSupportedException: System.Runtime.InteropServices.JavaScript is not supported on this platform`.
- Hybrid's `WebViewJSRuntime` derives from `JSRuntime` and is *not* an `IJSInProcessRuntime`: an async message bridge, the same shape as Server's.

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run -c Release --project Server --no-launch-profile --urls http://127.0.0.1:5281
npm i playwright-core   # once, in this folder
CHROME=/path/to/chromium node run.mjs http://127.0.0.1:5281/wasm http://127.0.0.1:5281/server
# also: /wasm-jsruntime, /server-jsimport, and /auto (visit it twice: server first, then WebAssembly)
```

## Results

.NET 11 RC1 on Mono WebAssembly, headless Chromium, server on localhost. Two runs each:

| | WebAssembly | Server (localhost) |
|---|---:|---:|
| `GetZoomAsync()` completes synchronously | **yes** | no |
| managed allocations per `GetZoomAsync()` | **0 B** | n/a |
| `await GetZoomAsync()` sequential | 1.5 µs | 0.8–1.1 ms |
| `await SetZoomAsync()` sequential | 1.5 µs | 0.7–1.0 ms |
| `SetZoomAsync()` pipelined (start all, then `WhenAll`) | 1.7–2.8 µs | **28–39 µs** |
| write order preserved when pipelined | yes | yes |
| snapshot (one call, all properties) | works | works |
| event JS → .NET (first call, cold) | 8–11 ms | 2–8 ms |

What this means for the design:

- **Async-only costs WebAssembly almost nothing.** Results come back as completed `ValueTask`s, so there's no allocation and ~1.5 µs per call. That is still ~30× faster than Blazor's `IJSInProcessRuntime` (~45 µs per call on Mono, measured in an earlier version of this spike; see git history for `spikes/wasm-transport`).
- **On Server, latency is the whole cost.** Sequential awaits pay a full SignalR round trip each, ~1 ms even on localhost; more on a real network. Pipelining the same writes is **25–40× faster**, and order is preserved. So the runtime needs batching and pipelining as first-class features, and snapshots for reads.
- **Hybrid** uses the same `JsRuntimeTransport` over the WebView bridge. It isn't exercised end to end here: Hybrid hosts (MAUI, WPF, WinForms) don't run in this Linux container.

Switch results (one run):

| Page | .NET runs in | Mode | Transport picked | Awaited `GetZoomAsync()` |
|---|---|---|---|---:|
| `/wasm` | WebAssembly | Auto | `[JSImport]` | ~2 µs |
| `/wasm-jsruntime` | WebAssembly | JsRuntime | `IJSRuntime` | ~165 µs |
| `/server` | server process | Auto | `IJSRuntime` | ~1 ms |
| `/server-jsimport` | server process | JsImport | fails fast: `PlatformNotSupportedException` with guidance | — |
| `/auto`, visit 1 | server process | Auto | `IJSRuntime` | — |
| `/auto`, visits 2–3 | WebAssembly | Auto | `[JSImport]` | — |

Under `InteractiveAuto`, the same component moved from Server to WebAssembly between visits and picked the matching transport by itself. That's why the choice is made per component rather than once per app.
