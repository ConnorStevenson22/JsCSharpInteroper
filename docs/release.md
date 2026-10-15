# Release checklist

What runs before a version of the three packages (`Interoper.Runtime`, `Interoper.Blazor`, `Interoper.Tool`) is tagged. They aren't on nuget.org yet, so a release is a tagged commit and the packages packed from it.

## 1. The checks

On the commit to release, with nothing uncommitted:

- Every command in [CLAUDE.md](../CLAUDE.md#commands), all passing: the build, `dotnet test Interoper.slnx`, `dotnet test tests/Interoper.E2E` (the browser tests, including the latency-throttled Server tests, the size budget and the quickstart), `dotnet format --verify-no-changes` and the JS tests.
- `tsc --noEmit -p fixtures/demo-maps` and `-p fixtures/demo-widgets`, with the extractor's TypeScript (`src/interoper-extractor/node_modules/.bin/tsc`): the fixtures' typings still compile ([PATTERNS.md](../fixtures/demo-maps/PATTERNS.md)).

## 2. Version and pack

1. Set `Version` in `Directory.Build.props`; the three packages share it ([design §5](design.md#5-consumer-workflow-and-packages)).
2. Pack them, and the fixture for step 3, with [quickstart §1](quickstart.md#1-the-packages)'s commands, into a folder outside the repo:

   ```bash
   dotnet pack src/Interoper.Runtime -o ../interoper-packages
   dotnet pack src/Interoper.Blazor -o ../interoper-packages
   dotnet pack src/Interoper.Tool -o ../interoper-packages
   npm pack ./fixtures/demo-maps --pack-destination ../interoper-packages
   ```

## 3. Hybrid, by hand on Windows

Hybrid hosts don't run on Linux, so no test drives one ([design §6](design.md#6-testing)); its code path, `JsRuntimeTransport` over an async bridge, is the one the Server tests cover. This check runs it once on a real `BlazorWebView`, with the packages from step 2:

1. A WPF app (`dotnet new wpf`) with `Microsoft.AspNetCore.Components.WebView.Wpf` and a `BlazorWebView` whose host page is `wwwroot/index.html`, or a MAUI Blazor app (`dotnet new maui-blazor`). Its `nuget.config` lists the packages' folder; NuGet caches by version, so clear `~/.nuget/packages/interoper.*/<version>` and `~/.dotnet/toolResolverCache/1/interoper.tool` first if that version was restored before ([quickstart §1](quickstart.md#1-the-packages)).
2. `npm install` the packed fixture and `typescript@6.0.3`, `dotnet tool install Interoper.Tool --prerelease` (in a tool manifest) and `dotnet add package Interoper.Blazor --prerelease`, then generate the async bindings: `dotnet interoper generate @acme/demo-maps --api async --namespace Hybrid.Maps --out Maps --profile node_modules/@acme/demo-maps/profile.json`.
3. Copy `node_modules/@acme/demo-maps` to `wwwroot/node_modules/@acme/demo-maps` and the generated `demo-maps.meta.js` into `wwwroot`. `index.html` is static, so write its `<script type="importmap">` by hand, with the entries `Hybrid.Maps.DemoMaps.ImportMap("/node_modules")` returns ([design §3.5](design.md#35-module-loading)).
4. Register the realm with `AddInteroper("/demo-maps.meta.js", Hybrid.Maps.DemoMaps.Classes)` and leave `Interop:Transport` at `Auto`. In a component:
   - **zoom:** `MapView.CreateAsync` with `Zoom = 3`, `SetZoomAsync(4)`, then `GetZoomAsync()` is 4;
   - **one event:** `WatchZoomAsync`, then `SetZoomAsync(5)`, and the handler gets 5;
   - **one batch:** five writes in `await using (scope.BeginBatch())` raise `Realm.RoundTrips` by exactly 1;
   - **census:** `Realm.Transport.CensusAsync()` after `await Realm.StartAsync()` and before the first `CreateAsync`, and again after the scope is disposed (the subscription with it), is the same number.
5. Show the four results on the page and note them, with the Windows and WebView2 versions, in the release's notes. Any failure blocks the release and gets a test in this repo where one can reproduce it.
