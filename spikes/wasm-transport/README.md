# wasm-transport spike

Can a generated "live object" use plain synchronous C# properties (`view.Zoom = 7`) on Blazor WebAssembly, and what does each interop path cost per call, on Mono and on CoreCLR?

`JsBind.cs` holds `[JSImport]` accessors over a tiny JS handle table (`wwwroot/jsbind.js`) plus a `[JSExport]` callback. `Bench.razor` times one property read across interop paths.

```bash
# CoreCLR (opt-in preview in .NET 11; the project sets UseMonoRuntime=false)
dotnet publish -c Release -o out-clr
# Mono (clean bin/obj between the two builds; reusing them produced a build that failed to start)
rm -rf bin obj && dotnet publish -c Release -p:UseMonoRuntime=true -o out-mono

# serve out-*/wwwroot with any static server, then:
npm i playwright-core
CHROME=/path/to/chromium node run.mjs http://127.0.0.1:8611/
```

Results, from headless Chromium in a CI container (.NET 11 RC1, two runs each, warm). Absolute numbers depend on the machine; the ratios are what matter:

| One property read | Mono | CoreCLR (interpreter) |
|---|---:|---:|
| `[JSImport]` sync (`view.Zoom`) | 476–490 ns | 4,907–5,343 ns |
| `IJSInProcessRuntime.Invoke<double>` | 43–54 µs | 126–130 µs |
| `IJSRuntime.InvokeAsync<double>` | 88–113 µs | 230–271 µs |
| `[JSImport]` 2-property JSON snapshot | 1.7–2.1 µs | 7.3–8.1 µs |
| `[JSExport]` callback from JS | works | works |

Takeaways:

- `[JSImport]` is ~100× faster than Blazor's JSON-based interop on Mono, and ~25× faster on CoreCLR. Synchronous live properties are cheap enough to use freely.
- CoreCLR on WebAssembly runs this unchanged, but its interpreter makes each call ~10× slower than Mono in .NET 11. Microsoft targets performance work (ReadyToRun/RyuJIT) for .NET 12.
- One gotcha: the SDK sets `UseMonoRuntime=true` *before* the project file is evaluated, so a conditional default in the project never applies. Set it unconditionally; a `-p:` on the command line still overrides it.
