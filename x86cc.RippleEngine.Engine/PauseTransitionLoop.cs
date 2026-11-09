using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Drives the async pause/resume reconcile. Pausing/resuming a type is an O(1) flip of its
/// <c>type_schedule.pause_state</c> (the desired state); each tick this loop moves that type's ripples toward it
/// in bounded chunks — parking <c>Pending → Paused</c> for a paused type, un-parking <c>Paused → Pending</c>
/// (rebasing <c>schedule_order</c> onto the current frontier when asked) for a resuming one — so a pause/resume
/// over millions of ripples is spread across ticks rather than one long-locking transaction. The work is
/// advisory-lock-gated in the store, so every instance runs the loop but at most one does the work and the rest
/// are cheap no-ops (mirrors <see cref="WaveStatsRefreshLoop"/> / <see cref="CompactionLoop"/>).
/// </summary>
internal sealed class PauseTransitionLoop(
    IEngineStore engine,
    IOptions<RippleEngineOptions> options,
    TimeProvider time,
    ILogger<PauseTransitionLoop> logger)
    : PeriodicLoop(options.Value.PauseReconcileInterval, time, logger)
{
    private readonly RippleEngineOptions _options = options.Value;

    protected override string FailureMessage => "Pause/resume reconcile failed";

    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        await engine.ReconcilePauseTransitionsAsync(
            _options.PauseReconcileChunkSize, _options.PauseReconcileMaxRowsPerPass, stoppingToken);
    }
}
