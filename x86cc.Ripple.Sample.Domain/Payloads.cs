using System.Text.Json.Serialization;
using x86cc.RippleEngine.Core;

namespace x86cc.Ripple.Sample.Domain;

// The wave (shared "event") + ripple (per-target) payloads. Their CLR names form the engine's composite
// type_key — e.g. "SeedRun|SeedBatch", "CorporateTaxChange|RecalcCorporateTax" — which drive both scheduling
// config and handler resolution, so the names here must match the AddHandler<TWave,TRipple,...> registrations.
// Corporate and employee tax are two competing categories (distinct type_keys) so their fair-share is visible.

/// <summary>Shared payload of a seed wave: the overall shape of the data being generated.</summary>
public sealed class SeedRun
{
    public long Total { get; set; }
    public int BatchSize { get; set; }

    /// <summary>Approximate target size of each Company document in KB (0 ⇒ minimal ~0.2 KB company).</summary>
    public int SizeKb { get; set; }
}

/// <summary>One seed ripple: generate + bulk-insert the companies for a contiguous index range.</summary>
public sealed class SeedBatch : IRippleTarget
{
    public long StartIndex { get; set; }
    public int Count { get; set; }

    /// <summary>The batch is one logical target — its index range.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [$"{StartIndex}-{StartIndex + Count}"];
}

/// <summary>Shared payload of a corporate-tax wave: which tax code changed and the new rate on revenue.</summary>
public sealed class CorporateTaxChange
{
    public string TaxCode { get; set; } = "";
    public decimal Rate { get; set; }
}

/// <summary>One corporate-tax ripple: recompute corporate tax for a single impacted company.</summary>
public sealed class RecalcCorporateTax : IRippleTarget
{
    public Guid CompanyId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}

/// <summary>Shared payload of an employee-tax (payroll) wave: which tax code changed and the per-employee levy.</summary>
public sealed class EmployeeTaxChange
{
    public string TaxCode { get; set; } = "";
    public decimal RatePerEmployee { get; set; }
}

/// <summary>One employee-tax ripple: recompute payroll tax for a single impacted company.</summary>
public sealed class RecalcEmployeeTax : IRippleTarget
{
    public Guid CompanyId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}
