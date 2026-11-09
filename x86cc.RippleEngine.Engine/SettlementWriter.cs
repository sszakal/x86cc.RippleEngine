using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The settlement stage: buffers each attempt's outcome on a succeeded/failed channel and writes them in batches,
/// one DB round trip per batch, retrying a failed write until it lands. Only once a batch is durably written does it
/// release the attempts' <see cref="InFlightTracker"/> slots — which is what turns a settlement backlog into claim
/// back-pressure.
/// </summary>
internal sealed class SettlementWriter(
    IOptions<RippleEngineOptions> options,
    ISplashStore splashStore,
    InFlightTracker inFlight,
    TimeProvider time,
    ILogger<SettlementWriter> logger)
{
    private readonly RippleEngineOptions _options = options.Value;

    private readonly Channel<RippleCompletion> _succeeded = Channel.CreateUnbounded<RippleCompletion>();
    private readonly Channel<RippleFailure> _failed = Channel.CreateUnbounded<RippleFailure>();

    private Task[] _loops = [];
    private Lifecycle _lifecycle;

    // The pipeline's drain token (see ExecutionPipeline._drainCts): bounds the write retries during shutdown.
    private CancellationToken _drainToken;

    /// <summary>Starts the two flush loops. <paramref name="drainToken"/> is the pipeline's drain deadline — NOT the
    /// host's stopping token — and is the only thing that ever stops a write retry. Once only: a second call
    /// would start a second pair of loops reading the same channels.</summary>
    public void Start(CancellationToken drainToken)
    {
        _lifecycle.Start(nameof(SettlementWriter));
        _drainToken = drainToken;
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

    // Unbounded, so TryWrite only refuses after Complete — and nothing writes after the execute block has drained.
    public void Succeeded(RippleCompletion completion) => _succeeded.Writer.TryWrite(completion);

    public void Failed(RippleFailure failure) => _failed.Writer.TryWrite(failure);

    /// <summary>Closes the channels and waits for the flush loops to write (or, past the drain deadline, give up on)
    /// everything already buffered. Call only once nothing can produce another outcome. Requires
    /// <see cref="Start"/>: without the loops, the buffered outcomes would be silently dropped.</summary>
    public async Task CompleteAsync()
    {
        _lifecycle.Stop(nameof(SettlementWriter));
        _succeeded.Writer.TryComplete();
        _failed.Writer.TryComplete();
        try { await Task.WhenAll(_loops); } catch { /* logged inside loops */ }
    }

    private async Task FlushLoopAsync<T>(ChannelReader<T> reader, int batchSize,
        Func<IReadOnlyList<T>, CancellationToken, Task> write, Func<T, (Guid RippleId, int Attempt)> keyOf)
    {
        var batch = new List<T>(batchSize);
        // Read until the writer completes (CompleteAsync), independent of the stop token, so every buffered
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
                inFlight.Release(batch.Select(keyOf));
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
                    await Task.Delay(delay, time, _drainToken);
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
