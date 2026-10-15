# JsCSharpInteroper

**Interoper** generates C# bindings for JavaScript libraries from their TypeScript typings, plus the runtime that drives them on Blazor **Server**, **WebAssembly** and **Hybrid**. It targets .NET 11 and C# 15.

```bash
npm install @acme/maps
npm install -D typescript@6.0.3
dotnet new nugetconfig && dotnet nuget add source ./packages --name local   # until they're on nuget.org: where you packed them
dotnet new tool-manifest && dotnet tool install Interoper.Tool --prerelease
dotnet add package Interoper.Blazor --prerelease
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps
```

The packages aren't on nuget.org yet: `./packages` is the folder you pack them into ([quickstart](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/quickstart.md#1-the-packages)).

```csharp
await view.SetZoomAsync(7);                       // --api async: every host
view.Zoom = 7;                                    // --api sync: WebAssembly only, plain properties
if (await feature.GetShapeAsync() is PointSnapshot p) { } // unions, undefined/null and the most-derived type, all typed
```

**Status:** phases 1–4 (runtime core, generator, full surface, shipping) are done ([plan](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/plan.md)). The three packages (`Interoper.Runtime`, `Interoper.Blazor`, `Interoper.Tool`, version 0.1.0-preview.1) build with `dotnet pack` but aren't on nuget.org yet; [docs/release.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/release.md) is what runs before a release.

**Start with the [quickstart](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/quickstart.md):** a Blazor Web App that binds a library and drives it on Server and on WebAssembly.

| Read | For |
|---|---|
| [docs/quickstart.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/quickstart.md) | A Blazor Web App, from `npm install` to a running page, in both modes |
| [docs/design.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/design.md) | Decisions and architecture |
| [docs/plan.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/plan.md) | Build order, as four phases with checklists |
| [docs/release.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/release.md) | The checks before a release, including the Hybrid one by hand on Windows |
| [docs/mapping.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/mapping.md) | TypeScript → C# mapping rules and diagnostics |
| [CLAUDE.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/CLAUDE.md) | Rules and commands for anyone (human or agent) working in the repo |
| [docs/ir.md](https://github.com/ConnorStevenson22/JsCSharpInteroper/blob/main/docs/ir.md) | The extractor's output: what the typings say, resolved by the checker |
| [tools/dts-survey/](https://github.com/ConnorStevenson22/JsCSharpInteroper/tree/main/tools/dts-survey) | Measures which type shapes real typings use |

This repo publishes a generator and runtime only. It contains no code, typings or generated bindings from any third-party JS library: consumers install their library from npm and generate bindings themselves.
