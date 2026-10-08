# A generic JS → .NET binding generator in the C# 15 era

*Feasibility report, October 2026. Scope: generating C# bindings from JavaScript libraries' TypeScript declarations for Blazor (Server, WebAssembly, Hybrid) and for .NET outside the browser. Everything marked **verified** was run in this repository against the .NET 11 RC1 SDK (`11.0.100-rc.1.26425.128`) and TypeScript 6.0.3.*

> **Status:** research record. Where it differs from the [roadmap](roadmap.md), the roadmap wins. In particular: all three Blazor hosts with async live members, Mono only, and consumer-side generation instead of shipped bindings. Third-party libraries, ArcGIS included, appear here only as measured examples. No code, typings or generated output from them is kept in this repo.

---

## TL;DR

1. **A generic generator is feasible.** Across 12 real libraries (151,353 API positions, measured with [`tools/dts-survey`](../tools/dts-survey)), **68–91% of positions map directly** to C# with no per-library decisions (primitives, classes, arrays, enums, nullability). **2.4–12.5% need a real union type.** The remaining **4–24% need judgement** (callbacks, `any`, intersections, type-level computation). That last slice is what made your previous generator project-specific. It can't be removed, but it can be **moved out of the generator's code and into a per-library "profile" (data)**.
2. **C# 15 unions remove the biggest structural blocker, but not all of it.** `union ColorInput(string, double[], Rgba);` now gives implicit conversions, exhaustive `switch`, and **native System.Text.Json support that works unchanged through Blazor's `IJSRuntime` serializer (verified)**. What .NET 11 does *not* give you:
   - Value-based discrimination (`type: "point"`). The built-in classifiers only look at token kinds and property *names*. A ~100-line custom `JsonTypeClassifierFactory` fixes it (verified).
   - Unions of JS object handles. Blazor's handle reader rejects any extra property, so you need your own proxy converter (verified).
   - The structural classifier refuses unions whose distinguishing members are optional (verified). TypeScript is full of those.
   - Compiler-generated unions box value types: 2.4 MB per 100k `double` cases, against 0 B for a generator-emitted custom union (verified).
3. **Unions are not the most common problem.**
   - **String-literal unions** (→ enums) are more frequent than all real unions combined in most libraries. `@arcgis/core` has 795 distinct ones, one with 2,631 members.
   - **`null` vs `undefined`** affects up to 20% of positions (ArcGIS, Google Maps).
   - **Getter/setter type asymmetry**, i.e. ArcGIS autocasting: 1,553 accessors in `@arcgis/core` 5.1.
   - **Type-level computation** (`Partial<Pick<…>>`, `this["@eventTypes"][Type]`): 1,112 utility-type references in ArcGIS alone.

   All four need the TypeScript *checker*, not a syntax walk.
4. **ArcGIS specifically is moving under your feet.** ArcGIS Maps SDK 5.0 (Feb 2026) deprecated all widgets in favour of web components (`@arcgis/map-components`), with removal targeted for 6.0 (~Q1 2027). That package ships a **Custom Elements Manifest** (107 elements). A generator that also reads that manifest could emit **Razor components**, not just C# classes.
5. **Recommended shape:**
   - **Extractors** (TypeScript checker, plus Custom Elements Manifest and WebIDL readers) produce a **versioned JSON IR**.
   - A **mapper** applies library **profiles** to that IR.
   - **Emitters** produce C# (DTOs, unions, proxies), a **generated JS shim**, and optionally Razor components.
   - A small **runtime library** provides the union classifier, proxies, the tri-state optional type and events, behind pluggable backends: `IJSRuntime` first, then `[JSImport]`, Jint/ClearScript and node-api-dotnet.
6. **On .NET 10 LTS:** the `union` *language* feature works with a six-line polyfill (verified). .NET 10's STJ doesn't understand unions, though: it writes `{"Value":{…}}` (verified). Emit unions plus a generated converter for `net10.0`, and rely on the native support on `net11.0`+.

---

## 1. What changed in .NET 11 / C# 15, and what it means for interop

### 1.1 Union types (C# 15)

```csharp
public union Pet(Cat, Dog, Bird);                    // declaration
Pet p = new Dog("Rex");                              // implicit conversion from each case
var name = p switch { Dog d => d.Name, Cat c => c.Name, Bird b => b.Name }; // exhaustive, no default arm
```

- **Lowering.** `union` lowers to a `struct` marked `[Union]` and implementing `IUnion { object? Value }`. Case types can be classes, structs, interfaces, type parameters, nullable types or other unions. Generics work: `union Option<T>(None, Some<T>)`. ([reference][union-ref])
- **Custom unions.** Any class or struct with `[Union]`, single-parameter constructors and a `Value` property is a union. Adding `HasValue` and `TryGetValue(out T)` gives non-boxing pattern matching. A nested `IUnionMembers` interface supports factories and private constructors. ([reference][union-ref])
- **Status.** In the RC1 SDK, C# 15 is the default language version for `net11.0`, so you don't need `LangVersion=preview` (**verified**). Microsoft notes that some parts of the proposal aren't implemented yet. ([what's new][cs15])
- **Closed hierarchies** (`closed record Shape;`) are also in C# 15. They give exhaustiveness for class hierarchies you own.

