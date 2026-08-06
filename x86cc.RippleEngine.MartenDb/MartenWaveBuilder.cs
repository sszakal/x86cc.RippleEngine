using System.Linq.Expressions;
using Marten;
using Marten.Linq;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.MartenDb;

/// <summary>
/// The Marten <b>source</b> side of the fan-out: it only produces each spec's inner <c>data</c> SQL from a
/// Marten LINQ query; the shared <see cref="WaveBuilderBase"/> wraps it into the <c>ripple.ripple</c>
/// <c>INSERT … SELECT</c> and persists the wave. <c>IQueryable.ToCommand()</c> gives Marten's projected
/// <c>select jsonb_build_object(...)</c> (or a scalar select) over the user's document table as an
/// <see cref="Npgsql.NpgsqlCommand"/> (text + bound parameters); the base re-associates that command onto the
/// Ripple connection so it runs in one transaction with the wave row. No <c>$type</c> is baked into the
/// payload — the <c>payload_type</c> column (stamped by the base) is the single source of truth for a
/// ripple's type.
/// </summary>
internal sealed class MartenWaveBuilder(
    IQuerySession session, RippleDataSource dataSource, string? name, string? waveType,
    string? wavePayloadJson, Guid waveId, Guid? parentRippleId, bool continueExisting)
    : WaveBuilderBase(dataSource, name, waveType, wavePayloadJson, waveId, parentRippleId, continueExisting)
{
    public override IWaveBuilder AddRipples<TSource, TMessage>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TMessage>> toMessage)
    {
        // Marten translates the projection to `select jsonb_build_object(...) as data from <src> as d where ...`.
        var inner = session.Query<TSource>().Where(predicate).Select(toMessage).ToCommand(FetchType.FetchMany);
        // The inner already yields one `data` payload per impacted row; use it directly.
        AddSpec(typeof(TMessage).Name, TrimInner(inner.CommandText), inner);
        return this;
    }

    public override IWaveBuilder AddRipplesBatched<TSource, TBatch, TItem>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TItem>> item,
        Expression<Func<TBatch, IEnumerable<TItem>>> into,
        int batchSize)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be >= 1.");
        }

        // Marten translates the scalar projection to a single-column `select <expr> from <src> where ...`.
        // We don't know Marten's column alias, so we rename it to `val` via a derived-table column alias, then
        // bucket the rows into groups of batchSize (row_number()/N) and aggregate each bucket's scalar values
        // into one array, producing one batch payload per bucket: { <arrayProperty>: [...] }.
        var inner = session.Query<TSource>().Where(predicate).Select(item).ToCommand(FetchType.FetchMany);
        var arrayProperty = MemberName(into);
        var dataSql = $"""
            select jsonb_build_object('{arrayProperty}', jsonb_agg(val)) as data
            from (
                select val, row_number() over () as rn
                from ({TrimInner(inner.CommandText)}) as _i(val)
            ) as _b
            group by (rn - 1) / {batchSize}
            """;
        AddSpec(typeof(TBatch).Name, dataSql, inner);
        return this;
    }
}
