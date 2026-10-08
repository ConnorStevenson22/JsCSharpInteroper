# Implementation roadmap: ArcGIS-first, .NET 11+

*Draft 1, October 2026. Builds on the [feasibility report](report.md). Claims marked **verified** come from the runnable [spikes](../spikes) (.NET 11 RC1, headless Chromium).*

"Interoper" is a working name taken from the repo; rename freely.

---

## 0. Decisions

### Locked (from you)

| # | Decision |
|---|---|
| D1 | **.NET 11+ only.** C# 15 unions, `closed` hierarchies and the newest runtime features are fair game. No `net10.0` fallback. |
| D2 | **ArcGIS Maps SDK for JavaScript is the baseline.** That means `@arcgis/core` and `@arcgis/map-components`, pinned to 5.1.x to start. ArcGIS-specific needs drive the design; generality is proven later with a second library. |
| D3 | **Generated kinds include at least:** live objects, snapshots, and Blazor components for custom elements. More are proposed in §2. |
| D4 | **Unorthodox is allowed.** We own the wire protocol, the handle table and the value model rather than bending to `IJSRuntime`. |

### Proposed (please confirm or veto; details in the sections referenced)

| # | Proposal | Why |
|---|---|---|
| P1 | **`undefined` is a real union case and the `default` of every generated union and `Opt<T>`; the `null` literal is JS `null`** (§1) | Your idea, made to work. Verified: three states, compiler-checked exhaustiveness, unset members omitted on the wire with no extra code. |
| P2 | **Our own transport and handle table, with `[JSImport]`/`[JSExport]` as the primary transport** (§3) | Verified ~0.5 µs per synchronous property read on Mono, ~100× faster than `IJSInProcessRuntime`. Synchronous transport is what makes "live" C# properties possible. |
| P3 | **WebAssembly first**; Blazor Server/Hybrid (async-only transport) as a later milestone (M7) | A C# property getter can't `await`. Live properties are only natural where interop is synchronous. |
| P4 | **Both runtimes in CI; Mono is the default until CoreCLR catches up** (§3.5) | CoreCLR-on-WASM is opt-in preview in .NET 11. It runs our spike unchanged, but ~10× slower per call (interpreter). |
| P5 | **CDN loading first** (`js.arcgis.com/5.x` + `$arcgis.import`); npm/bundled later | Zero JS tooling for Blazor developers. |
| P6 | **Generated code is checked in and shipped as source in NuGet packages**, not generated on every consumer build | `@arcgis/core` is 1,652 `.d.ts` files; regeneration is a deliberate, reviewed step per ArcGIS release. |
| P7 | **Hand-write a "golden slice" before writing the generator** (M1) | We design the API by using it, then make the generator reproduce it exactly. |

---

## 1. The value model: JavaScript semantics in C#

Verified in [`spikes/value-model`](../spikes/value-model) (7/7 checks).

```csharp
public sealed class Undefined                 // users can't construct it; the runtime owns Undefined.Value
{
    public static readonly Undefined Value = new();
    private Undefined() { }
}

// type ColorLike = string | readonly number[] | ColorJSON | RGBA;   used as  color?: ColorLike | null
[Union] public readonly struct ColorLike : IUnion          // generated: non-boxing, tag-based
{
    public ColorLike(string value) ...
    public ColorLike(double[] value) ...                   // number[] and the ColorJSON tuple collapse into one case
    public ColorLike(Rgba value) ...
    public ColorLike(Undefined? value) ...                 // bottom case: null ref = JS null, singleton = undefined
    public static implicit operator ColorLike(Undefined? value) => new(value);  // makes `= null` compile
    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    // HasValue / TryGetValue(out T) for every case -> no boxing in pattern matching
}
```

How it behaves (all verified):

| You write | It means | On the wire |
|---|---|---|
| *(nothing)* / `default` | `undefined` | property omitted |
| `= null` | JS `null` | `null` |
| `= "red"`, `= new double[] {…}`, `= new Rgba {…}` | value | value |
| `= Undefined.Value` | explicit `undefined` (e.g. in `with` expressions) | omitted |

```csharp
string Describe(ColorLike c) => c switch
{
    string css      => css,
    double[] rgba   => $"rgba[{rgba.Length}]",
    Rgba r          => $"r={r.R}",
    Undefined       => "use the default",   // omit this arm -> CS8509
    null            => "no color",          // omit this arm -> CS8655
};
```

