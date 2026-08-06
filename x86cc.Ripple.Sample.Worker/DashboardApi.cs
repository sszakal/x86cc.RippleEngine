using Dapper;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.Ripple.Sample.Worker;

/// <summary>
/// The dashboard read API (Wave / Ripple / Splash vocabulary), hosted by every worker: the engine instances
/// are the symmetric, always-on cluster, and this surface is just read-only projections over the same
/// <c>ripple</c> schema they already poll. camelCase aliases so the JSON matches the Angular SPA models. The
/// SPA's own static files are served from <c>wwwroot</c> (same origin), so no proxy is needed in production.
/// </summary>
internal static class DashboardApi
{
    public static void MapDashboardApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Wave list + summary, with optional status / text / created-at-range filters. `succeeded` is derived
        // (never stored); a not-yet-refreshed wave (refreshed_at null) reports all its ripples as pending.
        api.MapGet("/waves", async (string? status, string? q, DateTimeOffset? from, DateTimeOffset? to,
            int? limit, RippleDataSource db) =>
        {
            var p = new
            {
                status = string.IsNullOrWhiteSpace(status) ? null : status,
                q = string.IsNullOrWhiteSpace(q) ? null : $"%{q}%",
                from, to,
                limit = Math.Clamp(limit ?? 50, 1, 500)
            };
            const string where =
                """
                where (@status is null or w.status = @status)
                  and (@q is null or w.name ilike @q or w.type ilike @q)
                  and (@from is null or w.created_at >= @from)
                  -- HALF-OPEN [from, to), matching /waves/histogram. When this was inclusive a wave created
                  -- exactly on a bucket boundary appeared in two adjacent ranges' timelines and disagreed with
                  -- the total derived from the histogram — and the SPA hands both endpoints the same bounds.
                  and (@to is null or w.created_at < @to)
                """;

            await using var conn = await db.OpenConnectionAsync();
            var waves = await conn.QueryAsync(
                $"""
                 select w.id, w.name, w.type, w.status,
                        w.ripple_count as "rippleCount",
                        case when w.refreshed_at is null then w.ripple_count else w.pending end as pending,
                        w.running,
                        w.paused,
                        case when w.refreshed_at is null then 0
                             else greatest(0, w.ripple_count - w.pending - w.running - w.paused - w.failed) end as succeeded,
                        w.failed,
                        w.retry_count as "retryCount",
                        w.avg_duration_ms as "avgDurationMs",
                        w.splash_sample_count as "splashSampleCount",
                        case when w.completed_at is not null
                             then round(extract(epoch from (w.completed_at - w.created_at)) * 1000)::bigint end as "durationMs",
                        case when w.completed_at is not null and w.completed_at > w.created_at
                             then round(w.ripple_count / extract(epoch from (w.completed_at - w.created_at)))::bigint end as "throughput",
                        w.created_at as "createdAt", w.completed_at as "completedAt", w.compacted_at as "compactedAt"
                 from ripple.wave w
                 {where}
                 order by w.created_at desc
                 limit @limit
                 """, p);

            var stats = await conn.QuerySingleAsync(
                $"""
                 select count(*) as total,
                        count(*) filter (where w.status = 'Active')    as active,
                        count(*) filter (where w.status = 'Completed') as completed,
                        count(*) filter (where w.status = 'Faulted')   as faulted
                 from ripple.wave w
                 {where}
                 """, p);

            return Results.Ok(new { waves, stats });
        });

        // Per-day wave counts for the contribution heatmap (grouped by created_at day, UTC).
        api.MapGet("/waves/activity", async (int? days, RippleDataSource db) =>
        {
            var window = Math.Clamp(days ?? 365, 1, 366);
            await using var conn = await db.OpenConnectionAsync();
            var rows = await conn.QueryAsync(
                """
                select to_char(date_trunc('day', created_at), 'YYYY-MM-DD') as date,
                       count(*) as count,
                       count(*) filter (where status = 'Completed') as completed,
                       count(*) filter (where status = 'Faulted')   as faulted,
                       count(*) filter (where status = 'Active')    as running
                from ripple.wave
                -- Plain now(): it is already timestamptz, so it compares correctly against created_at.
                -- `now() at time zone 'utc'` strips the zone, and comparing that back to a timestamptz makes
                -- Postgres re-interpret it in the SESSION's TimeZone — shifting the window by the server's
                -- offset on any non-UTC session.
                where created_at >= now() - (@window || ' days')::interval
                group by 1
                order by 1
                """, new { window });
            return Results.Ok(new { days = rows });
        });

