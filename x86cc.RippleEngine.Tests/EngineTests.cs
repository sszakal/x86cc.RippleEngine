using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Hosting;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>End-to-end: a real host runs the dispatcher + TPL pipeline and drives ripples to completion.</summary>
public sealed class EngineTests : RippleTestBase
{
    [Fact]
    public async Task engine_executes_every_ripple_and_completes_the_wave()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        var ids = await SeedRipplesAsync(wave.Id, 12);

        var sink = new ExecutionSink();
        using var host = BuildHost(sink);
        await host.StartAsync();
        try
        {
            var final = await WaitForTerminalAsync(wave.Id, TimeSpan.FromSeconds(60));

            final.Status.ShouldBe(WaveStatus.Completed);
            final.Succeeded.ShouldBe(12);
            final.Pending.ShouldBe(0);
            final.Running.ShouldBe(0);

            sink.Executed.ToHashSet().SetEquals(ids).ShouldBeTrue();
            (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Succeeded'")).ShouldBe(12);
            // Each splash carries a per-target report (the single company, inferred succeeded).
            (await ScalarAsync("select count(*) from ripple.splash where report is not null")).ShouldBe(12);
            (await ScalarAsync(
                "select count(*) from ripple.splash where report @> '[{\"outcome\":\"Succeeded\"}]'")).ShouldBe(12);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task engine_retries_a_failing_ripple_until_it_succeeds()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        var ids = await SeedRipplesAsync(wave.Id, 3); // default retry ceiling (5) — enough for the flaky ripple's 3 attempts
        var flaky = ids[0];

        var sink = new ExecutionSink
        {
            // The flaky company throws on its first two attempts, then succeeds on the third.
            OnExecute = (ripple, attempt) => ripple.CompanyId == flaky && attempt < 3
                ? throw new InvalidOperationException("transient")
                : Task.CompletedTask
        };

        using var host = BuildHost(sink, o =>
        {
            o.RetryBackoff = TimeSpan.FromMilliseconds(50);
            o.MaxRetryBackoff = TimeSpan.FromMilliseconds(200);
        });
        await host.StartAsync();
        try
        {
            var final = await WaitForTerminalAsync(wave.Id, TimeSpan.FromSeconds(60));

            final.Status.ShouldBe(WaveStatus.Completed);
            final.Succeeded.ShouldBe(3);
            sink.Attempts[flaky].ShouldBeGreaterThanOrEqualTo(3);
            (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Failed'")).ShouldBeGreaterThanOrEqualTo(2);
            // A throw becomes an all-targets-failed report carrying the exception message, so the splash explains itself.
            (await ScalarAsync(
                "select count(*) from ripple.splash where outcome = 'Failed' and report::text like '%transient%'"))
                .ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task engine_retries_a_failed_settlement_and_loses_no_outcomes()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 5);

        var sink = new ExecutionSink();
        // The splash store throws on its first settlement call; the pipeline must retry, not drop the batch —
        // otherwise the executed ripples would be stuck Running and the wave would never complete.
        using var host = BuildHost(sink, splashStore: new FlakySplashStore(Splashes));
        await host.StartAsync();
        try
        {
            var final = await WaitForTerminalAsync(wave.Id, TimeSpan.FromSeconds(60));

            final.Status.ShouldBe(WaveStatus.Completed);
            final.Succeeded.ShouldBe(5);
            (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Succeeded'")).ShouldBe(5);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task wave_spans_multiple_ripple_types_and_a_group_ripple_expands_into_child_ripples()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync(legislation: "TAX2026");

        // One "legislation changed" wave, three kinds of target: 3 sole traders, 2 standalone companies, and
        // 2 company groups. Each group carries 4 member companies it will expand into at execution time.
        var soleTraders = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var directCompanies = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToList();
        var groups = Enumerable.Range(0, 2)
            .Select(_ => new CompanyGroupTax
            {
                GroupId = Guid.NewGuid(),
                MemberCompanyIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList()
            })
            .ToList();

        await AddTypedRipplesAsync(wave.Id, soleTraders.Select(id => new SoleTraderTax { TraderId = id }));
        await AddTypedRipplesAsync(wave.Id, directCompanies.Select(id => new CompanyTax { CompanyId = id }));
        await AddTypedRipplesAsync(wave.Id, groups);

        // The CompanyTax handler should ultimately run for the standalone companies AND every group's members.
        var expectedCompanies = directCompanies
            .Concat(groups.SelectMany(g => g.MemberCompanyIds))
            .ToHashSet();

        var sink = new HierarchySink();
        using var host = BuildHierarchyHost(sink);
        await host.StartAsync();
        try
        {
            var final = await WaitForTerminalAsync(wave.Id, TimeSpan.FromSeconds(60));

            final.Status.ShouldBe(WaveStatus.Completed);
            // 3 sole traders + 2 companies + 2 groups + (2 groups × 4 members expanded) = 15 ripples.
            final.RippleCount.ShouldBe(15);
            final.Succeeded.ShouldBe(15);
            final.Pending.ShouldBe(0);
            final.Running.ShouldBe(0);

            sink.SoleTraders.ToHashSet().SetEquals(soleTraders).ShouldBeTrue();
            sink.Groups.ToHashSet().SetEquals(groups.Select(g => g.GroupId)).ShouldBeTrue();
            sink.Companies.ToHashSet().SetEquals(expectedCompanies).ShouldBeTrue();

            // The 8 expanded children are stamped with their group ripple as parent (the audit lineage) — two
            // distinct parents, one per group.
            (await ScalarAsync("select count(*) from ripple.ripple where parent_ripple_id is not null"))
                .ShouldBe(8);
            (await ScalarAsync(
                "select count(distinct parent_ripple_id) from ripple.ripple where parent_ripple_id is not null"))
                .ShouldBe(2);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static readonly JsonSerializerOptions TypedJson = new(JsonSerializerDefaults.Web);

    /// <summary>Seeds ripples of an arbitrary payload type onto a wave (payload_type = <c>typeof(T).Name</c>).</summary>
    private Task AddTypedRipplesAsync<T>(Guid waveId, IEnumerable<T> items) where T : notnull
    {
        var seeds = items
            .Select(i => new RippleSeed(JsonSerializer.Serialize(i, TypedJson), typeof(T).Name))
            .ToList();
        return Engine.AddRipplesAsync(waveId, seeds);
    }

    private IHost BuildHierarchyHost(HierarchySink sink)
        => BuildEngineHost(
            engine => engine
                .AddHandler<RecalcContext, SoleTraderTax, SoleTraderTaxHandler>()
                .AddHandler<RecalcContext, CompanyTax, CompanyTaxHandler>()
                .AddHandler<RecalcContext, CompanyGroupTax, CompanyGroupTaxHandler>(),
            services: s => s.AddSingleton(sink));

    /// <summary>
    /// Shutting down must let in-flight ripples FINISH, not cancel them. Handlers used to be linked to the
    /// BackgroundService stopping token, which the host cancels BEFORE the dispatcher's finally reaches
    /// StopAsync() — so every executing ripple threw OperationCanceledException, was recorded as a failed
    /// attempt with `attempt` already spent, and at MaxAttempts was written TERMINALLY Failed. A rolling restart
    /// silently faulted work that had never actually failed. Handlers now run on a separate drain token that is
    /// only cancelled once ShutdownDrainGrace expires.
    /// </summary>
    [Fact]
    public async Task shutdown_drains_in_flight_ripples_instead_of_cancelling_them()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 4);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ExecutionSink
        {
            // Honour the context token: if shutdown cancelled handlers, this throws and the ripple fails.
            OnExecuteWithContext = async (_, _, context) =>
            {
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
            }
        };

        using var host = BuildHost(sink, o =>
        {
            o.ShutdownDrainGrace = TimeSpan.FromSeconds(20); // comfortably longer than the 2s of work
            o.WaveStatsRefreshInterval = TimeSpan.FromMinutes(10); // assert on rows, not on the refreshed wave
        });
        await host.StartAsync();

        // Stop while work is genuinely mid-flight.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.StopAsync(TimeSpan.FromSeconds(60));

        sink.CancelledCount.ShouldBe(0, "no handler should have observed cancellation during a graceful drain");

        // Everything that got claimed ran to completion and settled Succeeded; nothing was failed, and no
        // ripple burned an attempt it did not deserve.
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Failed'")).ShouldBe(0);
        var succeeded = await ScalarAsync("select count(*) from ripple.splash where outcome = 'Succeeded'");
        succeeded.ShouldBeGreaterThan(0, "at least the in-flight batch should have drained and settled");
        (await ScalarAsync(
            "select count(*) from ripple.ripple where wave_id = @waveId and state = 'Failed'",
            new { waveId = wave.Id })).ShouldBe(0);
        // Anything not executed is simply back in the queue for the next instance — never terminally failed.
        (await ScalarAsync(
            "select count(*) from ripple.ripple where wave_id = @waveId and state in ('Succeeded', 'Pending')",
            new { waveId = wave.Id })).ShouldBe(4);
    }

    /// <summary>
    /// A settlement that fails mid-shutdown must still be RETRIED, not dropped. The retry backoff used to wait
    /// on the host's stopping token, which is already cancelled by the time the drain starts — so the first
    /// failure during shutdown returned immediately with zero retries and discarded the whole batch, leaving
    /// ripples Running for recovery to time out on. That directly undercut keeping handlers alive to finish:
    /// exactly when a restart makes a transient DB error most likely, their outcomes were thrown away.
    /// The backoff now waits on the drain token, so retries persist for the full ShutdownDrainGrace.
    /// </summary>
    [Fact]
    public async Task settlement_retries_through_a_failure_during_shutdown_drain()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 2);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ExecutionSink
        {
            OnExecuteWithContext = async (_, _, context) =>
            {
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(500), context.CancellationToken);
            }
        };

        // FlakySplashStore throws on its FIRST CompleteRipplesAsync — which here lands during the drain.
        var real = Storage.GetRequiredService<ISplashStore>();
        using var host = BuildHost(sink, o =>
        {
            o.ShutdownDrainGrace = TimeSpan.FromSeconds(20);
            o.SettlementRetryDelay = TimeSpan.FromMilliseconds(50);
            o.WaveStatsRefreshInterval = TimeSpan.FromMinutes(10);
        }, new FlakySplashStore(real));

        await host.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.StopAsync(TimeSpan.FromSeconds(60));

        // The first settlement threw; the retry must have landed it rather than dropping the outcomes.
        (await ScalarAsync("select count(*) from ripple.splash where outcome = 'Succeeded'"))
            .ShouldBeGreaterThan(0, "the failed settlement must have been retried during the drain, not dropped");
        (await ScalarAsync(
            "select count(*) from ripple.ripple where wave_id = @waveId and state = 'Running'",
            new { waveId = wave.Id }))
            .ShouldBe(0, "no executed ripple should be left stranded Running by a dropped settlement");
    }

    /// <summary>
    /// If shutdown gives up with work still Running, the heartbeat row must SURVIVE. It is the only handle
    /// recovery has: RecoverStaleAsync reclaims ripples whose claimed_by appears in instance_heartbeat past the
    /// timeout, and there is no owner-agnostic time reaper. Deregistering unconditionally deleted that row, so
    /// ripples abandoned when the drain grace expired stayed Running forever — a restart gets a fresh
    /// InstanceId, so self-recovery could not find them either — and their wave never completed or compacted.
    /// </summary>
    [Fact]
    public async Task shutdown_that_strands_work_keeps_its_heartbeat_so_recovery_can_reclaim_it()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 2);

        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ExecutionSink
        {
            OnExecuteWithContext = (_, _, _) =>
            {
                executed.TrySetResult();
                return Task.CompletedTask;
            }
        };

