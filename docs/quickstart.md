# Quickstart

A Blazor Web App that binds a JavaScript library and sets and reads a map's zoom from C#. One page runs on Interactive Server with `--api async` bindings. The other runs on WebAssembly with `--api sync` bindings.

The finished app is [`samples/Quickstart`](../samples/Quickstart): a server project and its WebAssembly client, as `dotnet new blazor --interactivity Auto` makes them. Its library is the repo's made-up fixture, `@acme/demo-maps`. [`QuickstartTests`](../tests/Interoper.E2E/QuickstartTests.cs) runs the commands below exactly as written, so keep the two in step.

You need the .NET 11 SDK and Node.js 22 or newer.

## 1. The packages

The packages aren't published, so pack them from a clone of this repo. The sample reads them, and the fixture, from its `packages/` folder. From the repo's root:

<!-- QuickstartTests: pack -->
```bash
dotnet pack src/Interoper.Runtime -o samples/Quickstart/packages
dotnet pack src/Interoper.Blazor -o samples/Quickstart/packages
dotnet pack src/Interoper.Tool -o samples/Quickstart/packages
npm pack ./fixtures/demo-maps --pack-destination samples/Quickstart/packages
```

What each package holds is in [design §5](design.md#5-consumer-workflow-and-packages). All three share one version. NuGet caches packages by version, so if you change Interoper and pack the same version again, delete these first:

- `~/.nuget/packages/interoper.*/<version>` (on Windows, `%USERPROFILE%\.nuget\packages\interoper.*\<version>`)
- `~/.dotnet/toolResolverCache/1/interoper.tool`

## 2. Generate, build, run

From `samples/Quickstart`:

<!-- QuickstartTests: steps -->
```bash
npm install
dotnet tool restore
dotnet interoper generate @acme/demo-maps --api async --namespace Quickstart.Maps --out Quickstart/Maps --profile node_modules/@acme/demo-maps/profile.json
dotnet interoper generate @acme/demo-maps --api sync --namespace Quickstart.Client.Maps --out Quickstart.Client/Maps --profile node_modules/@acme/demo-maps/profile.json
dotnet build Quickstart
dotnet run --project Quickstart
```

1. `npm install` installs the library and the TypeScript the extractor uses.
2. `dotnet tool restore` installs `dotnet interoper` from `.config/dotnet-tools.json`.
3. Each `generate` writes into the project that compiles it. The server gets async bindings, which work on every host. The client gets sync bindings, plain properties that only work where .NET runs in the browser. Each run writes `interoper-report.txt`, listing what it skipped or fell back on. The profile holds the library's settings, such as which classes are value-like and how its events work ([design §4](design.md#4-generator-architecture)). Both runs write the same `demo-maps.meta.js`, and the server serves one copy.
4. `dotnet build Quickstart` builds both projects with warnings as errors.
5. `dotnet run --project Quickstart` serves the app at http://localhost:5080. Open `/zoom-server` and `/zoom-wasm` and press Zoom in.

Run `generate` again when `npm install` brings a new version of the library.

## 3. What's in the app

The server's `Program.cs` registers the async bindings. It serves `node_modules` at `/node_modules/` and the metadata module at `/maps/demo-maps.meta.js`. A real app would copy the library into `wwwroot` or use a CDN or bundle ([design §3.5](design.md#35-module-loading)).

```csharp
builder.Services.AddInteroper(Metadata.Path, Quickstart.Maps.DemoMaps.Classes);
```

The client's `Program.cs` does the same with the sync bindings:

```csharp
builder.Services.AddInteroper(Metadata.Path, Quickstart.Client.Maps.DemoMaps.Classes);
```

Generated code imports the library by bare specifiers such as `@acme/demo-maps/MapView.js`. `App.razor` adds an import map with the entries from the generated `ImportMap(root)`:

```razor
<ImportMap ImportMapDefinition="importMap" />
@code {
    ImportMapDefinition importMap => ImportMapDefinition.Combine(
        ImportMapDefinition.FromResourceCollection(Assets),
        new ImportMapDefinition(Quickstart.Maps.DemoMaps.ImportMap("/node_modules"), null, null));
}
```

On `/zoom-server` every call is a round trip to the browser, so every member returns a `Task`:

```csharp
await Realm.StartAsync();
scope = new JsScope(Realm);
view = await MapView.CreateAsync(scope, new MapViewSnapshot { Zoom = 3 });
await view.SetZoomAsync(zoom + 1);
zoom = await view.GetZoomAsync();
```

On `/zoom-wasm` members are plain properties:

```csharp
await Realm.StartAsync();
scope = new JsScope(Realm);
view = MapView.Create(scope, new MapViewSnapshot { Zoom = 3 });
view.Zoom += 1;
```

Disposing the scope releases the view in the browser ([design §3.2](design.md#32-handles-identity-and-lifetime)).

## Your own app

Use the same steps with your library and your own tool manifest. Add a `nuget.config` that lists `./packages`, the folder from step 1, next to nuget.org:

```bash
npm install @acme/maps
npm install -D typescript@6.0.3
dotnet new nugetconfig
dotnet nuget add source ./packages --name local
dotnet new tool-manifest
dotnet tool install Interoper.Tool --prerelease
dotnet add package Interoper.Blazor --prerelease
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps
```

Write a profile for your library when the report asks for one, and serve the library and the metadata module as the sample does.
