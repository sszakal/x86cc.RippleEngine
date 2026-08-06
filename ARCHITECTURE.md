# RippleEngine — Architecture

How the engine works, in depth. For orientation see [README.md](README.md); for working-in-the-repo guidance
see [AGENTS.md](AGENTS.md).

RippleEngine turns **one business event** into **millions of per-entity tasks** and executes them across a
cluster of identical worker processes — with **the source rows never leaving the database** and **no message
broker**. Everything is coordinated through Postgres: symmetric instances poll a shared queue with
`FOR UPDATE SKIP LOCKED`, ordered by a **precomputed `schedule_order` key** that bakes in fair-share at fan-out,
and prove liveness with heartbeats. Progress and completion are **recomputed from the ripple rows** by a
periodic stats refresh, so nothing maintains counters on the hot paths.

```
   Event ──▶  Wave (1 row + shared payload)
                │  server-side INSERT … SELECT  (source rows never loaded);
                │  each ripple stamped with a schedule_order ordering key
                ▼
             Ripples (N rows, one per target entity)
                │  global claim: ORDER BY schedule_order, SKIP LOCKED
                ▼
   Worker A ─┐  Worker B ─┐  Worker C ─┐   (identical processes; disjoint claims)
             ▼            ▼            ▼
          TPL ActionBlock executes IRippleHandler<TWave,TRipple>
                │  outcome
                ▼
             Splash (1 row per attempt) — ripple flips state only
                │
   WaveStatsRefreshLoop: refresh_wave_stats() recomputes the wave's numbers ──▶ Wave Completed/Faulted
```

## 1. Data model

Everything lives in the `ripple` schema (FluentMigrator, one migration `Storage/Migrations/M0001_Schema.cs` —
the DB is ephemeral at POC stage, so the schema is a single authoritative CREATE, not an ALTER trail). Hot
paths are served by **partial indexes** that hold only the small working set, never the settled millions.

| Table | Role | Hot? |
|---|---|---|
| `wave` | one small row per job: shared payload + `status` + `ripple_count` + its **recomputed** `pending`/`running`/`failed` (+`refreshed_at`) | fan-out / completion + the periodic refresh |
| `ripple` | the huge table: one row per task, with `state`/`attempt`/`type_key`/`schedule_order`/payload | yes → partial indexes |
| `splash` | one row per execution attempt (the audit trail: outcome, per-target `report` jsonb, claimed/started/ended, duration) | append-only |
| `instance_heartbeat` | cluster membership: `last_seen_at`, `executing` | tiny |
| `type_schedule` | per-`type_key` config: `batch_size` + `gap_seconds` (scheduling) + `max_attempts` (retry ceiling) | tiny, read at fan-out + claim |

Key modelling choices:

- **Nothing counts on the hot paths; the wave's numbers are recomputed.** The claim, settlement, and recovery
  change only `ripple.state`. A periodic `refresh_wave_stats()` recomputes each active wave's `pending`/`running`/
  `failed` onto the `wave` row from the actual rows, and completes a drained wave — all in one `UPDATE`. So the
  numbers **self-heal** (no drift under a false recovery) and no counter moves on the hot paths. A wave is done when it has
  no pending and no running ripples; `ripple_count` keeps growing as handlers expand it (iterative fan-out),
  so the original count can't signal completion. `succeeded` is never stored — derived as
  `ripple_count − pending − running − failed`.
- **Ripples flip a `state` flag, they don't move tables.** `Pending → Running → Succeeded/Failed`. Partial
  indexes (`… where state='Pending'`, `… where state='Running'`, `… where state='Failed'`) keep the claim,
  recovery, and refresh scans over the working set only.
- **`schedule_order` is a precomputed ordering key, not a deadline.** Stamped once at fan-out (§3); the claim
  simply pulls the globally lowest `schedule_order`. It is never rewritten.
- **There is no `Ripple` CLR type.** A ripple is just a row; the claim returns a lightweight `ClaimedRipple`
  record.

## 2. Fan-out — the server-side `INSERT … SELECT`

`WaveBuilder` (in `MartenDb`) is the heart of "never load the source rows". Each
`AddRipples<TSource, TMessage>(predicate, toMessage)` becomes **one** `INSERT INTO ripple.ripple … SELECT …`
that runs entirely in Postgres:

1. `session.Query<TSource>().Where(predicate).Select(toMessage)` → Marten emits
   `select jsonb_build_object(...) as data from <src> where ...`.
