---
title: Stats & completion
sidebar_position: 8
---

# Stats and completion — the periodic recompute

`WaveStatsRefreshLoop` (every `WaveStatsRefreshInterval`, default 2s, on every instance) calls the DB-side
`refresh_wave_stats()`, gated by a `pg_try_advisory_lock` so at most one instance does the work at a time. It
is the **only** writer of a wave's live numbers and the **only** place a wave completes. It's a single
`UPDATE ripple.wave` that:

1. **Recomputes** each active wave's `pending`/`running`/`paused`/`failed` (and `retry_count`) from the ripple rows — reading only the small
   partial-index sets — and writes them, with `refreshed_at = now()`, onto the `wave` row. The per-wave counts
   are **correlated scalar subqueries**, each an index-range scan over one wave's slice.
2. **Completes** any active wave with `ripple_count > 0`, `pending = 0`, `running = 0` and `paused = 0` —
   flipping it to `Completed`, or `Faulted` if `failed > 0` — in the same statement. A wave with ripples of a
   [paused type](/operations/dashboard#pause--resume) therefore stays `Active` until the type is resumed and
   those ripples drain: parked work is not done.

3. **Updates each active wave's drain rate**: the ripples settled since the previous refresh, per elapsed
   second, blended into `drain_rate` as an exponential moving average with a 30s time constant. `drain_settled`
   records the settled count it was measured from. That count is taken against the *current* `ripple_count`, so
   in-flight expansion never reads as negative progress. The rate decays toward 0 if a wave stalls.

$$
\text{succeeded} = \text{ripple\_count} - \text{pending} - \text{running} - \text{paused} - \text{failed}
$$

`succeeded` is derived at read time, so no read scans the settled millions. A wave's estimated finish is derived
the same way, at read time:

$$
\text{eta} = \frac{\text{pending} + \text{running}}{\text{drain\_rate}}
$$

It is null for a wave that isn't Active, hasn't been refreshed twice yet, isn't measurably progressing, or has
only paused work left. Before a wave's first refresh
(`refreshed_at is null`) a read treats all its ripples as pending.

## Why recompute instead of count

There are **no synchronously-maintained counters** on the hot paths. Because the numbers are recomputed from
the truth they **self-heal**: a false recovery, a double-settle or a crash mid-write is corrected on the next
refresh — there is no delta to drift. And the claim/settle paths never contend on a hot counter row.

The trade: wave numbers are **eventually consistent**, up to one refresh interval stale.

:::tip For contributors
Storage-level tests must call `RefreshWaveStatsAsync()` (or run the engine) before asserting wave numbers.
If you add a per-wave aggregate, recompute it in `refresh_wave_stats()` — do not add a
synchronously-maintained counter.
:::

## Compaction and retention

`CompactionLoop` (every `CompactionInterval`, advisory-lock-gated):

- `compact_wave()` rolls a terminal wave's splash reports into aggregated `report_chunk` rows
  (`ReportChunkSize` targets each) and **deletes its ripples, splashes and expansion rows** — a per-wave
  `DELETE`, stamping `expire_at` from the wave type's retention.
- `purge_expired_waves()` deletes compacted waves past their `expire_at` (wave row + report chunks).

This keeps the hot tables small without partitioning, and gives exact per-type retention.
