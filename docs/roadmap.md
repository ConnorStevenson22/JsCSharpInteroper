# Implementation roadmap

*Draft 4, October 2026. Builds on the [feasibility report](report.md). Claims marked **verified** come from the runnable [spikes](../spikes) (.NET 11 RC1, Mono WebAssembly, headless Chromium).*

"Interoper" is a working name taken from the repo, and `@acme/maps` is a stand-in for whatever JS library a consumer binds.

---

## 0. Decisions

### Locked (from you)

| # | Decision |
|---|---|
| D1 | **.NET 11+ only.** C# 15 unions, `closed` hierarchies and other new features are fair game. |
| D2 | **Every Blazor host is first-class:** Server, WebAssembly and Hybrid (MAUI/WPF/WinForms `BlazorWebView`). Hybrid takes the `IJSRuntime` path, like Server: .NET runs natively beside the WebView, not inside it (verified, §3.1). |
| D3 | **A generator switch, `--api sync\|async`, decides the shape of every live member.** `sync` gives plain `X` members: `double Zoom { get; set; }`, `MapViewSnapshot Snapshot()`. It runs on WebAssembly only. `async` gives `Task<X>` members with the `Async` suffix: `Task<double> GetZoomAsync()`, `Task SetZoomAsync(double)`, `goTo()` → `GoToAsync()`. It runs on every host. Data types are identical in both modes. JS methods that return Promises are `Task<T>` in both. |
| D4 | **Mono only on WebAssembly.** CoreCLR is off the table while it is slower; revisit when .NET 12 ships. |
| D5 | **We publish only the generator and runtime.** No bindings for any third-party library ship from this repo. Consumers `npm install` their JS library and generate the C# themselves. |
| D6 | **ArcGIS is our private validation target and never enters the repo.** No typings, generated output, profiles or samples. |
| D7 | **Unorthodox is allowed:** we own the wire protocol, the handle table and the value model. |
| D8 | **In async mode the transport is chosen automatically per component, with a config override** (`"Interop": { "Transport": "Auto" \| "JsRuntime" \| "JsImport" }`); see §3.1. |
| D9 | **ArcGIS validation lives in a separate private GitHub repo**, `JsCSharpInteroper-validation`, next to this one (§6). That's the least friction for development: it's durable across ephemeral cloud sessions, versioned, and CI can run it. You've created it; the Claude GitHub app still needs access to it. |
| D10 | **Snapshot / constructor-bag records are named `XSnapshot`** (`MapViewSnapshot`). |
| D11 | **Generation is a command the consumer runs** (`dotnet interoper generate …`). Whether they wire it into a build, run it in CI or commit the output is up to them; we ship no MSBuild integration. |

### Proposed (please confirm or veto)

| # | Proposal | Why |
|---|---|---|
| P1 | **`undefined` is a real union case and the `default`; the `null` literal is JS `null`** (§1) | Your idea, verified: three states, compiler-checked exhaustiveness, unset members omitted on the wire. |
| P3 | **Our own transport seams**: `IJsSyncTransport` for sync mode (`[JSImport]`), and a `Task`-based `IJsTransport` for async mode (`[JSImport]` on WebAssembly, `IJSRuntime` on Server/Hybrid). Both have in-memory versions for tests (§3). | Verified: each mode's generated code runs unchanged on every host that mode supports. |
| P4 | **Batching is first-class in async mode** (`await using (scope.BeginBatch())`) | Verified on Server: ~1 ms per awaited call on localhost, while pipelined writes cost ~50 µs each, in order. |
| P5 | **A fixture JS library of our own lives in the repo** and drives all committed tests and CI (§6) | D6 means CI can't use ArcGIS, so the fixture reproduces the patterns ArcGIS taught us. |
| P6 | **The CLI reads the consumer's `node_modules` directly** (§5) | Satisfies D5 and D11. It also settles the licensing question: a library's own doc comments only ever land in the consumer's output. |
| P7 | **Hand-write a golden slice of the fixture library before writing the generator** (M1) | Design the API by using it, then make the generator reproduce it exactly. |

---

## 1. The value model: JavaScript semantics in C#

