using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// An outcome the database REJECTS must not wedge settlement. A write that fails for a reason carried by the data
/// (the canonical case: a report whose text holds a NUL, which <c>jsonb</c> refuses) fails identically on every
/// retry, and the flush loop used to retry the same batch forever: the instance stayed alive and heartbeating, so
/// recovery never reclaimed its work, its slots filled, and it stopped claiming for good. Run against the real
/// store, because the rejection is Postgres's own.
/// </summary>
public sealed class SettlementPoisonTests : RippleTestBase
{
    // What System.Text.Json emits for a NUL character — valid JSON that Postgres will not store as jsonb.
    private const string UnstorableReport = """[{"outcome":"Succeeded","output":"bad \u0000 byte","targetIds":["t1"]}]""";

    [Fact]
    public async Task one_unwritable_outcome_does_not_block_the_rest_of_its_batch()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 5);
        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        claimed.Count.ShouldBe(5);
        var poison = claimed[2];

        var options = Options.Create(new RippleEngineOptions { InstanceId = "inst-1" });
        var inFlight = new InFlightTracker();
        var settlement = new SettlementWriter(options, Splashes, inFlight, new RippleMetrics(), TimeProvider.System,
            NullLogger<SettlementWriter>.Instance);
        // The drain deadline bounds the test: a writer that retries the poisoned batch forever gives up here and
        // leaves every ripple Running, which the assertions below then report.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        settlement.Start(drain.Token);

        foreach (var c in claimed)
        {
            inFlight.Track(c.Id, c.Attempt);
            settlement.Succeeded(new RippleCompletion(c.Id, wave.Id, c.Attempt, DateTimeOffset.UtcNow,
                c.Id == poison.Id ? UnstorableReport : """[{"outcome":"Succeeded","output":null,"targetIds":["ok"]}]""",
                DateTimeOffset.UtcNow));
        }

        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));

        (await ScalarAsync("select count(*) from ripple.ripple where state = 'Succeeded'"))
            .ShouldBe(5, "the healthy outcomes AND the unwritable one must all settle");
        inFlight.Count.ShouldBe(0, "every slot is released once its outcome is written");
        (await ScalarAsync("select count(*) from ripple.splash where report @> '[{\"targetIds\":[\"ok\"]}]'"))
            .ShouldBe(4, "the healthy outcomes keep their own reports");

        // The unwritable outcome still settles as what it was — with a placeholder saying its report was lost.
        await using var conn = await Db.OpenConnectionAsync();
        var (outcome, report) = await conn.QuerySingleAsync<(string, string)>(
            "select outcome, report::text from ripple.splash where ripple_id = @id", new { id = poison.Id });
        outcome.ShouldBe("Succeeded");
        report.ShouldContain("could not be stored");
    }

    /// <summary>End to end: the same NUL arriving the ordinary way — in a handler's exception message — neither
    /// wedges the instance nor loses the message.</summary>
    [Fact]
    public async Task a_handler_failure_carrying_a_nul_still_settles_and_keeps_its_message()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 3);
        await SeedScheduleAsync(RippleTypeKey.Compose(nameof(RecalcContext), nameof(RecalcCompany)), 10, 1, maxAttempts: 1);

        var sink = new ExecutionSink { OnExecute = (_, _) => throw new InvalidOperationException("bad \0 byte") };
        using var host = BuildEngineHost(
            engine => engine.AddHandler<RecalcContext, RecalcCompany, RecalcHandler>(),
            services: services => services.AddSingleton(sink));
        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            Wave? final;
            while ((final = await Engine.GetWaveAsync(wave.Id))!.Status == WaveStatus.Active)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the wave never settled — settlement is wedged");
                await Task.Delay(100);
            }

            final.Status.ShouldBe(WaveStatus.Faulted);
            final.Failed.ShouldBe(3);
            (await ScalarAsync("select count(*) from ripple.splash where report::text like '%InvalidOperationException: bad%byte%'"))
                .ShouldBe(3, "the failure reason survives, minus the character the database cannot store");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
