# Spikes

Small, throwaway experiments that settle design questions for the [roadmap](../docs/roadmap.md). Each one is runnable, and the roadmap quotes its results.

| Spike | Question it answers | Result |
|---|---|---|
| [value-model](value-model) | Can `undefined` be a union case, and can a union's `default` *be* undefined, with `null` as JS null and compiler-checked exhaustiveness? | Yes: 7/7 checks pass (.NET 11 RC1) |
| [wasm-transport](wasm-transport) | Are synchronous "live" properties viable via `[JSImport]`, on Mono and on CoreCLR? How do they compare with Blazor's `IJSRuntime`? | Yes: ~0.5 µs per read on Mono, ~5 µs on CoreCLR, vs 45–270 µs via `IJSRuntime`; `[JSExport]` callbacks work on both |
