using System.Text.Json;
using Dapper;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>The seed of a ripple: its self-contained JSON payload and the payload's CLR type name.</summary>
public readonly record struct RippleSeed(string Payload, string? PayloadType);

/// <summary>
/// A ripple claimed for execution, joined with its wave's shared payload (loaded alongside so the handler
/// gets both the per-ripple target and the wave-wide "event" without a second round trip).
/// </summary>
public readonly record struct ClaimedRipple(
    Guid Id,
    Guid WaveId,
    int Attempt,
    int MaxAttempts,
    string TypeKey,
    string? PayloadType,
    JsonDocument Payload,
    string? WavePayloadType,
    JsonDocument? WavePayload);

/// <summary>
/// The engine's coordination surface: creating waves and their ripples, the global FIFO claim, cluster
/// heartbeats, and crash recovery. Everything a running instance needs to <b>acquire</b> work and stay a
/// member of the cluster. Settling a splash's outcome lives on the separate <see cref="ISplashStore"/>.
/// </summary>
public interface IEngineStore
{
    // ---- waves ----------------------------------------------------------------------------------

    /// <summary>
    /// Atomically creates a wave <b>with its initial ripples</b> in one transaction — the sanctioned way a root
    /// wave is born (via a generator's <c>Create</c>). A create whose seed set is <b>empty</b> is persisted
    /// already <see cref="WaveStatus.Completed"/> (<c>ripple_count = 0</c>, <c>completed_at</c> set) — a job with
    /// nothing to do, not a zero-ripple Active wave that could never drain. A non-empty create is
    /// <see cref="WaveStatus.Active"/> with its ripples claimable at once.
    /// </summary>
    Task<Wave> CreateWaveWithRipplesAsync(Wave wave, IReadOnlyList<RippleSeed> seeds, CancellationToken ct = default);

    /// <summary>
    /// Low-level plumbing: inserts a bare wave row (Active, <c>ripple_count 0</c>). Prefer
    /// <see cref="CreateWaveWithRipplesAsync"/> (via a generator's <c>Create</c>) — the sanctioned, atomic root
    /// create — since a bare wave with no ripples added is a zombie that never completes.
    /// </summary>
    Task<Wave> CreateWaveAsync(Wave wave, CancellationToken ct = default);

    /// <summary>
    /// Bulk-inserts pending ripples for a wave and bumps its <c>ripple_count</c>. The sanctioned use is
    /// <b>in-flight expansion</b> — a handler spawning child ripples through a generator's <c>Continue(context)</c>,
    /// where <paramref name="parentRippleId"/> is the spawning (<c>Running</c>) ripple, stamped onto each child's
    /// <c>parent_ripple_id</c> (audit lineage). Because that parent is still <c>Running</c>, the wave can't be
    /// observed drained mid-expansion. The new ripples get their <c>schedule_order</c> from the same base-clamp
    /// rule as the fan-out; their retry ceiling is the per-type <c>max_attempts</c> resolved at claim time. Adding
    /// to a wave with no in-flight ripple (out of band) is a contract violation — completion/compaction assume it
    /// never happens.
    /// </summary>
    Task AddRipplesAsync(Guid waveId, IReadOnlyList<RippleSeed> ripples,
        Guid? parentRippleId = null, CancellationToken ct = default);

    Task<Wave?> GetWaveAsync(Guid id, CancellationToken ct = default);

    // ---- type config ----------------------------------------------------------------------------

    /// <summary>
    /// <b>Upserts</b> (overwrite) a <c>(wave|ripple)</c> type's config: its <paramref name="batchSize"/>
    /// (ripples per <c>schedule_order</c> slot), <paramref name="gapSeconds"/> (spacing between a job's
    /// batches), and optional <paramref name="maxAttempts"/> (the retry ceiling; null falls back to the DEFAULT
    /// row, resolved at claim time). The dashboard's edit path — including editing the DEFAULT row
    /// (<see cref="RippleTypeKey.Default"/>) itself. The fan-out reads batch/gap live to stamp
    /// <c>schedule_order</c> and the claim reads <c>max_attempts</c>; a type with no row inherits the DEFAULT row.
    /// </summary>
    Task UpsertTypeScheduleAsync(string typeKey, int batchSize, double gapSeconds, int? maxAttempts = null,
        CancellationToken ct = default);

    /// <summary>
    /// <b>Insert-if-absent</b> seed of a type's config (used by the startup schedule seeder). Unlike
    /// <see cref="UpsertTypeScheduleAsync"/> it never overwrites an existing row, so a value a user changed from
    /// the dashboard is the source of truth and survives a restart re-seed.
    /// </summary>
    Task SeedTypeScheduleAsync(string typeKey, int batchSize, double gapSeconds, int? maxAttempts = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a type's config row so it reverts to inheriting the DEFAULT row (the dashboard's "reset" action).
    /// The reserved DEFAULT row itself (<see cref="RippleTypeKey.Default"/>) is never deleted — it is the floor
    /// everything else falls back to. A type that is paused or mid-resume is <b>refused</b>
    /// (<see cref="TypeScheduleDeleteResult.PauseInProgress"/>): its row carries the pause state machine, and
    /// deleting it would strand any <c>'Paused'</c> ripples permanently.
    /// </summary>
    Task<TypeScheduleDeleteResult> DeleteTypeScheduleAsync(string typeKey, CancellationToken ct = default);

