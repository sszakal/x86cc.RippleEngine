using Dapper;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using x86cc.RippleEngine.Hosting;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// One Postgres container for the whole test run, and a fresh database per test cloned from a migrated template.
/// </summary>
/// <remarks>
/// xUnit builds a new test-class instance for every test, so a container per <see cref="RippleTestBase"/> was a
/// container per TEST — container start-up dominated the suite. <c>CREATE DATABASE … TEMPLATE</c> keeps the
/// isolation that gave (every test still starts from an empty, migrated schema, and nothing a test leaves behind —
/// rows, Marten/EF tables, advisory locks — can reach the next one) at the cost of a file copy, and the migration
/// runs once instead of per test.
/// <para>
/// xUnit v2 has no assembly-level fixture, so the container is started lazily by the first test that needs it and
/// stopped on process exit (Testcontainers' reaper is the backstop if the process dies harder than that).
/// </para>
/// </remarks>
internal static class SharedPostgres
{
    private const string Template = "ripple_template";

    private static readonly Lazy<Task<PostgreSqlContainer>> Server = new(StartAsync);

    /// <summary>Creates an empty, migrated database for one test and returns its connection string.</summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        var server = await Server.Value;
        var name = $"t_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(server.GetConnectionString()))
        {
            await admin.ExecuteAsync($"create database {name} template {Template}");
        }

        return With(server, name);
    }

    /// <summary>Drops a test's database, closing whatever the test's hosts and pools left open.</summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        // Idle pooled connections would otherwise outlive the database, and drop … with (force) would kill them
        // underneath the pool.
        NpgsqlConnection.ClearAllPools();

        var server = await Server.Value;
        await using var admin = new NpgsqlConnection(server.GetConnectionString());
        await admin.ExecuteAsync($"drop database if exists {name} with (force)");
    }

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17").Build();
        await container.StartAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();

        await using (var admin = new NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.ExecuteAsync($"create database {Template}");
        }

        // Migrate the template through the same facade the tests use, then release every connection to it:
        // Postgres refuses to clone a database that has any open session.
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddRippleEngine(o =>
        {
            o.ConnectionString = With(container, Template);
            o.EnableWorkers = false;
            o.AutoMigrate = false;
        });
        var host = builder.Build();
        host.Services.MigrateRipple();
        // Async: the engine's NpgsqlDataSource is only IAsyncDisposable, and it — not the global pools that
        // ClearAllPools reaches — holds the connections the migration opened.
        await ((IAsyncDisposable)host).DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        // Belt and braces: a session left on the template (a pool we didn't know about) fails every clone with
        // "source database is being accessed by other users".
        await using (var admin = new NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.ExecuteAsync(
                "select pg_terminate_backend(pid) from pg_stat_activity where datname = @Template and pid <> pg_backend_pid()",
                new { Template });
        }

        return container;
    }

    private static string With(PostgreSqlContainer server, string database)
        => new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = database }.ConnectionString;
}
