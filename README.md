# RippleEngine

**Turn one business event into millions of per-entity tasks — without ever loading the source rows into your
app, and without a message broker.**

RippleEngine is a .NET library for **massive, set-based fan-out** over a Postgres store. A single event
("the corporate tax rate changed", "this legislation now applies", "re-index everything in region X") spawns
200k–10M+ per-entity tasks that a cluster of identical worker processes then executes — with retries,
fair-share scheduling, crash recovery, back-pressure, and a live dashboard — coordinating **entirely through
the database**. No broker, no scheduler service, no leader election.

You implement exactly one interface (`IRippleHandler<TWave, TRipple>`). The engine owns everything else.

## The problem it solves

When one event impacts a huge set of entities, the obvious approach doesn't scale:

> load every impacted row into the app → enqueue a message per row → let workers consume them.

That round-trips millions of rows through your process and a broker just to say "recompute this one". Memory,
serialization, and broker throughput all become the bottleneck before any real work happens. And once the
backlog exists, one giant job starves every other job behind it.

RippleEngine does the fan-out **inside Postgres**. `AddRipples(predicate, toMessage)` compiles to a single
server-side `INSERT INTO ripple.ripple … SELECT … FROM <your table> WHERE <predicate>` — one statement that
creates a task row per impacted entity **without the source rows ever leaving the database**. Workers then
pull tasks straight from Postgres with `FOR UPDATE SKIP LOCKED`, in an order that already encodes each job's
fair share.

## Vocabulary (water theme)

| Term | Is | Lives in |
|---|---|---|
| **Wave** | a job — one triggering event, carrying a shared payload | one `ripple.wave` row |
| **Ripple** | a task — one per target entity, carrying its own payload | rows in `ripple.ripple` |
| **Splash** | one execution attempt — the audit record | rows in `ripple.splash` |

```
   Event ──▶  Wave (1 row + shared payload)
                │  server-side INSERT … SELECT  (source rows never loaded);
                │  each ripple stamped with a schedule_order ordering key
                ▼
             Ripples (N rows, one per target entity)
                │  global claim: ORDER BY schedule_order, FOR UPDATE SKIP LOCKED
                ▼
   Worker A ─┐  Worker B ─┐  Worker C ─┐   (identical processes, disjoint claims)
             ▼            ▼            ▼
          TPL ActionBlock executes IRippleHandler<TWave, TRipple>
                │  outcome
                ▼
             Splash (1 row per attempt) — the ripple only flips state
                │
   stats refresh recomputes the wave's numbers ──▶ Completed / Faulted ──▶ compacted + retained
```

## What you get

- **Server-side fan-out** — 400k tasks materialised by one SQL statement, from a **Marten** LINQ query, an
  **EF Core** query, or an in-memory collection. Same builder API for all three.
- **Batch-interleaving fair-share** — each task's queue position (`schedule_order`) is precomputed at fan-out
  from a per-type `(batch_size, gap_seconds)`, so competing jobs **interleave** and a 10M-row backlog can't
  starve a small, urgent job. Workers just `ORDER BY schedule_order` — there is no runtime scheduler to run,
  contend on, or elect a leader for.
- **A cluster of symmetric workers** — every instance does the same thing (claim, execute, settle, recover,
  refresh); `SKIP LOCKED` keeps their claims disjoint, so throughput scales with instance count.
- **In-flight expansion** — a handler can grow the wave it belongs to (`Continue(context)`), spawning child
  ripples from a query or a list; the wave won't complete until the children settle.
- **Retries with backoff**, **crash recovery** via heartbeats (including an instance recovering work it
  stranded from *itself*), and **back-pressure** so the poller never outruns execution.
- **Batch-aware outcomes** — a ripple may target many entities; `SplashReport` records per-target results,
  aggregating identical outcomes so 200k successes collapse to one row.
- **No hot counter rows** — a wave's progress and completion are recomputed from the task rows by a periodic
  refresh, so the hot claim/settle paths never contend, and the numbers self-heal after any anomaly.
