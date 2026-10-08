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

`Client/Bench.razor` runs the same code under `/wasm` (`InteractiveWebAssembly`) and `/server` (`InteractiveServer`).

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run -c Release --project Server --no-launch-profile --urls http://127.0.0.1:5281
npm i playwright-core   # once, in this folder
CHROME=/path/to/chromium node run.mjs http://127.0.0.1:5281/wasm   # or /server
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
- **Hybrid** uses the same `JsRuntimeTransport` over the WebView bridge. It isn't exercised here: Hybrid hosts (MAUI, WPF, WinForms) don't run in this Linux container.