        // Wave counts bucketed by a time granularity over a [from, to) range — powers the adaptive tile-zoom
        // (day → hour → minute → second). Returns only NON-EMPTY buckets, colored by outcome like the heatmap.
        api.MapGet("/waves/histogram", async (DateTimeOffset from, DateTimeOffset to, string bucket, string? tz, RippleDataSource db) =>
        {
            // date_trunc's unit is bound safely (its first arg is text), but whitelist it anyway.
            if (bucket is not ("year" or "day" or "hour" or "minute" or "second"))
            {
                return Results.BadRequest(new { error = "bucket must be one of: year, day, hour, minute, second" });
            }
            // Bucket in the CALLER's timezone. The SPA colors/keys each calendar cell by the browser-local day
            // and drills in on browser-local midnight→midnight windows, so the server must group on the same
            // day boundaries or a green cell can drill into zero waves (its burst falls in the DB-tz day but
            // outside the browser-tz window). `created_at at time zone @tz` shifts the timestamptz to the
            // caller's wall clock; we date_trunc that and return the naive wall-clock start, so the browser
            // reads back the exact day it grouped by. Defaults to UTC when the caller sends no zone.
            var zone = string.IsNullOrWhiteSpace(tz) ? "UTC" : tz;
            await using var conn = await db.OpenConnectionAsync();
            var buckets = await conn.QueryAsync(
                """
                select date_trunc(@bucket, created_at at time zone @zone) as start,
                       count(*) as count,
                       count(*) filter (where status = 'Completed') as completed,
                       count(*) filter (where status = 'Faulted')   as faulted,
                       count(*) filter (where status = 'Active')    as running
                from ripple.wave
                where created_at >= @from and created_at < @to
                group by 1
                order by 1
                """, new { bucket, zone, from, to });
            return Results.Ok(new { buckets });
        });

        // One wave's detail (enriched with the compaction-computed avg execution time).
        api.MapGet("/waves/{id:guid}", async (Guid id, RippleDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var wave = await conn.QuerySingleOrDefaultAsync(
                """
                select w.id, w.name, w.type, w.status, w.payload_type as "payloadType",
                       w.ripple_count as "rippleCount",
                       case when w.refreshed_at is null then w.ripple_count else w.pending end as pending,
                       w.running,
                       w.paused,
                       case when w.refreshed_at is null then 0
                            else greatest(0, w.ripple_count - w.pending - w.running - w.paused - w.failed) end as succeeded,
                       w.failed,
                       w.retry_count as "retryCount",
                       w.avg_duration_ms as "avgDurationMs", w.splash_sample_count as "splashSampleCount",
                       case when w.completed_at is not null
                            then round(extract(epoch from (w.completed_at - w.created_at)) * 1000)::bigint end as "durationMs",
                       case when w.completed_at is not null and w.completed_at > w.created_at
                            then round(w.ripple_count / extract(epoch from (w.completed_at - w.created_at)))::bigint end as "throughput",
                       w.created_at as "createdAt", w.completed_at as "completedAt", w.compacted_at as "compactedAt"
                from ripple.wave w where w.id = @id
                """, new { id });
            return wave is null ? Results.NotFound() : Results.Ok(wave);
        });

        // Per-type metrics: scheduling share (batch/gap) + the surviving count-weighted EWMA of execution time
        // from ripple_type_metric (avg_ms is the moving average; sample_count is the lifetime "(n)" label).
        api.MapGet("/metrics/types", async (RippleDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var rows = await conn.QueryAsync(
                """
                select coalesce(ts.type_key, m.type_key) as "typeKey",
                       ts.batch_size  as "batchSize",
                       ts.gap_seconds as "gapSeconds",
                       case when ts.gap_seconds is not null and ts.gap_seconds <> 0
                            then ts.batch_size / ts.gap_seconds end as "share",
                       coalesce(m.sample_count, 0) as "sampleCount",
                       round(m.avg_ms)::bigint as "avgMs",
                       round(m.avg_wait_ms)::bigint as "avgWaitMs",
                       m.avg_retry_rate as "avgRetryRate"
                from ripple.type_schedule ts
                full outer join ripple.ripple_type_metric m on m.type_key = ts.type_key
                order by 1
                """);
            return Results.Ok(rows);
        });

        // Live cluster from heartbeats: in-flight per instance + staleness (seconds since last heartbeat).
        // Every worker writes its in-memory executing count on each heartbeat, so this is low-latency truth.
        api.MapGet("/cluster", async (RippleDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var instances = await conn.QueryAsync(
                """
                select instance_id as "instanceId",
                       last_seen_at as "lastSeenAt",
                       executing,
                       round(extract(epoch from (now() - last_seen_at)))::bigint as "ageSeconds"
                from ripple.instance_heartbeat
                order by instance_id
                """);
            return Results.Ok(new { instances });
        });

