---
title: Fan-out sources
sidebar_position: 2
---

# Fan-out sources

Every generator exposes the same two verbs — **`Create`** (new wave) and **`Continue(context)`** (expand the
wave the current ripple belongs to). The only thing that varies is the *source* of the ripples.

| Source | Generator | Enable with | Use for |
|---|---|---|---|
| Marten LINQ query | `IMartenWaveGenerator` | `o.UseMartenFanOut()` | documents in a Marten store |
| EF Core query | `IEfWaveGenerator` | `o.UseEntityFrameworkFanOut()` | entities in a `DbContext` |
| In-memory collection | `ICollectionWaveGenerator` | always registered | work items you already hold |

Wave-creation code depends only on `x86cc.RippleEngine.Core`, never on the runtime.

## Marten

```csharp
var wave = await martenGenerator
    .Create(session, "VAT rise", new TaxChange { Rate = 0.23m })
    .AddRipples<Company, RecalcCompany>(
        c => c.TaxCode == "VAT-STD",                                   // predicate — runs server-side
        c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();                                                   // one INSERT…SELECT; no rows loaded
```

:::warning Enums as strings
The fan-out `INSERT` writes enum literals into JSONB, so the Marten store **must** serialize enums as strings:
`UseSystemTextJsonForSerialization(EnumStorage.AsString)`.
:::

## EF Core

Identical, with a `DbContext` in place of the session:

```csharp
var wave = await efGenerator
    .Create(dbContext, "VAT rise", new TaxChange { Rate = 0.23m })
    .AddRipples<Company, RecalcCompany>(c => c.TaxCode == "VAT-STD", c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();
```

The EF provider is pinned to EF Core **9.x** so Npgsql stays on the line Marten needs.

## In-memory

For work items the caller already holds — no queryable source:

```csharp
var wave = await collectionGenerator
    .Create("Nightly reconcile", new ReconcileRun { Date = today })
    .AddRipples(regions.Select(r => new ReconcileRegion { Region = r }))
    .DispatchAsync();

await collectionGenerator.FireAsync("Ad-hoc", waveP, ripplePayload);    // one-wave/one-ripple shorthand
```

## Batched and raw fan-out

Two more shapes on the queryable builders:

- **`AddRipplesBatched`** collapses N impacted rows into one ripple carrying an array of ids (10M rows →
  10M/N ripples, bucketing done server-side with `row_number()` / `jsonb_agg`).
- **`AddRipplesRaw<TBatch>(sql)`** is the escape hatch for grouping LINQ can't translate.

## Idempotent creation

Wave creation runs on the engine's own connection, so it **cannot commit atomically** with the business change
that triggers it: the caller commits its change, then creates the wave — and if that call times out, it has to
try again. Make the retry safe with an idempotency key:

```csharp
var wave = await martenGenerator
    .Create(session, "VAT rise", new TaxChange { Rate = 0.23m })
    .WithIdempotencyKey($"vat-rise:{changeId}")
    .AddRipples<Company, RecalcCompany>(c => c.TaxCode == "VAT-STD", c => new RecalcCompany { CompanyId = c.Id })
    .DispatchAsync();
```

At most one wave exists per key; a repeated `DispatchAsync` returns it **without fanning out**. The check runs
under a transaction-scoped advisory lock *before* any insert, so a duplicate is caught before a
multi-million-row fan-out, not after. The key frees up when retention purges the wave. This is not a
transactional outbox — the caller still owns the retry.

See [Fan-out internals](/architecture/fan-out) for how the `INSERT … SELECT` is built.
