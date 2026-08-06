using Marten;
using x86cc.Ripple.Sample.Domain;
using x86cc.RippleEngine.Core;

namespace x86cc.Ripple.Sample.Worker;

/// <summary>
/// An employee-tax (payroll) ripple: recompute one company's payroll tax (<c>Headcount * RatePerEmployee</c>)
/// and persist it. A distinct <c>type_key</c> from corporate tax, so the two categories compete for execution
/// slots and their weighted fair-share is observable. Same isolated-session, idempotent pattern.
/// </summary>
public sealed class EmployeeTaxHandler(IDocumentStore store)
    : IRippleHandler<EmployeeTaxChange, RecalcEmployeeTax>
{
    public async Task<SplashReport?> Execute(EmployeeTaxChange wave, RecalcEmployeeTax ripple, IRippleContext context)
    {
        await using var session = store.LightweightSession();
        var company = await session.LoadAsync<Company>(ripple.CompanyId, context.CancellationToken);
        if (company is null)
        {
            return null; // nothing to do ⇒ inferred succeeded
        }

        company.EmployeeTaxDue = Math.Round(company.Headcount * wave.RatePerEmployee, 2);
        company.LastRecalculatedAt = DateTimeOffset.UtcNow;
        session.Store(company);
        await session.SaveChangesAsync(context.CancellationToken);

        return null;
    }
}
