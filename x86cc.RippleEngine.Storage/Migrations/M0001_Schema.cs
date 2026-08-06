using FluentMigrator;

namespace x86cc.RippleEngine.Storage.Migrations;

/// <summary>
/// The whole <c>ripple</c> schema in one migration. The engine is a POC and its database is ephemeral (fresh
/// Testcontainers / Aspire containers), so the schema is kept as a single authoritative CREATE rather than an
/// archaeology trail of incremental ALTERs — when this reaches production, freeze this and add forward
/// migrations from there.
/// <para>
/// Enum-valued columns (<c>status</c>/<c>state</c>/<c>outcome</c>) store the CLR enum names (e.g.
/// <c>'Pending'</c>) so Dapper maps them straight back. The hot paths — the global claim, recovery, and the
/// stats refresh — are served by <b>partial</b> indexes that hold only the small working set (pending /
/// in-flight / failed ripples), never the millions of settled rows.
/// </para>
/// </summary>
/// <remarks>
/// POC simplification: the <c>ripple</c> / <c>splash</c> tables are not partitioned yet. Monthly RANGE
/// partitioning by <c>created_at</c> (so retention can drop whole old partitions) is a Phase-2 add.
/// </remarks>
[Migration(1)]
public sealed class M0001_Schema : Migration
{
    public const string SchemaName = "ripple";

    public override void Up()
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        // --- wave: the small table, one row per fan-out. A payload carrier + status + its live numbers.
        //     `ripple_count` is a synchronous counter (bumped at fan-out/expansion). `pending`/`running`/
        //     `paused`/`failed` are NOT maintained on the hot paths — they are recomputed from the actual ripple
        //     states by refresh_wave_stats() (below); `refreshed_at` marks the last recompute (null = never yet,
        //     so reads treat the wave as all-pending). `paused` counts ripples parked while their type is paused
        //     (state='Paused') — it blocks completion so a paused-but-undrained wave stays Active. `succeeded` is
        //     derived at read time (ripple_count - pending - running - paused - failed), never stored.
        //     `retry_count` is the wave's re-execution count (splashes with attempt > 1) — recomputed like the
        //     other numbers (never a hot-path counter): refresh_wave_stats() keeps it live for an Active wave,
        //     and compact_wave() stamps its authoritative final value before the splashes are deleted.
        //     `avg_duration_ms`/`splash_sample_count`
        //     are the wave's mean per-attempt execution time over its succeeded splashes — computed ONCE at
        //     compaction (compact_wave, before the splashes are deleted), null until then. ------------------
        Execute.Sql($"""
            create table {SchemaName}.wave (
                id            uuid        not null primary key,
                name          text        not null,
                type          text        not null default 'default',
                payload       jsonb       null,
                payload_type  text        null,
                status        text        not null,
                ripple_count  bigint      not null default 0,
                pending       bigint      not null default 0,
                running       bigint      not null default 0,
                paused        bigint      not null default 0,
                failed        bigint      not null default 0,
                retry_count   bigint      not null default 0,
                refreshed_at  timestamptz null,
                created_at    timestamptz not null,
                completed_at  timestamptz null,
                compacted_at  timestamptz null,
                expire_at     timestamptz null,
                avg_duration_ms     bigint null,
                splash_sample_count bigint not null default 0
            )
            """);
        Execute.Sql($"create index ix_wave_active on {SchemaName}.wave (created_at) where status = 'Active'");
        // The compaction working set: terminal waves whose splashes/ripples haven't been rolled into a report yet.
        Execute.Sql($"create index ix_wave_uncompacted on {SchemaName}.wave (completed_at) " +
                    $"where status in ('Completed', 'Faulted') and compacted_at is null");
        // The retention working set: compacted waves due for deletion (expire_at stamped only when a retention applies).
        Execute.Sql($"create index ix_wave_expired on {SchemaName}.wave (expire_at) where expire_at is not null");