Rules the generator follows:

1. **Every TypeScript union becomes a generated `[Union]` struct, not the `union` keyword.**
   - The keyword's `default` is `null` and it boxes value types (2.4 MB per 100k `double`s in the report).
   - Custom structs let `default` mean undefined, avoid boxing, and still get all the language support: conversions, patterns, exhaustiveness.
2. **Non-union types in optional positions become `Opt<T>`** (`Opt<double> Z`, `Opt<Geometry> Geometry`), with the same three states. Required, non-null positions stay plain C# types (`double Zoom`).
3. **String-literal unions become C# `enum`s** with a generated name map (no reflection). Open-ended unions (`"a" | (string & {})`) and huge ones (the 2,631 Calcite icon names) become *extensible enums*: a `readonly record struct` wrapping a string, with static known values.
4. **A union of live classes that covers every concrete subclass of an abstract base maps to the base**, generated as `closed class`.
   - Example: `GeometryUnion` = `Extent | Multipoint | Point | Polygon | Polyline | Mesh` → `Geometry`.
   - Switches over subclasses are exhaustive, including through `Opt<Geometry>` (verified). Unions that are *subsets* stay unions.
5. **Collapse after mapping.** `readonly number[]` and `[number, number, number, number]` are both `double[]`, so they become one case.

Why the operator is needed: `null` never takes a union conversion (CS0037, verified), so a user-defined conversion from `Undefined?` is what lets `= null` compile. It also needs no extra marker type (verified).

---

## 2. What we generate (the catalogue)

Your three kinds, plus seven that fall out of the same IR.

### K1. Live objects

A class per ArcGIS class (`MapView`, `Graphic`, `FeatureLayer`). Each instance wraps a handle, and its properties *are* the JS properties:

```csharp
view.Zoom = 7;                       // one synchronous [JSImport] call (~0.5 µs on Mono)
double z = view.Zoom;
if (graphic.Geometry is Point p)     // Opt<Geometry>; JS reports the real class -> most-derived proxy
    Console.WriteLine(p.X);
```

- **Identity.** The JS handle table keeps a `WeakMap<object, id>`, so the same JS object always gets the same handle. The .NET side keeps a weak cache, so `view.Map == view.Map` is the *same C# object*. That makes reference equality, dictionaries and `@key` work.
- **Polymorphism.** JS sends the runtime class with each handle. ArcGIS has `declaredClass` (`"esri.layers.FeatureLayer"`), and a generated registry maps it to the most-derived proxy. So `map.Layers[0] is FeatureLayer fl` works.
- **Getter ≠ setter (autocasting, 1,553 accessors).** The property is typed by the getter. A generated `SetGeometry(GeometryInput value)` takes the setter union (live instances ∪ properties bags). Optionally, implicit conversions from a properties bag to a live type construct the JS object inline (`view.Map = new MapProperties {…}`); this is an open question (Q6).
- **Lazy realization (creative).** `new Graphic { Geometry = …, Symbol = … }` would normally cost one constructor call plus N setter calls. Instead, the proxy starts *unrealized*: setters record into a local properties bag, and the first read, method call or hand-off to JS realizes it with a single `new Graphic(bag)`. Object-initializer syntax then costs one interop call.
- **Promises** become `Task`/`Task<T>`; `[JSImport]` already supports this (verified). `when()`/`load()` map to `WhenAsync()`/`LoadAsync()`.
- **Lifetime.** Handles belong to a `JsScope`. Components own one, and `using var scope = JsScope.Begin()` works too. Disposing the scope releases every handle created in it, in one batched call. A finalizer backstop covers forgotten handles.

### K2. Snapshots

One generated record per class, named after ArcGIS's own vocabulary (`GraphicProperties`, `MapViewProperties`). The same type plays three roles:

```csharp
MapViewProperties snap = view.Snapshot();          // read: one call returns every data member
var g = new Graphic(new GraphicProperties          // create: constructor bag (ArcGIS's XProperties)
{
    Geometry = new PointProperties { X = -118.8, Y = 34.0 },
    Symbol   = new SimpleMarkerSymbolProperties { Color = "red", Size = 12 },
});
view.Apply(new MapViewProperties { Zoom = 5, Rotation = 15 });   // patch: unset members are untouched
view.Apply(MapViewProperties.Diff(before, after));               // generated diff -> minimal patch
```

