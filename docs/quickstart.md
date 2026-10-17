# Quickstart

A Blazor Web App that binds a JavaScript library and drives it from C#: one page on **Interactive Server** with `--api async` bindings, one on **WebAssembly** with `--api sync` bindings. Both set and read a map's zoom.

The finished app is [`samples/Quickstart`](../samples/Quickstart): a server project (`Quickstart`) and its WebAssembly client (`Quickstart.Client`), like `dotnet new blazor --interactivity Auto` makes. Its library is the repo's made-up fixture, `@acme/demo-maps`. [`QuickstartTests`](../tests/Interoper.E2E/QuickstartTests.cs) runs the commands below as they are written here, in a copy of the sample, so keep the two in step.

You need the .NET 11 SDK and Node.js (22 or newer) on `PATH`.

## 1. The packages

The packages aren't published; pack them from a clone of this repo. The sample's `nuget.config` reads the packages from its `packages/` folder, and its `package.json` installs the fixture from there too. From the repo's root:

<!-- QuickstartTests: pack -->
```bash
dotnet pack src/Interoper.Runtime -o samples/Quickstart/packages
dotnet pack src/Interoper.Blazor -o samples/Quickstart/packages
dotnet pack src/Interoper.Tool -o samples/Quickstart/packages
npm pack ./fixtures/demo-maps --pack-destination samples/Quickstart/packages
```

| Package | What it brings |
|---|---|
| `Interoper.Runtime` | The runtime, `interoper.js` (a static web asset, `_content/Interoper.Runtime/interoper.js`) and the chatty-await analyzer (IOP3001) |
| `Interoper.Blazor` | `AddInteroper()`, the generated components' base class and the custom-event plumbing; it brings `Interoper.Runtime` |
| `Interoper.Tool` | The `dotnet interoper` CLI: the generator, and the extractor as source, run with your Node and TypeScript |

Every package has the same version. NuGet caches a package by its version, so after you change Interoper and pack the same version again, delete `~/.nuget/packages/interoper.*/<version>` (on Windows, `%USERPROFILE%\.nuget\packages\interoper.*\<version>`) first. `dotnet tool restore` also remembers where it found each tool version, in `~/.dotnet/toolResolverCache`, so delete `~/.dotnet/toolResolverCache/1/interoper.tool` too; restoring into another packages folder (`NUGET_PACKAGES`) isn't enough on its own, since that cache still names the old one. The test doesn't need either: it restores into a packages folder of its own (`NUGET_PACKAGES`) and gives the .NET CLI a home of its own (`DOTNET_CLI_HOME`), both inside its copy.

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

