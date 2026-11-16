using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
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
        using var host = Build(o =>
        {
            o.ShutdownDrainGrace = TimeSpan.FromSeconds(20);
            o.HandlerCancellationGrace = TimeSpan.FromSeconds(3);
        });

        // drain + the grace a cancelled handler gets before it is abandoned + the 10s default margin
        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            .ShouldBe(TimeSpan.FromSeconds(33));
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

    /// <summary>
    /// The typed override must land under the key compaction actually looks up. Every generator stamps a wave's
    /// <c>type</c> as <c>typeof(TWave).Name</c> and <c>ReportStore</c> resolves the retention with
    /// <c>RetentionFor(wave.Type)</c> — this asserts both ends meet, which is the whole reason
    /// <c>Retention&lt;TWave&gt;()</c> can replace a hand-written key.
    /// </summary>
    [Fact]
    public void typed_retention_lands_under_the_wave_type_compaction_looks_up()
    {
        using var host = Build(o =>
        {
            o.DefaultRetention = TimeSpan.FromDays(7);
            o.Retention<CorporateTaxChange>(TimeSpan.FromDays(90));
        });

        var storage = host.Services.GetRequiredService<IOptions<RippleOptions>>().Value;
        storage.RetentionFor(nameof(CorporateTaxChange)).ShouldBe(TimeSpan.FromDays(90));
        storage.RetentionFor("anything-else").ShouldBe(TimeSpan.FromDays(7));
    }

    /// <summary>Keeping a type forever is the only way to exempt it from a default that would purge it.</summary>
    [Fact]
    public void retention_forever_beats_the_default()
    {
        using var host = Build(o =>
        {
            o.DefaultRetention = TimeSpan.FromDays(7);
            o.RetentionForever<CorporateTaxChange>();
        });

        host.Services.GetRequiredService<IOptions<RippleOptions>>().Value
            .RetentionFor(nameof(CorporateTaxChange)).ShouldBeNull();
    }

    /// <summary>The by-name escape hatch (for a wave whose type is not a payload type name) must be the same
    /// mechanism, not a parallel one.</summary>
    [Fact]
    public void the_string_overload_agrees_with_the_typed_one()
    {
        using var host = Build(o => o.Retention("CorporateTaxChange", TimeSpan.FromDays(90)));

        host.Services.GetRequiredService<IOptions<RippleOptions>>().Value
            .RetentionFor(nameof(CorporateTaxChange)).ShouldBe(TimeSpan.FromDays(90));
    }

    /// <summary>A zero/negative retention would expire every wave of the type the moment it compacts — a
    /// silent data-loss setting, so it is refused where it is written.</summary>
    [Fact]
    public void a_non_positive_retention_is_refused()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(
            () => Build(o => o.Retention<CorporateTaxChange>(TimeSpan.Zero)));

        ex.Message.ShouldContain(nameof(CorporateTaxChange));
        ex.Message.ShouldContain(nameof(RippleSetupOptions.RetentionForever));
    }

    /// <summary>A wave payload type, standing in for the caller's own — only its NAME matters here.</summary>
    private sealed class CorporateTaxChange
    {
        public decimal Rate { get; set; }
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

    /// <summary>Every startup rule, one at a time: each nonsense value is refused naming the option, instead of
    /// degrading into a silent runtime failure (a division by zero in compaction, a hot error loop, a cluster that
    /// keeps declaring live instances dead).</summary>
    [Theory]
    [InlineData(nameof(RippleEngineOptions.MaxConcurrency))]
    [InlineData(nameof(RippleEngineOptions.PrefetchFactor))]
    [InlineData(nameof(RippleEngineOptions.ClaimBatchSize))]
    [InlineData(nameof(RippleEngineOptions.ReportChunkSize))]
    [InlineData(nameof(RippleEngineOptions.SucceededBatchSize))]
    [InlineData(nameof(RippleEngineOptions.FailedBatchSize))]
    [InlineData(nameof(RippleEngineOptions.CompactionMaxWavesPerPass))]
    [InlineData(nameof(RippleEngineOptions.PauseReconcileChunkSize))]
    [InlineData(nameof(RippleEngineOptions.PauseReconcileMaxRowsPerPass))]
    [InlineData(nameof(RippleEngineOptions.ExecutionTimeout))]
    [InlineData(nameof(RippleEngineOptions.DispatchErrorDelay))]
    [InlineData(nameof(RippleEngineOptions.ShutdownDrainGrace))]
    [InlineData(nameof(RippleEngineOptions.HeartbeatTimeout))] // not above HeartbeatInterval
    public void a_nonsense_engine_option_is_refused_at_startup(string option)
    {
        var property = typeof(RippleEngineOptions).GetProperty(option)!;
        using var host = Build(o => property.SetValue(o, property.PropertyType == typeof(int) ? 0 : TimeSpan.Zero));

        var ex = Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<RippleEngineOptions>>().Value);
        ex.Message.ShouldContain(option);
    }

    [Fact]
    public void the_default_engine_options_are_valid()
    {
        using var host = Build();

        Should.NotThrow(() => host.Services.GetRequiredService<IOptions<RippleEngineOptions>>().Value);
    }

    /// <summary><c>EnableMetrics</c> / <c>EnableTracing</c> each wire their own OpenTelemetry pipeline, subscribed to
    /// the engine's meter / activity source BY NAME — so a renamed meter or source can't silently export nothing —
    /// and neither switch drags in the other's pipeline.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void the_telemetry_switches_subscribe_opentelemetry_to_the_engine_by_name(bool metrics, bool tracing)
    {
        // The SDK only enables instruments for a provider that has a reader; with no OTLP endpoint there is none,
        // so attach a collecting one (additive, as a host's own exporter would be) to observe what gets exported.
        var exported = new CollectingMetricExporter();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddLogging();
        builder.AddRippleEngine(o =>
        {
            o.ConnectionString = Connection;
            o.EnableMetrics = metrics;
            o.EnableTracing = tracing;
        });
        if (metrics)
        {
            builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddReader(new BaseExportingMetricReader(exported)));
        }

        using var host = builder.Build();

        var meterProvider = host.Services.GetService<MeterProvider>();
        var tracerProvider = host.Services.GetService<TracerProvider>();
        (meterProvider is not null).ShouldBe(metrics, "a meter provider exactly when metrics are enabled");
        (tracerProvider is not null).ShouldBe(tracing, "a tracer provider exactly when tracing is enabled");

        if (metrics)
        {
            // A real engine instrument, recorded after the provider started, comes out the other end.
            using var engineMetrics = new RippleMetrics();
            engineMetrics.Recovered(2, "dead_instance");
            meterProvider!.ForceFlush().ShouldBeTrue();
            exported.Names.ShouldContain("ripple.recovered");
        }

        if (tracing)
        {
            RippleDiagnostics.Source.HasListeners().ShouldBeTrue();
        }
    }

    /// <summary>The OTLP exporter is added only when an endpoint is configured (Aspire injects one), so a
    /// standalone run doesn't start an exporter against a collector that isn't there.</summary>
    [Fact]
    public void the_otlp_exporter_is_added_only_when_an_endpoint_is_configured()
    {
        static int OtlpRegistrations(string? endpoint)
        {
            var builder = Host.CreateEmptyApplicationBuilder(null);
            if (endpoint is not null)
            {
                builder.Configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint });
            }

            builder.AddRippleEngine(o =>
            {
                o.ConnectionString = Connection;
                o.EnableMetrics = true;
            });
            return builder.Services.Count(d =>
                (d.ServiceType.FullName ?? "").Contains("Otlp", StringComparison.Ordinal)
                || (d.ImplementationType?.FullName ?? "").Contains("Otlp", StringComparison.Ordinal));
        }

        OtlpRegistrations(endpoint: null).ShouldBe(0);
        OtlpRegistrations(endpoint: "   ").ShouldBe(0, "a blank endpoint counts as none");
        OtlpRegistrations(endpoint: "http://localhost:4317").ShouldBeGreaterThan(0);
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

    private sealed class CollectingMetricExporter : BaseExporter<Metric>
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Names { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                Names.Add(metric.Name);
            }

            return ExportResult.Success;
        }
    }

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
