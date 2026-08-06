using Marten;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;

namespace x86cc.Ripple.Sample.Domain;

public static class SampleMartenExtensions
{
    /// <summary>
    /// Registers Marten for the sample's <see cref="Company"/> documents in its own <c>sample</c> schema
    /// (separate from the engine's <c>ripple</c> schema, same database). Enums are stored as strings — the
    /// fan-out <c>INSERT..SELECT</c> writes enum literals into JSONB — and <c>TaxCode</c> is indexed so the
    /// taxation-change predicate (<c>c => c.TaxCode == code</c>) is served efficiently. Pass
    /// <paramref name="applyChangesOnStartup"/> on the process that issues the fan-out (the WebAPI) so the
    /// Company table + index exist before the first query; the workers rely on Marten's advisory-locked lazy
    /// creation via their bulk inserts.
    /// </summary>
    public static IServiceCollection AddSampleMarten(this IServiceCollection services, string connectionString,
        bool applyChangesOnStartup = false)
    {
        var config = services.AddMarten(opts =>
            {
                opts.Connection(connectionString);
                opts.DatabaseSchemaName = "sample";
                opts.UseSystemTextJsonForSerialization(EnumStorage.AsString);
                opts.Schema.For<Company>().Index(x => x.TaxCode);
            })
            .UseLightweightSessions();

        if (applyChangesOnStartup)
        {
            config.ApplyAllDatabaseChangesOnStartup();
        }

        return services;
    }
}
