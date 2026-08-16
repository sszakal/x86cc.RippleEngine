using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using x86cc.RippleEngine.Dashboard;

namespace x86cc.RippleEngine.Hosting;

/// <summary>
/// The web-host half of <c>AddRippleEngine</c>: migrates before the server listens, and appends the dashboard
/// to the request pipeline — the two things that used to be hand-written after <c>builder.Build()</c>.
/// </summary>
/// <remarks>
/// The dashboard is mapped in a routing pass placed <b>after</b> the application's own pipeline, so the app
/// always wins: its endpoints match first, and only what it left unmatched reaches the dashboard's <c>/api</c>
/// group and the SPA fallback. Mapping ahead of the app would invert that — the SPA fallback matches every
/// path, so it would swallow the host's own routes. The flip side is that middleware the app installs (auth,
/// CORS) runs before the dashboard's endpoints are matched, so their endpoint metadata is not visible to it: a
/// host that needs the dashboard behind authorization should leave <c>EnableDashboard</c> off and call
/// <see cref="DashboardApi.MapRippleDashboard"/> itself, inside its own pipeline.
/// </remarks>
internal sealed class RippleStartupFilter(bool autoMigrate, bool enableDashboard) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Runs while the pipeline is being built — i.e. before Kestrel accepts the first request.
        if (autoMigrate)
        {
            app.ApplicationServices.GetRequiredService<RippleMigrator>().EnsureMigrated();
        }

        next(app);

        if (!enableDashboard)
        {
            return;
        }

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapRippleDashboard();
            endpoints.MapRippleDashboardSpa();
        });
    };
}
