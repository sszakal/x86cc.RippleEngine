using x86cc.Ripple.Sample.Domain;
using x86cc.Ripple.Sample.Worker;
using x86cc.RippleEngine.Hosting;

// A web host (not a bare worker service): besides running the engine, every instance also serves the
// monitoring dashboard — the read API (/api) and the Angular SPA — since the symmetric, always-on pollers are
// the natural place to expose a read surface over the ripple schema they already coordinate on.
var builder = WebApplication.CreateBuilder(args);

// This process IS a poller: the engine's dispatcher + recovery + schedule-seeder + stats-refresh hosted
// services run here, and the handlers below do the actual per-ripple work. The execution cap + claim batch are
// env-tunable so a demo can crank slots up (global exec cap = MaxConcurrency x replicas) and watch saturation.
// PrefetchFactor sets the pipeline depth (MaxConcurrency x PrefetchFactor): a deeper prefetch buffer keeps the
// execute block fed when handlers are fast enough to drain in-flight faster than the next poll refills it — so
// concurrency holds near its cap instead of sawtoothing down to a poll-latency trough. ClaimBatchSize is set
// so a single poll can top the whole depth back up.
// Corporate vs employee tax are two competing job types: their fair-share is precomputed into schedule_order at
// fan-out via (batchSize, gapSeconds). Equal gap + employee's 3x batch => employee drains ~3x faster, and the
// two waves' batches interleave in the global queue instead of one draining fully before the other starts.
// Retention: keep a finished wave's report for a while after completion, then the retention purge deletes it.
// Seed waves keep-forever (default null); the taxation-change waves keep their reports 90 days.
builder.AddRippleEngine(o =>
    {
        // The connection string comes from ConnectionStrings:ripple, which Aspire injects.
        o.MaxConcurrency = EnvInt("RIPPLE_MAX_CONCURRENCY", 16);
        o.PrefetchFactor = EnvInt("RIPPLE_PREFETCH", 4);
        o.ClaimBatchSize = EnvInt("RIPPLE_CLAIM_BATCH", 256);
        o.EnableDashboard = true;
        // Exports ripple.claimed/succeeded/failed/duration; the OTLP endpoint Aspire injects picks them up.
        o.EnableMetrics = true;
        o.RetentionByWaveType[nameof(CorporateTaxChange)] = TimeSpan.FromDays(90);
        o.RetentionByWaveType[nameof(EmployeeTaxChange)] = TimeSpan.FromDays(90);
    })
    .AddHandler<SeedRun, SeedBatch, SeedCompaniesHandler>(batchSize: 15, gapSeconds: 10)
    .AddHandler<CorporateTaxChange, RecalcCorporateTax, CorporateTaxHandler>(batchSize: 5, gapSeconds: 1)
    .AddHandler<EmployeeTaxChange, RecalcEmployeeTax, EmployeeTaxHandler>(batchSize: 15, gapSeconds: 1);

// Marten (the sample's Company documents) — the same database, the app's own store.
builder.Services.AddSampleMarten(builder.Configuration.GetConnectionString("ripple")
    ?? throw new InvalidOperationException("Missing 'ripple' connection string."));

var app = builder.Build();

// The schema migration (advisory-lock-safe on every replica) and the dashboard's endpoints are wired by
// AddRippleEngine — it runs both before this host serves its first request.
app.Run();

static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