2. `IQueryable.ToCommand()` hands us that as an `NpgsqlCommand` with its bound parameters.
3. `BuildInsertSelect` wraps it as the *source* of an `INSERT`, generating ripple ids with
   `gen_random_uuid()`, stamping the composite `type_key`, setting `state = 'Pending'`, and computing each
   ripple's `schedule_order` (§3) — all as inlined framework literals; user-driven values stay bound parameters
   on the reused command.

So a taxation change over 400k companies is **one SQL statement**; the 400k company rows are never
materialised in the client. Variants:

- **`AddRipplesBatched<TSource,TBatch,TItem>`** — wraps Marten's scalar projection with
  `row_number()/jsonb_agg/GROUP BY (rn-1)/batchSize` to collapse N impacted rows into one batch task.
- **`AddRipplesRaw<TBatch>(sql)`** — an escape hatch for grouping Marten LINQ can't translate.
- **In-memory** (`ICollectionWaveGenerator`) — for work items the caller already holds (no queryable source),
  e.g. the sample's seed wave, which fans out "generate companies [start..start+N)" tasks.

**Two verbs, one mental model.** Every generator — in-memory (`ICollectionWaveGenerator`), Marten
(`IMartenWaveGenerator`), EF (`IEfWaveGenerator`) — exposes the same pair: **`Create`** starts a new wave;
**`Continue(context)`** *expands an existing wave from inside a handler*, appending ripples parented to the
running ripple (`parent_ripple_id`, the audit lineage) and bumping the wave's `ripple_count` so it can't
complete before the children run. Only the *source* differs — the queryable generators run the same
server-side `INSERT … SELECT` for expansion as for creation (a group ripple discovers its members with a query,
never loading them), while the in-memory generator serializes items the handler already holds. `Continue`
takes the handler's `IRippleContext`, which carries the wave id + this ripple's id. This is why "a wave's
ripple count keeps growing as handlers expand it" (§1) — expansion is just `Continue` on the same builders.

The subtle `schedule_order` stamping (§3) lives once in `ScheduleOrderSql`, shared by both insert paths (the
unnest insert behind the in-memory generator and the `INSERT … SELECT` behind the queryable ones), so a
`Continue`-expanded batch is ordered by exactly the same base-clamp/batch-gap rule as an initial fan-out.

Because the INSERT writes enum literals into JSONB, the Marten store **must** serialize enums as strings
(`UseSystemTextJsonForSerialization(EnumStorage.AsString)`).

## 3. The batch-interleaving scheduler — fairness precomputed into `schedule_order`

Fair-share is decided **once, at fan-out**, by stamping each ripple with a `schedule_order` — a `double
precision` **ordering key**, epoch-seconds based (anchored on `now()` + gap offsets) but treated as an opaque
number, not a timestamp/deadline. It is a pure
**ordering key, not a deadline**. Workers stay dumb: they claim the globally lowest `schedule_order` first
(§4). Nothing is ever rescheduled. This is static weighted-fair-queueing (a stride scheduler with
stride `gap / batch_size`).

The composite **`type_key = "{waveType}|{rippleType}"`** (e.g. `CorporateTaxChange|RecalcCorporateTax`, built
by `RippleTypeKey.Compose`) carries the config and resolves the handler:

- the **config key** — `type_schedule(batch_size, gap_seconds, max_attempts)` is keyed by it, seeded by
  `AddHandler<TWave,TRipple,THandler>(batchSize, gapSeconds, maxAttempts)` at startup (a one-shot
  `ScheduleSeeder`); batch/gap drive scheduling, `max_attempts` (nullable → engine default) the retry ceiling;
- the **handler key** — `RippleHandlerRegistry` resolves `IRippleHandler<TWave,TRipple>` by it, using the
  same `typeof(TWave).Name|typeof(TRipple).Name` at registration.

**Stamping (in the fan-out SQL).** For each ripple at position `k` within its type:

```
schedule_order = base + floor(k / batch_size) * gap_seconds            -- plain float arithmetic (seconds)
base           = coalesce( max(schedule_order of the wave's pending ripples),           -- continuation
                           greatest( epoch(now()), min(schedule_order over ALL pending) ) ) -- new job
```

(`schedule_order` is `double precision`; `epoch(now())` is `extract(epoch from now())` — the wall clock as
seconds, the monotonic anchor. No interval/timestamp math, just numbers.)

- `batch_size` consecutive ripples share one slot; each slot is `gap_seconds` after the last.
- **Continuation appends.** New work for a job with pending ripples bases off that pending tail, so it queues
  *after* it (FIFO within a job).
