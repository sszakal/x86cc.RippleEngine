using System.Linq.Expressions;
using Dapper;
using Npgsql;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// Shared base for every <b>queryable-source</b> wave builder (Marten, EF Core, …). It owns all the
/// provider-neutral plumbing: accumulating specs, running each one's <c>INSERT … SELECT</c> via
/// <see cref="WaveInsertSql"/> in a single transaction, and persisting the wave (create or continue). A
/// provider subclass only implements the two source-querying methods — turning an expression predicate +
/// projection into the inner <c>data</c> SQL and the command that carries its bound parameters — and calls
/// <see cref="AddSpec"/>. The raw-SQL escape hatch and the whole dispatch are already provider-neutral here.
/// Internal; exposed to the provider packages via <c>InternalsVisibleTo</c>.
/// </summary>
internal abstract class WaveBuilderBase : IWaveBuilder
{
    private const string S = M0001_Schema.SchemaName;

    /// <param name="DataSql">A <c>select … as data</c> producing one jsonb ripple payload per row.</param>
    /// <param name="Command">The provider-built command whose bound parameters <paramref name="DataSql"/>
    /// references; null for a raw-SQL spec with no parameters.</param>
    protected sealed record Spec(string Discriminator, string DataSql, NpgsqlCommand? Command);

    private readonly RippleDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly string? _name;
    private readonly string? _waveType;
    private readonly string? _wavePayloadJson;
    private readonly Guid _waveId;
    private readonly RippleExpansion? _expansion;
    private readonly List<Spec> _specs = new();

    // Continue mode is exactly "dispatching a running ripple's expansion".
    private bool ContinueExisting => _expansion is not null;

    /// <param name="expansion">Null to create a new wave; the running ripple's expansion to continue its wave.</param>
    protected WaveBuilderBase(RippleDataSource dataSource, TimeProvider time, string? name, string? waveType,
        string? wavePayloadJson, Guid waveId, RippleExpansion? expansion)
    {
        _dataSource = dataSource;
        _time = time;
        _name = name;
        _waveType = waveType;
        _wavePayloadJson = wavePayloadJson;
        _waveId = waveId;
        _expansion = expansion;
    }

    /// <summary>Accumulates one fan-out spec (called by the provider subclass from its AddRipples methods).</summary>
    protected void AddSpec(string discriminator, string dataSql, NpgsqlCommand? command)
        => _specs.Add(new Spec(discriminator, dataSql, command));

