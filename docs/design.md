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
| **Blazor components** | One plain C# class per custom element in the library's Custom Elements Manifest (`DemoMap : JsComponentBase`, mapping R28), with no `.razor` file. Simple values render as HTML attributes, so they work during prerender and SSR; complex values are set after the first interactive render. Events go through Blazor's own pipeline (`registerCustomEventType` + `[EventHandler]`), which works on all hosts. Supports `@bind-X`, slots, and the element's live object (`Element`). |
| **Custom-event registrations** | `{Package}Events.cs` declares an `[EventHandler]` per manifest event, with `JsEventArgs` (`Interoper.Blazor`: the detail's JSON, read with `Read(reader)`), and `interoper.js` registers the events from the metadata when a realm starts. Hand-written custom-element markup can use them too, once a realm is started on the page (a generated component starts one). |
| **JS metadata tables** | One module per package (`{package}.meta.js`), the same in both modes: per class id, the module specifier and export that construct it, its runtime class name, whether it is value-like, and its snapshot members. Class ids run from 1 (0 is unknown): value-like classes first, then entities, each in IR order, which gives the fixture the ids the transport contract hard-codes. The runtime class name is the profile's `runtimeClassPrefix` (none by default), then the module's path in the package, dotted and without `.js`, plus `.Name` (the declaration's name, also for a default export) unless the file is named after it (`geometry.Point`, `MapView`, `layers.TileLayer`). There is no per-member generated JS. |

The runtime also provides:
- an **in-memory transport** that emulates the JS object model, so unit and bUnit tests need no browser: tests register fake classes and modules in C# (`InMemoryClass`, `InMemoryObject`, `DefineModule`), and it speaks the same JSON protocol as `interoper.js`, so one contract suite runs against both;
- **diagnostics**: a handle census for leaks, a round-trip counter, and the chatty-await analyzer (`src/Interoper.Analyzers`, IOP3001 in [mapping §8](mapping.md#8-diagnostics)), which points out consecutive awaited writes that a batch (§3.3) would send as one round trip. It's a Roslyn analyzer, so it targets netstandard2.0 and pins `Microsoft.CodeAnalysis.CSharp` no newer than the pinned SDK's compiler; how it ships comes with the packages.

Batching, snapshots and module functions are first-class because each awaited call on Server is a full round trip.

---

## 3. Runtime architecture

### 3.1 Modes, seams and transports (verified, [§8](#8-evidence))

| Mode | Seam | WebAssembly | Server | Hybrid |
|---|---|---|---|---|
| `sync` | `IJsSyncTransport` | `[JSImport]`: ~0.65 µs per read, 0 B | fails fast ("regenerate with `--api async`") | fails fast |
| `async` | `IJsTransport` | `[JSImport]`, completed `Task`s: ~1.1 µs, 48 B | `IJSRuntime` over SignalR: ~1 ms per await on localhost, ~50 µs per pipelined write | `IJSRuntime` over the WebView bridge |

- **Both seams have in-memory versions** for tests (`InMemoryTransport`).
- **The seams carry one op and return its envelope** (§3.4), plus typed fast paths for number, string and bool properties. Fast paths are emitted only for members declared as non-null primitives; a null or undefined value throws `JsException` (`InteroperError`) on every transport. `JsEnvelope` reads an envelope and turns `{"e"}` into `JsException`. `JsImportTransport` serves both seams: its async seam returns completed `Task`s for the fast paths and sends ops through `invoke`. `JsRuntimeTransport` serves only the async seam, and its fast paths are plain `get`/`set` ops.
- **Each transport owns its own interoper.js instance**, so handles, census and callback ids never cross transports, even when a page mixes render modes or forces `JsRuntime` next to `JsImport`. `JsRuntimeTransport` imports the module with a unique query (`interoper.js?t=…`). `[JSImport]` binds to one module instance by name, so there is one `JsImportTransport` per .NET runtime; `StartAsync` returns it again.
- **Sync mode fails fast** in `JsImportTransport.StartAsync` when .NET isn't in the browser: an `InvalidOperationException` that says to use async bindings on the JsRuntime transport (regenerate with `--api async`).
- **Disposal.** `JsRuntimeTransport.DisposeAsync` releases the sink and the module; events JS raises afterwards fail on the JS side, so subscriptions are released first.
- **Error handling lives in the transports.** They map JS errors to `JsException`, whatever the path; `JSDisconnectedException` passes through. The Blazor analyzer BL0016 (try/catch around JS interop) doesn't apply to the runtime, which doesn't reference the components package.
- **JS → .NET** goes through one sink per transport into its callback registry (`JsCallbacks`, `{"$f":id}`, R21). On WebAssembly the sink is a .NET delegate passed to `start` through `[JSImport]`, a synchronous call like the `[JSExport]` entry point the experiment verified, but per transport and with no export lookup from JS. It is exercised by the contract tests; its cost hasn't been measured. Elsewhere it is one `DotNetObjectReference` per circuit or WebView (verified), calling `[JSInvokable] InvokeCallback`. A registry per transport means one circuit can't call another's callbacks. A handler that throws fails the JS call that invoked it, and that op's `JsException` carries the handler's message; across `IJSRuntime` it is a rejected Promise in JS instead. Callbacks a scope registers run on its `Dispatch` delegate; `JsComponentBase` sets it to the component's `InvokeAsync`. When that runs the handler inline (always on WebAssembly, and on Server when the call is already on the circuit's dispatcher), JS gets its result as before; when it posts the handler for later, JS gets undefined. In a component, a throwing handler is reported like an event handler's exception (`DispatchExceptionAsync`), not to JS.
- **Registration.** `services.AddInteroper(metadataSpecifier, classes)` (in `Interoper.Blazor`) registers a scoped `JsRealm`: the started transport (`Transport`, and `SyncTransport` where the transport has the sync seam), the class registry and the identity cache. It is a concrete class, not an interface: tests build one over `InMemoryTransport`. Call it in the server app and in the WebAssembly client, so prerendered components can be built on both. The realm starts its transport once; a failed start can be retried, and so can a failed `JsImportTransport.StartAsync`. Sync-mode bindings reach JS through `SyncTransport`, which fails fast with "regenerate with `--api async`" when the transport has no sync seam.
- **Transport per realm (per host).** In async mode the transport comes from app configuration (`"Interop": { "Transport": "Auto" | "JsRuntime" | "JsImport" }`; an unknown value fails when the realm is created) and is resolved by each realm: one DI scope, so a circuit on Server and the app on WebAssembly. `Auto` picks `JsImport` in the browser and `JsRuntime` elsewhere. Each host has its own realm, so under `InteractiveAuto` a component follows its host. On WebAssembly every scope shares the one `JsImportTransport`, so tests measure census baselines rather than assume zero. Under `InteractiveAuto` the same component moved from Server to WebAssembly between visits, and `Auto` followed it (verified). Forcing `JsRuntime` inside WebAssembly reproduces the Server/Hybrid code path in the browser, which is how Hybrid gets tested without Windows (§6).

### 3.2 Handles, identity and lifetime
- **The JS side** keeps `Map<id, object>` plus `WeakMap<object, id>`, so one JS object always has one id. Every handle carries the object's class id.
- **The .NET side** keeps a weak cache per realm, so the same id gives the same C# object; a generated registry (`JsClassRegistry`: class id → factory) maps class ids to live types, which derive from `JsObject` (realm + handle). Live objects hold their realm and handle, not a scope: what a call returns is owned by the scope the caller passes ([mapping §5](mapping.md#5-members)). `JsScope.Wrap<T>(handle, classId)` returns the cached object or a new one of the most-derived registered type, read through any base (R19). An unregistered class id throws `JsException` (`InteroperError`), and the handle is still released with the scope.
- **Scopes.** Handles belong to a `JsScope`; components own one each. One type serves both modes: `Dispose` (`IDisposable`, sync mode) releases through the sync seam, `DisposeAsync` (`IAsyncDisposable`, async mode) through the async seam. A handle is owned by every scope that wraps it (`Wrap`) or owns it bare (`Own`, for subscriptions and other handles without a live object). Disposal sends one `release` op with the handles no other scope owns, then releases the scope's callbacks. A handle that reaches a disposed scope (its call was in flight) is released at once, unless another scope owns it, and the call throws `ObjectDisposedException`. Without a sync seam, `Dispose` throws before forgetting anything. A scope is used from one dispatcher (its component's); a lock keeps disposal and ownership consistent anyway. On Server, a dropped circuit voids all handles, and disposal swallows `JSDisconnectedException`.
- **Generated code** names the runtime's and .NET's types with `global::` (`global::Interoper.JsJson`, `global::System.Threading.Tasks.Task`) and has no usings, so a generated type or member of the same name (`JsJson`, `Opt`, `Task`) hides none of them. It writes ops with `JsOp`, live arguments with `JsObject.Write` (`{"$h":id}`), and reads live results with `JsScope.Read<T>` (`{"$h","$t"}` → `Wrap<T>`), or, for a generic class the registry can't make (`Collection<Marker>`), with `JsScope.Read(ref reader, create)`, which keeps identity through the same cache. `JsScope.Callback` registers a delegate with readers for its arguments (read with the scope) and a writer for its result (R21). Each call registers a new callback that lives until its scope is disposed, so repeated calls belong in a short-lived scope; every handle in a callback's arguments is owned by its scope as it arrives, read or not; a callback called after its scope is disposed is dropped (JS gets undefined) without releasing the handles in its arguments, which stay in JS's table for the transport's lifetime; and in async mode on Server and Hybrid a void one can run after the member's `Task` has completed. `JsScope.Subscribe`/`SubscribeAsync` registers a listener, sends `subscribe` with the event's `EventOptions` (projected to the generated default fields unless they name others) and owns the subscription in the scope; the disposable it returns releases the subscription and the listener before the scope does. `JsScope.StreamAsync` is the async-mode stream over the same: an unbounded single-reader channel that subscribes on the first `MoveNextAsync`, unsubscribes when the enumeration ends, and is completed when the scope is disposed. JS can't be paused, so a stream has no backpressure; projection keeps its values small. Sync-mode code calls `JsRealm.Call`/`Send` and the `Get*`/`Set` fast paths through the sync seam, as async code calls `CallAsync`/`SendAsync` and `Get*Async`/`SetAsync`. If a subscribe op fails the listener is released; a subscription's dispose sends the release before the scope disowns the handle.
- **Components.** `JsComponentBase` (in `Interoper.Blazor`) owns the component's `Scope`, disposes it with the component, and starts the realm in `SetParametersAsync` before its first interactive render, so live calls work from `OnInitializedAsync` on.
- **Prerendering.** Before a component is interactive there's no JS realm; live calls throw `JsRealmUnavailableException`. The realm never starts there, and disposing a scope that never reached JS touches no transport.

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
- JS errors surface at the flush as `JsBatchException` (a `JsException`), listing each failed op with its index; the other ops still ran. An unknown `$h` fails the whole batch as a plain `JsException` (§3.4).
- **Generated code says which ops can queue** by its entry point on `JsRealm`: `SendAsync(op)` and the `SetAsync` fast paths for ops that return nothing, `CallAsync(op, read)` and the `Get*Async` fast paths for everything else (they flush first). Live objects hold only their realm, so these are the realm's.
- **A batch belongs to the async flow that began it**, not to the whole realm: `BeginBatch` sets an async-local, so another component's calls on the same circuit (or WebAssembly app) go out at once. Calls from a flow that outlives the batch (a task it started) go out at once too.
- **Begin and dispose a batch in the same async method** (`await using`). The async-local covers that method's flow, so a batch begun in a helper method doesn't cover the helper's caller.
- **A nested `BeginBatch` joins** the batch the flow is already in; only the outer one flushes.
- **Inside a batch, a void call that returns a Promise completes when it is queued**; JS still awaits the Promise before the batch's next op.
- **A scope disposed with a batch open** flushes the queue before its `release`, and still releases if the flush fails (then throws the first error). The batch stays open; disposing it later sends whatever was queued since. Sync `Dispose` throws for such a scope: it must use `DisposeAsync`.
- **Every `release` flushes the current flow's batch first**, so a scope disposed inside another scope's batch (or a joined one) doesn't release handles that queued ops still use.
- `JsRealm.RoundTrips` counts the realm's calls through the async seam (ops, batches, fast paths), so tests can assert that N writes are one round trip.
- **Sync mode doesn't batch**: each call is already a sub-microsecond call on JS's thread. The `batch` op itself works through `invokeSync` too.

### 3.4 Protocol
- **Typed fast paths** on `[JSImport]` for primitives (number, string, bool).
- **UTF-8 JSON** for structured data, with generated readers and writers:
  - undefined members are omitted;
  - `{"$h":12,"$t":7}` is a handle with its class id;
  - `{"$c":3, …}` is a value-like class instance (R16): writers tag every `XSnapshot` of a value-like class with its concrete class id, and the reviver builds `new Class(rest)`, so libraries don't need to autocast;
  - `{"$f":3}` is a .NET callback;
  - `{"$e":"<id>"}` is a DOM element by its Blazor `ElementReference` id (mapping R13), written only: JS finds it by Blazor's capture attribute, `[_bl_<id>]`, and fails if no rendered element has it;
  - `{"$u":1}` is undefined inside arrays and arguments, in both directions; the `JSON.parse` reviver and the .NET readers restore it.
- **Batches** travel as one op array.
- **Errors** become `JsException` with the JS name, message and stack. The profile can map library error classes to typed subclasses.

**Wire protocol** (`src/Interoper.Runtime/wwwroot/interoper.js`). .NET sends one op as a JSON string and gets back a JSON envelope: `{"v": result}`, `{}` when the result is undefined, or `{"e": {"name", "message", "stack"}}`. A thrown non-`Error` becomes `{"name":"Error","message":String(x)}`.

| Op | Fields | Result |
|---|---|---|
| `create` | `t` class id, `args` | handle |
| `get` | `h` or `module`, `name`, `byValue?` | value |
| `set` | `h` or `module`, `name`, `value` | — |
| `call` | `h` or `module`, `name`, `args`, `byValue?` | value; a Promise is awaited |
| `snapshot` | `h` | the class's snapshot members, value-like ones as `$c` data (R17) |
| `apply` | `h`, `value` (a partial snapshot, set member by member) | — |
| `subscribe` | `h`, `method` (`on`, `watch`), `name`, `callback` (`$f`), `options?` | subscription id, a handle that `release` disposes: the method returns an object with `dispose()`, or `remove()` as some libraries name it |
| `release` | `ids` | — (unknown ids are ignored; every id is released even if a `dispose()` or `remove()` throws) |
| `load` | `modules` | — (imports them; `invoke` only) |
| `wrap` | `value` | the value, encoded as a result (a component turns its `$e` into its element's handle this way) |
| `batch` | `ops`: only ops that return nothing (writes and void calls, §3.3) | — ; if any op failed, `BatchError` with `errors: [{index, name, message, stack}]` |

- Results encode class instances as handles; with `byValue`, and always in callback arguments and snapshots, value-like instances go as `$c` data instead. Plain objects and arrays are copied.
- A handle's `$t` is the id of the runtime class name (the profile's runtime-class property); for a DOM element, the id of the class whose `tag` is its `localName` comes first, since a property like `className` means something else on an element. 0 when the metadata doesn't know it.
- A batch runs its ops in order, awaits a Promise before the next op, and keeps going after a failure. An unknown `$h` fails while parsing, so it fails the whole batch.
- Every `$h` that reaches .NET (results, callback arguments, snapshots) is owned by .NET and must be released.
- A `subscribe` op's `options` (`{"preventDefault":true,"stopPropagation":true,"fields":["x","y"]}`, each part only when set, the whole left out when empty) wrap the listener: JS calls the event's `preventDefault()` and `stopPropagation()` when it has them, then passes on only the named top-level members of the event (mapping R24).
- C# writers omit `module` and `byValue` when unused rather than writing `null`.
- Entry points:
  - `invoke(json)` for `IJSRuntime` and Promise ops; it never rejects. Modules not yet loaded are imported on first use.
  - `invokeSync(json)` for `[JSImport]`; it never starts async work. Sync mode loads every module it uses (classes and module functions) up front with `load`; an unloaded module is an error. A Promise-returning `call`, or a batch containing one, is a caller error, reported after the call has already run.
  - `getValue`/`setValue(h, name, v)`: the typed fast paths for number, string and bool. They throw, and `[JSImport]` keeps only the message, so `takeError()` returns the last fast-path call's error as an error envelope (once; null if it threw nothing). `census()`: the number of live handles.
- A result that can't be encoded (a `BigInt`, a cyclic plain object) becomes an error envelope.
- `start(metadataSpecifier, sink)` imports the generated metadata module (`{ classNameProperty, classes: { id: { module, export, name, value?, tag?, members } }, customEvents: [{ name, fields? }] }`) and the modules of value-like classes and custom elements. It registers each custom event with Blazor once per page (a `Set` on `globalThis` guards it; without `Blazor`, as in Node, it registers none): `name.toLowerCase()` is the Blazor event, the event's `detail` projected to `fields` (when given) is its `JsEventArgs.Json`. `sink(callbackId, argsJson)` returns the callback's result as JSON text (`"null"` for JS null), or null/nothing for undefined, or a Promise of either. `startWithDotNetObject(specifier, ref)` uses `ref.invokeMethodAsync("InvokeCallback", id, argsJson)` as the sink.

### 3.5 Module loading
Generated code knows only bare specifiers (`@acme/maps/views/MapView.js`). The runtime `import()`s them, and the app's import map (Blazor's `<ImportMap />`) decides where they come from: `node_modules` copied into `wwwroot`, a CDN, or a bundle. The CLI can print the import-map entries for a package. The modules of value-like classes are imported when the realm starts, so the reviver can construct them synchronously (sync mode included), and so are those of custom elements, so the elements a page renders are defined. Other modules are imported on first use; sync mode loads them up front with the `load` op: its generated registry lists them (`JsClassRegistry.Preload`), and the realm loads them when it starts.

---

## 4. Generator architecture

```
node_modules/@acme/maps/*.d.ts ──────────► extractor (TypeScript 6.x checker) ─┐
node_modules/@acme/maps/custom-elements.json ─► extractor (CEM) ───────────────┼─► IR (versioned JSON, cached)
                                                                               │        │
                                        profile (optional, consumer-owned) ────┴──► generator --api sync|async
                                                                                         ├─► {Type}.cs          live objects, snapshots, value types, events, module functions
                                                                                         ├─► {Package}.cs       the class registry
                                                                                         ├─► {package}.meta.js  metadata tables, custom-event registrations
                                                                                         ├─► {Component}.cs     components (plain C#, no .razor)
                                                                                         └─► {Package}Events.cs custom-event [EventHandler]s
```

- **Extractor** (`src/interoper-extractor`). Node + the TypeScript 6.x checker (pinned exactly; it has no dependencies, so there's no lockfile), bundled into one JS file that ships inside the CLI. It resolves types with the checker, not a syntax walk ([mapping.md §7](mapping.md#7-what-the-extractor-must-resolve-with-the-checker)). We stay on 6.x until TypeScript 7 has a stable programmatic API. It grew from `tools/dts-survey`, which shares its program setup and discriminant check (`program.mjs`).
- **IR.** A language-neutral, versioned JSON model ([ir.md](ir.md)). A library upgrade is an IR diff.
- **Profile.** Optional, consumer-owned data for library-specific decisions:
  - entity vs value classes;
  - the runtime-class property, a prefix of runtime class names (`runtimeClassPrefix`), and discriminators;
  - event conventions and collection types;
  - renames, numeric overrides, skips and JS-side callback implementations;
  - what to emit (`include`): module specifier globs (`"@acme/maps/layers/*"`, where `*` matches any characters, `/` included). Every exported declaration of a matching module is emitted, along with the bases of emitted classes and the data types they use, whole. A member that uses a class outside the selection is skipped (IOP2007). Absent means every module. This keeps a large library's bindings as small as the consumer needs.

  Good defaults cover most of a typical library.
- **Generator** (`src/Interoper.Generator`). A .NET library with a small indented writer: `Generator.Generate(ir, profile, options)`, whose options are the mode and the root namespace, returns the diagnostics and the files and writes nothing. It emits one `{Type}.cs` per type, in a folder per namespace (`Layers/TileLayer.cs`, R27), plus a `{Component}.cs` per custom element (R28) in its element class's folder, and the registry `{Package}.cs`, the event registrations `{Package}Events.cs` and `{package}.meta.js` at the root, each marked `// <auto-generated/>` (with `#nullable enable`). All output is `partial`.
  - **It never throws for input it can't map**, only for a malformed IR or profile (`InvalidDataException`). What it can't map is found while selecting, by the type map's mapping checks, so a skipped type takes the members that use it along with it ([mapping §8](mapping.md#8-diagnostics)): IOP2003 for an ambiguous union, IOP2007 for anything else. A type whose path is taken is renamed or skipped by R27 (IOP2006).

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

**`fixtures/demo-maps`** is a small JS library we write ourselves (ES modules + hand-written `.d.ts` + `custom-elements.json` + a profile), with its golden IR (`demo-maps.ir.json`, the generator's test input). It covers:
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

**Browser harness** (`tests/Interoper.E2E`). The tests start `tests/Interoper.E2E.Host` (one app with Server and WebAssembly interactivity; its pages live in `tests/Interoper.E2E.Client`) as a separate process on a free port, in Development so static web assets come from the build output, and drive one headless Chromium through Microsoft.Playwright. The package version is pinned to the Chromium revision preinstalled in cloud sessions. Pages are fixed to the `en-US` locale, because .NET on WebAssembly rejects the POSIX locale that headless Chromium reports on Linux. The host serves `fixtures/demo-maps` at `/demo-maps/`, mapped to `@acme/demo-maps/` in its import map, plus the golden slice's metadata module. The E2E projects are in `Interoper.slnx`, so the solution builds and formats them, but the test project turns off `IsTestProject` when built through the solution, so `dotnet test Interoper.slnx` stays browser-free (and IDE test explorers on the slnx don't list the E2E tests).

**Golden slice** (`tests/GoldenSlice`): the fixture's generated bindings, that is the bindings the generator emits for the fixture's whole golden IR and its profile, both modes, checked by `tests/Interoper.Generator.Tests/GoldenSliceTests` (on a difference it rewrites the files and fails; a rerun passes). The test also pins the exact list of diagnostics each mode still reports, each with the reason it remains; fixing one removes its entry. All of it is generated, except the project file, which references `Interoper.Blazor` for the components. One project: a folder per mode (`Sync/`, `Async/`; namespaces `GoldenSlice.Sync`, `GoldenSlice.Async`) with one file per type and a subfolder per namespace below it (`Layers/`, `GoldenSlice.Sync.Layers`), and the metadata module (`wwwroot/demo-maps.meta.js`, served at `/_content/GoldenSlice/`). Its snapshots carry data members only (R17). A behavioural suite per mode (`GoldenSyncSuite`, `GoldenAsyncSuite` in `tests/Interoper.Runtime.Tests`, framework-free like the contract) runs in memory, and in the browser on a realm `AddInteroper` builds for each page's `Interop:Transport`: sync on WebAssembly (and failing fast on Server), async on WebAssembly, on WebAssembly with `JsRuntime`, and on Server.

**Transport contract** (`tests/Interoper.Runtime.Tests/TransportContract.cs`). One framework-free suite (create/get/set, fast paths, `$c`, Promises, `$f` callbacks including re-entrant and throwing ones, module calls, subscribe/release and subscribe with options, errors, census, the `batch` op and `BeginBatch` through a realm, two independent transports) runs against `InMemoryTransport` with C# fakes of the fixture in `dotnet test`, and in the browser through each seam on each host: the file is compiled into `Interoper.E2E.Client`, whose pages run it and print one line per case. One more Server page checks that `PreventDefault` reaches a later DOM listener in time (mapping R24).

**Components** (mapping R28). The generated `DemoMap` is rendered statically in `dotnet test` (`HtmlRenderer`), and in the browser on Server, on WebAssembly in both modes, and by static SSR: attributes and slots, properties set once interactive, events, `@bind-Zoom` both ways, and `Element`'s live view.

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
