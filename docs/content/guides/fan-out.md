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

`AddRipple(message)` adds a single item. Each message is serialized with the
[payload contract](#the-payload-json-contract) and bulk-inserted, getting the same `schedule_order` stamping as
any other fan-out.

## Waves without a payload

Every generator also has a `Create(name)` overload without a wave payload: `Create(name)` on the collection
generator, `Create(session, name)` for Marten and `Create(dbContext, name)` for EF Core.

:::warning No handler can run a payload-less wave's ripples
The wave half of a `type_key` comes from the wave payload's type. Without a payload the key is
`|{RippleType}`, and every `AddHandler<TWave, TRipple, THandler>` registers `{TWave}|{TRipple}`, so **no
worker ever claims those ripples**. They stay `Pending` and the wave never completes. These overloads are for
storage-level tests and tooling. For real work, always pass a wave payload, even a small marker event type.
That payload also gives the dashboard a meaningful wave type.
:::

## Several ripple types in one wave

Call `AddRipples` more than once, with different message types, before `DispatchAsync`. All the specs are
created as **one wave**: it completes only when every ripple of every type has settled. Each type gets its own
`type_key` (`{WaveType}|{RippleType}`), so each is scheduled with its own `batch_size` / `gap_seconds`, can be
paused on its own, and needs its own handler:

```csharp
var wave = await martenGenerator
    .Create(session, "VAT rise", change)
    .AddRipples<Company, RecalcCompany>(c => c.TaxCode == "VAT-STD", c => new RecalcCompany { CompanyId = c.Id })
    .AddRipples<Invoice, ReissueInvoice>(i => i.Open && i.TaxCode == "VAT-STD", i => new ReissueInvoice { InvoiceId = i.Id })
    .DispatchAsync();
```

The same holds for the in-memory builder: `.AddRipples(listOfA).AddRipples(listOfB)`.

## Batched fan-out

`AddRipplesBatched` collapses many impacted rows into **one** ripple carrying an array. Each impacted row
contributes a scalar (`item`, typically its id). The scalars are bucketed `batchSize` at a time and each bucket
is assigned to the `into` array property of one `TBatch` payload. Use it for migrations and backfills where a
handler should work set-based over many ids at once (10M rows with `batchSize: 500` → 20k ripples):

```csharp
public sealed class RecalcBatch : IRippleTarget
{
    public List<Guid> CompanyIds { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => CompanyIds.Select(id => id.ToString()).ToList();
}

var wave = await martenGenerator
    .Create(session, "Backfill tax codes", new Backfill { Version = 7 })
    .AddRipplesBatched<Company, RecalcBatch, Guid>(
        c => c.Country == "DE",     // predicate: which rows
        c => c.Id,                  // item: the scalar each row contributes
        b => b.CompanyIds,          // into: the array property that receives the bucket
        batchSize: 500)
    .DispatchAsync();
```

The bucketing (`row_number()`) and aggregation (`jsonb_agg`) run server-side, so no rows are loaded. Two
consequences:

- The payload contains **only** the `into` property. Other `TBatch` properties keep their defaults, so put
  shared data on the wave payload.
- A batched ripple **retries as a unit**. One failed id fails the attempt and every id in the batch is
  re-run, so the work must be idempotent. Report the failing ids with
  `report.Failed(ids, reason)` so the audit trail names them.

## Raw SQL fan-out

`AddRipplesRaw<TBatch>(sql)` is the escape hatch for grouping LINQ can't translate (joins, computed bucket
keys, multi-column aggregation). Your SQL must return **one row per ripple with a single `jsonb` column**, which
becomes the payload. The framework wraps it in the `INSERT … SELECT` and stamps `TBatch`'s name as the
`payload_type`:

```csharp
var wave = await martenGenerator
    .Create(session, "Regional recalc", change)
    .AddRipplesRaw<RegionBatch>("""
        select jsonb_build_object('region', d.data->>'Region',
                                  'companyIds', jsonb_agg(d.id))
        from public.mt_doc_company d
        where d.data->>'Country' = 'DE'
        group by d.data->>'Region'
        """)
    .DispatchAsync();
```

:::danger The SQL is not parameterised
The string is spliced into the `INSERT` as-is and runs with no bound parameters. **Never interpolate user
input into it** — that is a SQL-injection hole running with the engine's database credentials. Keep it a
constant, or validate anything dynamic against a fixed allow-list.
:::

It is also the least portable path. You reference **physical table and column names** (Marten's
`mt_doc_*` tables and its `data` jsonb column, or EF's mapped tables), so a schema or mapping change breaks it
at run time, not at compile time. The JSON you build must also follow the payload contract below by hand.

## The payload JSON contract

Fan-out and execution are often different processes and deploys, so every wave and ripple payload goes
through one serializer contract, `RippleJson.Options`:

- `JsonSerializerDefaults.Web`: property names are written **camelCase** and read **case-insensitively**.
  The collection path writes `companyId`, while Marten's `jsonb_build_object` and EF's `to_jsonb` write the
  projected member name `CompanyId`. Both bind to the same property.
- `JsonStringEnumConverter`: enums are written as **names**, and read from names **or** integers. EF maps
  enums to integer columns by default, so its `to_jsonb` emits numbers, which still read correctly.

What this means for your payload types:

- They must be **deserializable** with System.Text.Json: a public parameterless constructor (or a
  constructor STJ can bind) and settable properties for everything projected.
- Mark **computed members** such as `TargetIds` with `[JsonIgnore]`. They are derived from the stored ids
  and shouldn't be persisted.
- The Marten store must use `EnumStorage.AsString` (see [above](#marten)), so that enum values the fan-out
  copies into JSONB are names.
- For [raw SQL fan-out](#raw-sql-fan-out), build keys that match your property names (any casing).

`RippleJson.Options` is public and read-only. Use it to serialize payloads yourself, for example in a test that
checks a projection round-trips, but never build your own `JsonSerializerOptions` for Ripple payloads.

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
