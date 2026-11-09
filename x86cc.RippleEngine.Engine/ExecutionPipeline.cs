using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The TPL Dataflow execution stage. Claimed ripples are posted into a single bounded
/// <see cref="ActionBlock{T}"/> whose <c>MaxDegreeOfParallelism</c> is this instance's ripple cap
/// (<see cref="RippleEngineOptions.MaxConcurrency"/>). Each execution resolves the handler by the ripple's
/// payload type, runs it inside a DI scope with a timeout, and hands the outcome to the
/// <see cref="SettlementWriter"/> (which writes the splashes in batches). No lanes, no weighting — the global FIFO
/// ordering is decided by the claim query, not here.
/// </summary>
/// <remarks>
/// The pipeline owns execution and the shutdown sequence; the rest is delegated: which attempts this instance owns
/// (<see cref="InFlightTracker"/>), whether a failure retries (<see cref="IRetryPolicy"/>), and getting outcomes
/// durably written (<see cref="SettlementWriter"/>).
/// </remarks>
internal sealed class ExecutionPipeline(
    IOptions<RippleEngineOptions> options,
    RippleHandlerRegistry registry,
    IServiceScopeFactory scopeFactory,
    InFlightTracker inFlight,
    IRetryPolicy retryPolicy,
    SettlementWriter settlement,
    RippleMetrics metrics,
    TimeProvider time,
    ILogger<ExecutionPipeline> logger)
{
    private readonly RippleEngineOptions _options = options.Value;

    // Null until Start — the pipeline is one-shot (Start once, StopAsync once), see Lifecycle.
    private ActionBlock<PreparedRipple>? _execute;
    private Lifecycle _lifecycle;

    // The pipeline's own shutdown clock, deliberately independent of the host's stopping token — nothing here
    // observes that token. The host cancels it BEFORE the dispatcher's finally reaches StopAsync(), so anything
    // tied to it dies at the START of shutdown rather than getting the drain it was promised:
    //   - handlers would surface OperationCanceledException, be caught below as a failed attempt with `attempt`
    //     already spent, and at MaxAttempts be written TERMINALLY Failed — a rolling restart silently faulting
    //     work that never actually failed;
    //   - settlement retries (SettlementWriter) would give up on their first failure, dropping outcomes for
    //     ripples that had just run successfully.
    // StopAsync arms this with CancelAfter(ShutdownDrainGrace), so both stages get the drain and one deadline
    // bounds the whole shutdown.
    private readonly CancellationTokenSource _drainCts = new(Timeout.InfiniteTimeSpan, time);

    /// <summary>Attempts owned right now — executing, queued, or awaiting settlement (see <see cref="InFlightTracker"/>).</summary>
    public int ExecutingCount => inFlight.Count;

    /// <summary>True once the execute block has faulted (its action threw and escaped) — the instance is alive
    /// but can no longer run anything, so it must fail fast. Null-safe before <see cref="Start"/>.</summary>
    public bool IsFaulted => _execute is { } b && b.Completion.IsFaulted;

    /// <summary>The exception that faulted the block, if any (for the fail-fast log).</summary>
    public Exception? FaultException => _execute is { Completion.IsFaulted: true } b ? b.Completion.Exception : null;

    /// <summary>The pipeline depth — executing + prefetched-and-queued + awaiting-settlement — the dispatcher fills.</summary>
    private int PipelineDepth => _options.MaxConcurrency * Math.Max(1, _options.PrefetchFactor);

    /// <summary>
    /// Free capacity right now (how many more ripples this instance can accept). Counts executing, queued, and
    /// awaiting-settlement ripples against <see cref="PipelineDepth"/> — so the dispatcher keeps the execute
    /// block fed with a prefetch buffer, yet claims still pause when a stalled settlement fills the depth.
    /// </summary>
    public int AvailableCapacity => Math.Max(0, PipelineDepth - inFlight.Count);

    // ---- lifecycle -------------------------------------------------------------------------------

    /// <summary>Builds the execute block and starts settlement. Once only: a second call throws rather than
    /// replacing the block, which would orphan everything queued in the first and start a second pair of flush
    /// loops on the same channels.</summary>
    public void Start()
    {
        _lifecycle.Start(nameof(ExecutionPipeline));
        metrics.SetExecutingProvider(() => ExecutingCount, _options.InstanceId);
        // MDOP = the execution parallelism; BoundedCapacity = the deeper pipeline, so prefetched ripples queue
        // in front of the block and start the instant a slot frees (no starvation between polls/settlements).
        _execute = new ActionBlock<PreparedRipple>(ExecuteAsync, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = _options.MaxConcurrency,
            BoundedCapacity = PipelineDepth
        });

        settlement.Start(_drainCts.Token);
    }

    /// <summary>
    /// Hands a claimed ripple to the execute block. The caller must have reserved capacity via
    /// <see cref="AvailableCapacity"/>, so the post always succeeds; returns false only if the block is
    /// already completing (shutdown), in which case the ripple stays Running and recovery re-claims it.
    /// </summary>
    public bool Post(PreparedRipple ripple)
    {
        var execute = _execute ?? throw Lifecycle.NotStarted(nameof(ExecutionPipeline), "accept ripples");
        inFlight.Track(ripple.RippleId, ripple.Attempt);
        if (execute.Post(ripple))
        {
            return true;
        }

        // Refused (block completing or faulted): drop it from the owned set so self-recovery will release the
        // DB row rather than treating us as its live owner.
        inFlight.Release([(ripple.RippleId, ripple.Attempt)]);
        return false;
    }

    /// <summary>Drains and settles everything in flight, bounded by <see cref="RippleEngineOptions.ShutdownDrainGrace"/>.
    /// Requires <see cref="Start"/>, and runs once: the drain clock is disposed at the end.</summary>
    public async Task StopAsync()
    {
        _lifecycle.Stop(nameof(ExecutionPipeline));
        var execute = _execute!; // Stop succeeded, so Start ran

        // ONE timer bounds the whole shutdown. Both stages observe _drainCts — the handlers (so in-flight
        // ripples finish instead of being cancelled) and the settlement retry backoff (so outcomes produced
        // during the drain still get written through a transient DB failure). Capping both with a single
        // deadline is what keeps total shutdown under the host's ShutdownTimeout: budgeting them separately
        // would let a slow drain plus a slow settle add up past it and get the process hard-killed mid-write.
        _drainCts.CancelAfter(_options.ShutdownDrainGrace);

        execute.Complete();
        try { await execute.Completion; } catch { /* faulted block — outcomes already recorded per-item */ }

        // The block has drained, so nothing produces another outcome: settle what the handlers just produced.
        await settlement.CompleteAsync();

        if (_drainCts.IsCancellationRequested)
        {
            // The grace ran out rather than the work finishing: handlers were cancelled and/or settlement
            // retries gave up. Anything left Running is recovered once this instance's heartbeat goes stale.
            logger.LogWarning("Drain grace of {Grace} expired during shutdown; {Count} ripple(s) left unsettled",
                _options.ShutdownDrainGrace, ExecutingCount);
        }

        _drainCts.Dispose();
    }

    // ---- the shared execute block ----------------------------------------------------------------

    private async Task ExecuteAsync(PreparedRipple ripple)
    {
        var startedAt = time.GetUtcNow();
        // Linked to the DRAIN token, not the stop token — see _drainCts. ExecutionTimeout still bounds every
        // individual handler; shutdown only intervenes once the drain grace has run out.
        // Two sources rather than CreateLinkedTokenSource + CancelAfter: a linked source's timer always runs on
        // the system clock, and both deadlines must follow the injected TimeProvider.
        using var deadlineCts = new CancellationTokenSource(_options.ExecutionTimeout, time);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_drainCts.Token, deadlineCts.Token);

        // The context carries the wave + this ripple's id so a handler can expand the wave (spawn child
        // ripples parented to this one) via a generator's Continue(context). LOAD-BEARING ORDERING: a handler's
        // Continue commits its child ripples (a self-contained transaction, during Run() below) STRICTLY BEFORE
        // this ripple is settled out of 'Running' (settlement happens later, via SettlementWriter, after Run()
        // returns). So this parent stays Running throughout the expansion — refresh_wave_stats() can never see the
        // wave drained mid-expansion, and completion/compaction can't race the newly-added work. This is why
        // ripples may only be added to an existing wave via Continue(context): a Running parent guarantees the
        // wave isn't (and can't be observed) complete at that point.
        var context = new RippleContext(ripple.WaveId, ripple.RippleId, ripple.Attempt, ripple.MaxAttempts, timeoutCts.Token);

        // Resolve the per-attempt report + whether the attempt failed. A throw (or a returned Failed group) =
        // failed; a normal return with no Failed group = succeeded, with unreported targets inferred succeeded.
        string? report;
        bool failed;
        string[] targetIds = [];
        try
        {
            if (!registry.TryGet(ripple.TypeKey, out var prepare))
            {
                throw new InvalidOperationException($"No handler registered for type key '{ripple.TypeKey}'.");
            }

            using var scope = scopeFactory.CreateScope();
            var invocation = prepare(scope.ServiceProvider, ripple.WavePayload, ripple.Payload, context);
            targetIds = invocation.TargetIds;
            var returned = await invocation.Run();
            (report, failed) = SplashReportBuilder.Resolve(targetIds, returned);
        }
        catch (Exception ex)
        {
            // Terminated outside the handler: every target gets the same Failed outcome + the exception message.
            report = SplashReportBuilder.Failed(targetIds, Flatten(ex));
            failed = true;
        }
        finally
        {
            // Return the payloads' pooled buffers. Safe here on every path: the registry deserialized both into
            // POCOs before Run(), so nothing downstream still reads the documents. The outcome written below
            // carries only the already-serialized report string.
            ripple.Dispose();
        }

        // The attempt is over HERE — one sample, carried on the outcome and reused for the metrics histogram, so
        // the persisted duration_ms and the exported latency measure the same thing. Settlement is asynchronous
        // (batching + retry backoff), so the store can't derive this: sampling it at write time would fold that
        // wait into every recorded execution time.
        var endedAt = time.GetUtcNow();
        var elapsedMs = (endedAt - startedAt).TotalMilliseconds;

        if (!failed)
        {
            settlement.Succeeded(new RippleCompletion(ripple.RippleId, ripple.WaveId, ripple.Attempt, startedAt, report, endedAt));
            metrics.Succeeded(elapsedMs, ripple.TypeKey);
        }
        else
        {
            DateTimeOffset? nextAttemptAt = retryPolicy.NextDelay(ripple.Attempt, ripple.MaxAttempts) is { } delay
                ? endedAt + delay
                : null;
            var terminal = nextAttemptAt is null;
            settlement.Failed(new RippleFailure(ripple.RippleId, ripple.WaveId, ripple.Attempt, startedAt, report, terminal, nextAttemptAt, endedAt));
            metrics.Failed(elapsedMs, ripple.TypeKey);
        }

        // NB: the in-flight slot is NOT released here — the ripple stays "owned" until its outcome is durably
        // written (SettlementWriter releases it on a successful write). This is what turns a settlement
        // backlog into claim back-pressure.
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (var e = error; e is not null; e = e.InnerException)
        {
            messages.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" -> ", messages);
    }
}
