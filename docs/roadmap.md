# Roadmap

This is the plan for **Interoper**, a generator and runtime that turn a JavaScript library's TypeScript typings into C# for Blazor. The type-mapping rules are in [mapping.md](mapping.md); the delegable tasks are in [work-packages.md](work-packages.md).

Claims marked **verified** come from the runnable [spikes](../spikes) (.NET 11 RC1, Mono WebAssembly, headless Chromium). `@acme/maps` stands in for whatever JS library a consumer binds.

---

## 1. Decisions

| # | Decision |
|---|---|
| D1 | **.NET 11+ only.** C# 15 unions, `closed` hierarchies and other new features are fair game. The SDK is pinned in `global.json`. |
| D2 | **Every Blazor host is supported:** Server, WebAssembly and Hybrid (MAUI/WPF/WinForms `BlazorWebView`). Hybrid works like Server: .NET runs natively beside the WebView and talks to it through an async bridge (verified). |
| D3 | **A generator switch, `--api sync\|async`, decides the shape of every live member.** `sync`: plain members (`double Zoom { get; set; }`), WebAssembly only. `async`: `Task<X>` members with the `Async` suffix (`GetZoomAsync()`, `SetZoomAsync(…)`), every host. Data types are identical in both modes, and JS Promises are `Task<T>` in both. See [mapping.md §5](mapping.md#5-members). |
| D4 | **Mono on WebAssembly.** CoreCLR-on-WASM is slower today; revisit with .NET 12. |
| D5 | **We publish only the generator and runtime.** No bindings for any third-party library ship from this repo. |
| D6 | **Consumers generate their own bindings** by running a command against their `node_modules`. Wiring it into a build, running it in CI or committing the output is up to them; there is no MSBuild integration. |
| D7 | **ArcGIS is our private validation target and never enters this repo**: no typings, generated output, profiles or samples. It lives in the private companion repo `JsCSharpInteroper-validation`. |
| D8 | **We own the wire protocol, the handle table and the value model** (`undefined` as a union case and the `default`, `null` as JS null). See [mapping.md §1](mapping.md#1-the-value-model). |
| D9 | **Two transport seams.** `IJsSyncTransport` serves sync mode (`[JSImport]`). The `Task`-based `IJsTransport` serves async mode: `[JSImport]` on WebAssembly, `IJSRuntime` on Server/Hybrid. Both have in-memory versions for tests. In async mode the transport is chosen per component (`"Interop": { "Transport": "Auto" \| "JsRuntime" \| "JsImport" }`). |
| D10 | **Batching, snapshots and module functions are first-class**, because each awaited call on Server is a full round trip. |
| D11 | **Records for snapshots and constructor bags are named `XSnapshot`** (`MapViewSnapshot`). |
| D12 | **Tests run on an in-repo fixture library we write** (`fixtures/demo-maps`) that reproduces every hard pattern. CI is Linux only; there is no Windows runner. |
| D13 | **Tooling:** xUnit v3 for .NET tests, Microsoft.Playwright (.NET) for browser tests, `node --test` for `interoper.js` and the fixture, and `dotnet format` as the formatter. |

---

## 2. What we generate

| Kind | What it is |
|---|---|
| **Live objects** | A class per JS class, wrapping a handle. Members follow D3. The same JS object always maps to the same C# object (identity), and JS reports the runtime class so you get the most-derived type (`layer is FeatureLayer`). Created with `Create`/`CreateAsync(scope, XSnapshot)`; owned by a `JsScope`. |
| **Snapshots** | `XSnapshot` records: one call reads every data member, the same record is the constructor input, and a partially filled record is a patch (`Apply`/`ApplyAsync`, generated `Diff`). |
| **Value types** | Enums, extensible enums, generated unions and `Opt<T>`, with per-type JSON readers and writers (no reflection, trim/AOT-safe). |
| **Events and watches** | Typed subscriptions per event from the library's event maps, with JS-side options (`PreventDefault`, `StopPropagation`, payload projection). Async mode also gets `IAsyncEnumerable` streams for events and property watches. |
| **Module functions** | Stateless library functions over snapshot records: data in, data out, one call, no handles. |
| **Blazor components** | One per custom element in the library's Custom Elements Manifest. Simple values render as HTML attributes, so they work during prerender and SSR; complex values are set after the first interactive render. Events go through Blazor's own pipeline via generated `registerCustomEventType` + `[EventHandler]`, which works on all hosts. Supports `@bind-X` and slots. |
| **Custom-event registrations** | The JS + C# registrations above, usable on hand-written custom-element markup too. |
| **JS metadata tables** | class → module specifier, runtime class name → class id, snapshot member lists, event names. There is no per-member generated JS. |

The runtime also provides:
- an **in-memory transport** that emulates the JS object model, so unit and bUnit tests need no browser, with record/replay of browser op logs;
- **diagnostics**: a handle census for leaks, a round-trip counter, and an analyzer for chatty sequential awaits.

---

## 3. Runtime architecture

### 3.1 Modes, seams and transports (verified in [`spikes/hosts`](../spikes/hosts))

| Mode | Seam | WebAssembly | Server | Hybrid |
|---|---|---|---|---|
| `sync` | `IJsSyncTransport` | `[JSImport]`: ~0.65 µs per read, 0 B | fails fast ("regenerate with `--api async`") | fails fast |
| `async` | `IJsTransport` | `[JSImport]`, completed `Task`s: ~1.1 µs, 48 B | `IJSRuntime` over SignalR: ~1 ms per await on localhost, ~50 µs per pipelined write | `IJSRuntime` over the WebView bridge |

- **JS → .NET** goes through one `[JSExport]` entry point on WebAssembly, and one `DotNetObjectReference` per circuit or WebView elsewhere. Both were verified. Callbacks are posted to the component's dispatcher.
- **Registration.** `services.AddInteroper()` registers a scoped `IJsRealm` that provides the right seam.
- **Transport per component.** In async mode the transport is resolved per component: under `InteractiveAuto` the same component moved from Server to WebAssembly between visits, and `Auto` followed it (verified). Forcing `JsRuntime` inside WebAssembly reproduces the Server/Hybrid code path in the browser; that's how Hybrid is covered without a Windows runner (§6).

### 3.2 Handles, identity and lifetime
- **The JS side** keeps `Map<id, object>` plus `WeakMap<object, id>`, so one JS object always has one id. Every handle carries the object's class id.
- **The .NET side** keeps a weak cache per realm, so the same id gives the same C# object; a generated registry maps class ids to live types.
- **Scopes.** Handles belong to a `JsScope` (`IDisposable` in sync mode, `IAsyncDisposable` in async mode); components own one each. Disposal releases every handle in one call. On Server, a dropped circuit voids all handles, and disposal swallows `JSDisconnectedException`.
- **Prerendering.** Before a component is interactive there's no JS realm; live calls throw `JsRealmUnavailableException`.

### 3.3 Batching (async mode)

```csharp
await using (scope.BeginBatch())
{
    await view.SetZoomAsync(5);        // queued; the Task completes immediately
    await view.SetRotationAsync(15);   // queued
}                                      // one round trip carries both, in order
```

- Inside a batch, writes and void calls queue up.
- A read flushes the queue first, so program order is preserved.
- JS errors surface at the flush as `JsBatchException`, listing each failed op.

### 3.4 Protocol
- **Typed fast paths** on `[JSImport]` for primitives (number, string, bool, handle).
- **UTF-8 JSON** for structured data, with generated readers and writers:
  - undefined members are omitted;
  - `{"$h":12,"$t":7}` is a handle with its class id;
  - `{"$f":3}` is a .NET callback;
  - `{"$u":1}` is undefined inside arrays and arguments; the `JSON.parse` reviver restores it.
- **Batches** travel as one op array.
- **Errors** become `JsException` with the JS name, message and stack. The profile can map library error classes to typed subclasses.

### 3.5 Module loading
Generated code knows only bare specifiers (`@acme/maps/views/MapView.js`). The runtime `import()`s them, and the app's import map (Blazor's `<ImportMap />`) decides where they come from: `node_modules` copied into `wwwroot`, a CDN, or a bundle. The CLI can print the import-map entries for a package.

---

## 4. Generator architecture

```
node_modules/@acme/maps/*.d.ts ──────────► extractor (TypeScript 6.x checker) ─┐
node_modules/@acme/maps/custom-elements.json ─► extractor (CEM) ───────────────┼─► IR (versioned JSON, cached)
                                                                               │        │
                                        profile (optional, consumer-owned) ────┴──► generator --api sync|async
                                                                                         ├─► *.g.cs       live objects, snapshots, value types, events, module functions
                                                                                         ├─► *.g.js       metadata tables, custom-event registrations
                                                                                         └─► *.g.razor.cs components
```

- **Extractor.** Node + the TypeScript 6.x checker, bundled into one JS file that ships inside the CLI. It resolves types with the checker, not a syntax walk ([mapping.md §7](mapping.md#7-what-the-extractor-must-resolve-with-the-checker)). We stay on 6.x until TypeScript 7 has a stable programmatic API. `tools/dts-survey` is its starting point.
- **IR.** A language-neutral, versioned JSON model. A library upgrade is an IR diff.
- **Profile.** Optional, consumer-owned data for library-specific decisions:
  - entity vs value classes;
  - the runtime-class property and discriminators;
  - event conventions and collection types;
  - renames, numeric overrides, skips and JS-side callback implementations.

  Good defaults cover most of a typical library.
- **Generator.** A .NET library with a small indented writer. All output is `partial`.

---

## 5. Consumer workflow and packages

```bash
npm install @acme/maps
dotnet tool install Interoper.Tool
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps [--profile acme-maps.json]
```

| Package | Contents |
|---|---|
| `Interoper.Runtime` | value model, realm/scope/handles, protocol, both seams and their transports, `interoper.js` (static web asset) |
| `Interoper.Blazor` | `AddInteroper()`, component base class, custom-event plumbing |
| `Interoper.Tool` | the `dotnet interoper` CLI: bundled extractor + generator |

- The output is plain files plus a report.
- The IR cache is keyed by package version, profile hash and generator version.
- JSDoc becomes XML docs in the consumer's output; we never redistribute a library's docs.
- Fallbacks are reported with codes ([mapping.md §8](mapping.md#8-diagnostics)), and `--warnings-as-errors` fails the run on them.

---

## 6. Validation

**Public: `fixtures/demo-maps`.** A small JS library we write ourselves (ES modules + hand-written `.d.ts` + `custom-elements.json` + a profile). It covers:
- an abstract base with subclasses and a runtime class-name property;
- getter ≠ setter;
- discriminated, untagged, mixed and literal unions (including a 100+ member one);
- `null | undefined` positions;
- event maps through phantom properties;
- `Partial<Pick<…>>` constructor bags;
- Promise methods, callbacks (including one with a synchronous return value), a collection type, module functions;
- custom elements with attributes, properties, events and slots.

All committed tests use it.

**Host coverage without a Windows runner:**

| Host | How it's tested |
|---|---|
| WebAssembly | Playwright, both modes |
| Server | Playwright, async mode |
| Hybrid | Its code path, `JsRuntimeTransport` over an async bridge, is exercised by the Server tests, and by WebAssembly tests with `Transport=JsRuntime`. A short manual smoke checklist runs on a real `BlazorWebView` before releases. |
| Latency | Server tests under Chromium network throttling (50–150 ms RTT) tune batching and snapshot defaults. |

**Private: `JsCSharpInteroper-validation`** (ArcGIS). It installs pinned `@arcgis/core` + `@arcgis/map-components` from npm, keeps the private profile, generates bindings exactly as a consumer would, and runs ported Esri samples against a sibling clone of this repo. Per milestone we track generation time, warning count, trimmed app size and sample pass rate.

---

## 7. Milestones

Each milestone is broken into delegable tasks in [work-packages.md](work-packages.md).

| Milestone | Scope | Exit criteria |
|---|---|---|
| **M0 · Groundwork** | Solution and project scaffold, CI, browser test harness, fixture library v0, validation repo bootstrap | CI green on Linux: build, format check, unit tests, one browser smoke test per mode/host |
| **M1 · Runtime core** | Value model, both seams and all transports, `interoper.js`, realm/scope/identity/polymorphism, batching, events, snapshots; a **hand-written golden slice** of the fixture in both modes | One test suite per mode passes in-memory and on every host that mode supports; a batch of N writes is one round trip; handle count returns to baseline after scope disposal |
| **M2 · Extractor and IR** | Checker-based extractor, CEM reader, IR schema v1, golden IR | The IR is byte-identical across runs, for the fixture and for the private ArcGIS packages |
| **M3 · Generator v1** | Value types, snapshot records, live classes in both modes, metadata tables | The generated fixture slice replaces the hand-written one with no API changes; the M1 tests pass unmodified |
| **M4 · Behaviour** | Events/watches, collections, callbacks, Promises, module functions, `Diff`, analyzers | The whole fixture is generated and passes on all hosts; the first five private ArcGIS samples pass |
| **M5 · Components** | Components and custom events from the CEM | The fixture components and the private ArcGIS component samples pass |
| **M6 · CLI and packages** | `dotnet interoper generate`, caching, report, import-map helper, XML docs, three NuGet packages | Documented quickstart (`npm install` + one command) works for both modes; private full-surface ArcGIS generation has zero errors and a tracked warning budget |
| **M7 · Hardening** | Latency-throttled Server tests, size/trim budget, Hybrid manual checklist | Acceptance samples meet the agreed latency and size budgets |
| **M8 · Genericity** | A second, permissively licensed public library bound with only a profile; WebIDL front end; TypeScript 7 API when stable | The second library works with no generator changes |

---

## 8. Repository layout (target)

```
src/
  Interoper.Runtime/            value model, realm/scope/handles, protocol, seams + transports, wwwroot/interoper.js
  Interoper.Blazor/             AddInteroper(), component base, custom-event plumbing
  Interoper.Generator/          IR model + emitters (a library the CLI uses)
  Interoper.Tool/               dotnet CLI, bundles the extractor
extractor/                      TypeScript 6.x checker -> IR (Node), bundled into one file
fixtures/demo-maps/             our JS library + .d.ts + custom-elements.json + profile
tests/
  Interoper.Runtime.Tests/      in-memory transport, value model
  Interoper.Generator.Tests/    golden IR / golden C#
  Interoper.E2E/                Playwright: WebAssembly (both modes) + Server; test host app
  GoldenSlice/                  hand-written bindings for the fixture (M1); the generator's target in M3
  interoper-js/                 node --test suites for interoper.js
spikes/                         experiments backing these decisions (reference only)
tools/dts-survey/               measures typings; seed for the extractor
```

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Server chattiness (~1 ms+ per await) | Batching, snapshots, module functions, the chatty-await analyzer, latency-throttled tests |
| JS events can't be cancelled from .NET on Server/Hybrid | Declarative JS-side event options |
| Synchronous-result callbacks impossible in async mode | Diagnostic IOP2005; JS-side implementations via profile |
| Sync-mode bindings used on Server/Hybrid/`InteractiveAuto` | Fail fast with "regenerate with `--api async`"; called out in the quickstart |
| Hybrid not run in CI | Same transport as Server, exercised in-browser with `Transport=JsRuntime`; manual checklist before releases |
| The union feature still evolves before .NET 11 GA (November 2026) | SDK pinned; the spikes double as regression checks; custom `[Union]` structs use the stable core |
| Generation time for huge libraries | IR cache; measured on the private target from M2 |
| TypeScript 7 replaces the 6.x API | The IR isolates the extractor; migrate in M8 |
