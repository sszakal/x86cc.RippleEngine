namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Tunables for one engine instance. The scheduling model is deliberately flat: every instance pulls
/// pending ripples from one global queue in <c>schedule_order</c> order (the fair-share having been precomputed
/// into that key at fan-out) and runs them through a bounded execute block. The single capacity knob is
/// <see cref="MaxConcurrency"/> — the ripple cap for <b>this</b> instance; global concurrency is that cap
/// times the number of running instances.
/// </summary>
/// <remarks>
/// Not sealed: the hosting package's setup options derive from it so that the engine tunables and the
/// process-level ones (connection string, migration, dashboard, …) are one flat object in one lambda.
/// </remarks>
public class RippleEngineOptions
{
    /// <summary>A stable identity for this instance (claim ownership, heartbeat, recovery). Defaults to machine+pid+guid.</summary>
    public string InstanceId { get; set; } =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>Max ripples this instance may execute in parallel — the execute block's MaxDegreeOfParallelism.</summary>
    public int MaxConcurrency { get; set; } = 64;

    /// <summary>
    /// Pipeline depth as a multiple of <see cref="MaxConcurrency"/>: the dispatcher keeps up to
    /// <c>MaxConcurrency * PrefetchFactor</c> ripples in flight (executing + prefetched-and-queued +
    /// awaiting-settlement), while only <see cref="MaxConcurrency"/> execute at once. The extra depth is a
    /// prefetch/settlement buffer so the execute block always has queued work to start the instant a slot
    /// frees — it doesn't starve between poll cycles or while a completed batch's outcome is being written.
    /// Must be &gt;= 1 (1 = no buffer, the old behaviour).
    /// </summary>
    public int PrefetchFactor { get; set; } = 2;

    /// <summary>Max ripples the dispatcher claims from the global queue in a single poll cycle.</summary>
    public int ClaimBatchSize { get; set; } = 128;

