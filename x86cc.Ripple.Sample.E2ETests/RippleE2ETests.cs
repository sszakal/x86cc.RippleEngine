using System.Net.Http.Json;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace x86cc.Ripple.Sample.E2ETests;

/// <summary>
/// Manually-run, whole-system throughput tests. Spin up the sample via Aspire, seed once, then run taxation
/// changes at a few scales and report how long each wave takes + its ripples/sec. Kick off with, e.g.:
/// <code>
/// RIPPLE_SEED_TOTAL=10000000 dotnet test --filter seed_generates_companies
/// dotnet test --filter taxation_change_measures_throughput
/// </code>
/// The seed persists in the named Postgres volume, so the taxation tests reuse it across separate runs.
/// </summary>
[Collection(AspireCollection.Name)]
[TestCaseOrderer("x86cc.Ripple.Sample.E2ETests.PriorityOrderer", "x86cc.Ripple.Sample.E2ETests")]
public sealed class RippleE2ETests(AspireAppFixture fixture, ITestOutputHelper output)
{
    private HttpClient Http => fixture.Http;

    [Fact]
    [TestPriority(0)]
    public async Task seed_generates_companies_and_measures_throughput()
    {
        var total = EnvLong("RIPPLE_SEED_TOTAL", 10_000_000);
        var batch = (int)EnvLong("RIPPLE_SEED_BATCH", 1000);
        var sizeKb = (int)EnvLong("RIPPLE_SEED_SIZEKB", 10); // 0 = minimal ~0.2 KB; e.g. 300 for very large aggregates

        var seed = await Http.PostReadAsync<SeedResponse>($"/seed?total={total}&batchSize={batch}&sizeKb={sizeKb}");
        output.WriteLine($"seed wave {seed.WaveId}: {seed.Ripples:N0} ripples for {seed.Total:N0} companies " +
                         $"(batch {seed.BatchSize:N0}, ~{(sizeKb == 0 ? "0.2" : sizeKb.ToString())} KB each)");

        var (elapsed, ripples) = await MeasureWaveAsync(seed.WaveId, "SEED");
        output.WriteLine($"SEED throughput: {total / elapsed.TotalSeconds:N0} companies/sec");
        ripples.ShouldBe(seed.Ripples);

        var codes = await Http.GetFromJsonAsync<List<TaxCodeDto>>("/tax-codes") ?? [];
        foreach (var c in codes)
        {
            output.WriteLine($"  {c.Code}: expected {c.Expected:N0}, actual {c.Actual:N0}");
        }
    }

    [Theory]
    [TestPriority(1)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task taxation_change_measures_throughput(int targetSize)
    {
        // Find the tax code linked to ~targetSize companies — queried live from the DB via /tax-codes.
        var codes = await Http.GetFromJsonAsync<List<TaxCodeDto>>("/tax-codes") ?? [];
        var target = codes.FirstOrDefault(c => c.Expected == targetSize);
        target.ShouldNotBeNull($"No tax code configured for target size {targetSize}");

        target!.Actual.ShouldBeGreaterThanOrEqualTo(targetSize,
            $"Seed first — {target.Code} has {target.Actual:N0}/{targetSize:N0} companies. " +
            $"Run seed_generates_companies_and_measures_throughput with RIPPLE_SEED_TOTAL >= {targetSize}.");

        var resp = await Http.PostReadAsync<TaxChangeResponse>(
            $"/corporate-tax?taxCode={target.Code}&rate=0.2");
        output.WriteLine($"corporate-tax {target.Code}: {resp.Ripples:N0} ripples fanned out (wave {resp.WaveId})");
        resp.Ripples.ShouldBe(targetSize);

        var (_, ripples) = await MeasureWaveAsync(resp.WaveId, $"TAX {target.Code}");
        ripples.ShouldBe(targetSize);
    }

    /// <summary>
    /// Under a big constant backlog, the cluster's execution concurrency should HOLD near its cap, not
    /// sawtooth 0↔cap (which would mean the poller can't keep the TPL block fed). Fans out one large
    /// single-type wave and samples the TRUE in-flight count — <c>sum(instance_heartbeat.executing)</c> via
    /// <c>/engine/instances</c>, which each worker writes live on every beat — rather than
    /// the wave's <c>running</c> number, a 2s periodic recompute that would alias the batchy pipeline into false
    /// dips. Asserts the steady-state plateau doesn't collapse.
    /// </summary> 
    [Fact]
    [TestPriority(2)]
    public async Task concurrency_stays_saturated_under_constant_work()
    {
        var codes = await Http.GetFromJsonAsync<List<TaxCodeDto>>("/tax-codes") ?? [];
        var code = codes.Where(c => c.Actual >= 20_000).MaxBy(c => c.Actual);
        code.ShouldNotBeNull("Seed a code with >= 20,000 companies first (e.g. RIPPLE_SEED_TOTAL=111000).");

        var resp = await Http.PostReadAsync<TaxChangeResponse>($"/corporate-tax?taxCode={code!.Code}&rate=0.2");
        output.WriteLine($"[SATURATION] corporate-tax {code.Code} -> {resp.Ripples:N0} ripples; sampling live in-flight (executing)...");

        var samples = new List<(long Executing, long Pending, long Settled)>();
        var start = DateTime.UtcNow;
        var deadline = start + TimeSpan.FromMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            // The wave gives pending/settled (for windowing + completion); /engine/instances gives the live,
            // un-aliased cluster in-flight (the actual saturation signal).
            var w = await Http.GetFromJsonAsync<WaveDto>($"/waves/{resp.WaveId}");
            var inst = await Http.GetFromJsonAsync<InstancesDto>("/engine/instances");
            w.ShouldNotBeNull();
            var executing = inst?.TotalExecuting ?? 0;
            var settled = w!.Succeeded + w.Failed;
            samples.Add((executing, w.Pending, settled));
            if (w.RippleCount > 0 && settled >= w.RippleCount && w.Pending == 0)
            {
                break;
            }

            await Task.Delay(250);
        }

        // Steady state = after the block has ramped up and while the backlog still exceeds the in-flight level
        // (so there is always work to keep it full). Excludes ramp-up and the final drain.
        var maxExecuting = samples.Max(s => s.Executing);
        var rampEnd = samples.FindIndex(s => s.Executing >= 0.8 * maxExecuting);
        var drainStart = samples.FindLastIndex(s => s.Pending >= maxExecuting);
        var steady = rampEnd >= 0 && drainStart > rampEnd
            ? samples.GetRange(rampEnd, drainStart - rampEnd + 1).Select(s => (double)s.Executing).ToList()
            : [];
        steady.Count.ShouldBeGreaterThan(5, "not enough steady-state samples — seed a bigger backlog");

        var sorted = steady.OrderBy(x => x).ToList();
        double Percentile(double p) =>
            sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];

