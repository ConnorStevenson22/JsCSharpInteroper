# Spikes

Small, throwaway experiments that settle design questions for the [roadmap](../docs/roadmap.md). Each one is runnable, and the roadmap quotes its results. They use only made-up example code; no third-party library code lives here.

| Spike | Question it answers | Result |
|---|---|---|
| [value-model](value-model) | Can `undefined` be a union case, and can a union's `default` *be* undefined, with `null` as JS null and compiler-checked exhaustiveness? | Yes: 7/7 checks pass (.NET 11 RC1) |
| [hosts](hosts) | Can one generated async live-object API (`GetZoomAsync`/`SetZoomAsync`) run unchanged on WebAssembly and Server? What does each host cost? | Yes. WebAssembly: completes synchronously, 0 B, ~1.5 µs. Server: ~1 ms per awaited call on localhost, 25–40× cheaper when pipelined. Callbacks work on both. |