- Because `default` is undefined (P1), **a sparse patch is just a partially initialized record**. `Apply` maps onto ArcGIS's `Accessor.set(props)`.
- **Depth policy.** Value-like classes (ArcGIS `JSONSupport`/`Clonable`: geometries, symbols, colors, renderers) are copied deeply as their properties records. Entities (layers, maps, views) appear as live handles inside a snapshot.
- **Projections (later, creative).** `view.Snapshot(v => new { v.Zoom, v.Center })` reads exactly those members in one call. A source generator with C# interceptors would compile the projection to a JS read list, so there's no runtime expression-tree cost.

### K3. Blazor components for custom elements

Generated from the Custom Elements Manifest that `@arcgis/map-components` ships (107 elements; `arcgis-map` has 72 members, 26 events and 18 attributes):

```razor
<ArcgisMap Basemap="topo-vector" @bind-Zoom="zoom" OnArcgisViewClick="AddPoint" @ref="map">
    <ArcgisZoom Slot="top-left" />
    <ArcgisLegend Slot="bottom-right" />
</ArcgisMap>
```

- Simple values render as **HTML attributes**, so the map appears even in static SSR or before interactivity. Complex values are set as properties after first render.
- **Events go through Blazor's own event system.** We generate `Blazor.registerCustomEventType(...)` registrations plus `[EventHandler]` classes for every manifest event, so even a raw `<arcgis-map @onarcgisviewclick="...">` gets typed event args with no `DotNetObjectReference`. Live objects in event details (such as `mapPoint`) arrive as handles.
- `@bind-X` is wired to the matching change events, or to a property watch.
- `map.Element` is the live `ArcgisMapElement`; `map.View` is the live `MapView`.
- Slots map to a `Slot` parameter and child content. `reference-element` linking for components placed outside the map uses a cascading value.

### K4. Value types
Enums, extensible enums, generated unions and `Opt<T>` (§1). JSON readers and writers are generated per type: no reflection, trim/AOT-safe, and undefined/null/handles handled exactly.

### K5. Events and watches
- From ArcGIS's `"@eventTypes"` maps (resolved by the type checker): `view.On.Click += handler` and `await foreach (var e in view.On.Click.ReadAllAsync(ct))`.
- From `reactiveUtils`: `view.Watch(v => v.Zoom)` returns an `IObservable<double>` or `IAsyncEnumerable<double>`. Both are disposable subscriptions owned by the scope.

### K6. Module functions (stateless, data in → data out)
ArcGIS geometry operators (`bufferOperator.execute`, `projectOperator`, …), `reactiveUtils`, `intl`, and so on. These become static classes that take **properties records, not live objects**: `GeometryOperators.Buffer(pointProps, 100)` returns `PolygonProperties`. Heavy geometry work needs no handles at all.

### K7. Custom-event registrations
A small generated JS module plus C# `[EventHandler]` attributes (see K3). It's useful on its own: anyone hand-writing `<arcgis-*>` markup benefits.

### K8. In-memory transport (tests without a browser)
A transport that emulates the JS object model, with handles mapped to property dictionaries and calls recorded or stubbed. Generated live objects run against it unchanged, so bUnit and unit tests need no browser. The report's `FakeJsRuntime` was the seed of this. It also works as a **record/replay** tool: capture the op log from a real browser run and replay it as a golden test.

### K9. Diagnostics
A dev-only overlay or endpoint listing live handles by class and owning scope (leak detection), recent interop ops, and per-call timing. There are also analyzers:
- reading N members in a row → "use `Snapshot()`";
- a live handle that isn't owned by a scope;
- (M7) synchronous access on an async host.

### K10. The JS half
A small hand-written runtime (`interoper.js`: handle table, dispatcher, marshalling, events, errors), plus **generated metadata tables**:
- class → module path, used by `$arcgis.import` (CDN) or the npm loader;
- `declaredClass` → class id;
- snapshot member lists per class;
- event names.

There is no per-member generated JS, so it stays small.

---

## 3. Runtime architecture

### 3.1 Layers

```
 Generated C# (K1–K7)  ─── uses ──►  Interoper.Runtime
                                      ├─ value model: Undefined, Opt<T>, union helpers
                                      ├─ JsHandle, JsScope, proxy registry (declaredClass -> factory)
                                      ├─ protocol: op codes + UTF-8 JSON payloads with $h / $f markers
                                      └─ ITransport
                                           ├─ JsImportTransport   (WASM, sync; Mono + CoreCLR)   ← v1
                                           ├─ InMemoryTransport   (tests, record/replay)        ← v1
                                           └─ JsRuntimeTransport  (IJSRuntime; Server/Hybrid)   ← M7
 interoper.js (JS half, K10)  ◄── [JSImport] / [JSExport] ──►  JsImportTransport
```

