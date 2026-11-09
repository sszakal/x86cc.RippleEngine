using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The shape every engine maintenance loop shares (<see cref="RecoveryLoop"/>, <see cref="WaveStatsRefreshLoop"/>,
/// <see cref="CompactionLoop"/>, <see cref="PauseTransitionLoop"/>): wait <c>interval</c>, run one
/// <see cref="TickAsync"/>, repeat until the host stops. A failed tick is logged and the loop carries on — these
/// are all idempotent sweeps that the next tick retries, so one DB blip must never stop the loop for good.
/// </summary>
/// <remarks>
/// The interval is the gap BETWEEN ticks (delay, then tick), not a fixed rate: a slow tick pushes the next one
/// back rather than queueing a catch-up burst, which is what a <see cref="PeriodicTimer"/> would do.
/// </remarks>
internal abstract class PeriodicLoop(TimeSpan interval, TimeProvider time, ILogger logger) : BackgroundService
{
    /// <summary>The loop's logger (its concrete type's category), for logging from <see cref="TickAsync"/>.</summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>The error message logged when a tick throws.</summary>
    protected abstract string FailureMessage { get; }

    /// <summary>One pass of the loop's work.</summary>
    protected abstract Task TickAsync(CancellationToken stoppingToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, time, stoppingToken);

                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Logger.LogError(e, FailureMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