- **A new/drained job starts at the global frontier**, not wall-clock `now()`. The frontier is
  `min(schedule_order)` over all pending — the left edge of `ix_ripple_schedule_order`, the slot being consumed right
  now — clamped up to `now()` (a fully idle system, no pending, falls back to `now()`). This is the crucial
  clamp: because the engine drains **far faster than one slot per `gap`**, virtual time races ahead of the
  wall clock, so an in-flight job's frontier sits well in the *virtual future*. A late job based at bare
  `now()` would land far *behind* that frontier and the claim would drain its whole backlog exclusively —
  monopolising the cluster to "catch up" — before the two interleave. Basing at the frontier makes it
  interleave **immediately** (the classic weighted-fair-queueing virtual-start rule).
- **Interleaving.** From the frontier onward, the two jobs' batches alternate — the global order interleaves
  `A₁ B₁ A₂ B₂ …` instead of draining A fully first — far better than strict FIFO (behind *every* earlier task).
- **Fairness knob.** A job's steady-state share of the schedule ≈ `batch_size / gap_seconds`. Smaller batches
  / larger gaps spread a job more evenly so competitors progress sooner; larger batches / smaller gaps favour
  throughput. Keep `batch_size ≪ cluster execution capacity` for a *blended* mix (both jobs run concurrently);
  batches ≥ capacity give coarse slot-at-a-time alternation. In the sample, corporate (`batch 5`) vs employee
  (`batch 15`) at equal gap ⇒ employee gets ~3× the share, blended.
- `base` and `now()` are **DB-clock** (transaction-stable) so a lagging host can't jump the global queue.

There is **no per-type concurrency ceiling** — global concurrency is simply `MaxConcurrency × instances`. (If a
type ever needs a hard cap, e.g. an external rate limit, it must be re-introduced separately; the ordering key
controls *share*, not an absolute ceiling.)

## 4. The claim — one round trip, lowest `schedule_order` first

`EngineStore.PollAsync(limit, instanceId, executing)` is a **single statement** (one implicit transaction):

```
with
  beat     -- (a) upsert this instance's heartbeat (runs even at limit 0 → polling IS liveness)
  claimed  -- (b) the globally lowest-schedule_order pending, retry-eligible ripples across ALL waves,
           --     ORDER BY schedule_order, FOR UPDATE SKIP LOCKED, limit @limit;
           --     set Running, stamp claimed_by, attempt++
select …claimed ripples joined to their wave's shared payload…
```

Why it's shaped this way:

- **No runtime scheduling.** The fair-share is already in `schedule_order`, so the claim is just an index scan of
  `ix_ripple_schedule_order` — no type selection, no quota, no counters. A poll's batch is heterogeneous (whatever
  the precomputed order interleaves).
- **`SKIP LOCKED`** means N workers polling the same queue take **disjoint** slices without blocking — cluster
  throughput scales with instance count. All workers hammer the same left edge of the index; `SKIP LOCKED`
  keeps them disjoint.
- **The heartbeat rides on the poll.** No separate beat loop; a poll at `limit 0` still beats. The dispatcher
  falls back to a direct beat only if a poll throws, so a live node is never declared dead.
- **The claim is slim** — it returns only what execution needs plus the wave's shared payload (joined in the
  same query), so the big per-ripple payloads travel once.
- **Retry-eligibility** is `next_attempt_at is null or next_attempt_at <= now()`; a backing-off ripple keeps
  its (early) `schedule_order`, so once eligible it re-claims at its original queue position.

## 5. Execution — the TPL pipeline and back-pressure

`ExecutionPipeline` runs the claimed ripples through a single bounded `ActionBlock<PreparedRipple>`:

```
Dispatcher (1 per instance)                 ExecutionPipeline
  loop while capacity > 0:                     ActionBlock:  MDOP = MaxConcurrency
    claim up to capacity  ───── Post ───▶      BoundedCapacity = MaxConcurrency * PrefetchFactor
    refill immediately if it filled            per ripple: DI scope → resolve handler by type_key
                                               → run with ExecutionTimeout → success/failure channel
                                                     │
                                     batched flush loops write splashes (1 round trip / batch)
```

- **MDOP vs depth (the saturation fix).** Execution parallelism is `MaxConcurrency`, but the block's
  `BoundedCapacity` is `MaxConcurrency * PrefetchFactor` (default 2). The dispatcher **prefetches** a queue in
  front of the block, so a finishing slot immediately starts queued work — the block doesn't starve between
  poll cycles or while a completed batch's outcome is still being written. The dispatcher polls again *at
  once* when it filled its capacity, backing off only when the queue truly empties. (Without this, execution
  sawtooths 0↔cap and averages a small fraction of the cap.)
