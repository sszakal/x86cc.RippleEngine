using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>What the engine reports about itself: its traces, the queue sample, and the intervention counters.</summary>
public sealed class TelemetryTests : RippleTestBase
{
    /// <summary>
    /// The trace of a wave: creating it is one span under the caller's own, and every execution — on a worker,
    /// in another process, possibly hours later — is its own root span LINKED back to that creation, with the
    /// in-flight expansion nested under the execution that made it.
    /// </summary>
    [Fact]
    public async Task executions_are_root_spans_linked_to_the_trace_that_created_their_wave()
    {
        await ResetAsync();
        var spans = new ConcurrentBag<Activity>();
        using var caller = new ActivitySource("ripple-tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is RippleDiagnostics.ActivitySourceName or "ripple-tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);

        Wave wave;
        var members = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToList();
        using (var request = caller.StartActivity("POST /tax-change"))
        {
            wave = await CollectionGenerator
                .Create("group recalc", new RecalcContext { LegislationCode = "VAT26" })
                .AddRipple(new CompanyGroupTax { GroupId = Guid.NewGuid(), MemberCompanyIds = members })
                .DispatchAsync();
        }

        using var host = BuildEngineHost(
            engine => engine
                .AddHandler<RecalcContext, CompanyGroupTax, CompanyGroupTaxHandler>()
                .AddHandler<RecalcContext, CompanyTax, CompanyTaxHandler>(),
            services: services => services.AddSingleton(new HierarchySink()));
        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            while ((await Engine.GetWaveAsync(wave.Id))!.Status == WaveStatus.Active)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the wave never settled");
                await Task.Delay(100);
            }
        }
        finally
        {
            await host.StopAsync();
        }

        var mine = spans.Where(s => Equals(s.GetTagItem("ripple.wave.id"), wave.Id)).ToList();

        var create = mine.Single(s => s.OperationName == "ripple.wave.create");
        create.Kind.ShouldBe(ActivityKind.Producer);
        create.Parent.ShouldNotBeNull().OperationName.ShouldBe("POST /tax-change");
        create.GetTagItem("ripple.count").ShouldBe(1);

        var executions = mine.Where(s => s.OperationName == "ripple.execute").ToList();
        executions.Count.ShouldBe(3); // the group + its two members
        foreach (var execution in executions)
        {
            execution.Kind.ShouldBe(ActivityKind.Consumer);
            execution.ParentSpanId.ShouldBe(default, "an execution is a root span, not a child of the fan-out");
            execution.TraceId.ShouldNotBe(create.TraceId);
            var link = execution.Links.ShouldHaveSingleItem();
            link.Context.TraceId.ShouldBe(create.TraceId);
            link.Context.SpanId.ShouldBe(create.SpanId);
            execution.GetTagItem("ripple.outcome").ShouldBe("succeeded");
            execution.GetTagItem("ripple.attempt").ShouldBe(1);
        }

        // The group's Continue is a span inside the group's execution.
        var group = executions.Single(s => Equals(s.GetTagItem("ripple.type_key"), "RecalcContext|CompanyGroupTax"));
        var expand = mine.Single(s => s.OperationName == "ripple.wave.expand");
        expand.ParentSpanId.ShouldBe(group.SpanId);
        expand.TraceId.ShouldBe(group.TraceId);
        expand.GetTagItem("ripple.count").ShouldBe(2);
    }

    /// <summary>A failed attempt's span is marked as an error and says why.</summary>
    [Fact]
    public async Task a_failed_execution_is_an_error_span_carrying_the_reason()
    {
        await ResetAsync();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RippleDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);

        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);
        await SeedScheduleAsync("RecalcContext|RecalcCompany", 10, 1, maxAttempts: 1);
        var sink = new ExecutionSink { OnExecute = (_, _) => throw new InvalidOperationException("rate table missing") };
        using var host = BuildEngineHost(
            engine => engine.AddHandler<RecalcContext, RecalcCompany, RecalcHandler>(),
            services: services => services.AddSingleton(sink));
        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            while ((await Engine.GetWaveAsync(wave.Id))!.Status == WaveStatus.Active)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the wave never settled");
                await Task.Delay(100);
            }
        }
        finally
        {
            await host.StopAsync();
        }

        var execution = spans.Single(s => s.OperationName == "ripple.execute" && Equals(s.GetTagItem("ripple.wave.id"), wave.Id));
        execution.Status.ShouldBe(ActivityStatusCode.Error);
        execution.StatusDescription.ShouldNotBeNull().ShouldContain("rate table missing");
        execution.GetTagItem("ripple.outcome").ShouldBe("failed");
        execution.Links.ShouldBeEmpty("this wave was created outside any trace, so there is nothing to link to");
    }

    /// <summary>The queue sample: depth is the pending work across active waves, and head wait is the age of the
    /// ripple the next claim would take — zero once nothing is claimable.</summary>
    [Fact]
    public async Task the_queue_snapshot_reports_depth_and_how_long_the_head_has_waited()
    {
        await ResetAsync();
        (await Engine.GetQueueSnapshotAsync()).ShouldBe(new QueueSnapshot(0, 0));

        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 5);
        await ExecuteAsync("update ripple.ripple set created_at = now() - interval '90 seconds'");
        await RefreshWaveStatsAsync();

        var waiting = await Engine.GetQueueSnapshotAsync();
        waiting.Depth.ShouldBe(5);
        waiting.HeadWaitSeconds.ShouldBe(90, tolerance: 5);

        // Everything claimed: nothing left to wait at the head, and (after a refresh) nothing pending.
        (await Engine.PollAsync(10, "inst-1", 0)).Count.ShouldBe(5);
        await RefreshWaveStatsAsync();
        (await Engine.GetQueueSnapshotAsync()).ShouldBe(new QueueSnapshot(0, 0));
    }

    /// <summary>The interventions that used to be visible only in logs are counted, and the queue gauges report
    /// the last sample (and nothing before there is one).</summary>
    [Fact]
    public void interventions_are_counted_and_the_queue_gauges_report_the_last_sample()
    {
        using var metrics = new RippleMetrics();
        var seen = new ConcurrentDictionary<string, double>();
        var tags = new ConcurrentDictionary<string, string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, MeterOf(metrics)))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, value, t, _) => Record(i, value, t));
        listener.SetMeasurementEventCallback<double>((i, value, t, _) => Record(i, value, t));
        listener.Start();

        metrics.Recovered(3, "dead_instance");
        metrics.Recovered(0, "self_stranded"); // nothing reclaimed is not a measurement
        metrics.SettlementRetried();
        metrics.SettlementRetried();
        metrics.SettlementRejected();
        metrics.Released("W|R");
        metrics.Polled(12.5);

        seen["ripple.recovered"].ShouldBe(3);
        tags["ripple.recovered"].ShouldBe("dead_instance");
        seen["ripple.settlement.retries"].ShouldBe(2);
        seen["ripple.settlement.rejected"].ShouldBe(1);
        seen["ripple.released"].ShouldBe(1);
        seen["ripple.poll.duration"].ShouldBe(12.5);

        metrics.QueueObserved.ShouldBeTrue();
        listener.RecordObservableInstruments();
        seen.ContainsKey("ripple.queue.depth").ShouldBeFalse("no sample yet, so nothing to report");

        metrics.SetQueue(new QueueSnapshot(1200, 4.5));
        listener.RecordObservableInstruments();
        seen["ripple.queue.depth"].ShouldBe(1200);
        seen["ripple.queue.head_wait"].ShouldBe(4.5);

        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> measurementTags)
        {
            // Counters accumulate; gauges and the histogram keep the last value.
            seen.AddOrUpdate(instrument.Name, value, (_, total) => instrument is Counter<long> ? total + value : value);
            if (measurementTags.Length > 0)
            {
                tags[instrument.Name] = measurementTags[0].Value?.ToString();
            }
        }
    }

    // RippleMetrics owns a private Meter; every instance uses the same name, so tests that run in one process
    // tell their own apart by instance rather than by name.
    private static Meter MeterOf(RippleMetrics metrics)
        => (Meter)typeof(RippleMetrics)
            .GetField("_meter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(metrics)!;
}