Unchanged from draft 1 and verified in [`spikes/value-model`](../spikes/value-model) (7/7 checks).

```csharp
public sealed class Undefined                 // users can't construct it; the runtime owns Undefined.Value
{
    public static readonly Undefined Value = new();
    private Undefined() { }
}

// type ColorInput = string | number[] | RGBA;   used as  color?: ColorInput | null
[Union] public readonly struct ColorInput : IUnion        // generated: non-boxing, tag-based
{
    public ColorInput(string value) ...
    public ColorInput(double[] value) ...
    public ColorInput(Rgba value) ...
    public ColorInput(Undefined? value) ...                // bottom case: null ref = JS null, singleton = undefined
    public static implicit operator ColorInput(Undefined? value) => new(value);  // makes `= null` compile
    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    // HasValue / TryGetValue(out T) for every case -> no boxing in pattern matching
}
```

| You write | It means | On the wire |
|---|---|---|
| *(nothing)* / `default` | `undefined` | property omitted |
| `= null` | JS `null` | `null` |
| `= "red"`, `= new double[] {…}`, `= new Rgba {…}` | value | value |
| `= Undefined.Value` | explicit `undefined` | omitted |

```csharp
string Describe(ColorInput c) => c switch
{
    string css    => css,
    double[] rgba => $"rgba[{rgba.Length}]",
    Rgba r        => $"r={r.R}",
    Undefined     => "use the default",   // omit this arm -> CS8509
    null          => "no color",          // omit this arm -> CS8655
};
```

Generator rules:
1. **Every TypeScript union is a generated `[Union]` struct, not the `union` keyword.** The keyword's `default` is `null`, and it boxes value types.
2. **Non-union types in optional positions use `Opt<T>`**, with the same three states. Required, non-null positions stay plain (`double`).
3. **String-literal unions become `enum`s** with a generated name map. Open-ended or huge literal unions become extensible string-backed structs.
4. **A union of live classes that covers every concrete subclass of an abstract base maps to the base**, generated as a `closed class`. Switches stay exhaustive, including through `Opt<T>` (verified).
5. **Collapse after mapping:** `number[]` and `[number, number, number]` are both `double[]`, so they become one case.

Live getters return these unchanged in sync mode (`Opt<Shape> Shape { get; }`, then `if (feature.Shape is Point p)`) and wrapped in async mode (`Task<Opt<Shape>> GetShapeAsync()`, then `if (await feature.GetShapeAsync() is Point p)`).

---

## 2. What we generate

### K1. Live objects: one generator switch, two shapes

```csharp
// --api sync   (WebAssembly only)                          // --api async   (every host)
using var scope = js.CreateScope();                          await using var scope = js.CreateScope();
var view = MapView.Create(scope, new MapViewSnapshot {       var view = await MapView.CreateAsync(scope, new MapViewSnapshot {
    Container = mapElement, Zoom = 4 });                         Container = mapElement, Zoom = 4 });

view.Zoom = 7;                                               await view.SetZoomAsync(7);
double zoom = view.Zoom;                                     double zoom = await view.GetZoomAsync();
if (feature.Shape is Point p)                                if (await feature.GetShapeAsync() is Point p)
    Console.WriteLine(p.X);                                      Console.WriteLine(await p.GetXAsync());
feature.SetShape(new LineData { Points = [[0, 0], [5, 5]] }); await feature.SetShapeAsync(new LineData { Points = [[0, 0], [5, 5]] });
var hits = await view.HitTestAsync(screenPoint);             var hits = await view.HitTestAsync(screenPoint);   // a Promise is a Task in both modes
```

- **Sync mode** (`X` members) is the fastest option and reads like plain C#. Verified on WebAssembly: ~0.65 µs per property read with 0 bytes allocated, against ~1.1 µs and 48 bytes (the `Task`) in async mode on the same page. It needs .NET and JS on one thread, so it **runs on WebAssembly only**. On Server, Hybrid, or an `InteractiveAuto` component that renders on the server, it fails fast with "regenerate with `--api async`".
- **Async mode** (`Task<X>` members) runs everywhere. On WebAssembly the `Task`s are already completed; on Server and Hybrid each call is a real round trip.
- **Naming.**
  - Sync mode: property `x` → `X { get; set; }`; read-only properties get no setter.
  - Async mode: `GetXAsync()` / `SetXAsync(value)`; method `foo()` → `FooAsync()`.
  - If a JS member would collide with a generated one (`getZoom()` next to `zoom`), the profile renames it. The generator refuses to emit duplicates silently.
