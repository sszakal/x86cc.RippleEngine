using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.Internal; // IRelationalQueryingEnumerable (EF1001 — see below)
using Npgsql;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.EntityFrameworkCore;

/// <summary>
/// The EF Core <b>source</b> side of the fan-out: it only produces each spec's inner <c>data</c> SQL from an
/// EF <see cref="IQueryable{T}"/>; the shared <see cref="WaveBuilderBase"/> wraps it into the
/// <c>ripple.ripple</c> <c>INSERT … SELECT</c> and persists the wave.
/// <para>
/// Two differences from a document store (Marten): (1) EF has no <c>ToCommand()</c>, so the parameterised SQL +
/// bound parameters are pulled from the query's relational querying-enumerable via <c>CreateDbCommand()</c>
/// (without executing it); (2) an EF projection emits <b>relational columns</b>, not jsonb, so we wrap each
/// projected row as a <c>data</c> payload with <c>to_jsonb(...)</c> keyed by the projected member names. From
/// there the INSERT wrapping, <c>type_key</c>, and <c>schedule_order</c> stamping are identical to every other
/// provider (all in <see cref="WaveInsertSql"/>).
/// </para>
/// </summary>
internal sealed class EfWaveBuilder(
    DbContext context, RippleDataSource dataSource, string? name, string? waveType,
    string? wavePayloadJson, Guid waveId, Guid? parentRippleId, bool continueExisting)
    : WaveBuilderBase(dataSource, name, waveType, wavePayloadJson, waveId, parentRippleId, continueExisting)
{
    public override IWaveBuilder AddRipples<TSource, TMessage>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TMessage>> toMessage)
    {
        var command = ToCommand(context.Set<TSource>().Where(predicate).Select(toMessage));
        // EF projects to relational columns; turn each row into a jsonb `data` payload keyed by the projected
        // member names (the SELECT aliases), which the handler deserializes case-insensitively.
        var dataSql = $"select to_jsonb(_ef) as data from ({TrimInner(command.CommandText)}) as _ef";
        AddSpec(typeof(TMessage).Name, dataSql, command);
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

        // The scalar projection yields a single column; rename it to `val`, bucket by row_number()/N, and
        // aggregate each bucket into one array — one batch payload per bucket: { <arrayProperty>: [...] }.
        var command = ToCommand(context.Set<TSource>().Where(predicate).Select(item));
        var arrayProperty = MemberName(into);
        var dataSql = $"""
            select jsonb_build_object('{arrayProperty}', jsonb_agg(val)) as data
            from (
                select val, row_number() over () as rn
                from ({TrimInner(command.CommandText)}) as _i(val)
            ) as _b
            group by (rn - 1) / {batchSize}
            """;
        AddSpec(typeof(TBatch).Name, dataSql, command);
        return this;
    }

    // Extract the parameterised SQL + bound parameters from an EF query WITHOUT executing it. The relational
    // querying-enumerable (what Provider.Execute returns for a sequence query) exposes CreateDbCommand(); for
    // the Npgsql provider that is an NpgsqlCommand carrying the query's parameters. The base then re-associates
    // it onto the Ripple connection and wraps its text into the INSERT…SELECT.
    //
    // IRelationalQueryingEnumerable lives in EF's Query.Internal namespace (EF1001, suppressed in the csproj):
    // it is public infrastructure but not part of EF's stable API surface. This is the standard technique for
    // turning an EF query into a DbCommand; if a future EF version changes it, only this method needs updating.
    private static NpgsqlCommand ToCommand<T>(IQueryable<T> query)
    {
        var enumerable = (IRelationalQueryingEnumerable)query.Provider.Execute<IEnumerable<T>>(query.Expression)!;
        return (NpgsqlCommand)enumerable.CreateDbCommand();
    }
}
