---
title: Quick start
sidebar_position: 1
---

# Quick start

**Prerequisites:** .NET 10 SDK and a running Docker/Podman (Postgres runs in a container; the tests and the
sample require it). Node 20+ only if you want the dashboard SPA rebuilt.

Three steps: define the payloads and a handler, wire the engine into each worker process, and fan out a wave.

## 1. Define the payloads and a handler

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

See [Writing handlers](/guides/handlers) for reporting by exception, retries and idempotency.

## 2. Wire the engine (in each worker process)

One call, from `x86cc.RippleEngine.Hosting`:

```csharp
builder.AddRippleEngine(o =>
    {
        o.MaxConcurrency  = 32;                                        // per-instance execution cap
        o.EnableDashboard = true;                                      // /api + the SPA, from this process
        o.EnableMetrics   = true;                                      // the engine's OpenTelemetry meter
    })
    .AddHandler<TaxChange, RecalcCompany, RecalcHandler>(batchSize: 200, gapSeconds: 1);

builder.Build().Run();
```

That registers the storage services on the `ripple` connection string, applies the schema migration before
anything reads it (advisory-lock-safe on every replica), and starts the hosted services that make this process
a full peer: the dispatcher (heartbeat + claim), the execution pipeline, recovery, stats refresh, compaction,
and pause reconciliation. **Adding capacity is just starting another identical process.**

`(batchSize, gapSeconds)` is the fairness knob — a job's steady-state share of the cluster is roughly
`batchSize / gapSeconds` relative to its competitors. See [The scheduler](/architecture/scheduler).

A process that only *creates* waves (an API) uses the same call with `o.EnableWorkers = false`: it gets the
stores, the migration and the fan-out generators, but never claims or executes a ripple.

## 3. Fan out a wave

With the Marten source (`o.UseMartenFanOut()` in the setup lambda):

```csharp
var wave = await martenGenerator
    .Create(session, "VAT rise", new TaxChange { Rate = 0.23m })
    .AddRipples<Company, RecalcCompany>(
        c => c.TaxCode == "VAT-STD",                                   // predicate — runs server-side
        c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();                                                   // one INSERT…SELECT; no rows loaded
// wave.RippleCount == the number of impacted companies; the cluster starts on it immediately.
```

EF Core and in-memory sources use the same builder — see [Fan-out sources](/guides/fan-out).

## Build & test the repo

```bash
dotnet build x86cc.RippleEngine.slnx
dotnet build x86cc.RippleEngine.slnx -p:BuildSpa=false                  # without the Angular dashboard
dotnet test  x86cc.RippleEngine.Tests/x86cc.RippleEngine.Tests.csproj   # real Postgres via Testcontainers
```

The engine tests spin up a real `postgres:17` container (the fan-out and claim are raw SQL — there is no
in-memory fallback), so Docker/Podman must be running.
