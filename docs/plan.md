# Plan

The build order for the [design](design.md), in four phases. Each phase has one tracking issue with this checklist. Open the next phase's issue when the current one closes.

Every item is done when it has tests and the checks in [`CLAUDE.md`](../CLAUDE.md) pass. Tests that exercise a mapping rule cite it (`R19`).

## Phase 1 · Runtime core

Hand-written bindings for a slice of the fixture, running on every host.

- [x] **Scaffold.** `Interoper.slnx`, `Directory.Build.props` (`net11.0`, nullable, `TreatWarningsAsErrors`), central package versions (`Directory.Packages.props`) and `.editorconfig`.
  - Projects: `src/Interoper.Runtime` and `src/Interoper.Blazor` (Razor class libraries, so `wwwroot/interoper.js` ships as a static web asset), and `tests/Interoper.Runtime.Tests` on xUnit v3, so that plain `dotnet test Interoper.slnx` works.
  - Other projects are added by the item that first needs them: `tests/Interoper.E2E` with the browser harness, `src/Interoper.Generator` in phase 2, `src/Interoper.Tool` in phase 4.
- [x] **Fixture library v0** in `fixtures/demo-maps`. It needs the JS library, a hand-written `.d.ts` covering every pattern in [design §6](design.md#6-testing), `custom-elements.json`, `profile.json`, and a `PATTERNS.md` table mapping each pattern to its R-number. Acceptance: `tsc --noEmit` passes, and it has `node --test` coverage.
- [x] **Browser test harness** in a new `tests/Interoper.E2E` project. Build a Blazor test host (Server + WebAssembly) driven by Microsoft.Playwright, with one smoke test per mode and host. Port it from the hosts experiment ([`7d48a23/spikes/hosts`](https://github.com/ConnorStevenson22/JsCSharpInteroper/tree/7d48a23/spikes/hosts)).
- [x] **Value model.** Add `Undefined`, `Opt<T>`, the union pattern and JSON helpers for the three states (R1–R6). Port the value-model experiment's checks as tests ([`7d48a23/spikes/value-model`](https://github.com/ConnorStevenson22/JsCSharpInteroper/tree/7d48a23/spikes/value-model)).
- [x] **`interoper.js`.** Build the handle table, the ops (`create`, `get`, `set`, `call`, `snapshot`, `apply`, `subscribe`, `release`, `batch`), the `$h`/`$f`/`$u`/`$c` reviver, error mapping, and entry points for both seams. Tests live in `tests/interoper-js`.
- [x] **Seams and transports.** Implement `IJsSyncTransport`, `IJsTransport`, `JsImportTransport`, `JsRuntimeTransport`, the callback registry and `InMemoryTransport`. The same contract tests run in-memory and in the browser.
- [x] **Realm, scopes and identity.** Add `AddInteroper()`, the per-realm transport choice, `JsScope`, the identity cache and the most-derived-type registry. The handle census must return to baseline after disposal.
- [x] **Batching.** Add `BeginBatch()`, flush-on-read, `JsBatchException` and a round-trip counter. N writes must be one round trip.
- [x] **Golden slice** in `tests/GoldenSlice`. Write the bindings the generator should emit, by hand, in both modes, citing R-numbers. Cover `Shape`/`Point`/`Line`/`Polygon`, `MapView` (properties, a Promise method, a `click` event), `Marker` (getter ≠ setter), one union, one enum, one watch, one module function and their snapshots.

**Done when** the golden slice passes one behavioural suite per mode in each of these places: in-memory, on WebAssembly (sync and async), on WebAssembly async with `Transport=JsRuntime`, and on Server async. Sync bindings must also fail fast on Server.

## Phase 2 · Generator

- [x] An IR schema v1 and an extractor grown from `tools/dts-survey`. It must use checker-resolved members, accessor pairs, overloads, event maps, discriminants, JSDoc and module specifiers.
- [x] A CEM reader and a golden IR for the fixture.
- [x] Emitter infrastructure: an indented writer, naming (R25–R27) and diagnostics.
- [x] Emitters for value types, snapshot records + JSON, live classes in both modes, and metadata tables.

**Done when** generated code replaces the golden slice with no API changes, the phase-1 tests pass unmodified, and the IR is byte-identical across runs.

## Phase 3 · Full surface

- [x] Events and watches, with options, projection and streams.
- [x] Collections, callbacks (R21), module functions, `Diff`, and the chatty-await analyzer.
- [x] Razor components from the CEM, with custom events, `@bind` and slots.
- [x] Generator gaps the whole fixture reaches: rest parameters (R22), namespaces from module folders (R27), `new` on members a subclass redeclares, and IOP2007 instead of `NotSupportedException`.
- [x] Extractor: default exports, several packages in one IR, re-exports, specifiers through `exports` maps, and declarations for dependency packages' types, as the validation samples need them.

**Done when** the whole fixture is generated and passes on all hosts, and the first private validation samples pass.

## Phase 4 · Ship

- [x] XML docs from JSDoc (R29).
- [x] The import-map helper: the registry's `ImportMap(root)` (design §3.5).
- [x] The `dotnet interoper generate` command, the IR cache, and the report with `--warnings-as-errors`.
- [x] The three NuGet packages and a quickstart ([quickstart.md](quickstart.md)).
- [ ] Latency-throttled Server tests, a trim/size budget, and the Hybrid manual check.

**Done when** `npm install` plus one command works in both modes.

## Later

- A second, permissively licensed public library bound with only a profile.
- A WebIDL front end.
- The TypeScript 7 API, once it is stable.
- Namespaces in the IR (`export * as ns`, `declare namespace`), which the validation samples don't need.