        var avg = steady.Average();
        var min = steady.Min();
        var max = steady.Max();
        // The floor is the 10th percentile, not the absolute min: even a well-fed pipeline briefly hits a deep
        // trough when all workers' claim/settle sawtooths happen to align, and one such sample shouldn't flip
        // the verdict. p10 measures where the plateau actually sits.
        var p10 = Percentile(10);
        var stddev = Math.Sqrt(steady.Average(r => (r - avg) * (r - avg)));
        var cov = stddev / Math.Max(1, avg);
        output.WriteLine($"[SATURATION] plateau over {steady.Count} steady samples: avg {avg:N1}, p10 {p10:N0}, " +
                         $"min {min:N0}, max {max:N0}, stddev {stddev:N1}, CoV {cov:P0} (peak in-flight {maxExecuting})");
        // A smooth, saturated pipeline holds near its peak (p10 close to avg, low CoV). A sawtooth shows as a
        // low avg vs peak and a high CoV / low p10. This is a measurement test — it reports rather than fails,
        // except on total collapse.
        var smooth = p10 >= 0.6 * avg && cov < 0.5;
        output.WriteLine($"[SATURATION] {(smooth ? "SMOOTH — pipeline held its plateau" : "OSCILLATING — in-flight collapses below its peak while a backlog remains (poller not keeping the block fed)")}");

