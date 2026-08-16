using Dapper;
using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

public static class RippleStorageExtensions
{
    /// <summary>
    /// Registers Ripple's relational storage: the dedicated <see cref="RippleDataSource"/>, the two
    /// Dapper stores — <see cref="IEngineStore"/> (waves / global claim / heartbeat / recovery) and
    /// <see cref="ISplashStore"/> (splash settlement) — and the FluentMigrator runner for the
    /// <c>ripple</c> schema. Call <see cref="MigrateRipple"/> once at startup to apply migrations.
    /// </summary>
    /// <remarks>
    /// Internal: storage is never registered on its own, because nothing works until the schema exists. It is
    /// registered — with the migration ordered ahead of the engine's startup — by
    /// <c>IHostApplicationBuilder.AddRippleEngine</c> in <c>x86cc.RippleEngine.Hosting</c>, the one supported
    /// entry point. A host that only creates waves gets exactly this and no pollers via
    /// <c>o.EnableWorkers = false</c>.
    /// </remarks>
    internal static IServiceCollection AddRippleStorage(this IServiceCollection services,
        string connectionString, Action<RippleOptions>? configure = null)
    {
        ConfigureDapper();

        services.AddOptions<RippleOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddSingleton(new RippleDataSource(connectionString));
        services.AddSingleton<IEngineStore, EngineStore>();
        services.AddSingleton<ISplashStore, SplashStore>();
        services.AddSingleton<IReportStore, ReportStore>();
        // The source-less wave generator: create waves/ripples from an in-memory collection, no provider needed.
        services.AddSingleton<ICollectionWaveGenerator, CollectionWaveGenerator>();

        services.AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(M0001_Schema).Assembly).For.Migrations())
            .AddScoped<IVersionTableMetaData, RippleVersionTable>();

        return services;
    }

    // Arbitrary fixed key so concurrent instances serialize their first migration run.
    private const long MigrationLockKey = 0x7269_7070_6C65_01L; // "ripple\x01"

    /// <summary>
    /// Applies all pending Ripple migrations (creates the <c>ripple</c> schema). Safe to call from every
    /// instance at startup: a Postgres advisory lock serializes concurrent first runs.
    /// </summary>
    /// <remarks>
    /// <c>AddRippleEngine</c> runs this for you before the engine starts. It stays public for the two cases
    /// that need to drive it themselves: <c>AutoMigrate = false</c> (migrating from a separate release step),
    /// and a provider built but never started (tests, tooling).
    /// </remarks>
    public static void MigrateRipple(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<RippleDataSource>().OpenConnection();

        using (var acquire = conn.CreateCommand())
        {
            acquire.CommandText = $"select pg_advisory_lock({MigrationLockKey})";
            acquire.ExecuteNonQuery();
        }

        try
        {
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        finally
        {
            using var release = conn.CreateCommand();
            release.CommandText = $"select pg_advisory_unlock({MigrationLockKey})";
            release.ExecuteNonQuery();
        }
    }

    private static void ConfigureDapper()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonDocumentTypeHandler());
    }
}
