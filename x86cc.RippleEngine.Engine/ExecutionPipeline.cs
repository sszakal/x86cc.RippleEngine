using System.Collections.Concurrent;
using System.Threading.Channels;
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
/// payload type, runs it inside a DI scope with a timeout, and routes the outcome to a succeeded/failed
/// channel; batched flush loops write the splashes, one DB round trip per batch. No lanes, no weighting — the
/// global FIFO ordering is decided by the claim query, not here.
/// </summary>
internal sealed class ExecutionPipeline(
    IOptions<RippleEngineOptions> options,
    RippleHandlerRegistry registry,
    IServiceScopeFactory scopeFactory,
    ISplashStore splashStore,
    RippleMetrics metrics,
    ILogger<ExecutionPipeline> logger)
{
    private readonly RippleEngineOptions _options = options.Value;

    private readonly Channel<RippleCompletion> _succeeded = Channel.CreateUnbounded<RippleCompletion>();
    private readonly Channel<RippleFailure> _failed = Channel.CreateUnbounded<RippleFailure>();

    private ActionBlock<PreparedRipple> _execute = default!;
    private Task[] _loops = [];

    // The pipeline's own shutdown clock, deliberately independent of the host's stopping token — nothing here
    // observes that token. The host cancels it BEFORE the dispatcher's finally reaches StopAsync(), so anything
    // tied to it dies at the START of shutdown rather than getting the drain it was promised:
    //   - handlers would surface OperationCanceledException, be caught below as a failed attempt with `attempt`
    //     already spent, and at MaxAttempts be written TERMINALLY Failed — a rolling restart silently faulting
    //     work that never actually failed;
    //   - settlement retries (WriteWithRetryAsync) would give up on their first failure, dropping outcomes for
    //     ripples that had just run successfully.
    // StopAsync arms this with CancelAfter(ShutdownDrainGrace), so both stages get the drain and one deadline
    // bounds the whole shutdown.
    private readonly CancellationTokenSource _drainCts = new();

    // Ripples this instance owns: posted and not yet <b>durably settled</b> (executed AND their outcome
    // written). Held until settlement — not just until execution finishes — so a stalled settlement (see
    // WriteWithRetryAsync) keeps the count high, shrinks the capacity the dispatcher claims against to zero,
    // and stops it pulling more work. That also implicitly bounds the settlement channels: at most
    // MaxConcurrency outcomes can be awaiting a write at once.
    private int _inFlight;

    // The attempts currently owned in-process (posted, not yet durably settled) — the ground truth the recovery
    // loop reconciles against: a ripple the DB says is Running & claimed by us but which is NOT in here has been
    // stranded (never reached a handler, or the block faulted), and self-recovery releases it.
    //
    // Keyed on (ripple id, ATTEMPT), not on ripple id. This instance can genuinely own two attempts of the same
    // ripple at once: it stalls past HeartbeatTimeout while still inside ExecutionTimeout, a peer requeues the
    // ripple, and it comes back under the same InstanceId and re-claims it. Keyed by id alone, attempt 2's Post
    // overwrote attempt 1's entry and then attempt 1's settlement — which correctly no-ops against the store's
    // attempt fence, but is still a completed write from the flush loop's point of view — REMOVED the key
    // belonging to the live attempt 2. Self-recovery would then see a Running row it believes nothing is
    // executing and requeue a ripple whose handler was still running, re-running it and burning an attempt.
    private readonly ConcurrentDictionary<(Guid RippleId, int Attempt), byte> _inFlightIds = new();

    public int ExecutingCount => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Snapshot of the ripple ids this instance is actually executing/settling right now. Distinct: the DB row
    /// has one current attempt, so an id must be reported as owned while ANY of its attempts is still live here.
    /// </summary>
    public IReadOnlyCollection<Guid> InFlightIds => _inFlightIds.Keys.Select(k => k.RippleId).Distinct().ToArray();

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
    public int AvailableCapacity => Math.Max(0, PipelineDepth - Volatile.Read(ref _inFlight));

    // ---- lifecycle -------------------------------------------------------------------------------

    public void Start()
    {
        metrics.SetExecutingProvider(() => ExecutingCount, _options.InstanceId);
        // MDOP = the execution parallelism; BoundedCapacity = the deeper pipeline, so prefetched ripples queue
        // in front of the block and start the instant a slot frees (no starvation between polls/settlements).
        _execute = new ActionBlock<PreparedRipple>(ExecuteAsync, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = _options.MaxConcurrency,
            BoundedCapacity = PipelineDepth
        });

        _loops =
        [
            Task.Run(() => FlushLoopAsync(_succeeded.Reader, _options.SucceededBatchSize,
                (b, c) => splashStore.CompleteRipplesAsync(b, _options.InstanceId, c),
                r => (r.RippleId, r.Attempt)), CancellationToken.None),
            Task.Run(() => FlushLoopAsync(_failed.Reader, _options.FailedBatchSize,
                (b, c) => splashStore.FailRipplesAsync(b, _options.InstanceId, c),
                r => (r.RippleId, r.Attempt)), CancellationToken.None)
        ];
    }

    /// <summary>
    /// Hands a claimed ripple to the execute block. The caller must have reserved capacity via
    /// <see cref="AvailableCapacity"/>, so the post always succeeds; returns false only if the block is
    /// already completing (shutdown), in which case the ripple stays Running and recovery re-claims it.
    /// </summary>
    public bool Post(PreparedRipple ripple)
    {
        _inFlightIds[(ripple.RippleId, ripple.Attempt)] = 0;
        Interlocked.Increment(ref _inFlight);
        if (_execute.Post(ripple))
        {
            return true;
        }

        // Refused (block completing or faulted): drop it from the owned set so self-recovery will release the
        // DB row rather than treating us as its live owner.
        _inFlightIds.TryRemove((ripple.RippleId, ripple.Attempt), out _);
        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    public async Task StopAsync()
    {
        // ONE timer bounds the whole shutdown. Both stages observe _drainCts — the handlers (so in-flight
        // ripples finish instead of being cancelled) and the settlement retry backoff (so outcomes produced
        // during the drain still get written through a transient DB failure). Capping both with a single
        // deadline is what keeps total shutdown under the host's ShutdownTimeout: budgeting them separately
        // would let a slow drain plus a slow settle add up past it and get the process hard-killed mid-write.
        _drainCts.CancelAfter(_options.ShutdownDrainGrace);

        _execute.Complete();
        try { await _execute.Completion; } catch { /* faulted block — outcomes already recorded per-item */ }

        // Close the channels so the flush loops drain what the handlers just produced and exit.
        _succeeded.Writer.TryComplete();
        _failed.Writer.TryComplete();
        try { await Task.WhenAll(_loops); } catch { /* logged inside loops */ }

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
        var startedAt = DateTimeOffset.UtcNow;
        // Linked to the DRAIN token, not the stop token — see _drainCts. ExecutionTimeout still bounds every
        // individual handler; shutdown only intervenes once the drain grace has run out.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_drainCts.Token);
        timeoutCts.CancelAfter(_options.ExecutionTimeout);

        // The context carries the wave + this ripple's id so a handler can expand the wave (spawn child
        // ripples parented to this one) via a generator's Continue(context). LOAD-BEARING ORDERING: a handler's
        // Continue commits its child ripples (a self-contained transaction, during Run() below) STRICTLY BEFORE
        // this ripple is settled out of 'Running' (settlement happens later, via the flush loop, after Run()
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
        var endedAt = DateTimeOffset.UtcNow;
        var elapsedMs = (endedAt - startedAt).TotalMilliseconds;

        if (!failed)
        {
            _succeeded.Writer.TryWrite(new RippleCompletion(ripple.RippleId, ripple.WaveId, ripple.Attempt, startedAt, report, endedAt));
            metrics.Succeeded(elapsedMs, ripple.TypeKey);
        }
        else
        {
            var terminal = ripple.Attempt >= ripple.MaxAttempts;
            DateTimeOffset? nextAttemptAt = terminal ? null : endedAt + Backoff(ripple.Attempt);
            _failed.Writer.TryWrite(new RippleFailure(ripple.RippleId, ripple.WaveId, ripple.Attempt, startedAt, report, terminal, nextAttemptAt, endedAt));
            metrics.Failed(elapsedMs, ripple.TypeKey);
        }

        // NB: _inFlight is NOT decremented here — the ripple stays "owned" until its outcome is durably
        // written (the flush loop decrements on a successful settlement). This is what turns a settlement
        // backlog into claim back-pressure.
    }

    private TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        var ms = Math.Min(_options.RetryBackoff.TotalMilliseconds * factor, _options.MaxRetryBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
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

    // ---- batched completion writers --------------------------------------------------------------

    private async Task FlushLoopAsync<T>(ChannelReader<T> reader, int batchSize,
        Func<IReadOnlyList<T>, CancellationToken, Task> write, Func<T, (Guid RippleId, int Attempt)> keyOf)
    {
        var batch = new List<T>(batchSize);
        // Read until the writer completes (StopAsync), independent of the stop token, so every buffered
        // outcome is settled even during shutdown.
        while (await reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < batchSize && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            // Release the owned slots only once the outcome is durably written; a stalled settlement holds
            // them, which is exactly the back-pressure that pauses claiming.
            if (await WriteWithRetryAsync(batch, write))
            {
                foreach (var item in batch)
                {
                    _inFlightIds.TryRemove(keyOf(item), out _);
                }

                Interlocked.Add(ref _inFlight, -batch.Count);
            }
        }
    }

    private async Task<bool> WriteWithRetryAsync<T>(IReadOnlyList<T> batch, Func<IReadOnlyList<T>, CancellationToken, Task> write)
    {
        var delay = _options.SettlementRetryDelay;
        while (true)
        {
            try
            {
                await write(batch, CancellationToken.None);
                return true;
            }
            catch (Exception e)
            {
                // Never drop a settled outcome while we're alive: recovery only reclaims *dead* instances,
                // so a live instance's dropped settlement would leave executed ripples stuck Running
                // forever. Retry with backoff until it lands.
                logger.LogError(e, "Ripple settlement failed for {Count} row(s); retrying", batch.Count);
                try
                {
                    // The DRAIN token, not the host's stop token. The host cancels its token BEFORE StopAsync
                    // even begins the drain, so backing off on it meant the first settlement failure of a
                    // shutdown dropped the batch with zero retries — precisely when handlers are deliberately
                    // kept running to finish cleanly, and precisely when a restart makes a transient DB error
                    // most likely. Retries now persist for the full ShutdownDrainGrace.
                    await Task.Delay(delay, _drainCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Drain grace exhausted — stop retrying. These ripples stay Running (still counted
                    // in-flight) and recovery re-runs them once this instance's heartbeat goes stale
                    // (handlers are idempotent).
                    logger.LogWarning(
                        "Shutdown with {Count} unsettled outcome(s); recovery will re-run them", batch.Count);
                    return false;
                }

                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, _options.MaxSettlementRetryDelay.TotalMilliseconds));
            }
        }
    }
}
