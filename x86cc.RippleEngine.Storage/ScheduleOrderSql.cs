using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The single home of the subtle <c>schedule_order</c> stamping SQL, shared by both fan-out inserts: the
/// unnest-based <see cref="EngineStore.AddRipplesAsync"/> (in-memory / collection source, with bound
/// parameters) and the <c>INSERT … SELECT</c> <see cref="WaveInsertSql"/> (queryable source, with inlined
/// literals). Each caller supplies its own SQL expression for the wave id / defaults (a bound-param reference
/// like <c>@waveId</c> or an inlined literal like <c>'…'::uuid</c>), so the one nucleus of ordering logic —
/// the base-clamp and the per-type batch/gap lookup — lives here rather than being copy-pasted.
/// </summary>
/// <remarks>
/// Both callers compute the final slot the same way: <c>base + (k / batch_size) * gap_seconds</c>, where
/// <c>k</c> is the 0-based index of the ripple within its <c>type_key</c>. Only <c>k</c> differs by caller
/// (EngineStore partitions by <c>type_key</c> because one call mixes types; WaveInsertSql is one discriminator
/// per call, so a plain <c>row_number()</c> suffices), so that stays inline in each. Exposed to the provider
/// packages via <c>InternalsVisibleTo</c>.
/// </remarks>
internal static class ScheduleOrderSql
{
    private const string S = M0001_Schema.SchemaName;

    /// <summary>
    /// The base (the epoch-seconds slot the first batch lands on): if the wave still has pending work, append
    /// after its own tail (FIFO within the job); otherwise it's a new/re-activated job — start at the current
    /// global frontier (the lowest pending <c>schedule_order</c>, the leftmost leaf of
    /// <c>ix_ripple_schedule_order</c>), clamped up to <c>now()</c>. NOT bare <c>now()</c>: virtual time runs
    /// ahead of the wall clock (the engine drains far faster than 1 slot/gap), so a late job based at
    /// <c>now()</c> would sit far behind the frontier and monopolise the cluster to "catch up".
    /// <c>greatest()</c> ignores nulls, so a fully idle system (no pending anywhere) falls back to
    /// <c>now()</c>. <c>now()</c> is DB-clock so a lagging host can't jump the global queue.
    /// </summary>
    /// <param name="waveIdExpr">A SQL expression for the wave id (e.g. <c>@waveId</c> or <c>'…'::uuid</c>).</param>
    /// <param name="typeKeyExpr">A SQL expression for the type key — the scheduling unit.</param>
    /// <remarks>
    /// Two subtleties, both learned the hard way:
    /// <list type="bullet">
    /// <item>The tail is scoped to this <c>type_key</c>, not to the whole wave. <c>type_key</c> IS the
    /// scheduling unit (batch/gap are per type), so a heterogeneous wave's types must each start at the frontier
    /// independently and interleave. Taking the max across the whole wave made a multi-spec dispatch SERIALISE:
    /// each spec saw the previous spec's just-inserted rows inside the same transaction and stacked behind them.</item>
    /// <item>One <c>gap</c> is ADDED, so the first new batch lands after the tail batch rather than on it.
    /// Without it <c>k = 0</c> resolves to exactly the tail slot and an expansion doubles that slot's occupancy.</item>
    /// </list>
    /// </remarks>
    public static string BaseExpr(string waveIdExpr, string typeKeyExpr) =>
        $"""
         coalesce(
             (select max(schedule_order) from {S}.ripple
              where wave_id = {waveIdExpr} and type_key = {typeKeyExpr} and state = 'Pending')
                 + {TypeConfigExpr("gap_seconds", typeKeyExpr)},
             {FrontierExpr}
         )
         """;

    /// <summary>
    /// The current global frontier as a base slot: the leftmost pending <c>schedule_order</c> (the left edge of
    /// <c>ix_ripple_schedule_order</c>) clamped up to <c>now()</c>. This is the second arm of <see cref="BaseExpr"/>
    /// — where a new/re-activated job starts — extracted so the resume rebase (which re-stamps a resumed type's
    /// parked ripples onto the current frontier so they interleave fairly rather than monopolise the cluster to
    /// "catch up") shares the exact same clamp. <c>greatest()</c> ignores nulls, so a fully idle system falls back
    /// to <c>now()</c>. Reads only <c>state='Pending'</c>, so ripples parked in <c>'Paused'</c> don't drag it down,
    /// and — evaluated inside the resume <c>UPDATE</c>'s pre-update snapshot — the rows being flipped back are still
    /// <c>'Paused'</c> here, so they are correctly excluded from the frontier they are about to land on.
    /// </summary>
    public static string FrontierExpr =>
        $"""
         greatest(extract(epoch from now())::double precision,
                  (select min(schedule_order) from {S}.ripple where state = 'Pending'))
         """;