        // --- ripple: the huge table, one row per execution target -------------------------------
        //   type_key       = the composite "{waveType}|{rippleType}" — handler key + scheduling-config key.
        //   schedule_order = a precomputed ORDERING KEY (not a deadline, hence a plain number, not a
        //                    timestamp): the batch-interleaved position the claim sorts by. It is epoch-seconds
        //                    based (anchored on now() at fan-out + gap offsets) but treated as opaque —
        //                    double precision so the index is compact and compares fast. Stamped once; never
        //                    rescheduled.
        Execute.Sql($"""
            create table {SchemaName}.ripple (
                id               uuid             not null primary key,
                wave_id          uuid             not null,
                parent_ripple_id uuid             null,
                ripple_index     bigint           not null default 0,
                payload          jsonb            not null,
                payload_type     text             null,
                type_key         text             not null default '',
                state            text             not null,
                attempt          int              not null default 0,
                claimed_by       text             null,
                next_attempt_at  timestamptz      null,
                created_at       timestamptz      not null,
                schedule_order   double precision not null default extract(epoch from now()),
                claimed_at       timestamptz      null,
                completed_at     timestamptz      null
            )
            """);

        // The claim path: pending ripples across ALL waves, lowest schedule_order first. Partial => only the
        // working set, never the settled millions.
        Execute.Sql($"create index ix_ripple_schedule_order on {SchemaName}.ripple (schedule_order) where state = 'Pending'");
        // Lets the stats refresh count Pending per wave, and the fan-out find a wave's last pending scheduled
        // position (its scheduling base), via an index-only scan of the small pending set.
        Execute.Sql($"create index ix_ripple_wave_pending on {SchemaName}.ripple (wave_id) where state = 'Pending'");
        // Recovery: in-flight ripples owned by a (possibly dead) instance.
        Execute.Sql($"create index ix_ripple_running on {SchemaName}.ripple (claimed_by) where state = 'Running'");
        // Lets the stats refresh count Failed per wave (the exceptional, small set).
        Execute.Sql($"create index ix_ripple_failed on {SchemaName}.ripple (wave_id) where state = 'Failed'");
        // Lets the stats refresh count Paused per wave, and bounds the (usually empty) set of work parked while
        // its type is paused. Kept out of ix_ripple_schedule_order (partial on state='Pending'), so paused work
        // never bloats or is scanned by the hot claim path.
        Execute.Sql($"create index ix_ripple_paused on {SchemaName}.ripple (wave_id) where state = 'Paused'");
        // The two indexes the pause/resume reconcile (EngineStore.ReconcilePauseTransitionsAsync) chunks on. Its unit
        // of work is the TYPE, not the wave, and every index above is keyed on schedule_order / wave_id /
        // claimed_by — so without these its `type_key = ? and state = ?` chunk had nothing to seek on and
        // re-scanned the whole Pending (or Paused) set for EVERY chunk. PauseTransitionLoop runs many chunks per
        // tick on a 2-second cadence, so pausing a large type re-scanned millions of rows per tick and the drain
        // never converged. The resume side also wants schedule_order in the key: it un-parks lowest-first, so
        // the second column makes that ordering an index walk instead of a sort of the whole paused set.
        //
        // The Pending one is the only index here that adds write amplification to the fan-out's hot insert path
        // (three partial-on-Pending indexes now, not two). Accepted deliberately: the alternative is a pause
        // that cannot finish, and type_key is stamped once at insert and never updated, so the cost is one extra
        // index entry per ripple insert and nothing on the claim path.
        Execute.Sql($"create index ix_ripple_type_pending on {SchemaName}.ripple (type_key) where state = 'Pending'");
        Execute.Sql($"create index ix_ripple_type_paused on {SchemaName}.ripple (type_key, schedule_order) where state = 'Paused'");
        // The one NON-partial wave_id index, and the only thing compact_wave can use. Every index above is
        // partial on a live state, so SETTLED rows — the overwhelming majority — are in none of them, and
        // compact_wave's `delete ... where wave_id = ?` plus its rollup reads would sequential-scan the whole
        // table. CompactionLoop runs up to CompactionMaxWavesPerPass waves per tick, so that is many full scans
        // per tick at 10M+ scale. This is the standing cost of the deliberate no-partitioning choice (per-wave
        // DELETE buys exact per-type retention); it only has to serve terminal waves.
        Execute.Sql($"create index ix_ripple_wave on {SchemaName}.ripple (wave_id)");

