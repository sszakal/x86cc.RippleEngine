using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Xunit;

namespace x86cc.Ripple.Sample.E2ETests;

/// <summary>
/// Spins up the whole sample via the real Aspire AppHost (Postgres + WebAPI + 3 Worker replicas) once per
/// test run and exposes an <see cref="HttpClient"/> to the WebAPI. Shared across the E2E tests so the app
/// starts a single time. The Postgres data volume is <b>named</b> (see AppHost), so a seed done in one run is
/// still there on the next — the taxation tests reuse it rather than re-seeding.
/// </summary>
public sealed class AspireAppFixture : IAsyncLifetime
{
    private DistributedApplication? _app;

    public HttpClient Http { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.x86cc_Ripple_Sample_AppHost>();

        _app = await builder.BuildAsync();
        await _app.StartAsync();

        Http = _app.CreateHttpClient("webapi");
        Http.Timeout = TimeSpan.FromMinutes(30); // large seeds/waves are slow; we poll, so per-call is short anyway

        await WaitForApiAsync(TimeSpan.FromMinutes(5));
    }

    public async Task DisposeAsync()
    {
        // Disposes the app (stops the containers/processes) but leaves the NAMED data volume intact.
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    // Poll the WebAPI until it answers — covers Postgres coming up, migrations, and Marten's startup schema.
    private async Task WaitForApiAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new HttpClient { BaseAddress = Http.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
                var resp = await probe.GetAsync("/waves");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch
            {
                // not up yet
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"WebAPI did not become ready within {timeout}.");
    }
}

[CollectionDefinition(Name)]
public sealed class AspireCollection : ICollectionFixture<AspireAppFixture>
{
    public const string Name = "aspire-app";
}