    /// <summary>Poll cadence when work is flowing.</summary>
    public TimeSpan MinPollDelay { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Poll cadence after repeated empty polls (adaptive backoff ceiling).</summary>
    public TimeSpan MaxPollDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Max succeeded-splash rows written per DB round trip.</summary>
    public int SucceededBatchSize { get; set; } = 100;

    /// <summary>Max failed-splash rows written per DB round trip (smaller: failures want lower latency).</summary>
    public int FailedBatchSize { get; set; } = 25;

    /// <summary>Initial backoff before retrying a failed settlement write (doubles up to <see cref="MaxSettlementRetryDelay"/>).</summary>
    public TimeSpan SettlementRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling for the settlement-retry backoff.</summary>
    public TimeSpan MaxSettlementRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Hard timeout on a single ripple execution before it is cancelled and counts as a failed attempt.</summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long shutdown waits for in-flight ripples to finish on their own before cancelling them. Handlers are
    /// NOT tied to the host's stopping token (see <c>ExecutionPipeline._drainCts</c>), so within this window a
    /// restart costs nothing: work completes and settles normally instead of burning a retry attempt — or, at
    /// <c>MaxAttempts</c>, being written terminally Failed despite never having failed.
    /// </summary>
    /// <remarks>
    /// Must stay BELOW the host's <c>HostOptions.ShutdownTimeout</c>, otherwise the host hard-kills the process
    /// mid-drain and the cancel-and-settle path below never runs (rows are left Running for recovery to time out
    /// on). <c>AddRippleEngine</c> keeps the two in step for you: it derives the host timeout from this value
    /// (plus a margin) unless <c>RippleSetupOptions.ShutdownTimeout</c> says otherwise, and refuses a pair that
    /// would truncate the drain.
    /// </remarks>
    public TimeSpan ShutdownDrainGrace { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Base delay for retry backoff (doubled per attempt, capped by <see cref="MaxRetryBackoff"/>).</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often this instance writes its heartbeat.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>An instance silent longer than this is declared dead and its work recovered.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the recovery loop checks for dead instances.</summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Grace period before the recovery loop reclaims one of THIS instance's own ripples that the DB shows
    /// Running but which the execute block isn't actually running (a claim that never reached a handler, or was
    /// stranded by a race). Covers the tiny window between the DB claim and the block enqueue, so a
    /// just-claimed ripple isn't reaped before it's dispatched — set it comfortably above that gap.
    /// </summary>
    public TimeSpan SelfReconcileGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often <see cref="WaveStatsRefreshLoop"/> recomputes each wave's live numbers and settles drained waves. This
    /// is the granularity at which a wave's live pending/running/succeeded numbers and its completion become
    /// visible (the hot paths don't maintain counters). Shorter = fresher numbers, more refresh round trips.
    /// </summary>
    public TimeSpan WaveStatsRefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often <see cref="CompactionLoop"/> rolls finished waves' splashes into the aggregated report
    /// and reclaims their ripple/splash rows.</summary>
    public TimeSpan CompactionInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many terminal waves a single compaction pass processes (bounds the work per tick).</summary>
    public int CompactionMaxWavesPerPass { get; set; } = 50;

    /// <summary>Max targets per <c>report_chunk</c> — a wave's report is chunked into rows of this many targets.</summary>
    public int ReportChunkSize { get; set; } = 10_000;

    /// <summary>How often <see cref="PauseTransitionLoop"/> reconciles ripple states toward each type's desired
    /// <c>pause_state</c> (parking/un-parking work when a type is paused/resumed).</summary>
    public TimeSpan PauseReconcileInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Ripples moved per statement/transaction in the pause reconcile (bounds each UPDATE's lock/WAL).</summary>
    public int PauseReconcileChunkSize { get; set; } = 10_000;

    /// <summary>Max ripples the pause reconcile moves per pass — bounds the drain rate so a millions-row
    /// pause/resume is spread across ticks rather than done in one long-locking transaction.</summary>
    public int PauseReconcileMaxRowsPerPass { get; set; } = 200_000;

    /// <summary>
    /// Copies every tunable onto <paramref name="target"/>. The hosting package's setup options DERIVE from this
    /// class (so the engine knobs stay flat in the one configuration lambda) and are a composition-time object,
    /// not the registered <c>IOptions</c> instance — this is what carries the caller's values across.
    /// </summary>
    /// <remarks>
    /// Written out by hand rather than reflected, and deliberately exhaustive: <b>a property added above must be
    /// added here too</b>, or it silently stops reaching the engine when set through <c>AddRippleEngine</c>.
    /// <c>FacadeTests.every_engine_option_reaches_the_engine</c> fails if one is missed.
    /// </remarks>
    internal void CopyEngineOptionsTo(RippleEngineOptions target)
    {
        target.InstanceId = InstanceId;
        target.MaxConcurrency = MaxConcurrency;
        target.PrefetchFactor = PrefetchFactor;
        target.ClaimBatchSize = ClaimBatchSize;
        target.MinPollDelay = MinPollDelay;
        target.MaxPollDelay = MaxPollDelay;
        target.SucceededBatchSize = SucceededBatchSize;
        target.FailedBatchSize = FailedBatchSize;
        target.SettlementRetryDelay = SettlementRetryDelay;
        target.MaxSettlementRetryDelay = MaxSettlementRetryDelay;
        target.ExecutionTimeout = ExecutionTimeout;
        target.ShutdownDrainGrace = ShutdownDrainGrace;
        target.RetryBackoff = RetryBackoff;
        target.MaxRetryBackoff = MaxRetryBackoff;
        target.HeartbeatInterval = HeartbeatInterval;
        target.HeartbeatTimeout = HeartbeatTimeout;
        target.RecoveryInterval = RecoveryInterval;
        target.SelfReconcileGrace = SelfReconcileGrace;
        target.WaveStatsRefreshInterval = WaveStatsRefreshInterval;
        target.CompactionInterval = CompactionInterval;
        target.CompactionMaxWavesPerPass = CompactionMaxWavesPerPass;
        target.ReportChunkSize = ReportChunkSize;
        target.PauseReconcileInterval = PauseReconcileInterval;
        target.PauseReconcileChunkSize = PauseReconcileChunkSize;
        target.PauseReconcileMaxRowsPerPass = PauseReconcileMaxRowsPerPass;
    }
}
