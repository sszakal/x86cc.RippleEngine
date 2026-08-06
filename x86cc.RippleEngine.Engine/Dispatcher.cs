using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The one poller per instance. Each cycle makes a <b>single</b> DB round trip that both heartbeats and
/// claims a slice of the global pending-ripple queue (in <c>schedule_order</c> order) sized to the execute
/// block's free capacity, then posts the claimed ripples for execution. That's the whole scheduler: no
/// admission, no lanes — all instances pull from the same queue via <c>SKIP LOCKED</c>, so they take disjoint
/// work and cluster throughput scales with instance count. Fairness is precomputed into <c>schedule_order</c> at
/// fan-out, not decided here.
/// <para>
/// The heartbeat rides on the poll (polling <i>is</i> the liveness proof), but its cadence is decoupled
/// from claiming: a saturated instance (no free capacity) still beats every
/// <see cref="RippleEngineOptions.HeartbeatInterval"/> instead of hammering the DB, and if a poll throws
/// we fall back to a direct beat so a healthy node is never declared dead. Adaptive backoff keeps polling
/// tight while work flows and quiet when the queue is empty.
/// </para>
/// </summary>
internal sealed class Dispatcher(
    ExecutionPipeline pipeline,
    IEngineStore engineStore,
    RippleMetrics metrics,
    IOptions<RippleEngineOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<Dispatcher> logger) : BackgroundService
{
    private readonly RippleEngineOptions _options = options.Value;

    // When the pipeline is full, how often to re-check for a freed slot — cheap (reads an int), and short so
    // the execute block is topped up promptly rather than starving for a whole poll cadence.
    private static readonly TimeSpan PipelineFullRecheck = TimeSpan.FromMilliseconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ripple engine starting: instance {InstanceId}, {MaxConcurrency} max concurrency",
            _options.InstanceId, _options.MaxConcurrency);

        pipeline.Start();
        var idleDelay = _options.MinPollDelay;
        var lastBeat = DateTimeOffset.MinValue; // force a beat on the first cycle

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (pipeline.IsFaulted)
                {
                    // The execute block died but this process is alive — it can no longer run anything, so
                    // stop and let the orchestrator restart us with a fresh block. Release our claims first
                    // (the in-flight set is meaningless once faulted) so peers can re-run them immediately.
                    logger.LogCritical(pipeline.FaultException,
                        "Ripple execute block faulted — releasing claims and stopping to restart with a fresh block");
                    try
                    {
                        await engineStore.RecoverSelfStrandedAsync(
                            _options.InstanceId, Array.Empty<Guid>(), TimeSpan.Zero, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Releasing claims after block fault failed; peers recover via heartbeat timeout");
                    }

                    lifetime.StopApplication();
                    return;
                }

                try
                {
                    var capacity = pipeline.AvailableCapacity;
                    var beatDue = DateTimeOffset.UtcNow - lastBeat >= _options.HeartbeatInterval;

                    if (capacity <= 0 && !beatDue)
                    {
                        // Pipeline full — re-check for a freed slot promptly (the prefetch buffer keeps the
                        // execute block busy meanwhile), no DB round trip.
                        await Task.Delay(PipelineFullRecheck, stoppingToken);
                        continue;
                    }

                    // One round trip: heartbeat + claim up to free capacity (may be 0, still beats).
                    var limit = Math.Min(capacity, _options.ClaimBatchSize);
                    var claimed = await PollAndDispatchAsync(limit, stoppingToken);
                    lastBeat = DateTimeOffset.UtcNow;

                    // Keep the pipeline saturated: if we filled our capacity there is likely more, so poll
                    // again immediately (no gap). Only back off when the queue actually ran dry; a partial
                    // claim gets a brief pause.
                    if (claimed >= limit && limit > 0)
                    {
                        idleDelay = TimeSpan.Zero;
                    }
                    else if (limit == 0)
                    {
                        // A heartbeat-only poll (pipeline was full, so we asked for 0 rows). Claiming nothing
                        // says nothing about the queue here — treating it as "ran dry" would back off a
                        // SATURATED instance toward MaxPollDelay, stalling both claims and beats for seconds
                        // and defeating the PipelineFullRecheck cadence above. Go straight back to that
                        // recheck WITHOUT sleeping on idleDelay and without disturbing it, so a genuinely
                        // idle instance doesn't have to re-ramp its backoff from scratch after every beat.
                        continue;
                    }
                    else if (claimed == 0)
                    {
                        idleDelay = TimeSpan.FromMilliseconds(
                            Math.Min(Math.Max(idleDelay.TotalMilliseconds, 1) * 2, _options.MaxPollDelay.TotalMilliseconds));
                    }
                    else
                    {
                        idleDelay = _options.MinPollDelay;
                    }

                    if (idleDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(idleDelay, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // shutting down
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Ripple dispatch cycle failed");

                    // The poll (which carries the heartbeat) failed — beat directly so a live node isn't
                    // mistaken for dead and its in-flight work recovered out from under it.
                    try
                    {
                        await engineStore.BeatAsync(_options.InstanceId, pipeline.ExecutingCount, stoppingToken);
                        lastBeat = DateTimeOffset.UtcNow;
                    }
                    catch (Exception beatError)
                    {
                        logger.LogWarning(beatError, "Ripple fallback heartbeat failed");
                    }

                    // Both awaits above take stoppingToken, and this catch is a SIBLING of the
                    // catch (OperationCanceledException) — so it cannot catch their cancellation. A DB fault that
                    // coincides with shutdown (the common case: connections drop as the host stops) would throw
                    // TaskCanceledException here, propagate through the finally and out of ExecuteAsync, and the
                    // host's BackgroundServiceExceptionBehavior would report the engine as CRASHED on an ordinary
                    // shutdown. Swallow cancellation specifically; the while condition then exits the loop.
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // shutting down
                    }
                }
            }
        }
        finally
        {
            logger.LogInformation("Ripple engine stopping: draining in-flight ripples");
            await pipeline.StopAsync();

            // Deregister so peers don't wait out HeartbeatTimeout to reclaim nothing. RemoveInstanceAsync
            // refuses (and keeps the row) if we still own any Running ripple: the heartbeat is recovery's only
            // handle on that work, and StopAsync deliberately gives up on some paths — so keeping a stale row is
            // exactly what lets RecoverStaleAsync pick the leftovers up.
            try
            {
                if (!await engineStore.RemoveInstanceAsync(_options.InstanceId, CancellationToken.None))
                {
                    logger.LogWarning(
                        "Ripple engine stopped with work still Running; keeping the heartbeat so recovery reclaims it");
                }
            }
            catch (Exception e) { logger.LogWarning(e, "Ripple heartbeat deregister failed"); }
        }
    }

    private async Task<int> PollAndDispatchAsync(int limit, CancellationToken ct)
    {
        var claimed = await engineStore.PollAsync(limit, _options.InstanceId, pipeline.ExecutingCount, ct);
        // A poll claims in schedule_order order, so the batch can span several types — attribute per type_key.
        foreach (var group in claimed.GroupBy(c => c.TypeKey))
        {
            metrics.Claimed(group.Count(), group.Key);
        }

        var dispatched = 0;
        for (var i = 0; i < claimed.Count; i++)
        {
            var r = claimed[i];
            var prepared = new PreparedRipple(r.Id, r.WaveId, r.Attempt, r.MaxAttempts, r.TypeKey, r.PayloadType, r.Payload, r.WavePayload);
            // Ownership of the payload documents passes to the pipeline, which disposes them after execution.
            if (pipeline.Post(prepared))
            {
                dispatched++;
                continue;
            }

            // Block refused it (completing on shutdown, or faulted). Leave the rest Running; on shutdown
            // recovery re-claims them, and a fault is caught at the top of the loop next cycle (fail-fast).
            // Nothing will execute these, so return THEIR pooled buffers here — the refused one plus every
            // ripple after it — or a shutdown mid-claim quietly drains the ArrayPool.
            logger.LogWarning("Execute block refused a ripple (completing or faulted); it stays Running for recovery");
            prepared.Dispose();
            for (var j = i + 1; j < claimed.Count; j++)
            {
                claimed[j].Payload.Dispose();
                claimed[j].WavePayload?.Dispose();
            }

            break;
        }

        // What we actually DISPATCHED, not what we claimed. Returning the claim count on the refusal path made
        // the caller see `claimed >= limit`, zero its backoff and re-poll immediately — a tight loop against the
        // DB at exactly the moment the block is faulted or completing.
        return dispatched;
    }
}
