using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// A wave created under an idempotency key is created once. The create runs in the engine's own transaction, so
/// it can't commit with the business change that triggers it — a caller whose create timed out, or who crashed
/// in between, has to try again, and without a key a retry of a create that DID land fans the whole job out twice.
/// </summary>
public sealed class WaveIdempotencyTests : RippleTestBase
{
    [Fact]
    public async Task repeating_a_create_under_the_same_key_returns_the_first_wave_and_adds_nothing()
    {
        await ResetAsync();
        Task<Wave> CreateAsync(string? key)
        {
            var builder = CollectionGenerator
                .Create("VAT26 changed", new RecalcContext { LegislationCode = "VAT26" })
                .AddRipples(Enumerable.Range(0, 3).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }));
            return (key is null ? builder : builder.WithIdempotencyKey(key)).DispatchAsync();
        }

        var first = await CreateAsync("event-42");
        var repeat = await CreateAsync("event-42");

        repeat.Id.ShouldBe(first.Id);
        repeat.IdempotencyKey.ShouldBe("event-42");
        repeat.RippleCount.ShouldBe(3);
        (await ScalarAsync("select count(*) from ripple.wave")).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.ripple")).ShouldBe(3);

        // A different key, or none, is a different wave.
        (await CreateAsync("event-43")).Id.ShouldNotBe(first.Id);
        (await CreateAsync(null)).Id.ShouldNotBe(first.Id);
        (await CreateAsync(null)).Id.ShouldNotBe(first.Id);
        (await ScalarAsync("select count(*) from ripple.wave")).ShouldBe(4);
    }

    /// <summary>The race a retry storm produces: many creates of one key at once. Exactly one fans out; the rest
    /// wait for it and return its wave.</summary>
    [Fact]
    public async Task concurrent_creates_under_one_key_produce_one_wave()
    {
        await ResetAsync();

        var waves = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => CollectionGenerator
            .Create("VAT26 changed", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples(Enumerable.Range(0, 5).Select(_ => new RecalcCompany { CompanyId = Guid.NewGuid() }))
            .WithIdempotencyKey("event-42")
            .DispatchAsync())));

        waves.Select(w => w.Id).Distinct().Count().ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.wave")).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.ripple")).ShouldBe(5);
    }

    /// <summary>The query-source fan-out checks the key BEFORE running its <c>INSERT … SELECT</c>, so a repeat
    /// inserts no ripples at all — even though the source would now match more rows.</summary>
    [Fact]
    public async Task a_query_source_create_is_also_made_once()
    {
        await ResetAsync();
        var options = new DbContextOptionsBuilder<KeyedEfContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new KeyedEfContext(options);
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        db.Companies.AddRange(Enumerable.Range(0, 4).Select(_ => new KeyedCompany { Id = Guid.NewGuid() }));
        await db.SaveChangesAsync();

        Task<Wave> CreateAsync() => EfGenerator
            .Create(db, "VAT26 changed", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples<KeyedCompany, RecalcCompany>(c => true, c => new RecalcCompany { CompanyId = c.Id })
            .WithIdempotencyKey("event-42")
            .DispatchAsync();

        var first = await CreateAsync();
        first.RippleCount.ShouldBe(4);

        db.Companies.Add(new KeyedCompany { Id = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var repeat = await CreateAsync();

        repeat.Id.ShouldBe(first.Id);
        (await ScalarAsync("select count(*) from ripple.ripple")).ShouldBe(4);
    }

    [Fact]
    public async Task a_key_is_refused_on_an_expansion_and_must_not_be_blank()
    {
        await ResetAsync();
        var context = new StubRippleContext(Guid.NewGuid(), Guid.NewGuid());

        Should.Throw<InvalidOperationException>(() => CollectionGenerator.Continue(context).WithIdempotencyKey("k"));
        Should.Throw<ArgumentException>(() => CollectionGenerator.Create("w").WithIdempotencyKey(" "));
    }

    private sealed class KeyedCompany
    {
        public Guid Id { get; set; }
    }

    private sealed class KeyedEfContext(DbContextOptions<KeyedEfContext> options) : DbContext(options)
    {
        public DbSet<KeyedCompany> Companies => Set<KeyedCompany>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<KeyedCompany>().ToTable("keyed_company");
    }
}