### 3.2 Protocol
- **Typed fast paths** for primitives: `getDouble(h, name)`, `setDouble(h, name, v)`, plus string, bool and handle variants. No JSON at all; this is the ~0.5 µs path.
- **JSON for structured data**, using generated writers:
  - undefined members are omitted;
  - `{"$h": 12, "$t": 7}` is a handle with its class id;
  - `{"$f": 3}` is a .NET callback;
  - in arrays and arguments, `{"$u":1}` is undefined, and the JS side's `JSON.parse` reviver turns it back into a real `undefined`.
- **Batching.** `using (Js.Batch()) { … }` queues writes and flushes them in one call. Async transports batch every turn automatically.
- **Errors.** JS exceptions become `JsException` with the JS `name`, `message` and stack. ArcGIS's own `Error` class (`@arcgis/core/core/Error.js`, which carries `name`, `message` and `details`) maps to a typed subclass.

### 3.3 Calls into .NET
One `[JSExport]` entry point, `Dispatch(int callbackId, string payload)`, is synchronous (verified). Events, watches and callback-typed parameters (such as `popupTemplate.content` functions) all route through it. There is no per-callback `DotNetObjectReference`.

### 3.4 Module loading
Synchronous construction requires the class's module to be loaded already:
- `await ArcGis.LoadAsync()` preloads the modules the app uses. The generated manifest lists them, and components preload their own.
- Constructing an unloaded class throws a message that names the module to add.
- **CDN first:** one `<script type="module" src="https://js.arcgis.com/5.x/">`, then `$arcgis.import([...])`.
- **npm later:** a generated ES module that statically imports the same list, for bundlers.

### 3.5 Runtimes: Mono and CoreCLR
- CoreCLR-on-WASM is opt-in in .NET 11 (`<UseMonoRuntime>false</UseMonoRuntime>`). Microsoft lists it as incomplete, with AOT still Mono-only and stability and performance targeted for .NET 12.
- The spike ran unchanged on CoreCLR, `[JSExport]` included, at ~5 µs per sync call versus ~0.5 µs on Mono.
- Plan: **CI runs every browser test on both runtimes.** Default to Mono for now, and flip the default when CoreCLR wins on our benchmarks.

---

## 4. Generator architecture

```
@arcgis/core *.d.ts ─► extractor (Node, TypeScript 6.x checker) ─┐
map-components custom-elements.json ─► extractor (CEM) ──────────┼─► IR (versioned JSON, golden-tested)
                                                                 │        │
                                       profiles/arcgis.json ─────┴──► generator (.NET tool) ─► *.g.cs  (K1–K8)
                                                                                           ├─► *.g.js  (K10 tables)
                                                                                           └─► *.g.razor.cs (K3)
```

- **Extractor** (grows out of `tools/dts-survey`). It asks the *checker* for resolved members:
  - computed bags (`Partial<Pick<…>>`), get/set type pairs, overloads, `@eventTypes` events, discriminants, JSDoc, module paths;
  - **`declaredClass` names, which aren't in the `.d.ts`.** We derive them by convention (`@arcgis/core/layers/FeatureLayer.js` → `esri.layers.FeatureLayer`), cross-check that the literal appears in the module's shipped `.js`, and assert the mapping in the browser test suite. The convention held for every module sampled: `Point`, `FeatureLayer`, `MapView`, `SimpleMarkerSymbol`, `Collection`.

  TypeScript 7.0 has no stable programmatic API yet, so we stay on 6.x behind the IR.
- **IR.** A language-neutral JSON model: types, members, unions with case metadata, events, components. It's diffable, so **an ArcGIS upgrade is an IR diff we can review like a changelog**.
- **Profile** (`profiles/arcgis.json`). Every library-specific decision lives here as data:
  - which classes are entities and which are value-like;
  - the type tag (`declaredClass`) and discriminator (`type`);
  - event conventions, `Collection<T>` handling, renames (`"3d"` → `ThreeD`), numeric overrides (`wkid` → `int`) and skips.
