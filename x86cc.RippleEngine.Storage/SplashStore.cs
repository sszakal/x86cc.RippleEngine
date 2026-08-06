using Dapper;
using Npgsql;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>A successful splash to record: which ripple/wave, the attempt, when it started, and its per-target
/// <c>report</c> (the grouped <c>[{outcome, output, targetIds}]</c> jsonb the handler produced).
/// <see cref="EndedAt"/> is when the HANDLER finished — see <see cref="RippleFailure.EndedAt"/>.</summary>
public readonly record struct RippleCompletion(
    Guid RippleId, Guid WaveId, int Attempt, DateTimeOffset StartedAt, string? Report,
    DateTimeOffset? EndedAt = null);

/// <summary>
/// A failed splash to record. <see cref="Report"/> is the attempt's per-target report — the handler's Failed
/// groups (plus inferred successes), or one all-targets-failed group carrying the exception when it threw.
/// <see cref="Terminal"/> means the attempt was the last allowed, so the ripple is marked Failed; otherwise it
/// returns to Pending, eligible again at <see cref="NextAttemptAt"/>.
/// </summary>
/// <param name="EndedAt">
/// When the handler finished — stamped by the caller at that moment, NOT when this settlement is written. The
/// two are far apart: an outcome waits for its batch to fill and can sit through settlement retry backoff, and
/// <c>duration_ms</c> is documented (and rolled up into <c>wave.avg_duration_ms</c> /
/// <c>ripple_type_metric.avg_ms</c>) as per-attempt EXECUTION time, so measuring it at write time silently
/// inflates every persisted average by the settlement latency. Null falls back to write time.
/// </param>
public readonly record struct RippleFailure(
    Guid RippleId, Guid WaveId, int Attempt, DateTimeOffset StartedAt, string? Report,
    bool Terminal, DateTimeOffset? NextAttemptAt, DateTimeOffset? EndedAt = null);

/// <summary>
/// Settlement — the write side a handler's outcome flows into. Records the splash's audit row (its
/// <c>outcome</c> + per-target <c>report</c>) and flips the ripple's terminal state. Every state-changing write
/// is <b>fenced</b> on <c>claimed_by</c> + <c>state='Running'</c> + <c>attempt</c>, so a wrongly-declared-dead
/// instance's late write no-ops instead of resurrecting a reclaimed ripple, and only real transitions produce a
/// splash. <b>The attempt is load-bearing</b>, not belt-and-braces: owner + state alone do not identify an
/// attempt, because a stalled instance that is reclaimed and then resumes re-registers under the SAME
/// <c>InstanceId</c> and can re-claim the very ripple it stalled on. Its stale attempt-1 outcome would then find
/// <c>claimed_by = me</c> and <c>state = 'Running'</c> both true of attempt 2 and settle it out from under a
/// still-executing handler (whose own settlement then silently no-ops). No counters move here — the wave's live
/// numbers and its completion are recomputed from the truth by <c>refresh_wave_stats()</c>.
/// </summary>
public interface ISplashStore
{
    /// <summary>Settles a batch of successful splashes (flips the ripples to Succeeded, records their splashes).</summary>
    Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId, CancellationToken ct = default);

    /// <summary>Settles a batch of failed splashes — terminal ones fail, the rest requeue with backoff.</summary>
    Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId, CancellationToken ct = default);
}

internal sealed class SplashStore(RippleDataSource dataSource) : ISplashStore
{
    private const string S = M0001_Schema.SchemaName;

    // The fenced RETURNING shape: which ripples actually transitioned, and when they were claimed for this
    // attempt (snapshotted onto the splash — the report needs it and ripples are deleted later).
    // The ATTEMPT comes back too, and the transitioned set is keyed on (id, attempt), not on id: one settlement
    // batch can legitimately carry BOTH a superseded attempt and the live attempt of the same ripple (the stale
    // outcome queued behind a fast second attempt). Keying by id alone let the live attempt's transition vouch
    // for the stale one, writing a splash for an attempt that never settled — inflating retry_count and the
    // compaction rollups with a phantom row, which is exactly what the fence exists to prevent.
    private readonly record struct MovedRipple(Guid Id, int Attempt, DateTimeOffset? ClaimedAt);

