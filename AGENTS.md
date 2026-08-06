# AGENTS.md

Guidance for AI coding agents working in this repository. This is the canonical instructions file;
`CLAUDE.md` imports it. For the mechanism in depth see [ARCHITECTURE.md](ARCHITECTURE.md); for the pitch and
a quick start see [README.md](README.md).

## What this is

**RippleEngine** is a POC library for **massive, set-based fan-out** over a Postgres store: one business
event (e.g. "corporate tax rate changed") spawns 200k–10M+ per-entity tasks **without ever loading the source
rows into the client**, then executes them across a cluster of symmetric worker instances. It is a
**self-contained, in-process distributed job engine** — .NET hosted services + TPL Dataflow + raw Postgres
(Dapper). The database is the only source of truth; instances coordinate purely through it
(`FOR UPDATE SKIP LOCKED`, a precomputed `schedule_order` ordering key, a periodically-recomputed stats table,
heartbeats). There is **no message broker**.

Target framework is **net10.0**. Several files are commented as "POC simplification" — deliberate
single-instance shortcuts, called out below and in `<remarks>` at each site.

### Vocabulary (water theme)

| Term | Means | Type |
|---|---|---|
| **Wave** | a job — one triggering event, carries a shared payload + completion state | `Core/Wave.cs` |
| **Ripple** | a task — one per target entity, carries a per-target payload | rows in `ripple.ripple` (no CLR type) |
| **Splash** | one execution attempt — the audit record | rows in `ripple.splash` (`SplashOutcome`) |

A developer implements exactly one interface:
`IRippleHandler<TWave, TRipple>.Execute(wave, ripple, context)` (where `TRipple : IRippleTarget`), returning a
`SplashReport` built by exception — `report.Success(id)` / `report.Failed(id, msg)` only for targets that
deviate from success (unmentioned ⇒ succeeded), throw to fail all targets, and any Failed target fails (and
retries) the whole ripple. The report aggregates targets sharing an `(outcome, message)` into one item.

## Build / test / run

```bash
dotnet build x86cc.RippleEngine.slnx                                    # build everything
dotnet test  x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj   # run the engine tests

# run a single test
dotnet test x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj \
  --filter "FullyQualifiedName~concurrent_claims_get_disjoint_slices"

# run the sample end-to-end (Postgres + WebAPI + 3 Worker replicas via Aspire)
dotnet run --project x86cc.Ripple.Sample.AppHost
```

