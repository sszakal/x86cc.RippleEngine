using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The single projection of a <c>wave</c> row into <see cref="Core.Wave"/>, shared by every reader — and its
/// derived <see cref="PendingExpr"/> / <see cref="SucceededExpr"/>, which the dashboard's wave queries reuse.
/// </summary>
/// <remarks>
/// It lives here rather than on one store because the derived columns are subtle and were previously copied:
/// a wave's live numbers are a cache recomputed onto the row by <c>refresh_wave_stats()</c>, so before the first
/// refresh (<c>refreshed_at is null</c>) the counts are still their 0 defaults and every ripple must be reported
/// as pending — the honest pre-refresh view. Once refreshed, <c>succeeded</c> is DERIVED
/// (<c>ripple_count - pending - running - paused - failed</c>) rather than stored, so no read ever scans the
/// settled millions. Omitting <c>paused</c> from that subtraction silently inflates <c>succeeded</c> by exactly
/// the parked-ripple count, which is what a divergent copy of this SQL used to do.
/// </remarks>
internal static class WaveSql
{
    private const string S = M0001_Schema.SchemaName;

    /// <summary>The honest live <c>pending</c> of wave <c>w</c>: every ripple until the first refresh.</summary>
    public const string PendingExpr = "case when w.refreshed_at is null then w.ripple_count else w.pending end";

    /// <summary>The derived <c>succeeded</c> of wave <c>w</c> (never stored) — see the remarks for why <c>paused</c>
    /// must be in the subtraction.</summary>
    public const string SucceededExpr =
        "case when w.refreshed_at is null then 0 "
        + "else greatest(0, w.ripple_count - w.pending - w.running - w.paused - w.failed) end";

    /// <summary>The projection, without a <c>where</c> clause — callers append their own predicate.</summary>
    public const string Select = $"""
        select w.id, w.name, w.type, w.payload, w.payload_type, w.status, w.ripple_count,
               {PendingExpr} as pending,
               w.running,
               w.paused,
               {SucceededExpr} as succeeded,
               w.failed,
               w.retry_count,
               w.created_at, w.completed_at
        from {S}.wave w
        """;

    /// <summary>The projection for a single wave, bound to the given parameter name (default <c>@id</c>).</summary>
    public static string ById(string parameterName = "@id") => $"{Select}{Environment.NewLine}where w.id = {parameterName}";
}
