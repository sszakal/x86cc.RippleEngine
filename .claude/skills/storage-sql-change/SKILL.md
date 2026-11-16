---
name: storage-sql-change
description: Checklist for changing RippleEngine's Postgres SQL or schema — EngineStore/SplashStore/DashboardQueries, WaveInsertSql, ScheduleOrderSql, ExpansionSql, M0001_Schema, refresh_wave_stats/compact_wave/purge functions. Use before editing any hot-path SQL, the claim, settlement, recovery, fan-out insert, indexes, or adding a per-wave aggregate.
---

# Changing Storage SQL / schema

The database is the only coordination surface; a subtle SQL change can silently break exactly-once, fairness, or
completion across the cluster while unit tests stay green. Walk this list before and after the edit.

## Where things live

- One migration: `x86cc.RippleEngine.Storage/Migrations/M0001_Schema.cs` holds the **whole** schema (the DB is
  ephemeral in the POC) — edit it in place, don't add `M0002`, unless told the schema is now persistent.
- `schedule_order` stamping: **only** `ScheduleOrderSql` (shared by `EngineStore.AddRipplesAsync` and
  `WaveInsertSql`). Never inline a second copy.
- In-flight expansion inserts: **only** through `ExpansionSql.TryBeginAsync` / `CompleteAsync`.
- Any insert into an existing wave first takes `ScheduleOrderSql.LockWaveTailsAsync`.
- Dashboard reads: `DashboardQueries` (the Dashboard project contains no SQL).

## Invariants to preserve

1. **No hot counters.** Claim, settlement and recovery change only `ripple.state`. A new per-wave number is
   recomputed in `refresh_wave_stats()`, never maintained synchronously. `succeeded` stays derived.
2. **Completion** is decided only in `refresh_wave_stats()` (pending = 0 and running = 0).
3. **Settlement fencing** is on `claimed_by` + `state = 'Running'` + `attempt` — all three.
4. **Recovery** is idempotent and fenced on `state = 'Running'`.
5. **Claim** stays one statement: heartbeat upsert + `FOR UPDATE SKIP LOCKED` over the lowest `schedule_order`,
   filtered to the instance's handled `type_key`s, retry-eligible only. No quota or counter writes.
6. **Schedule base** is the DB clock (`now()`) and the global frontier rule — never bare `now()`, never host time.
   Nothing reschedules except the operator `RescheduleWaveAsync` path (advisory-locked, `skip locked`).
7. **Bound parameters** for user-driven values; only framework-controlled values (guids, type names,
   `'Pending'`) are inlined.
8. Reads that run per poll/refresh must hit the **partial indexes** (`Pending` / `Running` / `Failed`); never
   scan settled rows. Check with `EXPLAIN` on a seeded database if you change a predicate or index.

## Style

Storage SQL is heavily annotated on purpose — explain *why* each clause exists, keep existing comments accurate,
and preserve the `<remarks>` on intentional POC simplifications.

## Verify

- Run the relevant suites through `run-tests`: `StorageTests`, `SchedulerTests`, `RegressionTests`,
  `SettlementPoisonTests`, `ExpansionIdempotencyTests`, `RescheduleTests`, `CompactionTests`,
  `WaveLifecycleTests`, `DashboardQueriesTests` — then the full suite.
- Add a regression test for the concurrency case you touched (two claimers, a stale re-claim, a retry repeat).
- Update `ARCHITECTURE.md` and the matching `docs/content/architecture/*.md` page when the mechanism changes.
