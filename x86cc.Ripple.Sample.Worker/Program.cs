using OpenTelemetry;
using x86cc.Ripple.Sample.Domain;
using x86cc.Ripple.Sample.Worker;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

// A web host (not a bare worker service): besides running the engine, every instance also serves the
// monitoring dashboard — the read API (/api) and the Angular SPA (wwwroot) — since the symmetric, always-on
// pollers are the natural place to expose a read surface over the ripple schema they already coordinate on.
var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ripple")
    ?? throw new InvalidOperationException("Missing 'ripple' connection string.");

// Export the engine's throughput metrics (ripple.claimed/succeeded/failed/duration) to the Aspire dashboard.
// Guarded on the OTLP endpoint Aspire injects, so a standalone run isn't noisy.
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
{
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m.AddMeter(RippleMetrics.MeterName))
        .UseOtlpExporter();
}

// Engine storage (the ripple schema) + Marten (the sample's Company documents), same database.
// Retention: keep a finished wave's report for a while after completion, then the retention purge deletes it.
// Seed waves keep-forever (default null); the taxation-change waves keep their reports 90 days.
builder.Services.AddRippleStorage(connectionString, o =>
{
    o.RetentionByWaveType[nameof(x86cc.Ripple.Sample.Domain.CorporateTaxChange)] = TimeSpan.FromDays(90);
    o.RetentionByWaveType[nameof(x86cc.Ripple.Sample.Domain.EmployeeTaxChange)] = TimeSpan.FromDays(90);
});
builder.Services.AddSampleMarten(connectionString);

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
// Shutdown budget: the host must outlast the engine's drain, or it hard-kills the process mid-drain and the
// in-flight ripples are left Running for recovery to time out on. Set both explicitly (and in this order) so
// the relationship is visible rather than resting on framework defaults.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Services.AddRippleEngine(o =>
    {
        o.MaxConcurrency = EnvInt("RIPPLE_MAX_CONCURRENCY", 16);
        o.PrefetchFactor = EnvInt("RIPPLE_PREFETCH", 4);
        o.ClaimBatchSize = EnvInt("RIPPLE_CLAIM_BATCH", 256);
        // Comfortably under the 30s host timeout above.
        o.ShutdownDrainGrace = TimeSpan.FromSeconds(20);
    })
    .AddHandler<SeedRun, SeedBatch, SeedCompaniesHandler>(batchSize: 15, gapSeconds: 10)
    // Corporate vs employee batches are kept SMALL relative to the cluster's execution capacity
    // (~MaxConcurrency x replicas = 16 x 3 = 48 concurrent slots) so many slots of BOTH waves sit in the
    // schedule_order claim window at once — the cluster runs a blended 1:3 mix concurrently instead of draining
    // one whole slot before the other. Equal gap keeps share = batch, so the 5:15 ratio still yields the 3:1
    // employee:corporate throughput split. (Bump these toward/above 48 to see coarse slot-at-a-time ping-pong.)
    .AddHandler<CorporateTaxChange, RecalcCorporateTax, CorporateTaxHandler>(batchSize: 5, gapSeconds: 1)
    .AddHandler<EmployeeTaxChange, RecalcEmployeeTax, EmployeeTaxHandler>(batchSize: 15, gapSeconds: 1);

var app = builder.Build();

// Safe to run from every replica: a Postgres advisory lock serializes the first migration.
app.Services.MigrateRipple();

// Serve the dashboard: its read API, its static bundle, and an SPA fallback so client-side routes
// (e.g. /waves/{id}) resolve to index.html. Same origin as /api, so the SPA needs no proxy in production.
app.MapDashboardApi();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
