using System.Linq.Expressions;

namespace x86cc.RippleEngine.Core;

/// <summary>
/// Fluent builder that fans a wave's ripples out of a <b>queryable source</b> (Marten documents, EF Core
/// entities, …) with a set-based, server-side <c>INSERT … SELECT</c> — the source rows are never loaded into
/// the client. Each <c>AddRipples</c> call accumulates one spec; <see cref="DispatchAsync"/> runs them all and
/// persists the wave. Provider packages implement this interface and translate the expression
/// predicate/projection into their dialect's SQL. For work items the caller already holds in memory (not a
/// queryable source) use <see cref="ICollectionWaveBuilder"/> instead.
/// </summary>
public interface IWaveBuilder
{
    /// <param name="predicate">Which source rows are impacted (translated to the SQL <c>WHERE</c>).</param>
    /// <param name="toMessage">
    /// Projects each impacted row to its self-contained ripple payload. Must be provider-translatable (plain
    /// member access + captured constants); the provider emits it as a server-side JSON projection.
    /// <typeparamref name="TMessage"/>'s name becomes the ripple's <c>payload_type</c>.
    /// </param>
    IWaveBuilder AddRipples<TSource, TMessage>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TMessage>> toMessage)
        where TSource : class
        where TMessage : notnull;

    /// <summary>
    /// Like <see cref="AddRipples{TSource,TMessage}"/> but collapses <paramref name="batchSize"/> impacted
    /// rows into <b>one</b> ripple: each impacted row contributes a scalar (<paramref name="item"/>, e.g. its
    /// id) and the framework aggregates each bucket of <paramref name="batchSize"/> scalars into a single
    /// <typeparamref name="TBatch"/> payload, assigning them to the <paramref name="into"/> property. Use for
    /// migrations / backfills where one handler should process many ids set-based (10M rows / N → ~10M/N
    /// ripples). The bucketing + array aggregation run server-side — no rows are loaded into the client. Note:
    /// a batched ripple retries as a unit, so the work must be idempotent.
    /// </summary>
    /// <param name="predicate">Which source rows are impacted (translated to the SQL <c>WHERE</c>).</param>
    /// <param name="item">A scalar projected from each impacted row (e.g. <c>c => c.Id</c>), collected into
    /// the batch. Must be provider-translatable.</param>
    /// <param name="into">The <typeparamref name="TBatch"/> property the aggregated array is assigned to
    /// (e.g. <c>b => b.CompanyIds</c>).</param>
    /// <param name="batchSize">How many impacted rows go into one ripple.</param>
    IWaveBuilder AddRipplesBatched<TSource, TBatch, TItem>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TItem>> item,
        Expression<Func<TBatch, IEnumerable<TItem>>> into,
        int batchSize)
        where TSource : class
        where TBatch : notnull
        where TItem : notnull;

    /// <summary>
    /// Escape hatch for complex grouping a provider's LINQ can't translate (joins, computed bucket keys,
    /// multi-column aggregation): the caller supplies raw SQL that yields one row per ripple with a single
    /// jsonb column (the payload). The framework wraps it in the <c>INSERT</c> and stamps the
    /// <typeparamref name="TBatch"/> name as the <c>payload_type</c>. The SQL must reference the source's
    /// physical table names and is the caller's responsibility (it is not parameterised here).
    /// </summary>
    IWaveBuilder AddRipplesRaw<TBatch>(string sql)
        where TBatch : notnull;

    /// <summary>
    /// Runs every accumulated <c>INSERT SELECT</c>, persists the wave (Active), and returns it.
    /// </summary>
    Task<Wave> DispatchAsync(CancellationToken ct = default);
}