- **Generator.** A .NET tool that writes C# with a small indented writer (no template engine) and `partial` everything, so hand-written members can live beside generated ones.
- **Output split.** `Interoper.ArcGIS` (core, may later split by namespace for build time and trimming) and `Interoper.ArcGIS.Components`.

---

## 5. ArcGIS baseline and acceptance

**Golden slice (M1, hand-written):**
- `Map`, `MapView`, `Graphic`, `GraphicsLayer`, `FeatureLayer`;
- `Point`, `Polyline`, `Polygon`, `Extent` under a `closed Geometry`;
- `SimpleMarkerSymbol`, `SimpleLineSymbol`, `SimpleFillSymbol`;
- `PopupTemplate`, `Collection<T>`;
- one `@eventTypes` event (click), one watch (zoom), and one geometry operator (buffer).

**Acceptance samples**, ported from Esri's official samples and run headless on both runtimes from M1 onwards:

| Sample | Exercises |
|---|---|
| Intro to MapView | construction, live properties, module loading |
| Intro to graphics | properties bags, symbols, unions, collections |
| Intro to FeatureLayer + PopupTemplate | entities vs values, callbacks, promises |
| Query features | `Task<T>`, snapshots of query results |
| Watch for changes (reactiveUtils) | watches, scopes and disposal |
| Map components: map + zoom + legend + search | K3, custom events, slots, `@bind` |
| Sketch component | complex events with live geometries |

Coverage tiers:
1. **The slice** (M1–M4).
2. **Everything the samples touch** (M4).
3. **All of `@arcgis/core` and `map-components`** (M6). Each class carries a generated `[JsCoverage(Tested | Generated)]` marker, so docs show what is browser-tested.

---

## 6. Milestones

Ordered, each with exit criteria. No dates: each milestone's exit criteria decide when it's done.

**M0 · Groundwork**
- Repo layout (§7) and CI: build, unit tests, and a headless Chromium harness (from `spikes/wasm-transport`) running a test page on Mono *and* CoreCLR.
- Confirm the open questions (§9). Move to .NET 11 GA when it ships (expected November 2026).
- *Exit:* CI is green on both runtimes with one smoke test.

**M1 · Runtime core and golden slice (no generator)**
- Value model (promoted from `spikes/value-model`).
- `ITransport`, `JsImportTransport`, `InMemoryTransport`.
- `interoper.js`: handle table with identity, release, class tags, sync `[JSExport]` dispatch, errors, promise bridge.
- The hand-written golden slice (§5), and the *Intro to graphics* sample.
- *Exit:*
  - the sample runs headless on both runtimes;
  - a sync property read stays under 1 µs on Mono;
  - after `scope.Dispose()` the JS handle count returns to baseline;
  - the same unit tests pass on `InMemoryTransport`.

**M2 · Extractor and IR**
- The checker-based extractor for `@arcgis/core`, plus the CEM reader.
- IR schema v1 and golden IR snapshots for the slice.
- `declaredClass` convention plus a browser check.
- *Exit:* IR for the full packages is deterministic (byte-identical across runs), and the slice IR is reviewed.

**M3 · Generator v1: data plane and live objects**
- K4 value types and K2 properties records.
- K1 live classes: properties, methods, `closed` bases, proxy registry.
- K10 metadata tables.
- *Exit:* the **generated slice replaces the hand-written golden slice with no API changes**, and the M1 sample compiles unmodified.

**M4 · Behaviour**
- K5 events and watches, `Collection<T>`, callbacks, promises.
- `Snapshot`/`Apply`/`Diff`, lazy realization, batching.
- K6 module functions (geometry operators).
- *Exit:* the first five acceptance samples pass on both runtimes.

**M5 · Components**
- K3 from the CEM: attributes for SSR, properties after render, custom events via Blazor's event system (K7), `@bind`, slots, `Element`/`View` access.
- *Exit:* samples rewritten with `<ArcgisMap>`, and the components and sketch samples pass.

**M6 · Full surface and polish**
- All of `@arcgis/core` and `map-components`; the profile completed.
- Trimming and size budget; XML docs (see Q7); K9 diagnostics and analyzers; packages.
- *Exit:*
  - zero generator warnings over the full API;
  - a trimmed sample app within the agreed size budget;
  - an upgrade dry run from 5.1 to the next ArcGIS minor version, producing an IR diff.

