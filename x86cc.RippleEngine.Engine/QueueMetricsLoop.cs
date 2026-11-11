using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Samples the queue for the <c>ripple.queue.*</c> gauges every <see cref="RippleEngineOptions.QueueMetricsInterval"/>.
/// A gauge's callback runs synchronously on the metrics reader, which is no place for a database round trip, so
/// this loop does the read and the gauges report the last sample.
/// </summary>
/// <remarks>
/// The numbers are cluster-wide, so every instance reports the same series (aggregate with max, not sum). Each
/// instance samples for itself rather than electing one: the read is two index lookups, and a gauge that only one
/// instance could report would vanish whenever that instance did. Nothing is read while no one is listening.
/// </remarks>
internal sealed class QueueMetricsLoop(
    IEngineStore engine,
    RippleMetrics metrics,
    IOptions<RippleEngineOptions> options,
    TimeProvider time,
    ILogger<QueueMetricsLoop> logger)
    : PeriodicLoop(options.Value.QueueMetricsInterval, time, logger)
{
    protected override string FailureMessage => "Ripple queue metrics sample failed";

    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        if (metrics.QueueObserved)
        {
            metrics.SetQueue(await engine.GetQueueSnapshotAsync(stoppingToken));
        }
    }
}
