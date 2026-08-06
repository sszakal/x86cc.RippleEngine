# RippleEngine

**Turn one business event into millions of per-entity tasks — without ever loading the source rows into your
app, and without a message broker.**

RippleEngine is a POC .NET library for **massive, set-based fan-out** over a Postgres store. A single event
("the corporate tax rate changed", "this legislation now applies", "re-index everything in region X") spawns
200k–10M+ per-entity tasks that a cluster of identical worker processes then executes — with retries,
fair-share scheduling, crash recovery, and back-pressure — coordinating entirely through the database.

- **Wave** = a job (one event). **Ripple** = a task (one per target entity). **Splash** = one attempt.
- You implement exactly one interface: `IRippleHandler<TWave, TRipple>`.

## The problem it solves

When one event impacts a huge set of entities, the obvious approach doesn't scale:

> load every impacted row into the app → enqueue a message per row → let workers consume them.

That round-trips millions of rows through your process and a broker just to say "recompute this one". Memory,
serialization, and broker throughput all become the bottleneck before any real work happens.

RippleEngine does the fan-out **inside Postgres**. `AddRipples(predicate, toMessage)` compiles to a single
server-side `INSERT INTO ripple.ripple … SELECT … FROM <your table> WHERE <predicate>` — one statement that
creates a task row per impacted entity **without the source rows ever leaving the database**. Workers then
pull tasks straight from Postgres with `FOR UPDATE SKIP LOCKED`. No broker, no client-side row storm.

## What you get

- **Server-side fan-out** from a Marten LINQ predicate — 400k tasks materialised by one SQL statement.
- **A cluster of symmetric workers** that take disjoint work via `SKIP LOCKED`; throughput scales with
  instance count.
- **Batch-interleaving fair-share** — each task's queue position (`schedule_order`) is precomputed at fan-out
  from a per-job-type `(batch_size, gap)`, so competing jobs interleave and a giant backlog can't starve
  small/rare jobs. Workers just `ORDER BY schedule_order` — no runtime scheduler.
- **Retries** with exponential backoff, **crash recovery** via heartbeats, and **back-pressure** so the poller
  never outruns execution.
- **Per-handler-type metrics** (OpenTelemetry) — claimed/succeeded/failed/duration + live concurrency.
- **No hot counter rows** — a wave's progress and completion are recomputed from the task rows by a periodic
  stats refresh, so the hot claim/settle paths never contend on a shared counter.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how it all works, and [AGENTS.md](AGENTS.md) for repo conventions.

## Getting started

**Prerequisites:** .NET 10 SDK (`10.0.101`) and a running Docker/Podman (Postgres runs in a container; the
tests and sample require it).

### 1. Define the payloads and a handler

```csharp
public sealed class TaxChange     { public decimal Rate { get; set; } }        // wave — the shared event
public sealed class RecalcCompany { public Guid CompanyId { get; set; } }      // ripple — one target

public sealed class RecalcHandler(IDocumentStore store)
    : IRippleHandler<TaxChange, RecalcCompany>
{
    public async Task Execute(TaxChange wave, RecalcCompany ripple, IRippleContext ctx)
    {
        await using var session = store.LightweightSession();          // own session per ripple
        var company = await session.LoadAsync<Company>(ripple.CompanyId, ctx.CancellationToken);
        if (company is null) return;
        company.TaxDue = company.Revenue * wave.Rate;                  // do the work — idempotently
        session.Store(company);
        await session.SaveChangesAsync(ctx.CancellationToken);
    }
    // throw ⇒ the attempt failed (retries, then terminal). return ⇒ succeeded.
}
```

### 2. Wire the engine (in each worker process)

```csharp
builder.Services.AddRippleStorage(connectionString);                  // the ripple schema
builder.Services.AddRippleEngine(o => o.MaxConcurrency = 32)           // per-instance execution cap
    .AddHandler<TaxChange, RecalcCompany, RecalcHandler>(batchSize: 200, gapSeconds: 1);

var host = builder.Build();
host.Services.MigrateRipple();                                        // advisory-lock-safe on every replica
host.Run();
```

### 3. Fan out (from anywhere — e.g. a web API)

```csharp
var wave = await generator                                            // IMartenWaveGenerator (AddRippleMartenGeneration)
    .Create(session, "VAT rise", new TaxChange { Rate = 0.23m })
    .AddRipples<Company, RecalcCompany>(
        c => c.TaxCode == "VAT-STD",                                   // predicate — runs server-side
        c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();                                                  // one INSERT…SELECT; no rows loaded
// wave.RippleCount == number of impacted companies; the cluster starts processing immediately.
```

## Run the sample end-to-end

The repo ships a runnable Aspire sample (Postgres + a Swagger WebAPI + **3 competing Worker replicas**) around
a company/government-taxation scenario:

```bash
dotnet run --project x86cc.Ripple.Sample.AppHost
```

Open the **Aspire dashboard**, then the **WebAPI's Swagger UI**, and drive it:

1. `POST /seed?total=1000000&batchSize=5000` — a *seed wave* generates 1M companies (Bogus + Marten
   `BulkInsert`), linking them to tax codes with **exact** sizes (`TAX-1K`, `TAX-10K`, `TAX-100K`, …). Add
   `&sizeKb=300` for very large aggregates.
2. `GET /tax-codes` — see the codes and their populated counts.
3. `POST /corporate-tax?taxCode=TAX-100K&rate=0.23` — a *taxation-change wave* fans out one ripple per
   impacted company and recomputes its tax. `POST /employee-tax?...` is a second, competing job type.
4. `GET /waves/{id}` — watch it drain to `Completed`. `GET /engine/types` — each type's configured
   `batch_size` / `gap_seconds` and its derived throughput share.

The `Sample.E2ETests` project drives all of this through the Aspire host to **measure throughput, concurrency
smoothness, and fair-share between job types** — run them manually with `dotnet test --filter …`.

## Build & test

```bash
dotnet build x86cc.RippleEngine.slnx
dotnet test  x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj   # real Postgres via Testcontainers
```

## Project layout

| Project | What |
|---|---|
| `x86cc.RippleEngine.Core` | POCOs, zero dependencies (`Wave`, `SplashOutcome`, …) |
| `x86cc.RippleEngine.Storage` | Dapper stores + migrations — the DB coordination surface |
| `x86cc.RippleEngine.Engine` | the runtime — dispatcher, TPL execution pipeline, recovery, handler registry |
| `x86cc.RippleEngine.MartenDb` | the Marten-source `INSERT…SELECT` fan-out generator |
| `x86cc.Ripple.Sample.*` | the runnable company/tax demo (Domain, WebAPI, Worker, AppHost, E2ETests) |

## Status

This is a **proof of concept**. Several deliberate single-instance shortcuts are documented as "POC
simplification" in the code and in [AGENTS.md](AGENTS.md) (notably: settled task rows accumulate — a
partitioned, chunked archive is the planned next phase). The database is the single source of truth; there is
no broker.
