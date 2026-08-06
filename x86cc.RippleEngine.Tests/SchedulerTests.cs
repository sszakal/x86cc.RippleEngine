using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The batch-interleaving scheduler: fan-out stamps <c>schedule_order</c> (batches of <c>batch_size</c> spaced by
/// <c>gap_seconds</c>), the claim pulls the globally lowest <c>schedule_order</c> first, continuation appends
/// after a job's pending work while a completed job restarts at <c>now()</c>, and concurrent claims stay
/// disjoint. Completion + live numbers come from the DB-side stats refresh.
/// </summary>
public sealed class SchedulerTests : RippleTestBase
{
    private const string RecalcTypeKey = "RecalcContext|RecalcCompany";

    [Fact]
    public async Task fanout_stamps_ripples_into_gapped_batches()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 3, gapSeconds: 10);

        await SeedRipplesAsync(wave.Id, 7);

        // 7 ripples, batch 3 => 3 slots (3 + 3 + 1), each 10s apart => span of 2 gaps = 20s.
        (await ScalarAsync("select count(distinct schedule_order) from ripple.ripple")).ShouldBe(3);
        (await ScalarAsync(
            "select (max(schedule_order) - min(schedule_order))::bigint from ripple.ripple"))
            .ShouldBe(20);
        // The batch sizes: 3, 3, 1.
        var slotCounts = (await QueryLongsAsync(
            "select count(*) from ripple.ripple group by schedule_order order by schedule_order")).ToList();
        slotCounts.ShouldBe([3, 3, 1]);
    }

    [Fact]
    public async Task claim_interleaves_two_competing_jobs_by_schedule_order()
    {
        await ResetAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 10);

        // Two jobs of the same type. Each ripple is its own batch, 10s apart. Job B is fanned out a beat after
        // A (base = its own now()), so its batches fall just after A's => the global order interleaves A,B,A,B…
        var a = await CreateWaveAsync(legislation: "A");
        await SeedRipplesAsync(a.Id, 3);
        var b = await CreateWaveAsync(legislation: "B");
        await SeedRipplesAsync(b.Id, 3);

        var order = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var claimed = await Engine.PollAsync(1, "inst-1", 0);
            claimed.Count.ShouldBe(1);
            order.Add(claimed[0].WaveId);
        }

        // Strict interleave: A, B, A, B, A, B.
        order.ShouldBe([a.Id, b.Id, a.Id, b.Id, a.Id, b.Id]);
    }

    [Fact]
    public async Task continuation_appends_after_a_jobs_pending_work()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 100);

        await SeedRipplesAsync(wave.Id, 2);      // created_at = t1, schedule_order = base .. base+100
        await SeedRipplesAsync(wave.Id, 2);      // created_at = t2 (later), appended after the pending work

        // Every ripple from the later fan-out is scheduled at or after the last position of the earlier one —
        // FIFO within the job, no jumping ahead of already-queued work.
        (await ScalarAsync(
            """
            select case when
                (select min(schedule_order) from ripple.ripple
                 where created_at = (select max(created_at) from ripple.ripple))
                >=
                (select max(schedule_order) from ripple.ripple
                 where created_at = (select min(created_at) from ripple.ripple))
            then 1 else 0 end
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task completed_job_restarts_at_now_without_a_historical_penalty()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        // Big gap pushes the first fan-out's tail hours into the virtual future.
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 3600);

        await SeedRipplesAsync(wave.Id, 3);      // schedule_order now, now+1h, now+2h
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(3);
        await Splashes.CompleteRipplesAsync(
            claimed.Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, Ago(1), null)).ToList(),
            "inst-1");

        // With no pending work left, the next fan-out bases off now() — not the old +2h tail.
        await SeedRipplesAsync(wave.Id, 1);
        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Pending'")).ShouldBe(1);
        (await ScalarAsync(
            "select count(*) from ripple.ripple where state = 'Pending' and schedule_order <= extract(epoch from now()) + 60"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task refresh_wave_stats_tracks_numbers_and_completes_a_drained_wave()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 5);

        // Before any claim: all pending.
        await RefreshWaveStatsAsync();
        var seeded = await Engine.GetWaveAsync(wave.Id);
        seeded!.Status.ShouldBe(WaveStatus.Active);
        seeded.Pending.ShouldBe(5);
        seeded.Succeeded.ShouldBe(0);

        // Claimed but not settled: running, not yet drained.
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(5);
        await RefreshWaveStatsAsync();
        var running = await Engine.GetWaveAsync(wave.Id);
        running!.Status.ShouldBe(WaveStatus.Active);
        running.Running.ShouldBe(5);
        running.Pending.ShouldBe(0);

        // All settled: the refresh flips the drained wave to Completed and derives succeeded.
        await Splashes.CompleteRipplesAsync(
            claimed.Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, Ago(1), null)).ToList(),
            "inst-1");
        await RefreshWaveStatsAsync();
        var done = await Engine.GetWaveAsync(wave.Id);
        done!.Status.ShouldBe(WaveStatus.Completed);
        done.Succeeded.ShouldBe(5);
        done.Pending.ShouldBe(0);
        done.Running.ShouldBe(0);
        done.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task concurrent_claims_get_disjoint_slices()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 100);

        // Two instances poll at the same time — FOR UPDATE SKIP LOCKED must hand them non-overlapping sets.
        var t1 = Engine.PollAsync(50, "inst-1", 0);
        var t2 = Engine.PollAsync(50, "inst-2", 0);
        var results = await Task.WhenAll(t1, t2);

        var ids1 = results[0].Select(r => r.Id).ToHashSet();
        var ids2 = results[1].Select(r => r.Id).ToHashSet();

        ids1.Overlaps(ids2).ShouldBeFalse();
        // No ripple was double-claimed: the total claimed equals the number now Running.
        (ids1.Count + ids2.Count).ShouldBe(
            (int)await ScalarAsync("select count(*) from ripple.ripple where state = 'Running'"));
    }

    [Fact]
    public async Task new_job_arriving_mid_flight_starts_at_the_frontier_not_wallclock()
    {
        await ResetAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 100);

        // Job A: 10 ripples, each its own slot 100s apart => schedule_order base_A + 0,100,…,900.
        var a = await CreateWaveAsync(legislation: "A");
        await SeedRipplesAsync(a.Id, 10);

        // Drain A's first 5 slots, so its frontier (lowest pending schedule_order) advances ~500s into the
        // virtual future — far ahead of the wall clock.
        var claimed = await Engine.PollAsync(5, "inst-1", 0);
        claimed.Count.ShouldBe(5);
        await Splashes.CompleteRipplesAsync(
            claimed.Select(r => new RippleCompletion(r.Id, a.Id, r.Attempt, Ago(1), null)).ToList(),
            "inst-1");

        // Job B arrives now. Its base must clamp to A's frontier (the current global min pending), NOT
        // wall-now — otherwise B's slots would all precede A's remaining work and B would monopolise the
        // cluster to "catch up".
        var b = await CreateWaveAsync(legislation: "B");
        await SeedRipplesAsync(b.Id, 3);

        // B starts at or after A's frontier (it interleaves), never before it.
        (await ScalarAsync(
            """
            select case when
                (select min(schedule_order) from ripple.ripple where wave_id = @b)
                >= (select min(schedule_order) from ripple.ripple where wave_id = @a and state = 'Pending')
            then 1 else 0 end
            """, new { a = a.Id, b = b.Id })).ShouldBe(1);

        // …and that base is far in the future vs now(), proving the clamp fired (base != wall-now, which would
        // have put B ~500s in the past relative to the frontier).
        (await ScalarAsync(
            "select case when (select min(schedule_order) from ripple.ripple where wave_id = @b) > extract(epoch from now()) + 60 then 1 else 0 end",
            new { b = b.Id })).ShouldBe(1);
    }

    private static DateTimeOffset Ago(int seconds) => DateTimeOffset.UtcNow.AddSeconds(-seconds);
}
