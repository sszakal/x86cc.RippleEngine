using Dapper;
using Npgsql;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The identity of one in-flight expansion — one <c>Continue(context)</c> dispatch by a running ripple: which
/// ripple is expanding (<see cref="ParentRippleId"/>), on which <see cref="Attempt"/>, and which of its expansions
/// this is (<see cref="Key"/>). <c>(ParentRippleId, Key)</c> is what makes an expansion happen <b>once</b>: a
/// retry of the parent repeats the same key and is skipped.
/// </summary>
/// <param name="Key">
/// By default the ordinal of the <c>Continue</c> call within the attempt (<c>#1</c>, <c>#2</c>, …) — stable across
/// attempts for a handler that expands in the same order each time. A handler whose order is not deterministic
/// names its expansions explicitly instead.
/// </param>
public readonly record struct RippleExpansion(Guid ParentRippleId, int Attempt, string Key)
{
    // Ordinal keys carry this prefix and explicit ones may not, so the two can never collide.
    private const char OrdinalPrefix = '#';

    /// <summary>The expansion a generator's <c>Continue(context, expansionKey)</c> is about to dispatch.</summary>
    public static RippleExpansion For(IRippleContext context, string? expansionKey)
    {
        if (expansionKey is null)
        {
            return new RippleExpansion(context.RippleId, context.Attempt, $"{OrdinalPrefix}{context.NextExpansionOrdinal()}");
        }

        if (string.IsNullOrWhiteSpace(expansionKey) || expansionKey[0] == OrdinalPrefix)
        {
            throw new ArgumentException(
                $"An expansion key must be non-empty and must not start with '{OrdinalPrefix}' (reserved for the default, ordinal keys).",
                nameof(expansionKey));
        }

        return new RippleExpansion(context.RippleId, context.Attempt, expansionKey);
    }
}

/// <summary>
/// The two statements that bracket every in-flight expansion's transaction, shared by both fan-out paths (the
/// unnest insert in <see cref="EngineStore"/> and the <c>INSERT … SELECT</c> in <see cref="WaveBuilderBase"/>):
/// <see cref="TryBeginAsync"/> claims the expansion in the <c>ripple.expansion</c> ledger, and
/// <see cref="CompleteAsync"/> fences it on the parent's attempt before the commit.
/// </summary>
/// <remarks>
/// Why a ledger. An expansion's children commit in their own transaction, ahead of the parent's outcome — they
/// have to, since the parent staying <c>Running</c> past that commit is what stops the wave being seen as drained
/// (see <c>ExecutionPipeline</c>). The price is that an attempt which expands and then fails, times out, or dies
/// leaves its children behind, and the retry — running the same handler code — used to add a second set: every
/// child executed twice and the wave's <c>ripple_count</c> was inflated to match. "Handlers must be idempotent"
/// cannot cover it: a handler has no way to know it already expanded, and nothing tied a second set of children
/// to the first. So each expansion is recorded once per <c>(parent, key)</c>, and the FIRST to commit stands —
/// the only coherent choice, since its children may already be running or finished.
/// <para>
/// One row per <c>Continue</c> dispatch, not per child, and no index on the ripple table: a check against the
/// children themselves would need <c>parent_ripple_id</c> indexed across the settled millions.
/// </para>
/// </remarks>
internal static class ExpansionSql
{
    private const string S = M0001_Schema.SchemaName;

    /// <summary>
    /// Claims the expansion. False means it has already been made (by an earlier attempt of the parent, or by this
    /// one) and the caller must insert nothing. Runs first in the transaction: a concurrent dispatch of the same
    /// expansion blocks here on the primary key until this one commits or rolls back, so two attempts that are
    /// briefly both alive cannot both insert.
    /// </summary>
    public static async Task<bool> TryBeginAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid waveId,
        RippleExpansion expansion, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {S}.expansion (parent_ripple_id, expansion_key, wave_id, attempt)
             values (@ParentRippleId, @Key, @waveId, @Attempt)
             on conflict (parent_ripple_id, expansion_key) do nothing
             """,
            new { expansion.ParentRippleId, expansion.Key, waveId, expansion.Attempt }, tx, cancellationToken: ct)) > 0;

    /// <summary>
    /// Records how many ripples the expansion added and <b>fences</b> it: the parent must still be <c>Running</c>
    /// on the attempt that is expanding, or this throws and the whole expansion rolls back.
    /// </summary>
    /// <remarks>
    /// The fence is what keeps "only a Running ripple expands its wave" true when an attempt outlives its claim —
    /// a handler abandoned for ignoring cancellation, or a stalled instance whose work was recovered — and calls
    /// <c>Continue</c> late: by then the ripple may be settled and the wave completed, even compacted, and the
    /// new ripples would belong to nothing. <c>for share</c> holds the parent row against a concurrent settle or
    /// recovery until this transaction commits, so "Running" is still true at the moment the children become
    /// visible. It runs LAST so that lock is held for one statement, not for the length of the fan-out.
    /// </remarks>
    public static async Task CompleteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid waveId,
        RippleExpansion expansion, long rippleCount, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
             update {S}.expansion set ripple_count = @rippleCount
             where parent_ripple_id = @ParentRippleId and expansion_key = @Key
             """,
            new { expansion.ParentRippleId, expansion.Key, rippleCount }, tx, cancellationToken: ct));

        var running = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            $"""
             select 1 from {S}.ripple
             where id = @ParentRippleId and wave_id = @waveId and state = 'Running' and attempt = @Attempt
             for share
             """,
            new { expansion.ParentRippleId, waveId, expansion.Attempt }, tx, cancellationToken: ct));
        if (running is null)
        {
            throw new InvalidOperationException(
                $"Ripple {expansion.ParentRippleId} is no longer running attempt {expansion.Attempt}, so it can no "
                + "longer expand its wave; the expansion was discarded.");
        }
    }
}