        // --- splash: the audit trail, one row per attempt -----------------------------------------
        Execute.Sql($"""
            create table {SchemaName}.splash (
                id          uuid        not null primary key,
                ripple_id   uuid        not null,
                wave_id     uuid        not null,
                attempt     int         not null,
                claimed_at  timestamptz null,
                started_at  timestamptz not null,
                ended_at    timestamptz null,
                outcome     text        not null,
                duration_ms bigint      not null,
                report      jsonb       null
            )
            """);
        Execute.Sql($"create index ix_splash_ripple on {SchemaName}.splash (ripple_id)");
        // The retry set: splashes recording a re-execution (attempt > 1) — the exceptional set (only failures
        // retry), so this partial index stays tiny and lets refresh_wave_stats() count a wave's retries
        // index-only, never scanning the settled millions.
        Execute.Sql($"create index ix_splash_retry on {SchemaName}.splash (wave_id) where attempt > 1");
        // Same reason as ix_ripple_wave: compact_wave reads and deletes a whole wave's splashes by wave_id, and
        // ix_splash_retry is partial on attempt > 1 so it can't serve the all-attempts scan.
        Execute.Sql($"create index ix_splash_wave on {SchemaName}.splash (wave_id)");

        // --- instance_heartbeat: cluster membership -------------------------------------------
        Execute.Sql($"""
            create table {SchemaName}.instance_heartbeat (
                instance_id  text        not null primary key,
                last_seen_at timestamptz not null,
                executing    int         not null default 0
            )
            """);

        // --- type_schedule: per-type knobs, the single source of truth for scheduling config -------
        // The fan-out reads (batch_size, gap_seconds) to stamp schedule_order; the claim + recovery read
        // max_attempts (the retry ceiling) per type. A type with no row here — or a null max_attempts — falls
        // back to the reserved DEFAULT row ('__default__', seeded just below) rather than a constant baked into
        // the SQL, so the engine's defaults live in the database and are editable from the dashboard. Per-type
        // rows are seeded at handler registration (insert-if-absent, so a dashboard edit survives restart) and
        // added/edited/reset from the dashboard. A job's steady-state throughput share ~ batch_size / gap_seconds.
        Execute.Sql($"""
            create table {SchemaName}.type_schedule (
                type_key     text    not null primary key,
                -- NULLABLE, like max_attempts: null means "inherit the DEFAULT row" (ScheduleOrderSql
                -- .TypeConfigExpr coalesces to it). A row can exist purely to hold pause_state — pausing a type
                -- that was happily inheriting must not silently freeze its batch/gap at whatever the DEFAULT
                -- happened to be, which is what copying concrete values into the materialised row used to do:
                -- the type then quietly stopped tracking later DEFAULT edits and reported itself "configured".
                batch_size   int     null,
                gap_seconds  numeric null,
                max_attempts int     null,
                -- The pause state machine — the DESIRED state; a background loop (PauseTransitionLoop →
                -- reconcile) moves this type's ripples between 'Pending' and 'Paused' in bounded chunks to match
                -- it, so a pause/resume over millions of ripples is never one giant transaction.
                --   'active'          — normal.
                --   'paused'          — should be paused: the claim skips this type INSTANTLY (backstop predicate)
                --                       and every writer of 'Pending' (fan-out/retry/recovery) writes 'Paused'
                --                       (StateExpr); the loop parks the residual Pending rows out of the claim
                --                       index. In-flight ('Running') ripples are never paused.
                --   'resuming_rebase' — resuming: the loop flips 'Paused'→'Pending' in chunks, re-stamping
                --                       schedule_order onto the current frontier (fair interleave); → 'active' when drained.
                --   'resuming_asis'   — resuming without re-stamping (runs ahead to catch up); → 'active' when drained.
                pause_state  text    not null default 'active',
                -- The DEFAULT row is the floor everything coalesces to, so IT must always carry concrete
                -- values — a null there would make schedule_order null and violate the ripple's NOT NULL.
                constraint ck_type_schedule_default_complete check (
                    type_key <> '{RippleTypeKey.Default}'
                    or (batch_size is not null and gap_seconds is not null and max_attempts is not null))
            )
            """);
        // The DEFAULT row: the fall-back batch/gap/max every unconfigured type_key inherits (via the coalesce in
        // ScheduleOrderSql.TypeConfigExpr and the claim/recovery max_attempts lookup). Values match the
        // RippleOptions.*Fallback constants — one source, referenced here so the two never drift.
        Execute.Sql($"""
            insert into {SchemaName}.type_schedule (type_key, batch_size, gap_seconds, max_attempts)
            values ('{RippleTypeKey.Default}', {RippleOptions.DefaultBatchSizeFallback},
                    {RippleOptions.DefaultGapSecondsFallback}, {RippleOptions.DefaultMaxAttemptsFallback})
            """);

