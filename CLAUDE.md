# CLAUDE.md

Interoper generates C# bindings for JavaScript libraries from their TypeScript typings, plus the runtime that runs them on Blazor Server, WebAssembly and Hybrid.

Read first:
- [docs/design.md](docs/design.md): decisions and architecture.
- [docs/mapping.md](docs/mapping.md): the TypeScript to C# rules (R1–R29) and diagnostic codes.
- [docs/ir.md](docs/ir.md): the extractor's output, which the generator reads.

## Rules

- No third-party library code in this repo: no typings, generated bindings or profiles. Tests use our own fixtures (`fixtures/demo-maps`, `fixtures/demo-widgets`), and examples use made-up names like `@acme/maps`.
- .NET 11+ only (SDK pinned in `global.json`). WebAssembly uses Mono, the default; don't set `UseMonoRuntime=false`.
- Zero warnings (`TreatWarningsAsErrors`), and `dotnet format --verify-no-changes` must pass.
- Every change comes with tests. A test that covers a mapping rule cites it (`R19`).
- A change to a decision or rule updates `design.md` or `mapping.md` in the same PR.

## Commands

There is no CI. Run all of these before merging:

```bash
npm install --prefix src/interoper-extractor --no-audit --no-fund   # once: the extractor's pinned TypeScript
dotnet build Interoper.slnx
dotnet test Interoper.slnx                      # unit, golden and CLI tests
dotnet test tests/Interoper.E2E                 # browser tests (Playwright, Chromium); not part of the slnx test run
dotnet format Interoper.slnx --verify-no-changes
node --test "tests/interoper-js/*.test.mjs" "fixtures/demo-maps/test/*.test.mjs" "fixtures/demo-widgets/test/*.test.mjs" "src/interoper-extractor/test/*.test.mjs"   # use the glob form; a bare folder fails on Node 22
```

## Environment

- Cloud sessions run `.claude/hooks/session-start.sh`, which installs the pinned SDK into `~/.dotnet` and restores packages.
- `samples/Quickstart` is not in `Interoper.slnx`; it builds against the packed packages. `QuickstartTests` (in the E2E run) runs the commands in [docs/quickstart.md](docs/quickstart.md) as written, so edit them there. It needs npm and nuget.org.
- Chromium for the browser tests is preinstalled under `/opt/pw-browsers` (`PLAYWRIGHT_BROWSERS_PATH`). Don't run `playwright install`; if Playwright expects a different revision, pass `ExecutablePath`.
