using Dapper;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>The <c>/api/waves</c> filters. All optional; <see cref="Search"/> is a literal substring of the wave's
/// name or type (LIKE wildcards in it are matched literally). <see cref="From"/>/<see cref="To"/> are half-open.</summary>
public sealed record WaveListFilter(
    string? Status = null, string? Search = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    int Limit = 50);

/// <summary>A wave as the dashboard lists it: the live numbers (see <c>WaveSql</c> for how pending/succeeded are
/// derived) plus its timings. <see cref="Throughput"/> is ripples per second over the wave's lifetime.</summary>
public record DashboardWave
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Status { get; init; } = "";
    public long RippleCount { get; init; }
    public long Pending { get; init; }
    public long Running { get; init; }
    public long Paused { get; init; }
    public long Succeeded { get; init; }
    public long Failed { get; init; }
    public long RetryCount { get; init; }
    public long? AvgDurationMs { get; init; }
    public long SplashSampleCount { get; init; }
    public long? DurationMs { get; init; }
    public long? Throughput { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CompactedAt { get; init; }
}

/// <summary>One wave's detail: the list view plus its payload type.</summary>
public sealed record DashboardWaveDetail : DashboardWave
{
    public string? PayloadType { get; init; }
}

/// <summary>Wave counts by status across a <see cref="WaveListFilter"/> (the list's summary strip).</summary>
public sealed record WaveStatusCounts(long Total, long Active, long Completed, long Faulted);

/// <summary>A page of the wave list and the status counts over the SAME filter (unaffected by the limit).</summary>
public sealed record WaveListPage(IReadOnlyList<DashboardWave> Waves, WaveStatusCounts Stats);

/// <summary>Waves created on one UTC day (<see cref="Date"/> is <c>YYYY-MM-DD</c>), by outcome.</summary>
public sealed record WaveDayActivity(string Date, long Count, long Completed, long Faulted, long Running);

/// <summary>Waves created in one histogram bucket. <see cref="Start"/> is the bucket's naive wall-clock start in the
/// requested time zone — deliberately zone-less, so the browser reads back the exact boundary it grouped by.</summary>
public sealed record WaveBucket(DateTime Start, long Count, long Completed, long Faulted, long Running);

/// <summary>A type's scheduling share (<c>batch_size / gap_seconds</c>) joined to its execution metrics.</summary>
public sealed record TypeMetric(
    string TypeKey, int? BatchSize, decimal? GapSeconds, decimal? Share, long SampleCount, long? AvgMs,
    long? AvgWaitMs, double? AvgRetryRate);

/// <summary>A live instance from its heartbeat; <see cref="AgeSeconds"/> is seconds since its last beat (DB clock).</summary>
/// <remarks>Init properties, not a positional record: Npgsql reports <c>timestamptz</c> as <see cref="DateTime"/>,
/// and Dapper's constructor matching won't convert that to <see cref="DateTimeOffset"/> (its property path does) —
/// so any result with a timestamptz column is shaped like this, as <see cref="DashboardWave"/> is.</remarks>
public sealed record InstanceStatus
{
    public string InstanceId { get; init; } = "";
    public DateTimeOffset LastSeenAt { get; init; }
    public int Executing { get; init; }
    public long AgeSeconds { get; init; }
}

/// <summary>A <c>type_schedule</c> row. The values are nullable: a row can exist purely to hold
/// <see cref="PauseState"/> while the type still inherits the DEFAULT row's scheduling values.</summary>
public sealed record TypeScheduleRow(
    string TypeKey, int? BatchSize, decimal? GapSeconds, int? MaxAttempts, string PauseState);

/// <summary>
/// The dashboard's read side — every query the monitoring API runs, kept here with the rest of the SQL so the
/// <c>ripple</c> schema is known to exactly one assembly (and the wave's derived numbers come from the same
/// <c>WaveSql</c> expressions every other reader uses, not a copy). Results are typed records; the API serialises
/// them camelCase.
/// </summary>
public interface IDashboardQueries
{
    /// <summary>The newest waves matching <paramref name="filter"/>, plus status counts over the same filter.</summary>
    Task<WaveListPage> ListWavesAsync(WaveListFilter filter, CancellationToken ct = default);