- **The hot tables stay small** — a finished wave's splashes are rolled into aggregated report chunks and its
  ripples/splashes deleted (compaction), then the wave itself is purged after a per-wave-type retention.
- **Pause / resume a job type** at runtime — O(1) to request, drained asynchronously in bounded chunks so
  pausing 10M ripples never takes a long lock.
- **Operability** — an Angular dashboard (waves, timelines, per-type metrics, live cluster, CSV report export,
  editable schedule config) plus OpenTelemetry metrics tagged by type.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how each of these works, and [AGENTS.md](AGENTS.md) for repo
conventions.

## Getting started

**Prerequisites:** .NET 10 SDK and a running Docker/Podman (Postgres runs in a container; the tests and the
sample require it). Node 20+ only if you want the dashboard SPA rebuilt.

### 1. Define the payloads and a handler

```csharp
// The wave payload — the shared event, serialized once onto the wave row.
public sealed class TaxChange { public decimal Rate { get; set; } }

// The ripple payload — one target. Ripple payloads declare their target ids so the engine can
// attribute per-target outcomes (and synthesise an all-failed report if the handler throws).
public sealed class RecalcCompany : IRippleTarget
{
    public Guid CompanyId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}

public sealed class RecalcHandler(IDocumentStore store) : IRippleHandler<TaxChange, RecalcCompany>
{
    public async Task<SplashReport?> Execute(TaxChange wave, RecalcCompany ripple, IRippleContext ctx)
    {
        await using var session = store.LightweightSession();          // own session per ripple
        var company = await session.LoadAsync<Company>(ripple.CompanyId, ctx.CancellationToken);
        if (company is null) return null;                              // null ⇒ every target succeeded

        company.TaxDue = company.Revenue * wave.Rate;                  // do the work — idempotently
        session.Store(company);
        await session.SaveChangesAsync(ctx.CancellationToken);
        return null;
    }
}
```

**Reporting is by exception.** Return `null` (or an empty report) and every target counts as succeeded. Build
a `SplashReport` only to annotate the ones that deviate:

```csharp
var report = SplashReport.Create();
report.Failed(badId, "no ledger for period");     // any Failed target ⇒ the attempt fails and retries
report.Success(otherId, "recomputed from draft"); // succeeded, with a note kept in the audit trail
return report;
```

Throwing is equivalent to failing every target with the exception message — so a failed attempt always
explains itself. A failed attempt retries with exponential backoff up to the type's `max_attempts`, then goes
terminally `Failed`. **Handlers must be idempotent**: retry and crash recovery can both re-run a ripple.

### 2. Wire the engine (in each worker process)

```csharp
builder.Services.AddRippleStorage(connectionString);                   // the ripple schema + stores
builder.Services.AddRippleEngine(o => o.MaxConcurrency = 32)           // per-instance execution cap
    .AddHandler<TaxChange, RecalcCompany, RecalcHandler>(batchSize: 200, gapSeconds: 1);

var host = builder.Build();
host.Services.MigrateRipple();                                         // advisory-lock-safe on every replica
host.Run();
```

`AddRippleEngine` starts the hosted services that make this process a full peer: the dispatcher (heartbeat +
claim), the execution pipeline, recovery, stats refresh, compaction, and pause reconciliation. Adding capacity
is just starting another identical process.

`(batchSize, gapSeconds)` is the fairness knob — a job's steady-state share of the cluster is roughly
`batchSize / gapSeconds` relative to its competitors. Keep `batchSize` well under the cluster's total
execution capacity for a *blended* mix (both jobs running concurrently) rather than coarse alternation.

### 3. Fan out — from a query, or from a list

**Marten source** (`AddRippleMartenGeneration()`):

```csharp
var wave = await martenGenerator
    .Create(session, "VAT rise", new TaxChange { Rate = 0.23m })
    .AddRipples<Company, RecalcCompany>(
        c => c.TaxCode == "VAT-STD",                                   // predicate — runs server-side
        c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();                                                   // one INSERT…SELECT; no rows loaded
// wave.RippleCount == the number of impacted companies; the cluster starts on it immediately.
```

