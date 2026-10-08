# JsCSharpInteroper

Research into a **generic** generator and runtime library for calling JavaScript libraries from .NET (Blazor and beyond), now that C# 15 / .NET 11 has union types.

- **[docs/roadmap.md](docs/roadmap.md)**: the implementation roadmap (.NET 11+, Blazor Server, WebAssembly and Hybrid). It covers the value model with `undefined` as a union case, sync/async live objects, snapshots, components, transports, the generator CLI, milestones, and open questions.
- **[docs/report.md](docs/report.md)**: the feasibility report. It covers pain points, measured data from 12 real libraries, what C# 15 unions do and don't solve, a proposed architecture, and a roadmap.
- **[tools/dts-survey](tools/dts-survey)**: a TypeScript-checker-based survey of `.d.ts` libraries; which libraries is a config file.
- **[prototype/UnionInterop](prototype/README.md)**: a C# 15 prototype of the proposed mappings, checked through Blazor's real interop serializer.
- **[spikes](spikes/README.md)**: runnable experiments backing the roadmap (the value model; sync vs async live objects on WebAssembly and Server).

This repo publishes a generator and runtime only. It contains no code, typings or generated bindings from any third-party JS library: consumers install their library from npm and generate bindings in their own build.
