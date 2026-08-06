using System.Text.Json;
using Marten;
using Shouldly;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Integration tests for the Marten-source fan-out generator: it must materialise ripples with a single
/// server-side <c>INSERT ... SELECT</c> (source rows never round-trip through the client) and land them in
/// the new <c>ripple.wave</c> / <c>ripple.ripple</c> schema so the engine can claim and rehydrate them.
/// </summary>
public sealed class WaveGeneratorTests : RippleTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A Marten source aggregate (the "business" rows a wave fans out over).</summary>
    public sealed class Company
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Country { get; set; } = "";
    }

    /// <summary>A batched ripple payload: many company ids collapsed into one unit of work.</summary>
    public sealed class RecalcBatch
    {
        public List<Guid> CompanyIds { get; set; } = [];
    }

    [Fact]
    public async Task fans_out_a_ripple_per_impacted_company_without_loading_them()
    {
        await ResetAsync();
        await using var store = BuildMartenStore();
        var impacted = await SeedCompaniesAsync(store, country: "IE", count: 5);
        await SeedCompaniesAsync(store, country: "US", count: 3); // not impacted

        await using var session = store.QuerySession();
        var wave = await MartenGenerator
            .Create(session, "VAT rise", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples<Company, RecalcCompany>(
                c => c.Country == "IE",
                c => new RecalcCompany { CompanyId = c.Id })
            .DispatchAsync();

        wave.Status.ShouldBe(WaveStatus.Active);
        wave.RippleCount.ShouldBe(5);
        wave.Pending.ShouldBe(5);

        (await ScalarAsync("select count(*) from ripple.ripple where payload_type = 'RecalcCompany'")).ShouldBe(5);
        // The INSERT SELECT stamps the composite (wave|ripple) type_key that drives scheduling + handler resolution.
        (await ScalarAsync("select count(*) from ripple.ripple where type_key = 'RecalcContext|RecalcCompany'")).ShouldBe(5);

        // Claim them back through the engine and confirm both payloads rehydrate correctly.
        var claimed = await Engine.PollAsync(100, "t", 0);
        claimed.Count.ShouldBe(5);
        claimed.ShouldAllBe(r => r.WavePayloadType == nameof(RecalcContext));

        var ids = claimed
            .Select(r => r.Payload.RootElement.Deserialize<RecalcCompany>(Web)!.CompanyId)
            .ToHashSet();
        ids.SetEquals(impacted).ShouldBeTrue();
    }

    [Fact]
    public async Task batched_fan_out_collapses_companies_into_one_ripple_per_bucket()
    {
        await ResetAsync();
        await using var store = BuildMartenStore();
        var impacted = await SeedCompaniesAsync(store, country: "DE", count: 7);

        await using var session = store.QuerySession();
        var wave = await MartenGenerator
            .Create(session, "migrate", new RecalcContext { LegislationCode = "MIG" })
            .AddRipplesBatched<Company, RecalcBatch, Guid>(
                c => c.Country == "DE",
                c => c.Id,
                b => b.CompanyIds,
                batchSize: 3)
            .DispatchAsync();

        // 7 companies / 3 per bucket => 3 ripples (3 + 3 + 1).
        wave.RippleCount.ShouldBe(3);
        (await ScalarAsync("select count(*) from ripple.ripple where payload_type = 'RecalcBatch'")).ShouldBe(3);
        (await ScalarAsync("select count(*) from ripple.ripple where type_key = 'RecalcContext|RecalcBatch'")).ShouldBe(3);

        var claimed = await Engine.PollAsync(100, "t", 0);
        claimed.Count.ShouldBe(3);

        var all = claimed
            .SelectMany(r => r.Payload.RootElement.Deserialize<RecalcBatch>(Web)!.CompanyIds)
            .ToHashSet();
        all.SetEquals(impacted).ShouldBeTrue();
    }

    // Marten store over the same database, in its own schema so the document tables never collide with the
    // ripple schema. (A store that serialises enums as strings — UseSystemTextJsonForSerialization with
    // EnumStorage.AsString — is required for enum-valued payloads; these test payloads have none.)
    private DocumentStore BuildMartenStore()
        => DocumentStore.For(opts =>
        {
            opts.Connection(ConnectionString);
            opts.DatabaseSchemaName = "sample";
        });

    private static async Task<HashSet<Guid>> SeedCompaniesAsync(IDocumentStore store, string country, int count)
    {
        await using var session = store.LightweightSession();
        var ids = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var company = new Company { Id = Guid.NewGuid(), Name = $"{country}-{i}", Country = country };
            session.Store(company);
            ids.Add(company.Id);
        }

        await session.SaveChangesAsync();
        return ids;
    }
}