        // Handlers run fine; SETTLEMENT is what fails. Retries are bounded by the drain grace, so shutdown gives
        // up with the ripple executed but its row still Running — the real stranding path (a cancelled handler
        // settles normally as a failed attempt, so it strands nothing).
        const string instanceId = "inst-stranding";
        using var host = BuildHost(sink, o =>
        {
            o.InstanceId = instanceId;
            o.ShutdownDrainGrace = TimeSpan.FromMilliseconds(500);
            o.SettlementRetryDelay = TimeSpan.FromMilliseconds(50);
            o.WaveStatsRefreshInterval = TimeSpan.FromMinutes(10);
        }, new AlwaysFailingSplashStore());

        await host.StartAsync();
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.StopAsync(TimeSpan.FromSeconds(60));

        (await ScalarAsync("select count(*) from ripple.ripple where claimed_by = @instanceId and state = 'Running'",
            new { instanceId })).ShouldBeGreaterThan(0, "the scenario requires work genuinely left Running");

        (await ScalarAsync("select count(*) from ripple.instance_heartbeat where instance_id = @instanceId",
            new { instanceId }))
            .ShouldBe(1, "the row must remain so RecoverStaleAsync can reclaim what shutdown abandoned");

        // And recovery genuinely reclaims it once the heartbeat is stale.
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = @instanceId",
            new { instanceId });
        await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), "inst-other");

        (await ScalarAsync(
            "select count(*) from ripple.ripple where wave_id = @waveId and state = 'Running'",
            new { waveId = wave.Id }))
            .ShouldBe(0, "the abandoned ripple must be reclaimed, not left Running forever");
    }

    private IHost BuildHost(ExecutionSink sink, Action<RippleSetupOptions>? configure = null,
        ISplashStore? splashStore = null)
        => BuildEngineHost(
            engine => engine.AddHandler<RecalcContext, RecalcCompany, RecalcHandler>(),
            configure,
            services =>
            {
                services.AddSingleton(sink);
                if (splashStore is not null)
                {
                    services.AddSingleton(splashStore);
                }
            });

    private async Task<Wave> WaitForTerminalAsync(Guid waveId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var wave = await Engine.GetWaveAsync(waveId);
            if (wave is { Status: WaveStatus.Completed or WaveStatus.Faulted })
            {
                return wave;
            }

            await Task.Delay(100);
        }

        var last = await Engine.GetWaveAsync(waveId);
        await using var conn = await Db.OpenConnectionAsync();
        var ripples = string.Join(" | ", await conn.QueryAsync<string>(
            "select 'state=' || state || ' attempt=' || attempt || ' claimed_by=' || coalesce(claimed_by,'null') " +
            "|| ' next_attempt_at=' || coalesce(next_attempt_at::text,'null') || ' schedule_order=' || schedule_order::text " +
            "|| ' now=' || now()::text " +
            "from ripple.ripple where wave_id = @waveId order by schedule_order", new { waveId }));
        throw new TimeoutException(
            $"Wave never reached a terminal state within {timeout}. Last: status={last?.Status}, " +
            $"pending={last?.Pending}, running={last?.Running}, succeeded={last?.Succeeded}, failed={last?.Failed}\n" +
            $"RIPPLES: {ripples}");
    }
}