        // The DB-side monitoring/completion source. In ONE update it recomputes each active wave's live numbers
        // from the small partial-index sets (pending via ix_ripple_wave_pending, running via the tiny Running
        // set, paused via ix_ripple_paused, failed via ix_ripple_failed), stamps refreshed_at, and — in the same
        // pass — settles any wave that has drained (ripple_count > 0, pending = 0, running = 0, paused = 0) to
        // Completed/Faulted. The hot
        // claim/settle/recovery paths write no counters; this is the ONLY place a wave's numbers are updated and
        // the ONLY place it completes, so the numbers self-heal from the truth. succeeded is never stored — it
        // is derived at read time as ripple_count - pending - running - failed. Called periodically
        // (advisory-lock-gated) by WaveStatsRefreshLoop; a pg_cron job could call the identical function instead.
        Execute.Sql($"""
            create or replace function {SchemaName}.refresh_wave_stats() returns void
            language plpgsql as $fn$
            begin
                update {SchemaName}.wave w
                set pending = c.pending,
                    running = c.running,
                    paused  = c.paused,
                    failed  = c.failed,
                    retry_count = c.retries,
                    refreshed_at = now(),
                    -- A wave drains only when nothing is pending, running, OR paused: parked (paused) work is
                    -- not done, so a wave whose remainder is all Paused stays Active until it is resumed.
                    status = case when w.ripple_count > 0 and c.pending = 0 and c.running = 0 and c.paused = 0
                                  then (case when c.failed > 0 then 'Faulted' else 'Completed' end)
                                  else w.status end,
                    completed_at = case when w.ripple_count > 0 and c.pending = 0 and c.running = 0 and c.paused = 0
                                        then now() else w.completed_at end
                from (
                    -- The per-wave counts are CORRELATED (scalar subqueries), not uncorrelated `group by
                    -- wave_id` sub-selects LEFT JOINed to the active waves. That shape put the
                    -- `status = 'Active'` filter outside the aggregates, where Postgres cannot push it down, so
                    -- each count hash-aggregated the whole matching set and threw away every group that did not
                    -- belong to an active wave. Correlated, each is an index-range scan over ONE wave's slice.
                    --
                    -- Be honest about what that does and does not buy. It genuinely bounds `failed` and
                    -- `retries`, which accumulate on waves that are terminal but not yet compacted. It buys
                    -- almost NOTHING on `pending`/`paused`: a wave with pending ripples is Active by
                    -- definition, so the status filter excludes essentially none of them, and the single
                    -- 10M-ripple wave at target scale still has all 10M index entries counted every tick. The
                    -- win here is shape (N index-range scans instead of a global aggregate + hash join per
                    -- count), not a change in the dominant cost. If the refresh running back-to-back ever
                    -- becomes the real problem, the fix is incremental — refresh only waves whose ripples
                    -- changed since the last pass — not another join-shape rewrite.
                    select w2.id,
                           (select count(*) from {SchemaName}.ripple
                            where wave_id = w2.id and state = 'Pending') as pending,
                           coalesce(rn.n, 0)                             as running,
                           (select count(*) from {SchemaName}.ripple
                            where wave_id = w2.id and state = 'Paused')  as paused,
                           (select count(*) from {SchemaName}.ripple
                            where wave_id = w2.id and state = 'Failed')  as failed,
                           -- Retries: re-execution attempts (splash.attempt > 1), index-only via ix_splash_retry.
                           (select count(*) from {SchemaName}.splash
                            where wave_id = w2.id and attempt > 1)       as retries
                    from {SchemaName}.wave w2
                    -- Running is the ONE count that stays uncorrelated, because it is the one whose global set
                    -- is smaller than a single wave's slice: at most MaxConcurrency x instances rows exist in
                    -- 'Running' cluster-wide, and ix_ripple_running is keyed on claimed_by, so there is no
                    -- (wave_id) index a correlated count could use — it would fall back to ix_ripple_wave and
                    -- walk ALL of a big wave's ripples. Aggregating the whole tiny Running set once per refresh
                    -- is strictly cheaper than that.
                    left join (select wave_id, count(*) n from {SchemaName}.ripple where state = 'Running' group by wave_id) rn
                           on rn.wave_id = w2.id
                    where w2.status = 'Active'
                ) c
                where c.id = w.id;
            end
            $fn$
            """);

