using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Periodically recovers stuck work in two ways:
/// <list type="number">
/// <item><b>Dead instances</b> — one whose heartbeat is stale past <see cref="RippleEngineOptions.HeartbeatTimeout"/>
/// has its in-flight ripples returned to Pending for survivors to re-claim.</item>
/// <item><b>Our own stranded claims</b> — ripples the DB shows Running &amp; claimed by us that the execute block
/// isn't actually running (a claim that never reached a handler, or one stranded by a fault/race). Dead-instance
/// recovery can't rescue these because we're alive; only we know we aren't running them.</item>
/// </list>
/// Every survivor runs this; both writes are idempotent/fenced, so concurrent recoveries are harmless.
/// </summary>
internal sealed class RecoveryLoop(
    IEngineStore engine,
    ExecutionPipeline pipeline,
    IOptions<RippleEngineOptions> options,
    ILogger<RecoveryLoop> logger) : BackgroundService
{
    private readonly RippleEngineOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.RecoveryInterval, stoppingToken);

                try
                {
                    // Detect stale instances (excluding self) and requeue their orphans.
                    var requeued = await engine.RecoverStaleAsync(
                        _options.HeartbeatTimeout, _options.InstanceId, stoppingToken);
                    if (requeued > 0)
                    {
                        logger.LogWarning("Recovered {Count} ripple(s) from dead instance(s)", requeued);
                    }

                    // Release any of OUR OWN claims the execute block isn't actually running (past the grace
                    // window), gated on the pipeline's live in-flight set so genuinely-executing ripples are kept.
                    var reclaimed = await engine.RecoverSelfStrandedAsync(
                        _options.InstanceId, pipeline.InFlightIds, _options.SelfReconcileGrace, stoppingToken);
                    if (reclaimed > 0)
                    {
                        logger.LogWarning("Reclaimed {Count} self-stranded ripple(s) — claimed but not executing", reclaimed);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Ripple recovery sweep failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