    /// <summary>Per-UTC-day wave counts over the last <paramref name="days"/> days (only days that had waves).</summary>
    Task<IReadOnlyList<WaveDayActivity>> GetWaveActivityAsync(int days, CancellationToken ct = default);

    /// <summary>Non-empty wave-count buckets over <c>[from, to)</c>, truncated to <paramref name="bucket"/> (a
    /// <c>date_trunc</c> unit) in <paramref name="timeZone"/>.</summary>
    Task<IReadOnlyList<WaveBucket>> GetWaveHistogramAsync(DateTimeOffset from, DateTimeOffset to, string bucket,
        string timeZone, CancellationToken ct = default);

    /// <summary>One wave, or null if unknown.</summary>
    Task<DashboardWaveDetail?> GetWaveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every type with a schedule row or recorded metrics.</summary>
    Task<IReadOnlyList<TypeMetric>> GetTypeMetricsAsync(CancellationToken ct = default);

    /// <summary>Every instance with a heartbeat row.</summary>
    Task<IReadOnlyList<InstanceStatus>> GetClusterAsync(CancellationToken ct = default);

    /// <summary>Every <c>type_schedule</c> row, including the DEFAULT one.</summary>
    Task<IReadOnlyList<TypeScheduleRow>> GetTypeSchedulesAsync(CancellationToken ct = default);
}

internal sealed class DashboardQueries(RippleDataSource dataSource) : IDashboardQueries
{
    private const string S = M0001_Schema.SchemaName;

    // The dashboard's wave columns. pending/succeeded are the SHARED derivations (WaveSql) — a divergent copy of
    // these is exactly how `succeeded` was once inflated by the parked-ripple count.
    private const string WaveColumns = $"""
        w.id, w.name, w.type, w.status, w.ripple_count,
        {WaveSql.PendingExpr} as pending,
        w.running, w.paused,
        {WaveSql.SucceededExpr} as succeeded,
        w.failed, w.retry_count, w.avg_duration_ms, w.splash_sample_count,
        case when w.completed_at is not null
             then round(extract(epoch from (w.completed_at - w.created_at)) * 1000)::bigint end as duration_ms,
        case when w.completed_at is not null and w.completed_at > w.created_at
             then round(w.ripple_count / extract(epoch from (w.completed_at - w.created_at)))::bigint end as throughput,
        w.created_at, w.completed_at, w.compacted_at
        """;

    // `escape '\'` is spelled out although it is Postgres's default, because SearchPattern relies on it.
    private const string WaveListWhere = """
        where (@status is null or w.status = @status)
          and (@q is null or w.name ilike @q escape '\' or w.type ilike @q escape '\')
          and (@from is null or w.created_at >= @from)
          -- HALF-OPEN [from, to), matching the histogram. When this was inclusive a wave created exactly on a
          -- bucket boundary appeared in two adjacent ranges' timelines and disagreed with the total derived from
          -- the histogram — and the SPA hands both endpoints the same bounds.
          and (@to is null or w.created_at < @to)
        """;

    public async Task<WaveListPage> ListWavesAsync(WaveListFilter filter, CancellationToken ct = default)
    {
        var p = new
        {
            status = string.IsNullOrWhiteSpace(filter.Status) ? null : filter.Status,
            q = string.IsNullOrWhiteSpace(filter.Search) ? null : SearchPattern(filter.Search),
            from = filter.From,
            to = filter.To,
            limit = filter.Limit
        };

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var waves = await conn.QueryAsync<DashboardWave>(new CommandDefinition(
            $"""
             select {WaveColumns}
             from {S}.wave w
             {WaveListWhere}
             order by w.created_at desc
             limit @limit
             """, p, cancellationToken: ct));

        var stats = await conn.QuerySingleAsync<WaveStatusCounts>(new CommandDefinition(
            $"""
             select count(*) as total,
                    count(*) filter (where w.status = 'Active')    as active,
                    count(*) filter (where w.status = 'Completed') as completed,
                    count(*) filter (where w.status = 'Faulted')   as faulted
             from {S}.wave w
             {WaveListWhere}
             """, p, cancellationToken: ct));

        return new WaveListPage(waves.AsList(), stats);
    }

