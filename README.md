# JsCSharpInteroper

Interoper generates C# bindings for JavaScript libraries from their TypeScript typings. It also ships the runtime that drives them on Blazor Server, WebAssembly and Hybrid. It targets .NET 11 and C# 15.

```bash
npm install @acme/maps
npm install -D typescript@6.0.3
dotnet new nugetconfig && dotnet nuget add source ./packages --name local
dotnet new tool-manifest && dotnet tool install Interoper.Tool --prerelease
dotnet add package Interoper.Blazor --prerelease
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps
```

```csharp
await view.SetZoomAsync(7);   // --api async: works on every host
view.Zoom = 7;                // --api sync: WebAssembly only
```

The packages (`Interoper.Runtime`, `Interoper.Blazor` and `Interoper.Tool`, version 0.1.0-preview.1) aren't published yet. Pack them from a clone into `./packages` as the [quickstart](docs/quickstart.md#1-the-packages) shows.

Open work is listed at the end of [design.md](docs/design.md#9-not-done-yet).

## Docs

| Read | For |
|---|---|
| [quickstart.md](docs/quickstart.md) | A Blazor Web App, from `npm install` to a running page |
| [design.md](docs/design.md) | Decisions and architecture |
| [mapping.md](docs/mapping.md) | TypeScript to C# mapping rules and diagnostics |
| [ir.md](docs/ir.md) | The extractor's output |
| [release.md](docs/release.md) | Checks to run before a release |
| [CLAUDE.md](CLAUDE.md) | Rules and commands for working in the repo |
| [tools/dts-survey](tools/dts-survey) | Counts which type shapes real typings use |

This repo holds no code, typings or bindings from any third-party JS library. You install your library from npm and generate its bindings yourself.