    public abstract IWaveBuilder AddRipples<TSource, TMessage>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TMessage>> toMessage)
        where TSource : class
        where TMessage : notnull;

    public abstract IWaveBuilder AddRipplesBatched<TSource, TBatch, TItem>(
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TItem>> item,
        Expression<Func<TBatch, IEnumerable<TItem>>> into,
        int batchSize)
        where TSource : class
        where TBatch : notnull
        where TItem : notnull;

    public IWaveBuilder AddRipplesRaw<TBatch>(string sql)
        where TBatch : notnull
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("Raw ripple SQL must be non-empty.", nameof(sql));
        }

        // The user's SQL yields one row per ripple with a single jsonb column (the payload); we just normalise
        // its column name to `data`. No bound parameters — the SQL is self-contained.
        AddSpec(RippleTypeName.Of<TBatch>(), $"select _r.data as data from ({TrimInner(sql)}) as _r(data)", command: null);
        return this;
    }

    public async Task<Wave> DispatchAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // An in-flight expansion is made once (see ExpansionSql): if an earlier attempt of the expanding
            // ripple already made this one, run none of the specs and hand back the wave as it stands.
            if (_expansion is { } repeated && !await ExpansionSql.TryBeginAsync(conn, tx, _waveId, repeated, ct))
            {
                return await ReadWaveAsync(conn, tx, ct);
            }

            long total = 0;
            foreach (var spec in _specs)
            {
                // Reuse the provider-built command (it carries the bound parameters DataSql references); a raw
                // spec has no command, so we run a fresh one. The shared builder wraps the inner `data` SQL into
                // the ripple.ripple INSERT…SELECT with the schedule_order/type_key stamping.
                var cmd = spec.Command ?? new NpgsqlCommand();
                try
                {
                    cmd.Connection = conn;
                    cmd.Transaction = tx;
                    cmd.CommandText = WaveInsertSql.BuildInsertSelect(
                        _waveId, _expansion?.ParentRippleId, ContinueExisting, _waveType, spec.Discriminator, spec.DataSql);
                    total += await cmd.ExecuteNonQueryAsync(ct);
                }
                finally
                {
                    if (spec.Command is null)
                    {
                        await cmd.DisposeAsync();
                    }
                }
            }

            Wave wave;
            if (_expansion is { } expansion)
            {
                wave = await ContinueWaveAsync(conn, tx, total, ct);
                await ExpansionSql.CompleteAsync(conn, tx, _waveId, expansion, total, ct);
            }
            else
            {
                wave = await CreateWaveAsync(conn, tx, total, ct);
            }

            await tx.CommitAsync(ct);
            return wave;
        }
        finally
        {
            // Dispose the provider-built commands on EVERY path, including the ones that never ran (a repeated
            // expansion, an earlier spec throwing): Marten's ToCommand() and EF's CreateDbCommand() results are
            // ours to own once handed over, and skipping them leaked a command + its parameter collection per
            // spec per dispatch.
            foreach (var spec in _specs)
            {
                if (spec.Command is { } command)
                {
                    await command.DisposeAsync();
                }
            }
        }
    }

    private async Task<Wave> CreateWaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long total,
        CancellationToken ct)
    {
        var createdAt = _time.GetUtcNow();
        // Born-complete: a fan-out whose source matched zero rows is a job with nothing to do — persist it
        // already Completed (audit record, flows through compaction/retention) rather than a zero-ripple Active
        // wave that, because refresh_wave_stats() requires ripple_count > 0, would never drain.
        var wave = new Wave
        {
            Id = _waveId,
            Name = _name ?? "",
            Type = _waveType ?? "default",
            PayloadType = _waveType,
            CreatedAt = createdAt,
            Status = total > 0 ? WaveStatus.Active : WaveStatus.Completed,
            CompletedAt = total > 0 ? null : createdAt,
            RippleCount = total,
            Pending = total // all fanned-out ripples are pending until the first refresh_wave_stats() recomputes.
        };

        // The wave row keeps ripple_count; there are no counters to seed — the wave's live numbers are
        // recomputed from the ripples themselves by refresh_wave_stats().
        await conn.ExecuteAsync(new CommandDefinition(
            $"insert into {S}.wave (id, name, type, payload, payload_type, status, ripple_count, created_at, completed_at) " +
            "values (@id, @name, @type, @payload::jsonb, @payloadType, @status, @rippleCount, @createdAt, @completedAt);",
            new
            {
                id = wave.Id, name = wave.Name, type = wave.Type, payload = _wavePayloadJson,
                payloadType = _waveType, status = wave.Status.ToString(), rippleCount = wave.RippleCount,
                createdAt = wave.CreatedAt, completedAt = wave.CompletedAt
            }, tx, cancellationToken: ct));

        return wave;
    }

    private async Task<Wave> ContinueWaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long total,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            $"update {S}.wave set ripple_count = ripple_count + @total where id = @waveId;",
            new { total, waveId = _waveId }, tx, cancellationToken: ct));

        return await ReadWaveAsync(conn, tx, ct);
    }

    // The SHARED projection (WaveSql) — this used to be a divergent copy that omitted paused/retry_count and
    // left `- w.paused` out of the succeeded derivation, so continuing a wave whose type was paused reported
    // Paused = 0 and a Succeeded inflated by exactly the parked-ripple count.
    private async Task<Wave> ReadWaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<Wave>(new CommandDefinition(
               WaveSql.ById("@waveId"), new { waveId = _waveId }, tx, cancellationToken: ct))
           ?? throw new InvalidOperationException($"Wave {_waveId} was not found");

    /// <summary>A provider's SQL builder may emit a trailing ';' that is illegal once the SELECT becomes a subquery.</summary>
    protected static string TrimInner(string sql) => sql.TrimEnd().TrimEnd(';').TrimEnd();

    /// <summary>Reads the target property name from an <c>into</c> selector (e.g. <c>b => b.CompanyIds</c>).</summary>
    protected static string MemberName<T, TProp>(Expression<Func<T, TProp>> selector)
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : selector.Body;
        return body is MemberExpression m
            ? m.Member.Name
            : throw new ArgumentException("Expected a simple property selector, e.g. b => b.CompanyIds.", nameof(selector));
    }
}