Tests use **xUnit + Shouldly** and spin up a **real Postgres via Testcontainers** (`postgres:17`) — the
fan-out and claim are raw SQL, so **Docker/Podman must be running** and there is no in-memory fallback. The
Testcontainers suite is run **serially** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`)
because concurrent containers overload the local runtime.

**Gotcha:** `dotnet run --no-build --project <sample>` uses that project's OWN `bin/` copy of the engine
DLLs. After editing `Engine`/`Storage`, rebuild the whole solution (or the sample project), not just the
edited project, or standalone runs silently execute the old engine.

### Package versions (Central Package Management)

All versions live in `Directory.Packages.props` (CPM is on, so `PackageReference`s must NOT carry a
`Version=` — that's an `NU1008`). Aspire is pinned to `13.4.6`; the AppHost needs an explicit
`<PackageReference Include="Aspire.Hosting.AppHost" />` (its SDK adds an implicit one CPM rejects with
`NU1009`), and `Microsoft.Extensions.*` are `>= 10.0.8` to satisfy Aspire.

## Solution layout

Layered so the core has zero dependencies; the two fan-out providers (Marten, EF Core) are interchangeable
sibling packages over the same Core abstractions:

- **`x86cc.RippleEngine.Core`** — the dependency-free model **and developer contract**. Model POCOs: `Wave`
  (+`WaveStatus`), `InstanceHeartbeat`, `SplashOutcome`. Contract: **`IRippleHandler<TWave,TRipple>`** +
  **`IRippleContext`** (the handler interface + its handle) and the fan-out abstractions — the builders
  **`IWaveBuilder`** (queryable-source `INSERT … SELECT`) and **`ICollectionWaveBuilder`** (in-memory source),
  plus the source-less generator interface **`ICollectionWaveGenerator`** (its impl lives in Storage). Every
  generator (collection / Marten / EF) exposes the same two verbs: **`Create`** = new wave, **`Continue(context)`**
  = expand the wave the current ripple belongs to (in-flight expansion), so the only thing that varies is the
  *source*. Handler and wave-creation code depends only on this package, never the runtime or a specific
  provider. (The concrete `RippleContext` lives in Engine; the builder/generator impls in Storage and the
  provider packages.) A wave's live numbers are **recomputed from the ripple rows** by a periodic stats
  refresh, not maintained on the hot paths; completion is "no pending and no running" (a wave's ripple count
  keeps growing as handlers expand it, so it can't be inferred from the original count).
- **`x86cc.RippleEngine.Storage`** — the coordination surface (Dapper + FluentMigrator). `IEngineStore`
  (`EngineStore.cs`: create wave / add ripples / the one-round-trip `PollAsync` claim / heartbeat / recovery /
  type-schedule / stats refresh) and `ISplashStore` (`SplashStore.cs`: settlement). Also the
  `Core.ICollectionWaveGenerator` impl (`CollectionWaveGenerator`, a thin adapter over `IEngineStore` — its
  builder does `Create`/`Continue` in-memory), `RippleSeedSerializer` (the one place in-memory items become
  `RippleSeed`s), and — shared by all queryable-source providers via `InternalsVisibleTo` — `WaveInsertSql`
  (the fan-out `INSERT … SELECT` + `type_key` stamping) and `WaveBuilderBase` (the provider-neutral dispatch +
  wave create/continue; a provider subclass only builds each spec's inner `data` SQL). **`ScheduleOrderSql`**
  holds the subtle `schedule_order` stamping (base-clamp + per-type batch/gap) in **one place**, shared by both
  the unnest insert (`EngineStore.AddRipplesAsync`) and the `INSERT … SELECT` (`WaveInsertSql`).
  `RippleTypeKey`, `RippleOptions`, `RippleDataSource`, `Migrations/M0001_Schema` (the whole schema — one
  migration while the DB is ephemeral).
- **`x86cc.RippleEngine.Engine`** — the runtime. `Dispatcher` (the poller `BackgroundService`),
  `ExecutionPipeline` (TPL `ActionBlock`), `RecoveryLoop`, `WaveStatsRefreshLoop`, `RippleHandlerRegistry`,
  `RippleContext` (the internal `IRippleContext` impl), `RippleEngineOptions`, `RippleMetrics`, `ScheduleSeeder`,
  `RippleEngineExtensions` (`AddRippleEngine().AddHandler<TWave,TRipple,THandler>()`). Depends on Core + Storage.
- **`x86cc.RippleEngine.MartenDb`** — the Marten-source fan-out provider: `IMartenWaveGenerator`
  (`Create(IQuerySession, …)` / `Continue(IQuerySession, IRippleContext)`) whose `WaveBuilder : WaveBuilderBase`
  only produces each spec's inner `data` SQL from a Marten LINQ query (`ToCommand()` → `jsonb_build_object`).
  Depends on Core + Storage + Marten.
- **`x86cc.RippleEngine.EntityFrameworkCore`** — the EF-Core-source fan-out provider, a sibling of MartenDb:
  `IEfWaveGenerator` (`Create(DbContext, …)` / `Continue(DbContext, IRippleContext)`) whose
  `EfWaveBuilder : WaveBuilderBase` extracts the query's
  parameterised SQL via `IRelationalQueryingEnumerable.CreateDbCommand()` (EF `Query.Internal`, hence the
  `EF1001` suppression — the technique is isolated to one method) and wraps each projected row as a `data`
  payload with `to_jsonb(...)`. Same `WaveInsertSql` stamping as Marten. Pinned to EF Core **9.x** so Npgsql
  stays on the 9.0.x line Marten needs (EF 10 would pull Npgsql 10). Depends on Core + Storage + Npgsql EF.

The **`Sample.*`** projects (`Domain`, `WebAPI`, `Worker`, `AppHost`, `E2ETests`) are a runnable
company/government-taxation demo — see [README.md](README.md). `x86cc.RippleEngine.Dashboard` is a
standalone Angular SPA, not wired into the sample.

## Architecture in one screen

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full treatment. The essentials:

1. **Fan-out** (`WaveBuilder`): `session.Query<TSource>().Where(p).Select(toMsg).ToCommand()` gives Marten's
   `select jsonb_build_object(...)`; it's wrapped as the source of `INSERT INTO ripple.ripple … SELECT …`,
   generating ids with `gen_random_uuid()`, stamping the `type_key`, and computing each ripple's `schedule_order`
   — all server-side. Enums must be stored as strings (`EnumStorage.AsString`) because the INSERT writes enum
   literals into JSONB.
2. **Schedule** (fan-out): `schedule_order` is a **precomputed ordering key** (NOT a deadline) — a `double
   precision` number (epoch-seconds based, treated as opaque), stamped once:
   `base + floor(k/batch_size) * gap_seconds`. The `base` is `coalesce(max(schedule_order of the wave's pending),
   greatest(extract(epoch from now()), min(schedule_order over ALL pending)))` — i.e. continuation appends after the job's own tail,
   while a new/drained job starts at the current **global frontier** (the claim index's left edge), clamped up
   to `now()`. It is deliberately NOT bare `now()`: virtual time runs ahead of the wall clock (the engine
   drains far faster than 1 slot/gap), so a late job based at `now()` would sit behind the frontier and
   monopolise the cluster to "catch up" (classic WFQ). A later job's batches then interleave with an in-flight
   job's remaining batches from the frontier. Nothing is ever rescheduled; `base`/`now()` are DB-clock so a
   lagging host can't jump the global queue.
3. **Claim** (`PollAsync`): one statement that (a) upserts the heartbeat, (b) claims the globally lowest
   `schedule_order` pending, retry-eligible ripples across ALL waves with `FOR UPDATE SKIP LOCKED`, bumping
   attempt. No per-type selection, no quota, no counter writes — the fair-share is already baked into
   `schedule_order`. Polling IS the liveness proof. Global concurrency is just `MaxConcurrency × instances`.
4. **Configure**: the composite **`type_key = "{waveType}|{rippleType}"`** is both the config key and the
   handler key. `type_schedule(batch_size, gap_seconds, max_attempts)` is seeded at
   `AddHandler(batchSize, gapSeconds, maxAttempts)` registration; a job's steady-state throughput share ≈
   `batch_size / gap_seconds`, and `max_attempts` (nullable → engine default) is the retry ceiling the claim
   and recovery read per type. Batch/gap are read at fan-out (baked into `schedule_order`); `max_attempts` is
   read at claim time, not stamped on the ripple.
5. **Execute** (`ExecutionPipeline`): claimed ripples flow into a bounded `ActionBlock` (MDOP =
   `MaxConcurrency`, with a `PrefetchFactor` input buffer). The handler runs in a per-ripple DI scope; throw =
   failed (retry with backoff, then terminal), return = succeeded. Outcomes batch-write via channels.
6. **Settle** (`SplashStore`): records a per-attempt `splash` and flips the ripple's terminal state — fenced on
   `claimed_by` + `state='Running'` + `attempt` (the attempt matters: a stalled instance is reclaimed, comes
   back under the same `InstanceId`, and can re-claim the same ripple, so owner+state alone would let its stale
   outcome settle the *new* attempt). The splash's `duration_ms` comes from an `EndedAt` the pipeline stamps
   when the handler finished — settlement is async, so measuring it at write time would fold batching and retry
   backoff into it. No counters move; a requeued ripple keeps its `schedule_order`.
7. **Recover** (`RecoveryLoop` → `RecoverStaleAsync`): a stale-heartbeat instance's in-flight ripples are
   reclaimed (requeued or poison-failed) with an `Abandoned` splash. Idempotent, fenced on `state='Running'`.
8. **Stats + completion** (`WaveStatsRefreshLoop` → `refresh_wave_stats()`): the ONLY writer of a wave's live
   numbers and the ONLY place a wave completes. Every `WaveStatsRefreshInterval` (advisory-lock-gated) it recomputes
   each active wave's pending/running/failed (onto the `wave` row) from the actual ripple states and flips a
   drained wave (`pending=0`, `running=0`) to Completed/Faulted — one `UPDATE`.

## Completion is recomputed, not counted (no hot counter rows)

There are **no synchronously-maintained counters** on the hot paths. The claim, settlement, and recovery change
ONLY `ripple.state`. A wave's `pending`/`running`/`failed` numbers are a cache recomputed
from the truth by `refresh_wave_stats()` (written onto the `wave` row) — reading only the small partial-index sets (`(wave_id) where
state='Pending'`, the tiny `Running` set, `(wave_id) where state='Failed'`), via **correlated** per-wave counts
(which bound `failed`/`retries` to active waves; `pending` is inherently active-only, so its cost stands) — so the numbers **self-heal**
(no drift under a false recovery) and completion is always decided from actual row states. `succeeded` is never
stored; it's derived at read time as `ripple_count - pending - running - failed`, so no read scans the settled
millions. **Consequence for tests:** wave numbers are no longer synchronous — storage-level tests must call
`RefreshWaveStatsAsync()` (or run the engine, which drives `WaveStatsRefreshLoop`) before asserting them. **If you add
a per-wave aggregate, recompute it in `refresh_wave_stats()` — do not add a synchronously-maintained counter.**

## POC simplifications (intentional — preserve the `<remarks>` if you touch them)

- `DispatchLoop` full-pipeline recheck is a 2ms busy-wait; a saturated instance still beats on cadence.
- Recovery is two-pronged: `RecoverStaleAsync` reclaims **dead** instances (heartbeat stale past
  `HeartbeatTimeout`), and `RecoverSelfStrandedAsync` (run by the same `RecoveryLoop`) reclaims an instance's
  **own** claims that the execute block isn't actually running — the DB says `Running & claimed_by=me` but the
  id isn't in `ExecutionPipeline.InFlightIds`, past a `SelfReconcileGrace` window (covers the claim→enqueue gap).
  That closes the gap where a *live* instance strands its own work (a claim that never reached a handler, or one
  lost to a fault/race) — dead-owner recovery alone can't rescue those. And if the execute block **faults** (its
  action throws and escapes), the `Dispatcher` detects `pipeline.IsFaulted`, releases all its claims, and calls
  `StopApplication()` to fail fast (restart with a fresh block) rather than silently abandoning rows as
  `Running`. A pure time-based reaper (age-only, owner-agnostic) is still a later, table-split phase.
- The scheduler is **static**: once `schedule_order` is stamped it is never rebalanced, so thousands of jobs
  arriving at once can each put one batch ahead of a newcomer. Far better than strict FIFO (wait behind one
  batch per job, not every task) but not a dynamic fair queue.
- All workers claim off the same left edge of `ix_ripple_schedule_order` (lowest `schedule_order`); `SKIP LOCKED`
  keeps them disjoint. `schedule_order` inserts land mid-index (interleaved by design), so that index bloats more
  than a monotonic `created_at` one would — fine at POC scale.
- Settled `ripple`/`splash` rows do NOT accumulate: when a wave goes terminal, `CompactionLoop` →
  `compact_wave()` rolls its per-attempt splash reports into aggregated `report_chunk` rows and **deletes the
  wave's ripples + splashes** (per-wave `DELETE`); the wave's `report_chunk`s + wave row are then kept for a
  per-wave-type retention (`RippleOptions.RetentionByWaveType`/`DefaultRetention`, stamped as `expire_at` at
  compaction) and deleted by `purge_expired_waves()`. Deliberately NOT partitioned — per-wave delete gives
  exact per-type retention and only ever touches completed waves.

## Conventions

- Match the surrounding code's comment density and idiom. Storage SQL is heavily annotated because the hot
  paths are subtle — keep it that way.
- Framework-controlled values in generated SQL (guids, type names, `'Pending'`) are inlined; user-driven
  values stay bound parameters.
- Handlers must be **idempotent** (a ripple can run more than once via retry or recovery).
- Marten handlers open their OWN session from `IDocumentStore` (`store.LightweightSession()`), not an
  injected scoped `IDocumentSession` — the latter doesn't isolate cleanly across the engine's per-ripple
  scopes under concurrency.
- The engine mixes host-clock (`DateTimeOffset.UtcNow` for `next_attempt_at`) with DB-clock (`now()` for
  eligibility); fine when NTP-synced, but tight time tolerances make tests fragile on a drifting container VM.
