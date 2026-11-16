---
title: Contributing
sidebar_position: 99
---

# Contributing

The canonical guide for working in the repository, for people and AI agents alike, is
[`AGENTS.md`](https://github.com/sszakal/x86cc.RippleEngine/blob/main/AGENTS.md). The mechanism in full depth
is in [`ARCHITECTURE.md`](https://github.com/sszakal/x86cc.RippleEngine/blob/main/ARCHITECTURE.md). This page
summarises both.

## Prerequisites

- **.NET 10 SDK.** All projects target `net10.0`.
- **Docker or Podman, running.** The tests use a real Postgres through Testcontainers.
- **Node** (optional). It builds the Angular dashboard during `dotnet build`. Without Node the SPA build is
  skipped with a warning, or you can turn it off with `-p:BuildSpa=false`.

## Build and run

```bash
dotnet build x86cc.RippleEngine.slnx                       # everything
dotnet build x86cc.RippleEngine.slnx -p:BuildSpa=false     # without the Angular dashboard
dotnet run --project x86cc.Ripple.Sample.AppHost           # Postgres + WebAPI + 3 workers via Aspire
```

:::warning Rebuild the whole solution after engine changes
`dotnet run --no-build --project <sample>` uses that project's **own** `bin/` copy of the engine DLLs. After
editing `Engine` or `Storage`, rebuild the solution (or the sample project), not just the edited project, or
the sample silently runs the old engine.
:::

Package versions are centrally managed in `Directory.Packages.props`, so `PackageReference`s must **not** carry
a `Version=` (that's `NU1008`).

## Tests

```bash
dotnet test x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj
dotnet test x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj \
  --filter "FullyQualifiedName~concurrent_claims_get_disjoint_slices"
```

- **xUnit + Shouldly**, against **real Postgres** (`postgres:17`). The fan-out and claim are raw SQL, so there
  is no in-memory fallback.
- **One container, one database per test.** `SharedPostgres` starts a single container for the run and
  migrates a template database once. Every `RippleTestBase` test gets its own database via
  `CREATE DATABASE … TEMPLATE`, which keeps tests isolated without paying for a container or migration each.
- **Serial.** The suite disables test parallelization because the engine tests are timing-sensitive.
- **No database for pure logic.** Reports, keys, guards, options and the retry policy are tested directly.
  Timing logic runs on a `FakeTimeProvider` (`ClockDrivenTests`). Never put a whole engine host on a fake
  clock: its loop delays would stall, and the database's `now()` doesn't follow it.
- **Wave numbers are not synchronous.** Storage-level tests must trigger a stats refresh, or run the engine,
  before asserting a wave's `pending`/`running`/`failed`.
- `x86cc.Ripple.Sample.E2ETests` runs the whole sample through Aspire.

## Conventions

- **No hot counters.** If you add a per-wave aggregate, recompute it in `refresh_wave_stats()`. Don't add a
  synchronously-maintained counter.
- **Generated SQL.** Framework-controlled values (guids, type names, `'Pending'`) are inlined; user-driven
  values stay bound parameters. Storage SQL is heavily commented because the hot paths are subtle. Keep it
  that way.
- **Type names come only from `RippleTypeName.Of<T>()`.** Never stamp `typeof(T).Name`. Fan-out and the
  worker registry must resolve identically.
- **Expansion inserts** must go through `ExpansionSql.TryBeginAsync` / `CompleteAsync`, and any insert into
  an existing wave must first take `ScheduleOrderSql.LockWaveTailsAsync`.
- **Time.** Engine and Storage code uses the injected `TimeProvider`: `time.GetUtcNow()`,
  `Task.Delay(…, time, ct)`, `new CancellationTokenSource(delay, time)`. Never use `DateTimeOffset.UtcNow`.
- **POC simplifications** are intentional and documented in `<remarks>`. Keep those remarks when you touch
  the code.

## Documentation

This site lives in `docs/` (Docusaurus 3, deployed to GitHub Pages on pushes to `main` that touch `docs/**`).
Pages are Markdown under `docs/content/`, and the sidebar in `docs/sidebars.ts` is curated by hand, so a new
page must be added there.

```bash
cd docs && npm ci && npm start    # live preview
npm run build                     # the check: fails on broken internal links
```

When you change behaviour the docs describe (options and defaults, API shapes, the mechanism), update the
matching page in the same change.
