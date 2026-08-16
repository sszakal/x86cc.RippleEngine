using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Hosting;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Registration-level tests for the one entry point, <c>IHostApplicationBuilder.AddRippleEngine</c>. They only
/// build a container — nothing connects — so they need no Postgres.
/// </summary>
public sealed class FacadeTests
{
    private const string Connection = "Host=localhost;Database=ripple_facade_tests;Username=u;Password=p";

    /// <summary>A misconfigured connection must fail loudly at composition, naming both places it can come from
    /// — not surface later as an obscure Npgsql error from a background loop.</summary>
    [Fact]
    public void a_missing_connection_string_fails_immediately_and_says_where_to_put_one()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var ex = Should.Throw<InvalidOperationException>(() => builder.AddRippleEngine());

        ex.Message.ShouldContain(nameof(RippleSetupOptions.ConnectionString));
        ex.Message.ShouldContain($"ConnectionStrings:{RippleHostingExtensions.DefaultConnectionName}");
    }

    /// <summary>The connection string comes from configuration by default, so a host that already has the
    /// <c>ripple</c> connection (Aspire injects one) configures nothing at all.</summary>
    [Fact]
    public void the_ripple_connection_string_is_picked_up_from_configuration()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:ripple"] = Connection });

        Should.NotThrow(() => builder.AddRippleEngine());

        using var host = builder.Build();
        host.Services.GetRequiredService<RippleDataSource>().ShouldNotBeNull();
    }

    /// <summary>
    /// The invariant this facade exists for: the host's shutdown budget must outlast the engine's drain, or the
    /// process is hard-killed mid-drain and its in-flight ripples are left <c>Running</c> until recovery times
    /// out on them. The framework default (5s) is SHORTER than the default drain (15s), so leaving it alone is
    /// the broken case — hence the derivation.
    /// </summary>
    [Fact]
    public void the_host_shutdown_timeout_is_derived_from_the_engine_drain()
    {
        using var host = Build(o => o.ShutdownDrainGrace = TimeSpan.FromSeconds(20));

        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            .ShouldBe(TimeSpan.FromSeconds(30)); // drain + the 10s default margin
    }

    [Fact]
    public void an_explicit_host_shutdown_timeout_wins()
    {
        using var host = Build(o =>
        {
            o.ShutdownDrainGrace = TimeSpan.FromSeconds(20);
            o.ShutdownTimeout = TimeSpan.FromMinutes(2);
        });

        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            .ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void a_shutdown_timeout_that_would_truncate_the_drain_is_rejected()
    {
        var ex = Should.Throw<InvalidOperationException>(() => Build(o =>
        {
            o.ShutdownDrainGrace = TimeSpan.FromSeconds(20);
            o.ShutdownTimeout = TimeSpan.FromSeconds(5);
        }));

        ex.Message.ShouldContain(nameof(RippleEngineOptions.ShutdownDrainGrace));
    }

    /// <summary>
    /// A creation-side host (the sample's WebAPI) fans waves out and migrates, but must not claim: none of the
    /// engine's loops may be registered. The handler registry still is — the dashboard's settings page reads it.
    /// </summary>
    [Fact]
    public void enable_workers_false_registers_the_stores_but_none_of_the_engine_loops()
    {
        using var host = Build(o => o.EnableWorkers = false);

        host.Services.GetRequiredService<IEngineStore>().ShouldNotBeNull();
        host.Services.GetRequiredService<ISplashStore>().ShouldNotBeNull();
        host.Services.GetRequiredService<IReportStore>().ShouldNotBeNull();
        host.Services.GetRequiredService<ICollectionWaveGenerator>().ShouldNotBeNull();
        host.Services.GetRequiredService<RippleHandlerRegistry>().ShouldNotBeNull();

        HostedServiceNames(host).ShouldBe(["RippleMigrationService"]);
    }

    [Fact]
    public void the_migration_runs_before_the_schedule_seeder()
    {
        using var host = Build();

        var names = HostedServiceNames(host);
        names.ShouldContain("RippleMigrationService");
        names.ShouldContain("ScheduleSeeder");
        // Hosted services start in registration order, and the seeder writes to a table the migration creates.
        names.IndexOf("RippleMigrationService").ShouldBeLessThan(names.IndexOf("ScheduleSeeder"));
    }

    [Fact]
    public void auto_migrate_off_registers_no_migration_service()
    {
        using var host = Build(o => o.AutoMigrate = false);

        HostedServiceNames(host).ShouldNotContain("RippleMigrationService");
    }

    [Fact]
    public void retention_reaches_the_storage_options()
    {
        using var host = Build(o =>
        {
            o.DefaultRetention = TimeSpan.FromDays(7);
            o.RetentionByWaveType["CorporateTaxChange"] = TimeSpan.FromDays(90);
        });

        var storage = host.Services.GetRequiredService<IOptions<RippleOptions>>().Value;
        storage.RetentionFor("CorporateTaxChange").ShouldBe(TimeSpan.FromDays(90));
        storage.RetentionFor("anything-else").ShouldBe(TimeSpan.FromDays(7));
    }

    /// <summary>
    /// The setup options are a composition-time object; their engine half is copied onto the registered
    /// <c>IOptions&lt;RippleEngineOptions&gt;</c> by hand. This walks every property so a knob added to
    /// <see cref="RippleEngineOptions"/> but forgotten in <c>CopyEngineOptionsTo</c> fails here instead of
    /// silently ignoring whatever the caller set.
    /// </summary>
    [Fact]
    public void every_engine_option_reaches_the_engine()
    {
        var knobs = typeof(RippleEngineOptions).GetProperties()
            .Where(p => p is { CanRead: true, CanWrite: true })
            .ToList();
        knobs.ShouldNotBeEmpty();

        var expected = new Dictionary<string, object?>();
        using var host = Build(o =>
        {
            foreach (var knob in knobs)
            {
                var value = Mutate(knob.GetValue(o));
                knob.SetValue(o, value);
                expected[knob.Name] = value;
            }
        });

        var engineOptions = host.Services.GetRequiredService<IOptions<RippleEngineOptions>>().Value;
        foreach (var knob in knobs)
        {
            knob.GetValue(engineOptions).ShouldBe(expected[knob.Name],
                $"{knob.Name} never reaches the engine — add it to {nameof(RippleEngineOptions)}.CopyEngineOptionsTo");
        }
    }

    // A deterministic "something else, but still valid": every knob is a positive count or duration, so nudging
    // it upwards keeps the options-validation rules satisfied.
    private static object? Mutate(object? current) => current switch
    {
        TimeSpan t => t + TimeSpan.FromSeconds(1),
        int i => i + 7,
        string s => s + "-mutated",
        _ => throw new NotSupportedException(
            $"Unhandled option type {current?.GetType().Name ?? "null"} — extend Mutate.")
    };

    private static List<string> HostedServiceNames(IHost host) =>
        host.Services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToList();

    private static IHost Build(Action<RippleSetupOptions>? configure = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddLogging();
        builder.AddRippleEngine(o =>
        {
            o.ConnectionString = Connection;
            configure?.Invoke(o);
        });
        return builder.Build();
    }
}