**EF Core source** (`AddRippleEfGeneration()`) — identical, with a `DbContext` in place of the session:

```csharp
var wave = await efGenerator
    .Create(dbContext, "VAT rise", new TaxChange { Rate = 0.23m })
    .AddRipples<Company, RecalcCompany>(c => c.TaxCode == "VAT-STD", c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();
```

**In-memory source** (`ICollectionWaveGenerator`, no queryable source) — for work items you already hold:

```csharp
var wave = await collectionGenerator
    .Create("Nightly reconcile", new ReconcileRun { Date = today })
    .AddRipples(regions.Select(r => new ReconcileRegion { Region = r }))
    .DispatchAsync();

await collectionGenerator.FireAsync("Ad-hoc", waveP, ripplePayload);    // one-wave/one-ripple shorthand
```

Two more fan-out shapes on the queryable builders: **`AddRipplesBatched`** collapses N impacted rows into one
ripple carrying an array of ids (10M rows → 10M/N ripples, bucketing done server-side), and
**`AddRipplesRaw<TBatch>(sql)`** is the escape hatch for grouping LINQ can't translate.

### 4. Grow a wave from inside a handler

Every generator exposes the same two verbs — `Create` (new wave) and `Continue` (expand the wave the current
ripple belongs to), so a coarse ripple can discover its own children without loading them:

```csharp
public async Task<SplashReport?> Execute(Migration wave, MigrateGroup ripple, IRippleContext ctx)
{
    await using var session = store.LightweightSession();
    await martenGenerator                                              // or efGenerator / collectionGenerator
        .Continue(session, ctx)                                        // same wave, parented to this ripple
        .AddRipples<Member, MigrateMember>(m => m.GroupId == ripple.GroupId,
                                           m => new MigrateMember { Id = m.Id })
        .DispatchAsync(ctx.CancellationToken);
    return null;
}
```

The children are appended after the wave's own tail in the schedule, the wave's `ripple_count` grows, and the
wave cannot complete until they settle.

## Operating it

- **Dashboard** — the Angular SPA (`x86cc.RippleEngine.Dashboard`) is served by every worker at its root URL,
  same origin as its read API (`/api`): waves by year/month/day/range with timelines, per-type throughput
  metrics, live cluster membership, a wave's aggregated report as CSV, and a settings page that edits each
  type's `batch_size` / `gap_seconds` / `max_attempts` (including the reserved `__default__` row) and
  pauses/resumes types. It builds into the worker's `wwwroot` as part of `dotnet build` when Node is on PATH
  (skips with a warning otherwise; disable with `-p:BuildSpa=false`).
- **Pause / resume** — `PauseTypeAsync(typeKey)` flips a desired state and takes effect immediately (the claim
  skips the type at once); the millions of ripples move `Pending ⇄ Paused` in bounded background chunks.
  Resume can *rebase* the resumed work to the current frontier or keep its original position.
- **Retention** — configure per wave type; a finished wave's report chunks are kept for that long after
  completion, then purged. `null` means keep forever.
  ```csharp
  builder.Services.AddRippleStorage(cs, o => o.RetentionByWaveType["CorporateTaxChange"] = TimeSpan.FromDays(90));
  ```
- **Metrics** — `AddMeter("x86cc.RippleEngine")` in OpenTelemetry gets `ripple.claimed` / `succeeded` /
  `failed` / `duration` (all tagged with `type_key`) plus a per-instance `ripple.executing` gauge.

## Run the sample end-to-end

The repo ships a runnable Aspire sample (Postgres + a Swagger WebAPI + **3 competing Worker replicas**) around
a company/government-taxation scenario:

```bash
dotnet run --project x86cc.Ripple.Sample.AppHost
```

Open the **Aspire dashboard**, then the **WebAPI's Swagger UI** (`:5100`), and drive it:

1. `POST /seed?total=1000000&batchSize=5000` — a *seed wave* generates 1M companies (Bogus + Marten
   `BulkInsert`), linking them to tax codes with **exact** sizes (`TAX-1K`, `TAX-10K`, `TAX-100K`, …). Add
   `&sizeKb=300` for very large aggregates. This one uses the in-memory generator: its ripples are index
   ranges, not existing rows.