- **Back-pressure.** A ripple counts as "in flight" from claim until its outcome is **durably settled**
  (executed *and* written), not merely executed. So a stalled settlement keeps the in-flight count high,
  collapses claim capacity to zero, and pauses the dispatcher — which also bounds the in-memory settlement
  buffers to `depth`.
- **Outcome handling.** A handler returns a `SplashReport` built by exception — `report.Success(id)` /
  `report.Failed(id, msg)`, aggregating targets by `(outcome, message)`; unreported targets are inferred
  succeeded. The attempt fails if it throws **or** reports any Failed target — then it retries with exponential
  backoff (`next_attempt_at`) until the type's `max_attempts` (per-type config from `type_schedule` at claim
  time, else the engine default), then terminal; otherwise it succeeds. The resolved items are persisted as the
  splash's `report` jsonb; a throw becomes one Failed item over every target with the exception message, so a
  failed splash always explains itself. `IRippleContext` gives read-only attempt info, a timeout
  `CancellationToken`, and the wave/ripple ids for in-flight expansion.

## 6. Settlement — a fenced state flip

`SplashStore.CompleteRipplesAsync` / `FailRipplesAsync` (batched, one round trip each):

1. **Fenced state flip** — `update ripple r set state=… from unnest(@ids, @attempts) u where r.id=u.id and r.claimed_by=@me and r.state='Running' and r.attempt=u.attempt returning …`.
   Only rows still Running, *owned by us*, and *on the attempt this outcome came from* move (`RETURNING` yields
   the real set), so a wrongly-declared-dead instance's late write no-ops instead of resurrecting a reclaimed
   ripple. The **attempt is part of the fence**, not decoration: owner + state alone don't identify an attempt,
   because an instance that stalls past `HeartbeatTimeout` (while still inside `ExecutionTimeout`) has its work
   reclaimed, then resumes, re-registers under the *same* `InstanceId`, and can re-claim the very ripple it
   stalled on — its stale attempt-1 outcome would find `claimed_by=me` and `state='Running'` both true of
   attempt 2 and settle it out from under a still-running handler. Terminal failures flip to `Failed`;
   requeues flip to `Pending` with a per-ripple `next_attempt_at` backoff (and keep their `schedule_order`, so a
   retry re-claims at its original position).
2. **Splash rows** — one `unnest(...)` insert for the whole batch, only for ripples that actually transitioned
   (never a statement per ripple).

That's it — **no counters, no completion check**. The wave's numbers and its completion are the stats refresh's
job (§8), decided from the actual row states rather than from deltas. This makes settlement a small, contention-
free write and removes the post-commit race the old sharded-counter model had to guard against.

## 7. Recovery — surviving a dead instance

`RecoveryLoop` (a ~10s sweep on every instance) calls `EngineStore.RecoverStaleAsync`, one statement:

