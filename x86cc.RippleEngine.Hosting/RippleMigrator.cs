using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Hosting;

/// <summary>
/// Applies the <c>ripple</c> schema migrations exactly once per process, whoever asks first.
/// </summary>
/// <remarks>
/// Two things race to be first, and both must find the schema in place: the engine's
/// <c>ScheduleSeeder</c> (a startup-blocking hosted service that writes <c>type_schedule</c>) and — on a web
/// host — the first request to the dashboard. A hosted service alone cannot cover the second: in a
/// <c>WebApplication</c> the web host's own hosted service is registered while the builder is created, i.e.
/// before anything <c>AddRippleEngine</c> adds, so it always starts first. Startup filters, by contrast, run
/// inside that service before the server listens. So both paths call in here and the loser is a no-op.
/// </remarks>
internal sealed class RippleMigrator(IServiceProvider services, ILogger<RippleMigrator> logger)
{
    private readonly Lock _gate = new();
    private bool _migrated;

    public void EnsureMigrated()
    {
        if (_migrated)
        {
            return;
        }

        lock (_gate)
        {
            if (_migrated)
            {
                return;
            }

            logger.LogInformation("Applying Ripple schema migrations");
            services.MigrateRipple();
            _migrated = true;
        }
    }
}

/// <summary>Migrates on start for hosts with no request pipeline (a worker service, a test host). Registered
/// ahead of the engine's hosted services, so the schema exists before the schedule seeder writes to it.</summary>
internal sealed class RippleMigrationService(RippleMigrator migrator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        migrator.EnsureMigrated();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