2. `GET /tax-codes` — see the codes and their populated counts.
3. `POST /corporate-tax?taxCode=TAX-100K&rate=0.23` — a *taxation-change wave* fans out one ripple per
   impacted company and recomputes its tax. `POST /employee-tax?…` is a second, competing job type,
   registered at 3× the batch size, so you can watch the two interleave at a ~3:1 share.
4. `GET /waves/{id}` — watch it drain to `Completed`; `GET /engine/instances` — the true live cluster
   concurrency. Or open a **worker's** URL (`:5200`) for the dashboard.

The `Sample.E2ETests` project drives all of this through the Aspire host to **measure throughput, concurrency
smoothness, and fair-share between job types** — run them manually with `dotnet test --filter …`.

## Build & test

```bash
dotnet build x86cc.RippleEngine.slnx
dotnet test  x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj   # real Postgres via Testcontainers
```

The engine tests spin up a real `postgres:17` container (the fan-out and claim are raw SQL — there is no
in-memory fallback), so Docker/Podman must be running. They cover the scheduler's fairness maths, the claim's
disjointness under concurrency, recovery, compaction, pause/resume, expansion, and both query providers.

## Project layout

| Project | What |
|---|---|
| `x86cc.RippleEngine.Core` | the developer contract + model, zero dependencies (`IRippleHandler`, `IRippleTarget`, `SplashReport`, the builder/generator interfaces, `Wave`) |
| `x86cc.RippleEngine.Storage` | the DB coordination surface — Dapper stores (claim, settle, recover, stats, compaction, pause), the schema migration, the shared fan-out SQL |
| `x86cc.RippleEngine.Engine` | the runtime — dispatcher, TPL execution pipeline, recovery, stats refresh, compaction, pause reconciliation, handler registry, metrics |
| `x86cc.RippleEngine.MartenDb` | the Marten-source `INSERT…SELECT` fan-out provider |
| `x86cc.RippleEngine.EntityFrameworkCore` | the EF-Core-source fan-out provider (sibling of the above, same builder) |
| `x86cc.RippleEngine.Dashboard` | the Angular + Tailwind monitoring SPA (built into the worker's `wwwroot`) |
| `x86cc.RippleEngine.Tests` | the engine test suite (Testcontainers Postgres) |
| `x86cc.Ripple.Sample.*` | the runnable company/tax demo (Domain, WebAPI, Worker, AppHost, E2ETests) |

## Status

The mechanism is complete and covered by 84 engine tests plus a whole-system throughput suite that seeds 10M
entities through the engine itself. It is **pre-1.0**: consumed by project reference (the engine projects
carry no package metadata yet) and not yet run in production.

Known limits, each a deliberate trade rather than an omission — the full treatment is in
[ARCHITECTURE.md](ARCHITECTURE.md#known-limits--roadmap):

- The scheduler is **static**: once `schedule_order` is stamped it is never rebalanced, so thousands of jobs
  arriving at once can each sit one batch ahead of a newcomer (still far better than strict FIFO).
- Wave numbers are **eventually consistent** (up to one refresh interval stale) — the trade for zero counter
  maintenance on the hot paths.
- The stats refresh recomputes a wave's pending set on every tick, so one very large wave keeps re-counting
  its whole backlog; making that cheap at 10M would need an incremental design.
- There is **no per-type concurrency ceiling**; global concurrency is `MaxConcurrency × instances`. A hard cap
  for a rate-limited dependency would need to be added.
- The hot tables stay small via **per-wave compaction**, not partitioning; a very large wave's compaction
  `DELETE` is the one heavy statement (`created_at` RANGE partitioning is the escape hatch if it bites).
- The schema is a single authoritative migration on an ephemeral database, not an ALTER trail — freeze it and
  add forward migrations before running against a database you intend to keep.

The database is the single source of truth; there is no broker, no scheduler service, and no leader.
