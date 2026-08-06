using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The archive maintenance loop. Each tick it (1) compacts finished waves — rolls each terminal wave's
/// per-attempt splash reports into aggregated <c>report_chunk</c> rows and reclaims its ripple/splash rows —
/// and (2) purges compacted waves whose per-type retention has elapsed (deleting their <c>wave</c> + report
/// chunks). The work is DB-side (<c>compact_wave()</c> / <c>purge_expired_waves()</c>); every instance runs the
/// loop but both are advisory-lock-gated in the store, so at most one instance does the work and the rest are
/// cheap no-ops.
/// </summary>
internal sealed class CompactionLoop(
    IReportStore reports,
    IOptions<RippleEngineOptions> options,
    ILogger<CompactionLoop> logger) : BackgroundService
{
    private readonly RippleEngineOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.CompactionInterval, stoppingToken);

                try
                {
                    await reports.CompactReadyWavesAsync(
                        _options.ReportChunkSize, _options.CompactionMaxWavesPerPass, stoppingToken);
                    await reports.PurgeExpiredWavesAsync(_options.CompactionMaxWavesPerPass, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Wave compaction/purge failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