1. `npm install` installs the library and the TypeScript the extractor runs on (`typescript@6.0.3`, from `package.json`).
2. `dotnet tool restore` installs `dotnet interoper` from the tool manifest (`.config/dotnet-tools.json`).
3. One `generate` per mode, each into the project that compiles it: the server project gets the async bindings, which work on every host; the WebAssembly client gets the sync ones, plain properties that only work where .NET runs in the browser. Each run writes a report (`interoper-report.txt`) with the typings' constructs it fell back on or skipped, and the profile is the library's: which classes are value-like, how its events work, and so on ([design §4](design.md#4-generator-architecture)). Both runs write the same metadata module, `demo-maps.meta.js`; the server serves one copy for both.
4. `dotnet build Quickstart` builds both projects; the sample treats warnings as errors, so the generated code builds clean.
5. `dotnet run --project Quickstart` serves the app at http://localhost:5080. Open `/zoom-server` and `/zoom-wasm`, and press **Zoom in**.

Run `generate` again after `npm install` brings a new version of the library: it reuses its IR while the versions are the same ([design §5](design.md#5-consumer-workflow-and-packages)).

## 3. What's in the app

**Server, `Program.cs`.** Registers the realm for Server components over the async bindings, serves `node_modules` at `/node_modules/` and the metadata module at `/maps/demo-maps.meta.js` (`Quickstart.Client.Metadata.Path`). An app that ships would copy the library into `wwwroot`, or map it to a CDN or a bundle ([design §3.5](design.md#35-module-loading)).

```csharp
builder.Services.AddInteroper(Metadata.Path, Quickstart.Maps.DemoMaps.Classes);
```

**Client, `Program.cs`.** The same, over the sync bindings:

```csharp
builder.Services.AddInteroper(Metadata.Path, Quickstart.Client.Maps.DemoMaps.Classes);
```

**`App.razor`.** Generated code imports the library by its bare specifiers (`@acme/demo-maps/MapView.js`), and the import map says where they are. The generated registry has the entries, `ImportMap(root)`:

```razor
<ImportMap ImportMapDefinition="importMap" />
@code {
    ImportMapDefinition importMap => ImportMapDefinition.Combine(
        ImportMapDefinition.FromResourceCollection(Assets),
        new ImportMapDefinition(Quickstart.Maps.DemoMaps.ImportMap("/node_modules"), null, null));
}
```

**`/zoom-server`** (`Quickstart/Components/Pages/ZoomServer.razor`). Every call is a round trip to the browser, so every member is a `Task`:

```csharp
await Realm.StartAsync();
scope = new JsScope(Realm);
view = await MapView.CreateAsync(scope, new MapViewSnapshot { Zoom = 3 });
await view.SetZoomAsync(zoom + 1);
zoom = await view.GetZoomAsync();
```

**`/zoom-wasm`** (`Quickstart.Client/Pages/ZoomWasm.razor`). Members are plain properties, so the markup reads `view.Zoom` from JS as it renders:

```csharp
await Realm.StartAsync();
scope = new JsScope(Realm);
view = MapView.Create(scope, new MapViewSnapshot { Zoom = 3 });
view.Zoom += 1;
```

Disposing the scope releases the view's handle in the browser ([design §3.2](design.md#32-handles-identity-and-lifetime)).

## Your own app

The same steps, with your library and a tool manifest of your own. The packages aren't published, so a `nuget.config` lists `./packages`, the folder you packed them into (step 1), beside nuget.org, as the sample's does:

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

Write a profile to mark value-like classes, events, collections or a selection ([design §4](design.md#4-generator-architecture)), and serve its folder and the metadata module as the sample does.

## In a Razor class library

To ship bindings for apps to reference, put them in a Razor class library (RCL), say `Acme.Maps.Blazor`:

- **Generate into the RCL.** Run `generate` with an `--out` folder in the RCL's project, so the RCL compiles the bindings. Keep that folder out of `wwwroot`, since everything there is served.
- **Serve the metadata module from `wwwroot`.** Copy or link `{package}.meta.js` from the output into the RCL's `wwwroot` after each `generate`. It's then a static web asset at `/_content/Acme.Maps.Blazor/…` (`/_content/Acme.Maps.Blazor/maps.meta.js`, say), and that's the specifier to give `AddInteroper`.
- **Register it for the app.** Expose an extension method, `services.AddAcmeMaps()`, that calls `AddInteroper` with that specifier and the generated registry's `Classes`. The app calls it in the server project and in the WebAssembly client, as the sample does.
- **The host renders the import map.** An RCL can't add to the app's import map itself, so the app's `App.razor` still renders `<ImportMap>`. The RCL can offer a helper that returns the registry's `ImportMap(root)` entries for the app to combine with its own, as the sample's `App.razor` does.
- **The library's own dependencies.** `ImportMap(root)` maps only the bound packages' modules. When the library's JS imports other packages by bare specifiers (a helper library, a date library), add import-map entries for those too, or serve a bundle or a CDN build that has them resolved ([design §3.5](design.md#35-module-loading)).
- **One `generate` run per app.** An app has one realm, and each `generate` run numbers its classes from 1 ([design §2](design.md#2-what-we-generate)), so two sets of bindings generated apart can't share it. Bind every library an app uses in one run (`generate @acme/maps @acme/widgets …`), which also lets their types refer to each other.