    /// <summary>
    /// The base slot for the next chunk of a RESUMING type's re-admitted ripples: continue after that type's own
    /// live tail, or start at the frontier if it has none. The wave-agnostic sibling of <see cref="BaseExpr"/>
    /// (a resume spans every wave carrying the type).
    /// </summary>
    /// <remarks>
    /// This is what makes a chunked resume advance. Using <see cref="FrontierExpr"/> directly for every chunk
    /// does NOT work: the frontier is a MINIMUM, so once chunk 1 lands its rows at <c>B…</c> the frontier is
    /// still <c>B</c>, and chunk 2 — and every chunk after it — re-stamps onto the identical window. At the
    /// default 10k chunk / 200k pass that piles 20 chunks onto the same handful of slots, which is precisely
    /// the catch-up herd the rebase exists to prevent. Reading the type's own tail instead means each chunk
    /// appends after the previous one.
    /// </remarks>
    /// <param name="typeKeyExpr">A SQL expression for the type key.</param>
    public static string TypeTailBaseExpr(string typeKeyExpr) =>
        $"""
         coalesce(
             (select max(schedule_order) from {S}.ripple
              where type_key = {typeKeyExpr} and state = 'Pending')
                 + {TypeConfigExpr("gap_seconds", typeKeyExpr)},
             {FrontierExpr}
         )
         """;

    /// <summary>
    /// Whether a ripple's <c>type_key</c> is currently paused (its <c>type_schedule.pause_state = 'paused'</c>) —
    /// the single source of truth read by every writer of <c>'Pending'</c> (fan-out, retry requeue, recovery
    /// requeue), which writes <c>'Paused'</c> instead when this is true, so paused work never enters the claim's
    /// pending index. Only the <c>'paused'</c> state parks new work; a <c>resuming_*</c> type is treated as active
    /// (new work goes <c>'Pending'</c>). A type with no <c>type_schedule</c> row is not paused
    /// (<c>coalesce → 'active'</c>); the reserved DEFAULT row's state is irrelevant (no ripple carries that
    /// <c>type_key</c>).
    /// </summary>
    /// <param name="typeKeyExpr">A SQL expression for the type key (a column ref or an inlined literal).</param>
    public static string PausedExpr(string typeKeyExpr) =>
        $"(coalesce((select pause_state from {S}.type_schedule where type_key = {typeKeyExpr}), 'active') = 'paused')";

    /// <summary>
    /// The state a freshly-inserted or requeued ripple should take: <c>'Paused'</c> when its type is paused (see
    /// <see cref="PausedExpr"/>), else the given live state (<c>'Pending'</c>). Keeps the "park paused work out of
    /// the claim index" rule in one place across the fan-out inserts and the requeue paths.
    /// </summary>
    /// <param name="typeKeyExpr">A SQL expression for the type key.</param>
    /// <param name="liveState">The state to use when the type is not paused (normally <c>'Pending'</c>).</param>
    public static string StateExpr(string typeKeyExpr, string liveState = "'Pending'") =>
        $"case when {PausedExpr(typeKeyExpr)} then 'Paused' else {liveState} end";

    /// <summary>
    /// A single <c>type_schedule</c> column (<c>batch_size</c> / <c>gap_seconds</c>) for a ripple's
    /// <c>type_key</c>, falling back to the reserved DEFAULT row (<see cref="RippleTypeKey.Default"/>) when the
    /// type has no row of its own — so the scheduler's defaults live in the database, not in inlined SQL
    /// constants. Correlated so it works both for a single inlined discriminator and per-row over a set of mixed
    /// <c>type_key</c>s (<c>type_key</c> is the table's primary key, so each subquery matches at most one row).
    /// </summary>
    /// <param name="column"><c>batch_size</c> or <c>gap_seconds</c>.</param>
    /// <param name="typeKeyExpr">A SQL expression for the type key (a column ref or an inlined literal).</param>
    public static string TypeConfigExpr(string column, string typeKeyExpr) =>
        $"coalesce((select {column} from {S}.type_schedule where type_key = {typeKeyExpr}), " +
        $"(select {column} from {S}.type_schedule where type_key = '{RippleTypeKey.Default}'))";
}
