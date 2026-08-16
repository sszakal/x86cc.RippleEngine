using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// End-to-end coverage of the per-target report inference: a handler returns <see cref="SplashReport"/> groups
/// (report-by-exception) and the engine records the resolved report on the splash and drives the ripple's
/// success/failure from it (any Failed target ⇒ the ripple fails and retries).
/// </summary>
public sealed class SplashReportTests : RippleTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task empty_report_marks_every_target_succeeded()
    {
        await ResetAsync();
        var wave = await SeedBatchAsync(["t1", "t2", "t3"]);

        var sink = new ReportSink { OnExecute = _ => null }; // report nothing ⇒ all succeeded
        await RunToCompletionAsync(sink, wave.Id);

        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Completed);
        // One splash, one Succeeded group covering all three targets.
        (await ScalarAsync("""
            select count(*) from ripple.splash where report @> '[{"outcome":"Succeeded","targetIds":["t1","t2","t3"]}]'
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task partial_report_keeps_reported_targets_and_infers_the_rest()
    {
        await ResetAsync();
        var wave = await SeedBatchAsync(["t1", "t2", "t3"]);

        // Only t1 is reported (with a message); t2/t3 are inferred succeeded/no-output. No Failed ⇒ ripple ok.
        var sink = new ReportSink { OnExecute = _ => SplashReport.Create().Success("t1", "special") };
        await RunToCompletionAsync(sink, wave.Id);

        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Completed);
        (await ScalarAsync("""select count(*) from ripple.splash where report @> '[{"output":"special","targetIds":["t1"]}]'""")).ShouldBe(1);
        (await ScalarAsync("""select count(*) from ripple.splash where report @> '[{"outcome":"Succeeded","targetIds":["t2","t3"]}]'""")).ShouldBe(1);
    }

    [Fact]
    public async Task a_failed_target_fails_the_ripple_and_retries_then_faults_the_wave()
    {
        await ResetAsync();
        var wave = await SeedBatchAsync(["t1", "t2"]);

        // t1 always fails ⇒ the whole ripple fails and retries up to max_attempts (2), then terminal.
        var sink = new ReportSink { OnExecute = _ => SplashReport.Create().Failed("t1", "bad t1") };
        await RunToCompletionAsync(sink, wave.Id, maxAttempts: 2);

        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Faulted);
        sink.Attempts.ShouldBe(2); // retried once, then terminal
        // Each attempt's splash reports the failed t1 (with the message) and the inferred-succeeded t2.
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Failed'")).ShouldBe(2);
        (await ScalarAsync("""select count(*) from ripple.splash where report @> '[{"outcome":"Failed","output":"bad t1","targetIds":["t1"]}]'""")).ShouldBe(2);
        (await ScalarAsync("""select count(*) from ripple.splash where report @> '[{"outcome":"Succeeded","targetIds":["t2"]}]'""")).ShouldBe(2);
    }

    [Fact]
    public async Task a_thrown_handler_fails_all_targets_with_the_exception_message()
    {
        await ResetAsync();
        var wave = await SeedBatchAsync(["t1", "t2"]);

        var sink = new ReportSink { OnExecute = _ => throw new InvalidOperationException("kaboom") };
        await RunToCompletionAsync(sink, wave.Id, maxAttempts: 1); // terminal on the first attempt

        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Faulted);
        // A throw ⇒ one Failed group over EVERY target, carrying the exception message.
        (await ScalarAsync("""select count(*) from ripple.splash where report @> '[{"outcome":"Failed","targetIds":["t1","t2"]}]'""")).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.splash where report::text like '%kaboom%'")).ShouldBe(1);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<Wave> SeedBatchAsync(string[] ids)
    {
        var wave = await CreateWaveAsync();
        var payload = JsonSerializer.Serialize(new BatchTax { Ids = ids }, Web);
        await Engine.AddRipplesAsync(wave.Id, [new RippleSeed(payload, nameof(BatchTax))]);
        return wave;
    }

    private async Task RunToCompletionAsync(ReportSink sink, Guid waveId, int? maxAttempts = null)
    {
        using var host = BuildEngineHost(
            engine => engine.AddHandler<RecalcContext, BatchTax, ReportingHandler>(
                batchSize: 1, gapSeconds: 1, maxAttempts: maxAttempts),
            o =>
            {
                o.MaxConcurrency = 4;
                o.RetryBackoff = TimeSpan.FromMilliseconds(50);
                o.MaxRetryBackoff = TimeSpan.FromMilliseconds(100);
            },
            services => services.AddSingleton(sink));

        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await Engine.GetWaveAsync(waveId) is { Status: WaveStatus.Completed or WaveStatus.Faulted })
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Wave never reached a terminal state within 60s.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
