using System.Text.Json;
using Dapper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.MartenDb;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>A Marten source aggregate: a company that belongs to a group (the rows a group ripple expands over).</summary>
public sealed class GroupCompany
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
}

/// <summary>
/// A group ripple that carries only its group id — unlike <see cref="CompanyGroupTax"/> it does NOT list its
/// members; they are discovered server-side by the handler's query.
/// </summary>
public sealed class CompanyGroupRef : IRippleTarget
{
    public Guid GroupId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> TargetIds => [GroupId.ToString()];
}

/// <summary>
/// Expands a group ripple with a <b>server-side</b> <c>INSERT … SELECT</c>: it hands the group's member query
/// to <see cref="IMartenWaveGenerator.Continue"/>, which fans out one <see cref="CompanyTax"/> ripple per
/// member into the <b>same wave</b>, parented to this ripple — the member rows never load into the handler.
/// Contrast <see cref="CompanyGroupTaxHandler"/>, which carries its members in the payload and expands them
/// in-memory via the collection generator's <c>Continue</c>.
/// </summary>
public sealed class MartenGroupExpandHandler(
    IDocumentStore store, IMartenWaveGenerator generator, HierarchySink sink)
    : IRippleHandler<RecalcContext, CompanyGroupRef>
{
    public async Task<SplashReport?> Execute(RecalcContext wave, CompanyGroupRef ripple, IRippleContext context)
    {
        await using var session = store.QuerySession();
        await generator
            .Continue(session, context)
            .AddRipples<GroupCompany, CompanyTax>(
                c => c.GroupId == ripple.GroupId,
                c => new CompanyTax { CompanyId = c.Id })
            .DispatchAsync(context.CancellationToken);

        sink.Groups.Add(ripple.GroupId);
        return null; // the group target is inferred succeeded once its members are enqueued
    }
}

/// <summary>
/// End-to-end: a running engine executes a group ripple whose handler expands the wave through the
/// queryable-source Marten generator (<c>Continue</c>) — proving in-flight expansion works with the same
/// server-side fan-out the initial wave uses, not just the in-memory <c>context.AddRipplesAsync</c> path.
/// </summary>
public sealed class ContinueExpansionTests : RippleTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task a_group_ripple_expands_over_a_marten_query_without_loading_its_members()
    {
        await ResetAsync();
        await using var store = BuildMartenStore();

        // Member companies live only in Marten (the "sample" schema). The handler discovers them server-side.
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var membersA = await SeedGroupCompaniesAsync(store, groupA, count: 5);
        var membersB = await SeedGroupCompaniesAsync(store, groupB, count: 3);
        await SeedGroupCompaniesAsync(store, Guid.NewGuid(), count: 4); // an unrelated group — must NOT be touched

        var wave = await CreateWaveAsync(legislation: "TAX2026");
        await AddTypedRipplesAsync(wave.Id, new[]
        {
            new CompanyGroupRef { GroupId = groupA },
            new CompanyGroupRef { GroupId = groupB }
        });

        var expectedCompanies = membersA.Concat(membersB).ToHashSet();

        var sink = new HierarchySink();
        using var host = BuildHost(store, sink);
        await host.StartAsync();
        try
        {
            var final = await WaitForTerminalAsync(wave.Id, TimeSpan.FromSeconds(60));

            final.Status.ShouldBe(WaveStatus.Completed);
            // 2 group ripples + (5 + 3) members expanded server-side = 10 ripples; the unrelated group's 4 are untouched.
            final.RippleCount.ShouldBe(10);
            final.Succeeded.ShouldBe(10);
            final.Pending.ShouldBe(0);
            final.Running.ShouldBe(0);

            sink.Groups.ToHashSet().SetEquals([groupA, groupB]).ShouldBeTrue();
            sink.Companies.ToHashSet().SetEquals(expectedCompanies).ShouldBeTrue();

            // The children were fanned out server-side with the composite type_key and parented to their group ripple.
            (await ScalarAsync("select count(*) from ripple.ripple where type_key = 'RecalcContext|CompanyTax'"))
                .ShouldBe(8);
            (await ScalarAsync(
                "select count(distinct parent_ripple_id) from ripple.ripple where parent_ripple_id is not null"))
                .ShouldBe(2);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private IHost BuildHost(IDocumentStore store, HierarchySink sink)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(sink);
        builder.Services.AddSingleton(store);
        builder.Services.AddRippleStorage(ConnectionString);
        builder.Services.AddRippleMartenGeneration();
        builder.Services
            .AddRippleEngine(o =>
            {
                o.MaxConcurrency = 8;
                o.MinPollDelay = TimeSpan.FromMilliseconds(10);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(200);
                o.WaveStatsRefreshInterval = TimeSpan.FromMilliseconds(150);
                o.CompactionInterval = TimeSpan.FromMinutes(10); // don't compact mid-test — this inspects ripples
            })
            .AddHandler<RecalcContext, CompanyGroupRef, MartenGroupExpandHandler>()
            .AddHandler<RecalcContext, CompanyTax, CompanyTaxHandler>();
        return builder.Build();
    }

    private Task AddTypedRipplesAsync<T>(Guid waveId, IEnumerable<T> items) where T : notnull
    {
        var seeds = items
            .Select(i => new RippleSeed(JsonSerializer.Serialize(i, Web), typeof(T).Name))
            .ToList();
        return Engine.AddRipplesAsync(waveId, seeds);
    }

    // Marten store over the same database, in its own schema so the document tables never collide with the
    // ripple schema (matches WaveGeneratorTests; these payloads have no enums so no enum-storage config needed).
    private DocumentStore BuildMartenStore()
        => DocumentStore.For(opts =>
        {
            opts.Connection(ConnectionString);
            opts.DatabaseSchemaName = "sample";
        });

    private static async Task<HashSet<Guid>> SeedGroupCompaniesAsync(IDocumentStore store, Guid groupId, int count)
    {
        await using var session = store.LightweightSession();
        var ids = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var company = new GroupCompany { Id = Guid.NewGuid(), GroupId = groupId };
            session.Store(company);
            ids.Add(company.Id);
        }

        await session.SaveChangesAsync();
        return ids;
    }

    private async Task<Wave> WaitForTerminalAsync(Guid waveId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var wave = await Engine.GetWaveAsync(waveId);
            if (wave is { Status: WaveStatus.Completed or WaveStatus.Faulted })
            {
                return wave;
            }

            await Task.Delay(100);
        }

        var last = await Engine.GetWaveAsync(waveId);
        throw new TimeoutException(
            $"Wave never reached a terminal state within {timeout}. Last: status={last?.Status}, " +
            $"pending={last?.Pending}, running={last?.Running}, succeeded={last?.Succeeded}, failed={last?.Failed}");
    }
}