- **Getter ≠ setter types** (autocast-style setters that take a live instance *or* a properties bag; 1,553 such accessors in our private target alone):
  - Async mode handles them for free, because `GetXAsync` and `SetXAsync` can take different types.
  - Sync mode types the property by its getter and adds a `SetX(input)` method for the setter's wider type.
- **Identity.** The JS handle table keeps a `WeakMap<object, id>`, so the same JS object always gets the same handle. The .NET side keeps a weak cache per realm, so two reads return *the same C# object*: `==`, dictionaries and `@key` all work.
- **Polymorphism.** JS sends the object's runtime class with each handle, and a generated registry maps it to the most-derived proxy. *Which* property holds the class name is a profile setting: ArcGIS has one, other libraries fall back to `constructor.name`.
- **Creation is one call:** `Create`/`CreateAsync(scope, snapshot)`. Generated constructor parameters, if any, come first.
- **Lifetime.** Handles belong to a `JsScope` (`IDisposable` in sync mode, `IAsyncDisposable` in async mode); components own one per instance. Disposing it releases everything in one call. On Server, a dropped circuit makes all handles void, and disposal swallows `JSDisconnectedException`.

### K2. Snapshots, constructor bags and patches: one record type

```csharp
MapViewSnapshot snap = view.Snapshot();                       // sync mode: one call, every data property
MapViewSnapshot snap = await view.SnapshotAsync();            // async mode: one round trip
await view.ApplyAsync(new MapViewSnapshot { Zoom = 5, Rotation = 15 });  // one round trip; unset = untouched
await view.ApplyAsync(MapViewSnapshot.Diff(before, after));  // generated minimal patch
```

- Because `default` is undefined (P1), **a sparse patch is just a partially filled record**.
- The same type is the constructor input for `Create`/`CreateAsync`, and it's identical in both modes.
- On Server, `SnapshotAsync` + `ApplyAsync` replace N round trips with one each.
- **Depth policy (profile):** value-like classes (geometry, symbols, colors) copy deeply; entities (layers, maps, views) appear as live proxies inside the snapshot.

### K3. Blazor components for custom elements

Generated from the library's Custom Elements Manifest:

```razor
<AcmeMap Basemap="streets" @bind-Zoom="zoom" OnAcmeViewClick="AddPoint" @ref="map">
    <AcmeZoom Slot="top-left" />
</AcmeMap>
```