**M7 · Async hosts** *(if Q1 says yes)*
- `JsRuntimeTransport` over `IJSRuntime` for Server and Hybrid.
- An async member surface (`GetZoomAsync`, `ApplyAsync`), auto-batching per turn.
- Optional **mirrored properties**: the JS side watches the properties a proxy reads (ArcGIS `reactiveUtils`) and pushes changes, so getters stay synchronous on Server.

**M8 · Prove genericity**
- A second library (Leaflet or Chart.js) with **only a new profile**, no generator changes.
- A WebIDL front end; the TypeScript 7.1 API once it is stable.

---

## 7. Proposed repository layout

```
src/
  Interoper.Runtime/              value model, handles, scopes, protocol, ITransport, InMemoryTransport
  Interoper.Runtime.Browser/      JsImportTransport + interoper.js (static web asset)
  Interoper.Runtime.Components/   component base class, custom-event plumbing
  Interoper.Generator/            .NET tool: IR + profile -> C# / JS / Razor
  Interoper.ArcGIS/               generated (+ partial hand-written) live objects, records, unions
  Interoper.ArcGIS.Components/    generated Blazor components from the CEM
tools/
  extractor/                      Node + TypeScript 6.x checker -> IR (successor to dts-survey)
  dts-survey/
profiles/arcgis.json
samples/ArcGIS.Samples.Wasm/      the acceptance samples
tests/
  Interoper.Runtime.Tests/        unit tests (InMemoryTransport)
  Interoper.Generator.Tests/      golden IR and golden C# snapshots
  Interoper.Browser.Tests/        Playwright, Mono + CoreCLR
spikes/                           experiments backing this roadmap
```

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| CoreCLR-on-WASM is preview; trimming issues are open | Dual-runtime CI; default to Mono; keep runtime-specific code out of generated output |
| The union feature still evolves before GA | Pin the SDK; the spikes double as regression tests; we use custom `[Union]` structs, the stable core of the feature |
| ArcGIS removes widgets in 6.0 (~Q1 2027) | Components-first (K3); don't generate widget classes |
| Generated assembly size and build time | Value types are cheap; split by namespace if needed; measure from M3 |
| `declaredClass` convention gaps | A browser test asserts the mapping for every generated class |
| Esri license: "redistribute … without modification" | Q7: link to Esri docs rather than copying JSDoc text; get a legal read before publishing packages |
| TypeScript 7 replaces the 6.x API | The IR isolates the extractor; migrate in M8 |

---

## 9. Questions to confirm

1. **Hosting models:** is WebAssembly-only right for v1, with Server/Hybrid as M7? Or do you need Server or MAUI Hybrid from the start? This decides whether live properties must also exist in an async form early.
2. **CoreCLR:** CoreCLR on WebAssembly is an opt-in preview in .NET 11 and ~10× slower per interop call in our test.
   - Option A: support both runtimes and default to Mono until CoreCLR is faster (recommended).
   - Option B: go CoreCLR-only now.

   Or did you mean CoreCLR for MAUI/desktop, where it's already the runtime?
3. **ArcGIS loading:** is CDN-first OK, or do your apps bundle `@arcgis/core` from npm?
4. **Naming:** the product and namespace names. For snapshot/bag types, reuse ArcGIS's vocabulary (`GraphicProperties`), or use something like `Graphic.Snapshot`?
5. **Faithfulness:** should every optional position be tri-state (`Opt<T>` and unions with `Undefined`), as proposed? Or should *read-only* outputs collapse to `T?` for convenience?
6. **Autocast conversions:** should a properties bag convert implicitly to a live object (`view.Map = new MapProperties {…}` silently creates the JS object)? It's convenient but hides an allocation. The default proposal is an explicit `SetMap(...)` / `new Map(props)`.
7. **Licensing:** do you plan to publish this (open source or NuGet)? If so, copying Esri's JSDoc into XML docs needs a legal read. The fallback is "summary line + link to the Esri reference page".

---

## 10. Parked ideas

- **Interceptor-compiled snapshot projections** (K2): one JS read list per call site.
- **Web Worker offload** for pure K6 operators, using the .NET 11 Blazor Web Worker template.
- **Zero-copy payloads.** `[JSImport]` accepts `Span<byte>` as a `MemoryView`, so batch payloads could be written straight into WASM memory and decoded by JS with `TextDecoder`.
- **Generated bUnit helpers:** component tests that run against `InMemoryTransport`.
- **IR → docs site:** API pages with coverage tiers and the ArcGIS reference link for every member.
- **A "JS heap" debugger visualizer** for `JsHandle` in Visual Studio.
