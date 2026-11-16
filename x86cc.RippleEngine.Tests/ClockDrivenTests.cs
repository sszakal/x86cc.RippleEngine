using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The engine's time-driven behaviour, checked against a <see cref="FakeTimeProvider"/> instead of the wall clock:
/// the execution timeout, the retry backoff stamp, the recorded duration, the shutdown drain grace, the settlement
/// write retry and the loop cadence. Components are built directly with an in-memory splash store — no host, no Postgres — so time only
/// moves when the test says so and every timestamp is asserted EXACTLY. The real waits left here only let async
/// continuations run; nothing asserts on how long they took.
/// </summary>
/// <remarks>
/// A fake clock is a component-level tool, not a host-level one: a whole engine host on a fake clock would stall
/// (the dispatcher's poll backoff and every loop wait on time nobody advances), and the timestamps it writes are
/// compared with Postgres <c>now()</c>, which no fake clock moves.
/// </remarks>
public sealed class ClockDrivenTests
{
    private static readonly string TypeKey = RippleTypeKey.Compose(nameof(ClockWave), nameof(ClockRipple));

    private readonly CountingTimeProvider _time = new();
    private readonly ClockProbe _probe = new();
    private readonly RecordingSplashStore _store = new();

    [Fact]
    public async Task the_execution_timeout_cancels_the_handler_exactly_when_the_clock_reaches_it()
    {
        var pipeline = BuildPipeline(o => o.ExecutionTimeout = TimeSpan.FromSeconds(30));
        _probe.Behaviour = async (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return null;
        };

        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        var context = await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startedAt = _time.GetUtcNow();

        // Timers fire synchronously inside Advance, so the token state is exact on both sides of the deadline.
        _time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        _time.Advance(TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeTrue();

        var failure = await SingleAsync(_store.Failed);
        failure.StartedAt.ShouldBe(startedAt);
        failure.EndedAt.ShouldBe(startedAt + TimeSpan.FromSeconds(30));
        failure.Terminal.ShouldBeFalse();
        failure.Report!.ShouldContain("TaskCanceledException");

        await StopAsync(pipeline);
    }

    /// <summary>A handler that IGNORES its cancelled token — awaiting something that never completes, or blocking
    /// its thread outright — is waited for only HandlerCancellationGrace longer. Then the attempt fails as
    /// abandoned and its slot is free for the next ripple, instead of the ripple staying Running forever under a
    /// live instance that no recovery path will ever touch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_handler_that_ignores_cancellation_is_abandoned_after_the_grace_and_frees_its_slot(bool blocksItsThread)
    {
        var pipeline = BuildPipeline(o =>
        {
            o.ExecutionTimeout = TimeSpan.FromSeconds(30);
            o.HandlerCancellationGrace = TimeSpan.FromSeconds(5);
        });
        using var release = new ManualResetEventSlim();
        var never = new TaskCompletionSource<SplashReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.Behaviour = (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            if (blocksItsThread)
            {
                release.Wait(); // never looks at ctx.CancellationToken
                return Task.FromResult<SplashReport?>(null);
            }

            return never.Task;
        };

        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        var context = await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startedAt = _time.GetUtcNow();
        var timers = _time.TimersCreated;

        _time.Advance(TimeSpan.FromSeconds(30));
        context.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        await WaitUntilAsync(() => _time.TimersCreated > timers); // the grace is armed

        _time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        _store.Failed.ShouldBeEmpty("the handler gets its full grace to honour the cancellation");
        _time.Advance(TimeSpan.FromTicks(1));

        var failure = await SingleAsync(_store.Failed);
        failure.EndedAt.ShouldBe(startedAt + TimeSpan.FromSeconds(35));
        failure.Terminal.ShouldBeFalse("abandonment is an ordinary failed attempt: it retries");
        failure.Report.ShouldNotBeNull().ShouldContain("abandoned");
        await WaitUntilAsync(() => pipeline.ExecutingCount == 0);

        // MaxConcurrency is 1 and the first handler is STILL hung: the next ripple runs only if its slot was freed.
        _probe.Behaviour = (_, _) => Task.FromResult<SplashReport?>(null);
        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        await SingleAsync(_store.Completed);

        release.Set();
        never.SetResult(null);
        await StopAsync(pipeline);
    }

    /// <summary>The retry deadline is <c>endedAt + RetryBackoff · 2^(attempt-1)</c>, capped at MaxRetryBackoff, and
    /// the last allowed attempt is terminal with no retry.</summary>
    [Theory]
    [InlineData(1, 2)]  // 2s base
    [InlineData(2, 4)]  // doubled
    [InlineData(3, 5)]  // 8s, capped at 5s
    public async Task a_failed_attempt_is_requeued_at_exactly_its_backoff(int attempt, int expectedBackoffSeconds)
    {
        var pipeline = BuildPipeline(o =>
        {
            o.RetryBackoff = TimeSpan.FromSeconds(2);
            o.MaxRetryBackoff = TimeSpan.FromSeconds(5);
        });
        _probe.Behaviour = (_, _) => throw new InvalidOperationException("boom");

        pipeline.Post(Ripple(attempt, maxAttempts: 5));

        var failure = await SingleAsync(_store.Failed);
        failure.EndedAt.ShouldBe(_time.GetUtcNow()); // stamped from the injected clock, not the wall clock
        failure.Terminal.ShouldBeFalse();
        failure.NextAttemptAt.ShouldBe(failure.EndedAt + TimeSpan.FromSeconds(expectedBackoffSeconds));

        await StopAsync(pipeline);
    }

    [Fact]
    public async Task the_last_allowed_attempt_fails_terminally_with_no_retry()
    {
        var pipeline = BuildPipeline();
        _probe.Behaviour = (_, _) => throw new InvalidOperationException("boom");

        pipeline.Post(Ripple(attempt: 5, maxAttempts: 5));

        var failure = await SingleAsync(_store.Failed);
        failure.Terminal.ShouldBeTrue();
        failure.NextAttemptAt.ShouldBeNull();

        await StopAsync(pipeline);
    }

    /// <summary>The recorded duration is the handler's own time — 42 fake seconds, exactly, in no real time.</summary>
    [Fact]
    public async Task the_recorded_duration_is_exactly_the_handlers_time()
    {
        var pipeline = BuildPipeline();
        _probe.Behaviour = async (ctx, time) =>
        {
            var work = Task.Delay(TimeSpan.FromSeconds(42), time, ctx.CancellationToken);
            _probe.Started.TrySetResult(ctx); // signalled after the timer is registered, so Advance can't miss it
            await work;
            return null;
        };

        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(42));

        var completion = await SingleAsync(_store.Completed);
        (completion.EndedAt - completion.StartedAt).ShouldBe(TimeSpan.FromSeconds(42));

        await StopAsync(pipeline);
    }

