using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The dashboard's read queries (<see cref="IDashboardQueries"/>). Every query is exercised against real rows: the
/// results are positional records Dapper maps by column ORDER and type, so a reordered column or a changed SQL type
/// only fails at runtime — here rather than as a blank dashboard panel.
/// </summary>
public sealed class DashboardQueriesTests : RippleTestBase
{
    // What ASP.NET Core minimal APIs serialise with — the SPA reads these exact keys.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private IDashboardQueries Queries => Storage.GetRequiredService<IDashboardQueries>();

    [Fact]
    public async Task the_wave_list_derives_live_numbers_and_keeps_the_spas_json_keys()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        var ids = await SeedRipplesAsync(wave.Id, 4);

        // Before the first stats refresh every ripple is reported pending (the honest pre-refresh view).
        var before = (await Queries.ListWavesAsync(new WaveListFilter())).Waves.Single();
        before.Pending.ShouldBe(4);
        before.Succeeded.ShouldBe(0);

        var claimed = await Engine.PollAsync(1, "inst-1", 0);
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(claimed[0].Id, wave.Id, claimed[0].Attempt, DateTimeOffset.UtcNow, null)], "inst-1");
        await RefreshWaveStatsAsync();

        var page = await Queries.ListWavesAsync(new WaveListFilter());
        var listed = page.Waves.Single();
        listed.Id.ShouldBe(wave.Id);
        listed.RippleCount.ShouldBe(ids.Count);
        listed.Pending.ShouldBe(3);
        listed.Succeeded.ShouldBe(1); // derived: ripple_count - pending - running - paused - failed
        listed.Status.ShouldBe(nameof(WaveStatus.Active));
        listed.DurationMs.ShouldBeNull(); // not completed yet
        page.Stats.ShouldBe(new WaveStatusCounts(Total: 1, Active: 1, Completed: 0, Faulted: 0));

        JsonKeys(listed).ShouldBe(
        [
            "avgDurationMs", "compactedAt", "completedAt", "createdAt", "durationMs", "failed", "id", "name",
            "paused", "pending", "retryCount", "rippleCount", "running", "splashSampleCount", "status", "succeeded",
            "throughput", "type"
        ]);
    }

    /// <summary><c>q</c> is a literal substring: <c>%</c> and <c>_</c> used to be live LIKE wildcards, so searching
    /// <c>50%_off</c> also matched "50 and more off".</summary>
    [Theory]
    [InlineData("50%_off", new[] { "promo 50%_off" })]
    [InlineData("a_b", new[] { "a_b" })]
    [InlineData(@"c:\d", new[] { @"c:\d" })]
    [InlineData("PROMO", new[] { "promo 50%_off", "promo 50 and more off" })] // still case-insensitive
    public async Task search_matches_the_text_literally(string search, string[] expected)
    {
        await ResetAsync();
        foreach (var name in new[] { "promo 50%_off", "promo 50 and more off", "a_b", "axb", @"c:\d", "c:d" })
        {
            await Engine.CreateWaveAsync(new Wave { Name = name, Type = "t" });
        }

        var page = await Queries.ListWavesAsync(new WaveListFilter(Search: search));

        page.Waves.Select(w => w.Name).ShouldBe(expected, ignoreOrder: true);
        page.Stats.Total.ShouldBe(expected.Length); // the summary applies the same filter
    }

    [Fact]
    public async Task the_list_filters_status_and_a_half_open_range_and_counts_past_the_limit()
    {
        await ResetAsync();
        var t0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            await Engine.CreateWaveAsync(new Wave { Name = $"w{i}", Type = "t", CreatedAt = t0.AddHours(i) });
        }

        // [t0, t0+2h): the wave created exactly at the upper bound is excluded.
        var ranged = await Queries.ListWavesAsync(new WaveListFilter(From: t0, To: t0.AddHours(2)));
        ranged.Waves.Select(w => w.Name).ShouldBe(["w1", "w0"]); // newest first

        // The limit caps the page, not the counts.
        var limited = await Queries.ListWavesAsync(new WaveListFilter(Limit: 1));
        limited.Waves.Count.ShouldBe(1);
        limited.Stats.Total.ShouldBe(3);

        (await Queries.ListWavesAsync(new WaveListFilter(Status: "Completed"))).Waves.ShouldBeEmpty();
    }

    [Fact]
    public async Task wave_detail_adds_the_payload_type_and_is_null_when_unknown()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();

        var detail = (await Queries.GetWaveAsync(wave.Id)).ShouldNotBeNull();
        detail.PayloadType.ShouldBe(nameof(RecalcContext));
        detail.Name.ShouldBe(wave.Name);
        JsonKeys(detail).ShouldContain("payloadType");

        (await Queries.GetWaveAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task activity_and_histogram_bucket_waves_by_time()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        await Engine.CreateWaveAsync(new Wave { Name = "a", Type = "t", CreatedAt = hour.AddMinutes(1) });
        await Engine.CreateWaveAsync(new Wave { Name = "b", Type = "t", CreatedAt = hour.AddMinutes(2) });

        var activity = await Queries.GetWaveActivityAsync(days: 7);
        activity.Single().ShouldBe(new WaveDayActivity(hour.ToString("yyyy-MM-dd"), 2, 0, 0, 2));

        var buckets = await Queries.GetWaveHistogramAsync(hour, hour.AddHours(1), "hour", "UTC");
        buckets.Single().ShouldBe(new WaveBucket(hour.UtcDateTime, 2, 0, 0, 2));
        // Naive wall-clock start, not an instant — the browser reads back the exact boundary it grouped by.
        buckets.Single().Start.Kind.ShouldBe(DateTimeKind.Unspecified);
    }

    [Fact]
    public async Task type_metrics_schedules_and_cluster_map_every_column()
    {
        await ResetAsync();
        var typeKey = RippleTypeKey.Compose(nameof(RecalcContext), nameof(RecalcCompany));
        await SeedScheduleAsync(typeKey, batchSize: 10, gapSeconds: 4, maxAttempts: 3);
        await Engine.PollAsync(0, "inst-1", executing: 2); // heartbeat only

        var metric = (await Queries.GetTypeMetricsAsync()).Single(m => m.TypeKey == typeKey);
        metric.ShouldBe(new TypeMetric(typeKey, 10, 4m, 2.5m, 0, null, null, null));

        var schedules = await Queries.GetTypeSchedulesAsync();
        schedules.Single(s => s.TypeKey == typeKey).ShouldBe(new TypeScheduleRow(typeKey, 10, 4m, 3, "active"));
        schedules.ShouldContain(s => s.TypeKey == RippleTypeKey.Default);

        var instance = (await Queries.GetClusterAsync()).Single();
        instance.InstanceId.ShouldBe("inst-1");
        instance.Executing.ShouldBe(2);
        instance.AgeSeconds.ShouldBeInRange(0, 60);
        JsonKeys(instance).ShouldBe(["ageSeconds", "executing", "instanceId", "lastSeenAt"]);
    }

    private static IReadOnlyList<string> JsonKeys<T>(T value)
        => JsonSerializer.SerializeToElement(value, Web).EnumerateObject().Select(p => p.Name).Order().ToList();
}
