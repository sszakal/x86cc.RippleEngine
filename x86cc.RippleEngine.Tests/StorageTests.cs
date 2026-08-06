using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Lower-level tests over the two stores: the global schedule_order claim, settlement, and recovery. Wave live
/// numbers come from the DB-side stats refresh, so a test calls <c>RefreshWaveStatsAsync</c> before asserting them.
/// </summary>
public sealed class StorageTests : RippleTestBase
{
    [Fact]
    public async Task concurrent_claims_get_disjoint_slices_and_carry_the_wave_payload()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 8);

        var results = await Task.WhenAll(
            Engine.PollAsync(5, "inst-1", 0),
            Engine.PollAsync(5, "inst-2", 0));

        var a = results[0].Select(r => r.Id).ToHashSet();
        var b = results[1].Select(r => r.Id).ToHashSet();

        a.Overlaps(b).ShouldBeFalse();
        (a.Count + b.Count).ShouldBe(8);
        results[0].ShouldAllBe(r => r.Attempt == 1);
        // The shared wave payload rides along with every claimed ripple.
        results[0].ShouldAllBe(r => r.WavePayloadType == nameof(RecalcContext) && r.WavePayload != null);

        await RefreshWaveStatsAsync();
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Pending.ShouldBe(0);
        reloaded.Running.ShouldBe(8);

        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Running'")).ShouldBe(8);
    }

    [Fact]
    public async Task poll_writes_a_heartbeat_even_when_it_claims_nothing()
    {
        await ResetAsync();

        // No ripples and limit 0: nothing to claim, but the beat must still land.
        var claimed = await Engine.PollAsync(limit: 0, "inst-1", executing: 3);

        claimed.Count.ShouldBe(0);
        var beats = await Engine.GetHeartbeatsAsync();
        beats.Count.ShouldBe(1);
        beats[0].InstanceId.ShouldBe("inst-1");
        beats[0].Executing.ShouldBe(3);
    }

    [Fact]
    public async Task claim_pulls_globally_lowest_scheduled_first_across_waves()
    {
        await ResetAsync();
        var older = await CreateWaveAsync(legislation: "OLD");
        await SeedRipplesAsync(older.Id, 3);
        await Task.Delay(20); // ensure the second wave bases off a strictly later now()
        var newer = await CreateWaveAsync(legislation: "NEW");
        await SeedRipplesAsync(newer.Id, 3);

        var claimed = await Engine.PollAsync(3, "inst-1", 0);

        // Default schedule (one slot for 3 ripples): the first wave's base (now() at its fan-out) is lower, so
        // its three come first.
        claimed.Count.ShouldBe(3);
        claimed.ShouldAllBe(r => r.WaveId == older.Id);
        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(older.Id))!.Pending.ShouldBe(0);
        (await Engine.GetWaveAsync(newer.Id))!.Pending.ShouldBe(3);
    }

    [Fact]
    public async Task completing_all_ripples_drains_and_completes_the_wave()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 6);

        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(6);

        await Splashes.CompleteRipplesAsync(
            claimed.Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, Ago(1),
                """[{"outcome":"Succeeded","output":"done","targetIds":["t"]}]""")).ToList(),
            "inst-1");

        await RefreshWaveStatsAsync();
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Pending.ShouldBe(0);
        reloaded.Running.ShouldBe(0);
        reloaded.Succeeded.ShouldBe(6);
        reloaded.Status.ShouldBe(WaveStatus.Completed);
        reloaded.CompletedAt.ShouldNotBeNull();

        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Succeeded'")).ShouldBe(6);
        (await ScalarAsync("""select count(*) from ripple.splash where report @> '[{"output": "done"}]'""")).ShouldBe(6);
    }

    [Fact]
    public async Task terminal_failure_faults_the_wave_when_drained()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 6);

        var claimed = await Engine.PollAsync(100, "inst-1", 0);

        await Splashes.FailRipplesAsync(
            [new RippleFailure(claimed[0].Id, wave.Id, claimed[0].Attempt, Ago(1), null, Terminal: true, null)],
            "inst-1");
        await Splashes.CompleteRipplesAsync(
            claimed.Skip(1).Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, Ago(1), null)).ToList(),
            "inst-1");

        await RefreshWaveStatsAsync();
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Pending.ShouldBe(0);
        reloaded.Running.ShouldBe(0);
        reloaded.Succeeded.ShouldBe(5);
        reloaded.Failed.ShouldBe(1);
        reloaded.Status.ShouldBe(WaveStatus.Faulted);
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Failed'")).ShouldBe(1);
    }

    [Fact]
    public async Task non_terminal_failure_requeues_the_ripple_with_a_backoff()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 3);

        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        var future = DateTimeOffset.UtcNow.AddMinutes(5);

        await Splashes.FailRipplesAsync(
            [new RippleFailure(claimed[0].Id, wave.Id, claimed[0].Attempt, Ago(1), null, Terminal: false, future)],
            "inst-1");

        await RefreshWaveStatsAsync();
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Running.ShouldBe(2);
        reloaded.Pending.ShouldBe(1);
        reloaded.Status.ShouldBe(WaveStatus.Active);

        (await ScalarAsync(
            "select count(*) from ripple.ripple where state = 'Pending' and next_attempt_at is not null")).ShouldBe(1);

        // Not yet eligible (backoff in the future), so a claim skips it.
        (await Engine.PollAsync(10, "inst-1", 0)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task recovery_requeues_ripples_of_stale_instances_and_prunes_them()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 5);

        // "dead" claims all 5 (the poll also writes its heartbeat)...
        await Engine.PollAsync(100, "dead", 0);
        // ...then goes silent: age its heartbeat past the recovery threshold.
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = 'dead'");

        var requeued = await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor");

        requeued.ShouldBe(5);
        (await ScalarAsync(
            "select count(*) from ripple.ripple where state = 'Pending' and claimed_by is null")).ShouldBe(5);
        // Each interrupted attempt is recorded as an Abandoned splash (so an outcome-less ripple is explained).
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Abandoned'")).ShouldBe(5);
        // The dead heartbeat row is pruned.
        (await ScalarAsync("select count(*) from ripple.instance_heartbeat where instance_id = 'dead'")).ShouldBe(0);

        await RefreshWaveStatsAsync();
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Running.ShouldBe(0);
        reloaded.Pending.ShouldBe(5);
        reloaded.Status.ShouldBe(WaveStatus.Active);
    }

    [Fact]
    public async Task recovery_poisons_a_ripple_that_has_exhausted_its_attempts()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        // The retry ceiling is per-type config now: give this type max_attempts = 1 via type_schedule.
        await SeedScheduleAsync("RecalcContext|RecalcCompany", batchSize: 1000, gapSeconds: 1, maxAttempts: 1);
        await SeedRipplesAsync(wave.Id, 1);

        // Claimed once => attempt == max_attempts (1). The owner then dies mid-run.
        await Engine.PollAsync(100, "dead", 0);
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = 'dead'");

        var reclaimed = await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor");

        reclaimed.ShouldBe(1);
        // Attempt budget spent => terminally Failed (poison), not requeued.
        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Failed'")).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Pending'")).ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Abandoned'")).ShouldBe(1);

        await RefreshWaveStatsAsync();
        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Running.ShouldBe(0);
        reloaded.Pending.ShouldBe(0);
        reloaded.Failed.ShouldBe(1);
        reloaded.Status.ShouldBe(WaveStatus.Faulted);
    }

    [Fact]
    public async Task recovery_leaves_live_instances_and_self_untouched()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 4);

        // A fresh heartbeat (just written by the poll) is not stale, so nothing is recovered — and a
        // survivor must never recover its own in-flight ripples.
        await Engine.PollAsync(100, "survivor", 0);

        var requeued = await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor");

        requeued.ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Running'")).ShouldBe(4);
    }

    [Fact]
    public async Task self_recovery_reclaims_own_running_ripples_it_is_not_executing()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 4);

        // "me" claims all 4 (they're Running, claimed_by='me') but only actually executes one of them — the
        // other three were stranded (claimed but never handed to a handler, or lost to a fault).
        var claimed = await Engine.PollAsync(100, "me", 0);
        claimed.Count.ShouldBe(4);
        var stillRunning = claimed[0].Id;

        // grace = 0 ⇒ anything claimed before "now" is eligible; keepIds = the one id we're genuinely running.
        var reclaimed = await Engine.RecoverSelfStrandedAsync("me", new[] { stillRunning }, TimeSpan.Zero);

        reclaimed.ShouldBe(3);
        (await ScalarAsync(
            "select count(*) from ripple.ripple where state = 'Pending' and claimed_by is null")).ShouldBe(3);
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Abandoned'")).ShouldBe(3);
        // The one we're actually executing is left alone.
        (await ScalarAsync("select count(*) from ripple.ripple where id = @id and state = 'Running' and claimed_by = 'me'",
            new { id = stillRunning })).ShouldBe(1);
    }

    [Fact]
    public async Task self_recovery_leaves_freshly_claimed_ripples_within_the_grace_window()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 3);

        // Just claimed — even with NO id in the in-flight set, the grace window protects them so a ripple
        // isn't reaped in the tiny gap between the DB claim and being posted to the execute block.
        await Engine.PollAsync(100, "me", 0);

        var reclaimed = await Engine.RecoverSelfStrandedAsync("me", Array.Empty<Guid>(), TimeSpan.FromMinutes(1));

        reclaimed.ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Running' and claimed_by = 'me'")).ShouldBe(3);
    }

    [Fact]
    public async Task requeued_ripple_is_reclaimable_once_its_backoff_has_elapsed()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);

        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        claimed.Count.ShouldBe(1);

        // Requeue with a backoff well in the past — the next poll must re-claim it. Use a large offset so a
        // clock skew between the test host and the Postgres container can't leave it "not yet eligible".
        await Splashes.FailRipplesAsync(
            [new RippleFailure(claimed[0].Id, wave.Id, claimed[0].Attempt, Ago(1), null,
                Terminal: false, DateTimeOffset.UtcNow.AddHours(-1))],
            "inst-1");

        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Pending'")).ShouldBe(1);
        var reclaimed = await Engine.PollAsync(10, "inst-2", 0);
        reclaimed.Count.ShouldBe(1);
        reclaimed[0].Attempt.ShouldBe(2);
    }

    private static DateTimeOffset Ago(int seconds) => DateTimeOffset.UtcNow.AddSeconds(-seconds);
}
