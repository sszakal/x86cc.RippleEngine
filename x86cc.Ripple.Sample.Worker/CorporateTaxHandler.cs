using Marten;
using x86cc.Ripple.Sample.Domain;
using x86cc.RippleEngine.Core;

namespace x86cc.Ripple.Sample.Worker;

/// <summary>
/// A corporate-tax ripple: recompute one company's corporate tax (<c>Revenue * Rate</c>) and persist it.
/// Opens a fresh lightweight session per execution so each ripple's Marten unit of work is isolated.
/// Idempotent — the recompute is a pure function of the company's revenue and the wave's rate.
/// </summary>
public sealed class CorporateTaxHandler(IDocumentStore store)
    : IRippleHandler<CorporateTaxChange, RecalcCorporateTax>
{
    public async Task<SplashReport?> Execute(CorporateTaxChange wave, RecalcCorporateTax ripple, IRippleContext context)
    {
        await using var session = store.LightweightSession();
        var company = await session.LoadAsync<Company>(ripple.CompanyId, context.CancellationToken);
        if (company is null)
        {
            return null; // nothing to do ⇒ inferred succeeded
        }

        company.CorporateTaxDue = Math.Round(company.Revenue * wave.Rate, 2);
        company.LastRecalculatedAt = DateTimeOffset.UtcNow;
        session.Store(company);
        await session.SaveChangesAsync(context.CancellationToken);

        // No report ⇒ the target company is inferred succeeded.
        return null;
    }
}
