using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>One aggregated report entry: the targets that shared an <see cref="Outcome"/>/<see cref="Output"/>.</summary>
public readonly record struct ReportItem(SplashOutcome Outcome, string? Output, IReadOnlyList<string> TargetIds);

/// <summary>A completed wave's surviving report: the wave's metadata + its aggregated items (flattened chunks).</summary>
/// <remarks><see cref="AvgDurationMs"/> is the mean per-attempt execution time over the wave's succeeded splashes,
/// computed at compaction; null for a wave that has not compacted yet (or had no succeeded attempts).</remarks>
public sealed record WaveReport(
    Guid WaveId, string Name, string Status, long RippleCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CompactedAt,
    long? AvgDurationMs, long SplashSampleCount,
    IReadOnlyList<ReportItem> Items);

/// <summary>
/// The archive side: after a wave finishes, <see cref="CompactReadyWavesAsync"/> rolls its per-attempt splash
/// reports into aggregated <c>report_chunk</c> rows (via the DB-side <c>compact_wave()</c>) and reclaims the
/// ripple/splash rows, so only the <c>wave</c> row + a few chunks remain. <see cref="GetReportAsync"/> reads
/// the aggregated report back (for export).
/// </summary>
public interface IReportStore
{
    /// <summary>
    /// Compacts up to <paramref name="maxWaves"/> terminal, not-yet-compacted waves — each into
    /// <c>report_chunk</c> rows of up to <paramref name="chunkSize"/> targets — then deletes their splashes and
    /// ripples. Advisory-lock-gated so at most one instance compacts at a time (the rest are cheap no-ops).
    /// Returns the number of waves compacted.
    /// </summary>
    Task<int> CompactReadyWavesAsync(int chunkSize, int maxWaves, CancellationToken ct = default);

    /// <summary>
    /// Deletes up to <paramref name="maxWaves"/> compacted waves whose retention has elapsed
    /// (<c>expire_at &lt; now()</c>) — their report chunks then the wave rows (ripples/splashes are long gone).
    /// Advisory-lock-gated. Returns the number of waves purged.
    /// </summary>
    Task<int> PurgeExpiredWavesAsync(int maxWaves, CancellationToken ct = default);

    /// <summary>The wave's metadata + its aggregated report items (ordered), or null if the wave is unknown.</summary>
    Task<WaveReport?> GetReportAsync(Guid waveId, CancellationToken ct = default);
}

internal sealed class ReportStore(RippleDataSource dataSource, IOptions<RippleOptions> options) : IReportStore
{
    private const string S = M0001_Schema.SchemaName;
    private readonly RippleOptions _options = options.Value;

    // Fixed key so at most one instance runs compaction at a time (distinct from the stats-refresh lock).
    private const long CompactionLockKey = 0x7269_7070_6C65_03L; // "ripple\x03"

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<int> CompactReadyWavesAsync(int chunkSize, int maxWaves, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select pg_try_advisory_lock(@key)", new { key = CompactionLockKey }, cancellationToken: ct));
        if (!acquired)
        {
            return 0;
        }

        try
        {
            var waves = (await conn.QueryAsync<WaveTypeRow>(new CommandDefinition(
                $"""
                 select id, type from {S}.wave
                 where status in ('Completed', 'Faulted') and compacted_at is null
                 order by completed_at
                 limit @maxWaves
                 """,
                new { maxWaves }, cancellationToken: ct))).AsList();

            foreach (var w in waves)
            {
                // Each compact_wave() runs in its own implicit transaction (insert chunks + delete + stamp).
                // The per-wave-type retention is stamped as expire_at for the later purge (null ⇒ keep forever).
                await conn.ExecuteAsync(new CommandDefinition(
                    $"select {S}.compact_wave(@id, @chunkSize, @retention::interval)",
                    new { id = w.Id, chunkSize, retention = _options.RetentionFor(w.Type) }, cancellationToken: ct));
            }

            return waves.Count;
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_unlock(@key)", new { key = CompactionLockKey }));
        }
    }

    public async Task<int> PurgeExpiredWavesAsync(int maxWaves, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select pg_try_advisory_lock(@key)", new { key = CompactionLockKey }, cancellationToken: ct));
        if (!acquired)
        {
            return 0;
        }

        try
        {
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"select {S}.purge_expired_waves(@maxWaves)", new { maxWaves }, cancellationToken: ct));
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_unlock(@key)", new { key = CompactionLockKey }));
        }
    }

    public async Task<WaveReport?> GetReportAsync(Guid waveId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var wave = await conn.QuerySingleOrDefaultAsync<WaveRow>(new CommandDefinition(
            $"""
             select id, name, status, ripple_count, created_at, completed_at, compacted_at,
                    avg_duration_ms, splash_sample_count
             from {S}.wave where id = @waveId
             """,
            new { waveId }, cancellationToken: ct));
        if (wave is null)
        {
            return null;
        }

        // Each chunk's `items` is a jsonb array of {outcome, output, targetIds}; read them as text and flatten
        // across chunks in order.
        var chunkJson = await conn.QueryAsync<string>(new CommandDefinition(
            $"select items from {S}.report_chunk where wave_id = @waveId order by chunk_index",
            new { waveId }, cancellationToken: ct));

        var items = chunkJson
            .SelectMany(j => JsonSerializer.Deserialize<List<ReportItem>>(j, Json) ?? [])
            .ToList();

        return new WaveReport(wave.Id, wave.Name, wave.Status, wave.RippleCount,
            wave.CreatedAt, wave.CompletedAt, wave.CompactedAt,
            wave.AvgDurationMs, wave.SplashSampleCount, items);
    }

    private sealed class WaveTypeRow
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = "";
    }

    // Mutable (settable-property) row so Dapper's per-column coercion handles timestamptz → DateTimeOffset,
    // matching how the Wave POCO is mapped (record/constructor mapping is stricter about the type match).
    private sealed class WaveRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public long RippleCount { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public DateTimeOffset? CompactedAt { get; init; }
        public long? AvgDurationMs { get; init; }
        public long SplashSampleCount { get; init; }
    }
}
