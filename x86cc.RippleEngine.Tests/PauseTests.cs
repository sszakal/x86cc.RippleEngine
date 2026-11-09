using Dapper;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Pausing/resuming a job type. Pause/resume are O(1) flips of the type's <c>pause_state</c>; correctness is
/// immediate (the claim skips a paused type at once and every 'Pending'-writer parks new work as 'Paused'), while
/// the (possibly millions of) existing ripples are moved between 'Pending' and 'Paused' asynchronously, in bounded
/// chunks, by the reconcile — so tests call <see cref="RippleTestBase.ReconcilePauseTransitionsAsync"/> (like they
/// call RefreshWaveStatsAsync) to drive the drain before asserting parked/un-parked states.
/// </summary>
public sealed class PauseTests : RippleTestBase
{
    private const string TypeA = "RecalcContext|TypeA";
    private const string TypeB = "RecalcContext|TypeB";

    [Fact]
    public async Task pausing_a_type_skips_the_claim_instantly_then_the_reconcile_parks_the_residual()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 3, "TypeA");
        await SeedRipplesOfTypeAsync(wave.Id, 3, "TypeB");

        await Engine.PauseTypeAsync(TypeA);

        // Instant correctness: the type's rows are still Pending (not yet reconciled), but the claim already
        // skips them via the pause_state backstop — a poll pulls only the un-paused type.
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(3);
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(3);
        claimed.ShouldAllBe(r => r.TypeKey == TypeB);

        // The reconcile then parks A's residual Pending rows out of the claim index.
        await ReconcilePauseTransitionsAsync();
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(3);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
    }

    [Fact]
    public async Task new_fan_out_for_a_paused_type_lands_directly_in_paused()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 1, "TypeA"); // seeded before the pause ⇒ Pending until reconciled

        await Engine.PauseTypeAsync(TypeA);
        await SeedRipplesOfTypeAsync(wave.Id, 2, "TypeA"); // seeded after ⇒ StateExpr parks them immediately

        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(2);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(1);

        await ReconcilePauseTransitionsAsync();
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(3);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
    }

    [Fact]
    public async Task in_flight_ripple_is_not_paused_and_a_retry_while_paused_lands_in_paused()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 1, "TypeA");

        // Claim it (now Running), THEN pause: the in-flight ripple must stay Running.
        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        claimed.Count.ShouldBe(1);
        await Engine.PauseTypeAsync(TypeA);
        (await CountAsync("state = 'Running'", null)).ShouldBe(1);

        // It then fails non-terminally (retry). Because the type is paused it requeues into 'Paused' (StateExpr),
        // not 'Pending' — a retry can't sneak paused work back into the claim.
        await Splashes.FailRipplesAsync(
            [new RippleFailure(claimed[0].Id, wave.Id, claimed[0].Attempt, Ago(1), null,
                Terminal: false, DateTimeOffset.UtcNow.AddHours(-1))],
            "inst-1");

        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(1);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
        (await Engine.PollAsync(10, "inst-2", 0)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task recovery_of_a_paused_types_stranded_work_requeues_into_paused()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 2, "TypeA");

        // A now-dead instance claims both (Running); the type is then paused; the instance goes stale.
        await Engine.PollAsync(100, "dead", 0);
        await Engine.PauseTypeAsync(TypeA);
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = 'dead'");

        var requeued = await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor");

        requeued.ShouldBe(2);
        // Reclaimed paused work parks (StateExpr) rather than re-entering the claim.
        (await CountAsync("type_key = @k and state = 'Paused' and claimed_by is null", TypeA)).ShouldBe(2);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
    }

    [Fact]
    public async Task resume_as_is_unparks_and_leaves_schedule_order_untouched()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 3, "TypeA");
        var before = await SumScheduleOrderAsync(TypeA);

        await Engine.PauseTypeAsync(TypeA);
        await ReconcilePauseTransitionsAsync(); // park
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(3);

        await Engine.ResumeTypeAsync(TypeA, rebase: false);
        await ReconcilePauseTransitionsAsync(); // un-park

        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(3);
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(0);
        (await PauseStateCountAsync(TypeA, "active")).ShouldBe(1); // drained ⇒ back to active
        (await SumScheduleOrderAsync(TypeA)).ShouldBe(before);     // as-is ⇒ schedule_order unchanged
    }

    [Fact]
    public async Task resume_with_rebase_restamps_schedule_order_onto_the_current_frontier()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 3, "TypeA");
        var origMax = await DoubleAsync(
            "select max(schedule_order) from ripple.ripple where type_key = @k", new { k = TypeA });

        await Engine.PauseTypeAsync(TypeA);
        await ReconcilePauseTransitionsAsync(); // park
        // Let virtual time (the DB clock the frontier clamps up to) advance past the parked work's stale slots.
        await Task.Delay(1200);
        await Engine.ResumeTypeAsync(TypeA, rebase: true);
        await ReconcilePauseTransitionsAsync(); // un-park + rebase

        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(3);
        (await PauseStateCountAsync(TypeA, "active")).ShouldBe(1);
        var newMin = await DoubleAsync(
            "select min(schedule_order) from ripple.ripple where type_key = @k", new { k = TypeA });
        newMin.ShouldBeGreaterThan(origMax); // rebased forward to the frontier — interleaves rather than floods
    }

    [Fact]
    public async Task a_wave_whose_remaining_work_is_all_paused_does_not_complete()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 3, "TypeA");

        await Engine.PauseTypeAsync(TypeA);
        await ReconcilePauseTransitionsAsync(); // park so pending=0, paused=3
        await RefreshWaveStatsAsync();

        var paused = await Engine.GetWaveAsync(wave.Id);
        paused!.Pending.ShouldBe(0);
        paused.Running.ShouldBe(0);
        paused.Paused.ShouldBe(3);
        paused.Status.ShouldBe(WaveStatus.Active); // parked work is not done — must stay Active

        // Resume, drain, and run to completion.
        await Engine.ResumeTypeAsync(TypeA, rebase: false);
        await ReconcilePauseTransitionsAsync();
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        await Splashes.CompleteRipplesAsync(
            claimed.Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, Ago(1), null)).ToList(), "inst-1");

        await RefreshWaveStatsAsync();
        var done = await Engine.GetWaveAsync(wave.Id);
        done!.Paused.ShouldBe(0);
        done.Succeeded.ShouldBe(3);
        done.Status.ShouldBe(WaveStatus.Completed);
    }

    [Fact]
    public async Task pause_reconcile_drains_a_large_set_in_bounded_chunks()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 25, "TypeA");
        await Engine.PauseTypeAsync(TypeA);

        // A capped pass moves at most maxRowsPerPass, leaving the rest for later ticks.
        var moved = await ReconcilePauseTransitionsAsync(chunkSize: 10, maxRowsPerPass: 10);
        moved.ShouldBe(10);
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(10);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(15);

        // Subsequent passes drain the remainder.
        await ReconcilePauseTransitionsAsync(chunkSize: 10, maxRowsPerPass: 1000);
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(25);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
    }

    [Fact]
    public async Task resume_streams_back_in_chunks_and_flips_to_active_only_when_drained()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 25, "TypeA");
        await Engine.PauseTypeAsync(TypeA);
        await ReconcilePauseTransitionsAsync(); // fully parked

        await Engine.ResumeTypeAsync(TypeA, rebase: true);

        // A capped pass un-parks only some; the type stays 'resuming' until the Paused set is exhausted.
        await ReconcilePauseTransitionsAsync(chunkSize: 10, maxRowsPerPass: 10);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(10);
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(15);
        (await PauseStateCountAsync(TypeA, "resuming_rebase")).ShouldBe(1);

        await ReconcilePauseTransitionsAsync(); // drains the rest
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(25);
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(0);
        (await PauseStateCountAsync(TypeA, "active")).ShouldBe(1);
    }

    [Fact]
    public async Task re_pause_midway_through_a_resume_converges_back_to_fully_paused()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 10, "TypeA");
        await Engine.PauseTypeAsync(TypeA);
        await ReconcilePauseTransitionsAsync(); // fully parked

        await Engine.ResumeTypeAsync(TypeA, rebase: true);
        await ReconcilePauseTransitionsAsync(chunkSize: 3, maxRowsPerPass: 3); // partial un-park
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(3);

        // Change our mind mid-resume: pause again. The reconcile re-parks the un-parked rows and converges.
        await Engine.PauseTypeAsync(TypeA);
        await ReconcilePauseTransitionsAsync();
        (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(10);
        (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
        (await PauseStateCountAsync(TypeA, "paused")).ShouldBe(1);
    }

    /// <summary>
    /// A reschedule and a pause (then a resume) of the SAME type, genuinely concurrent, round after round. Both
    /// select rows with <c>for update skip locked</c>, so neither ever waits on the other — the risk is not a
    /// deadlock but a ripple left behind: skipped by a reconcile pass while a reschedule held it, and then never
    /// parked (a "paused" type that still has claimable work) or never un-parked (a wave that can't complete).
    /// Whatever the interleaving, every ripple must converge to the desired state, and the claim must drain the
    /// exact seeded set, once each, in schedule_order.
    /// </summary>
    [Fact]
    public async Task a_reschedule_racing_a_pause_and_a_resume_of_the_same_type_loses_nothing()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        const int count = 3_000;
        await SeedRipplesOfTypeAsync(wave.Id, count, "TypeA");
        await SeedRipplesOfTypeAsync(wave.Id, 50, "TypeB"); // a bystander type that must stay claimable

        await using var conn = await Db.OpenConnectionAsync();
        var seeded = (await conn.QueryAsync<Guid>(
            "select id from ripple.ripple where type_key = @k", new { k = TypeA })).ToHashSet();
        var overlapped = 0;

        for (var round = 0; round < 4; round++)
        {
            await Engine.PauseTypeAsync(TypeA);
            overlapped += await RaceAsync(wave.Id);
            (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(count, $"round {round}: pause");
            (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(0);
            (await PauseStateCountAsync(TypeA, "paused")).ShouldBe(1);

            await Engine.ResumeTypeAsync(TypeA, rebase: round % 2 == 0);
            overlapped += await RaceAsync(wave.Id);
            (await CountAsync("type_key = @k and state = 'Pending'", TypeA)).ShouldBe(count, $"round {round}: resume");
            (await CountAsync("type_key = @k and state = 'Paused'", TypeA)).ShouldBe(0);
            (await PauseStateCountAsync(TypeA, "active")).ShouldBe(1);
        }

        // Proof the race was real: some reschedules re-stamped rows while a reconcile was still moving the type.
        overlapped.ShouldBeGreaterThan(0, "no reschedule ever overlapped a reconcile — the test raced nothing");
        (await CountAsync("type_key = @k and state = 'Pending'", TypeB)).ShouldBe(50);

        // The whole set is claimable exactly once — nothing lost, nothing duplicated — and in schedule_order: the
        // claim takes the LOWEST slots, so each batch sorts at or after the one before it. (Within one batch the
        // rows come back in no particular order — the claim's final select has no order by — so compare batches.)
        var orderOf = (await conn.QueryAsync<(Guid Id, double Order)>(
                "select id, schedule_order from ripple.ripple where wave_id = @id", new { id = wave.Id }))
            .ToDictionary(r => r.Id, r => r.Order);
        var claimed = new List<Guid>();
        var previousMax = double.MinValue;
        while (await Engine.PollAsync(100, "inst-1", 0) is { Count: > 0 } batch)
        {
            var orders = batch.Select(r => orderOf[r.Id]).ToList();
            orders.Min().ShouldBeGreaterThanOrEqualTo(previousMax, "a later claim took an earlier slot");
            previousMax = orders.Max();
            claimed.AddRange(batch.Where(r => r.TypeKey == TypeA).Select(r => r.Id));
        }

        claimed.Count.ShouldBe(count);
        claimed.ToHashSet().SetEquals(seeded).ShouldBeTrue("the claim must drain exactly the seeded ripples");
    }

    /// <summary>
    /// Drives the pause reconcile to completion in small chunks while a loop keeps re-stamping the same wave's
    /// type, then lets the loop finish and drains once more — as the next <c>PauseTransitionLoop</c> tick would — to
    /// pick up every row a reschedule's lock made a pass skip. Returns how many reschedules moved rows while the
    /// reconcile was still running.
    /// </summary>
    private async Task<int> RaceAsync(Guid waveId)
    {
        var reconciling = true;
        var overlapped = 0;
        var reschedules = Task.Run(async () =>
        {
            for (var i = 0; Volatile.Read(ref reconciling); i++)
            {
                var moved = await Engine.RescheduleWaveAsync(waveId, batchSize: 3 + i % 5, gapSeconds: 1 + i * 0.25,
                    typeKey: TypeA);
                if (moved > 0 && Volatile.Read(ref reconciling))
                {
                    overlapped++;
                }
            }
        });

        // Small chunks and passes: the reconcile is many short transactions interleaved with the reschedules.
        while (await ReconcilePauseTransitionsAsync(chunkSize: 50, maxRowsPerPass: 200) > 0)
        {
        }

        Volatile.Write(ref reconciling, false);
        await reschedules;

        while (await ReconcilePauseTransitionsAsync(chunkSize: 50, maxRowsPerPass: 200) > 0)
        {
        }

        return overlapped;
    }

    private async Task<long> CountAsync(string where, string? typeKey) =>
        await ScalarAsync($"select count(*) from ripple.ripple where {where}", new { k = typeKey });

    private Task<long> PauseStateCountAsync(string typeKey, string state) =>
        ScalarAsync("select count(*) from ripple.type_schedule where type_key = @k and pause_state = @s",
            new { k = typeKey, s = state });

    private Task<double> SumScheduleOrderAsync(string typeKey) =>
        DoubleAsync("select coalesce(sum(schedule_order), 0) from ripple.ripple where type_key = @k",
            new { k = typeKey });

    private static DateTimeOffset Ago(int seconds) => DateTimeOffset.UtcNow.AddSeconds(-seconds);
}