        // --- ripple_type_metric: the surviving per-type execution-time metric -------------------------------
        // A wave's splashes are deleted at compaction, so any cross-wave metric must be persisted before then.
        // `avg_ms` (execution time) and `avg_wait_ms` (queue wait = time from ripple creation to claim) are both
        // COUNT-WEIGHTED EWMAs: each compaction blends a wave's mean in with weight = that wave's succeeded-attempt
        // count, and decays the accumulated `weight` by a fixed factor per wave so the averages track recent waves
        // (see compact_wave). `sample_count` is the raw lifetime count, kept only as the "(n)" label.
        // `avg_retry_rate` is a simpler PER-WAVE EWMA of the retry rate (retries / ripple_count): each compacted
        // wave is one observation blended with alpha = 0.2 (lambda = 0.8), seeded on the first wave; a fraction,
        // e.g. 0.021 = 2.1%. One tiny row per type_key.
        Execute.Sql($"""
            create table {SchemaName}.ripple_type_metric (
                type_key       text             not null primary key,
                sample_count   bigint           not null default 0,
                weight         double precision not null default 0,
                avg_ms         double precision null,
                avg_wait_ms    double precision null,
                avg_retry_rate double precision null
            )
            """);

        // --- report_chunk: the compacted, aggregated report that survives a wave's ripples/splashes ---------
        // Once a wave is terminal, compact_wave() rolls all its splash reports into these chunks (grouping
        // targets by (outcome, output)), then deletes the ripple/splash rows. Each chunk holds up to chunk_size
        // targets across its items; a wave is a handful of chunks instead of millions of rows.
        Execute.Sql($"""
            create table {SchemaName}.report_chunk (
                wave_id      uuid        not null,
                chunk_index  int         not null,
                items        jsonb       not null,
                target_count int         not null,
                created_at   timestamptz not null default now(),
                primary key (wave_id, chunk_index)
            )
            """);

