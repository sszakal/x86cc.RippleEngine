using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The EF Core fan-out provider (<see cref="x86cc.RippleEngine.EntityFrameworkCore.IEfWaveGenerator"/>): a
/// server-side <c>INSERT … SELECT</c> from EF entities into <c>ripple.ripple</c>, source rows never loaded —
/// the same result as Marten, driven from a <see cref="DbContext"/> instead. Uses a tiny EF context over the
/// same Postgres (its table lives in <c>public</c>, separate from <c>ripple.*</c>).
/// </summary>
public sealed class EfSourceTests : RippleTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class EfCompany
    {
        public Guid Id { get; set; }
        public string TaxCode { get; set; } = "";
    }

    private sealed class SampleEfContext(DbContextOptions<SampleEfContext> options) : DbContext(options)
    {
        public DbSet<EfCompany> Companies => Set<EfCompany>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<EfCompany>().ToTable("ef_company");
    }

    [Fact]
    public async Task fans_out_a_ripple_per_impacted_entity_via_ef()
    {
        await ResetAsync();

        var options = new DbContextOptionsBuilder<SampleEfContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new SampleEfContext(options);
        // The ripple DB already exists (migrated), so EnsureCreated is a no-op — create just the model's table
        // in the existing database.
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

        var impacted = Enumerable.Range(0, 5)
            .Select(_ => new EfCompany { Id = Guid.NewGuid(), TaxCode = "VAT" }).ToList();
        db.Companies.AddRange(impacted);
        db.Companies.AddRange(Enumerable.Range(0, 3)
            .Select(_ => new EfCompany { Id = Guid.NewGuid(), TaxCode = "US" })); // not impacted
        await db.SaveChangesAsync();

        var wave = await EfGenerator
            .Create(db, "VAT rise", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples<EfCompany, RecalcCompany>(
                c => c.TaxCode == "VAT",
                c => new RecalcCompany { CompanyId = c.Id })
            .DispatchAsync();

        wave.RippleCount.ShouldBe(5);

        // Stamped with the composite (wave|ripple) type_key, exactly like the Marten fan-out.
        (await ScalarAsync("select count(*) from ripple.ripple where type_key = 'RecalcContext|RecalcCompany'")).ShouldBe(5);
        (await ScalarAsync("select count(*) from ripple.ripple where payload_type = 'RecalcCompany'")).ShouldBe(5);

        // Claim them back: only the impacted (VAT) companies, both payloads rehydrate, wave payload rides along.
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(5);
        claimed.ShouldAllBe(r => r.WavePayloadType == nameof(RecalcContext) && r.WavePayload != null);

        var got = claimed
            .Select(r => r.Payload.RootElement.Deserialize<RecalcCompany>(Web)!.CompanyId)
            .ToHashSet();
        got.SetEquals(impacted.Select(c => c.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task continue_expands_an_existing_wave_from_an_ef_query()
    {
        await ResetAsync();

        var options = new DbContextOptionsBuilder<SampleEfContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new SampleEfContext(options);
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

        var members = Enumerable.Range(0, 4)
            .Select(_ => new EfCompany { Id = Guid.NewGuid(), TaxCode = "GRP" }).ToList();
        db.Companies.AddRange(members);
        db.Companies.AddRange(Enumerable.Range(0, 2)
            .Select(_ => new EfCompany { Id = Guid.NewGuid(), TaxCode = "OTHER" })); // must NOT be touched
        await db.SaveChangesAsync();

        // A wave with one "group" ripple, as a handler would see it.
        var wave = await CollectionGenerator
            .Create("group recalc", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipple(new RecalcCompany { CompanyId = Guid.NewGuid() })
            .DispatchAsync();
        var parentRippleId = (await QueryGuidsAsync(
            "select id from ripple.ripple where wave_id = @id", new { id = wave.Id })).Single();

        // Expand it server-side from an EF query via the same Continue(context) verb Marten uses.
        var context = new StubRippleContext(wave.Id, parentRippleId);
        var continued = await EfGenerator
            .Continue(db, context)
            .AddRipples<EfCompany, RecalcCompany>(
                c => c.TaxCode == "GRP",
                c => new RecalcCompany { CompanyId = c.Id })
            .DispatchAsync();

        continued.Id.ShouldBe(wave.Id);
        continued.RippleCount.ShouldBe(5); // 1 initial ripple + 4 members expanded (the 2 OTHER rows skipped).
        (await ScalarAsync(
            "select count(*) from ripple.ripple where parent_ripple_id = @p", new { p = parentRippleId })).ShouldBe(4);
    }

    [Fact]
    public async Task ef_create_matching_zero_rows_is_born_completed()
    {
        await ResetAsync();

        var options = new DbContextOptionsBuilder<SampleEfContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new SampleEfContext(options);
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        // Rows exist, but none match the predicate ⇒ the fan-out INSERT…SELECT produces zero ripples.
        db.Companies.AddRange(Enumerable.Range(0, 3)
            .Select(_ => new EfCompany { Id = Guid.NewGuid(), TaxCode = "US" }));
        await db.SaveChangesAsync();

        var wave = await EfGenerator
            .Create(db, "no targets", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples<EfCompany, RecalcCompany>(
                c => c.TaxCode == "NONE",
                c => new RecalcCompany { CompanyId = c.Id })
            .DispatchAsync();

        // Born-complete rather than a zero-ripple Active zombie.
        wave.Status.ShouldBe(WaveStatus.Completed);
        wave.RippleCount.ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @id", new { id = wave.Id })).ShouldBe(0);
        (await ScalarAsync("select count(*) from ripple.wave where id = @id and completed_at is not null",
            new { id = wave.Id })).ShouldBe(1);
    }

    private async Task<IReadOnlyList<Guid>> QueryGuidsAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        return (await conn.QueryAsync<Guid>(sql, p)).AsList();
    }
}
