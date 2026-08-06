using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// A one-shot startup step: writes each registered <c>(wave, ripple)</c> type's configured batch size + gap
/// into <c>ripple.type_schedule</c> (<b>insert-if-absent</b>), so the fan-out sees them when it stamps
/// <c>schedule_order</c>. Runs before the <see cref="Dispatcher"/> starts. It never overwrites an existing row,
/// so a value changed from the dashboard is the source of truth and is not clobbered on restart; the code
/// config is only the first-boot seed. Types registered without explicit config get no row and inherit the
/// <c>type_schedule</c> default row at fan-out.
/// </summary>
internal sealed class ScheduleSeeder(
    RippleHandlerRegistry registry,
    IEngineStore engineStore,
    ILogger<ScheduleSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var schedule in registry.Schedules)
        {
            await engineStore.SeedTypeScheduleAsync(schedule.TypeKey, schedule.BatchSize, schedule.GapSeconds,
                schedule.MaxAttempts, cancellationToken);
            logger.LogInformation(
                "Seeded type schedule {TypeKey}: batchSize={Batch}, gapSeconds={Gap}, maxAttempts={MaxAttempts}",
                schedule.TypeKey, schedule.BatchSize, schedule.GapSeconds, schedule.MaxAttempts);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