        api.MapGet("/waves/{id:guid}/report.csv", (Guid id, IReportStore reports) => WaveReportCsv(id, reports));

        // ---- settings: the scheduler's per-type config (the one editable, DB-stored config) -------
        // ripple.type_schedule is the single source of truth for batch/gap/max_attempts. A reserved DEFAULT row
        // holds the fall-back every unconfigured type inherits; a per-type row overrides it. The dashboard can
        // edit the DEFAULT row and add/edit/reset any registered handler's row. The list of configurable
        // type_keys comes from the in-process RippleHandlerRegistry — the worker runtime this dashboard is
        // hosted in — so even a handler with no row yet is offered. (Config edits take effect on the NEXT
        // fan-out: batch/gap are baked into schedule_order when a wave is created, not re-read for in-flight
        // ripples; max_attempts is read live at claim time.)
        api.MapGet("/settings/types", async (RippleDataSource db, RippleHandlerRegistry registry) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var rows = (await conn.QueryAsync<TypeScheduleRow>(
                "select type_key, batch_size, gap_seconds, max_attempts, pause_state from ripple.type_schedule"))
                .ToDictionary(r => r.TypeKey);

            rows.TryGetValue(RippleTypeKey.Default, out var def);
            var seeds = registry.Schedules.ToDictionary(s => s.TypeKey);

            var types = registry.RegisteredTypes
                .Concat(rows.Keys.Where(k => k != RippleTypeKey.Default))
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k =>
                {
                    var hasRow = rows.TryGetValue(k, out var row);
                    var hasSeed = seeds.TryGetValue(k, out var seed);
                    // A row can exist purely to hold pause_state while every value still inherits the DEFAULT,
                    // so "configured" means it carries at least one explicit value — not merely that a row exists.
                    var configured = hasRow &&
                        (row!.BatchSize is not null || row.GapSeconds is not null || row.MaxAttempts is not null);
                    return new
                    {
                        typeKey = k,
                        configured,
                        pauseState = hasRow ? row!.PauseState : "active",
                        batchSize = hasRow ? row!.BatchSize : null,
                        gapSeconds = hasRow ? row!.GapSeconds : null,
                        maxAttempts = hasRow ? row!.MaxAttempts : null,
                        // The hard-coded AddHandler(...) value, shown as the "seeded from code" hint.
                        seededBatchSize = hasSeed ? seed.BatchSize : (int?)null,
                        seededGapSeconds = hasSeed ? seed.GapSeconds : (double?)null,
                        seededMaxAttempts = hasSeed ? seed.MaxAttempts : null
                    };
                });

