using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Periodically recomputes each active wave's live numbers (pending/running/failed) from the truth and settles
/// any wave that has drained — the DB-side <c>refresh_wave_stats()</c>, driven every
/// <see cref="RippleEngineOptions.WaveStatsRefreshInterval"/>. This is the <b>only</b> writer of the wave's live
/// numbers and the only place a wave flips to Completed/Faulted: the hot claim/settle/recovery paths just change ripple
/// state, so the numbers self-heal (no drift under a false recovery) and completion is decided from the actual
/// row states, not from counter deltas. Every instance runs this; the refresh is advisory-lock-gated in the
/// store, so at most one instance does the work at a time and the rest are cheap no-ops.
/// </summary>
internal sealed class WaveStatsRefreshLoop(
    IEngineStore engine,
    IOptions<RippleEngineOptions> options,
    ILogger<WaveStatsRefreshLoop> logger) : BackgroundService
{
    private readonly RippleEngineOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.WaveStatsRefreshInterval, stoppingToken);

                try
                {
                    await engine.TryRefreshWaveStatsAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Ripple stats refresh failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
