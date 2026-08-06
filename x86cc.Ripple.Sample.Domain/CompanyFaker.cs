using Bogus;

namespace x86cc.Ripple.Sample.Domain;

/// <summary>
/// Generates <see cref="Company"/> documents for a seed batch. Bogus' <see cref="Faker"/> is not thread-safe,
/// so each seed ripple creates its own instance (handlers run concurrently). A company's <c>Id</c> and
/// <c>TaxCode</c> are pure functions of its global seed index — so a retried batch upserts the same rows
/// (idempotent bulk insert) and the target tax-code cardinalities stay exact. Passing a positive
/// <c>filings</c> count fills the nested detail (registration, addresses, contacts, and that many tax
/// filings) so the document grows to a configurable size.
/// </summary>
public sealed class CompanyFaker
{
    /// <summary>Line items per filing — fixed so each filing is a roughly constant size and count scales predictably.</summary>
    public const int LineItemsPerFiling = 8;

    // Rough serialized sizes (bytes) used to translate a target sizeKb into a filing count. Calibrated so the
    // default 300 KB lands close; exactness isn't important ("around 300kb").
    private const int BaseBytes = 900;       // id/name/etc. + registration + addresses + contacts
    private const int PerFilingBytes = 1587; // a filing with LineItemsPerFiling line items (measured via Marten)

    private static readonly string[] LegalForms = ["Ltd", "PLC", "LLP", "GmbH", "SARL", "Inc", "Pty"];
    private static readonly string[] FilingTypes = ["VAT", "CorporationTax", "PAYE", "Excise", "WithholdingTax"];
    private static readonly string[] FilingStatuses = ["Draft", "Submitted", "Assessed", "Paid", "Amended"];
    private static readonly string[] Categories = ["OperatingExpense", "CapitalAllowance", "Payroll", "Interest", "Depreciation", "RnD"];
    private static readonly string[] ContactRoles = ["CFO", "Accountant", "TaxAgent", "Director", "Auditor"];

    private readonly Faker _faker = new("en");

    public Company Create(long index, int filings = 0)
    {
        var company = CreateCore(index);
        if (filings <= 0)
        {
            return company;
        }

        AddDetail(company);
        company.Filings = BuildFilingSet(filings);
        return company;
    }

    /// <summary>
    /// Fast-path company creation for seeding: per-company identity (<c>Id</c>/<c>TaxCode</c>/<c>Revenue</c>/…)
    /// but a <b>shared</b> <paramref name="filings"/> list for the bulk padding. The filings content is never
    /// read (only its serialized size matters), so sharing one pre-built set across many companies avoids
    /// re-running Bogus ~11k times per document — the dominant seed cost — and keeps the live object graph tiny.
    /// </summary>
    public Company Create(long index, List<TaxFiling> filings)
    {
        var company = CreateCore(index);
        AddDetail(company);
        company.Filings = filings; // shared reference; Marten serializes each document independently (read-only)
        return company;
    }

    private Company CreateCore(long index) => new()
    {
        Id = IdFor(index),
        Name = _faker.Company.CompanyName(),
        Country = _faker.Address.CountryCode(),
        Revenue = _faker.Finance.Amount(10_000, 50_000_000),
        Headcount = _faker.Random.Int(1, 50_000),
        TaxCode = TaxCodePlan.TaxCodeForIndex(index),
    };

    private void AddDetail(Company company)
    {
        company.Registration = new CompanyRegistration
        {
            RegistrationNumber = _faker.Random.Replace("##-#######"),
            IncorporationDate = _faker.Date.PastOffset(30),
            LegalForm = _faker.PickRandom(LegalForms),
            Jurisdiction = _faker.Address.Country(),
            VatNumber = _faker.Random.Replace("??#########"),
            SicCode = _faker.Random.Replace("#####"),
            EmployeeCount = _faker.Random.Int(1, 250_000),
            IsPublic = _faker.Random.Bool(),
        };
        company.Addresses = [MakeAddress("Registered"), MakeAddress("Trading")];
        company.Contacts = [MakeContact(), MakeContact()];
    }

    /// <summary>Builds one set of <paramref name="filings"/> tax filings — the size-carrying padding. Used to
    /// pre-build a small pool of shared sets so per-company generation stays cheap (see <see cref="Create(long, List{TaxFiling})"/>).</summary>
    public List<TaxFiling> BuildFilingSet(int filings)
    {
        var set = new List<TaxFiling>(filings);
        for (var f = 0; f < filings; f++)
        {
            set.Add(MakeFiling());
        }
        return set;
    }

    /// <summary>How many filings to generate to approximate <paramref name="sizeKb"/> KB (0 ⇒ minimal company).</summary>
    public static int FilingsForSizeKb(int sizeKb) =>
        sizeKb <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling((sizeKb * 1024.0 - BaseBytes) / PerFilingBytes));

    private Address MakeAddress(string type) => new()
    {
        Type = type,
        Line1 = _faker.Address.StreetAddress(),
        Line2 = _faker.Address.SecondaryAddress(),
        City = _faker.Address.City(),
        Region = _faker.Address.State(),
        PostalCode = _faker.Address.ZipCode(),
        Country = _faker.Address.CountryCode(),
    };

    private Contact MakeContact() => new()
    {
        Name = _faker.Name.FullName(),
        Role = _faker.PickRandom(ContactRoles),
        Email = _faker.Internet.Email(),
        Phone = _faker.Phone.PhoneNumber(),
    };

    private TaxFiling MakeFiling()
    {
        var gross = _faker.Finance.Amount(50_000, 20_000_000);
        var deductions = Math.Round(gross * _faker.Random.Decimal(0.05m, 0.4m), 2);
        var taxable = gross - deductions;
        var rate = _faker.Random.Decimal(0.1m, 0.3m);
        var assessed = Math.Round(taxable * rate, 2);

        var lineItems = new List<LineItem>(LineItemsPerFiling);
        for (var i = 0; i < LineItemsPerFiling; i++)
        {
            lineItems.Add(new LineItem
            {
                Code = _faker.Random.Replace("LI-####"),
                Description = _faker.Commerce.ProductName(),
                Category = _faker.PickRandom(Categories),
                Amount = _faker.Finance.Amount(100, 500_000),
                Taxable = _faker.Random.Bool(),
                TaxCode = _faker.Random.Replace("VAT-??"),
            });
        }

        return new TaxFiling
        {
            Year = _faker.Random.Int(2005, 2025),
            Quarter = _faker.Random.Int(1, 4),
            FilingType = _faker.PickRandom(FilingTypes),
            GrossRevenue = gross,
            Deductions = deductions,
            TaxableIncome = taxable,
            TaxRate = rate,
            TaxAssessed = assessed,
            TaxPaid = Math.Round(assessed * _faker.Random.Decimal(0.5m, 1.0m), 2),
            Status = _faker.PickRandom(FilingStatuses),
            SubmittedAt = _faker.Date.PastOffset(5),
            ReferenceNumber = _faker.Random.Replace("REF-########"),
            Notes = _faker.Lorem.Sentence(8),
            LineItems = lineItems,
        };
    }

    /// <summary>A stable Guid derived from the seed index; a marker byte keeps index 0 from being Guid.Empty.</summary>
    public static Guid IdFor(long index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index);
        bytes[15] = 0x01;
        return new Guid(bytes);
    }
}
