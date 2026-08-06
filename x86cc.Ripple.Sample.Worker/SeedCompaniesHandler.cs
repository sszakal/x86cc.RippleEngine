using Marten;
using Weasel.Core;
using x86cc.Ripple.Sample.Domain;
using x86cc.RippleEngine.Core;

namespace x86cc.Ripple.Sample.Worker;

/// <summary>
/// A seed ripple: generate the companies for one contiguous index range and bulk-insert them.
/// <para>
/// Performance-shaped for large documents: (1) the size-carrying <c>Filings</c> padding is generated once into
/// a small shared pool and reused across companies (Bogus is the dominant cost, so per-document generation is
/// avoided); (2) the first attempt uses Marten's fastest straight-COPY (<see cref="BulkInsertMode.InsertsOnly"/>)
/// and only a retry upserts (<see cref="BulkInsertMode.OverwriteExisting"/>) — ids are a deterministic function
/// of the index, so a retried batch is idempotent either way; (3) generation + insert run in sub-batches so
/// peak memory is bounded by the chunk, not the whole ripple, keeping many concurrent handlers off the GC.
/// </para>
/// </summary>
public sealed class SeedCompaniesHandler(IDocumentStore store) : IRippleHandler<SeedRun, SeedBatch>
{
    // Insert in sub-batches so peak heap ≈ ChunkSize × docSize, not ripple.Count × docSize (× MaxConcurrency).
    private const int ChunkSize = 500;

    // How many distinct filing-sets to pre-build and share across companies (padding content is never read).
    private const int FilingPoolSize = 8;

    public async Task<SplashReport?> Execute(SeedRun wave, SeedBatch ripple, IRippleContext context)
    {
        var faker = new CompanyFaker();
        var filingCount = CompanyFaker.FilingsForSizeKb(wave.SizeKb);

        // Build the shared filing-set pool ONCE (the expensive Bogus work), then every company just references
        // one of these sets — instead of regenerating ~11k random values per document.
        var pool = filingCount > 0
            ? Enumerable.Range(0, Math.Min(FilingPoolSize, ripple.Count)).Select(_ => faker.BuildFilingSet(filingCount)).ToList()
            : null;

        // Fast COPY on the first try; idempotent upsert only when retrying a partially-applied batch.
        var mode = context.Attempt == 1 ? BulkInsertMode.InsertsOnly : BulkInsertMode.OverwriteExisting;

        var end = ripple.StartIndex + ripple.Count;
        for (var start = ripple.StartIndex; start < end; start += ChunkSize)
        {
            var count = (int)Math.Min(ChunkSize, end - start);
            var companies = new List<Company>(count);
            for (var i = start; i < start + count; i++)
            {
                companies.Add(pool is null ? faker.Create(i) : faker.Create(i, pool[(int)(i % pool.Count)]));
            }

            await store.BulkInsertAsync(companies, mode, cancellation: context.CancellationToken);
        }

        // No report ⇒ the target (this batch) is inferred succeeded.
        return null;
    }
}
