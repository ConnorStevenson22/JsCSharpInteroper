# CLAUDE.md

Interoper generates C# bindings for JavaScript libraries from their TypeScript typings, plus the runtime that runs them on Blazor Server, WebAssembly and Hybrid.

Read first:
- [docs/design.md](docs/design.md): decisions and architecture.
- [docs/mapping.md](docs/mapping.md): the TypeScript to C# rules (R1–R29) and diagnostic codes.
- [docs/ir.md](docs/ir.md): the extractor's output, which the generator reads.

## Rules

- No third-party library code in this repo: no typings, generated bindings or profiles. Tests use our own fixtures (`fixtures/demo-maps`, `fixtures/demo-widgets`), and examples use made-up names like `@acme/maps`.
- .NET 11+ only (SDK pinned in `global.json`). WebAssembly uses Mono, the default. Don't set `UseMonoRuntime=false`.
- Zero warnings (`TreatWarningsAsErrors`), and `dotnet format --verify-no-changes` must pass.
- Every change comes with tests. A test that covers a mapping rule cites it (`R19`).
- A change to a decision or rule updates `design.md` or `mapping.md` in the same change.

## Commands

There is no CI. Run all of these before merging:

```bash
npm install --prefix src/interoper-extractor --no-audit --no-fund   # once: the extractor's pinned TypeScript
dotnet build Interoper.slnx
dotnet test Interoper.slnx                      # unit, golden and CLI tests
dotnet test tests/Interoper.E2E                 # browser tests (Playwright, Chromium); not part of the slnx test run
dotnet format Interoper.slnx --verify-no-changes
node --test "tests/interoper-js/*.test.mjs" "fixtures/demo-maps/test/*.test.mjs" "fixtures/demo-widgets/test/*.test.mjs" "src/interoper-extractor/test/*.test.mjs"   # a bare folder instead of a glob fails on Node 22
```

## Environment

- `.claude/hooks/session-start.sh` installs the pinned SDK into `~/.dotnet` and restores packages.
- `samples/Quickstart` is not in `Interoper.slnx`. It builds against the packed packages. `QuickstartTests` (in the E2E run) runs the commands in [docs/quickstart.md](docs/quickstart.md) as written, so edit them there. It needs npm and nuget.org.
- The browser tests use the Chromium in `PLAYWRIGHT_BROWSERS_PATH` (`/opt/pw-browsers` in the cloud environment). Don't run `playwright install`. If Playwright expects a different revision, pass `ExecutablePath`.
