# JsCSharpInteroper

**Interoper** generates C# bindings for JavaScript libraries from their TypeScript typings, plus the runtime that drives them on Blazor **Server**, **WebAssembly** and **Hybrid**. It targets .NET 11 and C# 15.

```bash
npm install @acme/maps
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps
```

```csharp
await view.SetZoomAsync(7);                       // --api async: every host
view.Zoom = 7;                                    // --api sync: WebAssembly only, plain properties
if (await feature.GetShapeAsync() is Point p) { } // unions, undefined/null and the most-derived type, all typed
```

**Status:** design complete, implementation starting at milestone M0.

| Read | For |
|---|---|
| [docs/roadmap.md](docs/roadmap.md) | Decisions, architecture, milestones |
| [docs/mapping.md](docs/mapping.md) | TypeScript → C# mapping rules and diagnostics |
| [docs/work-packages.md](docs/work-packages.md) | Delegable tasks with acceptance criteria |
| [CLAUDE.md](CLAUDE.md) | Rules and commands for anyone (human or agent) working in the repo |
| [spikes/](spikes/README.md) | Runnable experiments behind the key decisions |
| [tools/dts-survey/](tools/dts-survey) | Measures which type shapes real typings use; seed for the extractor |

This repo publishes a generator and runtime only. It contains no code, typings or generated bindings from any third-party JS library: consumers install their library from npm and generate bindings themselves.
