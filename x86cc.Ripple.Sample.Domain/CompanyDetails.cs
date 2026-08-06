namespace x86cc.Ripple.Sample.Domain;

// Nested detail types that let a Company aggregate grow to an arbitrary size — the Filings list (each with
// its own line items) is the scalable part, driven by the seed's sizeKb knob. All stored inside the single
// Company JSONB document, so large aggregates exercise Postgres TOAST + the recalc load/rewrite cost.

public sealed class CompanyRegistration
{
    public string RegistrationNumber { get; set; } = "";
    public DateTimeOffset IncorporationDate { get; set; }
    public string LegalForm { get; set; } = "";
    public string Jurisdiction { get; set; } = "";
    public string VatNumber { get; set; } = "";
    public string SicCode { get; set; } = "";
    public int EmployeeCount { get; set; }
    public bool IsPublic { get; set; }
}

public sealed class Address
{
    public string Type { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}

public sealed class Contact
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
}

/// <summary>One period's tax filing with a set of line items — the repeatable unit that scales aggregate size.</summary>
public sealed class TaxFiling
{
    public int Year { get; set; }
    public int Quarter { get; set; }
    public string FilingType { get; set; } = "";
    public decimal GrossRevenue { get; set; }
    public decimal Deductions { get; set; }
    public decimal TaxableIncome { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAssessed { get; set; }
    public decimal TaxPaid { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string ReferenceNumber { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<LineItem> LineItems { get; set; } = [];
}

public sealed class LineItem
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public bool Taxable { get; set; }
    public string TaxCode { get; set; } = "";
}
