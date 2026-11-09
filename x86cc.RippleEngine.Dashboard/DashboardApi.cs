using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Dashboard;

/// <summary>
/// The dashboard API (Wave / Ripple / Splash vocabulary), hosted by every worker: the engine instances are the
/// symmetric, always-on cluster, and this surface is mostly read-only projections over the same <c>ripple</c>
/// schema they already poll — plus the operator writes: per-type config (<c>PUT</c>/<c>DELETE
/// /settings/types/{typeKey}</c>), pause/resume, and a live wave's reschedule. These write endpoints are
/// unauthenticated, so map them behind auth anywhere the dashboard is reachable by more than operators. camelCase
/// aliases so the JSON matches the Angular SPA models. The
/// SPA's own static files are served by <see cref="RippleDashboardSpa"/> from the same origin, so no proxy is
/// needed in production.
/// </summary>
/// <remarks>
/// Mapped for you when <c>RippleSetupOptions.EnableDashboard</c> is set; call it directly to place the
/// endpoints yourself (a different prefix, behind auth, on one host of a cluster, …).
/// </remarks>
public static class DashboardApi
{
    /// <summary>Maps the dashboard API (read projections + operator writes) under <c>/api</c>. Requires the Ripple storage services
    /// (<see cref="IDashboardQueries"/>, <see cref="IEngineStore"/>, <see cref="IReportStore"/>); the
    /// <c>/api/settings/types</c> route additionally reads this process's <see cref="RippleHandlerRegistry"/>,
    /// so a host that runs no engine simply lists the types that already have a <c>type_schedule</c> row.</summary>
    public static IEndpointRouteBuilder MapRippleDashboard(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // Wave list + summary, with optional status / text / created-at-range filters. `succeeded` is derived
        // (never stored); a not-yet-refreshed wave reports all its ripples as pending. `q` is a literal substring.
        api.MapGet("/waves", async (string? status, string? q, DateTimeOffset? from, DateTimeOffset? to,
            int? limit, IDashboardQueries queries) =>
        {
            var page = await queries.ListWavesAsync(
                new WaveListFilter(status, q, from, to, Math.Clamp(limit ?? 50, 1, 500)));
            return Results.Ok(new { waves = page.Waves, stats = page.Stats });
        });

        // Per-day wave counts for the contribution heatmap (grouped by created_at day, UTC).
        api.MapGet("/waves/activity", async (int? days, IDashboardQueries queries) =>
            Results.Ok(new { days = await queries.GetWaveActivityAsync(Math.Clamp(days ?? 365, 1, 366)) }));

        // Wave counts bucketed by a time granularity over a [from, to) range — powers the adaptive tile-zoom
        // (day → hour → minute → second). Returns only NON-EMPTY buckets, colored by outcome like the heatmap.
        api.MapGet("/waves/histogram", async (DateTimeOffset from, DateTimeOffset to, string bucket, string? tz,
            IDashboardQueries queries) =>
        {
            // date_trunc's unit is bound safely (its first arg is text), but whitelist it anyway.
            if (bucket is not ("year" or "day" or "hour" or "minute" or "second"))
            {
                return Results.BadRequest(new { error = "bucket must be one of: year, day, hour, minute, second" });
            }
            // Bucket in the CALLER's timezone. The SPA colors/keys each calendar cell by the browser-local day
            // and drills in on browser-local midnight→midnight windows, so the server must group on the same
            // day boundaries or a green cell can drill into zero waves (its burst falls in the DB-tz day but
            // outside the browser-tz window). Defaults to UTC when the caller sends no zone.
            var zone = string.IsNullOrWhiteSpace(tz) ? "UTC" : tz;
            return Results.Ok(new { buckets = await queries.GetWaveHistogramAsync(from, to, bucket, zone) });
        });

        // One wave's detail (enriched with the compaction-computed avg execution time).
        api.MapGet("/waves/{id:guid}", async (Guid id, IDashboardQueries queries) =>
            await queries.GetWaveAsync(id) is { } wave ? Results.Ok(wave) : Results.NotFound());

        // Per-type metrics: scheduling share (batch/gap) + the surviving count-weighted EWMA of execution time
        // from ripple_type_metric (avg_ms is the moving average; sample_count is the lifetime "(n)" label).
        api.MapGet("/metrics/types", async (IDashboardQueries queries) =>
            Results.Ok(await queries.GetTypeMetricsAsync()));

        // Live cluster from heartbeats: in-flight per instance + staleness (seconds since last heartbeat).
        // Every worker writes its in-memory executing count on each heartbeat, so this is low-latency truth.
        api.MapGet("/cluster", async (IDashboardQueries queries) =>
            Results.Ok(new { instances = await queries.GetClusterAsync() }));

        api.MapGet("/waves/{id:guid}/report.csv", (Guid id, IReportStore reports) => WaveReportCsv(id, reports));

        // Re-stamp a LIVE wave's remaining pending ripples with a new batch/gap — the only wave-scoped write in
        // this API (every other one is type_schedule-scoped). It exists because batch/gap are baked into
        // schedule_order at fan-out: a settings edit changes the NEXT wave, never the 10M-ripple one already
        // starving the cluster. The re-stamp is anchored at the wave's own head, so the wave holds its place in
        // the global queue and only its share changes — raising the gap slows it without displacing other jobs.
        api.MapPost("/waves/{id:guid}/reschedule", async (Guid id, WaveReschedule body, IEngineStore engine) =>
        {
            // Same floors as the settings PUT — batch/gap mean the same thing here, they are just applied to
            // rows that already exist rather than to the ones the next fan-out will create.
            if (body.BatchSize < 1)
                return Results.BadRequest(new { error = "batchSize must be >= 1" });
            if (body.GapSeconds <= 0)
                return Results.BadRequest(new { error = "gapSeconds must be > 0" });

            if (await engine.GetWaveAsync(id) is null)
                return Results.NotFound();

            // null ⇒ another reschedule holds the advisory lock. A 409 rather than a silent 0, so "someone else
            // is doing this right now" is never mistaken for "your change applied to nothing".
            if (await engine.RescheduleWaveAsync(id, body.BatchSize, body.GapSeconds, body.TypeKey) is not { } n)
                return Results.Conflict(new { error = "another reschedule is in progress — retry in a moment" });

            return Results.Ok(new
            {
                rescheduled = n,
                batchSize = body.BatchSize,
                gapSeconds = body.GapSeconds,
                share = body.BatchSize / body.GapSeconds
            });
        });

        // ---- settings: the scheduler's per-type config (the one editable, DB-stored config) -------
        // ripple.type_schedule is the single source of truth for batch/gap/max_attempts. A reserved DEFAULT row
        // holds the fall-back every unconfigured type inherits; a per-type row overrides it. The dashboard can
        // edit the DEFAULT row and add/edit/reset any registered handler's row. The list of configurable
        // type_keys comes from the in-process RippleHandlerRegistry — the worker runtime this dashboard is
        // hosted in — so even a handler with no row yet is offered. (Config edits take effect on the NEXT
        // fan-out: batch/gap are baked into schedule_order when a wave is created, not re-read for in-flight
        // ripples; max_attempts is read live at claim time.)
        api.MapGet("/settings/types", async (IDashboardQueries queries, RippleHandlerRegistry registry) =>
        {
            var rows = (await queries.GetTypeSchedulesAsync()).ToDictionary(r => r.TypeKey);

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

        return endpoints;
    }

    // The PUT body for a settings edit; max_attempts null ⇒ inherit the default row's ceiling.
    public sealed record TypeScheduleUpdate(int BatchSize, double GapSeconds, int? MaxAttempts);

    // The POST body for an in-flight wave reschedule; type_key null ⇒ every type the wave still has pending
    // (each re-stamped independently, anchored at its own head).
    public sealed record WaveReschedule(int BatchSize, double GapSeconds, string? TypeKey);

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
