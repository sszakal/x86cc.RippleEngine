using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Hosting;

/// <summary>The single entry point for standing up Ripple in a host.</summary>
public static class RippleHostingExtensions
{
    /// <summary>
    /// Registers everything a Ripple process needs, in the order it needs it: the storage services on the
    /// <c>ripple</c> connection, the schema migration (ahead of anything that reads the schema), the engine's
    /// hosted services, a host shutdown budget that outlasts the engine's drain, and — on request — the
    /// dashboard and the metrics meter. Chain <c>AddHandler</c> to register the developer handlers and their
    /// per-type scheduling config.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.AddRippleEngine(o =>
    ///     {
    ///         o.MaxConcurrency  = 32;
    ///         o.EnableDashboard = true;
    ///         o.Retention&lt;TaxChange&gt;(TimeSpan.FromDays(90));
    ///     })
    ///     .AddHandler&lt;TaxChange, RecalcCompany, RecalcHandler&gt;(batchSize: 200, gapSeconds: 1);
    /// </code>
    /// </example>
    public static IRippleEngineBuilder AddRippleEngine(this IHostApplicationBuilder builder,
        Action<RippleSetupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RippleSetupOptions();
        configure?.Invoke(options);

        var connectionString = options.ConnectionString
            ?? builder.Configuration.GetConnectionString(DefaultConnectionName)
            ?? throw new InvalidOperationException(
                $"No Ripple connection string: set {nameof(RippleSetupOptions)}.{nameof(RippleSetupOptions.ConnectionString)} "
                + $"in AddRippleEngine, or configure ConnectionStrings:{DefaultConnectionName}.");

        var services = builder.Services;

        services.AddRippleStorage(connectionString, storage =>
        {
            storage.DefaultRetention = options.DefaultRetention;
            foreach (var (waveType, retention) in options.RetentionByWaveType)
            {
                storage.RetentionByWaveType[waveType] = retention;
            }
        });

        // o.UseMartenFanOut() / o.UseEntityFrameworkFanOut(): the optional packages' registrations, deferred to
        // here so they land after the storage services they depend on.
        foreach (var feature in options.Features)
        {
            feature(services);
        }

        ConfigureShutdownBudget(services, options);

        if (options.AutoMigrate)
        {
            services.AddSingleton<RippleMigrator>();
            // Registered BEFORE the engine's hosted services (below), which is what orders the migration ahead
            // of the schedule seeder: IHostedService instances start in registration order.
            services.AddHostedService<RippleMigrationService>();
        }

        if (options.AutoMigrate || options.EnableDashboard)
        {
            services.AddSingleton<IStartupFilter>(
                new RippleStartupFilter(options.AutoMigrate, options.EnableDashboard));
        }

        if (options.EnableMetrics)
        {
            AddMetrics(builder);
        }

        return services.AddRippleEngine(options.CopyEngineOptionsTo, options.EnableWorkers);
    }

    /// <summary>The configuration key <c>AddRippleEngine</c> reads the connection string from when none is set
    /// explicitly — the name Aspire and the sample use for the Ripple database.</summary>
    public const string DefaultConnectionName = "ripple";

    /// <summary>
    /// Keeps <c>HostOptions.ShutdownTimeout</c> above the engine's drain. The framework default is 5s while the
    /// drain defaults to 15s, so a host that says nothing is hard-killed mid-drain and leaves its in-flight
    /// ripples <c>Running</c> for recovery to time out on — the failure this derivation exists to prevent.
    /// </summary>
    private static void ConfigureShutdownBudget(IServiceCollection services, RippleSetupOptions options)
    {
        var shutdownTimeout = options.ShutdownTimeout ?? options.ShutdownDrainGrace + options.ShutdownTimeoutMargin;
        if (shutdownTimeout <= options.ShutdownDrainGrace)
        {
            throw new InvalidOperationException(
                $"{nameof(RippleSetupOptions.ShutdownTimeout)} ({shutdownTimeout}) must exceed "
                + $"{nameof(RippleEngineOptions.ShutdownDrainGrace)} ({options.ShutdownDrainGrace}): the host would "
                + "hard-kill the process while the engine is still draining, leaving in-flight ripples Running "
                + "until recovery reclaims them.");
        }

        services.Configure<HostOptions>(host => host.ShutdownTimeout = shutdownTimeout);
    }

    /// <summary>
    /// Publishes the engine's meter. The OTLP exporter is added only when an endpoint is configured, so a
    /// standalone run does not spend a background exporter on a collector that isn't there, and a host that
    /// already configured its own exporters keeps them (both calls are additive).
    /// </summary>
    private static void AddMetrics(IHostApplicationBuilder builder)
    {
        var otel = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter(RippleMetrics.MeterName));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }
    }
}