    /// <summary>Shutdown waits for an in-flight handler until ShutdownDrainGrace — not a tick sooner — then cancels it
    /// and still settles its outcome.</summary>
    [Fact]
    public async Task shutdown_drains_until_the_grace_expires_then_cancels_and_settles()
    {
        var pipeline = BuildPipeline(o => o.ShutdownDrainGrace = TimeSpan.FromSeconds(10));
        _probe.Behaviour = async (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return null;
        };

        pipeline.Post(Ripple(attempt: 3, maxAttempts: 3)); // its LAST attempt
        var context = await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = pipeline.StopAsync(); // arms the drain deadline synchronously

        _time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeFalse("the handler must get the full drain grace");
        stop.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        _store.Failed.Count.ShouldBe(1, "the cancelled attempt's outcome is still written");

        // ...as a RELEASE, not a failure: shutdown stopped it, so even on its last attempt it is requeued with the
        // attempt handed back instead of being failed terminally for a restart it had nothing to do with.
        var outcome = _store.Failed.Single();
        outcome.Released.ShouldBeTrue();
        outcome.Terminal.ShouldBeFalse();
        outcome.Report.ShouldNotBeNull().ShouldContain("Abandoned");
    }

    /// <summary>Release is only for work the SHUTDOWN stopped. A handler that had already overrun its own
    /// ExecutionTimeout when the drain ran out failed on its own account and is settled as an ordinary failure.</summary>
    [Fact]
    public async Task a_handler_that_overran_its_own_timeout_is_not_released_by_a_shutdown()
    {
        var pipeline = BuildPipeline(o =>
        {
            o.ExecutionTimeout = TimeSpan.FromSeconds(5);
            o.ShutdownDrainGrace = TimeSpan.FromSeconds(10);
            o.HandlerCancellationGrace = TimeSpan.FromSeconds(60);
        });
        using var letGo = new ManualResetEventSlim();
        _probe.Behaviour = (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            letGo.Wait(); // ignores its token, so it is still running when the drain runs out
            ctx.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SplashReport?>(null);
        };

        pipeline.Post(Ripple(attempt: 3, maxAttempts: 3));
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(5)); // its own deadline passes first...
        var stop = pipeline.StopAsync();
        _time.Advance(TimeSpan.FromSeconds(10)); // ...then the drain runs out
        letGo.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        var outcome = _store.Failed.Single();
        outcome.Released.ShouldBeFalse();
        outcome.Terminal.ShouldBeTrue();
    }

    /// <summary>A failed settlement write is retried on the clock — 250ms, then doubled — and the attempt keeps its
    /// in-flight slot (the dispatcher's back-pressure) until the write actually lands.</summary>
    [Fact]
    public async Task a_failed_settlement_retries_with_doubling_delay_and_holds_the_slot_until_it_lands()
    {
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", SettlementRetryDelay = TimeSpan.FromMilliseconds(250)
        });
        var inFlight = new InFlightTracker();
        var settlement = BuildSettlement(options, inFlight);
        using var drain = new CancellationTokenSource();
        settlement.Start(drain.Token);
        _store.FailNextWrites = 2;

        var rippleId = Guid.NewGuid();
        inFlight.Track(rippleId, attempt: 1);
        settlement.Succeeded(new RippleCompletion(rippleId, Guid.NewGuid(), 1, _time.GetUtcNow(), null));

        await WaitUntilAsync(() => _time.TimersCreated == 1); // first write failed, backing off 250ms
        _time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => _time.TimersCreated == 2); // second write failed, backing off 500ms
        _time.Advance(TimeSpan.FromMilliseconds(500) - TimeSpan.FromTicks(1));
        _store.Completed.ShouldBeEmpty();
        inFlight.Count.ShouldBe(1, "an unwritten outcome keeps its slot");

        _time.Advance(TimeSpan.FromTicks(1));
        (await SingleAsync(_store.Completed)).RippleId.ShouldBe(rippleId);
        await WaitUntilAsync(() => inFlight.Count == 0);
        inFlight.RippleIds.ShouldBeEmpty();

        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A batch the database REJECTS is not retried as it stands — that fails the same way forever. It is
    /// split until the offending outcome is alone; every other outcome lands with no backoff at all, and the
    /// offender is settled without its report.</summary>
    [Fact]
    public async Task a_rejected_batch_is_split_and_only_the_offending_outcome_loses_its_report()
    {
        var options = Options.Create(new RippleEngineOptions { InstanceId = "clock-test" });
        var inFlight = new InFlightTracker();
        var settlement = BuildSettlement(options, inFlight);
        using var drain = new CancellationTokenSource();
        _store.Rejects = report => report == "poison";

        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            inFlight.Track(id, attempt: 1);
            settlement.Succeeded(new RippleCompletion(id, Guid.NewGuid(), 1, _time.GetUtcNow(),
                id == ids[4] ? "poison" : "fine"));
        }

        settlement.Start(drain.Token); // started after queueing, so all seven are read as ONE batch
        await WaitUntilAsync(() => _store.Completed.Count == 7);
        await WaitUntilAsync(() => inFlight.Count == 0);

        _time.TimersCreated.ShouldBe(0, "a rejection is isolated, never backed off on");
        _store.Completed.Count(c => c.Report == "fine").ShouldBe(6);
        var offender = _store.Completed.Single(c => c.RippleId == ids[4]);
        var placeholder = offender.Report.ShouldNotBeNull();
        placeholder.ShouldContain("report could not be stored");
        placeholder.ShouldContain("Succeeded");

        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>If the row is refused even without its report, the report was never the cause — so it is not
    /// dropped again or split further: it falls back to the ordinary retry, slot held.</summary>
    [Fact]
    public async Task an_outcome_rejected_even_without_its_report_is_retried_not_dropped()
    {
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", SettlementRetryDelay = TimeSpan.FromMilliseconds(250)
        });
        var inFlight = new InFlightTracker();
        var settlement = BuildSettlement(options, inFlight);
        using var drain = new CancellationTokenSource();
        settlement.Start(drain.Token);
        _store.Rejects = _ => true;

        var rippleId = Guid.NewGuid();
        inFlight.Track(rippleId, attempt: 1);
        settlement.Failed(new RippleFailure(rippleId, Guid.NewGuid(), 1, _time.GetUtcNow(), "any", true, null));

        await WaitUntilAsync(() => _time.TimersCreated == 1); // rejected with and without its report: backing off
        _store.Failed.ShouldBeEmpty();
        inFlight.Count.ShouldBe(1, "an unwritten outcome keeps its slot");

        _store.Rejects = null; // whatever was wrong is fixed; the next retry lands
        _time.Advance(TimeSpan.FromMilliseconds(250));
        (await SingleAsync(_store.Failed)).Report!.ShouldContain("report could not be stored");
        await WaitUntilAsync(() => inFlight.Count == 0);

        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Past the drain deadline a failing write stops retrying, and the attempt stays owned — recovery, not
    /// a dropped outcome, is what settles it.</summary>
    [Fact]
    public async Task the_drain_deadline_stops_settlement_retries_and_leaves_the_attempt_owned()
    {
        var options = Options.Create(new RippleEngineOptions { InstanceId = "clock-test" });
        var inFlight = new InFlightTracker();
        var settlement = BuildSettlement(options, inFlight);
        using var drain = new CancellationTokenSource();
        settlement.Start(drain.Token);
        _store.FailNextWrites = int.MaxValue;

        var rippleId = Guid.NewGuid();
        inFlight.Track(rippleId, attempt: 1);
        settlement.Failed(new RippleFailure(rippleId, Guid.NewGuid(), 1, _time.GetUtcNow(), null, true, null));
        await WaitUntilAsync(() => _time.TimersCreated == 1);

        var complete = settlement.CompleteAsync();
        complete.IsCompleted.ShouldBeFalse("still retrying inside the drain grace");
        await drain.CancelAsync();
        await complete.WaitAsync(TimeSpan.FromSeconds(10));

        inFlight.Count.ShouldBe(1);
        inFlight.RippleIds.ShouldBe([rippleId]);
    }

    /// <summary>A ripple whose type_key this instance has no handler for (the claim filters on handled types, so
    /// only a registry/claim mismatch gets one here) fails its ATTEMPT — retried like any failure, the reason in the
    /// report — rather than throwing out of the block action, which would fault the whole execute block.</summary>
    [Fact]
    public async Task a_ripple_with_no_registered_handler_fails_its_attempt_not_the_block()
    {
        var pipeline = BuildPipeline();
        const string unknown = "Nope|Nothing";

        pipeline.Post(new PreparedRipple(Guid.NewGuid(), Guid.NewGuid(), 1, 3, unknown, "Nothing",
            JsonDocument.Parse("{}"), JsonDocument.Parse("{}"))).ShouldBeTrue();

        var failure = await SingleAsync(_store.Failed);
        failure.Terminal.ShouldBeFalse("an ordinary failed attempt, retried under the policy");
        using (var report = JsonDocument.Parse(failure.Report.ShouldNotBeNull()))
        {
            var item = report.RootElement.EnumerateArray().ShouldHaveSingleItem();
            item.GetProperty("outcome").GetString().ShouldBe("Failed");
            item.GetProperty("output").GetString().ShouldNotBeNull().ShouldContain($"No handler registered for type key '{unknown}'");
        }

        pipeline.IsFaulted.ShouldBeFalse();

        // The block is still serving: a known ripple runs to success afterwards.
        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3)).ShouldBeTrue();
        await SingleAsync(_store.Completed);

        await StopAsync(pipeline);
    }

    /// <summary>The pipeline is one-shot. A second Start used to replace the execute block (orphaning whatever was
    /// queued in the first) and start a second pair of flush loops; use before Start was a NullReferenceException.</summary>
    [Fact]
    public async Task the_pipeline_starts_once_stops_once_and_refuses_work_before_start()
    {
        var unstarted = BuildPipeline(start: false);
        Should.Throw<InvalidOperationException>(() => unstarted.Post(Ripple(attempt: 1, maxAttempts: 3)))
            .Message.ShouldContain("must be started");
        (await Should.ThrowAsync<InvalidOperationException>(unstarted.StopAsync)).Message.ShouldContain("must be started");
        unstarted.IsFaulted.ShouldBeFalse();

        var pipeline = BuildPipeline();
        Should.Throw<InvalidOperationException>(pipeline.Start).Message.ShouldContain("already been started");

        await StopAsync(pipeline);
        (await Should.ThrowAsync<InvalidOperationException>(pipeline.StopAsync)).Message.ShouldContain("already been stopped");
    }

    [Fact]
    public async Task settlement_starts_once_and_cannot_complete_unstarted()
    {
        var options = Options.Create(new RippleEngineOptions { InstanceId = "clock-test" });

        // Completing without loops would return as if every buffered outcome had been written.
        var unstarted = BuildSettlement(options, new InFlightTracker());
        await Should.ThrowAsync<InvalidOperationException>(unstarted.CompleteAsync);

        var settlement = BuildSettlement(options, new InFlightTracker());
        settlement.Start(CancellationToken.None);
        Should.Throw<InvalidOperationException>(() => settlement.Start(CancellationToken.None));
        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A failed poll cycle (the DB down) retries after exactly <see cref="RippleEngineOptions.DispatchErrorDelay"/>,
    /// with the fallback heartbeat attempted in between.</summary>
    [Fact]
    public async Task a_failed_dispatch_cycle_retries_after_the_configured_error_delay()
    {
        var delay = TimeSpan.FromSeconds(7);
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", MaxConcurrency = 1, DispatchErrorDelay = delay
        });
        var down = ScriptedEngineStore.Create(_time, _ => throw new InvalidOperationException("database is down"));
        using var dispatcher = BuildDispatcher(options, down);

        await dispatcher.StartAsync(CancellationToken.None);

        // The poll failed and the fallback beat was tried; wait until the error delay is actually armed.
        await WaitUntilAsync(() => down.Calls("BeatAsync") == 1);
        await WaitUntilAsync(() => _time.TimersCreated > down.TimersAtLastCall);
        down.Calls("PollAsync").ShouldBe(1);

        _time.Advance(delay - TimeSpan.FromTicks(1));
        down.Calls("PollAsync").ShouldBe(1, "no retry before the configured delay");
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => down.Calls("PollAsync") == 2);

        await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>The heartbeat rides on the poll, so a poll that hangs must not take the beat down with it: it is
    /// abandoned at exactly <c>StoreCallDeadline</c> and the fallback beat goes out — long before HeartbeatTimeout,
    /// where the provider's own 30s command timeout would have left the instance declared dead.</summary>
    [Fact]
    public async Task a_hung_poll_is_abandoned_at_its_deadline_and_the_fallback_beat_goes_out()
    {
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", MaxConcurrency = 1,
            HeartbeatInterval = TimeSpan.FromSeconds(3), HeartbeatTimeout = TimeSpan.FromSeconds(15)
        });
        var deadline = TimeSpan.FromSeconds(3); // (15 - 3) / 4
        options.Value.StoreCallDeadline.ShouldBe(deadline);

        var hung = ScriptedEngineStore.Create(_time, (method, token) => method == "PollAsync"
            ? HangAsync(token)
            : Task.CompletedTask);
        using var dispatcher = BuildDispatcher(options, hung);

        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => hung.Calls("PollAsync") == 1);

        _time.Advance(deadline - TimeSpan.FromTicks(1));
        hung.Calls("BeatAsync").ShouldBe(0, "the poll is given its full deadline");
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => hung.Calls("BeatAsync") == 1);
        hung.Calls("PollAsync").ShouldBe(1);

        await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        static async Task<IReadOnlyList<ClaimedRipple>> HangAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }
    }

    /// <summary>Shutdown stops the poll loop, but the instance still owns the work it is draining — so it keeps
    /// beating, every HeartbeatInterval, until the drain ends. Silent, it would be declared dead mid-drain and its
    /// still-running ripples requeued for a second execution.</summary>
    [Fact]
    public async Task the_heartbeat_continues_through_the_shutdown_drain()
    {
        var interval = TimeSpan.FromSeconds(3);
        void Configure(RippleEngineOptions o)
        {
            o.HeartbeatInterval = interval;
            o.ShutdownDrainGrace = TimeSpan.FromHours(1);
            o.ExecutionTimeout = TimeSpan.FromHours(2);
        }

        var options = new RippleEngineOptions { InstanceId = "clock-test", MaxConcurrency = 1 };
        Configure(options);

        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.Behaviour = async (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            await finish.Task;
            return null;
        };

        var claimed = 0;
        var store = ScriptedEngineStore.Create(_time, (method, _) => method switch
        {
            "PollAsync" => Task.FromResult<IReadOnlyList<ClaimedRipple>>(Interlocked.Increment(ref claimed) == 1
                ? [new ClaimedRipple(Guid.NewGuid(), Guid.NewGuid(), 1, 3, TypeKey, nameof(ClockRipple),
                    JsonDocument.Parse($$"""{"id":"{{Guid.NewGuid()}}"}"""), null, JsonDocument.Parse("{}"))]
                : []),
            "RemoveInstanceAsync" => Task.FromResult(true),
            _ => Task.CompletedTask
        });
        using var dispatcher = BuildDispatcher(Options.Create(options), store, BuildPipeline(Configure, start: false));

        await dispatcher.StartAsync(CancellationToken.None);
        var context = await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = dispatcher.StopAsync(CancellationToken.None);
        store.Calls("BeatAsync").ShouldBe(0, "while polling, the beat rides on the poll");

        // Nudge the clock forward until two drain beats have gone out (nudging, not one jump: the drain arms its
        // delay on another thread, and a jump that landed first would be missed).
        var drainStarted = _time.GetUtcNow();
        await WaitUntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            return store.Calls("BeatAsync") >= 2;
        });
        (_time.GetUtcNow() - drainStarted).ShouldBeGreaterThanOrEqualTo(2 * interval, "one beat per interval, not a burst");
        context.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        stop.IsCompleted.ShouldBeFalse("the drain is still waiting on the handler");

        finish.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        (await SingleAsync(_store.Completed)).Attempt.ShouldBe(1);
    }

    /// <summary>An empty queue backs the poll cadence off — doubling from MinPollDelay up to MaxPollDelay — so an
    /// idle cluster stops hammering the DB. Each step is checked to the tick.</summary>
    [Fact]
    public async Task empty_polls_double_the_poll_delay_up_to_the_ceiling()
    {
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", MaxConcurrency = 1,
            MinPollDelay = TimeSpan.FromMilliseconds(100), MaxPollDelay = TimeSpan.FromMilliseconds(1000)
        });
        var idle = ScriptedEngineStore.Create(_time, method => method == "PollAsync"
            ? Task.FromResult<IReadOnlyList<ClaimedRipple>>([])
            : Task.CompletedTask);
        using var dispatcher = BuildDispatcher(options, idle);

        await dispatcher.StartAsync(CancellationToken.None);

        var polls = 1;
        foreach (var ms in new[] { 200, 400, 800, 1000, 1000 })
        {
            await WaitUntilAsync(() => idle.Calls("PollAsync") == polls);
            await WaitUntilAsync(() => _time.TimersCreated > idle.TimersAtLastCall); // the idle delay is armed

            _time.Advance(TimeSpan.FromMilliseconds(ms) - TimeSpan.FromTicks(1));
            idle.Calls("PollAsync").ShouldBe(polls, $"no poll before the {ms}ms idle delay");
            _time.Advance(TimeSpan.FromTicks(1));
            polls++;
        }

        await WaitUntilAsync(() => idle.Calls("PollAsync") == polls);
        await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A faulted execute block can never run anything again, but the process is still alive and
    /// heartbeating — so nothing would ever recover the work it holds. The dispatcher must fail fast: release this
    /// instance's claims (keeping none, with no grace) so peers re-run them at once, then stop the host so the
    /// orchestrator restarts it with a fresh block. A release that fails must not stop the shutdown; recovery
    /// then falls back to the heartbeat timeout.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_faulted_execute_block_releases_this_instances_claims_and_stops_the_host(bool releaseFails)
    {
        var options = new RippleEngineOptions { InstanceId = "clock-test", MaxConcurrency = 1 };
        _probe.Behaviour = (_, _) => throw new InvalidOperationException("handler failed");
        var pipeline = BuildPipeline(start: false, retryPolicy: new ThrowingRetryPolicy());

        var polls = 0;
        var store = ScriptedEngineStore.Create(_time, method => method switch
        {
            "PollAsync" => Task.FromResult<IReadOnlyList<ClaimedRipple>>(
                Interlocked.Increment(ref polls) == 1 ? [Claimed()] : []),
            "RecoverSelfStrandedAsync" => releaseFails
                ? Task.FromException<int>(new InvalidOperationException("database is down"))
                : Task.FromResult(1),
            "RemoveInstanceAsync" => Task.FromResult(true),
            _ => Task.CompletedTask
        });
        var lifetime = new RecordingLifetime();
        using var dispatcher = BuildDispatcher(Options.Create(options), store, pipeline, lifetime);

        await dispatcher.StartAsync(CancellationToken.None);

        // The loop returns ON ITS OWN — nobody calls StopAsync. Nudge the clock past the poll backoff meanwhile.
        await WaitUntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            return dispatcher.ExecuteTask!.IsCompleted;
        });
        await dispatcher.ExecuteTask!; // returned, did not throw

        pipeline.IsFaulted.ShouldBeTrue();
        pipeline.FaultException.ShouldBeOfType<AggregateException>().Flatten().InnerExceptions
            .ShouldContain(e => e.Message == ThrowingRetryPolicy.Message);

        store.Calls("RecoverSelfStrandedAsync").ShouldBe(1);
        var release = store.LastArgs("RecoverSelfStrandedAsync");
        release[0].ShouldBe("clock-test");
        release[1].ShouldBeAssignableTo<IReadOnlyCollection<Guid>>()!.ShouldBeEmpty("a faulted block keeps nothing");
        release[2].ShouldBe(TimeSpan.Zero);

        lifetime.StopRequests.ShouldBe(1);
        store.Calls("RemoveInstanceAsync").ShouldBe(1, "the shutdown path still runs after a fail-fast return");
    }

    /// <summary>The block can fault while a poll is in flight; everything that poll claimed is then refused. The
    /// rows stay Running for recovery, but their pooled payload buffers are returned here — the refused one and
    /// every ripple after it — and the refused attempt leaves the in-flight set, or self-recovery would treat this
    /// instance as its live owner. The next cycle then fails fast.</summary>
    [Fact]
    public async Task ripples_refused_by_a_block_that_faulted_mid_claim_return_their_buffers()
    {
        void Configure(RippleEngineOptions o)
        {
            o.MaxConcurrency = 1;
            o.PrefetchFactor = 4;
            // A StoreCallDeadline far beyond the clock nudges below, so the scripted poll is never abandoned.
            o.HeartbeatTimeout = TimeSpan.FromHours(1);
        }

        var options = new RippleEngineOptions { InstanceId = "clock-test" };
        Configure(options);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.Behaviour = async (_, _) =>
        {
            await gate.Task;
            throw new InvalidOperationException("handler failed");
        };
        var pipeline = BuildPipeline(Configure, start: false, retryPolicy: new ThrowingRetryPolicy());

        ClaimedRipple[] refused = [Claimed(), Claimed(), Claimed()];
        var polls = 0;
        var store = ScriptedEngineStore.Create(_time, method => method switch
        {
            "PollAsync" => Interlocked.Increment(ref polls) switch
            {
                1 => Task.FromResult<IReadOnlyList<ClaimedRipple>>([Claimed()]),
                2 => FaultThenClaimAsync(),
                _ => Task.FromResult<IReadOnlyList<ClaimedRipple>>([])
            },
            "RecoverSelfStrandedAsync" => Task.FromResult(0),
            "RemoveInstanceAsync" => Task.FromResult(true),
            _ => Task.CompletedTask
        });
        var lifetime = new RecordingLifetime();
        using var dispatcher = BuildDispatcher(Options.Create(options), store, pipeline, lifetime);

        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            return dispatcher.ExecuteTask!.IsCompleted;
        });
        await dispatcher.ExecuteTask!;

        polls.ShouldBe(2, "the cycle after the refused claim fails fast instead of polling again");
        foreach (var r in refused)
        {
            Should.Throw<ObjectDisposedException>(() => r.Payload.RootElement.ValueKind);
            Should.Throw<ObjectDisposedException>(() => r.WavePayload!.RootElement.ValueKind);
        }

        pipeline.ExecutingCount.ShouldBe(1, "only the ripple the block accepted is still owned");
        lifetime.StopRequests.ShouldBe(1);

        // Poll 2's top-of-loop fault check has already passed (the first ripple is parked on the gate), so the
        // block faults while this claim is "in flight" — exactly the window the refusal path exists for.
        async Task<IReadOnlyList<ClaimedRipple>> FaultThenClaimAsync()
        {
            gate.SetResult(); // the first ripple fails → the retry policy throws → the block faults
            await WaitUntilAsync(() => pipeline.IsFaulted);
            return refused;
        }
    }

    /// <summary>The recovery loop's wiring, which the storage tests (calling the store directly) never exercise:
    /// every <see cref="RippleEngineOptions.RecoveryInterval"/> it reclaims dead peers' work (excluding itself, past
    /// HeartbeatTimeout) and then its own stranded claims — keeping exactly the attempts the pipeline owns AT THAT
    /// TICK, past SelfReconcileGrace — and counts what each sweep recovered, by reason.</summary>
    [Fact]
    public async Task the_recovery_loop_sweeps_dead_peers_then_its_own_strays_each_interval()
    {
        var options = new RippleEngineOptions
        {
            InstanceId = "clock-test",
            RecoveryInterval = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(20),
            SelfReconcileGrace = TimeSpan.FromSeconds(45)
        };
        var store = ScriptedEngineStore.Create(_time, method => method switch
        {
            "RecoverStaleAsync" => Task.FromResult(2),
            "RecoverSelfStrandedAsync" => Task.FromResult(1),
            _ => throw new InvalidOperationException($"the recovery loop has no business calling {method}")
        });
        var owned = Guid.NewGuid();
        var inFlight = new InFlightTracker();
        inFlight.Track(owned, attempt: 1);

        using var metrics = new RippleMetrics();
        var recovered = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, TelemetryTests.MeterOf(metrics)) && instrument.Name == "ripple.recovered")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var reason = (string)tags.ToArray().Single(t => t.Key == "reason").Value!;
            recovered.AddOrUpdate(reason, value, (_, n) => n + value);
        });
        listener.Start();

        using var loop = new RecoveryLoop(store.AsEngineStore, inFlight, metrics, Options.Create(options), _time,
            NullLogger<RecoveryLoop>.Instance);
        await loop.StartAsync(CancellationToken.None);

        // First tick: exactly on the interval, both sweeps, with the configured thresholds.
        await WaitUntilAsync(() => _time.TimersCreated == 1);
        _time.Advance(options.RecoveryInterval - TimeSpan.FromTicks(1));
        store.Calls("RecoverStaleAsync").ShouldBe(0, "no sweep before the interval");
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => store.Calls("RecoverSelfStrandedAsync") == 1);

        store.Calls("RecoverStaleAsync").ShouldBe(1);
        var stale = store.LastArgs("RecoverStaleAsync");
        stale[0].ShouldBe(options.HeartbeatTimeout);
        stale[1].ShouldBe("clock-test", "an instance never declares itself dead");

        var stray = store.LastArgs("RecoverSelfStrandedAsync");
        stray[0].ShouldBe("clock-test");
        stray[1].ShouldBeAssignableTo<IReadOnlyCollection<Guid>>()!.ShouldBe([owned], "what the pipeline runs is kept");
        stray[2].ShouldBe(options.SelfReconcileGrace);

        recovered["dead_instance"].ShouldBe(2);
        recovered["self_stranded"].ShouldBe(1);

        // The kept set is read live each tick, not captured once: after the attempt settles, nothing is kept.
        inFlight.Release([(owned, 1)]);
        await WaitUntilAsync(() => _time.TimersCreated == 2);
        _time.Advance(options.RecoveryInterval);
        await WaitUntilAsync(() => store.Calls("RecoverSelfStrandedAsync") == 2);
        store.LastArgs("RecoverSelfStrandedAsync")[1].ShouldBeAssignableTo<IReadOnlyCollection<Guid>>()!.ShouldBeEmpty();
        recovered["dead_instance"].ShouldBe(4);
        recovered["self_stranded"].ShouldBe(2);

        await loop.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>One failing sweep must not skip the other: chained, a dead-instance sweep that kept failing would
    /// starve self-reconciliation — the only rescue for a live instance's own stranded claims — indefinitely.</summary>
    [Theory]
    [InlineData("RecoverStaleAsync", "RecoverSelfStrandedAsync")]
    [InlineData("RecoverSelfStrandedAsync", "RecoverStaleAsync")]
    public async Task a_failing_recovery_sweep_does_not_skip_the_other(string failing, string other)
    {
        var options = new RippleEngineOptions { InstanceId = "clock-test", RecoveryInterval = TimeSpan.FromSeconds(10) };
        var store = ScriptedEngineStore.Create(_time, method => method == failing
            ? Task.FromException<int>(new InvalidOperationException("sweep failed"))
            : Task.FromResult(0));
        using var loop = new RecoveryLoop(store.AsEngineStore, new InFlightTracker(), new RippleMetrics(),
            Options.Create(options), _time, NullLogger<RecoveryLoop>.Instance);
        await loop.StartAsync(CancellationToken.None);

        foreach (var tick in new[] { 1, 2 })
        {
            await WaitUntilAsync(() => _time.TimersCreated == tick);
            _time.Advance(options.RecoveryInterval);
            await WaitUntilAsync(() => store.Calls(other) == tick);
            store.Calls(failing).ShouldBe(tick);
        }

        await loop.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task a_periodic_loop_waits_its_interval_and_keeps_going_after_a_failed_tick()
    {
        var interval = TimeSpan.FromMinutes(1);
        using var loop = new CountingLoop(interval, _time) { FailFirstTick = true };
        await loop.StartAsync(CancellationToken.None);

        // ExecuteAsync runs on a background thread, so wait until the loop has actually armed its delay — an
        // Advance that landed first would be missed and the timer would start from the advanced time.
        await WaitUntilAsync(() => _time.TimersCreated == 1);
        _time.Advance(interval - TimeSpan.FromTicks(1));
        loop.Ticks.ShouldBe(0);
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => loop.Ticks == 1);

        // The first tick threw; the loop must still be alive and re-arm a full interval from the tick.
        await WaitUntilAsync(() => _time.TimersCreated == 2);
        _time.Advance(interval - TimeSpan.FromTicks(1));
        loop.Ticks.ShouldBe(1);
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => loop.Ticks == 2);

        await loop.StopAsync(CancellationToken.None);
    }

    // ---- fixture ---------------------------------------------------------------------------------

    private ExecutionPipeline BuildPipeline(Action<RippleEngineOptions>? configure = null, bool start = true,
        IRetryPolicy? retryPolicy = null)
    {
        var options = new RippleEngineOptions { InstanceId = "clock-test", MaxConcurrency = 1 };
        configure?.Invoke(options);

        var services = new ServiceCollection()
            .AddSingleton(_probe)
            .AddSingleton<TimeProvider>(_time)
            .AddScoped<ClockHandler>()
            .BuildServiceProvider();
        var registry = new RippleHandlerRegistry();
        registry.Register<ClockHandler, ClockWave, ClockRipple>(batchSize: null, gapSeconds: 1, maxAttempts: null);

        var opts = Options.Create(options);
        var inFlight = new InFlightTracker();
        var pipeline = new ExecutionPipeline(opts, registry, services.GetRequiredService<IServiceScopeFactory>(),
            inFlight, retryPolicy ?? new ExponentialRetryPolicy(opts), BuildSettlement(opts, inFlight), new RippleMetrics(), _time,
            NullLogger<ExecutionPipeline>.Instance);
        if (start)
        {
            pipeline.Start();
        }

        return pipeline;
    }

    /// <summary>A real dispatcher over a real (handler-less) pipeline, talking to a scripted store.</summary>
    private Dispatcher BuildDispatcher(IOptions<RippleEngineOptions> options, ScriptedEngineStore store,
        ExecutionPipeline? pipeline = null, IHostApplicationLifetime? lifetime = null)
    {
        var registry = new RippleHandlerRegistry();
        var inFlight = new InFlightTracker();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        pipeline ??= new ExecutionPipeline(options, registry, scopes, inFlight, new ExponentialRetryPolicy(options),
            BuildSettlement(options, inFlight), new RippleMetrics(), _time, NullLogger<ExecutionPipeline>.Instance);
        return new Dispatcher(pipeline, registry, store.AsEngineStore, new RippleMetrics(), options,
            lifetime ?? new NoopLifetime(), _time, NullLogger<Dispatcher>.Instance);
    }

    private static ClaimedRipple Claimed()
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, 3, TypeKey, nameof(ClockRipple),
            JsonDocument.Parse($$"""{"id":"{{Guid.NewGuid()}}"}"""), null, JsonDocument.Parse("{}"));

    private SettlementWriter BuildSettlement(IOptions<RippleEngineOptions> options, InFlightTracker inFlight)
        => new(options, _store, inFlight, new RippleMetrics(), _time, NullLogger<SettlementWriter>.Instance);

    private static PreparedRipple Ripple(int attempt, int maxAttempts)
        => new(Guid.NewGuid(), Guid.NewGuid(), attempt, maxAttempts, TypeKey, nameof(ClockRipple),
            JsonDocument.Parse($$"""{"id":"{{Guid.NewGuid()}}"}"""), JsonDocument.Parse("{}"));

    private static async Task StopAsync(ExecutionPipeline pipeline)
        => await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task<T> SingleAsync<T>(ConcurrentQueue<T> queue)
    {
        await WaitUntilAsync(() => !queue.IsEmpty);
        queue.Count.ShouldBe(1);
        return queue.Single();
    }

    /// <summary>Real-time wait for async continuations to land — a liveness cap, never a timing assertion.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "condition never became true");
            await Task.Delay(10);
        }
    }

    /// <summary>A fake clock that also counts the timers armed on it, so a test can wait until a component is
    /// actually waiting before advancing past its deadline.</summary>
    internal sealed class CountingTimeProvider() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timersCreated);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    public sealed class ClockWave;

    public sealed class ClockRipple : IRippleTarget
    {
        public Guid Id { get; set; }

        [JsonIgnore]
        public IReadOnlyList<string> TargetIds => [Id.ToString()];
    }

    /// <summary>What the handler does, set per test; <see cref="Started"/> hands the test the live context.</summary>
    public sealed class ClockProbe
    {
        public Func<IRippleContext, TimeProvider, Task<SplashReport?>> Behaviour { get; set; } =
            (_, _) => Task.FromResult<SplashReport?>(null);

        public TaskCompletionSource<IRippleContext> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ClockHandler(ClockProbe probe, TimeProvider time) : IRippleHandler<ClockWave, ClockRipple>
    {
        public Task<SplashReport?> Execute(ClockWave wave, ClockRipple ripple, IRippleContext context)
            => probe.Behaviour(context, time);
    }

    private sealed class RecordingSplashStore : ISplashStore
    {
        public ConcurrentQueue<RippleCompletion> Completed { get; } = new();
        public ConcurrentQueue<RippleFailure> Failed { get; } = new();

        /// <summary>How many of the next writes throw (a transient DB outage) before they start landing.</summary>
        public int FailNextWrites { get; set; }

        /// <summary>When set, a write whose batch holds a report this matches is refused the way the database
        /// refuses bad data: deterministically, with a non-transient SQLSTATE error.</summary>
        public Func<string?, bool>? Rejects { get; set; }

        public Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId,
            CancellationToken ct = default)
        {
            ThrowIfFailing();
            ThrowIfRejected(batch.Select(c => c.Report));
            foreach (var c in batch) Completed.Enqueue(c);
            return Task.CompletedTask;
        }

        public Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId,
            CancellationToken ct = default)
        {
            ThrowIfFailing();
            ThrowIfRejected(batch.Select(f => f.Report));
            foreach (var f in batch) Failed.Enqueue(f);
            return Task.CompletedTask;
        }

        private void ThrowIfRejected(IEnumerable<string?> reports)
        {
            if (Rejects is { } rejects && reports.Any(rejects))
            {
                throw new RejectedException();
            }
        }

        private sealed class RejectedException() : System.Data.Common.DbException("invalid input")
        {
            public override string SqlState => "22P05";
            public override bool IsTransient => false;
        }

        private void ThrowIfFailing()
        {
            if (FailNextWrites > 0)
            {
                FailNextWrites--;
                throw new InvalidOperationException("transient settlement failure");
            }
        }
    }

    /// <summary>
    /// An <see cref="IEngineStore"/> whose every call is answered by one responder (by method name) and counted.
    /// A proxy rather than a hand-written fake of the whole interface: the dispatcher only polls and beats.
    /// </summary>
    public class ScriptedEngineStore : DispatchProxy
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private CountingTimeProvider _time = default!;
        private Func<string, CancellationToken, object?> _respond = default!;

        internal IEngineStore AsEngineStore => (IEngineStore)(object)this;

        /// <summary>The fake clock's timer count at the last store call: a timer created after it is the delay
        /// the dispatcher armed in response.</summary>
        public int TimersAtLastCall { get; private set; }

        internal static ScriptedEngineStore Create(CountingTimeProvider time, Func<string, object?> respond)
            => Create(time, (method, _) => respond(method));

        /// <summary>As above, but the responder also gets the call's cancellation token — to script a call that
        /// hangs until the dispatcher gives up on it.</summary>
        internal static ScriptedEngineStore Create(CountingTimeProvider time,
            Func<string, CancellationToken, object?> respond)
        {
            var store = (ScriptedEngineStore)(object)Create<IEngineStore, ScriptedEngineStore>();
            store._time = time;
            store._respond = respond;
            return store;
        }

        public int Calls(string method) => _calls.GetValueOrDefault(method);

        /// <summary>The arguments of the most recent call to <paramref name="method"/>.</summary>
        public object?[] LastArgs(string method) => _lastArgs[method];

        private readonly ConcurrentDictionary<string, object?[]> _lastArgs = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            TimersAtLastCall = _time.TimersCreated;
            _calls.AddOrUpdate(name, 1, (_, n) => n + 1);
            _lastArgs[name] = args ?? [];
            return _respond(name, args?.OfType<CancellationToken>().LastOrDefault() ?? default);
        }
    }

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private int _stopRequests;

        public int StopRequests => Volatile.Read(ref _stopRequests);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref _stopRequests);
    }

    /// <summary>Faults the execute block on demand. The block's action catches everything the handler does, so it
    /// only faults when something AFTER the handler throws — and the retry decision is the one injected step there.</summary>
    private sealed class ThrowingRetryPolicy : IRetryPolicy
    {
        public const string Message = "retry policy failed";

        public TimeSpan? NextDelay(int attempt, int maxAttempts) => throw new InvalidOperationException(Message);
    }

    private sealed class CountingLoop(TimeSpan interval, TimeProvider time)
        : PeriodicLoop(interval, time, NullLogger.Instance)
    {
        private int _ticks;

        public bool FailFirstTick { get; init; }

        public int Ticks => Volatile.Read(ref _ticks);

        protected override string FailureMessage => "counting loop tick failed";

        protected override Task TickAsync(CancellationToken stoppingToken)
            => Interlocked.Increment(ref _ticks) == 1 && FailFirstTick
                ? throw new InvalidOperationException("first tick fails")
                : Task.CompletedTask;
    }
}
