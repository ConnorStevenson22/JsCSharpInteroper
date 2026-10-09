# Spikes

Small, throwaway experiments that settle design questions for the [design](../docs/design.md). Each one is runnable, and the design quotes its results. They use only made-up example code; no third-party library code lives here.

| Spike | Question it answers | Result |
|---|---|---|
| [value-model](value-model) | Can `undefined` be a union case, and can a union's `default` *be* undefined, with `null` as JS null and compiler-checked exhaustiveness? | Yes: 7/7 checks pass (.NET 11 RC1) |
| [hosts](hosts) | How do generated live objects run on WebAssembly, Server and Hybrid, with a generator switch between sync (`X Zoom { get; set; }`) and async (`Task<X> GetZoomAsync()`) members? | Sync mode works on WebAssembly only: ~0.65 µs per read, 0 B. On Server it fails fast. Async mode runs everywhere: ~1.1 µs and 48 B on WebAssembly, ~1 ms per await on Server (~50 µs per write pipelined). Hybrid can't use `[JSImport]` (`PlatformNotSupportedException` outside the browser), so it takes the `IJSRuntime` path. The async transport is chosen per component (`InteractiveAuto` moved from Server to WebAssembly by itself). |
