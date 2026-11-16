---
title: Fan-out
sidebar_position: 3
---

# Fan-out — the server-side `INSERT … SELECT`

`WaveBuilder` is the heart of "never load the source rows". Each
`AddRipples<TSource, TMessage>(predicate, toMessage)` becomes **one** `INSERT INTO ripple.ripple … SELECT …`
that runs entirely in Postgres:

1. `session.Query<TSource>().Where(predicate).Select(toMessage)` → Marten emits
   `select jsonb_build_object(...) as data from <src> where ...`.
2. `IQueryable.ToCommand()` hands that over as an `NpgsqlCommand` with its bound parameters.
3. `WaveInsertSql` wraps it as the *source* of an `INSERT`, generating ripple ids with `gen_random_uuid()`,
   stamping the composite `type_key`, setting `state = 'Pending'`, and computing each ripple's
   `schedule_order` — framework literals inlined, user-driven values left as bound parameters.

Simplified, the generated statement has this shape (the real SQL lives in `WaveInsertSql` / `ScheduleOrderSql`):

```sql
INSERT INTO ripple.ripple (id, wave_id, type_key, state, schedule_order, payload, …)
SELECT gen_random_uuid(), '<wave id>', 'TaxChange|RecalcCompany', 'Pending',
       <base> + floor((row_number() over () - 1) / <batch_size>) * <gap_seconds>,
       src.data, …
FROM (
    select jsonb_build_object('CompanyId', d.id) as data   -- Marten's projection
    from public.mt_doc_company d
    where d.data ->> 'TaxCode' = $1                         -- bound parameter
) src;
```

So a taxation change over 400k companies is **one SQL statement**; the 400k rows are never materialised in the
client. The EF Core provider does the same with `IRelationalQueryingEnumerable.CreateDbCommand()` and
`to_jsonb(...)`; both share `WaveInsertSql` and `WaveBuilderBase`, so a provider only builds each spec's inner
`data` SQL.

## Variants

- **`AddRipplesBatched<TSource,TBatch,TItem>`** — wraps the scalar projection with
  `row_number() / jsonb_agg / GROUP BY (rn-1)/batchSize` to collapse N impacted rows into one batch task.
- **`AddRipplesRaw<TBatch>(sql)`** — an escape hatch for grouping LINQ can't translate.
- **In-memory** (`ICollectionWaveGenerator`) — an `unnest` insert of items the caller already holds.

The subtle `schedule_order` stamping lives once in `ScheduleOrderSql`, shared by both the `unnest` insert and
the `INSERT … SELECT`, so every insert path orders by exactly the same rule.

## Idempotent creation

`WaveIdempotency` takes a transaction-scoped advisory lock on the idempotency key and looks the wave up
*before* any insert — the query-source path writes the wave row last, and a duplicate must be caught before a
multi-million-row fan-out, not after. A unique partial index on `wave.idempotency_key` is the backstop.

## Expansion

`Continue(context)` runs the same insert paths for an existing wave, bracketed by `ExpansionSql` (a
once-per-key ledger row + a fence on the parent still `Running`) and preceded by
`ScheduleOrderSql.LockWaveTailsAsync` so concurrent appends don't stack on the same slots. See
[In-flight expansion](/guides/expansion).
