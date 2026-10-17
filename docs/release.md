# Release checklist

Run this before tagging a version of `Interoper.Runtime`, `Interoper.Blazor` and `Interoper.Tool`. The packages aren't published, so a release is a tagged commit plus the packages packed from it.

## 1. The checks

On the commit to release, with nothing uncommitted:

- Run every command in [CLAUDE.md](../CLAUDE.md#commands). All must pass, including the browser tests in `tests/Interoper.E2E`.
- Check the fixtures' typings still compile with the extractor's TypeScript:

  ```bash
  src/interoper-extractor/node_modules/.bin/tsc --noEmit -p fixtures/demo-maps
  src/interoper-extractor/node_modules/.bin/tsc --noEmit -p fixtures/demo-widgets
  ```

## 2. Version and pack

1. Set `Version` in `Directory.Build.props`. All three packages share it. Set the same version in:
   - `samples/Quickstart/.config/dotnet-tools.json`
   - `samples/Quickstart/Quickstart/Quickstart.csproj`
   - `samples/Quickstart/Quickstart.Client/Quickstart.Client.csproj`
   - the packages line in `README.md`
2. Pack the packages and the fixture into a folder outside the repo:

   ```bash
   dotnet pack src/Interoper.Runtime -o ../interoper-packages
   dotnet pack src/Interoper.Blazor -o ../interoper-packages
   dotnet pack src/Interoper.Tool -o ../interoper-packages
   npm pack ./fixtures/demo-maps --pack-destination ../interoper-packages
   ```

## 3. Hybrid, by hand on Windows

Tests cover Hybrid's code path but not a real `BlazorWebView`, which needs Windows ([design §6](design.md#6-testing)). Check one by hand with the packages from step 2:

1. Create a WPF app (`dotnet new wpf`) with `Microsoft.AspNetCore.Components.WebView.Wpf` and a `BlazorWebView` whose host page is `wwwroot/index.html`. A MAUI Blazor app (`dotnet new maui-blazor`) works too. Add the packages' folder to its `nuget.config`. If you restored this version before, clear the caches first ([quickstart §1](quickstart.md#1-the-packages)).
2. Install and generate:

   ```bash
   npm install path/to/interoper-packages/acme-demo-maps-0.0.0.tgz
   npm install -D typescript@6.0.3
   dotnet new tool-manifest && dotnet tool install Interoper.Tool --prerelease
   dotnet add package Interoper.Blazor --prerelease
   dotnet interoper generate @acme/demo-maps --api async --namespace Hybrid.Maps --out Maps --profile node_modules/@acme/demo-maps/profile.json
   ```

3. Copy `node_modules/@acme/demo-maps` to `wwwroot/node_modules/@acme/demo-maps`, and the generated `demo-maps.meta.js` to `wwwroot`. Write the `<script type="importmap">` in `index.html` by hand, using the entries from `Hybrid.Maps.DemoMaps.ImportMap("/node_modules")` ([design §3.5](design.md#35-module-loading)).
4. Register with `AddInteroper("/demo-maps.meta.js", Hybrid.Maps.DemoMaps.Classes)` and leave `Interop:Transport` at `Auto`. In a component, check:
   - Zoom: `MapView.CreateAsync` with `Zoom = 3`, then `SetZoomAsync(4)`. `GetZoomAsync()` returns 4.
   - Events: `WatchZoomAsync`, then `SetZoomAsync(5)`. The handler gets 5.
   - Batching: five writes inside `await using (scope.BeginBatch())` raise `Realm.RoundTrips` by exactly 1.
   - Cleanup: `Realm.Transport.CensusAsync()` after `await Realm.StartAsync()` and before the first `CreateAsync` returns the same number as after the scope is disposed.
5. Show the four results on the page. Note them in the release notes, with the Windows and WebView2 versions. Any failure blocks the release and should get a test here if it can be reproduced.
