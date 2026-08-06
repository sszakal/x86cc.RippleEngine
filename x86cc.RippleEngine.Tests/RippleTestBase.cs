using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.EntityFrameworkCore;
using x86cc.RippleEngine.MartenDb;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// One real Postgres (Testcontainers) per test class, with the <c>ripple</c> schema migrated and the two
/// stores wired. <see cref="ResetAsync"/> truncates everything so each test starts clean. Helpers seed
/// waves and ripples the way the fan-out would.
/// </summary>
public abstract class RippleTestBase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17").Build();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected string ConnectionString { get; private set; } = "";
    protected ServiceProvider Storage { get; private set; } = default!;

    protected IEngineStore Engine => Storage.GetRequiredService<IEngineStore>();
    protected ISplashStore Splashes => Storage.GetRequiredService<ISplashStore>();
    protected IReportStore Reports => Storage.GetRequiredService<IReportStore>();
    protected IMartenWaveGenerator MartenGenerator => Storage.GetRequiredService<IMartenWaveGenerator>();
    protected ICollectionWaveGenerator CollectionGenerator => Storage.GetRequiredService<ICollectionWaveGenerator>();
    protected IEfWaveGenerator EfGenerator => Storage.GetRequiredService<IEfWaveGenerator>();
    protected RippleDataSource Db => Storage.GetRequiredService<RippleDataSource>();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        ConnectionString = _postgres.GetConnectionString();

        var services = new ServiceCollection();
        services.AddRippleStorage(ConnectionString);
        services.AddRippleMartenGeneration();
        services.AddRippleEfGeneration();
        Storage = services.BuildServiceProvider();
        Storage.MigrateRipple();
    }

    public async Task DisposeAsync()
    {
        await Storage.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected async Task ResetAsync()
    {
        await using var conn = await Db.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "truncate ripple.ripple, ripple.wave, ripple.splash, ripple.report_chunk, " +
            "ripple.ripple_type_metric, ripple.instance_heartbeat");
        // Clear per-type configs but keep the migration-seeded DEFAULT row — the hot-path SQL falls back to it,
        // so wiping it would break fan-out/claim on the next test.
        await conn.ExecuteAsync(
            $"delete from ripple.type_schedule where type_key <> '{RippleTypeKey.Default}'");
    }

    /// <summary>Compacts one wave directly (what <c>CompactionLoop</c> drives in a live engine): roll its
    /// splash reports into report_chunk rows, delete its ripples/splashes, and stamp expire_at from
    /// <paramref name="retention"/> (null ⇒ keep forever).</summary>
    protected Task CompactWaveAsync(Guid waveId, int chunkSize = 10_000, TimeSpan? retention = null)
        => ExecuteAsync("select ripple.compact_wave(@waveId, @chunkSize, @retention::interval)",
            new { waveId, chunkSize, retention });

    /// <summary>
    /// Runs the DB-side stats refresh directly (what <c>WaveStatsRefreshLoop</c> drives in a live engine): recompute
    /// each active wave's pending/running/failed from the truth and settle any that has drained. Storage-level
    /// tests run no hosted services, so they call this explicitly to observe wave numbers/completion.
    /// </summary>
    protected Task RefreshWaveStatsAsync() => ExecuteAsync("select ripple.refresh_wave_stats()");

    /// <summary>
    /// Runs the pause/resume reconcile directly (what <c>PauseTransitionLoop</c> drives in a live engine): move
    /// each type's ripples toward its desired <c>pause_state</c> in bounded chunks. Pause/resume only flip the
    /// state, so storage-level tests call this to drive the async parking/un-parking, then assert. Defaults drain
    /// everything; pass a small <paramref name="chunkSize"/>/<paramref name="maxRowsPerPass"/> to test chunking.
    /// </summary>
    protected Task<int> ReconcilePauseTransitionsAsync(int chunkSize = 10_000, int maxRowsPerPass = 1_000_000)
        => Engine.ReconcilePauseTransitionsAsync(chunkSize, maxRowsPerPass);

    // ---- seeding helpers -------------------------------------------------------------------------

    protected Task<Wave> CreateWaveAsync(string type = "recalc", string legislation = "VAT26")
        => Engine.CreateWaveAsync(new Wave
        {
            Name = $"{legislation} changed",
            Type = type,
            Payload = JsonSerializer.SerializeToDocument(new RecalcContext { LegislationCode = legislation }, Json),
            PayloadType = nameof(RecalcContext)
        });

    /// <summary>Adds <paramref name="count"/> RecalcCompany ripples to a wave; returns their company ids.</summary>
    protected async Task<IReadOnlyList<Guid>> SeedRipplesAsync(Guid waveId, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        var seeds = ids
            .Select(id => new RippleSeed(
                JsonSerializer.Serialize(new RecalcCompany { CompanyId = id }, Json), nameof(RecalcCompany)))
            .ToList();
        await Engine.AddRipplesAsync(waveId, seeds);
        return ids;
    }

    /// <summary>
    /// Adds <paramref name="count"/> ripples of an arbitrary payload type to a wave (trivial payload), so a
    /// test can create distinct <c>type_key</c>s (<c>{wavePayloadType}|{ripplePayloadType}</c>) with their own
    /// batch/gap.
    /// </summary>
    protected Task SeedRipplesOfTypeAsync(Guid waveId, int count, string ripplePayloadType)
    {
        var seeds = Enumerable.Range(0, count)
            .Select(_ => new RippleSeed("{}", ripplePayloadType))
            .ToList();
        return Engine.AddRipplesAsync(waveId, seeds);
    }

    /// <summary>Seeds a type's config (batch size + gap in seconds, and optional retry ceiling).</summary>
    protected Task SeedScheduleAsync(string typeKey, int batchSize, double gapSeconds, int? maxAttempts = null)
        => Engine.UpsertTypeScheduleAsync(typeKey, batchSize, gapSeconds, maxAttempts);

    protected async Task<long> ScalarAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(sql, p);
    }

    protected async Task<double> DoubleAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<double>(sql, p);
    }

    protected async Task<IReadOnlyList<long>> QueryLongsAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        return (await conn.QueryAsync<long>(sql, p)).AsList();
    }

    protected async Task ExecuteAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        await conn.ExecuteAsync(sql, p);
    }
}
