using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>Expands its group into member ripples, then FAILS its first attempt — after the children are already
/// committed. The retry runs the same code and expands again.</summary>
public sealed class ExpandThenFailHandler(ICollectionWaveGenerator generator, ConcurrentDictionary<Guid, int> attempts)
    : IRippleHandler<RecalcContext, CompanyGroupTax>
{
    public async Task<SplashReport?> Execute(RecalcContext wave, CompanyGroupTax ripple, IRippleContext context)
    {
        await generator.Continue(context)
            .AddRipples(ripple.MemberCompanyIds.Select(id => new CompanyTax { CompanyId = id }))
            .DispatchAsync(context.CancellationToken);

        if (attempts.AddOrUpdate(ripple.GroupId, 1, (_, n) => n + 1) == 1)
        {
            throw new InvalidOperationException("failed after expanding");
        }

        return null;
    }
}

/// <summary>
/// A <c>Continue</c> expansion must happen once per parent ripple, however many attempts the parent takes. Its
/// children commit in their own transaction, ahead of the parent's outcome, so an attempt that expands and then
/// fails (or times out, or dies) used to leave them behind — and the retry added a second set.
/// </summary>
public sealed class ExpansionIdempotencyTests : RippleTestBase
{
    [Fact]
    public async Task a_parent_that_fails_after_expanding_does_not_expand_again_on_retry()
    {
        await ResetAsync();
        var members = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var wave = await CollectionGenerator
            .Create("group recalc", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipple(new CompanyGroupTax { GroupId = Guid.NewGuid(), MemberCompanyIds = members })
            .DispatchAsync();

        var sink = new HierarchySink();
        using var host = BuildEngineHost(
            engine => engine
                .AddHandler<RecalcContext, CompanyGroupTax, ExpandThenFailHandler>()
                .AddHandler<RecalcContext, CompanyTax, CompanyTaxHandler>(),
            o => o.RetryBackoff = TimeSpan.FromMilliseconds(50),
            services => services.AddSingleton(sink).AddSingleton(new ConcurrentDictionary<Guid, int>()));
        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            Wave final;
            while ((final = (await Engine.GetWaveAsync(wave.Id))!).Status == WaveStatus.Active)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the wave never settled");
                await Task.Delay(100);
            }

            final.Status.ShouldBe(WaveStatus.Completed);
            final.RippleCount.ShouldBe(5, "1 group + its 4 members — not a second set from the retry");
            sink.Companies.Count.ShouldBe(4, "each member runs once");
            (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Failed'")).ShouldBe(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---- storage level: one StubRippleContext per attempt, as the engine builds one context per attempt ------

    /// <summary>The retry repeats the expansion the first attempt already made (skipped) and goes on to one the
    /// first attempt never reached (made): each expansion happens once, not each attempt's worth.</summary>
    [Fact]
    public async Task a_retry_skips_the_expansions_already_made_and_makes_the_ones_that_were_not()
    {
        var (wave, root) = await RunningRootAsync();

        var first = new StubRippleContext(wave.Id, root.Id, attempt: 1);
        await CollectionGenerator.Continue(first).AddRipples(Members(3)).DispatchAsync();

        var second = new StubRippleContext(wave.Id, root.Id, await RetryAsync(wave.Id, root));
        var repeated = await CollectionGenerator.Continue(second).AddRipples(Members(3)).DispatchAsync();
        repeated.RippleCount.ShouldBe(4, "expansion #1 was made by attempt 1 — attempt 2 adds nothing");
        var reached = await CollectionGenerator.Continue(second).AddRipples(Members(2)).DispatchAsync();
        reached.RippleCount.ShouldBe(6, "expansion #2 is new, so it is made");

        (await ChildrenAsync(root.Id)).ShouldBe(5);
        (await ScalarAsync("select count(*) from ripple.expansion where parent_ripple_id = @id", new { id = root.Id }))
            .ShouldBe(2);
        (await ScalarAsync("select ripple_count from ripple.expansion where expansion_key = '#2'")).ShouldBe(2);
    }

    /// <summary>A handler that does not expand in the same order every attempt names its expansions; the key, not
    /// the position, is then what identifies one.</summary>
    [Fact]
    public async Task named_expansions_are_recognised_whatever_order_a_retry_makes_them_in()
    {
        var (wave, root) = await RunningRootAsync();

        var first = new StubRippleContext(wave.Id, root.Id, attempt: 1);
        await CollectionGenerator.Continue(first, "subsidiaries").AddRipples(Members(2)).DispatchAsync();

        var second = new StubRippleContext(wave.Id, root.Id, await RetryAsync(wave.Id, root));
        await CollectionGenerator.Continue(second, "branches").AddRipples(Members(1)).DispatchAsync();
        await CollectionGenerator.Continue(second, "subsidiaries").AddRipples(Members(2)).DispatchAsync();

        (await ChildrenAsync(root.Id)).ShouldBe(3);
        Should.Throw<ArgumentException>(() => CollectionGenerator.Continue(second, "#1"))
            .Message.ShouldContain("reserved");
    }

    /// <summary>An attempt that has lost its claim — superseded by a retry, or its ripple already settled — may no
    /// longer expand the wave: by then the wave may have completed without it. Refused, with nothing inserted.</summary>
    [Fact]
    public async Task an_attempt_that_is_no_longer_running_cannot_expand_the_wave()
    {
        var (wave, root) = await RunningRootAsync();
        var stale = new StubRippleContext(wave.Id, root.Id, attempt: 1);
        var attempt = await RetryAsync(wave.Id, root); // the ripple is now Running on attempt 2

        (await Should.ThrowAsync<InvalidOperationException>(
                () => CollectionGenerator.Continue(stale).AddRipples(Members(3)).DispatchAsync()))
            .Message.ShouldContain("no longer running attempt 1");

        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(root.Id, wave.Id, attempt, DateTimeOffset.UtcNow, null)], "inst-1");
        var settled = new StubRippleContext(wave.Id, root.Id, attempt);
        await Should.ThrowAsync<InvalidOperationException>(
            () => CollectionGenerator.Continue(settled).AddRipples(Members(3)).DispatchAsync());

        (await ChildrenAsync(root.Id)).ShouldBe(0);
        (await Engine.GetWaveAsync(wave.Id))!.RippleCount.ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.expansion")).ShouldBe(0, "a refused expansion leaves no ledger row");
    }