    public async Task<IReadOnlyList<WaveDayActivity>> GetWaveActivityAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<WaveDayActivity>(new CommandDefinition(
            $"""
             select to_char(date_trunc('day', created_at), 'YYYY-MM-DD') as date,
                    count(*) as count,
                    count(*) filter (where status = 'Completed') as completed,
                    count(*) filter (where status = 'Faulted')   as faulted,
                    count(*) filter (where status = 'Active')    as running
             from {S}.wave
             -- Plain now(): it is already timestamptz, so it compares correctly against created_at.
             -- `now() at time zone 'utc'` strips the zone, and comparing that back to a timestamptz makes
             -- Postgres re-interpret it in the SESSION's TimeZone — shifting the window by the server's
             -- offset on any non-UTC session.
             where created_at >= now() - (@days || ' days')::interval
             group by 1
             order by 1
             """, new { days }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<WaveBucket>> GetWaveHistogramAsync(DateTimeOffset from, DateTimeOffset to,
        string bucket, string timeZone, CancellationToken ct = default)
    {
        // `created_at at time zone @zone` shifts the timestamptz to the caller's wall clock; date_trunc that and
        // return the naive wall-clock start, so the browser reads back the exact day it grouped by.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<WaveBucket>(new CommandDefinition(
            $"""
             select date_trunc(@bucket, created_at at time zone @zone) as start,
                    count(*) as count,
                    count(*) filter (where status = 'Completed') as completed,
                    count(*) filter (where status = 'Faulted')   as faulted,
                    count(*) filter (where status = 'Active')    as running
             from {S}.wave
             where created_at >= @from and created_at < @to
             group by 1
             order by 1
             """, new { bucket, zone = timeZone, from, to }, cancellationToken: ct))).AsList();
    }

    public async Task<DashboardWaveDetail?> GetWaveAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DashboardWaveDetail>(new CommandDefinition(
            $"select {WaveColumns}, w.payload_type from {S}.wave w where w.id = @id",
            new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TypeMetric>> GetTypeMetricsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TypeMetric>(new CommandDefinition(
            $"""
             select coalesce(ts.type_key, m.type_key) as type_key,
                    ts.batch_size,
                    ts.gap_seconds,
                    case when ts.gap_seconds is not null and ts.gap_seconds <> 0
                         then ts.batch_size / ts.gap_seconds end as share,
                    coalesce(m.sample_count, 0) as sample_count,
                    round(m.avg_ms)::bigint as avg_ms,
                    round(m.avg_wait_ms)::bigint as avg_wait_ms,
                    m.avg_retry_rate
             from {S}.type_schedule ts
             full outer join {S}.ripple_type_metric m on m.type_key = ts.type_key
             order by 1
             """, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<InstanceStatus>> GetClusterAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<InstanceStatus>(new CommandDefinition(
            $"""
             select instance_id, last_seen_at, executing,
                    round(extract(epoch from (now() - last_seen_at)))::bigint as age_seconds
             from {S}.instance_heartbeat
             order by instance_id
             """, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<TypeScheduleRow>> GetTypeSchedulesAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TypeScheduleRow>(new CommandDefinition(
            $"select type_key, batch_size, gap_seconds, max_attempts, pause_state from {S}.type_schedule",
            cancellationToken: ct))).AsList();
    }

    /// <summary>
    /// A contains-pattern matching <paramref name="search"/> LITERALLY: <c>%</c> and <c>_</c> (and the escape
    /// character itself) are escaped, so searching <c>50%_off</c> finds that text instead of treating it as
    /// "50, anything, any one character, off".
    /// </summary>
    internal static string SearchPattern(string search)
        => $"%{search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
}
