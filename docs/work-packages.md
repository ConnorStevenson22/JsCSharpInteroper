# Work packages

Delegable tasks for the [roadmap](roadmap.md) milestones. M0 and M1 are broken down fully. Later milestones are outlined and get detailed once M1 lands, because their shape depends on what M1 teaches us.

## How to work a package

- **Read first:** [roadmap.md](roadmap.md), [mapping.md](mapping.md), [`CLAUDE.md`](../CLAUDE.md), plus the package's own inputs.
- **Branch and review:** one branch per package, `wp/<id>-<slug>` (e.g. `wp/1.1-value-model`). Open a PR into `main`; CI must be green.
- **Done means:** the acceptance criteria pass, with tests. Tests that exercise a mapping rule name it in the test name or a comment (`R2`, `R19`, …).
- **Spikes are reference material, not dependencies.** Port what you need into `src/`; never reference `spikes/` from product code.
- **If a package forces a decision change,** update `roadmap.md` / `mapping.md` in the same PR and call it out in the PR description.

Sizes: **S** ≈ a focused session, **M** ≈ a few sessions, **L** ≈ a week of sessions.

## Dependency map

| Package | Depends on | Can start in parallel with |
|---|---|---|
| 0.1 Solution scaffold | none | 0.4 |
| 0.2 CI | 0.1 (browser job also needs 0.3) | 0.3, 0.4, 1.1, 1.2 |
| 0.3 Browser test harness | 0.1 | 0.2, 0.4, 1.1, 1.2 |
| 0.4 Fixture library | none | everything in M0 |
| 0.5 Validation repo | GitHub access to the private repo | everything |
| 1.1 Value model | 0.1 | 0.2, 0.3, 0.4, 1.2 |
| 1.2 `interoper.js` | 0.1 | 0.2, 0.3, 0.4, 1.1 |
| 1.3 Seams and transports | 1.1, 1.2 | |
| 1.4 Realm, scopes, identity | 1.3 | |
| 1.5 Batching | 1.4 | 1.6 |
| 1.6 Golden slice | 1.1, 1.4, 0.4 | 1.5 |
| 1.7 M1 test suites | 1.5, 1.6, 0.3 | |

---

## M0 · Groundwork

