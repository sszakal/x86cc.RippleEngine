using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Hosting;

/// <summary>
/// Everything <c>AddRippleEngine</c> needs, in one object: the engine tunables it inherits from
/// <see cref="RippleEngineOptions"/> plus the process-level decisions that used to be separate registration
/// calls — where the database is, whether this process migrates it, whether it runs the pollers, and whether it
/// exports metrics or serves the dashboard.
/// </summary>
/// <remarks>
/// This is a <b>composition-time</b> object, not the registered <c>IOptions&lt;RippleEngineOptions&gt;</c>: the
/// engine values are copied onto that instance (see
/// <c>RippleEngineOptions.CopyEngineOptionsTo</c>) once the lambda has run, and everything else here decides
/// what gets registered at all. Changing it after <c>AddRippleEngine</c> returns has no effect.
/// </remarks>
public sealed class RippleSetupOptions : RippleEngineOptions, IRippleFeatures
{
    private readonly List<Action<IServiceCollection>> _features = [];

    /// <summary>
    /// The Postgres connection Ripple coordinates on. Defaults to the <c>ripple</c> connection string from
    /// configuration (<c>ConnectionStrings:ripple</c>), which is what the Aspire/`AddNpgsqlDataSource`
    /// convention hands a worker — set this only to point somewhere else.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Applies the <c>ripple</c> schema migrations during startup, before anything reads the schema. On by
    /// default and safe on every replica (a Postgres advisory lock serializes concurrent first runs). Turn it
    /// off when migrations are a separate release step, and run <c>IServiceProvider.MigrateRipple()</c> there.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// Runs the engine in this process: the dispatcher (heartbeat + claim), execution pipeline, recovery, stats
    /// refresh, compaction and pause reconciliation. Setting it to <c>false</c> makes this a <b>creation-side</b>
    /// host — it can create waves, fan them out and serve the dashboard, but never claims or executes a ripple
    /// (and does not seed <c>type_schedule</c> from <c>AddHandler</c>, since it registers no working handlers).
    /// </summary>
    public bool EnableWorkers { get; set; } = true;

    /// <summary>
    /// Serves the monitoring dashboard from this process: the read API under <c>/api</c> and the Angular SPA at
    /// the root. Requires an ASP.NET Core host. The SPA is mapped as a route <i>fallback</i>, so it never
    /// shadows the app's own endpoints — but an app that maps its own SPA fallback should leave this off and
    /// call <c>MapRippleDashboard()</c> where it wants the API.
    /// </summary>
    public bool EnableDashboard { get; set; }

    /// <summary>
    /// Publishes the engine's metrics — <c>ripple.claimed</c> / <c>succeeded</c> / <c>failed</c> /
    /// <c>duration</c> (tagged with <c>type_key</c>) and the per-instance <c>ripple.executing</c> gauge — by
    /// adding <see cref="RippleMetrics.MeterName"/> to OpenTelemetry, and exporting over OTLP when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured. Without that endpoint it only registers the meter, so
    /// a standalone run stays quiet and a host with its own exporter keeps it.
    /// </summary>
    public bool EnableMetrics { get; set; }

    /// <summary>
    /// The host's shutdown budget (<c>HostOptions.ShutdownTimeout</c>). Leave it null and it is derived from
    /// <see cref="RippleEngineOptions.ShutdownDrainGrace"/> — the drain plus a margin — because the framework
    /// default (5s) is shorter than the default drain and would hard-kill the process mid-drain, stranding
    /// in-flight ripples as <c>Running</c> until recovery times out. A value that does not leave room for the
    /// drain is rejected at startup rather than silently truncating it.
    /// </summary>
    public TimeSpan? ShutdownTimeout { get; set; }

    /// <summary>Margin added to <see cref="RippleEngineOptions.ShutdownDrainGrace"/> when
    /// <see cref="ShutdownTimeout"/> is left null: the room the host keeps for settling the drained outcomes and
    /// stopping the remaining hosted services.</summary>
    public TimeSpan ShutdownTimeoutMargin { get; set; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc cref="RippleOptions.DefaultRetention"/>
    public TimeSpan? DefaultRetention { get; set; }

    /// <inheritdoc cref="RippleOptions.RetentionByWaveType"/>
    public IDictionary<string, TimeSpan?> RetentionByWaveType { get; } = new Dictionary<string, TimeSpan?>();

    /// <summary>Registrations contributed by the optional packages (<c>o.UseMartenFanOut()</c>,
    /// <c>o.UseEntityFrameworkFanOut()</c>), applied in order after the storage services.</summary>
    void IRippleFeatures.AddFeature(Action<IServiceCollection> register) => _features.Add(register);

    internal IReadOnlyList<Action<IServiceCollection>> Features => _features;
}