    public async Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId, CancellationToken ct = default)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Fenced: only ripples still Running, owned by us, AND on the attempt this outcome came from flip —
        // RETURNING gives the real transitioned set (+ their claim time), so we splash exactly those (a late
        // write for a reclaimed ripple, or for a superseded attempt of one we re-claimed, no-ops). The attempt
        // pairs travel with the ids through an unnest rather than a bare `id = any(...)`, since each row in the
        // batch fences on its own attempt.
        var moved = (await conn.QueryAsync<MovedRipple>(new CommandDefinition(
            $"""
             update {S}.ripple r set state = 'Succeeded', completed_at = now(), claimed_by = null
             from unnest(@ids::uuid[], @attempts::int[]) as u(id, attempt)
             where r.id = u.id and r.claimed_by = @me and r.state = 'Running' and r.attempt = u.attempt
             returning r.id, r.attempt, r.claimed_at
             """,
            new
            {
                ids = batch.Select(c => c.RippleId).ToArray(),
                attempts = batch.Select(c => c.Attempt).ToArray(),
                me = instanceId
            },
            tx, cancellationToken: ct))).ToDictionary(m => (m.Id, m.Attempt), m => m.ClaimedAt);

        var splashes = batch.Where(c => moved.ContainsKey((c.RippleId, c.Attempt))).Select(c => new SplashRow(
            c.RippleId, c.WaveId, c.Attempt, moved[(c.RippleId, c.Attempt)], c.StartedAt, c.EndedAt,
            SplashOutcome.Succeeded, c.Report)).ToList();

        await InsertSplashesAsync(conn, tx, splashes, ct);
        await tx.CommitAsync(ct);
    }

    public async Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId, CancellationToken ct = default)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Keyed on (id, attempt) for the same reason as CompleteRipplesAsync's `moved` — see MovedRipple.
        var claimedAt = new Dictionary<(Guid Id, int Attempt), DateTimeOffset?>();

        // Terminal failures flip to Failed in one set-based update; RETURNING gives the real (fenced) set.
        var terminals = batch.Where(f => f.Terminal).ToList();
        if (terminals.Count > 0)
        {
            foreach (var m in await conn.QueryAsync<MovedRipple>(new CommandDefinition(
                $"""
                 update {S}.ripple r set state = 'Failed', completed_at = now(), claimed_by = null
                 from unnest(@ids::uuid[], @attempts::int[]) as u(id, attempt)
                 where r.id = u.id and r.claimed_by = @me and r.state = 'Running' and r.attempt = u.attempt
                 returning r.id, r.attempt, r.claimed_at
                 """,
                new
                {
                    ids = terminals.Select(f => f.RippleId).ToArray(),
                    attempts = terminals.Select(f => f.Attempt).ToArray(),
                    me = instanceId
                }, tx, cancellationToken: ct)))
            {
                claimedAt[(m.Id, m.Attempt)] = m.ClaimedAt;
            }
        }

        // Requeues flip back to Pending, each with its own backoff deadline — one set-based update over
        // (id, next_attempt_at) pairs. schedule_order is untouched, so a retried ripple keeps its queue position.
        // If the ripple's type has since been paused, it requeues into 'Paused' instead (StateExpr), so a retry
        // can't sneak paused work back into the claim — it waits for resume like the rest of its type.
        var retries = batch.Where(f => !f.Terminal).ToList();
        if (retries.Count > 0)
        {
            foreach (var m in await conn.QueryAsync<MovedRipple>(new CommandDefinition(
                $"""
                 update {S}.ripple r set state = {ScheduleOrderSql.StateExpr("r.type_key")}, claimed_by = null, next_attempt_at = u.next
                 from unnest(@ids::uuid[], @nexts::timestamptz[], @attempts::int[]) as u(id, next, attempt)
                 where r.id = u.id and r.claimed_by = @me and r.state = 'Running' and r.attempt = u.attempt
                 returning r.id, r.attempt, r.claimed_at
                 """,
                new
                {
                    ids = retries.Select(f => f.RippleId).ToArray(),
                    nexts = retries.Select(f => f.NextAttemptAt).ToArray(),
                    attempts = retries.Select(f => f.Attempt).ToArray(),
                    me = instanceId
                }, tx, cancellationToken: ct)))
            {
                claimedAt[(m.Id, m.Attempt)] = m.ClaimedAt;
            }
        }

        // Record a failed splash for every ripple that actually transitioned (terminal or requeued).
        var splashes = batch
            .Where(f => claimedAt.ContainsKey((f.RippleId, f.Attempt)))
            .Select(f => new SplashRow(f.RippleId, f.WaveId, f.Attempt, claimedAt[(f.RippleId, f.Attempt)],
                f.StartedAt, f.EndedAt, SplashOutcome.Failed, f.Report))
            .ToList();

        await InsertSplashesAsync(conn, tx, splashes, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>One splash audit row to write (accumulated across a settlement batch, flushed in one insert).</summary>
    private readonly record struct SplashRow(
        Guid RippleId, Guid WaveId, int Attempt, DateTimeOffset? ClaimedAt, DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt, SplashOutcome Outcome, string? Report);

    // One round trip for the whole settlement batch: unnest the parallel column arrays into a set the INSERT
    // reads from, instead of a statement per ripple.
    private static Task InsertSplashesAsync(NpgsqlConnection conn, System.Data.Common.DbTransaction tx,
        IReadOnlyList<SplashRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Per-row end time, carried from the moment the handler actually finished — NOT sampled here. This
        // method runs after the outcome has queued for a full batch and through any settlement retry backoff
        // (250ms doubling to 10s), so a `now` taken here would fold that latency into every duration_ms and,
        // through compaction, into the avg_ms/avg_duration_ms averages documented as execution time. Falls back
        // to write time only for callers that don't stamp it.
        var writtenAt = DateTimeOffset.UtcNow;
        return conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.splash (id, ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report)
             select gen_random_uuid(), u.ripple_id, u.wave_id, u.attempt, u.claimed_at, u.started_at, u.ended_at,
                    u.outcome, u.duration_ms, u.report::jsonb
             from unnest(@rippleIds::uuid[], @waveIds::uuid[], @attempts::int[], @claimedAts::timestamptz[],
                         @startedAts::timestamptz[], @endedAts::timestamptz[], @outcomes::text[],
                         @durations::bigint[], @reports::text[])
                  as u(ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report)
             """,
            new
            {
                rippleIds = rows.Select(r => r.RippleId).ToArray(),
                waveIds = rows.Select(r => r.WaveId).ToArray(),
                attempts = rows.Select(r => r.Attempt).ToArray(),
                claimedAts = rows.Select(r => r.ClaimedAt).ToArray(),
                startedAts = rows.Select(r => r.StartedAt).ToArray(),
                endedAts = rows.Select(r => r.EndedAt ?? writtenAt).ToArray(),
                outcomes = rows.Select(r => r.Outcome.ToString()).ToArray(),
                // Clamped at 0: StartedAt is host-clock and EndedAt is stamped on the same host, so this is
                // monotonic in practice — but a negative duration would poison the EWMAs, so never emit one.
                durations = rows.Select(r =>
                    Math.Max(0L, (long)((r.EndedAt ?? writtenAt) - r.StartedAt).TotalMilliseconds)).ToArray(),
                reports = rows.Select(r => r.Report).ToArray()
            }, tx, cancellationToken: ct));
    }
}
