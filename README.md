# JsCSharpInteroper

**Interoper** generates C# bindings for JavaScript libraries from their TypeScript typings, plus the runtime that drives them on Blazor **Server**, **WebAssembly** and **Hybrid**. It targets .NET 11 and C# 15.

```bash
npm install @acme/maps
npm install -D typescript@6.0.3
dotnet new nugetconfig && dotnet nuget add source ./packages --name local   # the packages aren't published: where you packed them
dotnet new tool-manifest && dotnet tool install Interoper.Tool --prerelease
dotnet add package Interoper.Blazor --prerelease
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps
```

The packages aren't published; pack them from a clone: `./packages` is the folder you pack them into ([quickstart](docs/quickstart.md#1-the-packages)).

```csharp
await view.SetZoomAsync(7);                       // --api async: every host
view.Zoom = 7;                                    // --api sync: WebAssembly only, plain properties
if (await feature.GetShapeAsync() is PointSnapshot p) { } // unions, undefined/null and the most-derived type, all typed
```

**Status:** phases 1–4 (runtime core, generator, full surface, shipping) are done ([plan](docs/plan.md)). The three packages (`Interoper.Runtime`, `Interoper.Blazor`, `Interoper.Tool`, version 0.1.0-preview.1) aren't published; they build with `dotnet pack` from a clone, and [docs/release.md](docs/release.md) is what runs before a release.

**Start with the [quickstart](docs/quickstart.md):** a Blazor Web App that binds a library and drives it on Server and on WebAssembly.

| Read | For |
|---|---|
| [docs/quickstart.md](docs/quickstart.md) | A Blazor Web App, from `npm install` to a running page, in both modes |
| [docs/design.md](docs/design.md) | Decisions and architecture |
| [docs/plan.md](docs/plan.md) | Build order, as four phases with checklists |
| [docs/release.md](docs/release.md) | The checks before a release, including the Hybrid one by hand on Windows |
| [docs/mapping.md](docs/mapping.md) | TypeScript → C# mapping rules and diagnostics |
| [CLAUDE.md](CLAUDE.md) | Rules and commands for anyone (human or agent) working in the repo |
| [docs/ir.md](docs/ir.md) | The extractor's output: what the typings say, resolved by the checker |
| [tools/dts-survey/](tools/dts-survey) | Measures which type shapes real typings use |

This repo holds a generator and runtime only. It contains no code, typings or generated bindings from any third-party JS library: consumers install their library from npm and generate bindings themselves.