    /// <summary>
    /// <b>Pauses</b> a <c>(wave|ripple)</c> type — an O(1) flip of its <c>type_schedule.pause_state</c> to
    /// <c>'paused'</c> (materialising a row that inherits the DEFAULT batch/gap if the type had none). Correctness
    /// is immediate: the claim skips the type at once (the poll backstop) and every writer of <c>'Pending'</c>
    /// (fan-out/retry/recovery) parks new work as <c>'Paused'</c>. The type's residual <c>Pending</c> ripples are
    /// flipped to <c>'Paused'</c> asynchronously, in bounded chunks, by <see cref="ReconcilePauseTransitionsAsync"/>
    /// — so pausing millions is never one giant transaction. In-flight (<c>Running</c>) ripples are never paused.
    /// </summary>
    Task PauseTypeAsync(string typeKey, CancellationToken ct = default);

    /// <summary>
    /// <b>Resumes</b> a paused type — an O(1) flip of its <c>pause_state</c> to a drain state
    /// (<c>resuming_rebase</c> / <c>resuming_asis</c>). The type's <c>'Paused'</c> ripples are flipped back to
    /// <c>'Pending'</c> asynchronously, in bounded chunks, by <see cref="ReconcilePauseTransitionsAsync"/>, which
    /// clears the state to <c>'active'</c> once drained. When <paramref name="rebase"/> is true (the safe default)
    /// each chunk re-stamps <c>schedule_order</c> onto the current global frontier so the resumed work interleaves
    /// fairly (the gradual, chunked re-admit is itself a defense against the "catch-up" herd); when false
    /// ("resume as-is") <c>schedule_order</c> is left untouched and the job runs ahead of everything.
    /// </summary>
    Task ResumeTypeAsync(string typeKey, bool rebase = true, CancellationToken ct = default);

    /// <summary>
    /// The background reconcile that moves ripples toward each type's desired <c>pause_state</c> in bounded chunks
    /// (parking <c>Pending → Paused</c> for paused types, un-parking <c>Paused → Pending</c> — rebasing when asked —
    /// for resuming types, then flipping a drained resuming type to <c>'active'</c>). Advisory-lock-gated so at most
    /// one instance works at a time; the rest are cheap no-ops. Driven by <c>PauseTransitionLoop</c>.
    /// <paramref name="chunkSize"/> bounds each UPDATE's transaction; <paramref name="maxRowsPerPass"/> bounds the
    /// total ripples moved per call. Returns the number of ripples moved.
    /// </summary>
    Task<int> ReconcilePauseTransitionsAsync(int chunkSize, int maxRowsPerPass, CancellationToken ct = default);

    // ---- poll (heartbeat + claim, one round trip) -----------------------------------------------

    /// <summary>
    /// The single read-side round trip. In one statement it (1) upserts this instance's heartbeat with the
    /// live <paramref name="executing"/> count — so <b>polling is the liveness proof</b>, and the beat
    /// happens even when <paramref name="limit"/> is 0 — and (2) atomically claims up to
    /// <paramref name="limit"/> pending, retry-eligible ripples across <b>all</b> waves, lowest
    /// <c>schedule_order</c> first (the precomputed batch-interleaved order): marks them Running, stamps the
    /// owner, and bumps their attempt. Uses <c>FOR UPDATE SKIP LOCKED</c>, so concurrent instances get
    /// disjoint slices. Each returned ripple carries its wave's shared payload (joined in the same query). No
    /// counters move on the claim — the wave's live numbers are recomputed by <see cref="TryRefreshWaveStatsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ClaimedRipple>> PollAsync(int limit, string instanceId, int executing,
        CancellationToken ct = default);

    // ---- stats ----------------------------------------------------------------------------------

    /// <summary>
    /// Recomputes each active wave's live numbers (pending/running/failed, onto the wave row) from the truth
    /// and settles any wave that has drained — the DB-side <c>refresh_wave_stats()</c>. Gated by a Postgres advisory lock so only
    /// one instance refreshes at a time; returns <c>false</c> (a no-op) when another instance holds the lock.
    /// </summary>
    Task<bool> TryRefreshWaveStatsAsync(CancellationToken ct = default);

    // ---- heartbeat ------------------------------------------------------------------------------

    /// <summary>
    /// Upserts this instance's heartbeat directly. The hot path beats via <see cref="PollAsync"/>; this is
    /// the fallback the dispatcher uses when a poll throws, so a healthy node isn't declared dead.
    /// </summary>
    Task BeatAsync(string instanceId, int executing, CancellationToken ct = default);

    /// <summary>All current members with their last-seen time and executing count (dashboard).</summary>
    Task<IReadOnlyList<InstanceHeartbeat>> GetHeartbeatsAsync(CancellationToken ct = default);

    /// <summary>Removes this instance's row on graceful shutdown.</summary>
    /// <summary>
    /// Deregisters an instance on graceful shutdown so peers don't wait out <c>HeartbeatTimeout</c> to reclaim
    /// nothing. <b>Refuses</b> (returns false, leaving the row) while the instance still owns any
    /// <c>Running</c> ripple — that row is recovery's only handle on the work, so removing it would strand
    /// those ripples permanently.
    /// </summary>
    /// <returns>True if the heartbeat was removed; false if work was left behind and the row was kept.</returns>
    Task<bool> RemoveInstanceAsync(string instanceId, CancellationToken ct = default);

    // ---- recovery -------------------------------------------------------------------------------

    /// <summary>
    /// In one statement: finds instances whose last beat is older than <paramref name="threshold"/> (dead,
    /// excluding <paramref name="selfInstanceId"/>) and reclaims their in-flight ripples. Each reclaimed
    /// ripple gets an <c>Abandoned</c> splash row recording the interrupted attempt (so a ripple with no
    /// outcome data is explained, not a mystery); one that has now exhausted its attempts is failed
    /// terminally (poison — its owner died mid-run every time), the rest return to <c>Pending</c> (keeping
    /// their original <c>schedule_order</c>, so they re-claim at their old queue position). Dead heartbeat rows
    /// are pruned; the wave's live numbers self-heal on the next <see cref="TryRefreshWaveStatsAsync"/>. Almost
    /// free when healthy (stale set empty). Idempotent — fenced on <c>state='Running'</c>, so concurrent
    /// survivors are harmless. Returns the number of ripples reclaimed.
    /// </summary>
    Task<int> RecoverStaleAsync(TimeSpan threshold, string selfInstanceId, CancellationToken ct = default);

    /// <summary>
    /// Reclaims THIS instance's own <b>stranded</b> claims: ripples the DB shows <c>Running</c> and claimed by
    /// us, claimed longer ago than <paramref name="grace"/>, whose id is NOT in <paramref name="keepIds"/> (the
    /// set the execute block is actually running). Such a row was claimed but never handed to a handler (or was
    /// stranded by a fault/race); since we're still alive, dead-instance recovery can't rescue it — only we know
    /// we aren't running it. Requeues to <c>Pending</c> (keeping <c>schedule_order</c>) or fails terminally if
    /// poison, with an <c>Abandoned</c> splash. Pass an empty <paramref name="keepIds"/> and zero grace to
    /// release <b>all</b> our claims (e.g. after the execute block faults). Returns the number reclaimed.
    /// </summary>
    Task<int> RecoverSelfStrandedAsync(string selfInstanceId, IReadOnlyCollection<Guid> keepIds, TimeSpan grace,
        CancellationToken ct = default);
}

