using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Engine;

/// <summary>Fluent registration for the engine's ripple handlers.</summary>
public interface IRippleEngineBuilder
{
    IServiceCollection Services { get; }

    /// <summary>
    /// Registers a developer handler for a <c>(wave, ripple)</c> pair, keyed by the composite
    /// <c>type_key</c> (<c>"{typeof(TWave).Name}|{typeof(TRipple).Name}"</c>) stamped on the ripple at
    /// fan-out time. The type is left <b>unconfigured</b> — the fan-out uses the engine's default batch size
    /// and gap for it (see the overload to set them).
    /// </summary>
    IRippleEngineBuilder AddHandler<TWave, TRipple, THandler>()
        where THandler : class, IRippleHandler<TWave, TRipple>
        where TRipple : IRippleTarget;

    /// <summary>
    /// Registers a handler and seeds this <c>(wave, ripple)</c> type's config into <c>ripple.type_schedule</c>
    /// at startup: <paramref name="batchSize"/> is how many consecutive ripples of a job share one
    /// <c>schedule_order</c> slot, <paramref name="gapSeconds"/> is the spacing between a job's batches, and
    /// <paramref name="maxAttempts"/> (optional) is this type's retry ceiling before a failure becomes terminal
    /// — null keeps the engine default. A job's steady-state share of the global schedule is
    /// ~ <c>batchSize / gapSeconds</c>; smaller batches / larger gaps spread a job more evenly so competing
    /// jobs interleave sooner.
    /// </summary>
    IRippleEngineBuilder AddHandler<TWave, TRipple, THandler>(int batchSize, double gapSeconds = 1,
        int? maxAttempts = null)
        where THandler : class, IRippleHandler<TWave, TRipple>
        where TRipple : IRippleTarget;
}

public static class RippleEngineExtensions
{
    /// <summary>
    /// Registers the in-process engine: the execution pipeline plus the schedule seeder, dispatcher
    /// (heartbeat + schedule_order claim), recovery, and stats-refresh hosted services. Requires
    /// <c>AddRippleStorage</c>. Chain <c>AddHandler</c> to register the developer handlers and their per-type
    /// scheduling config.
    /// </summary>
    public static IRippleEngineBuilder AddRippleEngine(this IServiceCollection services,
        Action<RippleEngineOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<RippleEngineOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // Fail at startup, not silently forever. Several of these feed raw SQL or loop arithmetic where a
        // nonsense value degrades into an invisible, permanent outage rather than an error — most sharply
        // ReportChunkSize, which is the divisor in compact_wave's chunking: at 0 every compaction raises
        // "division by zero", CompactionLoop just logs and retries on its interval, and ripple/splash rows are
        // never reclaimed, so a single config typo turns into unbounded table growth.
        optionsBuilder.Validate(o => o.MaxConcurrency > 0, $"{nameof(RippleEngineOptions.MaxConcurrency)} must be > 0");
        optionsBuilder.Validate(o => o.PrefetchFactor > 0, $"{nameof(RippleEngineOptions.PrefetchFactor)} must be > 0");
        optionsBuilder.Validate(o => o.ClaimBatchSize > 0, $"{nameof(RippleEngineOptions.ClaimBatchSize)} must be > 0");
        optionsBuilder.Validate(o => o.ReportChunkSize > 0, $"{nameof(RippleEngineOptions.ReportChunkSize)} must be > 0");
        optionsBuilder.Validate(o => o.SucceededBatchSize > 0, $"{nameof(RippleEngineOptions.SucceededBatchSize)} must be > 0");
        optionsBuilder.Validate(o => o.FailedBatchSize > 0, $"{nameof(RippleEngineOptions.FailedBatchSize)} must be > 0");
        optionsBuilder.Validate(o => o.CompactionMaxWavesPerPass > 0,
            $"{nameof(RippleEngineOptions.CompactionMaxWavesPerPass)} must be > 0");
        optionsBuilder.Validate(o => o.PauseReconcileChunkSize > 0,
            $"{nameof(RippleEngineOptions.PauseReconcileChunkSize)} must be > 0");
        optionsBuilder.Validate(o => o.PauseReconcileMaxRowsPerPass > 0,
            $"{nameof(RippleEngineOptions.PauseReconcileMaxRowsPerPass)} must be > 0");
        optionsBuilder.Validate(o => o.ExecutionTimeout > TimeSpan.Zero,
            $"{nameof(RippleEngineOptions.ExecutionTimeout)} must be positive");
        optionsBuilder.Validate(o => o.ShutdownDrainGrace > TimeSpan.Zero,
            $"{nameof(RippleEngineOptions.ShutdownDrainGrace)} must be positive");
        optionsBuilder.Validate(o => o.HeartbeatTimeout > o.HeartbeatInterval,
            $"{nameof(RippleEngineOptions.HeartbeatTimeout)} must exceed {nameof(RippleEngineOptions.HeartbeatInterval)}, "
            + "or live instances are continually mistaken for dead and have their work recovered from under them");
        optionsBuilder.ValidateOnStart();

        var registry = new RippleHandlerRegistry();
        services.AddSingleton(registry);
        services.AddSingleton<RippleMetrics>();
        services.AddSingleton<ExecutionPipeline>();

        // Seed each registered type's batch/gap before the dispatcher starts claiming.
        services.AddHostedService<ScheduleSeeder>();
        // The dispatcher owns the heartbeat now (it rides on the poll), so there is no separate beat loop.
        services.AddHostedService<Dispatcher>();
        services.AddHostedService<RecoveryLoop>();
        // The only writer of the wave's live numbers / decider of wave completion.
        services.AddHostedService<WaveStatsRefreshLoop>();
        // Rolls finished waves into the aggregated report and reclaims their ripple/splash rows.
        services.AddHostedService<CompactionLoop>();
        // Reconciles ripple states toward each type's desired pause_state (async, chunked pause/resume).
        services.AddHostedService<PauseTransitionLoop>();

        return new RippleEngineBuilder(services, registry);
    }

    private sealed class RippleEngineBuilder(IServiceCollection services, RippleHandlerRegistry registry)
        : IRippleEngineBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IRippleEngineBuilder AddHandler<TWave, TRipple, THandler>()
            where THandler : class, IRippleHandler<TWave, TRipple>
            where TRipple : IRippleTarget
        {
            Services.AddScoped<THandler>();
            registry.Register<THandler, TWave, TRipple>(batchSize: null, gapSeconds: 1, maxAttempts: null);
            return this;
        }

        public IRippleEngineBuilder AddHandler<TWave, TRipple, THandler>(int batchSize, double gapSeconds = 1,
            int? maxAttempts = null)
            where THandler : class, IRippleHandler<TWave, TRipple>
            where TRipple : IRippleTarget
        {
            Services.AddScoped<THandler>();
            registry.Register<THandler, TWave, TRipple>(batchSize, gapSeconds, maxAttempts);
            return this;
        }
    }
}