Unions are the better fit for TypeScript because TS unions are ad hoc. The same type appears in many unrelated unions: ArcGIS's `ScreenPoint` is in both `ScreenPoint | ScreenRect` and `MouseEvent | ScreenPoint | ScreenRect`. A class can only have one base, so closed hierarchies can't model that.

### 1.2 System.Text.Json union support (.NET 11)

From the [STJ union docs][stj-union], plus what was verified here:

| Behaviour | Status |
|---|---|
| A union serializes as its active case, with no wrapper or discriminator | ✅ verified, also through `Microsoft.JSInterop` (Blazor's path): `["A1"]`, `[42]` |
| Deserialization picks the case by JSON **token kind** (string/number/bool/array/object) | ✅ verified: `ColorInput(string, double[], Rgba)` round-trips all three |
| `[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]` picks object cases by **property names** | ✅ verified: narrows `{x,y,width,height}` to `ScreenRect` |
| …but it is rejected *when the classifier is built* if one case's names are a subset of another's and nothing stricter separates them | ✅ verified: `ScreenPoint \| LooseScreenRect` (with optional `width`/`height`) throws "case type … can never be selected uniquely" |
| The structural classifier never inspects property **values**, so `type: "point"` is invisible to it | documented; worked around with a custom classifier (P3) |
| Custom classifiers: `JsonTypeClassifierFactory` → `delegate Type JsonTypeClassifier(ref Utf8JsonReader)` | ✅ verified; it works inside Blazor's serializer |
| `JsonSerializerOptions.Web` makes `union(int/double, string)` ambiguous, because numbers can be read from strings | ✅ verified (throws) |
| The source generator reports ambiguous unions at **build time** (`SYSLIB1227`) | ✅ verified: a free validity check on generated code |
| Custom non-boxing `[Union]` structs serialize, by reflection and source generation | ✅ verified |
| Compiler-generated unions box value-type cases | ✅ verified: 100k create+match of a `double` allocates 2,400,000 B, against 0 B for a custom union |
| Union declarations compile on **`net10.0`** with a polyfilled `UnionAttribute`/`IUnion` | ✅ verified. **STJ on .NET 10 does not understand them** and writes `{"Value":{"Name":"Rex"}}` |

One Blazor constraint shapes the design. `JSRuntime.JsonSerializerOptions` is `protected internal` (**verified**), so application code can't register converters or classifiers on Blazor's interop serializer. **Everything has to be attached by attribute** (`[JsonUnion(TypeClassifier=…)]`, `[JsonConverter(…)]`). A generator does that naturally.

### 1.3 Interop APIs you would generate against

- **`IJSRuntime` / `IJSObjectReference`** (Server, WASM, Hybrid). .NET 10 added `InvokeConstructorAsync`, `GetValueAsync<T>`, `SetValueAsync<T>` and synchronous variants on `IJSInProcessRuntime`/`IJSInProcessObjectReference` ([docs][blazor-call-js]). These map one-to-one onto constructors, properties and methods. Before .NET 10, every property access needed a hand-written JS function. Synchronous calls are WASM-only. On Server every call is a SignalR round trip.
- **`[JSImport]`/`[JSExport]`** (WASM only): faster, source-generated marshalling, but only for primitives, strings, `JSObject`, `Task`, `Action`/`Func`, `Span`/`ArraySegment` and a few array types. There is no JSON object graph, so DTOs must travel as JSON strings or be set property by property. A [runtime PR][jsexport-pr] reworks `JSExport` registration for CoreCLR/NativeAOT in the 11.0 milestone. CoreCLR-on-WASM is [in progress][coreclr-wasm], with stabilisation aimed at .NET 12, so keep the JSImport backend optional.
- **ASP.NET Core 11** adds no new interop primitives. It adds a renamed Blazor Web Worker template (`InvokeVoidAsync`, cancellation and timeouts on worker calls) and the BL0010 analyzer (`InvokeVoidAsync` instead of `InvokeAsync<object>`) ([release notes][aspnet11]).

---

## 2. What TypeScript actually throws at a generator (measured)

[`tools/dts-survey/survey.mjs`](../tools/dts-survey/survey.mjs) loads each library's own `.d.ts` files into the TypeScript 6.0.3 checker. It resolves every property, accessor, parameter, return type, type alias and index signature, and puts each resolved type in exactly one bucket. The tables below come from that run. Re-running the tool regenerates them, plus raw data with examples per bucket; results aren't committed because they quote third-party typings.

### 2.1 How much of each library maps mechanically?

| Library | Positions | Direct mapping¹ | Needs a C# union² | Needs judgement³ | Distinct real-union shapes | Distinct literal unions |
|---|---:|---:|---:|---:|---:|---:|
| @arcgis/core 5.1 | 32,585 | 87.0% | 7.6% | 5.4% | 827 | 795 |
| @arcgis/map-components 5.1 | 10,503 | 81.7% | 2.4% | 15.9% | 76 | 192 |
| @types/google.maps | 4,815 | 82.4% | **12.5%** | 5.2% | 83 | 69 |
| @types/leaflet | 2,369 | 67.6% | 8.0% | **24.4%** | 49 | 16 |
| ol (OpenLayers) | 11,508 | 70.1% | 6.5% | 23.4% | 145 | 57 |
| maplibre-gl | 8,413 | 86.5% | 4.0% | 9.5% | 93 | 74 |
| cesium | 11,851 | 90.3% | 5.0% | 4.7% | 142 | 5 |
| chart.js | 4,164 | 70.7% | 6.5% | 22.9% | 115 | 38 |
| echarts | 23,081 | 75.0% | 11.4% | 13.6% | 819 | 286 |
| @types/three | 20,068 | 73.9% | 7.4% | 18.7% | 283 | 137 |
| monaco-editor (editor.api) | 2,723 | 90.5% | 5.2% | 4.3% | 79 | 62 |
| lib.dom.d.ts (WebIDL-derived) | 19,273 | 84.7% | 9.9% | 5.4% | 138 | 246 |

¹ primitive, class/interface, array, `void`, TS enum, literal union (→ enum), type parameter. ² primitive, mixed, discriminated or untagged object unions (after removing `null`/`undefined`). ³ callbacks, `any`/`unknown`, intersections, tuples, unresolved type-level expressions.

The "needs judgement" column is dominated by `any` in hand-written or JSDoc-generated typings (OpenLayers 17.8%, chart.js 18.4%, Leaflet 10.8%), and by intersections in three.js (13.3%: mixins and node-typing tricks). Libraries whose typings are generated from a richer model (ArcGIS, Cesium, Monaco, the DOM) come out cleanest. **The quality of the typings matters more than the size of the library.**

### 2.2 Which kinds of union?

Union positions as a share of all positions:

| Kind | Example (real) | Overall share | Hardest library |
|---|---|---:|---|
| Literal union → enum | `"circle" \| "square" \| "diamond" …` | 3.7% | ArcGIS 6.2%; widest union **2,631 members** (`Widget.icon`, Calcite icon names) |
| Mixed (primitive + object/array/function) | `string \| LatLng \| Place \| LatLngLiteral` (Google `DirectionsRequest.origin`) | **5.0%** | Google Maps 9.6%, DOM 9.0% |
| Untagged object union | `LatLng \| LatLngLiteral`, `ScreenPoint \| ScreenRect` | 1.1% | Google Maps 2.8% |
| Discriminated object union | `(PointData & {type:"point"}) \| …` | 0.8% | ArcGIS 3.1% (377 distinct) |
| Primitive union | `string \| number` | 0.8% | echarts 3.2% |

Unions are small: **2–4 members at the median**, 4–13 at the 90th percentile (map-components excepted, where the 2,631-member icon enum dominates). Arity isn't the problem; classification is.

### 2.3 Everything else that hurts

| Pain signal | ArcGIS core | Others (highest) |
|---|---:|---|
| Positions that accept `null` **and** `undefined` | **19.8%** | Google Maps 19.1% |
| Positions that are nullable or optional at all | 32.8% | Google Maps 47.9%, echarts 39.2% |
| Getter/setter with **different** types | **1,553** | three.js 192, Google 158 |
| Methods returning `Promise` | **883** | DOM 239 |
| Callback-typed parameters | 47 | **DOM 1,013**, echarts 366, Google 260 |
| Overloaded methods | 17 | **DOM 542**, echarts 141, Google 114 |
| Utility-type references (`Partial`, `Pick`, `Omit`, `Record`…) | **1,112** | echarts 399, maplibre 170 |
| `typeof X` in type positions | 1,082 | three.js 1,165, echarts 874 |
| Indexed-access types (`T["k"]`) | 434 | map-components 2,575, echarts 846 |
| Conditional types | 3 | echarts 111, three.js 58 |
| Generic declarations | 270 | echarts 926, DOM 558 |

---

## 3. Pain-point catalogue

For each pain point: what it is, how often it occurs, the recommended mapping, and whether C# 15 helps.

### P1. String-literal unions → enums *(C# 15 not needed)*
**What:** `style: "circle" | "square" | …`. This is the most common union by far: 795 distinct ones in ArcGIS.
**Map to:** `enum` + `[JsonStringEnumMemberName("simple-marker")]` + `JsonStringEnumConverter<T>` (verified both directions).

Watch for:
- **Open-ended unions**: `"a" | "b" | (string & {})`, rare in this sample but common elsewhere. Map these to an *extensible enum*: a `readonly record struct` wrapping a string, with static known values (the Azure SDK pattern). A closed enum would reject future values.
- **Huge unions** (2,631 icons): emit them as an extensible enum too, to keep the API browsable and binary size sane.
- **Number-literal unions** (`precision?: 1 | 2 | 4`) need integer-valued enums or plain `int`.
- **Naming**: `"x"`, `"3d"`, `"simple-marker"` → `X`, `_3d`/`ThreeD`, `SimpleMarker`. This needs one deterministic, documented rule plus profile overrides.

### P2. Real unions *(C# 15 helps the most here)*
**Map to:** `union` declarations. Rules:
- **De-duplicate after mapping, not before.** A typical color input, `string | number[] | [number, number, number] | RGBA`: the tuple collapses into the same `double[]` as `number[]`. Emit `union ColorInput(string, double[], Rgba)`.
- **Flatten nested unions.** A union inside a union doesn't get a single implicit conversion from the inner cases. `ShapeOrData(Point, Line, PointData, LineData, BoxData)` is flat, and then `SetShapeAsync(point)` and `SetShapeAsync(new LineData{…})` both compile (verified).
- **Give every union `[JsonUnion(TypeClassifier = typeof(JsUnionClassifier))]`** (see P3). Don't rely on Blazor-wide options, because you can't set them.
- **Primitive and hot-path unions**: emit the non-boxing custom-union pattern (`[Union] struct` + `TryGetValue`). The compiler-generated form allocates 24 B per value-type case (verified).
- **Nullable unions**: model `T | null` as `Union?` (`Nullable<Union>`). Trap: `nullableUnion.Value` is `Nullable<T>.Value` (the union), *not* the union's content. I hit this as a compile error (CS0030) in the prototype. Pattern matching sees through both layers, so steer users to patterns in docs and samples.
- **Parameters:** prefer the union over generating N overloads. Glutinum generates overloads ([blog][glutinum]), but implicit union conversions give the same call-site ergonomics with one method. Keep overloads for real TS overloads (P9).

### P3. Choosing the case on the way back *(C# 15 helps partly; you need your own classifier)*
TypeScript code discriminates by **value** (`if (g.type === "point")`). STJ's built-in classifiers look at token kinds and **names** only. The prototype's [`JsUnionClassifier`](../prototype/UnionInterop/Runtime/JsDiscriminator.cs) (~100 lines) handles the three cases a JS library needs:
1. `{"__jsObjectId": n, "__jsType": "demo.Line"}` → the proxy case whose `[JsProxyType]` matches.
2. A discriminator property value (`"type": "polyline"`) → the case marked `[JsDiscriminator("type","polyline")]`.
3. Otherwise, the token kind, if exactly one case fits. Anything else throws, naming the candidates.

The TypeScript checker already knows which property is the discriminant (the survey finds it for 377 ArcGIS unions), so the generator can emit these attributes mechanically. For **untagged** object unions, fall back to `JsonUnionTypeStructuralClassifier` only when the extractor proves the cases are separable by *required* member names. Otherwise emit a profile hook. Remember that TS optional members make most of them inseparable (verified failure mode).

### P4. `undefined` vs `null` vs value *(C# 15 doesn't help)*
19.8% of ArcGIS positions accept both `null` and `undefined`. In JS these differ: absent means "use the default", `null` means "clear it". C# `T?` collapses them. The prototype is a cautionary tale: a plain `double? Z` sent `"z":null` to JS for a property the user never set.

**Map to:** `JsOptional<T>` (prototype [`Runtime/JsOptional.cs`](../prototype/UnionInterop/Runtime/JsOptional.cs)). It is a three-state struct whose `default` is *undefined*, combined with `[JsonIgnore(Condition = WhenWritingDefault)]`, so undefined is omitted, null is written and values are written (verified in both directions). Use it only where the IR says both states are accepted. Use plain `T?` + `WhenWritingNull` everywhere else, so the API doesn't get noisy.

### P5. Getter ≠ setter (autocasting) *(C# can't express it; needs a convention)*
```ts
// illustrative shape of the pattern
get shape(): Point | Line | null | undefined;
set shape(value: Point | Line | (PointData & { type: "point" }) | (LineData & { type: "line" }) | null | undefined);
```
There are 1,553 of these in ArcGIS 5.1. A C# property has one type. Emit `GetXAsync(): ValueTask<Getter>` and `SetXAsync(SetterUnion)`, where the setter union is the flattened union of the instance types and the property-bag types. Structural typing is why both belong there: a `Point` instance *is* a valid `PointData & {type:"point"}` in TS.

### P6. By value or by reference? *(the central design decision)*
Every TS type has to be classified:
- **DTO** (serialized JSON copy): option bags, `*Properties`, `RGBA`, `ScreenPoint`.
- **Proxy** (handle to a live JS object): `class` instances, anything with methods or identity (`Graphic`, `MapView`, `Collection<T>`).

Getting this wrong is the classic failure mode. A class instance JSON-serialized by JS loses its identity, and ArcGIS's `toJSON()` even produces a *different* schema (Esri JSON, no `type`). Heuristics that work: `declare class` → proxy; interfaces whose members are all data → DTO; `*Properties` interfaces → DTO. Profile overrides cover the rest.

Proxies in unions need a **runtime type tag**. Blazor serializes handles as `{"__jsObjectId": n}`, which says nothing about the class. The generated JS shim adds `__jsType` (ArcGIS has `declaredClass`; elsewhere use `constructor.name` or the discriminant). Blazor's own `IJSObjectReference` reader then **rejects** the extra property (verified: "Unexpected JSON property __jsType"), so proxies need their own converter, which strips it ([`Runtime/JsProxy.cs`](../prototype/UnionInterop/Runtime/JsProxy.cs)).

Related: .NET can't tell `blazor.*.js` how to serialize a *nested* return value (handle or JSON?). Only the top-level `JSCallResultType` exists. **A generic generator therefore has to emit a JS half**: a shim with per-member marshalling metadata. That is the main thing Blazor's built-in interop doesn't give you.

### P7. Type-level computation *(needs the checker, not a parser)*
- Constructor bags declared as `interface PointData extends ShapeData, Partial<Pick<Point, "x" | "y" | "z">> {}`: the property bag is *computed* from the class.
- Event APIs typed as `on<K extends keyof this["eventMap"]>(type: K, listener: (e: this["eventMap"][K]) => void)`: the event list lives in a phantom property, and the handler type is an indexed access on `this`. ArcGIS uses exactly this pattern.
- Web-component props that reuse class property types through indexed access, such as `center: View["center"] | number[] | string`.

A syntax-level converter (ts2fable-style, or most hand-rolled generators) can't resolve these. The extractor must ask the checker for the *resolved* members (`checker.getPropertiesOfType`, `getTypeOfSymbolAtLocation`) and record them in the IR. The survey counted 1,112 utility-type references and 434 indexed accesses in ArcGIS alone.

For events: enumerate the properties of the resolved event-map type and emit a typed subscription per event, e.g. `view.OnClickAsync(Func<ViewClickEvent, Task>)` returning `IAsyncDisposable`, instead of a stringly-typed `on("click", …)`.

### P8. Everything is async, and chatty
883 ArcGIS methods return `Promise`, and *every* property read is a round trip over `IJSRuntime`. On Blazor Server, that is a network round trip each time. Ideas:
- **Batching:** generated "bulk" setters, or a queued-operation API flushed in one `InvokeVoidAsync` that the JS shim replays.
- **Sync variants:** generate synchronous members for `IJSInProcessObjectReference` (WASM only, behind a flag).
- **Read once:** `GetSnapshotAsync()`, which returns a DTO of all data properties in one call.

### P9. Callbacks, events, overloads
- **Callbacks** (1,013 DOM callback parameters) need `DotNetObjectReference` plumbing and disposal. ArcGIS's listener type also accepts a `WeakRef` to the function, which shows that JS libraries expect weak listeners. Make generated subscriptions `IAsyncDisposable` and finalizer-safe.
- **Overloads** (542 in the DOM) map to C# overloads, *except* when two TS overloads collapse to the same C# signature after mapping (string literal parameter types, for instance). Merge those into a union or enum parameter.
- **`AbortSignal` ↔ `CancellationToken`** can be bridged generically in the shim.

### P10. Generics, intersections, `this`
`Collection<T>`, `Promise<T>`, `Map<K,V>` and simple `T extends X` map fine. Generics are mostly erased at runtime, so emit reified proxies (`Collection<Graphic>`). Intersections that are *bags* (`A & {type:"x"}`, `Partial<X> & T9nMeta<…>`) should be flattened by the checker into one generated DTO. Intersections used for mixins (three.js) map to C# interfaces. A `this` return becomes the declaring proxy type (or a `TSelf` CRTP generic where fluency matters).

### P11. Lifetimes and modules
Proxies wrap `IJSObjectReference`s that must be disposed. Blazor Server throws `JSDisconnectedException` on disposal after a circuit drop ([docs][blazor-call-js]), so generated `DisposeAsync` must swallow it. ES modules: the shim should `import()` the library module (npm/ESM or the ArcGIS 5 `$arcgis.import`), so that consumers never write `window.*` glue.

### P12. Trimming, AOT and versions
- Blazor's interop serializer uses reflection, but the classifier and converters are trim-safe as long as they're `DynamicallyAccessedMembers`-annotated. The STJ source generator's `SYSLIB1227` diagnostic catches ambiguous unions at build time.
- **Regenerate per library version** and diff the IR. ArcGIS 4.x → 5.x changed loading, typings style (`accessor`, `Partial<Pick<…>>` bags) and the component model.
- Map JSDoc → XML docs, and `@deprecated` → `[Obsolete]`.

---

## 4. How generic can it be? A proposed architecture

```
 .d.ts ───► Extractor (TS checker) ──────┐
 WebIDL ──► Extractor (webidl2) ─────────┼──► IR (versioned JSON) ──► Mapper ──► C# model ──► Emitters ──┬─► *.g.cs   (DTOs, enums, unions, proxies)
 custom-elements.json ► Extractor (CEM) ─┘                              ▲                                 ├─► *.g.js   (JS shim: marshalling, __jsType, events)
                                                                        │                                 └─► *.razor  (web-component wrappers, optional)
                                                          Library profile (data):
                                                          DTO/proxy rules, discriminators, runtime type tag,
                                                          event conventions, renames, skips, union overrides

 Runtime library (NuGet): JsProxy, JsOptional<T>, JsUnionClassifier, JsEvent/IAsyncDisposable subscriptions,
                          batching, backends: IJSRuntime (Server/WASM/Hybrid) → JSImport (WASM fast path)
                                              → Jint / ClearScript (no browser) → node-api-dotnet (Node.js)
```

**Why an IR?**
- It separates the hard, TypeScript-specific part (resolving types) from the hard, .NET-specific part (choosing mappings). Each half can then be tested in isolation, and IR snapshots are diffable.
- It lets the extractor stay in TypeScript, where the checker lives. TypeScript 7.0 (Go, GA July 2026) [ships without a stable programmatic API][ts7]; it's planned for 7.1+. So **use the TS 6.x JS API now**, keep the IR stable, and swap the extractor later.
- WebIDL (DOM, the cleanest source: 84.7% direct) and Custom Elements Manifests become additional front ends rather than separate tools.

**What goes in a profile.** These are the things that made your old generator project-specific. For ArcGIS they'd be roughly:
- `*Properties` interfaces are DTOs; classes are proxies.
- `declaredClass` is the runtime type tag.
- `type` is the discriminator.
- `"@eventTypes"` defines events.
- `Collection<T>` is a live proxy collection.
- `when()` → `Task`.
- Renames for literal values (`"3d"`).

Expect a few dozen rules per large library, not thousands of lines of special-case code.

**Delivery.** Ship a CLI (`dotnet tool`) that writes checked-in `*.g.cs`/`*.g.js`; a library the size of `@arcgis/core` (1,652 `.d.ts` files) is too big for a source generator on every build. Optionally add a Roslyn incremental source generator that consumes a pre-built IR JSON (`AdditionalFiles`) for small, app-specific bindings.

**Targets.** Emit `union` declarations for `net11.0`+. For `net10.0` (LTS until Nov 2028), emit the same declarations plus the polyfill and a generated `JsonConverter` per union. Verified: the language feature works there, and .NET 10's STJ doesn't.

**A realistic generality estimate**, based on §2:

| Share of positions | Approach |
|---|---|
| 70–90% | Fully automatic, no configuration |
| 2–12% | Unions: automatic with C# 15, given checker-derived classifier attributes (P3) |
| 5–25% | The long tail, concentrated in a handful of patterns per library: profile rules for `any`, callbacks, intersections and computed types |

Treat the survey's "needs judgement" column as a **complexity budget** before taking on a library.

---

## 5. Where this pays off

**Blazor component and binding libraries** (the core audience):
- Mapping: ArcGIS, Google Maps, Leaflet, OpenLayers, MapLibre, Cesium.
- Charts: Chart.js, ECharts, Plotly.
- Editors: Monaco, CodeMirror.
- 3D: three.js, Babylon.js.
- SDKs: Stripe.js, MSAL.js, Firebase.

Most existing Blazor wrappers are hand-written and lag the JS releases. For reference, [GeoBlazor][geoblazor] keeps generated `.gb.cs`/`.gb.ts` pairs beside hand-written code, and [SpawnDev.BlazorJS][spawndev] hand-wraps 1,000+ Web APIs with its own `Union<…>` types. Both show the demand, and the cost of not having a general tool.

**Web components → Razor components:**
- `@arcgis/map-components` ships a Custom Elements Manifest (schema 2.1.0) with 107 elements. `arcgis-map` alone has 72 members, 26 events and 18 attributes.
- The manifest gives the component surface (attributes, events, slots). The `.d.ts` checker gives the real types: the manifest stores types as TS text such as `View["center"] | …`.
- Given Esri's widget removal plan ([transition plan][esri-components]), this is probably the highest-value target for your ArcGIS work.

**Browser APIs via WebIDL:** WebGPU, WebRTC, WebAudio, File System Access, Web Bluetooth. This is what Rust's `web-sys` and Dart's `package:web` do. The DOM typings were the cleanest input in the survey.

**Blazor Hybrid (MAUI/WPF/WinForms):** it uses the same `IJSRuntime`, so the same bindings work unchanged.

**Web Workers:** the .NET 11 Blazor Web Worker template exchanges messages with JS. Typed message contracts with union payloads are the same problem.

**.NET without a browser:**
- [Jint][jint]/[ClearScript][clearscript] can host DOM-free JS libraries (turf.js, proj4js, date-fns, ajv, marked, mathjs) behind the same generated API with a different backend.
- [node-api-dotnet][node-api-dotnet] can do the same for Node.js packages. Its maturity is worth checking before you rely on it.

**The reverse direction:** generate `.d.ts` for `[JSExport]`ed .NET APIs. The IR and mapping rules run backwards.

**Beyond JS:** `JsUnionClassifier`-style, value-aware classification is equally useful for OpenAPI `oneOf`/`anyOf` and GeoJSON, which have the same tagged and untagged unions.

---

## 6. Other thoughts and ideas

- **Learn from the graveyard.**
  - Kotlin's [dukat][dukat] reportedly lost its Gradle integration (Kotlin 1.8.20) and is largely unmaintained; kotlin-wrappers now generates with [Karakum][karakum].
  - Bridge.NET's Retyped (generated C# bindings for thousands of npm packages) was abandoned along with Bridge.
  - Fable's ts2fable was replaced by [Glutinum][glutinum-cli], which uses an intermediate AST, generates overloads for parameter unions, and still struggles with open string unions ([issue #27][glutinum-27]).
  - [ScalablyTyped][scalablytyped] is the success story: it converts DefinitelyTyped for Scala.js. That works because Scala.js always had a union encoding (`A | B`), which Scala 3 then made native.

  The common lesson: **"convert everything perfectly" fails, while "convert everything predictably, plus per-library overrides" survives.**
- **Test the bindings, not just the generator.**
  - Generate a smoke test per proxy that constructs the object in headless Chromium (via Playwright) and round-trips every DTO and union through the real JS library.
  - Commit the prototype's `FakeJsRuntime` approach as unit tests: it runs Blazor's real serializer with no browser.
- **Snapshots instead of chatty getters:** a generated `ToSnapshotAsync()` that returns a DTO of all data members in one call is often what Blazor Server users actually want.
- **Reactive surfaces:** map ArcGIS `reactiveUtils.watch(() => view.zoom)` and similar to `IAsyncEnumerable<T>` or `IObservable<T>`, implemented in the shim with one `DotNetObjectReference`.
- **Use the TypeScript checker for docs too:** JSDoc → `<summary>`, `@default` → `<remarks>`, `@see` → `<seealso href>`, `@since` → a generated `[JsSince("4.0")]` for compatibility analyzers.
- **Ship the survey as a CLI subcommand** (`jsbind analyze <package>`). Before generating, it tells a user how much profile work a library will need.
- **Closed hierarchies still have a place:** for the proxy *class* hierarchy you own (`Layer` → `FeatureLayer`, …), `closed` on generated abstract bases gives exhaustive switches over "all layer types" for free. It complements unions for the ad hoc cases.
- **LLM assistance belongs in profile authoring** (suggesting renames, DTO/proxy classification, discriminators), not in the emitter. The emitter must stay deterministic.

---

## 7. Open questions and risks

- **The CoreCLR-on-WASM transition** (.NET 11/12) may change JSImport/JSExport. Keep that backend optional.
- **.NET 11 is STS.** Many Blazor shops will stay on .NET 10 LTS, hence the dual-target emission (§4).
- **Some TypeScript won't map:** deeply generic conditional APIs (echarts option typing: 111 conditional types), declaration merging and global augmentation, and template-literal-typed APIs (Google Maps has 74 template literal types). Plan for `JsAny` escape hatches (a `JsonElement`/`JSObject` wrapper) plus profile overrides, and be explicit about them.
- **Union semantics may still move:** some proposal features are "coming in future previews" ([what's new][cs15]). Pin the SDK and re-run the prototype checks on each update.

---

## 8. Suggested roadmap

1. **IR + extractor** (TS 6.x checker). Start from `tools/dts-survey`, which already walks every position and resolves types. Golden-file test the IR on Leaflet (small) and a slice of `@arcgis/core`.
2. **C# emitter for the data plane:** enums, DTOs, unions with classifier attributes, `JsOptional`. Reuse the prototype's runtime pieces and turn `Program.cs` into tests.
3. **Proxies + JS shim:** constructors, properties (`GetValueAsync`/`SetValueAsync`), methods, `__jsType`, events, disposal. Add profiles at this point.
4. **Custom Elements Manifest → Razor components** for `@arcgis/map-components`.
5. **Second backends** (JSImport fast path, then Jint) and a `net10.0` converter target.
6. **WebIDL extractor;** move to the TS 7.1 API when it is stable.

---

## Appendix A: what is in this repository

| Path | What it is |
|---|---|
| [`tools/dts-survey/`](../tools/dts-survey) | TypeScript-checker-based survey of `.d.ts` libraries (results regenerate locally) |
| [`prototype/UnionInterop/`](../prototype/UnionInterop) | C# 15 prototype: runtime pieces (`Runtime/`), hand-written "generator output" for a small made-up library (`Generated/Sample.cs`), and 18 checks run through the real `Microsoft.JSInterop` serializer (`Program.cs`, `FakeJsRuntime.cs`) |

Prototype output (`dotnet run` in `prototype/UnionInterop`, .NET 11 RC1):

```
PASS  literal union -> enum (out)                        ["diamond"]
PASS  string | number (out, both cases)                  ["A1"]  [42]
PASS  ColorInput (in): string / array / object           #ff0000 | double[4] | Rgba { R = 255, ... }
PASS  shape data bag (out) keeps the type tag           [{"type":"point","x":1,"y":2,"z":null}]
PASS  shape data bag (in) chosen by tag value           LineData, BoxData
PASS  ScreenPoint | ScreenRect: {x,y,width,height}       -> ScreenRect
PASS  ...optional distinguishing members                 throws: case type 'ScreenPoint' can never be selected uniquely
PASS  Blazor's IJSObjectReference reader                 throws: Unexpected JSON property __jsType
PASS  Shape (Point | Line proxies)                      handle routed by __jsType -> Line
PASS  setter accepts instance OR property bag            ["shape",{"__jsObjectId":9}]  ["shape",{"type":"line",...}]
PASS  optional + nullable                                [{"shape":{...},"color":null}]   (title omitted)
PASS  JsonSerializerOptions.Web + string|number          throws: ambiguous
PASS  non-boxing custom union                            `union` 2,400,000 B vs custom 0 B per 100k
```

## Appendix B: sources

- C# union types reference: [learn.microsoft.com][union-ref] · What's new in C# 15: [learn.microsoft.com][cs15] · .NET blog, "Explore union types in C# 15": [devblogs][dotnet-blog-unions]
- System.Text.Json union support: [learn.microsoft.com][stj-union]
- Blazor: call JS from .NET (`InvokeConstructorAsync`, `GetValueAsync`, …): [learn.microsoft.com][blazor-call-js] · ASP.NET Core 11 release notes: [learn.microsoft.com][aspnet11] · JSImport/JSExport: [learn.microsoft.com][jsimport]
- dotnet/runtime JSExport registration rework: [PR 122024][jsexport-pr] · CoreCLR on WASM: [issue 121511][coreclr-wasm]
- TypeScript 7.0 GA without a stable API: [InfoQ][ts7] · [Visual Studio Magazine][ts7-vsm]
- ArcGIS components transition: [Esri][esri-components] · [5.0 release][esri-5] · [widget deprecation][esri-deprecation]
- Prior art: [Glutinum blog][glutinum] · [Glutinum CLI][glutinum-cli] · [Glutinum #27][glutinum-27] · [Fable TS features][fable-ts] · [ScalablyTyped][scalablytyped] · [dukat][dukat] · [Karakum][karakum] · [SpawnDev.BlazorJS unions][spawndev] · [GeoBlazor][geoblazor] · [BlazorTS][blazorts] · [Custom Elements Manifest][cem] · [node-api-dotnet][node-api-dotnet] · [Jint][jint] · [ClearScript][clearscript]

[union-ref]: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/union
[cs15]: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15
[dotnet-blog-unions]: https://devblogs.microsoft.com/dotnet/csharp-15-union-types/
[stj-union]: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/union-types
[blazor-call-js]: https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/call-javascript-from-dotnet?view=aspnetcore-10.0
[aspnet11]: https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11
[jsimport]: https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop
[jsexport-pr]: https://github.com/dotnet/runtime/pull/122024
[coreclr-wasm]: https://github.com/dotnet/runtime/issues/121511
[ts7]: https://infoq.com/news/2026/08/typescript-7-released/
[ts7-vsm]: https://visualstudiomagazine.com/articles/2026/07/08/typescript-7-arrives-to-rock-vs-code-with-go-powered-speed.aspx
[esri-components]: https://developers.arcgis.com/javascript/latest/components-transition-plan/
[esri-5]: https://www.esri.com/arcgis-blog/products/js-api-arcgis/announcements/arcgis-maps-sdk-for-javascript-whats-new-in-5-0
[esri-deprecation]: https://support.esri.com/en-us/knowledge-base/deprecation-widgets-in-arcgis-maps-sdk-for-javascript-000040437
[glutinum]: https://fable.io/blog/2024/2024-01-01-Glutinum_a_new_era.html
[glutinum-cli]: https://github.com/glutinum-org/cli
[glutinum-27]: https://github.com/glutinum-org/cli/issues/27
[fable-ts]: https://fable.io/docs/typescript/features.html
[scalablytyped]: https://github.com/ScalablyTyped/Converter
[dukat]: https://github.com/Kotlin/dukat
[karakum]: https://github.com/karakum-team/karakum
[spawndev]: https://github.com/LostBeard/SpawnDev.BlazorJS/blob/main/Docs/union-types.md
[geoblazor]: https://github.com/dymaptic/GeoBlazor
[blazorts]: https://www.nuget.org/packages/BlazorTS
[cem]: https://github.com/webcomponents/custom-elements-manifest
[node-api-dotnet]: https://github.com/microsoft/node-api-dotnet
[jint]: https://github.com/sebastienros/jint
[clearscript]: https://github.com/microsoft/ClearScript
