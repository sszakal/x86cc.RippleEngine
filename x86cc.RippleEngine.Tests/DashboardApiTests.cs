using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Hosting;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The dashboard's HTTP layer (<c>DashboardApi</c>) over an in-memory <see cref="TestServer"/>: routes, clamps,
/// the response wrappers the SPA unpacks, and the status codes of every write path. The SQL underneath is covered
/// by <see cref="DashboardQueriesTests"/>; this is what the browser actually sees.
/// </summary>
public sealed class DashboardApiTests : RippleTestBase
{
    private static readonly string TypeKey = RippleTypeKey.Compose(nameof(RecalcContext), nameof(RecalcCompany));

    private WebApplication _app = default!;
    private HttpClient _http = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // The creation-side shape plus the dashboard, as the sample WebAPI would host it: no workers, so nothing
        // claims or settles behind the test's back. The schema is already migrated by the base.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.AddRippleEngine(o =>
            {
                o.ConnectionString = ConnectionString;
                o.EnableWorkers = false;
                o.AutoMigrate = false;
                o.EnableDashboard = true;
            })
            .AddHandler<RecalcContext, RecalcCompany, RecalcHandler>(batchSize: 25, gapSeconds: 2, maxAttempts: 4);

        _app = builder.Build();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public override async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        await base.DisposeAsync();
    }

    // ---- reads ------------------------------------------------------------------------------------

    [Fact]
    public async Task the_wave_list_is_wrapped_and_its_limit_is_clamped()
    {
        await ResetAsync();
        await CreateWaveAsync(legislation: "A");
        await CreateWaveAsync(legislation: "B");

        var body = await GetJsonAsync("/api/waves?limit=0"); // clamped up to 1, not "no rows"
        body.GetProperty("waves").GetArrayLength().ShouldBe(1);
        body.GetProperty("stats").GetProperty("total").GetInt64().ShouldBe(2);

        var filtered = await GetJsonAsync("/api/waves?q=" + Uri.EscapeDataString("B changed"));
        filtered.GetProperty("waves").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("B changed");
    }

    [Fact]
    public async Task wave_detail_is_404_for_an_unknown_id()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();

        var detail = await GetJsonAsync($"/api/waves/{wave.Id}");
        detail.GetProperty("payloadType").GetString().ShouldBe(nameof(RecalcContext));

        (await _http.GetAsync($"/api/waves/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _http.GetAsync($"/api/waves/{Guid.NewGuid()}/report.csv")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task activity_histogram_and_cluster_use_the_wrappers_the_spa_unpacks()
    {
        await ResetAsync();
        await CreateWaveAsync();
        await Engine.PollAsync(0, "inst-1", executing: 0); // a heartbeat

        (await GetJsonAsync("/api/waves/activity?days=9999")).GetProperty("days").GetArrayLength().ShouldBe(1);
        (await GetJsonAsync("/api/cluster")).GetProperty("instances").GetArrayLength().ShouldBe(1);

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        (await GetJsonAsync($"/api/waves/histogram?from={from}&to={to}&bucket=day"))
            .GetProperty("buckets").GetArrayLength().ShouldBe(1);

        var bad = await _http.GetAsync($"/api/waves/histogram?from={from}&to={to}&bucket=week");
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(bad)).ShouldContain("bucket must be one of");
    }

    [Fact]
    public async Task settings_list_the_registered_handler_with_its_seeded_values()
    {
        await ResetAsync();

        var body = await GetJsonAsync("/api/settings/types");
        var type = body.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("typeKey").GetString() == TypeKey);
        type.GetProperty("configured").GetBoolean().ShouldBeFalse(); // no row yet: workers (the seeder) are off
        type.GetProperty("pauseState").GetString().ShouldBe("active");
        type.GetProperty("seededBatchSize").GetInt32().ShouldBe(25);
        type.GetProperty("seededMaxAttempts").GetInt32().ShouldBe(4);
        body.GetProperty("default").GetProperty("maxAttempts").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    // ---- type config writes --------------------------------------------------------------------------

    [Fact]
    public async Task a_valid_settings_put_is_stored_and_listed_as_configured()
    {
        await ResetAsync();

        var put = await _http.PutAsJsonAsync(TypeUrl(TypeKey), new { batchSize = 10, gapSeconds = 0.5, maxAttempts = 3 });
        put.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var type = (await GetJsonAsync("/api/settings/types")).GetProperty("types").EnumerateArray()
            .Single(t => t.GetProperty("typeKey").GetString() == TypeKey);
        type.GetProperty("configured").GetBoolean().ShouldBeTrue();
        type.GetProperty("batchSize").GetInt32().ShouldBe(10);
        type.GetProperty("gapSeconds").GetDecimal().ShouldBe(0.5m);
    }

    /// <summary>The PUT reports <c>TypeScheduleGuard</c>'s own rules as a 400, so the HTTP layer and the store
    /// can't drift apart — and nothing is written.</summary>
    [Theory]
    [InlineData(0, 1.0, null, "batchSize must be >= 1")]
    [InlineData(10, 0.0, null, "gapSeconds must be a finite number > 0")]
    [InlineData(10, -2.0, null, "gapSeconds must be a finite number > 0")]
    [InlineData(10, 1.0, 0, "maxAttempts must be >= 1")]
    public async Task an_unusable_schedule_is_a_400_with_the_guards_message(
        int batchSize, double gapSeconds, int? maxAttempts, string error)
    {
        await ResetAsync();

        var put = await _http.PutAsJsonAsync(TypeUrl(TypeKey), new { batchSize, gapSeconds, maxAttempts });

        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(put)).ShouldStartWith(error);
        (await ScalarAsync("select count(*) from ripple.type_schedule where type_key = @TypeKey", new { TypeKey }))
            .ShouldBe(0);
    }

    [Fact]
    public async Task the_default_row_needs_an_explicit_max_attempts_and_cannot_be_reset_or_paused()
    {
        await ResetAsync();
        var url = TypeUrl(RippleTypeKey.Default);

        (await _http.PutAsJsonAsync(url, new { batchSize = 10, gapSeconds = 1 })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PutAsJsonAsync(url, new { batchSize = 10, gapSeconds = 1, maxAttempts = 5 })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await _http.DeleteAsync(url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PostAsync(url + "/pause", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PostAsync(url + "/resume", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>A key with no <c>|</c> (or more than one) matches no ripple, so each write would be a silent no-op —
    /// a "paused" type that keeps running. Every type-key route refuses it before touching the store.</summary>
    [Theory]
    [InlineData("RecalcContext")]
    [InlineData("a|b|c")]
    public async Task a_malformed_type_key_is_refused_by_every_write(string typeKey)
    {
        await ResetAsync();
        var url = TypeUrl(typeKey);

        var responses = new[]
        {
            await _http.PutAsJsonAsync(url, new { batchSize = 10, gapSeconds = 1 }),
            await _http.DeleteAsync(url),
            await _http.PostAsync(url + "/pause", null),
            await _http.PostAsync(url + "/resume", null)
        };

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ErrorAsync(response)).ShouldContain("is not a type key");
        }

        (await ScalarAsync($"select count(*) from ripple.type_schedule where type_key <> '{RippleTypeKey.Default}'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task pause_resume_and_reset_round_trip_through_the_api()
    {
        await ResetAsync();
        var url = TypeUrl(TypeKey);
        await SeedScheduleAsync(TypeKey, batchSize: 10, gapSeconds: 1);

        (await _http.DeleteAsync(TypeUrl("RecalcContext|Nothing"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await _http.PostAsync(url + "/pause", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // Resetting a paused type would strand its parked ripples.
        var refused = await _http.DeleteAsync(url);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(refused)).ShouldContain("paused or resuming");

        (await _http.PostAsync(url + "/resume?rebase=false", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ReconcilePauseTransitionsAsync(); // resuming_* → active
        (await _http.DeleteAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---- reschedule ----------------------------------------------------------------------------------

    [Fact]
    public async Task reschedule_restamps_a_live_wave_and_reports_the_new_share()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 6);

        var response = await _http.PostAsJsonAsync($"/api/waves/{wave.Id}/reschedule",
            new { batchSize = 2, gapSeconds = 4, typeKey = TypeKey });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rescheduled").GetInt32().ShouldBe(6);
        body.GetProperty("share").GetDouble().ShouldBe(0.5);
    }

    [Fact]
    public async Task reschedule_validates_before_it_looks_up_the_wave()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        var url = $"/api/waves/{wave.Id}/reschedule";

        var badGap = await _http.PostAsJsonAsync(url, new { batchSize = 2, gapSeconds = 0 });
        badGap.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(badGap)).ShouldStartWith("gapSeconds must be");

        var badKey = await _http.PostAsJsonAsync(url, new { batchSize = 2, gapSeconds = 1, typeKey = "RecalcCompany" });
        badKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(badKey)).ShouldContain("is not a type key");

        (await _http.PostAsJsonAsync($"/api/waves/{Guid.NewGuid()}/reschedule", new { batchSize = 2, gapSeconds = 1 }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The store refuses the poison values itself too, so a caller that bypasses the dashboard can't
    /// re-stamp a wave with a zero divisor or a gap that sorts it ahead of the frontier.</summary>
    [Fact]
    public async Task the_store_refuses_an_unusable_reschedule_directly()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Engine.RescheduleWaveAsync(wave.Id, 0, 1));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Engine.RescheduleWaveAsync(wave.Id, 1, -1));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static string TypeUrl(string typeKey) => $"/api/settings/types/{Uri.EscapeDataString(typeKey)}";

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _http.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
