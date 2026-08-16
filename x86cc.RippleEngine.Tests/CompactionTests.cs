using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Compaction (P2): once a wave is terminal, its per-attempt splash reports are rolled into aggregated
/// <c>report_chunk</c> rows and the ripple/splash rows are reclaimed, leaving only the wave + its report.
/// Driven directly (claim + settle via the stores, then <c>compact_wave</c>) so the reports are deterministic.
/// </summary>
public sealed class CompactionTests : RippleTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task compaction_aggregates_across_retries_and_reclaims_rows()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedBatchAsync(wave.Id, ["t1", "t2"]);

        // Attempt 1: t1 fails ("boom"), t2 succeeds — non-terminal, so the ripple requeues.
        var c1 = await Engine.PollAsync(10, "inst-1", 0);
        c1.Count.ShouldBe(1);
        await Splashes.FailRipplesAsync(
            [new RippleFailure(c1[0].Id, wave.Id, c1[0].Attempt, Now(),
                """[{"outcome":"Failed","output":"boom","targetIds":["t1"]},{"outcome":"Succeeded","output":null,"targetIds":["t2"]}]""",
                Terminal: false, DateTimeOffset.UtcNow.AddHours(-1))],
            "inst-1");

        // Attempt 2: everything succeeds ⇒ ripple Succeeded, wave Completed.
        var c2 = await Engine.PollAsync(10, "inst-2", 0);
        c2.Count.ShouldBe(1);
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(c2[0].Id, wave.Id, c2[0].Attempt, Now(),
                """[{"outcome":"Succeeded","output":null,"targetIds":["t1","t2"]}]""")],
            "inst-2");
        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Completed);
        (await ScalarAsync("select count(*) from ripple.splash where wave_id = @w", new { w = wave.Id })).ShouldBe(2);
        // The retry (attempt-2 splash) is counted live by the stats refresh: 1 re-execution.
        (await Engine.GetWaveAsync(wave.Id))!.RetryCount.ShouldBe(1);

        await CompactWaveAsync(wave.Id);

        // Ripples + splashes reclaimed; only the wave (+ report chunks) remain.
        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @w", new { w = wave.Id })).ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.splash where wave_id = @w", new { w = wave.Id })).ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.report_chunk where wave_id = @w", new { w = wave.Id })).ShouldBeGreaterThan(0);
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.RippleCount.ShouldBe(1); // ripple_count preserved on the wave
        reloaded.RetryCount.ShouldBe(1);   // retry count stamped authoritatively at compaction (splashes now gone)
        (await ScalarAsync("select count(*) from ripple.wave where id = @w and compacted_at is not null", new { w = wave.Id })).ShouldBe(1);
        // The per-type retry-rate EWMA survives compaction: 1 retry over 1 ripple ⇒ rate 1.0 (first observation).
        (await DoubleAsync("select avg_retry_rate from ripple.ripple_type_metric where avg_retry_rate is not null")).ShouldBe(1.0, 0.001);

        // The aggregated report keeps retries honest: t1 appears both Failed (attempt 1) and Succeeded (attempt 2),
        // and t2 succeeds on both attempts (duplicated).
        var report = await Reports.GetReportAsync(wave.Id);
        report.ShouldNotBeNull();
        var failed = report!.Items.Where(i => i.Outcome == SplashOutcome.Failed).ToList();
        failed.ShouldHaveSingleItem();
        failed[0].Output.ShouldBe("boom");
        failed[0].TargetIds.ShouldContain("t1");
        var succeeded = report.Items.Where(i => i.Outcome == SplashOutcome.Succeeded).SelectMany(i => i.TargetIds).ToList();
        succeeded.ShouldContain("t1");                 // succeeded on the retry
        succeeded.Count(x => x == "t2").ShouldBe(2);   // t2 succeeded on both attempts
    }

    [Fact]
    public async Task compaction_rolls_up_execution_time_per_wave_and_type()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 2);

        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        claimed.Count.ShouldBe(2);
        // Settle both succeeded; StartedAt ~1s ago ⇒ a positive per-attempt duration to average.
        await Splashes.CompleteRipplesAsync(
            claimed.Select(c => new RippleCompletion(c.Id, wave.Id, c.Attempt, Now(),
                """[{"outcome":"Succeeded","output":null,"targetIds":["x"]}]""")).ToList(),
            "inst-1");
        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Completed);

        await CompactWaveAsync(wave.Id);

        // The per-wave mean is computed ONCE at compaction, over the 2 succeeded splashes (before they're deleted).
        (await ScalarAsync("select splash_sample_count from ripple.wave where id = @w", new { w = wave.Id })).ShouldBe(2);
        (await ScalarAsync("select avg_duration_ms from ripple.wave where id = @w", new { w = wave.Id })).ShouldBeGreaterThan(0);

        // The per-type rollup survives the wave's compaction: one row, both attempts counted.
        (await ScalarAsync("select coalesce(sum(sample_count), 0) from ripple.ripple_type_metric")).ShouldBe(2);
        (await ScalarAsync("select count(*) from ripple.ripple_type_metric where avg_ms > 0")).ShouldBe(1);

        // Surfaced on the report the CSV export reads.
        var report = await Reports.GetReportAsync(wave.Id);
        report!.AvgDurationMs.ShouldNotBeNull();
        report.SplashSampleCount.ShouldBe(2);
    }

    [Fact]
    public async Task type_metric_is_a_count_weighted_ewma_across_waves()
    {
        await ResetAsync();

        // Wave 1: 2 succeeded attempts of type 'K', each 100 ms execution + 50 ms queue wait ⇒ seeds the averages.
        var w1 = Guid.NewGuid();
        await InsertSucceededWaveAsync(w1, "K", count: 2, durationMs: 100, waitMs: 50);
        await CompactWaveAsync(w1);
        (await DoubleAsync("select avg_ms from ripple.ripple_type_metric where type_key = 'K'")).ShouldBe(100, 0.001);
        (await DoubleAsync("select avg_wait_ms from ripple.ripple_type_metric where type_key = 'K'")).ShouldBe(50, 0.001);
        (await ScalarAsync("select sample_count from ripple.ripple_type_metric where type_key = 'K'")).ShouldBe(2);

        // Wave 2: 1 attempt at 1000 ms exec + 300 ms wait. Count-weighted EWMA blends the new wave (weight 1)
        // against the accumulated weight (2) decayed by λ = 0.8:
        //   exec: (100·2·0.8 + 1000·1) / (2·0.8 + 1) = 1160 / 2.6 ≈ 446
        //   wait: ( 50·2·0.8 +  300·1) / (2·0.8 + 1) =  380 / 2.6 ≈ 146
        var w2 = Guid.NewGuid();
        await InsertSucceededWaveAsync(w2, "K", count: 1, durationMs: 1000, waitMs: 300);
        await CompactWaveAsync(w2);
        (await DoubleAsync("select avg_ms from ripple.ripple_type_metric where type_key = 'K'")).ShouldBe(446.15, 0.5);
        (await DoubleAsync("select avg_wait_ms from ripple.ripple_type_metric where type_key = 'K'")).ShouldBe(146.15, 0.5);
        (await ScalarAsync("select sample_count from ripple.ripple_type_metric where type_key = 'K'")).ShouldBe(3);
    }

    [Fact]
    public async Task retry_rate_metric_is_a_per_wave_ewma_across_waves()
    {
        await ResetAsync();

        // Wave 1: 4 ripples of type 'R', 1 of them retried once ⇒ rate 1/4 = 0.25 seeds the EWMA.
        var w1 = Guid.NewGuid();
        await InsertRetryWaveAsync(w1, "R", ripples: 4, retries: 1);
        await CompactWaveAsync(w1);
        (await DoubleAsync("select avg_retry_rate from ripple.ripple_type_metric where type_key = 'R'")).ShouldBe(0.25, 0.001);

        // Wave 2: 4 ripples, 3 retries ⇒ rate 0.75. Per-wave EWMA blends with alpha 0.2 (lambda 0.8):
        //   0.25 · 0.8 + 0.75 · 0.2 = 0.35
        var w2 = Guid.NewGuid();
        await InsertRetryWaveAsync(w2, "R", ripples: 4, retries: 3);
        await CompactWaveAsync(w2);
        (await DoubleAsync("select avg_retry_rate from ripple.ripple_type_metric where type_key = 'R'")).ShouldBe(0.35, 0.001);
    }

    /// <summary>Inserts a terminal wave of <paramref name="ripples"/> ripples for <paramref name="typeKey"/>,
    /// <paramref name="retries"/> of which carry an extra attempt-2 (retry) splash on top of their attempt-1
    /// splash — so the per-wave retry rate (retries ÷ ripples) is exact for the EWMA math.</summary>
    private async Task InsertRetryWaveAsync(Guid waveId, string typeKey, int ripples, int retries)
    {
        var created = DateTimeOffset.UtcNow.AddMinutes(-5);
        await ExecuteAsync(
            "insert into ripple.wave(id, name, type, status, ripple_count, created_at) " +
            "values (@waveId, 'retry-ewma', 't', 'Completed', @ripples, @created)",
            new { waveId, ripples, created });
        for (var i = 0; i < ripples; i++)
        {
            var rippleId = Guid.NewGuid();
            await ExecuteAsync(
                "insert into ripple.ripple(id, wave_id, payload, type_key, state, created_at) " +
                "values (@rippleId, @waveId, '{}'::jsonb, @typeKey, 'Succeeded', @created)",
                new { rippleId, waveId, typeKey, created });
            // Attempt-1 splash (never a retry).
            await ExecuteAsync(
                "insert into ripple.splash(id, ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report) " +
                "values (gen_random_uuid(), @rippleId, @waveId, 1, @created, @created, @created, 'Succeeded', 1, null)",
                new { rippleId, waveId, created });
            if (i < retries)
            {
                // Attempt-2 splash ⇒ one retry for this ripple.
                await ExecuteAsync(
                    "insert into ripple.splash(id, ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report) " +
                    "values (gen_random_uuid(), @rippleId, @waveId, 2, @created, @created, @created, 'Succeeded', 1, null)",
                    new { rippleId, waveId, created });
            }
        }
    }

    /// <summary>Inserts a terminal wave with <paramref name="count"/> succeeded splashes of a fixed
    /// <paramref name="durationMs"/> execution time and <paramref name="waitMs"/> queue wait (claimed_at −
    /// created_at) for <paramref name="typeKey"/> — so the EWMA math is deterministic (unlike wall-clock-derived
    /// timing). Uses an explicit base timestamp so wait is exact.</summary>
    private async Task InsertSucceededWaveAsync(Guid waveId, string typeKey, int count, long durationMs, long waitMs = 0)
    {
        var created = DateTimeOffset.UtcNow.AddMinutes(-5);
        var claimed = created.AddMilliseconds(waitMs);
        var ended = claimed.AddMilliseconds(durationMs);
        await ExecuteAsync(
            "insert into ripple.wave(id, name, type, status, ripple_count, created_at) " +
            "values (@waveId, 'ewma', 't', 'Completed', @count, @created)",
            new { waveId, count, created });
        for (var i = 0; i < count; i++)
        {
            var rippleId = Guid.NewGuid();
            await ExecuteAsync(
                "insert into ripple.ripple(id, wave_id, payload, type_key, state, created_at) " +
                "values (@rippleId, @waveId, '{}'::jsonb, @typeKey, 'Succeeded', @created)",
                new { rippleId, waveId, typeKey, created });
            await ExecuteAsync(
                "insert into ripple.splash(id, ripple_id, wave_id, attempt, claimed_at, started_at, ended_at, outcome, duration_ms, report) " +
                "values (gen_random_uuid(), @rippleId, @waveId, 0, @claimed, @claimed, @ended, 'Succeeded', @durationMs, null)",
                new { rippleId, waveId, claimed, ended, durationMs });
        }
    }

    [Fact]
    public async Task compaction_chunks_the_report_by_target_count()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedBatchAsync(wave.Id, ["a", "b", "c", "d", "e"]);

        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(claimed[0].Id, wave.Id, claimed[0].Attempt, Now(),
                """[{"outcome":"Succeeded","output":null,"targetIds":["a","b","c","d","e"]}]""")],
            "inst-1");
        await RefreshWaveStatsAsync();

        await CompactWaveAsync(wave.Id, chunkSize: 2);

        // 5 targets / 2 per chunk = 3 chunks; total target_count = 5.
        (await ScalarAsync("select count(*) from ripple.report_chunk where wave_id = @w", new { w = wave.Id })).ShouldBe(3);
        (await ScalarAsync("select coalesce(sum(target_count), 0) from ripple.report_chunk where wave_id = @w", new { w = wave.Id })).ShouldBe(5);
        (await Reports.GetReportAsync(wave.Id))!.Items.SelectMany(i => i.TargetIds).Count().ShouldBe(5);
    }

    [Fact]
    public async Task compact_ready_waves_processes_terminal_uncompacted_waves()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedBatchAsync(wave.Id, ["x"]);
        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(claimed[0].Id, wave.Id, claimed[0].Attempt, Now(),
                """[{"outcome":"Succeeded","output":null,"targetIds":["x"]}]""")],
            "inst-1");
        await RefreshWaveStatsAsync(); // wave ⇒ Completed

        (await Reports.CompactReadyWavesAsync(chunkSize: 10_000, maxWaves: 50)).ShouldBe(1);

        (await ScalarAsync("select count(*) from ripple.splash where wave_id = @w", new { w = wave.Id })).ShouldBe(0);
        var report = await Reports.GetReportAsync(wave.Id);
        report!.CompactedAt.ShouldNotBeNull();
        report.Items.SelectMany(i => i.TargetIds).ShouldContain("x");

        // A second pass finds nothing (the wave is already compacted).
        (await Reports.CompactReadyWavesAsync(10_000, 50)).ShouldBe(0);
    }

    [Fact]
    public async Task compaction_loop_auto_compacts_a_finished_wave()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedBatchAsync(wave.Id, ["t1", "t2"]);

        var sink = new ReportSink { OnExecute = _ => null }; // all succeed
        using var host = BuildEngineHost(
            engine => engine.AddHandler<RecalcContext, BatchTax, ReportingHandler>(),
            o =>
            {
                o.MaxConcurrency = 4;
                o.MaxPollDelay = TimeSpan.FromMilliseconds(100);
                // Unlike the other engine tests, this one is ABOUT compaction: let the loop run.
                o.CompactionInterval = TimeSpan.FromMilliseconds(200);
            },
            services => services.AddSingleton(sink));

        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            WaveReport? report = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                report = await Reports.GetReportAsync(wave.Id);
                if (report?.CompactedAt is not null)
                {
                    break;
                }

                await Task.Delay(100);
            }

            report.ShouldNotBeNull();
            report!.CompactedAt.ShouldNotBeNull();
            report.Items.SelectMany(i => i.TargetIds).ShouldBe(["t1", "t2"], ignoreOrder: true);
            (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @w", new { w = wave.Id })).ShouldBe(0);
            (await ScalarAsync("select count(*) from ripple.splash where wave_id = @w", new { w = wave.Id })).ShouldBe(0);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task compaction_stamps_expire_at_from_retention_and_null_keeps_forever()
    {
        await ResetAsync();

        var kept = await CompleteBatchWaveAsync(["a"]);
        await CompactWaveAsync(kept, retention: TimeSpan.FromHours(1)); // expire_at = completed_at + 1h (future)

        var forever = await CompleteBatchWaveAsync(["b"]);
        await CompactWaveAsync(forever, retention: null); // keep forever ⇒ expire_at null

        (await ScalarAsync(
            "select count(*) from ripple.wave where id = @w and expire_at > now()", new { w = kept })).ShouldBe(1);
        (await ScalarAsync(
            "select count(*) from ripple.wave where id = @w and expire_at is null", new { w = forever })).ShouldBe(1);
    }

    [Fact]
    public async Task purge_deletes_expired_compacted_waves_but_keeps_the_rest()
    {
        await ResetAsync();

        var expired = await CompleteBatchWaveAsync(["x"]);
        await CompactWaveAsync(expired, retention: TimeSpan.FromSeconds(-1)); // expire_at just in the past

        var fresh = await CompleteBatchWaveAsync(["y"]);
        await CompactWaveAsync(fresh, retention: TimeSpan.FromHours(1)); // not yet expired

        var active = await CreateWaveAsync(); // never compacted ⇒ expire_at null ⇒ never purged
        await SeedBatchAsync(active.Id, ["z"]);

        (await Reports.PurgeExpiredWavesAsync(50)).ShouldBe(1);

        (await Engine.GetWaveAsync(expired)).ShouldBeNull();
        (await ScalarAsync("select count(*) from ripple.report_chunk where wave_id = @w", new { w = expired })).ShouldBe(0);
        (await Engine.GetWaveAsync(fresh)).ShouldNotBeNull();
        (await Engine.GetWaveAsync(active.Id)).ShouldNotBeNull();
    }

    private static DateTimeOffset Now() => DateTimeOffset.UtcNow.AddSeconds(-1);

    private Task SeedBatchAsync(Guid waveId, string[] ids)
        => Engine.AddRipplesAsync(waveId,
            [new RippleSeed(JsonSerializer.Serialize(new BatchTax { Ids = ids }, Web), nameof(BatchTax))]);

    /// <summary>Seeds a one-batch-ripple wave, settles it succeeded, and refreshes to Completed; returns its id.</summary>
    private async Task<Guid> CompleteBatchWaveAsync(string[] ids)
    {
        var wave = await CreateWaveAsync();
        await SeedBatchAsync(wave.Id, ids);
        var claimed = await Engine.PollAsync(10, "inst", 0);
        var report = JsonSerializer.Serialize(new[] { new { outcome = "Succeeded", output = (string?)null, targetIds = ids } }, Web);
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(claimed[0].Id, wave.Id, claimed[0].Attempt, Now(), report)], "inst");
        await RefreshWaveStatsAsync();
        return wave.Id;
    }
}
