namespace x86cc.Ripple.Sample.Domain;

/// <summary>
/// The business aggregate the sample fans out over: a company that owes tax under some tax code. Stored as a
/// Marten document. <see cref="TaxDue"/> starts null and is filled in by a taxation-change wave.
/// </summary>
public sealed class Company
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Country { get; set; } = "";

    /// <summary>The tax code linking this company to a taxation regime — the fan-out predicate keys on this.</summary>
    public string TaxCode { get; set; } = "";

    public decimal Revenue { get; set; }

    /// <summary>Number of employees — the base for employee (payroll) tax.</summary>
    public int Headcount { get; set; }

    /// <summary>Computed by a corporate-tax recalc (<c>Revenue * rate</c>); null until first recalculated.</summary>
    public decimal? CorporateTaxDue { get; set; }

    /// <summary>Computed by an employee-tax recalc (<c>Headcount * ratePerEmployee</c>); null until first recalculated.</summary>
    public decimal? EmployeeTaxDue { get; set; }

    public DateTimeOffset? LastRecalculatedAt { get; set; }

    // Optional nested detail. Empty/null for a minimal company (sizeKb = 0); populated to grow the aggregate
    // to a target size — Filings (each with line items) is the part that scales.
    public CompanyRegistration? Registration { get; set; }

    public List<Address> Addresses { get; set; } = [];

    public List<Contact> Contacts { get; set; } = [];

    public List<TaxFiling> Filings { get; set; } = [];
}
