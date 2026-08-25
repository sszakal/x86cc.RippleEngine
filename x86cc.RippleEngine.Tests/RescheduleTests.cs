using Dapper;
using Shouldly;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// In-place reschedule of a live wave: re-stamping its remaining <c>Pending</c> ripples with a new batch/gap,
/// anchored at the wave's own head. The one exception to "schedule_order is stamped once and never rebalanced",
/// so these tests pin what it must NOT disturb — the wave's position in the global queue, in-flight ripples,
/// other waves, and the per-<c>type_key</c> independence the fan-out relies on.
/// </summary>
public sealed class RescheduleTests : RippleTestBase
{
    private const string RecalcTypeKey = "RecalcContext|RecalcCompany";
    private const string PersonTypeKey = "RecalcContext|RecalcPerson";

    [Fact]
    public async Task reschedule_holds_the_head_and_stretches_the_tail()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedRipplesAsync(wave.Id, 10);

        // 10 ripples, batch 1, gap 1 => 10 slots spanning 9 seconds of virtual time.
        (await ScalarAsync("select (max(schedule_order) - min(schedule_order))::bigint from ripple.ripple"))
            .ShouldBe(9);
        var head = await DoubleAsync("select min(schedule_order) from ripple.ripple");

        var moved = await Engine.RescheduleWaveAsync(wave.Id, batchSize: 1, gapSeconds: 10);