internal sealed class EngineStore(RippleDataSource dataSource) : IEngineStore
{
    private const string S = M0001_Schema.SchemaName;

    // Arbitrary fixed key so at most one instance runs the (idempotent) stats refresh at a time.
    private const long StatsRefreshLockKey = 0x7269_7070_6C65_02L; // "ripple\x02"

    // Distinct key so at most one instance runs the pause/resume reconcile at a time (ReportStore uses \x03).
    private const long PauseReconcileLockKey = 0x7269_7070_6C65_04L; // "ripple\x04"

    // A type_key and its desired pause_state — the resuming set the reconcile drains.
    private readonly record struct TypeState(string TypeKey, string PauseState);

    // The shared wave projection — see WaveSql for why the derived columns live in one place.
    private static readonly string WaveSelect = WaveSql.ById();

    // ---- waves ----------------------------------------------------------------------------------

    public async Task<Wave> CreateWaveAsync(Wave wave, CancellationToken ct = default)
    {
        wave.Id = wave.Id == Guid.Empty ? Guid.NewGuid() : wave.Id;
        wave.CreatedAt = wave.CreatedAt == default ? DateTimeOffset.UtcNow : wave.CreatedAt;
        wave.Status = WaveStatus.Active;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.wave (id, name, type, payload, payload_type, status, ripple_count, created_at)
             values (@Id, @Name, @Type, @Payload, @PayloadType, 'Active', 0, @CreatedAt)
             """,
            wave, cancellationToken: ct));
        return wave;
    }

    public async Task AddRipplesAsync(Guid waveId, IReadOnlyList<RippleSeed> ripples,
        Guid? parentRippleId = null, CancellationToken ct = default)
    {
        if (ripples.Count == 0)
        {
            return;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // The wave's payload type forms the wave half of each ripple's composite type_key
        // ("{waveType}|{rippleType}"), which drives both scheduling config and handler resolution.
        // `exists` is read separately because payload_type is itself nullable — without it a MISSING wave is
        // indistinguishable from a wave with a null payload type, and the insert would happily commit orphans:
        // there is no FK on ripple.wave_id, the type_key would degrade to "|rippleType", and the trailing
        // `update wave set ripple_count` would match zero rows, so nothing anywhere would error. Those orphans
        // are not inert — PollSql inner-joins wave, so the claim flips them to Running and burns an attempt
        // each pass while returning nothing, cycling claim → stranded-recovery → claim until the retry ceiling
        // poison-fails them, occupying claim slots the whole time.
        // Row COUNT is the existence test, not the value: payload_type is itself nullable, so a scalar read
        // can't tell "no wave" from "wave with a null payload type".
        var waveRows = (await conn.QueryAsync<string?>(new CommandDefinition(
            $"select payload_type from {S}.wave where id = @waveId", new { waveId }, tx, cancellationToken: ct)))
            .AsList();
        if (waveRows.Count == 0)
        {
            throw new InvalidOperationException(
                $"Wave {waveId} was not found; refusing to insert ripples that no wave owns.");
        }

        var wavePayloadType = waveRows[0];

        // Parallel column arrays fed to a single set-based INSERT. `ord` preserves caller order so the batch
        // numbering below is deterministic.
        var ids = new Guid[ripples.Count];
        var payloads = new string[ripples.Count];
        var payloadTypes = new string?[ripples.Count];
        var typeKeys = new string[ripples.Count];
        var ords = new long[ripples.Count];
        for (var i = 0; i < ripples.Count; i++)
        {
            ids[i] = Guid.NewGuid();
            payloads[i] = ripples[i].Payload;
            payloadTypes[i] = ripples[i].PayloadType;
            typeKeys[i] = RippleTypeKey.Compose(wavePayloadType, ripples[i].PayloadType);
            ords[i] = i;
        }

        // Stamp schedule_order (the pure ordering key) entirely in the DB, then bump the wave's ripple_count.
        // The set-based ripple insert (base-clamp + per-type batch/gap + paused-aware state) lives in the shared
        // RippleInsertSql so this same fan-out is reused by the atomic CreateWaveWithRipplesAsync; here we append
        // the count bump because the wave already exists.
        await conn.ExecuteAsync(new CommandDefinition(
            RippleInsertSql + $"\nupdate {S}.wave set ripple_count = ripple_count + @n where id = @waveId;",
            new
            {
                ids, payloads, payloadTypes, typeKeys, ords, waveId, parentRippleId,
                n = (long)ripples.Count
            },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    // The set-based ripple fan-out INSERT (no wave-row write), shared by AddRipplesAsync (which appends a
    // ripple_count bump on the existing wave) and CreateWaveWithRipplesAsync (which inserts the wave row in the
    // same transaction). Stamps schedule_order entirely DB-side — the base-clamp and per-type batch/gap lookup
    // live in ScheduleOrderSql (shared with the INSERT…SELECT fan-out, WaveInsertSql), and new work for a paused
    // type lands directly in state='Paused' (StateExpr) so it never enters the claim. static readonly, not const:
    // it interpolates ScheduleOrderSql expressions. `base`/`now()` are DB-clock so a lagging host can't jump the queue.
    private static readonly string RippleInsertSql = $"""
        with input as (
            select * from unnest(@ids::uuid[], @payloads::text[], @payloadTypes::text[],
                                 @typeKeys::text[], @ords::bigint[])
                as u(id, payload, payload_type, type_key, ord)
        ),
        -- One base PER TYPE, not one for the whole call: base is scoped to type_key (the scheduling unit), and
        -- a single call can mix types. Computed over the DISTINCT types so the correlated lookup runs once per
        -- type rather than once per row — this insert carries the whole batch.
        base as (
            select t.type_key, {ScheduleOrderSql.BaseExpr("@waveId", "t.type_key")} as b
            from (select distinct type_key from input) t
        ),
        seq as (
            select i.*, (row_number() over (partition by i.type_key order by i.ord) - 1) as k
            from input i
        ),
        cfg as (
            select seq.*,
                   {ScheduleOrderSql.TypeConfigExpr("batch_size", "seq.type_key")}  as bs,
                   {ScheduleOrderSql.TypeConfigExpr("gap_seconds", "seq.type_key")} as gp
            from seq
        )
        insert into {S}.ripple (id, wave_id, parent_ripple_id, ripple_index, payload, payload_type, type_key,
            state, attempt, created_at, schedule_order)
        select cfg.id, @waveId, @parentRippleId::uuid, cfg.ord, cfg.payload::jsonb, cfg.payload_type,
               cfg.type_key, {ScheduleOrderSql.StateExpr("cfg.type_key")}, 0, now(),
               base.b + (cfg.k / cfg.bs) * cfg.gp
        from cfg join base on base.type_key = cfg.type_key;
        """;

    public async Task<Wave> CreateWaveWithRipplesAsync(Wave wave, IReadOnlyList<RippleSeed> seeds,
        CancellationToken ct = default)
    {
        wave.Id = wave.Id == Guid.Empty ? Guid.NewGuid() : wave.Id;
        wave.CreatedAt = wave.CreatedAt == default ? DateTimeOffset.UtcNow : wave.CreatedAt;
        var count = seeds.Count;
        wave.RippleCount = count;
        wave.Pending = count;
        // Born-complete: a create with zero targets is a job with nothing to do — persist it already Completed
        // (audit record, flows through compaction/retention) rather than a zero-ripple Active wave that, because
        // refresh_wave_stats() requires ripple_count > 0, would never drain.
        wave.Status = count > 0 ? WaveStatus.Active : WaveStatus.Completed;
        wave.CompletedAt = count > 0 ? null : wave.CreatedAt;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // One transaction: the wave row (ripple_count set directly — no later bump) + its ripples. Atomic, so a
        // fault can never strand a wave without its ripples.
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.wave (id, name, type, payload, payload_type, status, ripple_count, created_at, completed_at)
             values (@Id, @Name, @Type, @Payload, @PayloadType, @Status, @RippleCount, @CreatedAt, @CompletedAt)
             """,
            new
            {
                wave.Id, wave.Name, wave.Type, wave.Payload, wave.PayloadType,
                Status = wave.Status.ToString(), wave.RippleCount, wave.CreatedAt, wave.CompletedAt
            },
            tx, cancellationToken: ct));

        if (count > 0)
        {
            var ids = new Guid[count];
            var payloads = new string[count];
            var payloadTypes = new string?[count];
            var typeKeys = new string[count];
            var ords = new long[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = Guid.NewGuid();
                payloads[i] = seeds[i].Payload;
                payloadTypes[i] = seeds[i].PayloadType;
                // The wave's payload type forms the wave half of each composite type_key — known here directly.
                typeKeys[i] = RippleTypeKey.Compose(wave.PayloadType, seeds[i].PayloadType);
                ords[i] = i;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                RippleInsertSql,
                new { ids, payloads, payloadTypes, typeKeys, ords, waveId = wave.Id, parentRippleId = (Guid?)null },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return wave;
    }

    public async Task<Wave?> GetWaveAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Wave>(new CommandDefinition(
            WaveSelect, new { id }, cancellationToken: ct));
    }

    // ---- type config ----------------------------------------------------------------------------

    public async Task UpsertTypeScheduleAsync(string typeKey, int batchSize, double gapSeconds,
        int? maxAttempts = null, CancellationToken ct = default)
    {
        // Refuse a config that can't produce a usable schedule (see TypeScheduleGuard) — this is the chokepoint
        // every caller reaches the table through, so a poison row can't be written from anywhere.
        TypeScheduleGuard.Validate(batchSize, gapSeconds, maxAttempts);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.type_schedule (type_key, batch_size, gap_seconds, max_attempts)
             values (@typeKey, @batchSize, @gapSeconds, @maxAttempts)
             on conflict (type_key) do update set batch_size = @batchSize, gap_seconds = @gapSeconds,
                                                  max_attempts = @maxAttempts
             """,
            new { typeKey, batchSize, gapSeconds, maxAttempts }, cancellationToken: ct));
    }

    public async Task SeedTypeScheduleAsync(string typeKey, int batchSize, double gapSeconds,
        int? maxAttempts = null, CancellationToken ct = default)
    {
        TypeScheduleGuard.Validate(batchSize, gapSeconds, maxAttempts);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Insert-if-absent: an existing row (e.g. one the dashboard changed) is left untouched.
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.type_schedule (type_key, batch_size, gap_seconds, max_attempts)
             values (@typeKey, @batchSize, @gapSeconds, @maxAttempts)
             on conflict (type_key) do nothing
             """,
            new { typeKey, batchSize, gapSeconds, maxAttempts }, cancellationToken: ct));
    }

    public async Task<TypeScheduleDeleteResult> DeleteTypeScheduleAsync(string typeKey, CancellationToken ct = default)
    {
        // The DEFAULT row is the floor every other type inherits — never let a reset delete it.
        if (typeKey == RippleTypeKey.Default)
        {
            return TypeScheduleDeleteResult.NotFound;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Refuse while the type is mid-pause-transition. The row IS the pause state machine: delete it while
        // ripples sit in 'Paused' and nothing can ever un-park them — the reconcile only visits types whose
        // pause_state is paused/resuming_*, and the claim only ever sees 'Pending'. Those ripples become
        // permanently unclaimable, their wave never drains (refresh_wave_stats keeps counting them in `paused`)
        // so it never completes and never compacts, and nothing logs a thing. Only an 'active' type is safe to
        // reset, because an active type has no parked rows by definition.
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            $"delete from {S}.type_schedule where type_key = @typeKey and pause_state = 'active'",
            new { typeKey }, cancellationToken: ct));
        if (deleted > 0)
        {
            return TypeScheduleDeleteResult.Deleted;
        }

        // Nothing deleted: either there was no row, or the guard above refused it. Distinguish the two so the
        // caller can say WHY rather than reporting a bare "not found" for a paused type.
        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            $"select 1 from {S}.type_schedule where type_key = @typeKey",
            new { typeKey }, cancellationToken: ct)) is not null;
        return exists ? TypeScheduleDeleteResult.PauseInProgress : TypeScheduleDeleteResult.NotFound;
    }

    // ---- pause / resume -------------------------------------------------------------------------
    //
    // Pause/resume are O(1): they only flip the type's pause_state (the DESIRED state). The (potentially
    // millions of) ripples are moved between 'Pending' and 'Paused' asynchronously, in bounded chunks, by
    // ReconcilePauseTransitionsAsync (driven by PauseTransitionLoop) — never one giant transaction. Correctness
    // is immediate regardless: the instant pause_state='paused' the claim skips the type (PollSql backstop) and
    // every 'Pending'-writer parks new work (StateExpr); the reconcile just cleans the residual out of the index.

    public async Task PauseTypeAsync(string typeKey, CancellationToken ct = default)
    {
        // Set the desired state. Materialise a row if the type had none, with batch/gap/max ALL null so it keeps
        // inheriting the DEFAULT row (TypeConfigExpr coalesces) — the row exists only to carry pause_state.
        // Copying the DEFAULT's concrete values here instead would permanently "configure" a type that was
        // deliberately inheriting: a later edit to the DEFAULT would move every other inheriting type but
        // silently not this one, and /settings/types would start reporting it as configured.
        // An already-configured type keeps whatever it had.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.type_schedule (type_key, batch_size, gap_seconds, max_attempts, pause_state)
             values (@typeKey, null, null, null, 'paused')
             on conflict (type_key) do update set pause_state = 'paused'
             """,
            new { typeKey }, cancellationToken: ct));
    }

    public async Task ResumeTypeAsync(string typeKey, bool rebase = true, CancellationToken ct = default)
    {
        // Set the desired state to a drain state; the loop flips 'Paused'→'Pending' in chunks (rebasing when
        // asked) and flips pause_state to 'active' once drained. A type with no row is already active — nothing
        // to resume — so this is a plain update (no materialise).
        var state = rebase ? "resuming_rebase" : "resuming_asis";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"update {S}.type_schedule set pause_state = @state where type_key = @typeKey",
            new { typeKey, state }, cancellationToken: ct));
    }

    // The background reconcile: move ripples toward each type's desired pause_state in bounded chunks, so a
    // pause/resume over millions is spread across ticks instead of one long-locking transaction. Advisory-lock-
    // gated (only one instance works at a time; the rest are cheap no-ops), mirroring refresh_wave_stats /
    // compaction. `chunkSize` bounds each UPDATE's transaction; `maxRowsPerPass` bounds total work per call.
    // Returns the number of ripples moved. Both directions converge monotonically (see the state comments in
    // M0001_Schema): while 'paused' no new 'Pending' appears, while 'resuming_*' no new 'Paused' appears.
    public async Task<int> ReconcilePauseTransitionsAsync(int chunkSize, int maxRowsPerPass,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select pg_try_advisory_lock(@key)", new { key = PauseReconcileLockKey }, cancellationToken: ct));
        if (!acquired)
        {
            return 0;
        }

        try
        {
            // Both directions are discovered BEFORE either runs, so the pass budget can be split between them.
            // Sharing one running total let direction (A) spend it all and starve (B) completely: pausing a
            // 10M-ripple type at the defaults (200k/pass, 2s interval) blocked EVERY other type's resume for
            // ~100 seconds, during which the resumed work stayed invisible to the claim with nothing logged.
            var pausing = (await conn.QueryAsync<string>(new CommandDefinition(
                $"select type_key from {S}.type_schedule where pause_state = 'paused'",
                cancellationToken: ct))).AsList();
            var resuming = (await conn.QueryAsync<TypeState>(new CommandDefinition(
                $"select type_key, pause_state from {S}.type_schedule where pause_state in ('resuming_rebase', 'resuming_asis')",
                cancellationToken: ct))).AsList();

            // Half each when both have work, so neither direction can starve the other; the whole budget when
            // only one does, so the common single-direction case is unchanged.
            var bothDirections = pausing.Count > 0 && resuming.Count > 0;
            var directionBudget = bothDirections ? Math.Max(1, maxRowsPerPass / 2) : maxRowsPerPass;

            // …and then PER TYPE within a direction, for the identical reason one level down: a running total
            // shared across type_keys lets one huge type consume the direction's whole budget for pass after
            // pass, so a second type paused/resumed in the meantime moves zero rows and stays invisible to the
            // claim (its ripples sit in 'Paused', counted by refresh_wave_stats, so its waves never complete).
            // Each type gets an equal slice; unused slices are simply not reallocated, which keeps the pass
            // bounded by maxRowsPerPass — the point of the budget.
            var pauseTypeBudget = Math.Max(1, directionBudget / Math.Max(1, pausing.Count));
            var resumeTypeBudget = Math.Max(1, directionBudget / Math.Max(1, resuming.Count));

            var moved = 0;

            // (A) Pausing types: park residual Pending → Paused (order irrelevant — they just leave the index).
            foreach (var typeKey in pausing)
            {
                var pausedMoved = 0;
                while (pausedMoved < pauseTypeBudget && !ct.IsCancellationRequested)
                {
                    var n = await conn.ExecuteAsync(new CommandDefinition(
                        $"""
                         update {S}.ripple r set state = 'Paused'
                         from (
                             select id from {S}.ripple
                             where type_key = @typeKey and state = 'Pending'
                             limit @chunkSize
                             for update skip locked
                         ) c
                         where r.id = c.id
                         """,
                        new { typeKey, chunkSize }, cancellationToken: ct));
                    pausedMoved += n;
                    if (n < chunkSize)
                    {
                        break; // drained this type's Pending set
                    }
                }

                moved += pausedMoved;
            }

            // (B) Resuming types: un-park Paused → Pending, lowest schedule_order first, re-stamping when
            //     rebasing (base + (k/batch)*gap, reusing the fan-out's ScheduleOrderSql so the ordering logic
            //     stays in one home). The base is the type's own live TAIL (TypeTailBaseExpr), not the bare
            //     frontier: the frontier is a minimum, so every chunk would recompute the SAME base and stack
            //     onto the same slots. Reading the tail — which each chunk's rows extend as they land — makes
            //     successive chunks append. Both read only state='Pending', and inside the UPDATE's pre-update
            //     snapshot the rows being un-parked are still 'Paused', so they're excluded from the base they
            //     are about to land on.
            foreach (var (typeKey, pauseState) in resuming)
            {
                var rebase = pauseState == "resuming_rebase";
                var resumedMoved = 0;
                bool drained;
                while (resumedMoved < resumeTypeBudget && !ct.IsCancellationRequested)
                {
                    var chunkSql = rebase
                        ? $"""
                           with cfg as (
                               select {ScheduleOrderSql.TypeConfigExpr("batch_size", "@typeKey")}  as bs,
                                      {ScheduleOrderSql.TypeConfigExpr("gap_seconds", "@typeKey")} as gp
                           ),
                           base as (
                               select {ScheduleOrderSql.TypeTailBaseExpr("@typeKey")} as b
                           ),
                           batch as (
                               select id, schedule_order from {S}.ripple
                               where type_key = @typeKey and state = 'Paused'
                               order by schedule_order
                               limit @chunkSize
                               for update skip locked
                           ),
                           seq as (
                               select id, (row_number() over (order by schedule_order) - 1) as k from batch
                           )
                           update {S}.ripple r
                           set state = 'Pending',
                               schedule_order = (select b from base) + (seq.k / (select bs from cfg)) * (select gp from cfg)
                           from seq
                           where r.id = seq.id
                           """
                        : $"""
                           update {S}.ripple r set state = 'Pending'
                           from (
                               select id from {S}.ripple
                               where type_key = @typeKey and state = 'Paused'
                               order by schedule_order
                               limit @chunkSize
                               for update skip locked
                           ) c
                           where r.id = c.id
                           """;
                    var n = await conn.ExecuteAsync(new CommandDefinition(
                        chunkSql, new { typeKey, chunkSize }, cancellationToken: ct));
                    resumedMoved += n;
                    if (n < chunkSize)
                    {
                        // A short chunk means "fewer than chunkSize *unlocked* rows" — the batch selects
                        // `for update skip locked`, so rows locked by a concurrent writer are invisible here
                        // and a short result is NOT proof the Paused set is empty. Confirm explicitly below
                        // before declaring the type active; flipping to 'active' with Paused rows left would
                        // strand them permanently (the claim only ever sees state='Pending', and the reconcile
                        // only revisits types that are still paused/resuming_*).
                        break;
                    }
                }

                // Only declare 'active' once genuinely drained — a plain existence check, no skip locked, so a
                // row held by a concurrent transaction still counts. Otherwise leave it resuming for the next pass.
                drained = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                    $"select 1 from {S}.ripple where type_key = @typeKey and state = 'Paused' limit 1",
                    new { typeKey }, cancellationToken: ct)) is null;

                if (drained)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        $"update {S}.type_schedule set pause_state = 'active' where type_key = @typeKey and pause_state = @pauseState",
                        new { typeKey, pauseState }, cancellationToken: ct));
                }

                moved += resumedMoved;
            }

            return moved;
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_unlock(@key)", new { key = PauseReconcileLockKey }));
        }
    }

    // ---- stats ----------------------------------------------------------------------------------

    public async Task<bool> TryRefreshWaveStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Only one instance need refresh at a time (the function is idempotent, but skipping the redundant
        // work is cheap). try-lock, not lock: a busy instance returns immediately rather than queueing.
        var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select pg_try_advisory_lock(@key)", new { key = StatsRefreshLockKey }, cancellationToken: ct));
        if (!acquired)
        {
            return false;
        }

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"select {S}.refresh_wave_stats()", cancellationToken: ct));
            return true;
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_unlock(@key)", new { key = StatsRefreshLockKey }));
        }
    }

    // ---- poll (heartbeat + claim, one round trip) -----------------------------------------------

    // One statement, one implicit transaction. The `beat` CTE upserts the heartbeat — a data-modifying CTE
    // runs to completion even when unreferenced, so the beat fires with limit 0. `claimed` pulls the globally
    // lowest-schedule_order pending, retry-eligible ripples across ALL waves under SKIP LOCKED — the batch is
    // heterogeneous (whatever the precomputed order interleaves), the fair-share having been baked into
    // schedule_order at fan-out. No counters move; the wave's live numbers are recomputed by refresh_wave_stats().
    // Served index-only by ix_ripple_schedule_order. The final SELECT joins each claimed ripple to its shared wave
    // payload.
    private const string PollSql = $"""
        with beat as (
            insert into {S}.instance_heartbeat (instance_id, last_seen_at, executing)
            values (@me, now(), @executing)
            on conflict (instance_id) do update set last_seen_at = now(), executing = @executing
        ),
        claimed as (
            update {S}.ripple r
            set state = 'Running', claimed_by = @me, claimed_at = now(), attempt = attempt + 1
            from (
                select id from {S}.ripple
                where state = 'Pending' and (next_attempt_at is null or next_attempt_at <= now())
                  -- Pause enforcement: the instant a type flips to pause_state='paused' the claim skips it here,
                  -- even before the background reconcile loop has parked its residual Pending rows into 'Paused'.
                  -- Near-free — the anti-join subquery is a one-time hashed sub-plan that is EMPTY whenever nothing
                  -- is paused, making `<> all(∅)` trivially true for every row; while a large type is mid-pause it
                  -- filters the not-yet-parked stragglers (a temporary, bounded scan-past until the loop drains
                  -- them). No type_schedule row is locked (a sub-SELECT is not covered by this statement's FOR
                  -- UPDATE, which fences ripple only).
                  and type_key <> all (select type_key from {S}.type_schedule where pause_state = 'paused')
                order by schedule_order
                for update skip locked
                limit @limit
            ) c
            where r.id = c.id
            returning r.id, r.wave_id, r.type_key, r.attempt, r.payload, r.payload_type
        )
        -- max_attempts (the retry ceiling) is per-type config: resolved here from type_schedule (falling back
        -- to the DEFAULT row when the type has no row or leaves it null). The left joins are in this final
        -- SELECT, NOT the FOR-UPDATE subquery, so no type_schedule row is locked by the claim.
        select c.id, c.wave_id, c.type_key, c.attempt,
               coalesce(ts.max_attempts, def.max_attempts) as max_attempts,
               c.payload, c.payload_type,
               w.payload as wave_payload, w.payload_type as wave_payload_type
        from claimed c
        join {S}.wave w on w.id = c.wave_id
        left join {S}.type_schedule ts on ts.type_key = c.type_key
        left join {S}.type_schedule def on def.type_key = '{RippleTypeKey.Default}';
        """;

    public async Task<IReadOnlyList<ClaimedRipple>> PollAsync(int limit, string instanceId, int executing,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var claimed = await conn.QueryAsync<ClaimedRipple>(new CommandDefinition(
            PollSql,
            new { me = instanceId, executing, limit = Math.Max(0, limit) },
            cancellationToken: ct));
        return claimed.AsList();
    }

    // ---- heartbeat ------------------------------------------------------------------------------

    public async Task BeatAsync(string instanceId, int executing, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.instance_heartbeat (instance_id, last_seen_at, executing)
             values (@instanceId, now(), @executing)
             on conflict (instance_id) do update set last_seen_at = now(), executing = @executing
             """,
            new { instanceId, executing }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<InstanceHeartbeat>> GetHeartbeatsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<InstanceHeartbeat>(new CommandDefinition(
            $"select instance_id, last_seen_at, executing from {S}.instance_heartbeat order by instance_id",
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> RemoveInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Drop the heartbeat so peers see us gone immediately rather than waiting out HeartbeatTimeout — but
        // ONLY if we left nothing behind. This row is the sole handle recovery has on our work: RecoverStaleAsync
        // reclaims ripples whose claimed_by appears in instance_heartbeat past the timeout, and there is no
        // owner-agnostic time reaper. Delete it with rows still Running and they are stranded FOREVER (a restart
        // gets a fresh InstanceId, so self-recovery can't find them either), their wave never sees running = 0,
        // never completes, and never compacts.
        // The guard is a NOT EXISTS rather than an in-process count because the ways work gets left behind don't
        // all show up in ExecutingCount: settlement retries exhausted keeps the ripple counted, but a ripple the
        // execute block refused was already decremented, and a cancelled handler settles normally. Asking the DB
        // "does this instance still own a Running row?" covers every path at once, and rides ix_ripple_running.
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            $"""
             delete from {S}.instance_heartbeat h
             where h.instance_id = @instanceId
               and not exists (select 1 from {S}.ripple
                               where claimed_by = @instanceId and state = 'Running')
             """,
            new { instanceId }, cancellationToken: ct));
        return deleted > 0;
    }

    // ---- recovery -------------------------------------------------------------------------------

    // One statement, one implicit transaction. `dead` lists the stale instances (excluding us). `moved`
    // reclaims their in-flight ripples — fenced on state='Running' so concurrent survivors don't double-
    // process — requeuing to Pending (keeping schedule_order, so they re-claim at their old position), or, if a
    // ripple has exhausted its attempts (its owner died mid-run every time = poison), failing it terminally.
    // If the ripple's type has since been paused, it requeues into 'Paused' (StateExpr) rather than 'Pending', so
    // reclaiming a paused type's stranded work doesn't leak it back into the claim.
    // `abandoned` records an Abandoned splash per reclaimed ripple, reconstructed from its claim (so an
    // outcome-less attempt is explained). `prune` drops the dead heartbeat rows. No counters move and no
    // completion is decided here — the wave's live numbers and any resulting drain self-heal on the next
    // refresh_wave_stats(). When `dead` is empty, nothing matches — nearly free.
    // static readonly (not const): interpolates ScheduleOrderSql.StateExpr(...) for the paused-aware requeue.
    private static readonly string RecoverStaleSql = $"""
        with dead as (
            select instance_id from {S}.instance_heartbeat
            where last_seen_at < now() - @threshold and instance_id <> @me
        ),
        moved as (
            -- Poison check: attempt spent vs the per-type retry ceiling (type_schedule.max_attempts, falling
            -- back to the DEFAULT row). Correlated per reclaimed row; the moved set is tiny (dead instances
            -- only), so this is off the hot path.
            update {S}.ripple r
            set state = case when r.attempt >= coalesce(
                                 (select ts.max_attempts from {S}.type_schedule ts where ts.type_key = r.type_key),
                                 (select max_attempts from {S}.type_schedule where type_key = '{RippleTypeKey.Default}'))
                             then 'Failed' else {ScheduleOrderSql.StateExpr("r.type_key")} end,
                claimed_by = null,
                completed_at = case when r.attempt >= coalesce(
                                 (select ts.max_attempts from {S}.type_schedule ts where ts.type_key = r.type_key),
                                 (select max_attempts from {S}.type_schedule where type_key = '{RippleTypeKey.Default}'))
                             then now() else r.completed_at end
            where r.state = 'Running' and r.claimed_by in (select instance_id from dead)
            returning r.id, r.wave_id, r.attempt, r.claimed_at
        ),
        abandoned as (
            -- Attempt-level abandon record; per-target expansion is deferred (recovery is SQL and can't read
            -- the payload's target ids), so the report is one Abandoned group with the reason and no targets.
            insert into {S}.splash
                (id, ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report)
            select gen_random_uuid(), m.id, m.wave_id, m.attempt, m.claimed_at, m.claimed_at, now(),
                   'Abandoned', (extract(epoch from (now() - m.claimed_at)) * 1000)::bigint,
                   jsonb_build_array(jsonb_build_object(
                       'outcome', 'Abandoned',
                       'output', 'abandoned: owning instance went stale',
                       'targetIds', '[]'::jsonb))
            from moved m
        ),
        prune as (
            delete from {S}.instance_heartbeat where instance_id in (select instance_id from dead)
        )
        select id from moved;
        """;

    public async Task<int> RecoverStaleAsync(TimeSpan threshold, string selfInstanceId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var moved = (await conn.QueryAsync<Guid>(new CommandDefinition(
            RecoverStaleSql, new { threshold, me = selfInstanceId },
            cancellationToken: ct))).AsList();
        return moved.Count;
    }

    // Mirror of RecoverStaleSql but scoped to OUR OWN claims and gated on the caller's live in-flight set: a
    // ripple Running & claimed by us, older than @grace, that we are NOT actually running (id not in @keepIds)
    // was stranded — requeue it (or poison-fail) and record an Abandoned splash. The @grace covers the claim →
    // block-enqueue window so a just-claimed ripple isn't reaped mid-dispatch. Empty @keepIds + zero @grace ⇒
    // release everything we own (post-fault). Poison check mirrors RecoverStaleSql.
    // static readonly (not const): interpolates ScheduleOrderSql.StateExpr(...) for the paused-aware requeue.
    private static readonly string RecoverSelfStrandedSql = $"""
        with moved as (
            update {S}.ripple r
            set state = case when r.attempt >= coalesce(
                                 (select ts.max_attempts from {S}.type_schedule ts where ts.type_key = r.type_key),
                                 (select max_attempts from {S}.type_schedule where type_key = '{RippleTypeKey.Default}'))
                             then 'Failed' else {ScheduleOrderSql.StateExpr("r.type_key")} end,
                claimed_by = null,
                completed_at = case when r.attempt >= coalesce(
                                 (select ts.max_attempts from {S}.type_schedule ts where ts.type_key = r.type_key),
                                 (select max_attempts from {S}.type_schedule where type_key = '{RippleTypeKey.Default}'))
                             then now() else r.completed_at end
            where r.state = 'Running'
              and r.claimed_by = @me
              and r.claimed_at < now() - @grace
              and not (r.id = any(@keepIds))
            returning r.id, r.wave_id, r.attempt, r.claimed_at
        ),
        abandoned as (
            insert into {S}.splash
                (id, ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report)
            select gen_random_uuid(), m.id, m.wave_id, m.attempt, m.claimed_at, m.claimed_at, now(),
                   'Abandoned', (extract(epoch from (now() - m.claimed_at)) * 1000)::bigint,
                   jsonb_build_array(jsonb_build_object(
                       'outcome', 'Abandoned',
                       'output', 'reclaimed: self-stranded claim (owner alive but not executing it)',
                       'targetIds', '[]'::jsonb))
            from moved m
        )
        select id from moved;
        """;

    public async Task<int> RecoverSelfStrandedAsync(string selfInstanceId, IReadOnlyCollection<Guid> keepIds,
        TimeSpan grace, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var moved = (await conn.QueryAsync<Guid>(new CommandDefinition(
            RecoverSelfStrandedSql,
            new { me = selfInstanceId, keepIds = keepIds.ToArray(), grace },
            cancellationToken: ct))).AsList();
        return moved.Count;
    }
}