- Simple values render as **HTML attributes**, so the element appears during Server/Auto prerendering and static SSR. Complex values are set through the transport after the first interactive render.
- **Events go through Blazor's own event pipeline.** We generate `Blazor.registerCustomEventType(...)` registrations and `[EventHandler]` classes, which works the same on all three hosts, even on raw `<acme-map @onacmeviewclick="...">` markup.
- `@bind-X` uses the element's change events or a property watch.
- `await map.GetElementAsync()` returns the live element; library-specific shortcuts (such as the element's view) come from the profile.

### K4. Value types
Enums, extensible enums, generated unions and `Opt<T>` (§1). JSON readers and writers are generated per type: no reflection, trim/AOT-safe.

### K5. Events and watches
- Sync mode: `using var sub = view.OnClick(e => ...)`. Async mode: `await using var sub = await view.OnClickAsync(e => ...)`, or `await foreach (var e in view.OnClickEvents(ct))`.
- `view.Watch(v => v.Zoom)` returns `IAsyncEnumerable<double>`.
- **Server/Hybrid can't call `preventDefault()` or `stopPropagation()` from .NET in time**: the event has already finished by the time .NET sees it. So generated subscriptions take **declarative JS-side options** that run in the browser before the event is forwarded: `OnDragAsync(handler, new() { StopPropagation = true })`.
- **Payload projection:** forward only the fields a handler declares, so high-frequency events (pointer move) stay cheap over SignalR.

### K6. Module functions (stateless, data in → data out)
Library functions that take and return plain data, such as geometry operators, become static methods over records. That's **one round trip with no handles**, the best case on Server.

### K7. Custom-event registrations
A generated JS module plus C# `[EventHandler]` attributes; see K3. It's also useful on its own for hand-written custom-element markup.

### K8. In-memory transport for tests
It emulates the JS object model, with handles mapped to property dictionaries and calls recorded or stubbed. Generated code runs against it unchanged, so unit and bUnit tests need no browser. It can also **record/replay** a real browser session's op log as a golden test.

### K9. Diagnostics
- A per-realm handle census (leaks) and a round-trip counter (chattiness).
- An analyzer that flags, in async mode, N sequential awaits on the same object outside a batch: "use `SnapshotAsync`/`ApplyAsync` or `BeginBatch`".

### K10. The JS half
A hand-written runtime (`interoper.js`: handle table, dispatcher, batches, events, errors) plus **generated metadata tables**:
- class → module specifier;
- runtime class name → class id;
- snapshot member lists;
- event names.

There is no per-member generated JS.

---

## 3. Runtime architecture

### 3.1 Modes, seams and transports (verified in [`spikes/hosts`](../spikes/hosts))

| Generated mode | Seam | WebAssembly | Server | Hybrid | Tests |
|---|---|---|---|---|---|
| `--api sync` | `IJsSyncTransport` | `[JSImport]`: **~0.65 µs per read, 0 B** | fails fast | fails fast | in-memory |
| `--api async` | `IJsTransport` (`Task`) | `[JSImport]`, completed `Task`s: ~1.1 µs, 48 B | `IJSRuntime` over SignalR: ~0.8–1.1 ms per await on localhost; ~50 µs per write pipelined | `IJSRuntime` over the WebView bridge (not run yet; needs Windows/macOS) | in-memory |

JS → .NET goes through one `[JSExport]` method on WebAssembly and one `DotNetObjectReference` per circuit or WebView elsewhere.

- `services.AddInteroper()` registers a scoped `IJsRealm` that provides the right seam for the host.
- Generated code only ever sees the seam for its mode, through the realm and its scopes.

**Choosing the transport in async mode (verified in `spikes/hosts`).** It comes from configuration, `"Interop": { "Transport": "Auto" }`, and is resolved **per component at runtime**:

| Mode | WebAssembly | Server | Hybrid |
|---|---|---|---|
| `Auto` (default) | `[JSImport]` | `IJSRuntime` | `IJSRuntime` |
| `JsRuntime` | `IJSRuntime`: reproduces Server/Hybrid behaviour in the browser, useful for parity tests (~165 µs per call vs ~2 µs) | `IJSRuntime` | `IJSRuntime` |
| `JsImport` | `[JSImport]` | fails fast with guidance | fails fast with guidance |

- **Why Hybrid can't use `[JSImport]`:** in a `BlazorWebView`, .NET runs natively in the app process and JS runs in the WebView, with no shared WebAssembly runtime.
  - Calling `[JSImport]` there throws `PlatformNotSupportedException: System.Runtime.InteropServices.JavaScript is not supported on this platform`.
  - Hybrid's `WebViewJSRuntime` is an async `JSRuntime`, not an `IJSInProcessRuntime`.

  Both were verified.
- **Why per component, not per app:** under `InteractiveAuto`, the same component ran on the server on its first visit and in WebAssembly on the next, and `Auto` picked the matching transport each time (verified). A single app-wide switch would be wrong for one of those visits.

### 3.2 Batching (async mode's Server lever)

```csharp
await using (scope.BeginBatch())
{
    await view.SetZoomAsync(5);        // queued; the Task completes immediately
    await view.SetRotationAsync(15);   // queued
    await layer.SetVisibleAsync(false);
}                                      // one round trip carries all three, in order
```

- Inside a batch, writes and void calls complete immediately and queue.
- **A read flushes the queue first**, so program order is always preserved.
- JS errors surface at the flush as a `JsBatchException` that lists each failed op.
- The same code outside a batch behaves normally. The batch changes only the cost, never the meaning.

### 3.3 Protocol
- **Typed fast paths** for primitives on `[JSImport]` (`getNumber`, `setNumber`, and string, bool and handle variants).
- **UTF-8 JSON** for structured data, using generated writers:
  - undefined members are omitted;
  - `{"$h":12,"$t":7}` is a handle with its class id;
  - `{"$f":3}` is a .NET callback;
  - `{"$u":1}` is undefined in arrays and arguments; the JS `JSON.parse` reviver restores it.
- **Batches** travel as one op array.
- **Errors** become `JsException` with the JS `name`, `message` and stack. The profile can map library error classes to typed subclasses.

### 3.4 Calls into .NET
One entry point per host (verified on both): `[JSExport] Dispatch(int callbackId, string payload)` on WebAssembly, and `[JSInvokable] Dispatch` on one `DotNetObjectReference` on Server/Hybrid. Callbacks are posted to the component's dispatcher, so handlers can call `StateHasChanged`.

### 3.5 Module loading
- Generated code knows only bare specifiers (`@acme/maps/views/MapView.js`). The runtime `import()`s them, and the app's **import map** decides where they come from: Blazor's `<ImportMap />` component, which the .NET 11 templates already include.
- The tooling (§5) can write the import-map entries for a package the consumer serves from `node_modules`. CDN or bundler setups just point the map elsewhere.
- Prerendering: there's no JS realm until the component is interactive. Calling a live member earlier throws `JsRealmUnavailableException`, and generated components defer their work to `OnAfterRenderAsync`.

---

## 4. Generator architecture

```
consumer's node_modules/@acme/maps/*.d.ts ─► extractor (TypeScript 6.x checker) ─┐
consumer's node_modules/@acme/maps/custom-elements.json ─► extractor (CEM) ──────┼─► IR (versioned JSON, cached)
                                                                                 │        │
                                            consumer's profile (optional) ───────┴──► generator ─► *.g.cs  (K1–K9)
                                                                                                ├─► *.g.js  (K10 tables)
                                                                                                └─► *.g.razor.cs (K3)
```

- **Extractor**, the successor to `tools/dts-survey`. It asks the *checker* for resolved members: computed bags, get/set type pairs, overloads, event maps, discriminants, JSDoc and module specifiers. It ships as **one bundled JS file with TypeScript 6.x inside**, so consumers need Node (which npm implies) but no extra npm packages. TypeScript 7.0 has no stable programmatic API, so we stay on 6.x behind the IR.
- **IR.** A language-neutral, versioned JSON model. Upgrading a JS library becomes an IR diff, readable like a changelog.
- **Profile.** Optional, consumer-owned data for every library-specific decision:
  - entity vs value classes;
  - the runtime-class property and discriminators;
  - event conventions and collection types;
  - renames, numeric overrides and skips.

  Good defaults cover what they can; the survey suggests 70–90% of a typical library needs no profile at all.
- **Generator.** A .NET library with a small indented writer (no template engine). `--api sync|async` changes only the live members. All output is `partial`, so consumers can add members beside it.

---

## 5. Consumer workflow (what we publish)

| Package | Contents |
|---|---|
| `Interoper.Runtime` | value model, realm/scope/handles, protocol, both seams and their transports, `interoper.js` as a static web asset |
| `Interoper.Blazor` | `AddInteroper()`, component base class, custom-event plumbing |
| `Interoper.Tool` | the `dotnet interoper` CLI: extractor bundle + generator engine |

The consumer runs one command:

```bash
npm install @acme/maps
dotnet tool install Interoper.Tool
dotnet interoper generate @acme/maps --api async --namespace Acme.Maps --out Generated/AcmeMaps [--profile interop/acme-maps.json]
```

- **Output** is plain `.g.cs`/`.g.js`/`.g.razor.cs` files plus a generation report. What happens next is the consumer's call: commit the output, regenerate in CI, or call the command from their own build target.
- **Caching.** The IR is cached in a local directory, keyed by the package's installed version, the profile hash and the generator version. Re-running after an unrelated change is fast, and large libraries pay the extraction cost once per upgrade.
- **Docs.** JSDoc becomes XML docs *in the consumer's output*. We never redistribute a library's documentation.
- **Diagnostics.** Every mapping fallback (`any`, an unresolvable conditional type, a skipped overload) is reported with a code. The command can fail on warnings (`--warnings-as-errors`) for people who want a strict build.

---

## 6. Validation: the fixture library (public) and ArcGIS (private)

**`fixtures/demo-maps` lives in the repo.** It's a small JS library we write, with hand-written `.d.ts` and a `custom-elements.json`. It deliberately reproduces every hard pattern we've found:

- an abstract base with concrete subclasses and a runtime class-name property;
- getter ≠ setter (instance-or-bag setters);
- discriminated, untagged, mixed and literal unions, including a very large literal union;
- `null | undefined` positions;
- event maps declared through a phantom property and indexed access on `this`;
- `Partial<Pick<…>>` constructor bags;
- Promise methods, callback parameters, a `Collection<T>`, and module-level functions;
- custom elements with attributes, properties, events and slots.

All committed tests run against it: unit (in-memory), golden IR and golden C#, and browser end-to-end on WebAssembly and Server.

**ArcGIS stays outside the repo, in a private companion repo** (suggested name: `JsCSharpInteroper-validation`, private). Layout:

```
JsCSharpInteroper-validation/        (private)
  bootstrap.sh                       npm install of pinned @arcgis/core + @arcgis/map-components (node_modules gitignored)
  profiles/arcgis.json               our private profile
  samples/ArcGisSamples/             Blazor Web App with ported Esri samples, referencing ../JsCSharpInteroper/src/*
  tests/                             Playwright runs on WebAssembly + Server (+ Hybrid on Windows)
  .github/workflows/validate.yml     checks out both repos side by side and runs everything
```

- **Why a repo and not a local folder:** cloud sessions start in a fresh container, so a folder outside git disappears together with the hand-written profile and samples.
- **How it consumes this repo:** project references to a sibling clone during development, and the published NuGet packages once they exist. That also tests the real consumer path.
- **In a cloud session,** both repos are attached and cloned side by side, and a SessionStart hook in each repo installs the .NET 11 SDK. That removes the manual SDK install every new container needed so far.

It installs `@arcgis/core` and `@arcgis/map-components` from npm, keeps our private profile, generates bindings exactly as a consumer would, and runs ported Esri samples. At each milestone we track:
- generation time;
- warning count;
- trimmed app size;
- sample pass rate on WebAssembly and Server, and Hybrid where we can.

---

## 7. Milestones

**M0 · Groundwork**
- Repo layout (§8).
- CI: build, unit tests, and a headless Chromium harness (from `spikes/hosts`) running WebAssembly *and* Server pages.
- `fixtures/demo-maps` v0.
- A SessionStart hook (`.claude/`) that installs the .NET 11 SDK, so every cloud session can build and test immediately.
- The private companion repo (§6): created; I set it up once the Claude GitHub app can see it.
- *Exit:* CI is green with one end-to-end smoke test per host, including an `InteractiveAuto` page that switches transport between visits.

**M1 · Runtime core and golden slice (no generator yet)**
- Value model; `IJsRealm`/`JsScope`/handles with identity and polymorphism; the three transports.
- `interoper.js`; batching; events with JS-side options; snapshots and apply.
- A hand-written slice of the fixture library, in both modes: base class + 3 subclasses, one discriminated union, one enum, one event, one watch, one module function, one Promise method.
- *Exit:*
  - one test suite per mode passes on in-memory and on every host that mode supports;
  - a batch of N writes is exactly one round trip;
  - the handle count returns to baseline after scope disposal;
  - the private ArcGIS check runs one hand-written slice end to end.

**M2 · Extractor and IR**
- The checker-based extractor and the CEM reader; IR schema v1.
- Golden IR for the fixture.
- *Exit:* the IR is byte-identical across runs, for the fixture and the private ArcGIS packages alike.

**M3 · Generator v1: data plane and live objects**
- Value types, snapshot records, live classes in **both modes** (sync properties and async accessors, methods, `closed` bases, proxy registry) and metadata tables.
- *Exit:* the **generated fixture slice replaces the hand-written one with no API changes**, in both modes, and the M1 tests pass unmodified.

**M4 · Behaviour completeness**
- Events and watches (projection, JS-side options), collections, callbacks, Promises, module functions, `Diff`, and the analyzers.
- *Exit:*
  - the full fixture library is generated and passes end to end on both hosts;
  - the private ArcGIS run has the first five samples passing.

**M5 · Components**
- K3/K7 from the CEM: attributes for prerendering, properties after render, Blazor custom events, `@bind`, slots, element access.
- *Exit:* the fixture's components and the private ArcGIS component samples pass on both hosts.

**M6 · Consumer tooling and packages**
- The CLI (`--api`, `--profile`, `--out`, caching, report), import-map helper, XML docs from JSDoc, warnings with codes, and the three NuGet packages.
- *Exit:*
  - `dotnet new blazor` + `npm install <lib>` + one `dotnet interoper generate` command gives working bindings (documented quickstart for both modes);
  - the private full-surface ArcGIS generation has zero errors and a tracked warning budget.

**M7 · Hybrid and latency hardening**
- `BlazorWebView` end-to-end tests on a Windows runner.
- Server tests under simulated latency (Chromium network throttling at 50–150 ms RTT) to tune batching and snapshot defaults.
- *Exit:* the acceptance samples pass on all three hosts within agreed latency budgets.

**M8 · Prove genericity**
- A second, public, permissively licensed library (e.g. Leaflet) bound with **only a profile**: no generator changes, nothing committed but the profile and tests that generate at test time.
- A WebIDL front end; the TypeScript 7.1 API once it's stable.

---

## 8. Proposed repository layout

```
src/
  Interoper.Runtime/            value model, realm/scope/handles, protocol, transports, interoper.js
  Interoper.Blazor/             AddInteroper(), component base, custom-event plumbing
  Interoper.Generator/          IR model + C#/JS/Razor emitters (a library the CLI uses)
  Interoper.Tool/               dotnet CLI, bundles the extractor
extractor/                      TypeScript 6.x checker -> IR, bundled into one JS file
fixtures/demo-maps/             our own JS library + .d.ts + custom-elements.json (+ profile)
tests/
  Interoper.Runtime.Tests/      in-memory transport
  Interoper.Generator.Tests/    golden IR / golden C# for the fixture
  Interoper.E2E/                Playwright: WebAssembly + Server (Hybrid on Windows, M7)
tools/dts-survey/
spikes/
```

Nothing under any of these paths contains or is generated from ArcGIS.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Server chattiness (~1 ms+ per await) | Batching, snapshot/apply, module functions, an analyzer for sequential awaits, and latency-simulated CI (M7) |
| Sync-mode bindings used where they can't run (Server, Hybrid, `InteractiveAuto`) | Fail fast with "regenerate with `--api async`"; the docs call it out in the quickstart |
| JS events can't be cancelled from .NET on Server/Hybrid | Declarative JS-side event options (K5) |
| Hybrid untested in Linux CI | Same transport as Server; Windows runner in M7 |
| The union feature still evolves before GA | Pin the SDK; the spikes double as regression tests; custom `[Union]` structs use the stable core |
| Consumer machines without Node | Node is implied by `npm install`; the CLI checks for it and explains what's missing |
| Generation time for huge libraries | IR cache keyed by package version and profile; measured on the private target from M2 |
| TypeScript 7 replaces the 6.x API | The IR isolates the extractor; migrate in M8 |

---

## 10. Questions to confirm

1. **Sync-mode shape:** I generate real properties (`view.Zoom = 7`) rather than `GetZoom()`/`SetZoom()` methods, since the type is plain `X` there. OK?
2. **Hybrid CI:** do you have, or want, a Windows runner for `BlazorWebView` tests?
3. **Private repo access:** this session can't see `JsCSharpInteroper-validation` yet. Add it to the Claude GitHub app's repository access (https://claude.ai/connect-github → configure the GitHub app → repository access), or start a session with both repos selected.
