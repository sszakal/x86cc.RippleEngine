using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Hosting;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The persisted type name (<see cref="RippleTypeName"/> / <see cref="RippleTypeAttribute"/>) and the registry's
/// refusal of two CLR types that resolve to one <c>type_key</c>. Registration-level only — no Postgres.
/// </summary>
public sealed class RippleTypeNameTests
{
    private const string Connection = "Host=localhost;Database=ripple_type_name_tests;Username=u;Password=p";

    [Fact]
    public void an_unattributed_type_keeps_its_clr_name()
        => RippleTypeName.Of<RecalcCompany>().ShouldBe(nameof(RecalcCompany));

    [Fact]
    public void the_attribute_pins_the_persisted_name()
        => RippleTypeName.Of<RenamedRecalcCompany>().ShouldBe(nameof(RecalcCompany));

    [Theory]
    [InlineData(typeof(BlankName))]
    [InlineData(typeof(SeparatorName))]
    public void an_unusable_attribute_name_is_rejected(Type type)
        => Should.Throw<InvalidOperationException>(() => RippleTypeName.Of(type)).Message.ShouldContain(type.FullName!);

    /// <summary>Two ripple types with the same CLR name in different namespaces used to share one type_key, and
    /// last-wins silently sent both types' ripples to the second handler.</summary>
    [Fact]
    public void two_types_resolving_to_one_type_key_fail_at_registration()
    {
        var engine = Host.CreateEmptyApplicationBuilder(null).AddRippleEngine(o => o.ConnectionString = Connection);
        engine.AddHandler<RecalcContext, Billing.Invoice, Billing.InvoiceHandler>();

        var ex = Should.Throw<InvalidOperationException>(
            () => engine.AddHandler<RecalcContext, Tax.Invoice, Tax.InvoiceHandler>());

        ex.Message.ShouldContain("RecalcContext|Invoice");
        ex.Message.ShouldContain(typeof(Billing.Invoice).FullName!);
        ex.Message.ShouldContain(typeof(Tax.Invoice).FullName!);
    }

    [Fact]
    public void re_registering_the_same_pair_is_still_last_wins()
    {
        var engine = Host.CreateEmptyApplicationBuilder(null).AddRippleEngine(o => o.ConnectionString = Connection);
        engine.AddHandler<RecalcContext, Billing.Invoice, Billing.InvoiceHandler>();

        Should.NotThrow(() => engine.AddHandler<RecalcContext, Billing.Invoice, Billing.InvoiceHandler>(batchSize: 10));
    }

    [RippleType(" ")]
    private sealed class BlankName;

    [RippleType("a|b")]
    private sealed class SeparatorName;
}

/// <summary>
/// <see cref="RecalcCompany"/> after a rename, pinned to its old persisted name so ripples fanned out before the
/// rename still find a handler.
/// </summary>
[RippleType(nameof(RecalcCompany))]
public sealed class RenamedRecalcCompany : IRippleTarget
{
    public Guid CompanyId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}

public sealed class RenamedRecalcHandler(ExecutionSink sink) : IRippleHandler<RecalcContext, RenamedRecalcCompany>
{
    public Task<SplashReport?> Execute(RecalcContext wave, RenamedRecalcCompany ripple, IRippleContext context)
    {
        sink.Executed.Add(ripple.CompanyId);
        return Task.FromResult<SplashReport?>(null);
    }
}

// Two ripple types sharing a CLR name (Type.Name of a nested type is just "Invoice") — the collision the
// registry must refuse.
public static class Billing
{
    public sealed class Invoice : IRippleTarget
    {
        public IReadOnlyList<string> TargetIds => [];
    }

    public sealed class InvoiceHandler : IRippleHandler<RecalcContext, Invoice>
    {
        public Task<SplashReport?> Execute(RecalcContext wave, Invoice ripple, IRippleContext context)
            => Task.FromResult<SplashReport?>(null);
    }
}

public static class Tax
{
    public sealed class Invoice : IRippleTarget
    {
        public IReadOnlyList<string> TargetIds => [];
    }

    public sealed class InvoiceHandler : IRippleHandler<RecalcContext, Invoice>
    {
        public Task<SplashReport?> Execute(RecalcContext wave, Invoice ripple, IRippleContext context)
            => Task.FromResult<SplashReport?>(null);
    }
}