1. `dead` — instances whose heartbeat is older than `HeartbeatTimeout`, **excluding self**.
2. `moved` — their `Running` ripples, fenced on `state='Running'` (concurrent survivors don't double-process):
   requeued to `Pending` (keeping `schedule_order`, so they re-claim at their old position), or **poison-failed**
   terminally if they've exhausted `max_attempts` (their owner died mid-run every time — the attempt is bumped
   at claim, so a process-killing ripple can't loop forever).
3. `abandoned` — an `Abandoned` splash per reclaimed ripple, reconstructed from its claim, so an outcome-less
   attempt is *explained*.
4. `prune` — the dead heartbeat rows.

No counter repair and no completion logic — the wave's numbers and any resulting drain self-heal on the next
`refresh_wave_stats()`. It's idempotent and nearly free when healthy (empty `dead` set matches nothing). A *live*
instance that can't flush a settlement does **not** rely on recovery — it retries the write itself, because
recovery only reclaims *dead* instances.

## 8. Stats and completion — the periodic recompute

`WaveStatsRefreshLoop` (every `WaveStatsRefreshInterval`, default 2s, on every instance) calls the DB-side
`refresh_wave_stats()` via `EngineStore.TryRefreshWaveStatsAsync` — gated by a `pg_try_advisory_lock`, so at most one
instance does the work at a time and the rest are cheap no-ops. It is the **only** writer of a wave's live
numbers and the **only** place a wave completes. It's a single `UPDATE ripple.wave` that:

1. **Recomputes** each active wave's `pending`/`running`/`failed` by counting the ripple rows in each state —
   reading only the small partial-index sets (`ix_ripple_wave_pending`, the tiny `Running` set,
   `ix_ripple_failed`) — writing them (and `refreshed_at = now()`) onto the `wave` row. The per-wave counts are
   **correlated scalar subqueries**, so each is an index-range scan over one wave's slice rather than a global
   aggregate plus a hash join. That bounds `failed` and `retries` (which linger on terminal-but-uncompacted
   waves) but is close to *neutral* for `pending`/`paused` — a wave with pending ripples is Active by
   definition, so at target scale the one big wave's whole pending set is still counted every tick. Making the
   refresh genuinely cheap at 10M would take an incremental design (only waves whose ripples changed), which
   this POC does not have. `running` is a deliberate exception: its cluster-wide set is bounded by
   `MaxConcurrency × instances` — smaller than one big wave's slice — and `ix_ripple_running` is keyed on
   `claimed_by`, so it stays a single grouped aggregate.
2. **Completes** any active wave with `ripple_count > 0`, `pending = 0`, `running = 0` — flip it to `Completed`,
   or `Faulted` if `failed > 0`, stamping `completed_at` — in the same statement.

Because it recomputes from the truth, the numbers **self-heal**: a false recovery, a double-settle, a crash
mid-write — the next refresh corrects them; there is no delta to drift. `GetWaveAsync` (and the sample's
`/waves`) read the `wave` row, deriving `succeeded = ripple_count − pending − running − failed`. Before a wave's
first refresh (`refreshed_at is null`) a read treats all its ripples as pending (the honest pre-refresh view).

**Consequence:** a wave's numbers are eventually-consistent (up to one interval stale) and *not* synchronous
with a claim/settle. Tests that assert them either run the engine (which drives the loop) or call the refresh
directly.

## 9. Observability

`RippleMetrics` publishes on a BCL `Meter("x86cc.RippleEngine")` (inert without a listener): counters
`ripple.claimed` / `ripple.succeeded` / `ripple.failed` and a `ripple.duration` histogram, **all tagged with
`type_key`**, plus an `ripple.executing` gauge (per-instance in-flight). A host that wires OpenTelemetry with
`AddMeter("x86cc.RippleEngine")` gets per-handler-type throughput/latency (the sample exports it to the Aspire
dashboard). The sample also exposes `GET /engine/types` — each type's configured `batch_size` / `gap_seconds`
and its derived share — as the cross-process signal for its scheduler tests.

## 10. Lifecycle summary

```
Event ─▶ Wave created (Active)  ─▶  N ripples INSERT…SELECT'd (Pending, each stamped schedule_order)

each worker, continuously:
   poll: heartbeat + claim lowest schedule_order (SKIP LOCKED) → Running
   execute in the ActionBlock (prefetched, bounded by depth)
   settle: splash + fenced state flip (no counters)
   recover (10s): reclaim dead instances' in-flight ripples

any instance, every WaveStatsRefreshInterval (advisory-lock-gated):
   refresh_wave_stats(): recompute the wave's numbers from the ripple rows ; complete a drained wave

any instance, every CompactionInterval (advisory-lock-gated):
   compact_wave(): terminal wave's splash reports → aggregated report_chunk rows ; delete its ripples + splashes
   purge_expired_waves(): delete compacted waves past their per-type retention (wave row + report chunks)

result:
   ripple / splash: partial-indexed working set stays fast; settled rows are reclaimed per-wave at compaction
   wave numbers: recomputed from truth, self-healing, no hot counter rows → throughput scales with workers
```

## Known limits / roadmap

- The scheduler is **static**: once `schedule_order` is stamped it is never rebalanced, so thousands of jobs
  arriving at once can each sit one batch ahead of a newcomer (still far better than strict FIFO). A dynamic
  fair queue would be a later phase.
- All workers claim off the same left edge of `ix_ripple_schedule_order`; `schedule_order` inserts land mid-index
  (interleaved by design), so it bloats more than a monotonic `created_at` index would — fine at POC scale.
- Wave numbers are eventually-consistent (up to one `WaveStatsRefreshInterval` stale), the deliberate trade for
  zero hot-path counter maintenance.
- There is **no per-type concurrency ceiling** — global concurrency is `MaxConcurrency × instances`. A hard
  cap for a rate-limited dependency would need to be re-added.
- The "small hot tables forever" guarantee is met by **per-wave compaction + retention**, not
  partitioning: on completion a wave's ripples/splashes are compacted to `report_chunk`s and deleted, and the
  wave is purged after its per-type retention. The one cost is the compaction-time `DELETE FROM ripple WHERE
  wave_id = …` on a very large wave (WAL/bloat, autovacuum-reclaimed); `created_at` RANGE partitioning of
  `ripple`/`splash` is the escape hatch if that ever bites, and is orthogonal to retention.
- Recovery is heartbeat/ownership based; a **time-based reaper** would be a later refinement.
- Backoff mixes host and DB clocks — fine when NTP-synced.