        moved.ShouldBe(10);
        // The head is the anchor: the wave stays exactly where it was in the global queue...
        (await DoubleAsync("select min(schedule_order) from ripple.ripple")).ShouldBe(head, 0.0001);
        // ...and only its density changes — same 10 slots, now 10s apart instead of 1s.
        (await ScalarAsync("select count(distinct schedule_order) from ripple.ripple")).ShouldBe(10);
        (await ScalarAsync("select (max(schedule_order) - min(schedule_order))::bigint from ripple.ripple"))
            .ShouldBe(90);
    }

    [Fact]
    public async Task reschedule_regroups_ripples_into_new_batches()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 10);
        await SeedRipplesAsync(wave.Id, 10);

        var before = (await QueryLongsAsync(
            "select count(*) from ripple.ripple group by schedule_order order by schedule_order")).ToList();
        before.ShouldBe([1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);

        // Batch 5 packs five ripples per slot: 2 slots, one gap apart.
        var moved = await Engine.RescheduleWaveAsync(wave.Id, batchSize: 5, gapSeconds: 10);

        moved.ShouldBe(10);
        var after = (await QueryLongsAsync(
            "select count(*) from ripple.ripple group by schedule_order order by schedule_order")).ToList();
        after.ShouldBe([5, 5]);
        (await ScalarAsync("select (max(schedule_order) - min(schedule_order))::bigint from ripple.ripple"))
            .ShouldBe(10);
    }

    [Fact]
    public async Task reschedule_lowers_a_jobs_share_against_a_competing_job()
    {
        await ResetAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);

        // Two jobs of the same type, each ripple its own batch 1s apart => they interleave A,B,A,B…
        var a = await CreateWaveAsync(legislation: "A");
        await SeedRipplesAsync(a.Id, 6);
        var b = await CreateWaveAsync(legislation: "B");
        await SeedRipplesAsync(b.Id, 6);

        // Throttle A by 100x. Its head keeps its slot, but every batch behind it moves 100s out, so B — which
        // was NOT touched — now owns nearly the whole near end of the queue.
        (await Engine.RescheduleWaveAsync(a.Id, batchSize: 1, gapSeconds: 100)).ShouldBe(6);

        var order = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var claimed = await Engine.PollAsync(1, "inst-1", 0);
            claimed.Count.ShouldBe(1);
            order.Add(claimed[0].WaveId);
        }

        // Only A's anchored head can still be in front; the other five claims are all B's.
        order.Count(id => id == b.Id).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task reschedule_leaves_running_ripples_alone()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedRipplesAsync(wave.Id, 6);

        var claimed = await Engine.PollAsync(2, "inst-1", 0);
        claimed.Count.ShouldBe(2);
        var runningBefore = await QueryLongsAsync(
            "select round(schedule_order)::bigint from ripple.ripple where state = 'Running' order by 1");

        var moved = await Engine.RescheduleWaveAsync(wave.Id, batchSize: 1, gapSeconds: 60);

        // Only the 4 still pending are re-stamped; the in-flight pair is out of scope (and would return to its
        // old slot anyway — retry/recovery requeues keep schedule_order deliberately).
        moved.ShouldBe(4);
        (await QueryLongsAsync(
            "select round(schedule_order)::bigint from ripple.ripple where state = 'Running' order by 1"))
            .ShouldBe(runningBefore);
    }

    [Fact]
    public async Task reschedule_leaves_other_waves_untouched()
    {
        await ResetAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        var a = await CreateWaveAsync(legislation: "A");
        await SeedRipplesAsync(a.Id, 5);
        var b = await CreateWaveAsync(legislation: "B");
        await SeedRipplesAsync(b.Id, 5);

        var bBefore = await QueryLongsAsync(
            "select round(schedule_order)::bigint from ripple.ripple where wave_id = @id order by 1",
            new { id = b.Id });

        (await Engine.RescheduleWaveAsync(a.Id, batchSize: 1, gapSeconds: 30)).ShouldBe(5);

        (await QueryLongsAsync(
            "select round(schedule_order)::bigint from ripple.ripple where wave_id = @id order by 1",
            new { id = b.Id }))
            .ShouldBe(bBefore);
    }

    [Fact]
    public async Task reschedule_anchors_each_type_key_at_its_own_head()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedScheduleAsync(PersonTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedRipplesAsync(wave.Id, 4);
        await SeedRipplesOfTypeAsync(wave.Id, 4, "RecalcPerson");

        var companyHead = await DoubleAsync(
            "select min(schedule_order) from ripple.ripple where type_key = @k", new { k = RecalcTypeKey });
        var personHead = await DoubleAsync(
            "select min(schedule_order) from ripple.ripple where type_key = @k", new { k = PersonTypeKey });

        // No typeKey filter => every type the wave still has pending, each re-stamped independently.
        (await Engine.RescheduleWaveAsync(wave.Id, batchSize: 1, gapSeconds: 50)).ShouldBe(8);

        // Each type holds ITS OWN position and stretches from there — they interleave rather than stacking,
        // the same rule the fan-out's per-type base enforces.
        (await DoubleAsync("select min(schedule_order) from ripple.ripple where type_key = @k",
            new { k = RecalcTypeKey })).ShouldBe(companyHead, 0.0001);
        (await DoubleAsync("select min(schedule_order) from ripple.ripple where type_key = @k",
            new { k = PersonTypeKey })).ShouldBe(personHead, 0.0001);
        (await ScalarAsync(
            "select (max(schedule_order) - min(schedule_order))::bigint from ripple.ripple where type_key = @k",
            new { k = RecalcTypeKey })).ShouldBe(150);
    }

    [Fact]
    public async Task reschedule_can_target_a_single_type_key()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedScheduleAsync(PersonTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedRipplesAsync(wave.Id, 4);
        await SeedRipplesOfTypeAsync(wave.Id, 4, "RecalcPerson");

        var personBefore = await QueryLongsAsync(
            "select round(schedule_order)::bigint from ripple.ripple where type_key = @k order by 1",
            new { k = PersonTypeKey });

        (await Engine.RescheduleWaveAsync(wave.Id, batchSize: 1, gapSeconds: 50, typeKey: RecalcTypeKey))
            .ShouldBe(4);

        (await ScalarAsync(
            "select (max(schedule_order) - min(schedule_order))::bigint from ripple.ripple where type_key = @k",
            new { k = RecalcTypeKey })).ShouldBe(150);
        (await QueryLongsAsync(
            "select round(schedule_order)::bigint from ripple.ripple where type_key = @k order by 1",
            new { k = PersonTypeKey }))
            .ShouldBe(personBefore);
    }

    [Fact]
    public async Task reschedule_of_a_drained_wave_is_a_no_op()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedRipplesAsync(wave.Id, 3);

        (await Engine.PollAsync(3, "inst-1", 0)).Count.ShouldBe(3); // nothing left pending

        // Not an error and not a lock conflict — just zero rows in scope.
        (await Engine.RescheduleWaveAsync(wave.Id, batchSize: 1, gapSeconds: 60)).ShouldBe(0);
    }

    [Fact]
    public async Task reschedule_skips_ripples_a_concurrent_claim_holds()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedScheduleAsync(RecalcTypeKey, batchSize: 1, gapSeconds: 1);
        await SeedRipplesAsync(wave.Id, 6);

        await using var probe = await Db.OpenConnectionAsync();
        var targetId = await probe.ExecuteScalarAsync<Guid>(
            """
            select id from ripple.ripple where wave_id = @id and state = 'Pending'
            order by schedule_order limit 1
            """, new { id = wave.Id });
        var slotBefore = await ScalarAsync(
            "select round(schedule_order)::bigint from ripple.ripple where id = @id", new { id = targetId });

        // Hold a row lock on it the way an in-flight poll would, on an open transaction.
        await using var holder = await Db.OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from ripple.ripple where id = @id for update",
            new { id = targetId }, tx);

        // The reschedule must step around it rather than block — that is the whole point of skip locked.
        var moved = await Engine.RescheduleWaveAsync(wave.Id, batchSize: 1, gapSeconds: 60);

        moved.ShouldBe(5);
        // The locked ripple kept its original slot: it was never re-stamped. (It is still the wave's HEAD, and
        // therefore still the anchor the other five were re-spread from — the min deliberately spans locked
        // rows too, so racing a poll can't drag the wave forward in the queue.)
        (await ScalarAsync("select round(schedule_order)::bigint from ripple.ripple where id = @id",
            new { id = targetId })).ShouldBe(slotBefore);
        await tx.RollbackAsync();
    }
}
