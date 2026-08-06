using System.Text.Json;
using Dapper;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The source-less <see cref="ICollectionWaveGenerator"/>: create a wave and its ripples from an in-memory
/// collection (no Marten/EF query), reusing the same <c>IEngineStore</c> insert + <c>schedule_order</c>
/// stamping as the query-source fan-out.
/// </summary>
public sealed class CollectionSourceTests : RippleTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task creates_a_wave_and_ripples_from_an_in_memory_collection()
    {
        await ResetAsync();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        var wave = await CollectionGenerator
            .Create("VAT rise", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples(ids.Select(id => new RecalcCompany { CompanyId = id }))
            .DispatchAsync();

        wave.Status.ShouldBe(WaveStatus.Active);
        wave.RippleCount.ShouldBe(5);

        // Stamped with the composite (wave|ripple) type_key, exactly like the query-source fan-out.
        (await ScalarAsync("select count(*) from ripple.ripple where type_key = 'RecalcContext|RecalcCompany'")).ShouldBe(5);
        (await ScalarAsync("select count(*) from ripple.ripple where payload_type = 'RecalcCompany'")).ShouldBe(5);

        // Claim them back through the engine: both payloads rehydrate and the shared wave payload rides along.
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(5);
        claimed.ShouldAllBe(r => r.WavePayloadType == nameof(RecalcContext) && r.WavePayload != null);

        var got = claimed
            .Select(r => r.Payload.RootElement.Deserialize<RecalcCompany>(Web)!.CompanyId)
            .ToHashSet();
        got.SetEquals(ids).ShouldBeTrue();
    }

    [Fact]
    public async Task accumulates_across_multiple_add_ripples_calls()
    {
        await ResetAsync();

        var wave = await CollectionGenerator
            .Create("batched adds")
            .AddRipples(Enumerable.Range(0, 5).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }))
            .AddRipples(Enumerable.Range(0, 3).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }))
            .DispatchAsync();

        wave.RippleCount.ShouldBe(8);
        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @id", new { id = wave.Id })).ShouldBe(8);
    }

    [Fact]
    public async Task fires_a_single_job_and_task()
    {
        await ResetAsync();
        var companyId = Guid.NewGuid();

        var wave = await CollectionGenerator.FireAsync(
            "one-off recalc",
            new RecalcContext { LegislationCode = "VAT26" },
            new RecalcCompany { CompanyId = companyId });

        wave.Status.ShouldBe(WaveStatus.Active);
        wave.RippleCount.ShouldBe(1);

        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        claimed.Count.ShouldBe(1);
        claimed[0].TypeKey.ShouldBe("RecalcContext|RecalcCompany");
        claimed[0].WavePayloadType.ShouldBe(nameof(RecalcContext));
        claimed[0].Payload.RootElement.Deserialize<RecalcCompany>(Web)!.CompanyId.ShouldBe(companyId);
    }

    [Fact]
    public async Task add_ripple_singular_adds_exactly_one()
    {
        await ResetAsync();

        var wave = await CollectionGenerator
            .Create("single via builder")
            .AddRipple(new RecalcCompany { CompanyId = Guid.NewGuid() })
            .DispatchAsync();

        wave.RippleCount.ShouldBe(1);
    }

    [Fact]
    public async Task continue_expands_an_existing_wave_parented_to_the_ripple()
    {
        await ResetAsync();

        // A wave with one "group" ripple, as a handler would see it.
        var wave = await CollectionGenerator
            .Create("group recalc", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipple(new RecalcCompany { CompanyId = Guid.NewGuid() })
            .DispatchAsync();
        var parentRippleId = (await QueryGuidsAsync(
            "select id from ripple.ripple where wave_id = @id", new { id = wave.Id })).Single();

        // Expand it in-memory via the same Continue(context) verb the queryable generators use.
        var context = new StubRippleContext(wave.Id, parentRippleId);
        var continued = await CollectionGenerator
            .Continue(context)
            .AddRipples(Enumerable.Range(0, 4).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }))
            .DispatchAsync();

        // Same wave, ripple_count bumped 1 -> 5; the 4 children are parented to the group ripple.
        continued.Id.ShouldBe(wave.Id);
        continued.RippleCount.ShouldBe(5);
        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @id", new { id = wave.Id })).ShouldBe(5);
        (await ScalarAsync(
            "select count(*) from ripple.ripple where parent_ripple_id = @p", new { p = parentRippleId })).ShouldBe(4);
    }

    private async Task<IReadOnlyList<Guid>> QueryGuidsAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        return (await conn.QueryAsync<Guid>(sql, p)).AsList();
    }
}