    /// <summary>The query-source fan-out (<c>INSERT … SELECT</c>, shared by Marten and EF) goes through the same
    /// ledger: a retry's repeat of the expansion runs no insert at all.</summary>
    [Fact]
    public async Task a_query_source_expansion_is_also_made_once()
    {
        var options = new DbContextOptionsBuilder<LedgerEfContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new LedgerEfContext(options);
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        db.Companies.AddRange(Enumerable.Range(0, 4).Select(_ => new LedgerCompany { Id = Guid.NewGuid(), TaxCode = "GRP" }));
        await db.SaveChangesAsync();

        var (wave, root) = await RunningRootAsync();
        Task<Wave> ExpandAsync(IRippleContext context) => EfGenerator
            .Continue(db, context)
            .AddRipples<LedgerCompany, CompanyTax>(c => c.TaxCode == "GRP", c => new CompanyTax { CompanyId = c.Id })
            .DispatchAsync();

        (await ExpandAsync(new StubRippleContext(wave.Id, root.Id, attempt: 1))).RippleCount.ShouldBe(5);
        var retried = await ExpandAsync(new StubRippleContext(wave.Id, root.Id, await RetryAsync(wave.Id, root)));

        retried.RippleCount.ShouldBe(5);
        (await ChildrenAsync(root.Id)).ShouldBe(4);
    }

    /// <summary>The ledger only protects live ripples, so it goes when its wave is compacted.</summary>
    [Fact]
    public async Task compaction_removes_the_waves_expansion_ledger()
    {
        var (wave, root) = await RunningRootAsync();
        await CollectionGenerator.Continue(new StubRippleContext(wave.Id, root.Id)).AddRipples(Members(2)).DispatchAsync();

        var all = new List<ClaimedRipple> { root };
        all.AddRange(await Engine.PollAsync(10, "inst-1", 0));
        await Splashes.CompleteRipplesAsync(
            all.Select(r => new RippleCompletion(r.Id, wave.Id, r.Attempt, DateTimeOffset.UtcNow, null)).ToList(), "inst-1");
        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Completed);
        (await ScalarAsync("select count(*) from ripple.expansion")).ShouldBe(1);

        await CompactWaveAsync(wave.Id);

        (await ScalarAsync("select count(*) from ripple.expansion")).ShouldBe(0);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private sealed class LedgerCompany
    {
        public Guid Id { get; set; }
        public string TaxCode { get; set; } = "";
    }

    private sealed class LedgerEfContext(DbContextOptions<LedgerEfContext> options) : DbContext(options)
    {
        public DbSet<LedgerCompany> Companies => Set<LedgerCompany>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<LedgerCompany>().ToTable("ledger_company");
    }

    private static readonly string RootType = RippleTypeKey.Compose(nameof(RecalcContext), nameof(RecalcCompany));

    /// <summary>A wave with one root ripple, claimed — so it is Running on attempt 1, as a handler would see it.</summary>
    private async Task<(Wave Wave, ClaimedRipple Root)> RunningRootAsync()
    {
        await ResetAsync();
        var wave = await CollectionGenerator
            .Create("group recalc", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipple(new RecalcCompany { CompanyId = Guid.NewGuid() })
            .DispatchAsync();
        return (wave, (await Engine.PollAsync(1, "inst-1", 0)).Single());
    }

    /// <summary>Fails the root's current attempt (requeued, eligible at once) and claims it again; returns the new
    /// attempt number. The claim is restricted to the root's type so it never picks up the children.</summary>
    private async Task<int> RetryAsync(Guid waveId, ClaimedRipple root)
    {
        var current = (int)await ScalarAsync("select attempt from ripple.ripple where id = @id", new { id = root.Id });
        await Splashes.FailRipplesAsync(
            [new RippleFailure(root.Id, waveId, current, DateTimeOffset.UtcNow, null, Terminal: false,
                NextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-1))], "inst-1");
        var reclaimed = (await Engine.PollAsync(1, "inst-1", 0, [RootType])).Single();
        reclaimed.Id.ShouldBe(root.Id);
        return reclaimed.Attempt;
    }

    private static IEnumerable<CompanyTax> Members(int count)
        => Enumerable.Range(0, count).Select(_ => new CompanyTax { CompanyId = Guid.NewGuid() });

    private Task<long> ChildrenAsync(Guid parentRippleId)
        => ScalarAsync("select count(*) from ripple.ripple where parent_ripple_id = @id", new { id = parentRippleId });
}