            return Results.Ok(new
            {
                @default = def is null ? null
                    : new { batchSize = def.BatchSize, gapSeconds = def.GapSeconds, maxAttempts = def.MaxAttempts },
                types
            });
        });

        // Create/overwrite a type's config (or edit the DEFAULT row). max_attempts null ⇒ inherit the default.
        api.MapPut("/settings/types/{typeKey}", async (string typeKey, TypeScheduleUpdate body, IEngineStore engine) =>
        {
            if (body.BatchSize < 1)
                return Results.BadRequest(new { error = "batchSize must be >= 1" });
            if (body.GapSeconds <= 0)
                return Results.BadRequest(new { error = "gapSeconds must be > 0" });
            if (body.MaxAttempts is < 1)
                return Results.BadRequest(new { error = "maxAttempts must be >= 1 (or omitted to inherit the default)" });
            // The DEFAULT row is the floor everything inherits — it must carry a concrete retry ceiling.
            if (typeKey == RippleTypeKey.Default && body.MaxAttempts is null)
                return Results.BadRequest(new { error = "the default row requires an explicit maxAttempts" });

            await engine.UpsertTypeScheduleAsync(typeKey, body.BatchSize, body.GapSeconds, body.MaxAttempts);
            return Results.NoContent();
        });

        // Reset a type to the default: delete its row so it re-inherits the DEFAULT row. The DEFAULT row itself
        // cannot be deleted (it is the floor); edit it via PUT instead.
        api.MapDelete("/settings/types/{typeKey}", async (string typeKey, IEngineStore engine) =>
        {
            if (typeKey == RippleTypeKey.Default)
                return Results.BadRequest(new { error = "the default row cannot be deleted — edit it instead" });
            return await engine.DeleteTypeScheduleAsync(typeKey) switch
            {
                TypeScheduleDeleteResult.Deleted => Results.NoContent(),
                // The row IS the pause state machine — resetting it now would strand the type's parked ripples.
                TypeScheduleDeleteResult.PauseInProgress => Results.BadRequest(new
                {
                    error = "the type is paused or resuming — resume it fully before resetting, "
                            + "otherwise its parked ripples would be stranded"
                }),
                _ => Results.NotFound()
            };
        });

        // Pause a type: park its pending ripples in state='Paused' (out of the claim index) so its work stops
        // being claimed until resumed. In-flight ripples finish; new/retried/recovered work for the type also
        // parks. The DEFAULT row is not a real ripple type_key, so pausing it is meaningless.
        api.MapPost("/settings/types/{typeKey}/pause", async (string typeKey, IEngineStore engine) =>
        {
            if (typeKey == RippleTypeKey.Default)
                return Results.BadRequest(new { error = "the default row cannot be paused" });
            await engine.PauseTypeAsync(typeKey);
            return Results.NoContent();
        });

        // Resume a paused type: un-park its ripples. ?rebase=true (default) re-stamps their schedule_order onto the
        // current frontier so the resumed work interleaves fairly; ?rebase=false resumes "as-is" (the job runs
        // ahead of everything to catch up from its stale slots).
        api.MapPost("/settings/types/{typeKey}/resume", async (string typeKey, bool? rebase, IEngineStore engine) =>
        {
            if (typeKey == RippleTypeKey.Default)
                return Results.BadRequest(new { error = "the default row cannot be resumed" });
            await engine.ResumeTypeAsync(typeKey, rebase ?? true);
            return Results.NoContent();
        });

        // Read-only view of THIS worker's engine options (per-instance, env-tunable — not DB-stored) + retention.
        api.MapGet("/settings/engine", (IOptions<RippleEngineOptions> engineOpts, IOptions<RippleOptions> storeOpts) =>
        {
            var e = engineOpts.Value;
            var s = storeOpts.Value;
            return Results.Ok(new
            {
                instanceId = e.InstanceId,
                maxConcurrency = e.MaxConcurrency,
                prefetchFactor = e.PrefetchFactor,
                claimBatchSize = e.ClaimBatchSize,
                executionTimeoutSeconds = e.ExecutionTimeout.TotalSeconds,
                heartbeatTimeoutSeconds = e.HeartbeatTimeout.TotalSeconds,
                waveStatsRefreshSeconds = e.WaveStatsRefreshInterval.TotalSeconds,
                compactionSeconds = e.CompactionInterval.TotalSeconds,
                reportChunkSize = e.ReportChunkSize,
                defaultRetentionDays = s.DefaultRetention?.TotalDays,
                retentionByWaveType = s.RetentionByWaveType.ToDictionary(kv => kv.Key, kv => kv.Value?.TotalDays)
            });
        });
    }

    // A row of ripple.type_schedule (snake_case → PascalCase via Dapper's underscore matching).
    // batch_size/gap_seconds are nullable: a row can exist purely to hold pause_state while the type still
    // inherits the DEFAULT's scheduling values.
    private sealed record TypeScheduleRow(string TypeKey, int? BatchSize, decimal? GapSeconds, int? MaxAttempts, string PauseState);

    // The PUT body for a settings edit; max_attempts null ⇒ inherit the default row's ceiling.
    public sealed record TypeScheduleUpdate(int BatchSize, double GapSeconds, int? MaxAttempts);

    // The aggregated report CSV for a compacted wave — the ripple outcome data only (no wave-stats preamble;
    // the wave's numbers are on the wave row / UI). 404 if unknown; 202 until it's compacted.
    private static async Task<IResult> WaveReportCsv(Guid id, IReportStore reports)
    {
        if (await reports.GetReportAsync(id) is not { } report)
        {
            return Results.NotFound();
        }

        if (report.CompactedAt is null)
        {
            return Results.Accepted(value: new { status = "report pending", waveStatus = report.Status });
        }

        static string Csv(string? field)
            => field is null ? "" : $"\"{field.Replace("\"", "\"\"")}\"";

        var sb = new System.Text.StringBuilder();
        sb.Append("outcome,output,target_count,target_ids\n");
        foreach (var item in report.Items)
        {
            sb.Append(item.Outcome).Append(',')
                .Append(Csv(item.Output)).Append(',')
                .Append(item.TargetIds.Count).Append(',')
                .Append(Csv(string.Join(';', item.TargetIds))).Append('\n');
        }

        return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"wave-{id}-report.csv");
    }
}
