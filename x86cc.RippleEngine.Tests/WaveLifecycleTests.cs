using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Wave lifecycle integrity: a wave is created atomically with its ripples; a create with zero targets is
/// born <see cref="WaveStatus.Completed"/> (never a zero-ripple Active zombie); and a wave being expanded
/// in-flight (via <c>Continue</c>) is never observed complete until the expanded children also drain.
/// </summary>
public sealed class WaveLifecycleTests : RippleTestBase
{
    [Fact]
    public async Task collection_create_with_zero_ripples_is_born_completed()
    {
        await ResetAsync();

        var wave = await CollectionGenerator
            .Create("nothing to do", new RecalcContext { LegislationCode = "VAT26" })
            .DispatchAsync(); // no AddRipples ⇒ empty seed set

        // Born-complete: not a stuck Active zombie.
        wave.Status.ShouldBe(WaveStatus.Completed);
        wave.RippleCount.ShouldBe(0);

        var reloaded = await Engine.GetWaveAsync(wave.Id);
        reloaded!.Status.ShouldBe(WaveStatus.Completed);
        reloaded.CompletedAt.ShouldNotBeNull();
        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @id", new { id = wave.Id })).ShouldBe(0);
    }

    [Fact]
    public async Task collection_create_with_ripples_is_active_and_atomic()
    {
        await ResetAsync();

        var wave = await CollectionGenerator
            .Create("real job", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples(Enumerable.Range(0, 5).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }))
            .DispatchAsync();

        // One wave row + exactly its ripples, together.
        wave.Status.ShouldBe(WaveStatus.Active);
        wave.RippleCount.ShouldBe(5);
        (await ScalarAsync("select count(*) from ripple.wave where id = @id and status = 'Active' and ripple_count = 5",
            new { id = wave.Id })).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @id", new { id = wave.Id })).ShouldBe(5);
        (await ScalarAsync("select count(*) from ripple.wave where id = @id and completed_at is null",
            new { id = wave.Id })).ShouldBe(1);
    }

    [Fact]
    public async Task a_born_complete_wave_compacts_cleanly()
    {
        await ResetAsync();

        var wave = await CollectionGenerator.Create("empty").DispatchAsync();
        wave.Status.ShouldBe(WaveStatus.Completed);

        // Compaction must handle a wave with no ripples/splashes: stamp compacted_at, write no chunks, no throw.
        await CompactWaveAsync(wave.Id);

        (await ScalarAsync("select count(*) from ripple.wave where id = @id and compacted_at is not null",
            new { id = wave.Id })).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.report_chunk where wave_id = @id", new { id = wave.Id })).ShouldBe(0);
    }

    [Fact]
    public async Task a_wave_expanded_in_flight_is_not_completed_until_its_children_drain()
    {
        await ResetAsync();

        // A wave with one "group" root ripple.
        var wave = await CollectionGenerator
            .Create("group recalc", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipple(new RecalcCompany { CompanyId = Guid.NewGuid() })
            .DispatchAsync();

        // Claim the root ⇒ Running. While Running the wave is not drained (running = 1).
        var root = await Engine.PollAsync(10, "inst-1", 0);
        root.Count.ShouldBe(1);
        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Active);

        // Expand it via Continue(context), parented to the still-Running root — exactly the in-flight path.
        await CollectionGenerator
            .Continue(new StubRippleContext(wave.Id, root[0].Id))
            .AddRipples(Enumerable.Range(0, 3).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }))
            .DispatchAsync();

        // Settle the root. The wave STILL isn't complete — the 3 children are pending.
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(root[0].Id, wave.Id, root[0].Attempt, Ago(1), null)], "inst-1");
        await RefreshWaveStatsAsync();
        var midway = await Engine.GetWaveAsync(wave.Id);
        midway!.Status.ShouldBe(WaveStatus.Active);
        midway.RippleCount.ShouldBe(4);

        // Drain the children ⇒ now it completes.
        var children = await Engine.PollAsync(10, "inst-1", 0);
        children.Count.ShouldBe(3);
        await Splashes.CompleteRipplesAsync(
            children.Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, Ago(1), null)).ToList(), "inst-1");
        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Completed);
    }

    private static DateTimeOffset Ago(int seconds) => DateTimeOffset.UtcNow.AddSeconds(-seconds);
}
