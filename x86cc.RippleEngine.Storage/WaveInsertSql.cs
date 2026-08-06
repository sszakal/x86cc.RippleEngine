using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The shared SQL builder for the fan-out <c>INSERT … SELECT</c>, used by every <b>queryable-source</b>
/// provider (Marten, EF Core, …). A provider builds only the inner <paramref name="dataSql"/> — a
/// <c>select … as data</c> that yields one jsonb ripple payload per impacted row, in its own dialect — and
/// this wraps it into <c>insert into ripple.ripple … select …</c>: generating ids, stamping the composite
/// <c>type_key</c> + <c>state</c>, and computing the batch-interleaved <c>schedule_order</c> (base-clamp +
/// per-type batch/gap), all server-side so source rows never round-trip. Keeping it here means the scheduling
/// logic lives in <b>exactly one place</b> regardless of provider.
/// </summary>
/// <remarks>
/// Framework-supplied values (the wave id, the ripple type name, fixed literals) are inlined; user-driven
/// values (predicate constants, projected members) stay bound parameters inside <c>dataSql</c>, carried by the
/// provider's own command. <c>attempt</c>/<c>ripple_index</c> take their schema defaults; the retry ceiling is
/// per-type config resolved at claim time (<c>type_schedule.max_attempts</c>), not stamped here. Exposed to the
/// provider packages via <c>InternalsVisibleTo</c>.
/// </remarks>
internal static class WaveInsertSql
{
    private const string S = M0001_Schema.SchemaName;

    /// <param name="dataSql">A <c>select … as data</c> producing one jsonb ripple payload per row.</param>
    public static string BuildInsertSelect(Guid waveId, Guid? parentRippleId, bool continueExisting,
        string? waveType, string discriminator, string dataSql)
    {
        var parent = parentRippleId.HasValue ? $"'{parentRippleId.Value}'::uuid" : "null";

        // type_key = "{waveType}|{rippleType}" — the composite scheduling/handler key. For Create the wave type
        // is known here (inline literal); for Continue the wave row already exists, so read its payload_type.
        var typeKeyExpr = continueExisting
            ? $"coalesce((select payload_type from {S}.wave where id = '{waveId}'::uuid), '') || '{RippleTypeKey.Separator}{discriminator}'"
            : $"'{RippleTypeKey.Compose(waveType, discriminator)}'";

        // Stamp schedule_order (the pure ordering key) entirely server-side, then give each batch of `bs`
        // ripples the same slot, spaced by `gp` seconds. now()/base are DB-clock and transaction-stable. The
        // base-clamp and per-type batch/gap lookup live in ScheduleOrderSql — shared with the unnest fan-out
        // (EngineStore.AddRipplesAsync) so this subtle ordering logic exists in exactly one place. An
        // unconfigured type falls back to the type_schedule DEFAULT row inside TypeConfigExpr. Here the wave id
        // is an inlined literal (framework-supplied) rather than a bound param.
        return $"""
                with _src as (
                    {dataSql}
                ),
                _seq as (
                    select _src.data, row_number() over () as rn from _src
                ),
                _cfg as (
                    select {ScheduleOrderSql.TypeConfigExpr("batch_size", typeKeyExpr)} as bs,
                           {ScheduleOrderSql.TypeConfigExpr("gap_seconds", typeKeyExpr)} as gp
                ),
                _base as (
                    select {ScheduleOrderSql.BaseExpr($"'{waveId}'::uuid", typeKeyExpr)} as b
                )
                insert into {S}.ripple (id, wave_id, parent_ripple_id, payload, payload_type, type_key, state, created_at, schedule_order)
                select gen_random_uuid(),
                       '{waveId}'::uuid,
                       {parent},
                       _seq.data,
                       '{discriminator}',
                       {typeKeyExpr},
                       {ScheduleOrderSql.StateExpr(typeKeyExpr)},
                       now(),
                       _base.b + ((_seq.rn - 1) / _cfg.bs) * _cfg.gp
                from _seq cross join _cfg cross join _base
                """;
    }
}