        // compact_wave: roll a terminal wave's per-attempt splash reports into aggregated report_chunk rows,
        // then reclaim the ripple/splash rows. Set-based, one transaction:
        //   1. explode each splash.report item into (outcome, output, target_id) rows (retries ⇒ a target can
        //      recur across items/attempts, kept as-is);
        //   2. order by (outcome, output) and cut into chunks of `chunk_size` targets (a group bigger than a
        //      chunk splits across chunks) via a row-number / chunk_size window;
        //   3. re-aggregate per (chunk, outcome, output) into an items jsonb and insert one report_chunk per chunk;
        //   4. roll up execution time over the wave's SUCCEEDED splashes — the wave's own mean (onto the wave row)
        //      and each type_key's running count+total (into ripple_type_metric) — while splashes AND ripples
        //      still exist (the per-type rollup joins splash → ripple for type_key);
        //   5. delete the wave's splashes AND ripples, stamp compacted_at, and (if a retention applies) stamp
        //      expire_at = completed_at + retention for the later retention purge.
        // Returns the number of chunks written. Idempotent-ish: a wave with no splashes just stamps compacted_at
        // (avg_duration_ms stays null, no type-metric rows touched).
        Execute.Sql($"""
            create or replace function {SchemaName}.compact_wave(p_wave_id uuid, p_chunk_size int, p_retention interval) returns int
            language plpgsql as $fn$
            declare
                v_chunks int;
            begin
                with exploded as (
                    -- LEFT join lateral, not CROSS: an item whose targetIds is [] must survive. A cross join
                    -- drops the row entirely, which silently discarded every targetless report — both recovery
                    -- paths always write 'targetIds', '[]' (Abandoned records), and SplashReportBuilder.Failed
                    -- emits a targetless item whenever the failure predates target resolution (no handler
                    -- registered for the type_key, payload deserialization throwing). Those waves compacted to a
                    -- completely empty report. tid is null for such items and is filtered back out below.
                    select item->>'outcome' as outcome, item->>'output' as output, tid
                    from {SchemaName}.splash s
                    cross join lateral jsonb_array_elements(s.report) as item
                    left join lateral jsonb_array_elements_text(item->'targetIds') as tid on true
                    where s.wave_id = p_wave_id and s.report is not null
                ),
                chunked as (
                    select outcome, output, tid,
                           (row_number() over (order by outcome, output) - 1) / p_chunk_size as chunk_index
                    from exploded
                ),
                grouped as (
                    -- coalesce so a targetless group aggregates to [] rather than jsonb_agg's null; count only
                    -- real targets so target_count stays a target count (a targetless item contributes 0).
                    select chunk_index, outcome, output,
                           coalesce(jsonb_agg(tid) filter (where tid is not null), '[]'::jsonb) as target_ids,
                           count(tid) as n
                    from chunked
                    group by chunk_index, outcome, output
                ),
                ins as (
                    insert into {SchemaName}.report_chunk (wave_id, chunk_index, items, target_count)
                    select p_wave_id, chunk_index,
                           jsonb_agg(jsonb_build_object('outcome', outcome, 'output', output, 'targetIds', target_ids)),
                           sum(n)
                    from grouped
                    group by chunk_index
                    returning 1
                )
                select count(*) into v_chunks from ins;

                -- Execution-time rollups over the succeeded attempts, BEFORE the splash/ripple deletes below.
                -- Per-wave mean onto the wave row (null-safe: avg over the empty set leaves it null).
                update {SchemaName}.wave w
                set avg_duration_ms     = agg.avg_ms,
                    splash_sample_count = agg.n
                from (
                    select avg(s.duration_ms)::bigint as avg_ms, count(*) as n
                    from {SchemaName}.splash s
                    where s.wave_id = p_wave_id and s.outcome = 'Succeeded'
                ) agg
                where w.id = p_wave_id and agg.n > 0;

                -- Per-type count-weighted EWMAs (survive this wave's compaction), joining splash → ripple for
                -- type_key. This wave contributes n = count(*) succeeded attempts with mean execution time
                -- m = avg(duration_ms) and mean queue wait w = avg(claimed_at - ripple.created_at). We blend each
                -- into its stored average with weight n, against the accumulated `weight` decayed by λ = 0.8 per
                -- wave (keep the 0.8 occurrences in sync) — so the averages "remember" ~1/(1-λ) ≈ 5 recent waves
                -- and each wave moves them in proportion to its size. First observation seeds them.
                insert into {SchemaName}.ripple_type_metric as t (type_key, sample_count, weight, avg_ms, avg_wait_ms)
                select r.type_key, count(*), count(*), avg(s.duration_ms),
                       avg(extract(epoch from (s.claimed_at - r.created_at)) * 1000)
                from {SchemaName}.splash s
                join {SchemaName}.ripple r on r.id = s.ripple_id
                where s.wave_id = p_wave_id and s.outcome = 'Succeeded'
                group by r.type_key
                on conflict (type_key) do update
                set avg_ms = (coalesce(t.avg_ms, 0) * t.weight * 0.8 + excluded.avg_ms * excluded.sample_count)
                             / (t.weight * 0.8 + excluded.sample_count),
                    -- keep the prior wait EWMA if this wave has no wait samples (claimed_at unknown)
                    avg_wait_ms = case when excluded.avg_wait_ms is null then t.avg_wait_ms
                                       else (coalesce(t.avg_wait_ms, 0) * t.weight * 0.8
                                             + excluded.avg_wait_ms * excluded.sample_count)
                                            / (t.weight * 0.8 + excluded.sample_count) end,
                    weight = t.weight * 0.8 + excluded.sample_count,
                    sample_count = t.sample_count + excluded.sample_count;

                -- Retries: authoritative final per-wave count (splashes with attempt > 1), stamped onto the wave
                -- row before the splashes vanish. (refresh_wave_stats already tracked this live; recompute here so
                -- the terminal value is exact even if the last refresh missed the final settlements.)
                update {SchemaName}.wave w
                set retry_count = agg.n
                from (select count(*) as n from {SchemaName}.splash where wave_id = p_wave_id and attempt > 1) agg
                where w.id = p_wave_id;

                -- Per-type retry-rate EWMA (survives compaction). This wave contributes, PER type_key, the rate
                -- r = retries / ripples = count(splash where attempt > 1) / count(ripples). Each wave is ONE
                -- observation, blended with alpha = 0.2 (lambda = 0.8, kept in sync with the EWMAs above), seeded on the
                -- first wave. Kept as a separate upsert from the succeeded-only avg_ms/avg_wait_ms rollup because
                -- retries span all outcomes.
                insert into {SchemaName}.ripple_type_metric as t (type_key, avg_retry_rate)
                select r.type_key,
                       count(*) filter (where s.attempt > 1)::double precision / count(distinct r.id)
                from {SchemaName}.ripple r
                left join {SchemaName}.splash s on s.ripple_id = r.id
                where r.wave_id = p_wave_id
                group by r.type_key
                on conflict (type_key) do update
                set avg_retry_rate = case when t.avg_retry_rate is null then excluded.avg_retry_rate
                                          else t.avg_retry_rate * 0.8 + excluded.avg_retry_rate * 0.2 end;

                delete from {SchemaName}.splash where wave_id = p_wave_id;
                delete from {SchemaName}.ripple where wave_id = p_wave_id;
                update {SchemaName}.wave
                set compacted_at = now(),
                    expire_at = case when p_retention is null then null else completed_at + p_retention end
                where id = p_wave_id;

                return v_chunks;
            end
            $fn$
            """);