### WP-0.1 Solution scaffold · S · depends on: none
- **Deliverables:**
  - `Interoper.slnx`.
  - `Directory.Build.props`: `net11.0`, nullable, implicit usings, `TreatWarningsAsErrors`.
  - `Directory.Packages.props`: central package versions.
  - `.editorconfig`.
  - The projects from [roadmap §8](roadmap.md#8-repository-layout-target), each with a placeholder type. `Interoper.Runtime` and `Interoper.Blazor` are Razor class libraries (`Microsoft.NET.Sdk.Razor`), so `wwwroot/interoper.js` ships as a static web asset.
  - Test projects on xUnit v3, each with one passing test. Pick one test platform, document it in `CLAUDE.md`, and make plain `dotnet test Interoper.slnx` work: either Microsoft.Testing.Platform (the .NET 10+ `dotnet test` mode, configured in `global.json`) or the VSTest adapter.
- **Acceptance:**
  - `dotnet build Interoper.slnx` passes with zero warnings.
  - `dotnet test Interoper.slnx` passes.
  - `dotnet format Interoper.slnx --verify-no-changes` passes.
  - The session hook restores the solution.

### WP-0.2 CI · S · depends on: 0.1 (and 0.3 for the browser job)
- **Deliverables:** `.github/workflows/ci.yml` on `ubuntu-latest`:
  - setup-dotnet from `global.json`;
  - Node 22;
  - `dotnet format --verify-no-changes`, build, unit tests;
  - `node --test "tests/interoper-js/*.test.mjs" "fixtures/demo-maps/test/*.test.mjs"` (Node 22 needs the glob form; a bare folder argument fails);
  - the browser job (Playwright Chromium installed by the workflow).
- **Acceptance:** green on a PR. A deliberate formatting error fails it.

### WP-0.3 Browser test harness · M · depends on: 0.1
- **Inputs:** [`spikes/hosts`](../spikes/hosts): pages per mode/host, the transport switch, `run.mjs`.
- **Deliverables:**
  - In `tests/Interoper.E2E`: a test host Blazor Web App with Server and WebAssembly render modes, plus an xUnit fixture that starts it on a free port and drives it with Microsoft.Playwright.
  - In cloud sessions Chromium is preinstalled under `/opt/pw-browsers`. If the Playwright package's expected browser revision differs, launch with an explicit `ExecutablePath`.
  - One smoke test per page: WASM sync, WASM async, WASM async with `Transport=JsRuntime`, Server async, and Server with sync bindings (must fail fast).
- **Acceptance:** `dotnet test tests/Interoper.E2E` passes locally and in CI.

### WP-0.4 Fixture library v0 · M · depends on: none (lands after 0.1 for CI)
- **Inputs:** [roadmap §6](roadmap.md#6-validation) pattern list; [mapping.md](mapping.md).
- **Deliverables:** `fixtures/demo-maps/`:
  - **A small working JS library** (ES modules, ~10 classes): an abstract `Shape` with `Point`/`Line`/`Polygon`, a `MapView`, a `Layer` + `Collection`, a `Marker` with a getter ≠ setter `shape` property, and a module of functions.
  - **Hand-written `.d.ts`** covering every pattern:
    - discriminated, untagged, mixed and literal unions (one with 100+ members);
    - `null | undefined` positions;
    - an event map via a phantom property and indexed access on `this`;
    - `Partial<Pick<…>>` bags;
    - Promise methods;
    - callbacks, including one whose return value JS uses synchronously.
  - **`custom-elements.json`** for two custom elements (`demo-map`, `demo-legend`) with attributes, properties, events and slots.
  - **`profile.json`** marking value-like classes and the runtime class-name property.
  - **`PATTERNS.md`**: a table from each pattern to its file/line and the mapping rule (R-number) it exercises.
- **Acceptance:**
  - `tsc --noEmit` on the `.d.ts` passes.
  - `node --test "fixtures/demo-maps/test/*.test.mjs"` covers the library's runtime behaviour.
  - Every roadmap §6 pattern is listed in `PATTERNS.md`.

### WP-0.5 Validation repo bootstrap · S · depends on: GitHub access to `JsCSharpInteroper-validation`
- **Deliverables (in the private repo, never here):**
  - `README.md`;
  - `package.json` pinning `@arcgis/core` + `@arcgis/map-components`;
  - `bootstrap.sh`;
  - `.gitignore`;
  - a SessionStart hook (same SDK install as this repo);
  - a placeholder `profiles/arcgis.json`;
  - a CI workflow that checks out both repos side by side.
- **Acceptance:** the private CI runs the bootstrap and builds against a sibling clone of this repo.

---

## M1 · Runtime core

### WP-1.1 Value model · S · depends on: 0.1
- **Inputs:** [`spikes/value-model`](../spikes/value-model); mapping R1–R6.
- **Deliverables:**
  - In `Interoper.Runtime`: `Undefined`, `Opt<T>`, and the union authoring pattern as a documented template (one hand-written reference union in the tests).
  - `Utf8JsonWriter`/`Utf8JsonReader` helpers for the three states: undefined → omit the property; `null` → `null`; absent on read → undefined.
- **Acceptance:** the seven spike checks ported as unit tests (including the CS8509/CS8655 expectations as compile-time test cases), plus JSON round-trip tests for all three states.

### WP-1.2 `interoper.js` · M · depends on: 0.1
- **Inputs:** [`spikes/hosts/Client/wwwroot/interoper.js`](../spikes/hosts/Client/wwwroot/interoper.js); [roadmap §3](roadmap.md#3-runtime-architecture).
- **Deliverables:** `src/Interoper.Runtime/wwwroot/interoper.js` (ES module):
  - **Handle table:** `Map` + `WeakMap` identity; class ids from a registered metadata table, falling back to `constructor.name`.
  - **Ops:** `create`, `get`, `set`, `call`, `snapshot`, `apply`, `subscribe` (with `preventDefault`/`stopPropagation`/projection options), `unsubscribe`, `release` (many ids), and `batch` (an op array, executed in order, errors collected).
  - **JSON reviver** for `$h`/`$f`/`$u`; errors mapped to `{ name, message, stack }`.
  - **Entry points for both seams:** typed fast paths for `[JSImport]`, and a JSON `dispatch` for `IJSRuntime`.
- **Acceptance:** a `node --test` suite in `tests/interoper-js/` (outside `wwwroot`, so tests don't ship as static assets) runs against the fixture library. It covers every op, identity (same object → same id), release, batch ordering and error collection.

### WP-1.3 Seams and transports · M · depends on: 1.1, 1.2
- **Inputs:** [`spikes/hosts/Client/Runtime`](../spikes/hosts/Client/Runtime).
- **Deliverables:**
  - `IJsSyncTransport` and `IJsTransport` (`Task`-based).
  - `JsImportTransport` (implements both).
  - `JsRuntimeTransport` (async; `IJSRuntime` + one `DotNetObjectReference`).
  - The callback registry with its `[JSExport]` entry point.
  - `InMemoryTransport`, implementing both seams: it emulates handles and properties, records ops, and lets tests stub calls.
- **Acceptance:** contract tests run the same scenarios against `InMemoryTransport` (unit) and the browser transports (via the 0.3 harness).

### WP-1.4 Realm, scopes, identity, polymorphism · M · depends on: 1.3
- **Deliverables:**
  - `IJsRealm` and `services.AddInteroper()`.
  - `InteropOptions.Transport` (`Auto`/`JsRuntime`/`JsImport`), resolved per component. Sync mode fails fast off-browser.
  - `JsScope`: sync and async disposal, one `release` call per dispose.
  - A weak identity cache (same handle → same C# object).
  - The class-id → factory registry (most-derived type).
  - `JsRealmUnavailableException` during prerender; `JSDisconnectedException` swallowed on dispose.
- **Acceptance:**
  - Identity and most-derived-type tests.
  - The handle census returns to baseline after scope disposal, in-memory and in-browser.
  - `InteractiveAuto` picks the right transport on each visit.

### WP-1.5 Batching · S · depends on: 1.4
- **Deliverables:**
  - `scope.BeginBatch()`: queued writes and void calls; a read flushes first; one `batch` op per flush; `JsBatchException` listing failures.
  - A round-trip counter exposed for tests.
- **Acceptance:**
  - N writes inside a batch = exactly 1 round trip (counted on Server and in-memory).
  - Program order is preserved across interleaved reads.

### WP-1.6 Golden slice (hand-written bindings) · L · depends on: 1.1, 1.4, 0.4
- **Inputs:** the fixture library; [mapping.md](mapping.md) (cite R-numbers in comments).
- **Deliverables:** `tests/GoldenSlice/` with **sync and async** hand-written bindings for:
  - `Shape` (`closed`) with `Point`/`Line`/`Polygon`;
  - `MapView` with properties, a Promise method and a `click` event;
  - `Marker` with its getter ≠ setter `shape`;
  - one discriminated union and one enum;
  - one property watch;
  - one module function;
  - their `XSnapshot` records.

  Write each binding exactly as the generator should emit it: this is the generator's target in M3.
- **Acceptance:** compiles warning-free in both modes; reviewed against mapping.md (every rule it relies on is cited).

### WP-1.7 M1 test suites · M · depends on: 1.5, 1.6, 0.3
- **Deliverables:** one behavioural suite per mode over the golden slice, run on:
  - in-memory;
  - WASM (sync and async);
  - WASM async with `Transport=JsRuntime`;
  - Server async.

  Plus the fail-fast checks for sync mode on Server.
- **Acceptance:** all green in CI. This is the M1 exit.

---

## M2–M8 · Outline (detailed after M1)

| Milestone | Candidate packages |
|---|---|
| M2 · Extractor and IR | IR schema v1 (JSON Schema + C# model) · extractor from `tools/dts-survey` (checker-resolved members, accessor pairs, overloads, event maps, discriminants, JSDoc, module specifiers) · CEM reader · golden IR for the fixture · determinism check on the private ArcGIS packages |
| M3 · Generator v1 | emitter infrastructure (indented writer, naming R25–R27, diagnostics) · value types · snapshot records + JSON · live classes in both modes · metadata tables · swap the golden slice for generated output |
| M4 · Behaviour | events/watches with options and projection · collections · callbacks (R21) · module functions · `Diff` · chatty-await analyzer |
| M5 · Components | Razor components from the CEM · custom-event registration · `@bind` · slots · prerender attributes |
| M6 · CLI and packages | `dotnet interoper generate` · IR cache · report and `--warnings-as-errors` · import-map helper · XML docs from JSDoc · NuGet packaging · quickstart |
| M7 · Hardening | latency-throttled Server tests · size/trim budget · Hybrid manual checklist |
| M8 · Genericity | second public library via profile only · WebIDL front end · TypeScript 7 API |