        avg.ShouldBeGreaterThan(1.0, "the pipeline made essentially no concurrent progress under a large backlog");
    }

    /// <summary>
    /// Two competing job types on disjoint tax codes should split the shared execution slots by their
    /// precomputed schedule. Both run gap=1s; employee's batch is 3x corporate's (15 vs 5), so its
    /// steady-state share (~ batchSize/gap) is ~3x. The batches are kept far below the cluster's execution
    /// capacity, so the two waves <b>blend</b> — both run concurrently in a ~1:3 mix rather than ping-ponging
    /// one whole slot at a time (watch the running counts move together, not in anti-phase). Runs both with
    /// large backlogs and reports each type's throughput during the overlap; asserts employee gets more.
    /// </summary>
    [Fact]
    [TestPriority(3)]
    public async Task fair_share_splits_throughput_by_schedule()
    {
        var big = (await Http.GetFromJsonAsync<List<TaxCodeDto>>("/tax-codes") ?? [])
            .Where(c => c.Actual >= 50_000).OrderByDescending(c => c.Actual).ToList();
        big.Count.ShouldBeGreaterThanOrEqualTo(2,
            "Need two codes with >= 50,000 companies (seed more, e.g. RIPPLE_SEED_TOTAL=1000000).");
        var empCode = big[0].Code;   // larger-share type on the larger backlog → longer overlap
        var corpCode = big[1].Code;

        var corp = await Http.PostReadAsync<TaxChangeResponse>($"/corporate-tax?taxCode={corpCode}&rate=0.2");
        var emp = await Http.PostReadAsync<TaxChangeResponse>($"/employee-tax?taxCode={empCode}&ratePerEmployee=12.5");
        output.WriteLine($"[FAIR-SHARE] corporate {corpCode} ({corp.Ripples:N0}) vs employee {empCode} ({emp.Ripples:N0})");

        long corpFrom = -1, empFrom = -1, corpTo = 0, empTo = 0;
        double tFrom = 0, tTo = 0;
        var overlapSamples = 0;
        var start = DateTime.UtcNow;
        var deadline = start + TimeSpan.FromMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            var wc = await Http.GetFromJsonAsync<WaveDto>($"/waves/{corp.WaveId}");
            var we = await Http.GetFromJsonAsync<WaveDto>($"/waves/{emp.WaveId}");
            var now = (DateTime.UtcNow - start).TotalSeconds;
            output.WriteLine($"[FAIR t={now,4:N0}s] corp running {wc!.Running,3} settled {wc.Succeeded + wc.Failed,7:N0}/{wc.RippleCount:N0}" +
                             $" | emp running {we!.Running,3} settled {we.Succeeded + we.Failed,7:N0}/{we.RippleCount:N0}");

            if (wc.Pending > 0 && we.Pending > 0) // both still backlogged → they compete
            {
                if (corpFrom < 0)
                {
                    corpFrom = wc.Succeeded + wc.Failed;
                    empFrom = we.Succeeded + we.Failed;
                    tFrom = now;
                }

                corpTo = wc.Succeeded + wc.Failed;
                empTo = we.Succeeded + we.Failed;
                tTo = now;
                overlapSamples++;
            }

            if (wc is { Pending: 0, Running: 0 } && we is { Pending: 0, Running: 0 })
            {
                break;
            }

            await Task.Delay(500);
        }

        overlapSamples.ShouldBeGreaterThan(3, "not enough overlap — backlogs drained too fast; seed larger codes");
        var dur = Math.Max(0.001, tTo - tFrom);
        var corpRate = (corpTo - corpFrom) / dur;
        var empRate = (empTo - empFrom) / dur;
        output.WriteLine($"[FAIR-SHARE] during {dur:N0}s overlap — corporate {corpRate:N0}/s | employee {empRate:N0}/s");
        output.WriteLine($"[FAIR-SHARE] employee:corporate ≈ {empRate / Math.Max(1, corpRate):N1}:1 throughput " +
                         "(configured batch share 15:5 = 3:1)");

        empRate.ShouldBeGreaterThan(corpRate * 1.3,
            "employee (3x batch share) should get materially more throughput than corporate");
    }

    /// <summary>Polls a wave to completion, logging progress + instantaneous rate, and reports total throughput.</summary>
    private async Task<(TimeSpan Elapsed, long Ripples)> MeasureWaveAsync(Guid waveId, string label)
    {
        var start = DateTime.UtcNow;
        long lastSettled = 0;
        var lastTick = start;
        var deadline = start + TimeSpan.FromMinutes(30);

        while (DateTime.UtcNow < deadline)
        {
            var w = await Http.GetFromJsonAsync<WaveDto>($"/waves/{waveId}");
            w.ShouldNotBeNull();

            var settled = w!.Succeeded + w.Failed;
            var now = DateTime.UtcNow;
            var instRate = (settled - lastSettled) / Math.Max(0.001, (now - lastTick).TotalSeconds);
            output.WriteLine(
                $"[{label}] {settled:N0}/{w.RippleCount:N0} settled — running {w.Running:N0}, pending {w.Pending:N0} — {instRate:N0}/s");
            lastSettled = settled;
            lastTick = now;

            if (w.RippleCount > 0 && settled >= w.RippleCount && w.Running == 0 && w.Pending == 0)
            {
                var elapsed = now - start;
                output.WriteLine(
                    $"[{label}] DONE: {w.RippleCount:N0} ripples in {elapsed.TotalSeconds:N1}s = " +
                    $"{w.RippleCount / elapsed.TotalSeconds:N0}/s (succeeded {w.Succeeded:N0}, failed {w.Failed:N0})");
                w.Failed.ShouldBe(0);
                return (elapsed, w.RippleCount);
            }

            // Match the engine's WaveStatsRefreshInterval (default 2s): the wave's numbers are periodically
            // recomputed onto the wave row, so sampling faster than it refreshes just re-reads an
            // unchanged snapshot and reports a phantom 0/s between real steps.
            await Task.Delay(2000);
        }

        throw new TimeoutException($"[{label}] wave {waveId} did not complete within the measurement window.");
    }

    private static long EnvLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}

file static class HttpExtensions
{
    public static async Task<T> PostReadAsync<T>(this HttpClient http, string url)
    {
        var resp = await http.PostAsync(url, content: null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }
}

public sealed record SeedResponse(Guid WaveId, int Ripples, long Total, int BatchSize);

public sealed record TaxChangeResponse(Guid WaveId, long Ripples);

public sealed record TaxCodeDto(string Code, int Expected, int Actual);

public sealed record WaveDto(long RippleCount, long Pending, long Running, long Succeeded, long Failed);

public sealed record InstancesDto(long TotalExecuting);
