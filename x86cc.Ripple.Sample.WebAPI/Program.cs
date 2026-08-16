using System.Text.Json;
using Dapper;
using Marten;
using x86cc.Ripple.Sample.Domain;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Hosting;
using x86cc.RippleEngine.MartenDb;
using x86cc.RippleEngine.Storage;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ripple")
    ?? throw new InvalidOperationException("Missing 'ripple' connection string.");

// Creation side only — EnableWorkers=false means this process does NOT run the engine, it just creates waves.
// It still gets the engine store (to create the seed wave + query status), the schema migration, and the Marten
// fan-out generator (for the taxation change); Marten itself applies the Company schema on startup so the first
// fan-out has a table to select from. The dashboard's read API + SPA live on the workers (the always-on engine
// cluster), not here.
builder.AddRippleEngine(o =>
{
    o.EnableWorkers = false;
    o.UseMartenFanOut();
});
builder.Services.AddSampleMarten(connectionString, applyChangesOnStartup: true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/", () => Results.Redirect("/swagger"));

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// Wave 1 — seed: fan out one ripple per contiguous index range; each ripple bulk-inserts its companies.
app.MapPost("/seed", async (long? total, int? batchSize, int? sizeKb, IEngineStore engine) =>
{
    var totalCount = total ?? 10_000_000L;
    var size = Math.Max(1, batchSize ?? 5_000);
    var docSizeKb = Math.Max(0, sizeKb ?? 0);

    var seeds = new List<RippleSeed>();
    for (var start = 0L; start < totalCount; start += size)
    {
        var count = (int)Math.Min(size, totalCount - start);
        var payload = JsonSerializer.Serialize(new SeedBatch { StartIndex = start, Count = count }, json);
        seeds.Add(new RippleSeed(payload, nameof(SeedBatch)));
    }

    // Atomic root create: wave row + all seed ripples in one transaction (no zero-ripple window).
    // The payload document rents from ArrayPool and is read only during the insert, so dispose it after.
    using var seedPayload = JsonSerializer.SerializeToDocument(
        new SeedRun { Total = totalCount, BatchSize = size, SizeKb = docSizeKb }, json);
    var wave = await engine.CreateWaveWithRipplesAsync(new Wave
    {
        Name = $"Seed {totalCount:N0} companies (~{(docSizeKb == 0 ? "0.2" : docSizeKb.ToString())} KB each)",
        Type = "seed",
        Payload = seedPayload,
        PayloadType = nameof(SeedRun),
    }, seeds);

    return Results.Ok(new { waveId = wave.Id, ripples = seeds.Count, total = totalCount, batchSize = size, sizeKb = docSizeKb });
});

// The target tax codes and their intended sizes, plus the actual seeded counts.
app.MapGet("/tax-codes", async (IQuerySession session) =>
{
    var results = new List<object>();
    foreach (var target in TaxCodePlan.Targets)
    {
        var actual = await session.Query<Company>().CountAsync(c => c.TaxCode == target.Code);
        results.Add(new { target.Code, expected = target.Count, actual });
    }

    return Results.Ok(results);
});

// Wave 2a — corporate tax: server-side INSERT..SELECT fans out one ripple per impacted company.
app.MapPost("/corporate-tax", async (string taxCode, decimal? rate, IMartenWaveGenerator generator, IQuerySession session) =>
{
    var r = rate ?? 0.23m;
    var wave = await generator
        .Create(session, $"Corporate tax {taxCode}", new CorporateTaxChange { TaxCode = taxCode, Rate = r })
        .AddRipples<Company, RecalcCorporateTax>(
            c => c.TaxCode == taxCode,
            c => new RecalcCorporateTax { CompanyId = c.Id })
        .DispatchAsync();

    return Results.Ok(new { waveId = wave.Id, ripples = wave.RippleCount, taxCode, rate = r });
});

// Wave 2b — employee (payroll) tax: a distinct type_key, so it competes with corporate tax for slots.
app.MapPost("/employee-tax", async (string taxCode, decimal? ratePerEmployee, IMartenWaveGenerator generator, IQuerySession session) =>
{
    var r = ratePerEmployee ?? 12.5m;
    var wave = await generator
        .Create(session, $"Employee tax {taxCode}", new EmployeeTaxChange { TaxCode = taxCode, RatePerEmployee = r })
        .AddRipples<Company, RecalcEmployeeTax>(
            c => c.TaxCode == taxCode,
            c => new RecalcEmployeeTax { CompanyId = c.Id })
        .DispatchAsync();

    return Results.Ok(new { waveId = wave.Id, ripples = wave.RippleCount, taxCode, ratePerEmployee = r });
});

// Live cluster membership + the TRUE in-flight count: each worker writes its in-memory ExecutingCount on
// every heartbeat, so sum(executing) is the real, low-latency cluster concurrency — unlike the wave's `running`
// number, which is a periodic recompute. The saturation E2E test samples totalExecuting from here.
app.MapGet("/engine/instances", async (IEngineStore engine) =>
{
    var beats = await engine.GetHeartbeatsAsync();
    return Results.Ok(new
    {
        totalExecuting = beats.Sum(b => (long)b.Executing),
        instances = beats.Select(b => new { b.InstanceId, b.LastSeenAt, b.Executing })
    });
});

app.MapGet("/waves/{id:guid}", async (Guid id, IEngineStore engine) =>
    await engine.GetWaveAsync(id) is { } wave ? Results.Ok(wave) : Results.NotFound());

app.MapGet("/waves", async (RippleDataSource db) =>
{
    await using var conn = await db.OpenConnectionAsync();
    // Live numbers are periodically recomputed onto the wave row; a wave not yet refreshed (refreshed_at is
    // null) shows all its ripples as pending, and succeeded is derived (never stored).
    var rows = await conn.QueryAsync(
        """
        select w.id, w.name, w.type, w.status, w.ripple_count,
               case when w.refreshed_at is null then w.ripple_count else w.pending end as pending,
               w.running,
               w.paused,
               case when w.refreshed_at is null then 0
                    else greatest(0, w.ripple_count - w.pending - w.running - w.paused - w.failed) end as succeeded,
               w.failed,
               w.retry_count,
               w.created_at, w.completed_at
        from ripple.wave w
        order by w.created_at desc limit 50
        """);
    return Results.Ok(rows);
});

app.Run();
