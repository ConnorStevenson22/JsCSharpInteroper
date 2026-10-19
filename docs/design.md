# Design

Interoper turns a JavaScript library's TypeScript typings into C# bindings for Blazor, and provides the runtime those bindings use. The type-mapping rules are in [mapping.md](mapping.md). `@acme/maps` stands in for whatever library a consumer binds.

---

## 1. Decisions

- .NET 11+ only. We use new C# features such as unions and `closed` hierarchies. The SDK is pinned in `global.json`. On WebAssembly we use Mono, the default.
- Every Blazor host is supported: Server, WebAssembly and Hybrid (`BlazorWebView`). Hybrid works like Server: .NET runs beside the WebView and talks to it through an async bridge.
- One switch, `--api sync|async`, sets the shape of every live member. `sync` gives plain members (`double Zoom { get; set; }`) and works on WebAssembly only. `async` gives `Task` members with an `Async` suffix (`SetZoomAsync(…)`) and works on every host. Data types are the same in both modes. The full table is [mapping §5](mapping.md#5-members).
- We own the value model, the wire protocol and the handle table. `undefined` is the `default` and `null` is JS null ([mapping §1](mapping.md#1-the-value-model)).
- We ship only the generator and the runtime. Consumers generate their own bindings with one command against their `node_modules`. There is no MSBuild integration. A separate private repo checks Interoper against a real library.
- Tests run on fixture libraries we write (§6).

---

## 2. What we generate

| Kind | What it is |
|---|---|
| Live objects | A class per JS class, wrapping a handle. The same JS object always maps to the same C# object, and you get the most-derived type (`shape is Polygon`). Created with `Create`/`CreateAsync` and owned by a `JsScope`. |
| Snapshots | `XSnapshot` records. One call reads every data member. The same record is the constructor input and, partly filled, a patch (R18). |
| Value types | Enums, extensible enums, unions and `Opt<T>`, with generated JSON readers and writers. No reflection, so they are trim and AOT safe. |
| Events and watches | Typed subscriptions per event, with JS-side options (R24). Async mode also gets `IAsyncEnumerable` streams. |
| Module functions | Stateless library functions over records. One call, no handles. |
| Components | One plain C# class per custom element in the library's Custom Elements Manifest (R28). |
| Custom events | `{Package}Events.cs`, the `[EventHandler]` attributes for the manifest's events (R28). |
| JS metadata | One module, `{package}.meta.js`. For each class id it lists the module and export that construct it (`default` when left out), its runtime class name (a custom element's tag instead, since JS finds an element by its tag), whether it is value-like, and its snapshot members (none when left out). A variant (a class outside the selection that adds a mixin, [mapping §5](mapping.md#5-members)) lists only its runtime class name and `of`, the class whose entry it shares. There is no per-member generated JS. |

Class ids start at 1 (0 means unknown): value-like classes first, then entities, each in IR order. A runtime class name is the declaring module's path within its package, dotted, plus the class name unless the file is named after it: `geometry.Point`, `MapView`, `layers.TileLayer`. The profile can add a prefix (`runtimeClassPrefix`).

The runtime also provides:

- An in-memory transport that emulates the JS object model, so unit and bUnit tests need no browser. Tests register fake classes and modules in C# (`InMemoryClass`, `InMemoryObject`, `DefineModule`). It speaks the same protocol as `interoper.js`, so one contract suite runs against both.
- Diagnostics: a handle census for leaks, a round-trip counter, and the chatty-await analyzer (IOP3001, [mapping §8](mapping.md#8-diagnostics)).

Batching, snapshots and module functions matter because every awaited call on Server is a full round trip.

---

## 3. Runtime architecture

### 3.1 Modes, seams and transports

| Mode | Seam | WebAssembly | Server | Hybrid |
|---|---|---|---|---|
| `sync` | `IJsSyncTransport` | `[JSImport]`, under 1 µs per read | fails fast | fails fast |
| `async` | `IJsTransport` | `[JSImport]` with completed `Task`s, about 1 µs | `IJSRuntime` over SignalR, about 1 ms per await on localhost | `IJSRuntime` over the WebView bridge |

- A seam carries one op and returns its result envelope (§3.4). It also has typed fast paths for number, string and bool properties, used only for members declared as non-null primitives.
- `JsImportTransport` serves both seams. `JsRuntimeTransport` serves only the async seam. `InMemoryTransport` serves both, for tests.
- Each transport has its own `interoper.js` instance, so handles and callbacks never cross transports. There is one `JsImportTransport` per .NET runtime.
- Sync mode fails fast when .NET isn't in the browser, with a message to regenerate with `--api async`.
- Transports turn JS errors into `JsException`. `JSDisconnectedException` passes through.

**Callbacks from JS.** Each transport has one sink into its own callback registry (`JsCallbacks`). On WebAssembly the sink is a .NET delegate passed to JS at start. Elsewhere it is a `DotNetObjectReference` calling `[JSInvokable] InvokeCallback`. A handler that throws fails the JS call that invoked it. In a component, callbacks run through the component's `InvokeAsync`. If that queues the handler instead of running it inline, JS gets `undefined`.

**Registration.** `services.AddInteroper(metadataSpecifier, classes)` (in `Interoper.Blazor`) registers a scoped `JsRealm`. A realm holds the started transport, the class registry and the identity cache. Call it in both the server app and the WebAssembly client. The realm starts its transport once, and a failed start can be retried.

**Choosing a transport.** In async mode, app configuration picks it per realm:

```json
"Interop": { "Transport": "Auto" }
```

`Auto` picks `JsImport` in the browser and `JsRuntime` elsewhere. You can also force either one. Each host has its own realm, so under `InteractiveAuto` a component follows its host. Forcing `JsRuntime` on WebAssembly runs the Server and Hybrid code path in the browser, which is how we test Hybrid without Windows (§6).

### 3.2 Handles, identity and lifetime

- JS side: a `Map<id, object>` plus a `WeakMap<object, id>`, so one JS object always has one id. Every handle carries the object's class id.
- .NET side: a weak cache per realm, so one id always gives the same C# object. A generated `JsClassRegistry` maps class ids to live types, which derive from `JsObject`. `JsScope.Wrap<T>(handle, classId)` returns the cached object or a new one of the most-derived registered type (R19). An unknown class id throws `JsException`.
- Scopes: handles belong to a `JsScope`, and each component owns one. `Dispose` or `DisposeAsync` sends one `release` op for the handles no other scope owns, then drops the scope's callbacks. A result that arrives after its scope was disposed is released at once and the call throws `ObjectDisposedException`. On Server, a dropped circuit voids all handles.
- Live objects hold their realm and handle, not a scope. A call's live results are owned by the scope the caller passes.
- `JsScope.Callback` registers a delegate owned by the scope (R21). `JsScope.Subscribe`/`SubscribeAsync` registers an event listener, and `JsScope.StreamAsync` is the async-mode stream (R24).
- Components: `JsComponentBase` owns the component's `Scope`, disposes it with the component, and starts the realm before the first interactive render, so live calls work from `OnInitializedAsync`. `ElementAsync<T>(reference)` gets a rendered element's live object.
- Prerendering: there is no JS before a component is interactive, so live calls throw `JsRealmUnavailableException`.

### 3.3 Batching (async mode)

```csharp
await using (scope.BeginBatch())
{
    await view.SetZoomAsync(5);        // queued; the Task completes at once
    await view.SetRotationAsync(15);   // queued
}                                      // one round trip sends both, in order
```

- Inside a batch, writes and void calls are queued. Generated code marks them by calling `SendAsync` or the `SetAsync` fast paths. Everything else (`CallAsync`, `Get*Async`) flushes the queue first, so program order holds.
- JS errors surface at the flush as `JsBatchException`, which lists each failed op by index. The other ops still ran.
- A batch belongs to the async flow that began it, not the whole realm. Other components' calls go out at once. Begin and dispose a batch in the same method with `await using`.
- A nested `BeginBatch` joins the outer batch. Only the outer one flushes.
- Every `release` flushes the current batch first, so queued ops never use released handles.
- `JsRealm.RoundTrips` counts round trips, so tests can check that N writes cost one.
- Sync mode doesn't batch. Each call is already sub-microsecond.

### 3.4 Protocol

The protocol lives in `src/Interoper.Runtime/wwwroot/interoper.js`. .NET sends one op as JSON and gets back an envelope: `{"v": result}`, `{}` for undefined, or `{"e": {"name", "message", "stack"}}`.

Structured data is UTF-8 JSON. Undefined members are left out. Special values:

| Encoding | Meaning |
|---|---|
| `{"$h":12,"$t":7}` | a handle and its class id |
| `{"$c":3, …}` | a value-like class instance (R16), rebuilt in JS with `new Class(rest)` |
| `{"$f":3}` | a .NET callback (R21) |
| `{"$e":"<id>"}` | a DOM element by its Blazor `ElementReference` id (R13) |
| `{"$u":1}` | undefined inside arrays and arguments |

| Op | Fields | Result |
|---|---|---|
| `create` | `t` class id, `args` | handle |
| `get` | `h` or `module`, `name`, `byValue?` | value |
| `set` | `h` or `module`, `name`, `value` | none |
| `call` | `h` or `module`, `name`, `args`, `byValue?` | value; a Promise is awaited |
| `snapshot` | `h` | the snapshot members (R17) |
| `apply` | `h`, `value` (a partial snapshot) | none |
| `subscribe` | `h`, `method` (`on`, `watch`), `name`, `callback`, `options?` | a subscription handle |
| `release` | `ids` | none; unknown ids are ignored |
| `load` | `modules` | none; imports them |
| `wrap` | `value` | the value as a result (turns `$e` into a handle) |
| `batch` | `ops` (only ops that return nothing) | none, or a `BatchError` listing failed ops |

- Results send class instances as handles. With `byValue`, and always in callback arguments and snapshots, value-like instances go as `$c` data. Plain objects and arrays are copied.
- A handle's `$t` is the class id of the object's runtime class name (R19). For a DOM element it is the class whose `tag` is the element's `localName`. A class with no runtime class name (a plain class, as an app's own module has) is known by its constructor once its module is loaded. When the metadata doesn't know the object's class, JS walks up the prototype chain to the nearest class it knows, so an object of an undeclared subclass gets its nearest declared ancestor. `$t` is 0 when no ancestor is known.
- Every handle that reaches .NET is owned by .NET and must be released.
- `subscribe` options wrap the listener in JS, so they apply in time even on Server (R24).
- A result that can't be encoded (a `BigInt`, a cycle) becomes an error envelope.

Entry points:

- `invoke(json)` for `IJSRuntime`. It never rejects, and imports modules on first use.
- `invokeSync(json)` for `[JSImport]`. It never starts async work, so sync mode loads its modules up front.
- `getValue`/`setValue` are the typed fast paths. `takeError()` returns the last fast-path error, since `[JSImport]` keeps only the message.
- `start(metadataSpecifier, sink)` loads the metadata module, imports value-like classes' modules and registers custom events with Blazor. `startWithDotNetObject` does the same with a `DotNetObjectReference` as the sink.
- `census()` returns the number of live handles.

### 3.5 Module loading

Generated code knows only bare specifiers (`@acme/maps/views/MapView.js`). The runtime `import()`s them, and the app's import map decides where they come from: `node_modules` copied into `wwwroot`, a CDN, or a bundle.

The generated registry builds the import map for you:

```csharp
new ImportMapDefinition(Maps.ImportMap("/node_modules/"), null, null)
```

`ImportMap(root)` maps each package to its folder under `root`, plus any export whose JS lives elsewhere (the IR's `file`). `root` must start with `/`, `./` or `../`, or be a URL, since browsers silently drop any other address; `ImportMap` throws `ArgumentException` otherwise. It only covers modules the IR lists. If a library's own JS imports other bare specifiers, you need a bundler or a CDN.

When modules load:

- Value-like classes' modules load when the realm starts, so JS can rebuild `$c` values synchronously.
- A component loads its element's module before wrapping the element (R28). Hand-written custom-element markup needs the app to load the module.
- Other modules load on first use. Sync mode loads them all up front (`JsClassRegistry.Preload`).

A module that no package export reaches is internal ([ir.md](ir.md#specifiers)). Its declarations are imported from a module that re-exports them (`importFrom`). If nothing re-exports one, it can't be imported: its class has no `Create`, and its functions and component are skipped (IOP2007). Its objects can still arrive from JS through other members.

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
                                                                                         └─► {Package}Events.cs custom-event [EventHandler]s (class EventHandlers)
```

**Extractor** (`src/interoper-extractor`). A Node script built on the TypeScript 6.x checker ([ir.md](ir.md)). It ships as source inside `Interoper.Tool` and runs on the consumer's Node and TypeScript. The CLI looks for `typescript` in the nearest `node_modules` and fails unless it is 6.x. We stay on 6.x until TypeScript 7 has a stable programmatic API.

**IR.** A language-neutral, versioned JSON model ([ir.md](ir.md)). A library upgrade shows up as an IR diff.

**Profile.** Optional, consumer-owned JSON for library-specific choices. An unknown field is an error. The fields are listed in [PATTERNS.md](../fixtures/demo-maps/PATTERNS.md#profilejson): which classes are value-like, the runtime-class property and prefix, the event and watch methods, collection classes, and `include`. `include` takes module globs (`"@acme/maps/layers/*"`) and emits only those modules plus the bases and data types they need. Members that use a class outside the selection are skipped (IOP2007). This keeps bindings for a large library small.

**Generator** (`src/Interoper.Generator`). A .NET library. `Generator.Generate(ir, profile, options)` returns files and diagnostics and writes nothing. `Bindings.Write` writes them to a folder: one file per type, in a folder per namespace (`Layers/TileLayer.cs`, R27), one per component, and the registry, event registrations and metadata module at the root. Every file is marked `// <auto-generated/>` and every type is `partial`.

The generator throws only for a malformed IR or profile. What it can't map is skipped with a diagnostic, along with the members that use it ([mapping §8](mapping.md#8-diagnostics)).

---

## 5. Consumer workflow and packages

The steps are in [quickstart.md](quickstart.md). The packages aren't published yet, so you pack them yourself and point `nuget.config` at the folder.

| Package | Contents |
|---|---|
| `Interoper.Runtime` | Value model, realm, scopes, protocol, transports, `interoper.js` (served at `_content/Interoper.Runtime/interoper.js`) and the chatty-await analyzer |
| `Interoper.Blazor` | `AddInteroper()`, the component base class and custom-event plumbing. It brings `Interoper.Runtime`. |
| `Interoper.Tool` | The `dotnet interoper` CLI: the generator plus the extractor's source |

All three share one version, set in `Directory.Build.props`. The runtime's public types are in the `Interoper` namespace.

**The `generate` command.** `dotnet interoper generate <package> --api sync|async --namespace <ns> --out <dir> [--profile <file>] [--warnings-as-errors]`.

- It needs Node.js on `PATH`. Packages are found as Node finds them, from the working directory up. The first one is the IR's package and must match the profile.
- Unchanged files aren't rewritten. `<out>/.interoper/files.txt` lists what the last run wrote, so the next run can delete stale generated files. Nothing else in `--out` is touched, so hand-written partials are safe.
- The IR is cached in `<out>/.interoper/ir.json`. Its key covers the tool version, a hash of the extractor, TypeScript version, package and dependency versions, and the one profile field the extractor reads. Other profile changes only regenerate. A package linked from outside `node_modules` (`file:`) adds a hash of its `package.json` and `.d.ts` files, so editing its typings re-extracts. Delete `.interoper` after editing any other package in place without a version bump.
- The report goes to `<out>/interoper-report.txt`: one line per diagnostic plus a count per code.
- Exit codes: 0 done, 1 when `--warnings-as-errors` is set and there are diagnostics (files are still written), 2 for usage, setup or I/O errors.

---

## 6. Testing

Fixtures: `fixtures/demo-maps` is a small JS library we write, with hand-written typings, a `custom-elements.json`, a profile and a golden IR. [PATTERNS.md](../fixtures/demo-maps/PATTERNS.md) lists each pattern it covers and the rule it tests. `fixtures/demo-widgets` is a tiny second package on top of it, for multi-package extraction: `exports` maps, re-exports, internal modules and types from another package.

| Host | How it's tested |
|---|---|
| WebAssembly | Playwright, both modes |
| Server | Playwright, async mode |
| Hybrid | Its code path is covered by Server tests and by WebAssembly tests with `Transport=JsRuntime`. A real `BlazorWebView` is checked by hand on Windows before releases ([release.md](release.md#3-hybrid-by-hand-on-windows)). |

| Suite | What it checks |
|---|---|
| Golden slice (`tests/GoldenSlice`, `GoldenSliceTests`) | The fixture's generated bindings in both modes, checked in. On a difference the test rewrites them and fails. It also pins the diagnostics each mode reports. |
| Golden behaviour (`GoldenSyncSuite`, `GoldenAsyncSuite`) | The generated bindings work, in memory and in the browser on every host and transport. |
| Transport contract (`TransportContract.cs`) | One protocol suite, run against `InMemoryTransport` and in the browser through each seam on each host. |
| Partial selections (`PartialSelectionTests`) | Generating each fixture module alone and in pairs never throws, and the result compiles. |
| Components | The generated `DemoMap` rendered statically, interactively in memory, and in the browser on Server, WebAssembly and static SSR. |
| CLI (`tests/Interoper.Tool.Tests`) | `generate` with the real extractor: the IR cache, the report, stale files and exit codes. |
| Latency (`LatencyTests`) | Server with 50 ms added to every message. A batch of 100 writes must be one round trip. |
| Size (`SizeBudgetTests`) | A trimmed WebAssembly publish has only the framework's own trim warnings, and the packages and golden slice stay under a size budget. |
| Quickstart (`QuickstartTests`) | Runs [quickstart.md](quickstart.md)'s commands against packed packages, then checks both pages in Chromium. |

The browser tests (`tests/Interoper.E2E`) start `tests/Interoper.E2E.Host` (Server and WebAssembly) on a free port and drive headless Chromium through Microsoft.Playwright. The host serves `fixtures/demo-maps` under `/node_modules/`.

---

## 7. Risks

- Server chattiness, about 1 ms per await. Batching, snapshots, module functions, the analyzer and the latency tests keep it in check.
- Async mode can't cancel JS events or return a callback value synchronously. JS-side event options cover the common event cases (R24). Callbacks whose result JS needs are skipped (IOP2005).
- C# unions may change before .NET 11 ships. The SDK is pinned and the value-model tests catch regressions.

---

## 8. Constraints

Two constraints shape the value model and the hosts. Tests check both.

- Value model: `null` never takes a union conversion, so the generated `implicit operator X(Undefined? value)` is what lets `null` compile. The bottom case must be the nullable `Undefined?`. With a non-nullable one, a switch that misses `null` still compiles (R2).
- Hosts: Hybrid can't use `[JSImport]`, since its JS runtime isn't in-process, so it takes the `IJSRuntime` path like Server. Sync bindings on Server fail fast.

---

## 9. Limitations

These aren't supported yet:

- Profile options for renames, numeric types (`int`, `long`), union classifiers, JS-side callbacks and typed error classes.
- A WebIDL front end.
- The TypeScript 7 API, once it is stable.
- Namespaces in the IR (`export * as ns`, `declare namespace`).
