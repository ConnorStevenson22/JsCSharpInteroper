# Design

What **Interoper** is and how it works. It is a generator and runtime that turn a JavaScript library's TypeScript typings into C# for Blazor. The type-mapping rules are in [mapping.md](mapping.md), and the build order is in [plan.md](plan.md).

Claims marked **verified** come from two throwaway experiments, summarized in [§8](#8-evidence). `@acme/maps` stands in for whatever JS library a consumer binds.

---

## 1. Decisions

- **.NET 11+ only.** C# 15 unions, `closed` hierarchies and other new features are fair game. The SDK is pinned in `global.json`. On WebAssembly we use Mono (the default); CoreCLR-on-WASM is slower today.
- **Every Blazor host is supported:** Server, WebAssembly and Hybrid (MAUI/WPF/WinForms `BlazorWebView`). Hybrid works like Server: .NET runs natively beside the WebView and talks to it through an async bridge (verified).
- **A generator switch, `--api sync|async`, decides the shape of every live member.**
  - `sync`: plain members (`double Zoom { get; set; }`), WebAssembly only.
  - `async`: `Task<X>` members with the `Async` suffix (`GetZoomAsync()`, `SetZoomAsync(…)`), every host.
  - Data types are identical in both modes, and JS Promises are `Task<T>` in both. See [mapping.md §5](mapping.md#5-members).
- **We own the value model, the wire protocol and the handle table.** `undefined` is a union case and the `default`; `null` is JS null. See [mapping.md §1](mapping.md#1-the-value-model).
- **We publish only the generator and runtime.** Consumers generate their own bindings by running one command against their `node_modules`; there is no MSBuild integration. No third-party library code, typings or bindings ship from this repo. ArcGIS is our private validation target and lives only in the private `JsCSharpInteroper-validation` repo.
- **Tests run on a fixture library we write** (`fixtures/demo-maps`) that reproduces every hard pattern (§6).

---

## 2. What we generate

| Kind | What it is |
|---|---|
| **Live objects** | A class per JS class, wrapping a handle. Member shape follows the `--api` switch. The same JS object always maps to the same C# object (identity), and JS reports the runtime class so you get the most-derived type (`shape is Polygon`). Created with `Create`/`CreateAsync(scope, XSnapshot)`; owned by a `JsScope`. |
| **Snapshots** | `XSnapshot` records (`MapViewSnapshot`): one call reads every data member, the same record is the constructor input, and a partially filled record is a patch (`Apply`/`ApplyAsync`, generated `Diff`). |
| **Value types** | Enums, extensible enums, generated unions and `Opt<T>`, with per-type JSON readers and writers (no reflection, trim/AOT-safe). |
| **Events and watches** | Typed subscriptions per event from the library's event maps, with JS-side options (`PreventDefault`, `StopPropagation`, payload projection). Async mode also gets `IAsyncEnumerable` streams for events and property watches. |
| **Module functions** | Stateless library functions over snapshot records: data in, data out, one call, no handles. |
| **Blazor components** | One per custom element in the library's Custom Elements Manifest. Simple values render as HTML attributes, so they work during prerender and SSR; complex values are set after the first interactive render. Events go through Blazor's own pipeline via generated `registerCustomEventType` + `[EventHandler]`, which works on all hosts. Supports `@bind-X` and slots. |
| **Custom-event registrations** | The JS + C# registrations above, usable on hand-written custom-element markup too. |
| **JS metadata tables** | class id → module specifier and export name, runtime class name → class id, snapshot member lists, event names. There is no per-member generated JS. |

The runtime also provides:
- an **in-memory transport** that emulates the JS object model, so unit and bUnit tests need no browser, with record/replay of browser op logs;
- **diagnostics**: a handle census for leaks, a round-trip counter, and an analyzer for chatty sequential awaits.

Batching, snapshots and module functions are first-class because each awaited call on Server is a full round trip.

---

## 3. Runtime architecture

### 3.1 Modes, seams and transports (verified, [§8](#8-evidence))

| Mode | Seam | WebAssembly | Server | Hybrid |
|---|---|---|---|---|
| `sync` | `IJsSyncTransport` | `[JSImport]`: ~0.65 µs per read, 0 B | fails fast ("regenerate with `--api async`") | fails fast |
| `async` | `IJsTransport` | `[JSImport]`, completed `Task`s: ~1.1 µs, 48 B | `IJSRuntime` over SignalR: ~1 ms per await on localhost, ~50 µs per pipelined write | `IJSRuntime` over the WebView bridge |

- **Both seams have in-memory versions** for tests.
- **JS → .NET** goes through one `[JSExport]` entry point on WebAssembly, and one `DotNetObjectReference` per circuit or WebView elsewhere. Both were verified. Callbacks are posted to the component's dispatcher.
- **Registration.** `services.AddInteroper()` registers a scoped `IJsRealm` that provides the right seam.
- **Transport per component.** In async mode the transport is resolved per component (`"Interop": { "Transport": "Auto" | "JsRuntime" | "JsImport" }`). Under `InteractiveAuto` the same component moved from Server to WebAssembly between visits, and `Auto` followed it (verified). Forcing `JsRuntime` inside WebAssembly reproduces the Server/Hybrid code path in the browser, which is how Hybrid gets tested without Windows (§6).

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
  - `{"$c":3, …}` is a value-like class instance (R16): writers tag every `XSnapshot` of a value-like class with its concrete class id, and the reviver builds `new Class(rest)`, so libraries don't need to autocast;
  - `{"$f":3}` is a .NET callback;
  - `{"$u":1}` is undefined inside arrays and arguments; the `JSON.parse` reviver restores it.
- **Batches** travel as one op array.
- **Errors** become `JsException` with the JS name, message and stack. The profile can map library error classes to typed subclasses.

### 3.5 Module loading
Generated code knows only bare specifiers (`@acme/maps/views/MapView.js`). The runtime `import()`s them, and the app's import map (Blazor's `<ImportMap />`) decides where they come from: `node_modules` copied into `wwwroot`, a CDN, or a bundle. The CLI can print the import-map entries for a package. The modules of value-like classes are imported when the realm starts, so the reviver can construct them synchronously (sync mode included).

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
- The runtime's public types live in the `Interoper` namespace (`Undefined`, `Opt<T>`, `JsJson`), whatever the assembly.
- The IR cache is keyed by package version, profile hash and generator version.
- JSDoc becomes XML docs in the consumer's output; we never redistribute a library's docs.
- Fallbacks are reported with codes ([mapping.md §8](mapping.md#8-diagnostics)), and `--warnings-as-errors` fails the run on them.

---

## 6. Testing

**`fixtures/demo-maps`** is a small JS library we write ourselves (ES modules + hand-written `.d.ts` + `custom-elements.json` + a profile). It covers:
- an abstract base with subclasses and a runtime class-name property;
- getter ≠ setter;
- discriminated, untagged, mixed and literal unions (including a 100+ member one);
- `null | undefined` positions;
- event maps through phantom properties;
- `Partial<Pick<…>>` constructor bags;
- Promise methods, callbacks (including one with a synchronous return value), a collection type, module functions;
- custom elements with attributes, properties, events and slots.

All committed tests use it.

| Host | How it's tested |
|---|---|
| WebAssembly | Playwright, both modes |
| Server | Playwright, async mode |
| Hybrid | Its code path (`JsRuntimeTransport` over an async bridge) is exercised by the Server tests, and by WebAssembly tests with `Transport=JsRuntime`. A short manual smoke check on a real `BlazorWebView` runs before releases. |

The private validation repo generates bindings for ArcGIS exactly as a consumer would, against a sibling clone of this repo.

---

## 7. Risks

- **Server chattiness** (~1 ms+ per await). Mitigated by batching, snapshots, module functions, the chatty-await analyzer and latency-throttled tests.
- **Things async mode can't do.** JS events can't be cancelled from .NET, and callbacks can't return a value synchronously. Mitigated by declarative JS-side event options, diagnostic IOP2005 and JS-side implementations via the profile.
- **Sync-mode bindings used on Server, Hybrid or `InteractiveAuto`.** They fail fast with "regenerate with `--api async`", and the quickstart calls it out.
- **The C# union feature may still change before .NET 11 GA** (November 2026). The SDK is pinned, the value-model tests (ported in phase 1) catch regressions, and our `[Union]` structs use only the stable core.

---

## 8. Evidence

Two experiments settled the riskiest decisions before phase 1. Their code was deleted once its results were captured here; it is still in history at [`7d48a23/spikes`](https://github.com/ConnorStevenson22/JsCSharpInteroper/tree/7d48a23/spikes) for porting.

**Value model** (.NET 11 RC1). All seven checks passed, and they passed again on the pinned SDK before the code was deleted; they now run as tests in `tests/Interoper.Runtime.Tests`. The checks covered `undefined` as a union case and the `default`, the `null` literal as JS null, exhaustive switches, `Opt<T>`, the wire format (`{"color":[255,0,0,1],"size":null}` with STJ `WhenWritingDefault`) and `closed` proxy hierarchies. Two compiler facts shaped [mapping.md §1](mapping.md#1-the-value-model):
- `null` never takes a union conversion (`ColorInput c = null;` is CS0037), so the generated `implicit operator X(Undefined? value)` is what makes the literal compile.
- The bottom case must be the nullable `Undefined?`. With a non-nullable `Undefined`, a switch that misses `null` compiles silently.

**Hosts** (.NET 11 RC1, Mono WebAssembly, published Release build, headless Chromium, server on localhost). One `zoom` access:

| | sync, WebAssembly | async, WebAssembly | async, Server |
|---|---:|---:|---:|
| read | 0.63–0.69 µs, 0 B | 1.1–1.2 µs, 48 B (the `Task<double>`) | 0.8–1.1 ms |
| write | 0.74–1.17 µs | ~1.5 µs | 0.73–0.78 ms |
| writes pipelined (`WhenAll`) | | ~1.9 µs | 49–65 µs |

- Write order was preserved, and snapshots and events worked, in every column. In sync mode, events arrived before `Raise()` returned.
- Sync-mode bindings on Server failed fast with the "regenerate with `--api async`" message.
- **Hybrid can't use `[JSImport]`.** Outside the browser it throws `PlatformNotSupportedException`, and Hybrid's `WebViewJSRuntime` is not an `IJSInProcessRuntime`. So Hybrid takes the `IJSRuntime` path, the same shape as Server. It wasn't run end to end, because Hybrid hosts don't run on Linux.
- Under `InteractiveAuto`, the same component ran on Server first and on WebAssembly on a later visit, and `Transport=Auto` followed it.
