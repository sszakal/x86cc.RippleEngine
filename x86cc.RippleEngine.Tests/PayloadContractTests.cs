using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;
using Weasel.Core;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The fan-out → worker payload contract (<see cref="RippleJson"/>), checked across all three providers: a payload
/// with an enum, a decimal and a null field is written by each generator, claimed back, and deserialised with the
/// options the handler registry uses. Each provider produces a DIFFERENT raw shape — camelCase vs member-name keys,
/// enum names vs integers — which is exactly what the shared options must absorb, so the raw shape is asserted
/// too: if a provider's output changes, this says which leniency it now depends on.
/// </summary>
public sealed class PayloadContractTests : RippleTestBase
{
    public enum Grade { Low, High }

    public sealed class ContractWave
    {
        public Grade Grade { get; set; }
    }

    public sealed class ContractRipple : IRippleTarget
    {
        public Guid Id { get; set; }
        public Grade Grade { get; set; }
        public decimal Amount { get; set; }
        public string? Note { get; set; }

        [JsonIgnore]
        public IReadOnlyList<string> TargetIds => [Id.ToString()];
    }

    /// <summary>The source rows the query providers fan out over (a Marten document / an EF entity).</summary>
    public sealed class ContractSource
    {
        public Guid Id { get; set; }
        public Grade Grade { get; set; }
        public decimal Amount { get; set; }
        public string? Note { get; set; }
    }

    private sealed class ContractEfContext(DbContextOptions<ContractEfContext> options) : DbContext(options)
    {
        public DbSet<ContractSource> Sources => Set<ContractSource>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ContractSource>().ToTable("contract_source");
    }

    private static readonly ContractSource[] Sources =
    [
        new() { Id = Guid.NewGuid(), Grade = Grade.High, Amount = 1234.5678m, Note = "flagged" },
        new() { Id = Guid.NewGuid(), Grade = Grade.Low, Amount = 0.01m, Note = null }
    ];

    private static ContractRipple ToRipple(ContractSource s)
        => new() { Id = s.Id, Grade = s.Grade, Amount = s.Amount, Note = s.Note };

    [Fact]
    public void the_shared_options_cannot_be_changed_by_a_consumer()
        => Should.Throw<InvalidOperationException>(() => RippleJson.Options.PropertyNameCaseInsensitive = false);

    [Fact]
    public async Task collection_payloads_round_trip()
    {
        await ResetAsync();

        await CollectionGenerator
            .Create("contract", new ContractWave { Grade = Grade.High })
            .AddRipples(Sources.Select(ToRipple))
            .DispatchAsync();

        // Written by the serializer itself: camelCase keys, enum names.
        await AssertRawShapeAsync(gradeKey: "grade", gradeJsonType: "string");
        await AssertRoundTripAsync();
    }

    [Fact]
    public async Task marten_payloads_round_trip()
    {
        await ResetAsync();
        await using var store = DocumentStore.For(opts =>
        {
            opts.Connection(ConnectionString);
            opts.DatabaseSchemaName = "contract";
            // Required for enum-valued fan-out payloads (see AGENTS.md): the INSERT…SELECT copies the stored value.
            opts.UseSystemTextJsonForSerialization(EnumStorage.AsString);
        });
        await using (var write = store.LightweightSession())
        {
            write.Store(Sources);
            await write.SaveChangesAsync();
        }

        await using var session = store.QuerySession();
        await MartenGenerator
            .Create(session, "contract", new ContractWave { Grade = Grade.High })
            .AddRipples<ContractSource, ContractRipple>(
                _ => true,
                s => new ContractRipple { Id = s.Id, Grade = s.Grade, Amount = s.Amount, Note = s.Note })
            .DispatchAsync();

        // Built by jsonb_build_object: member-name keys (case-insensitive binding), enum names.
        await AssertRawShapeAsync(gradeKey: "Grade", gradeJsonType: "string");
        await AssertRoundTripAsync();
    }

    [Fact]
    public async Task ef_payloads_round_trip()
    {
        await ResetAsync();
        var options = new DbContextOptionsBuilder<ContractEfContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new ContractEfContext(options);
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        db.Sources.AddRange(Sources.Select(s => new ContractSource
            { Id = s.Id, Grade = s.Grade, Amount = s.Amount, Note = s.Note }));
        await db.SaveChangesAsync();

        await EfGenerator
            .Create(db, "contract", new ContractWave { Grade = Grade.High })
            .AddRipples<ContractSource, ContractRipple>(
                _ => true,
                s => new ContractRipple { Id = s.Id, Grade = s.Grade, Amount = s.Amount, Note = s.Note })
            .DispatchAsync();

        // Built by to_jsonb over EF's columns: member-name keys, and enums as INTEGERS (EF's default mapping).
        await AssertRawShapeAsync(gradeKey: "Grade", gradeJsonType: "number");
        await AssertRoundTripAsync();
    }

    private async Task AssertRawShapeAsync(string gradeKey, string gradeJsonType)
        => (await ScalarAsync(
                "select count(*) from ripple.ripple where jsonb_typeof(payload -> @gradeKey) = @gradeJsonType",
                new { gradeKey, gradeJsonType }))
            .ShouldBe(Sources.Length, $"expected every payload to carry '{gradeKey}' as a JSON {gradeJsonType}");

    /// <summary>Claims the ripples and deserialises both payloads exactly as <c>RippleHandlerRegistry</c> does.</summary>
    private async Task AssertRoundTripAsync()
    {
        var claimed = await Engine.PollAsync(100, "inst-1", 0);
        claimed.Count.ShouldBe(Sources.Length);

        foreach (var r in claimed)
        {
            r.TypeKey.ShouldBe(RippleTypeKey.Compose(nameof(ContractWave), nameof(ContractRipple)));
            r.WavePayload!.Deserialize<ContractWave>(RippleJson.Options)!.Grade.ShouldBe(Grade.High);
        }

        var actual = claimed
            .Select(r => r.Payload.Deserialize<ContractRipple>(RippleJson.Options)!)
            .OrderBy(p => p.Id)
            .Select(p => (p.Id, p.Grade, p.Amount, p.Note));
        var expected = Sources.OrderBy(s => s.Id).Select(s => (s.Id, s.Grade, s.Amount, s.Note));
        actual.ShouldBe(expected);
    }
}
