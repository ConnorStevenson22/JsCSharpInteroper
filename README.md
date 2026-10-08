# JsCSharpInteroper

Research into a **generic** generator and runtime library for calling JavaScript libraries from .NET (Blazor and beyond), now that C# 15 / .NET 11 has union types.

- **[docs/roadmap.md](docs/roadmap.md)**: the implementation roadmap (ArcGIS-first, .NET 11+). It covers the value model with `undefined` as a union case, the generated kinds (live objects, snapshots, components and more), the runtime and generator architecture, milestones, and open questions.
- **[docs/report.md](docs/report.md)**: the feasibility report. It covers pain points, measured data from 12 real libraries, what C# 15 unions do and don't solve, a proposed architecture, and a roadmap.
- **[tools/dts-survey](tools/dts-survey)**: a TypeScript-checker-based survey of `.d.ts` libraries (ArcGIS, Google Maps, Leaflet, OpenLayers, MapLibre, Cesium, Chart.js, ECharts, three.js, Monaco, DOM).
- **[prototype/UnionInterop](prototype/README.md)**: a C# 15 prototype of the proposed mappings, checked through Blazor's real interop serializer.
- **[spikes](spikes/README.md)**: runnable experiments backing the roadmap (the value model; `[JSImport]` live properties on Mono vs CoreCLR).
