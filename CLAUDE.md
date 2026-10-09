# CLAUDE.md

Interoper generates C# bindings for JavaScript libraries from their TypeScript typings, plus the runtime that drives them on Blazor Server, WebAssembly and Hybrid.

Read before changing anything:
- [docs/design.md](docs/design.md): decisions and architecture.
- [docs/mapping.md](docs/mapping.md): TypeScript → C# rules (R1–R27) and diagnostic codes.
- [docs/plan.md](docs/plan.md): the build order; the current phase has a tracking issue.

## Hard rules

- **No third-party library code in this repo.** That means no typings, no generated bindings and no profiles, and **never anything from ArcGIS**, which lives only in the private `JsCSharpInteroper-validation` repo. Tests use our own `fixtures/demo-maps`; examples use made-up names (`@acme/maps`).
- **.NET 11+ only** (SDK pinned in `global.json`). On WebAssembly we use Mono (the default); don't set `UseMonoRuntime=false`.
- **Zero warnings** (`TreatWarningsAsErrors`), and `dotnet format --verify-no-changes` must pass.
- **Every change comes with tests.** Tests that exercise a mapping rule cite it (`R19`).
- **Decision changes** update `design.md` / `mapping.md` in the same PR.

## Commands

There is no CI. Run these before merging; all of them must pass.

```bash
dotnet build Interoper.slnx
dotnet test Interoper.slnx                      # unit + golden tests
dotnet test tests/Interoper.E2E                 # browser tests (Playwright, Chromium)
dotnet format Interoper.slnx --verify-no-changes
node --test "tests/interoper-js/*.test.mjs" "fixtures/demo-maps/test/*.test.mjs"   # JS tests (glob form; a bare folder fails on Node 22)
```

Until the phase 1 scaffold lands there is no solution file.

## Environment

- Cloud sessions run `.claude/hooks/session-start.sh`, which installs the pinned SDK into `~/.dotnet` and restores packages.
- Chromium for browser tests is preinstalled under `/opt/pw-browsers` (`PLAYWRIGHT_BROWSERS_PATH`). Don't run `playwright install` there; if the Playwright package expects a different browser revision, pass an explicit `ExecutablePath`.