        // purge_expired_waves: delete up to p_limit compacted, past-retention waves — their report chunks then
        // the wave rows (ripples/splashes are long gone). One transaction. Returns the number of waves purged.
        Execute.Sql($"""
            create or replace function {SchemaName}.purge_expired_waves(p_limit int) returns int
            language plpgsql as $fn$
            declare
                v_ids uuid[];
            begin
                select array_agg(id) into v_ids from (
                    select id from {SchemaName}.wave
                    where expire_at is not null and expire_at < now()
                    order by expire_at
                    limit p_limit
                ) e;

                if v_ids is null then
                    return 0;
                end if;

                delete from {SchemaName}.report_chunk where wave_id = any(v_ids);
                delete from {SchemaName}.wave where id = any(v_ids);
                return array_length(v_ids, 1);
            end
            $fn$
            """);
    }

    public override void Down()
    {
        Execute.Sql($"drop function if exists {SchemaName}.purge_expired_waves(int)");
        Execute.Sql($"drop function if exists {SchemaName}.compact_wave(uuid, int, interval)");
        Execute.Sql($"drop function if exists {SchemaName}.refresh_wave_stats()");
        Delete.Table("report_chunk").InSchema(SchemaName);
        Delete.Table("ripple_type_metric").InSchema(SchemaName);
        Delete.Table("type_schedule").InSchema(SchemaName);
        Delete.Table("instance_heartbeat").InSchema(SchemaName);
        Delete.Table("splash").InSchema(SchemaName);
        Delete.Table("ripple").InSchema(SchemaName);
        Delete.Table("wave").InSchema(SchemaName);
    }
}
